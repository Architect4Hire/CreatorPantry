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

internal interface IAiRevisionRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeRevisionViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting AIREC-003's scoped revision.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Recipe-bound, unlike AIREC-001 and AIREC-002.</strong> A revision names a recipe and the exact
/// version it revises, so this checks both through the recipe module's facade before queueing anything — the
/// same two checks <c>AiProposalBusiness</c> makes, for the same reasons.
/// </para>
/// <para>
/// <strong>The scope is recorded on the operation, before the model is called.</strong> That is what makes it
/// a bound rather than a label: the validator reads it from the stored operation when the answer comes back,
/// so nothing the model returns can argue about what it was allowed to touch.
/// </para>
/// </remarks>
internal sealed class AiRevisionRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IRecipeFacade recipes,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiRevisionRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeRevisionViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.RecipeRevision))
        {
            return Refuse(
                AiRevisionRequestErrors.TaskNotEnabled,
                "Recipe revision is not enabled for this workspace's deployment.");
        }

        // Before the operation is written, so a refused request leaves no orphan to clean up and spends no
        // allowance to say the allowance is spent (USAGE-006). Before the recipe lookup too: an account that
        // cannot spend is refused whatever it named, which is also one fewer read for a request going nowhere.
        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.RecipeRevision, cancellationToken) is { } spent)
        {
            return spent;
        }

        // Through the recipe module's facade, never its repositories. This is also the existence check: a
        // recipe in another workspace is invisible here, so it reports missing rather than forbidden.
        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!recipe.Succeeded)
        {
            return Refuse(AiRevisionRequestErrors.RecipeNotFound, "That recipe does not exist.");
        }

        // The pinned source must be this recipe's current version. A revision computed against an older one
        // would be stale before the creator ever saw it.
        if (recipe.Value!.CurrentVersion?.Id != model.SourceVersionId)
        {
            return Refuse(
                AiRevisionRequestErrors.SourceVersionInvalid,
                "That version is not the current version of this recipe.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.RecipeRevision,
                Scope = model.Scope,
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
            || operation.Operation.TaskType != AiTaskType.RecipeRevision
            || operation.Operation.RecipeId != recipeId)
        {
            return Failure(
                AiRevisionRequestErrors.RequestNotFound, "That revision request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The goal as JSON, in the shape <see cref="RecipeRevisionAiTaskHandler"/> reads back.
    /// </summary>
    /// <remarks>
    /// The scope is not written here: it is a column on the operation, which is what the validator reads when
    /// the answer comes back. Storing it twice would give a later reader two places to disagree about what a
    /// revision was allowed to change.
    /// </remarks>
    private static string SerializeInputs(RequestRecipeRevisionViewModel model)
    {
        var values = new Dictionary<string, string>(1, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(model.Goal))
        {
            values[AiRevisionInputs.Goal] = model.Goal;
        }

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
