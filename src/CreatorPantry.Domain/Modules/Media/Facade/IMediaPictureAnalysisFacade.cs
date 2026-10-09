using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary another module crosses to keep, or read back, what a model saw in one of this
/// workspace's pictures (AF.3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it exists.</strong> A reading of a picture used to live only in the proposal it arrived in,
/// findable by request and not by picture. Work grounded on a picture later (AF.1.5) had no way to ask "has
/// this been looked at?" short of looking again, which costs a creator's allowance twice for one answer.
/// </para>
/// <para>
/// <strong>No role gate, and no workspace argument.</strong> Like the lookup facades beside it: a resolved
/// workspace context is the authorization, and every id is resolved inside that workspace. A caller in a
/// background task has validated its workspace before reaching this (tenancy.md).
/// </para>
/// <para>
/// <strong>What comes back is model output that no creator has reviewed.</strong> It is prose a model wrote
/// about a picture that may itself carry instruction-like writing, and nothing strips such wording from it:
/// the validator it passed bans claims, links, markup and render directives, not a sentence that reads like
/// an order. A caller that puts it in a prompt must send it as untrusted text
/// (<c>PromptEnvelopeBuilder.WithUntrustedText</c>), never inside the task instructions; must carry each
/// observation's <see cref="MediaPictureObservation.Confidence"/> with its text, because a "probable"
/// inference with its label dropped reads as a fact; and must never present any of it as the creator's alt
/// text or show it to a creator as something they wrote.
/// </para>
/// <para>
/// <strong>Only a reading of the picture itself is kept.</strong> One steered by a creator's note, and one of
/// an animation of which only the first frame was read, stay with their proposals and are not found here.
/// </para>
/// </remarks>
public interface IMediaPictureAnalysisFacade
{
    /// <inheritdoc cref="IMediaPictureAnalysisBusiness.KeepAsync"/>
    /// <remarks>
    /// Taken as plain values rather than as one of this module's own input types, which do not cross a module
    /// boundary: exactly one picture is named — an asset with its version, or a generated image — with the
    /// checksum of the bytes that were read.
    /// </remarks>
    Task<bool> KeepAsync(
        Guid? mediaAssetId,
        int? mediaAssetVersionNumber,
        Guid? generatedImageId,
        string contentChecksum,
        IReadOnlyList<MediaPictureObservation> observations,
        Guid aiOperationId,
        string promptTemplateId,
        string promptTemplateVersion,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaPictureAnalysisBusiness.FindForAssetAsync"/>
    /// <remarks>
    /// Null for an unknown asset, another workspace's, a removed one, and a version nobody has read.
    /// </remarks>
    Task<MediaPictureAnalysisDescription?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaPictureAnalysisBusiness.FindForGeneratedImageAsync"/>
    /// <remarks>
    /// Null for an unknown image, another workspace's, a declined or expired one, and one nobody has read.
    /// </remarks>
    Task<MediaPictureAnalysisDescription?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaPictureAnalysisFacade"/>
internal sealed class MediaPictureAnalysisFacade(IMediaPictureAnalysisBusiness business)
    : IMediaPictureAnalysisFacade
{
    public Task<bool> KeepAsync(
        Guid? mediaAssetId,
        int? mediaAssetVersionNumber,
        Guid? generatedImageId,
        string contentChecksum,
        IReadOnlyList<MediaPictureObservation> observations,
        Guid aiOperationId,
        string promptTemplateId,
        string promptTemplateVersion,
        CancellationToken cancellationToken) =>
        business.KeepAsync(
            new MediaPictureAnalysisRecord(
                mediaAssetId,
                mediaAssetVersionNumber,
                generatedImageId,
                contentChecksum,
                observations,
                aiOperationId,
                promptTemplateId,
                promptTemplateVersion),
            cancellationToken);

    public Task<MediaPictureAnalysisDescription?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
        business.FindForAssetAsync(mediaAssetId, versionNumber, cancellationToken);

    public Task<MediaPictureAnalysisDescription?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        business.FindForGeneratedImageAsync(generatedImageId, cancellationToken);
}
