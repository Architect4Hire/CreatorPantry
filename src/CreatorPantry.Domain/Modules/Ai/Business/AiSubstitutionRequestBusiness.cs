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

internal interface IAiSubstitutionRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestIngredientSubstitutionViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting AIREC-004's substitution advice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three checks before anything is queued</strong>, all from one read through the recipe module's
/// facade: the recipe exists and is visible, the pinned version is the current one, and the selected line is
/// actually in it. The third is this capability's own — advice about an ingredient that is not in the recipe
/// is advice about nothing, and finding that out after a provider call has been paid for helps nobody. The
/// worker checks it again against the pinned snapshot, which is the authoritative one; this is the early
/// refusal, not the guarantee.
/// </para>
/// <para>
/// <strong>The scope is <see cref="AiOperationScope.Advisory"/> and the server decides it.</strong> The
/// request has no scope field, because there is no bound for a creator to choose: a substitution proposes no
/// change to the recipe at all, and <c>Advisory</c> is what makes that true of the stored operation rather
/// than merely true of the handler that happens to run it.
/// </para>
/// </remarks>
internal sealed class AiSubstitutionRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IRecipeFacade recipes,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiSubstitutionRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestIngredientSubstitutionViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.IngredientSubstitution))
        {
            return Refuse(
                AiSubstitutionRequestErrors.TaskNotEnabled,
                "Ingredient substitution is not enabled for this workspace's deployment.");
        }

        // Before the operation is written, so a refused request leaves no orphan to clean up and spends no
        // allowance to say the allowance is spent (USAGE-006).
        if (await quota.RefuseIfUnaffordableAsync(
            AiTaskType.IngredientSubstitution, cancellationToken) is { } spent)
        {
            return spent;
        }

        // Through the recipe module's facade, never its repositories. This is also the existence check: a
        // recipe in another workspace is invisible here, so it reports missing rather than forbidden.
        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!recipe.Succeeded)
        {
            return Refuse(AiSubstitutionRequestErrors.RecipeNotFound, "That recipe does not exist.");
        }

        if (recipe.Value!.CurrentVersion?.Id != model.SourceVersionId)
        {
            return Refuse(
                AiSubstitutionRequestErrors.SourceVersionInvalid,
                "That version is not the current version of this recipe.");
        }

        // From the detail already in hand, not from the pinned version's archived snapshot. The check above
        // established that the pinned version is the current one, and every edit writes a new version — so
        // the live content is that version's content, and its line ids are the same ids. Reading the whole
        // archived document again to test one Guid for membership would be a second full read on the
        // synchronous request path for an answer already sitting here.
        var present = recipe.Value.IngredientGroups
            .Any(group => group.Ingredients.Any(line => line.Id == model.IngredientId));

        if (!present)
        {
            return Refuse(
                AiSubstitutionRequestErrors.IngredientInvalid,
                "That ingredient is not part of the version named.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.IngredientSubstitution,

                // Not taken from the request, because the request has no scope field. Advisory is what makes
                // "this proposes no change to the recipe" a property of the stored row.
                Scope = AiOperationScope.Advisory,
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
            || operation.Operation.TaskType != AiTaskType.IngredientSubstitution
            || operation.Operation.RecipeId != recipeId)
        {
            return Failure(
                AiSubstitutionRequestErrors.RequestNotFound, "That substitution request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The selected line and the creator's reason, in the shape
    /// <see cref="IngredientSubstitutionAiTaskHandler"/> reads back.
    /// </summary>
    /// <remarks>
    /// The ingredient id is written here rather than onto a column of its own, because it is one capability's
    /// request field and <c>AiOperation</c> is every capability's aggregate. The version it belongs to is a
    /// column, which is what makes the id meaningful when it is read back.
    /// </remarks>
    private static string SerializeInputs(RequestIngredientSubstitutionViewModel model)
    {
        var values = new Dictionary<string, string>(2, StringComparer.Ordinal)
        {
            [AiSubstitutionInputs.IngredientId] = model.IngredientId.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(model.Reason))
        {
            values[AiSubstitutionInputs.Reason] = model.Reason;
        }

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
