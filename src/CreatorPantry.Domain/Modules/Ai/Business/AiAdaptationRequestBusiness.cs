using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiAdaptationRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeAdaptationViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting AIREC-005's single-goal adaptation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Recipe-bound, like AIREC-003 and AIREC-004.</strong> An adaptation names a recipe and the exact
/// version it adapts, checked through the recipe module's facade before anything is queued.
/// </para>
/// <para>
/// <strong>The scope is fixed here, not read from the client.</strong> Every operation this business queues
/// writes <see cref="AiOperationScope.WholeRecipe"/>, because a complete cross-field proposal has no narrower
/// scope to fit — the same reasoning that fixes AIREC-004's operations to
/// <see cref="AiOperationScope.Advisory"/>.
/// </para>
/// <para>
/// <strong>A yield goal is pre-checked here too.</strong>
/// <see cref="IRecipeFacade.ScaleAsync(Guid, int, decimal?, decimal?, CancellationToken)"/> is called
/// before the operation is written, so a non-positive target or a recipe whose own yield is not a structured
/// number is refused at the edge rather than spending a provider budget on an operation the handler would
/// refuse anyway once it re-resolves the same check.
/// </para>
/// </remarks>
internal sealed class AiAdaptationRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IRecipeFacade recipes,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiAdaptationRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeAdaptationViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.RecipeAdaptation))
        {
            return Refuse(
                AiAdaptationRequestErrors.TaskNotEnabled,
                "Recipe adaptation is not enabled for this workspace's deployment.");
        }

        // Before the operation is written, so a refused request leaves no orphan to clean up and spends no
        // allowance to say the allowance is spent (USAGE-006).
        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.RecipeAdaptation, cancellationToken) is { } spent)
        {
            return spent;
        }

        // Through the recipe module's facade, never its repositories. This is also the existence check: a
        // recipe in another workspace is invisible here, so it reports missing rather than forbidden.
        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!recipe.Succeeded)
        {
            return Refuse(AiAdaptationRequestErrors.RecipeNotFound, "That recipe does not exist.");
        }

        // The pinned source must be this recipe's current version. An adaptation computed against an older one
        // would be stale before the creator ever saw it.
        if (recipe.Value!.CurrentVersion?.Id != model.SourceVersionId)
        {
            return Refuse(
                AiAdaptationRequestErrors.SourceVersionInvalid,
                "That version is not the current version of this recipe.");
        }

        if (model.Goal is AiAdaptationGoal.Yield)
        {
            var scaled = await recipes.ScaleAsync(
                recipeId,
                recipe.Value.CurrentVersion.VersionNumber,
                model.TargetMultiplier,
                model.TargetYieldQuantity,
                cancellationToken);

            if (!scaled.Succeeded)
            {
                return Refuse(AiAdaptationRequestErrors.YieldTargetInvalid, scaled.Error!.Message);
            }
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.RecipeAdaptation,
                Scope = AiOperationScope.WholeRecipe,
                Status = AiOperationStatus.Requested,
                RecipeId = recipeId,
                RecipeVersionId = model.SourceVersionId,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = SerializeInputs(model),
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (requested.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<AiProposalStatusServiceModel>(
            OperationResult<AiProposalStatusServiceModel>.Success(
                AiOperationDescription.Describe(requested.Operation!, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // One absence for four conditions: no such request, one in another workspace, one belonging to a
        // different recipe, and one that ran a different task. None discloses that the others exist.
        if (operation is null
            || operation.Operation.TaskType != AiTaskType.RecipeAdaptation
            || operation.Operation.RecipeId != recipeId)
        {
            return Failure(
                AiAdaptationRequestErrors.RequestNotFound, "That adaptation request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>The goal and its detail as JSON, in the shape <see cref="RecipeAdaptationAiTaskHandler"/> reads back.</summary>
    /// <remarks>
    /// The scope is not written here: it is a column on the operation, fixed above rather than taken from the
    /// request, which is what the validator reads when the answer comes back.
    /// </remarks>
    private static string SerializeInputs(RequestRecipeAdaptationViewModel model)
    {
        var values = new Dictionary<string, string>(4, StringComparer.Ordinal)
        {
            [AiAdaptationInputs.Goal] = model.Goal.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(model.GoalDetail))
        {
            values[AiAdaptationInputs.GoalDetail] = model.GoalDetail;
        }

        if (model.TargetMultiplier is { } multiplier)
        {
            values[AiAdaptationInputs.TargetMultiplier] = multiplier.ToString(CultureInfo.InvariantCulture);
        }

        if (model.TargetYieldQuantity is { } targetYieldQuantity)
        {
            values[AiAdaptationInputs.TargetYieldQuantity] = targetYieldQuantity.ToString(CultureInfo.InvariantCulture);
        }

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
