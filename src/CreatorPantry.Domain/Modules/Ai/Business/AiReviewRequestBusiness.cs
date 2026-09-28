using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiReviewRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeReviewViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting AIREC-006's review findings.
/// </summary>
/// <remarks>
/// <para>
/// Two checks before anything is queued, both from one read through the recipe module's facade: the recipe
/// exists and is visible, and the pinned version is the current one. There is no third capability-specific
/// check the way <see cref="AiSubstitutionRequestBusiness"/> has one for its selected ingredient — a review
/// names no line of its own, so there is nothing further to check before the worker reads the whole snapshot.
/// </para>
/// <para>
/// <strong>The scope is <see cref="AiOperationScope.Advisory"/> and the server decides it</strong>, for the
/// same reason <see cref="AiSubstitutionRequestBusiness"/> decides it: the request has no scope field, because
/// a review proposes no change to the recipe at all, and <c>Advisory</c> is what makes that true of the stored
/// operation rather than merely true of the handler that happens to run it.
/// </para>
/// </remarks>
internal sealed class AiReviewRequestBusiness(
    IAiOperationDataLayer operations,
    IRecipeFacade recipes,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiReviewRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeReviewViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.RecipeReview))
        {
            return Refuse(
                AiRecipeReviewRequestErrors.TaskNotEnabled,
                "Recipe review is not enabled for this workspace's deployment.");
        }

        // Through the recipe module's facade, never its repositories. This is also the existence check: a
        // recipe in another workspace is invisible here, so it reports missing rather than forbidden.
        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!recipe.Succeeded)
        {
            return Refuse(AiRecipeReviewRequestErrors.RecipeNotFound, "That recipe does not exist.");
        }

        if (recipe.Value!.CurrentVersion?.Id != model.SourceVersionId)
        {
            return Refuse(
                AiRecipeReviewRequestErrors.SourceVersionInvalid,
                "That version is not the current version of this recipe.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.RecipeReview,

                // Not taken from the request, because the request has no scope field. Advisory is what makes
                // "this proposes no change to the recipe" a property of the stored row.
                Scope = AiOperationScope.Advisory,
                Status = AiOperationStatus.Requested,
                RecipeId = recipeId,
                RecipeVersionId = model.SourceVersionId,
                IdempotencyKey = idempotencyKey,
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
            || operation.Operation.TaskType != AiTaskType.RecipeReview
            || operation.Operation.RecipeId != recipeId)
        {
            return Failure(AiRecipeReviewRequestErrors.RequestNotFound, "That review request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
