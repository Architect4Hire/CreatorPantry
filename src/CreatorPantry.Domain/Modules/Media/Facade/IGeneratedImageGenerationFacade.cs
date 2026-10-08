using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary for image generation (IMG-003): requesting one, and running a claimed one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No controller calls this yet.</strong> 12.7 adds the gateway and the job; the HTTP route arrives
/// with the Image Studio screen that needs it. The seam is a facade rather than something the worker calls
/// directly because backend.md allows a worker exactly one entry point into a module, and because the
/// request path and the run path have to agree about what an operation is.
/// </para>
/// <para>
/// <strong>Both halves resolve the workspace before they are reached.</strong>
/// <see cref="RequestAsync"/> runs under a creator's resolved membership;
/// <see cref="ExecuteAsync"/> runs under the service resolution the worker performed after claiming. Neither
/// accepts a workspace identifier, and no model-supplied one exists anywhere on this path (ai.md).
/// </para>
/// </remarks>
public interface IGeneratedImageGenerationFacade
{
    /// <summary>
    /// Records a request for the resolved workspace, or returns the one its idempotency key already bought.
    /// </summary>
    /// <remarks>
    /// A replay is a success, not a conflict: the caller asked for images and there are images coming. The
    /// returned model is the first operation, so a client that lost its answer finds the same one.
    /// </remarks>
    Task<OperationResult<GeneratedImageOperationServiceModel>> RequestAsync(
        GeneratedImageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one claimed operation. The Worker only, and that is checked rather than trusted.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="GeneratedImageRunOutcome.Skipped"/> for a caller that is not the service
    /// identity. The lease token already bounds the damage a stray caller could do — it would have to
    /// guess a live one — but "never routed" was enforced by a comment, and a comment is not a check.
    /// A creator retrying a failed generation queues a new request; nothing a person does reaches here.
    /// </remarks>
    Task<GeneratedImageRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// One operation of the resolved workspace, with the images it has produced so far.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Any member may read, which is why it is here and not behind the Contributor gate above.</strong>
    /// Asking for images spends the workspace's budget; looking at what came back does not, and the preview and
    /// download routes this read feeds are themselves Viewer. A Viewer who can see an image but not learn that it
    /// exists would be a contract at odds with itself.
    /// </para>
    /// <para>
    /// An unknown operation and another workspace's are one answer, so neither discloses the other (tenancy.md).
    /// </para>
    /// </remarks>
    Task<OperationResult<GeneratedImageOperationDetailServiceModel>> GetOperationAsync(
        Guid operationId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageGenerationFacade"/>
internal sealed class GeneratedImageGenerationFacade(
    IGeneratedImageGenerationBusiness business,
    IAiProposalLookupFacade proposals,
    IWorkspaceContext workspace) : IGeneratedImageGenerationFacade
{
    public async Task<OperationResult<GeneratedImageOperationDetailServiceModel>> GetOperationAsync(
        Guid operationId, CancellationToken cancellationToken)
    {
        // No role gate: a resolved workspace context is the authorization for the lowest role, and the routes
        // this feeds are Viewer too.
        var detail = await business.FindDetailAsync(operationId, cancellationToken);

        return detail is null
            ? OperationResult<GeneratedImageOperationDetailServiceModel>.Failure(new OperationError(
                MediaErrorCodes.GenerationOperationNotFound,
                "That image request could not be found in this workspace.",
                new Dictionary<string, string[]>()))
            : OperationResult<GeneratedImageOperationDetailServiceModel>.Success(detail);
    }

    public async Task<OperationResult<GeneratedImageOperationServiceModel>> RequestAsync(
        GeneratedImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Contributor, not Editor. Generating images produces candidates a creator then chooses between —
        // nothing a generation does is canonical, and nothing it does is published. Keeping it above Viewer
        // is what matters: every variant is a charge against the workspace.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(new OperationError(
                MediaErrorCodes.GenerationForbidden,
                "You do not have permission to generate images in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var failures = GeneratedImageInputChecks.Request(request).ToList();

        if (failures.Count > 0)
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.GenerationInvalidRequest, "The images could not be requested.", failures));
        }

        var promptText = request.PromptText.Trim();
        var avoidText = string.IsNullOrWhiteSpace(request.AvoidText) ? null : request.AvoidText.Trim();

        // The proposal the prompt came from, when one is named, has to be one of this workspace's (12.10l).
        // The composite key on the operation already refuses anything else, but as an exception nobody reads:
        // asked here, through the Ai module's own facade, it is a refusal in words. An id that names nothing
        // and another workspace's proposal are one answer, so this cannot be used to ask what a neighbour has
        // generated (tenancy.md).
        if (request.AiProposalId is { } proposalId && !await proposals.ExistsAsync(proposalId, cancellationToken))
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.GenerationProposalUnprocessable,
                "The images could not be requested.",
                [(nameof(request.AiProposalId), "That prompt proposal is not in this workspace.")]));
        }

        var result = await business.RequestAsync(
            promptText,
            avoidText,
            request.AiProposalId,
            request.VariantCount,
            workspace.MembershipId,
            request.IdempotencyKey.Trim(),
            cancellationToken);

        // A key names one request. Found again with anything about the request different — the words, the
        // number of images, the proposal, or who is asking — it is a second request wearing the first one's
        // key, and answering it with the first one's operation would hand back images of something else
        // (12.10l). Refused as every other idempotent command refuses it, and nothing is queued.
        if (result.Replayed
            && !(string.Equals(result.Operation.PromptText, promptText, StringComparison.Ordinal)
                && string.Equals(result.Operation.AvoidText, avoidText, StringComparison.Ordinal)
                && result.Operation.AiProposalId == request.AiProposalId
                && result.Operation.VariantCount == request.VariantCount
                && result.Operation.RequestedByMembershipId == workspace.MembershipId))
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(new OperationError(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different image request.",
                new Dictionary<string, string[]>()));
        }

        // Zero on a fresh request without a round trip; the real count only matters for a replay, where the
        // caller is asking what became of the operation it already has.
        var staged = result.Replayed
            ? await business.StagedCountAsync(result.Operation.Id, cancellationToken)
            : 0;

        return OperationResult<GeneratedImageOperationServiceModel>.Success(
            new GeneratedImageOperationServiceModel(
                result.Operation.Id,
                result.Operation.Status,
                result.Operation.VariantCount,
                staged,
                result.Operation.ProviderName,
                result.Operation.ModelName,
                result.Operation.FailureCategory,
                result.Operation.FailureSummary,
                result.Operation.RequestedAt,
                result.Operation.CompletedAt));
    }

    public Task<GeneratedImageRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken) =>
        workspace.MembershipId == WorkspaceServiceIdentity.MembershipId
            ? business.ExecuteAsync(operationId, leaseToken, cancellationToken)
            : Task.FromResult(GeneratedImageRunOutcome.Skipped);
}
