using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiProposalExplanationRequestBusiness
{
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestProposalExplanationViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting AIREC-008's proposal explanation: that the task is enabled, that the named
/// source actually has a proposal to explain, and what a creator is shown about a request.
/// </summary>
/// <remarks>
/// <strong>Reads through <see cref="IAiOperationDataLayer"/>, not
/// <see cref="CreatorPantry.Domain.Modules.Recipes.Facade.IRecipeFacade"/>.</strong>
/// Every earlier request business either reaches into the recipe module or reaches into nothing; this is the
/// first to reach into another operation's own stored proposal instead — the same seam
/// <see cref="AiProposalExplanationAiTaskHandler"/> re-reads from at execution time, since the source could in
/// principle disappear between the two.
/// </remarks>
internal sealed class AiProposalExplanationRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiProposalExplanationRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestProposalExplanationViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.ProposalExplanation))
        {
            return Refuse(
                AiProposalExplanationRequestErrors.TaskNotEnabled,
                "Proposal explanation is not enabled for this workspace's deployment.");
        }

        // Before the operation is written, so a refused request leaves no orphan to clean up and spends no
        // allowance to say the allowance is spent (USAGE-006).
        if (await quota.RefuseIfUnaffordableAsync(
            AiTaskType.ProposalExplanation, cancellationToken) is { } spent)
        {
            return spent;
        }

        var source = await operations.GetWithProposalAsync(model.SourceRequestId, cancellationToken);

        // One refusal covers "does not exist", "belongs to another workspace" (filtered away identically by
        // the same global query filter), "has no proposal yet", and "is itself an explanation" (no chaining
        // one explanation off another). None of the four should be told apart from the outside (tenancy.md).
        if (source?.Proposal is null || source.Operation.TaskType == AiTaskType.ProposalExplanation)
        {
            return Refuse(
                AiProposalExplanationRequestErrors.SourceNotReady, "That proposal is not available to explain.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.ProposalExplanation,
                Scope = AiOperationScope.Advisory,

                // Inherited from the source rather than re-declared: an explanation is about whatever the
                // source proposal was about, and the source already established both server-side.
                RecipeId = source.Operation.RecipeId,
                RecipeVersionId = source.Operation.RecipeVersionId,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = SerializeInputs(model.SourceRequestId),
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

        if (operation is null || operation.Operation.TaskType != AiTaskType.ProposalExplanation)
        {
            return Failure(
                AiProposalExplanationRequestErrors.RequestNotFound,
                "That proposal explanation request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The one declared field as JSON, in the shape <see cref="AiProposalExplanationAiTaskHandler"/> reads
    /// back.
    /// </summary>
    private static string SerializeInputs(Guid sourceRequestId) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(1, StringComparer.Ordinal)
        {
            [AiProposalExplanationInputs.SourceRequestId] = sourceRequestId.ToString(),
        });

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
