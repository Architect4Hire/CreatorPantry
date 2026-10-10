using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary for the staged-image library: previewing, downloading, declining, and the
/// retention sweep (IMG-005, IMG-006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never a URL, in either direction.</strong> A caller names an image by its id and gets bytes
/// through this process; no address for a staging object is issued to anyone, and the object key never
/// leaves the domain (media.md). A client cannot name a key, so there is nothing for a traversal sequence
/// to be put in.
/// </para>
/// <para>
/// An unknown image, a neighbour's, and one whose bytes the sweep already removed are one answer, so none
/// of them discloses the others (tenancy.md).
/// </para>
/// </remarks>
public interface IStagedImageFacade
{
    /// <summary>Opens one staged image of the resolved workspace. Any member may read.</summary>
    Task<OperationResult<StagedImageDownload>> OpenAsync(
        Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a staged image, serving the named rendition in place of the original when it has one.
    /// </summary>
    /// <param name="rendition">The rendition wanted, or null for the image as it was staged.</param>
    /// <remarks>
    /// Who may open the image, and which images can be opened at all, are exactly as for the original. A
    /// rendition the image does not have is answered with the original, and the result says which it is.
    /// </remarks>
    Task<OperationResult<StagedImageDownload>> OpenAsync(
        Guid generatedImageId, MediaRenditionPurpose? rendition, CancellationToken cancellationToken);

    /// <summary>Declines one staged image, so the sweep may remove its bytes.</summary>
    Task<OperationResult<StagedImageRejectOutcome>> RejectAsync(
        Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>Runs the resolved workspace's retention. Called by the Worker only; never routed.</summary>
    Task<StagedImageRetentionSummary> RunRetentionAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IStagedImageFacade"/>
internal sealed class StagedImageFacade(
    IStagedImageBusiness business, IWorkspaceContext workspace) : IStagedImageFacade
{
    public Task<OperationResult<StagedImageDownload>> OpenAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        OpenAsync(generatedImageId, rendition: null, cancellationToken);

    public async Task<OperationResult<StagedImageDownload>> OpenAsync(
        Guid generatedImageId, MediaRenditionPurpose? rendition, CancellationToken cancellationToken)
    {
        var opened = await business.OpenAsync(generatedImageId, rendition, cancellationToken);

        return opened.Outcome switch
        {
            StagedImageOpenOutcome.Opened => OperationResult<StagedImageDownload>.Success(opened.Download!),
            StagedImageOpenOutcome.StorageUnavailable => OperationResult<StagedImageDownload>.Failure(
                new OperationError(
                    MediaErrorCodes.StagedImageStorageUnavailable,
                    "The image could not be read right now. Try again shortly.",
                    new Dictionary<string, string[]>())),
            _ => OperationResult<StagedImageDownload>.Failure(NotFound()),
        };
    }

    public async Task<OperationResult<StagedImageRejectOutcome>> RejectAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        // Contributor, the same role that may generate: declining a candidate is part of the same piece of
        // work as asking for it, and a member who may spend the generation budget may certainly say no to
        // what came back.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<StagedImageRejectOutcome>.Failure(new OperationError(
                MediaErrorCodes.StagedImageForbidden,
                "You do not have permission to decline generated images in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var outcome = await business.RejectAsync(generatedImageId, cancellationToken);

        return outcome switch
        {
            StagedImageRejectOutcome.NotFound => OperationResult<StagedImageRejectOutcome>.Failure(NotFound()),
            StagedImageRejectOutcome.NotRejectable => OperationResult<StagedImageRejectOutcome>.Failure(
                new OperationError(
                    MediaErrorCodes.StagedImageNotRejectable,
                    "This image can no longer be declined.",
                    new Dictionary<string, string[]>())),
            _ => OperationResult<StagedImageRejectOutcome>.Success(outcome),
        };
    }

    public Task<StagedImageRetentionSummary> RunRetentionAsync(CancellationToken cancellationToken) =>
        workspace.MembershipId == WorkspaceServiceIdentity.MembershipId
            ? business.RunRetentionAsync(cancellationToken)
            : Task.FromResult(new StagedImageRetentionSummary(0, 0, 0));

    /// <summary>One message for an unknown image and a neighbour's, so neither discloses the other.</summary>
    private static OperationError NotFound() => new(
        MediaErrorCodes.StagedImageNotFound,
        "That generated image could not be found in this workspace.",
        new Dictionary<string, string[]>());
}
