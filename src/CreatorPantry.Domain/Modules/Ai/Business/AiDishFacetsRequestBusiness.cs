using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiDishFacetsRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestDishFacetsViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting a dish-name reading: that the task is enabled and affordable, and what a
/// creator is shown about a request.
/// </summary>
/// <remarks>
/// Like <see cref="AiConceptRequestBusiness"/> and unlike <see cref="AiProposalBusiness"/>, this never reaches
/// into the recipe module — the request names no recipe, so there is nothing there to check. It does not
/// reach into the vocabulary module either: the catalogues are the handler's to read at the moment it asks,
/// and reading them here would only let them go stale between the request and the call.
/// </remarks>
internal sealed class AiDishFacetsRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiDishFacetsRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestDishFacetsViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.DishFacetSuggestion))
        {
            return Refuse(
                AiDishFacetsRequestErrors.TaskNotEnabled,
                "Reading a dish name into cuisine, dish type and method is not enabled for this workspace's "
                    + "deployment.");
        }

        // Before the operation is written, so a refused request leaves no orphan to clean up and spends no
        // allowance to say the allowance is spent (USAGE-006). After the task check above, which is free and
        // answers a question about the request rather than about the account.
        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.DishFacetSuggestion, cancellationToken) is { } spent)
        {
            return spent;
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.DishFacetSuggestion,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,

                // Null on both, and not an omission: a creator asks for this precisely when the dish is not in
                // their library, so there is no recipe to name and no version to pin.
                RecipeId = null,
                RecipeVersionId = null,
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
        Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // Both conditions report the same absence: a request in another workspace is filtered away by the
        // query filter before this ever sees it, and one belonging to some other task type is not this
        // route's resource. Neither discloses that it exists.
        if (operation is null || operation.Operation.TaskType != AiTaskType.DishFacetSuggestion)
        {
            return Failure(AiDishFacetsRequestErrors.RequestNotFound, "That dish-facet request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The one declared field as JSON, in the shape <see cref="DishFacetSuggestionAiTaskHandler"/> reads back.
    /// </summary>
    /// <remarks>
    /// Trimmed, because the name is also the idempotency subject in practice: " Fattoush" and "Fattoush" are
    /// the same request, and a reading of the first would differ from a reading of the second only by the
    /// accident of a leading space.
    /// </remarks>
    private static string SerializeInputs(RequestDishFacetsViewModel model) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(1, StringComparer.Ordinal)
        {
            [AiDishFacetInputs.DishName] = model.DishName!.Trim(),
        });

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
