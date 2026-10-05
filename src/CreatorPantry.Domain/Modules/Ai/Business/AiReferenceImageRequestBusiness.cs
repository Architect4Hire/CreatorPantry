using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiReferenceImageRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestReferenceImageAnalysisViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting IMG-004's reading: that the task is enabled, and that the named document is
/// one of this workspace's own whose current version is an image.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The version is resolved here and pinned</strong>, so the bytes the worker reads are the ones the
/// creator attached. It is also where "is this an image at all" is answered, before a provider is paid to
/// look at a PDF — the handler re-checks the same fact against the bytes it is about to send, because a
/// document can be replaced in between.
/// </para>
/// <para>
/// <strong>No recipe and no scope.</strong> A reading of a photograph is about the photograph; it pins no
/// recipe version and proposes no recipe edit, so the scope is fixed at
/// <see cref="AiOperationScope.NotApplicable"/> server-side and there is nothing for a client to choose.
/// </para>
/// </remarks>
internal sealed class AiReferenceImageRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IBrandSourceDocumentFacade documents,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiReferenceImageRequestBusiness
{
    /// <inheritdoc cref="ReferenceImageAnalysisAiTaskHandler"/>
    private static readonly HashSet<string> ImageMediaTypes = new(StringComparer.Ordinal)
    {
        BrandSourceFileInspector.PngMediaType,
        BrandSourceFileInspector.JpegMediaType,
        BrandSourceFileInspector.WebpMediaType,
        BrandSourceFileInspector.GifMediaType,
    };

    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestReferenceImageAnalysisViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.ReferenceImageAnalysis))
        {
            return Refuse(
                AiReferenceImageRequestErrors.TaskNotEnabled,
                "Reference image analysis is not enabled for this workspace's deployment.");
        }

        var reference = await ResolveReferenceAsync(model, cancellationToken);

        if (reference is not { } versionNumber)
        {
            return Refuse(
                AiReferenceImageRequestErrors.ReferenceNotFound,
                "This workspace has no image with that id.");
        }

        if (await quota.RefuseIfUnaffordableAsync(
                AiTaskType.ReferenceImageAnalysis, cancellationToken) is { } spent)
        {
            return spent;
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.ReferenceImageAnalysis,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = SerializeInputs(model, versionNumber),
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

        if (operation is null || operation.Operation.TaskType != AiTaskType.ReferenceImageAnalysis)
        {
            return Failure(
                AiReferenceImageRequestErrors.RequestNotFound,
                "That reference image request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The version number of the named document, or null when it is not a readable image of this workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One facade call answering both questions. A document this workspace does not have is a failure from
    /// the brand module under its own query filter, so a neighbour's id and a nonexistent one are the same
    /// answer here without this code having to arrange that.
    /// </para>
    /// <para>
    /// <strong>The media type is checked now as well as in the handler</strong>, and the duplication is the
    /// point: a creator who names a PDF should be told immediately, not left to find a failed operation
    /// minutes later, while the handler's check is against the bytes it actually sends. Neither is redundant
    /// because a document can be replaced between the two.
    /// </para>
    /// </remarks>
    private async Task<int?> ResolveReferenceAsync(
        RequestReferenceImageAnalysisViewModel model, CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(model.ReferenceDocumentId, cancellationToken);

        if (!document.Succeeded || document.Value is null)
        {
            return null;
        }

        var current = document.Value.CurrentVersion;

        // The size too, and not only the type. Brand uploads may be far larger than one request can carry
        // (BrandPolicy.SourceUploadMaxBytes against PromptEnvelopePolicy.ImageMaxBytes), so without this a
        // large image is accepted with 202, charged against the allowance, queued, and then guaranteed to
        // fail in the worker minutes later. Refusing it here costs the creator nothing and tells them now.
        return ImageMediaTypes.Contains(current.MediaType)
            && current.SizeBytes > 0
            && current.SizeBytes <= PromptEnvelopePolicy.ImageMaxBytes
                ? current.VersionNumber
                : null;
    }

    private static string SerializeInputs(RequestReferenceImageAnalysisViewModel model, int versionNumber)
    {
        var values = new Dictionary<string, string>(3, StringComparer.Ordinal)
        {
            [ReferenceImageInputs.ReferenceDocumentId] = model.ReferenceDocumentId.ToString(),
            [ReferenceImageInputs.ReferenceVersionNumber] =
                versionNumber.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(model.Note))
        {
            values[ReferenceImageInputs.CreatorNote] = model.Note.ReplaceLineEndings(" ").Trim();
        }

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
