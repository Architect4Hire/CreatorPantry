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
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;

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
    IMediaAssetLookupFacade mediaAssets,
    IGeneratedImageLookupFacade generatedImages,
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

        if (reference is null)
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
                TaskInputsJson = SerializeInputs(model, reference),
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
    /// <summary>
    /// The picture the request names, resolved inside this workspace and pinned — or null when it cannot be
    /// read, for any reason.
    /// </summary>
    /// <remarks>
    /// Metadata only. Each source is asked through its own module's facade, under the workspace filter, what
    /// the picture is; no bytes are opened until the worker has claimed the operation and resolved the
    /// workspace again. One null for every cause, so the route gives one answer (tenancy.md).
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>?> ResolveReferenceAsync(
        RequestReferenceImageAnalysisViewModel model, CancellationToken cancellationToken)
    {
        switch (AiReferenceImageSources.Of(model))
        {
            case AiReferenceImageSource.BrandDocument when model.ReferenceDocumentId is { } documentId:
            {
                var document = await documents.GetAsync(documentId, cancellationToken);

                if (!document.Succeeded || document.Value is null)
                {
                    return null;
                }

                var current = document.Value.CurrentVersion;

                return Readable(current.MediaType, current.SizeBytes)
                    ? Named(
                        AiReferenceImageSource.BrandDocument,
                        (ReferenceImageInputs.ReferenceDocumentId, documentId.ToString()),
                        (ReferenceImageInputs.ReferenceVersionNumber, Number(current.VersionNumber)))
                    : null;
            }

            case AiReferenceImageSource.DamAsset when model.MediaAssetId is { } assetId:
            {
                var target = await mediaAssets.ResolvePictureAsync(
                    assetId, model.MediaAssetVersionNumber, cancellationToken);

                // The version comes back from the lookup even when the request named none: that is the pin.
                return target is { VersionNumber: { } versionNumber } && Readable(target.MediaType, target.SizeBytes)
                    ? Named(
                        AiReferenceImageSource.DamAsset,
                        (ReferenceImageInputs.MediaAssetId, assetId.ToString()),
                        (ReferenceImageInputs.MediaAssetVersionNumber, Number(versionNumber)))
                    : null;
            }

            case AiReferenceImageSource.GeneratedImage when model.GeneratedImageId is { } imageId:
            {
                var target = await generatedImages.ResolvePictureAsync(imageId, cancellationToken);

                return target is not null && Readable(target.MediaType, target.SizeBytes)
                    ? Named(
                        AiReferenceImageSource.GeneratedImage,
                        (ReferenceImageInputs.GeneratedImageId, imageId.ToString()))
                    : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a stored picture of that type and size is one this can send.
    /// </summary>
    /// <remarks>
    /// The size too, and not only the type. A stored picture may be far larger than one request can carry
    /// (<see cref="PromptEnvelopePolicy.ImageMaxBytes"/>), so without this a large image is accepted with 202,
    /// charged against the allowance, queued, and then guaranteed to fail in the worker minutes later.
    /// Refusing it here costs the creator nothing and tells them now.
    /// </remarks>
    private static bool Readable(string mediaType, long sizeBytes) =>
        ImageMediaTypes.Contains(mediaType)
        && sizeBytes > 0
        && sizeBytes <= PromptEnvelopePolicy.ImageMaxBytes;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Named(
        AiReferenceImageSource source, params (string Key, string Value)[] ids)
    {
        var values = new Dictionary<string, string>(4, StringComparer.Ordinal)
        {
            [ReferenceImageInputs.Source] = source.ToString(),
        };

        foreach (var (key, value) in ids)
        {
            values[key] = value;
        }

        return values;
    }

    private static string SerializeInputs(
        RequestReferenceImageAnalysisViewModel model, IReadOnlyDictionary<string, string> reference)
    {
        var values = new Dictionary<string, string>(reference, StringComparer.Ordinal);

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
