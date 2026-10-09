using System.Globalization;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>Everything one asset creation needs, with the bytes already inspected.</summary>
/// <param name="Content">
/// Opened for reading, positioned at the start. The caller owns disposing it; this layer only reads.
/// </param>
/// <param name="Prompt">
/// The prompt that produced the image, when there is one. Written inside this operation's transaction,
/// never after it.
/// </param>
public sealed record MediaAssetCreation(
    MediaAssetKind Kind,
    MediaAssetVersionSource Source,
    Guid? SourceGeneratedImageId,
    Stream Content,
    string MediaType,
    int Width,
    int Height,
    long SizeBytes,
    string? ContentChecksum,
    string? OriginalFileName,
    MediaAssetMetadataInput Metadata,
    IReadOnlyList<Guid> TagIds,
    MediaAssetRecipeLinkInput? RecipeLink,
    PromptRecordSaveInput? Prompt,
    string UserId);

/// <summary>How one asset creation ended.</summary>
public enum MediaAssetCreateOutcome
{
    Created = 1,

    /// <summary>A replay: this staged image already has an asset, and that one is returned.</summary>
    AlreadyCreated = 2,

    /// <summary>Private storage could not be reached, or disagreed about what it stored. Nothing committed.</summary>
    StorageUnavailable = 3,

    /// <summary>The prompt the request carried was refused. Nothing committed, including the asset.</summary>
    PromptRefused = 4,

    /// <summary>The rows could not be committed. The object written for them has been removed.</summary>
    NotCommitted = 5,
}

/// <param name="Asset">Present when the outcome is <c>Created</c> or <c>AlreadyCreated</c>.</param>
/// <param name="PromptError">Present exactly when the outcome is <c>PromptRefused</c>.</param>
public sealed record MediaAssetCreateResult(
    MediaAssetCreateOutcome Outcome,
    MediaAsset? Asset = null,
    Guid? PromptRecordId = null,
    OperationError? PromptError = null);

/// <summary>Composes the DAM's persistence operations and owns its transaction boundaries (DAM-001).</summary>
public interface IMediaAssetDataLayer
{
    /// <inheritdoc cref="IMediaAssetRepository.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.VersionExistsAsync"/>
    Task<bool> VersionExistsAsync(Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.DescribeAsync"/>
    Task<MediaAssetDescription?> DescribeAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// What one version of a live asset holds, without touching storage: its type, size and checksum (AF.3.4).
    /// </summary>
    /// <param name="versionNumber">The version, or null for the asset's current one.</param>
    /// <returns>
    /// Null for an unknown asset, another workspace's, a removed one, and a version the asset does not have.
    /// </returns>
    Task<MediaPictureTarget?> ResolvePictureAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Opens one version's bytes for another module to read (AF.3.4).
    /// </summary>
    /// <remarks>
    /// The same lookup and the same open as the render routes, so there is no softer way to an asset's bytes
    /// than the one a creator's own download takes: the row is found inside the workspace filter first, and
    /// storage is asked only for a key that row holds.
    /// </remarks>
    Task<MediaPictureOpen> OpenPictureAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.FindByGeneratedImageAsync"/>
    Task<MediaAsset?> FindByGeneratedImageAsync(Guid generatedImageId, CancellationToken cancellationToken);


    /// <inheritdoc cref="IMediaAssetRepository.FindLiveForUpdateAsync"/>
    Task<MediaAsset?> FindLiveForUpdateAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a metadata patch onto an asset already loaded for update (DAM-004), with its audit fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No transaction, deliberately.</strong> One <c>SaveChanges</c> covers the asset row and its tag
    /// rows, and EF Core already wraps a single <c>SaveChanges</c> in a transaction of its own. The creation path
    /// opens one explicitly because it has an object write and a cross-module prompt save to coordinate; this has
    /// neither, and an explicit transaction around one statement batch would be ceremony that reads as though
    /// something more were going on.
    /// </para>
    /// <para>
    /// <strong>Nothing about the bytes is touched.</strong> No version is written, no object is read or moved, and
    /// <c>CurrentVersionNumber</c> is not assigned — a new file is DAM-010, never an edit.
    /// </para>
    /// <para>
    /// Returns false when the row moved between the read and the write. <c>RowVersion</c> is a
    /// <c>rowversion</c> column, so EF puts the original value in the <c>UPDATE</c>'s <c>WHERE</c> and the
    /// database decides — this is the narrow race the caller's token check cannot cover, because that check runs
    /// against the value this transaction read.
    /// </para>
    /// </remarks>
    Task<bool> UpdateMetadataAsync(
        MediaAsset asset,
        MediaAssetMetadataInput merged,
        Guid actorMembershipId,
        CancellationToken cancellationToken);

    /// <summary>The workspace tags among <paramref name="tagIds"/> that do not exist here.</summary>
    Task<IReadOnlyList<Guid>> UnknownTagsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);




    /// <summary>
    /// Opens a live asset's current version for reading (DAM-006, DAM-007).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller owns disposing the render. Nothing above this layer sees the object key: it is read here, handed
    /// to the gateway, and never returned.
    /// </para>
    /// <para>
    /// <strong>No URL and no redirect.</strong> The bytes are proxied, so there is nothing a client could hold or
    /// share (media.md, and this prompt's restriction).
    /// </para>
    /// </remarks>
    /// <param name="naming">
    /// Whether to name a file for the caller. <c>true</c> builds the download's filename from the title, the version
    /// and the stored media type; <c>false</c> leaves it null, because a render names no file. Decided here rather
    /// than in the controller so one piece of code owns the name.
    /// </param>
    Task<MediaAssetOpen> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, CancellationToken cancellationToken);

    /// <summary>
    /// Opens one named version of a live asset for download (DAM-008).
    /// </summary>
    /// <remarks>
    /// <inheritdoc cref="IMediaAssetRepository.FindVersionObjectAsync" path="/remarks/para[2]"/>
    /// Everything after the lookup is what the current-version open does, because the two differ only in which
    /// version they are asked for.
    /// </remarks>
    Task<MediaAssetOpen> OpenVersionAsync(
        Guid mediaAssetId, int versionNumber, bool naming, CancellationToken cancellationToken);


    /// <summary>
    /// Adds a new version to a live asset (DAM-010): allocates the number, writes the object, then commits the row
    /// and the asset's counter together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The order is forced by the key.</strong> A version's object key embeds its number, so the number has to
    /// be chosen before anything can be written — which is why the allocation cannot be a reservation and the race is
    /// resolved by the store instead. Three independent things make two uploads unable to share a number: the store
    /// is create-only, <c>(MediaAssetId, VersionNumber)</c> is unique, and the asset's <c>RowVersion</c> guards the
    /// counter bump. None of them is trusted alone.
    /// </para>
    /// <para>
    /// <strong>An <c>AlreadyExists</c> write compensates nothing</strong>, and that is the one rule here that would be
    /// a data-loss bug to get wrong: the object under that key belongs to whoever won, and deleting it would destroy a
    /// committed version's bytes. The creation path already returns without compensating when nothing was written; for
    /// a deterministic key that behaviour stops being incidental and becomes load-bearing.
    /// </para>
    /// <para>
    /// <strong>Nothing is overwritten.</strong> A new number is a new key, and the store refuses to overwrite in any
    /// case, so the previous version's row and object are untouched — which is what keeps
    /// <c>/versions/{n}/download</c> working for every version an asset has ever had.
    /// </para>
    /// </remarks>
    Task<MediaAssetVersionAdd> AddVersionAsync(
        Guid mediaAssetId,
        Stream content,
        string mediaType,
        int width,
        int height,
        long sizeBytes,
        string? originalFileName,
        Guid actorMembershipId,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.FindForDeleteAsync"/>
    Task<MediaAsset?> FindForDeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.FindReferencesAsync"/>
    Task<(IReadOnlyList<Guid> RecipeIds, int BrandProfileCount, int TestAttachmentCount)> FindReferencesAsync(
        Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// Stamps the tombstone on an asset already loaded for deletion (DAM-005), with its audit entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nothing physical is removed and nothing is scheduled to be.</strong> The objects the asset's versions
    /// name stay exactly where they are, which is what makes this answer fast and makes a half-done deletion
    /// impossible: there is no second system to fail. Reclaiming bytes, if it is ever wanted, is a retention
    /// decision rather than part of a request (media.md, and this prompt's restriction).
    /// </para>
    /// <para>
    /// <strong>No link is touched.</strong> Recipe, brand and test-attachment rows all survive — the restriction
    /// this prompt opens with — so the only rows written are the asset and its audit entry, which
    /// <c>IAuditWriter.Record</c> stages on the same context so the two commit together or neither does.
    /// </para>
    /// <para>
    /// Returns false when the row moved between the read and the write, which the caller turns into the same
    /// conflict a stale token gets.
    /// </para>
    /// </remarks>
    Task<bool> SoftDeleteAsync(
        MediaAsset asset,
        string actorUserId,
        Guid actorMembershipId,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.CuisineExistsAsync"/>
    Task<bool> CuisineExistsAsync(Guid cuisineId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.CourseExistsAsync"/>
    Task<bool> CourseExistsAsync(Guid courseId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.RecipeExistsAsync"/>
    Task<bool> RecipeExistsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>One page of the filtered library, and the total when the caller asked for it.</summary>
    /// <remarks>
    /// A single read, so there is nothing to compose and no transaction to own — the method exists because
    /// the seam does, and because a Business layer reaching the repository would be the defect backend.md
    /// names. Nothing is cached, for the reason <c>IRecipeFacade.SearchAsync</c> records at length.
    /// </remarks>
    Task<(IReadOnlyList<MediaAssetSearchRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        MediaAssetSearchCriteria criteria, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetDetailRepository.FindAsync"/>
    Task<MediaAssetDetailBundle?> FindDetailAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetDetailRepository.IsVisibleAsync"/>
    Task<bool> IsAssetVisibleAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken);


    /// <summary>
    /// Records one use of a live asset (DAM-009), or reports that there is no such asset to record against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>SaveChanges</c>, one row, no transaction of its own — EF already wraps a single save, and there is
    /// nothing else to coordinate: no object, no cross-module write, no outbox.
    /// </para>
    /// <para>
    /// <strong>Visibility is checked in the same call that writes</strong>, rather than by the caller beforehand, so
    /// there is no window where an asset is deleted between the check and the insert. The composite foreign key would
    /// refuse a row for a non-existent asset anyway; what this adds is refusing one for a <em>tombstoned</em> asset,
    /// which the key cannot see.
    /// </para>
    /// <para>
    /// <see cref="MediaAssetUtilization.UtilizedDay"/> is derived here from the date the caller gave, and
    /// <c>CreatedAt</c> comes from the clock — when the log was written, which is a different fact from when the asset
    /// was used.
    /// </para>
    /// </remarks>
    Task<MediaAssetUtilizationRecord?> LogUtilizationAsync(
        Guid mediaAssetId,
        string platformKey,
        DateOnly utilizedOn,
        string? campaignName,
        string? notes,
        Guid actorMembershipId,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetDetailRepository.ListUtilizationAsync"/>
    Task<(IReadOnlyList<MediaAssetUtilizationRecord> Rows, bool HasMore, int? Total)> ListUtilizationAsync(
        MediaAssetUtilizationCriteria criteria, CancellationToken cancellationToken);


    /// <summary>Creates an asset, its first version, its tags, its recipe link and its prompt.</summary>
    /// <param name="savePrompt">
    /// Saves the prompt the creation carries, inside this layer's transaction, and is null when it carries
    /// none.
    /// </param>
    /// <remarks>
    /// <strong>A delegate rather than a facade this layer injects</strong>, which is how
    /// <c>IAiOperationDataLayer.AcceptDraftAsync</c> composes the recipe module's create and for the same
    /// reason: whether a prompt is written is a decision, and decisions belong in Business, while the
    /// transaction it has to happen inside belongs here. A data layer that injected another module's
    /// facade would be reaching past the next layer, and it would close a dependency cycle — the prompt
    /// seam resolves a DAM asset id through this module on its way in.
    /// </remarks>
    Task<MediaAssetCreateResult> CreateAsync(
        MediaAssetCreation creation,
        Func<Guid, CancellationToken, Task<OperationResult<SavedPromptRecordServiceModel>>>? savePrompt,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetDataLayer"/>
/// <remarks>
/// <para>
/// <strong>Bytes first, then every row in one transaction, and the object is the only thing compensated.</strong>
/// The object is written before any row names it, so a row never points at bytes that are not there. Every
/// row — the asset, its version, its tags, its recipe link and its prompt record — commits together or not
/// at all. If the commit fails, the object written for it is removed; if the object write fails, no row was
/// ever attempted.
/// </para>
/// <para>
/// <strong>The prompt is written inside this transaction, and the direction is not negotiable.</strong>
/// <c>PromptRecord</c> is <see cref="IImmutableRecord"/>, so <c>ImmutableRecordInterceptor</c> refuses every
/// delete: a prompt committed beside an asset whose own commit then failed could only be erased, not
/// compensated. <c>PromptRecordDataLayer.SaveAsync</c> opens no transaction of its own precisely so that it
/// enlists in this one. It arrives as a delegate from Business — see <see cref="IMediaAssetDataLayer.CreateAsync"/>.
/// </para>
/// </remarks>
internal sealed class MediaAssetDataLayer(
    IMediaAssetRepository assets,
    IMediaAssetSearchRepository search,
    IMediaAssetDetailRepository detail,
    IMediaAssetObjectGateway objects,
    IGeneratedImageRepository generatedImages,
    IWorkspaceContext workspace,
    IAuditWriter auditWriter,
    IClock clock,
    ILogger<MediaAssetDataLayer> logger) : IMediaAssetDataLayer
{
    public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        assets.ExistsAsync(mediaAssetId, cancellationToken);

    public Task<bool> VersionExistsAsync(Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
        assets.VersionExistsAsync(mediaAssetId, versionNumber, cancellationToken);

    public Task<MediaAssetDescription?> DescribeAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        assets.DescribeAsync(mediaAssetId, cancellationToken);

    public async Task<MediaPictureTarget?> ResolvePictureAsync(
        Guid mediaAssetId, int? versionNumber, CancellationToken cancellationToken)
    {
        var version = versionNumber is { } number
            ? await assets.FindVersionObjectAsync(mediaAssetId, number, cancellationToken)
            : await assets.FindCurrentVersionObjectAsync(mediaAssetId, cancellationToken);

        // The key stays here. What crosses the boundary is only what a caller needs to decide whether to ask.
        return version is null
            ? null
            : new MediaPictureTarget(
                version.VersionNumber, version.MediaType, version.SizeBytes, version.ContentChecksum);
    }

    public async Task<MediaPictureOpen> OpenPictureAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken)
    {
        var opened = await OpenVersionAsync(mediaAssetId, versionNumber, naming: false, cancellationToken);

        return opened.Outcome switch
        {
            MediaAssetOpenOutcome.Opened => new MediaPictureOpen(
                MediaPictureOpenOutcome.Opened,
                new MediaPictureContent(
                    opened.Render!,
                    opened.Render!.Content,
                    opened.Render.MediaType,
                    opened.Render.ContentChecksum,
                    opened.Render.VersionNumber)),
            MediaAssetOpenOutcome.StorageUnavailable => new MediaPictureOpen(MediaPictureOpenOutcome.StorageUnavailable),
            _ => new MediaPictureOpen(MediaPictureOpenOutcome.NotFound),
        };
    }

    public Task<(IReadOnlyList<MediaAssetSearchRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        MediaAssetSearchCriteria criteria, CancellationToken cancellationToken) =>
        search.SearchAsync(criteria, cancellationToken);

    public Task<MediaAssetDetailBundle?> FindDetailAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken) =>
        detail.FindAsync(mediaAssetId, includeDeleted, cancellationToken);


    public Task<MediaAsset?> FindLiveForUpdateAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        assets.FindLiveForUpdateAsync(mediaAssetId, cancellationToken);

    public async Task<bool> UpdateMetadataAsync(
        MediaAsset asset,
        MediaAssetMetadataInput merged,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(merged);

        asset.Title = merged.Title!;
        asset.Description = merged.Description;
        asset.AltText = merged.AltText;
        asset.ChannelKey = merged.ChannelKey;
        asset.PlatformKey = merged.PlatformKey;
        asset.Day = merged.Day;
        asset.StyleKey = merged.StyleKey;
        asset.CuisineId = merged.CuisineId;
        asset.CourseId = merged.CourseId;
        asset.RightsHolder = merged.RightsHolder;
        asset.AttributionText = merged.AttributionText;

        SyncTags(asset, merged.WorkspaceTagIds ?? []);

        // Set here rather than by the caller, so no write path can forget them. WorkspaceId, CreatedAt and
        // CreatedByMembershipId are deliberately untouched: ownership and authorship are not editable, and a
        // patch that could move either would be the defect tenancy.md names.
        asset.UpdatedAt = clock.UtcNow;
        asset.UpdatedByMembershipId = actorMembershipId;

        try
        {
            await assets.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody committed between this transaction's read and its write. Not an error to log: it is the
            // guard working, and the caller turns it into the same 409 a stale token gets.
            return false;
        }
    }

    /// <summary>
    /// Makes the asset's tags exactly <paramref name="requested"/>.
    /// </summary>
    /// <remarks>
    /// A set difference rather than clear-and-re-add: re-adding a tag that was already there would delete and
    /// reinsert a row for no reason, and on a composite key that is two statements where none were needed.
    /// </remarks>
    private void SyncTags(MediaAsset asset, IReadOnlyList<Guid> requested)
    {
        var wanted = requested.ToHashSet();

        foreach (var tag in asset.Tags.Where(tag => !wanted.Contains(tag.WorkspaceTagId)).ToList())
        {
            assets.Remove(tag);
            asset.Tags.Remove(tag);
        }

        var held = asset.Tags.Select(tag => tag.WorkspaceTagId).ToHashSet();

        foreach (var tagId in wanted.Where(tagId => !held.Contains(tagId)))
        {
            asset.Tags.Add(new MediaAssetTag { MediaAssetId = asset.Id, WorkspaceTagId = tagId });
        }
    }




    public async Task<MediaAssetOpen> OpenCurrentVersionAsync(
        Guid mediaAssetId, bool naming, CancellationToken cancellationToken) =>
        await OpenAsync(
            mediaAssetId,
            await assets.FindCurrentVersionObjectAsync(mediaAssetId, cancellationToken),
            naming,
            cancellationToken);

    public async Task<MediaAssetOpen> OpenVersionAsync(
        Guid mediaAssetId, int versionNumber, bool naming, CancellationToken cancellationToken) =>
        await OpenAsync(
            mediaAssetId,
            await assets.FindVersionObjectAsync(mediaAssetId, versionNumber, cancellationToken),
            naming,
            cancellationToken);

    /// <summary>
    /// Opens whichever version was found, or reports why it could not be.
    /// </summary>
    /// <remarks>
    /// Shared by both opens so the gateway call, the storage-failure split and the file naming cannot drift between
    /// "the current version" and "this version" — the two routes differ only in the lookup above this.
    /// </remarks>
    private async Task<MediaAssetOpen> OpenAsync(
        Guid mediaAssetId,
        MediaAssetVersionObjectRecord? version,
        bool naming,
        CancellationToken cancellationToken)
    {

        if (version is null)
        {
            return new MediaAssetOpen(MediaAssetOpenOutcome.NotFound);
        }

        MediaAssetObjectContent? content;

        try
        {
            content = await objects.OpenReadAsync(version.ObjectKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The row says the bytes are there and storage disagreed. Logged without the key, because a key in a
            // log is a key outside the two places allowed to hold one.
            logger.LogWarning(
                exception,
                "DAM render could not reach storage. mediaAssetId={MediaAssetId} version={VersionNumber}",
                mediaAssetId, version.VersionNumber);

            return new MediaAssetOpen(MediaAssetOpenOutcome.StorageUnavailable);
        }

        // Null rather than an exception is the gateway refusing the key — a key that is not this workspace's, or an
        // object that is simply not there. Both are "nothing to render" rather than a storage fault, so neither is
        // worth telling a creator to retry.
        return content is null
            ? new MediaAssetOpen(MediaAssetOpenOutcome.NotFound)
            : new MediaAssetOpen(
                MediaAssetOpenOutcome.Opened,
                new MediaAssetRender(
                    content,
                    version.VersionNumber,

                    // From the store's media type rather than the row's, so the extension describes the bytes
                    // actually being sent. The two agree, and taking the one being sent means they cannot disagree
                    // in a response.
                    naming
                        ? MediaAssetDownloadFileName.For(
                            version.Title, version.VersionNumber, content.MediaType)
                        : null));
    }


    public async Task<MediaAssetVersionAdd> AddVersionAsync(
        Guid mediaAssetId,
        Stream content,
        string mediaType,
        int width,
        int height,
        long sizeBytes,
        string? originalFileName,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        if (await assets.NextVersionNumberAsync(mediaAssetId, cancellationToken) is not { } versionNumber)
        {
            return new MediaAssetVersionAdd(MediaAssetVersionAddOutcome.NotFound);
        }

        var write = await objects.PutVersionAsync(
            mediaAssetId, versionNumber, content, mediaType, MediaPolicy.ImageMaxBytes, cancellationToken);

        if (write.Outcome is MediaAssetObjectWriteOutcome.AlreadyExists)
        {
            // Somebody committed this number while this request was reading or uploading. NOTHING is removed: the
            // object under that key is theirs, and deleting it would destroy a committed version's bytes.
            logger.LogInformation(
                "DAM version {VersionNumber} was taken while uploading; the existing object is untouched.",
                versionNumber);

            return new MediaAssetVersionAdd(MediaAssetVersionAddOutcome.VersionTaken);
        }

        if (write.Outcome is not MediaAssetObjectWriteOutcome.Stored)
        {
            logger.LogError(
                "DAM version could not store its object ({Outcome}); nothing was written.", write.Outcome);

            return new MediaAssetVersionAdd(MediaAssetVersionAddOutcome.StorageUnavailable);
        }

        var stored = write.Object!;

        // The store measured these as it wrote. A row that disagreed with the object it names would make every later
        // read a guess, so the object goes rather than the row being written wrong.
        if (stored.SizeBytes != sizeBytes)
        {
            logger.LogError("DAM storage disagreed with the inspected bytes; the object has been removed.");
            await CompensateAsync(stored.ObjectKey);

            return new MediaAssetVersionAdd(MediaAssetVersionAddOutcome.StorageUnavailable);
        }

        return await CommitVersionAsync(
            mediaAssetId, versionNumber, stored, mediaType, width, height, originalFileName, actorMembershipId,
            cancellationToken);
    }

    /// <summary>
    /// The version row and the asset's counter, in one transaction, with the object removed if it does not commit.
    /// </summary>
    /// <remarks>
    /// The asset is loaded tracked so its <c>RowVersion</c> goes into the <c>UPDATE</c>'s <c>WHERE</c>: if anything
    /// else changed the asset since the number was read, this write is refused rather than overwriting it. A refusal
    /// here is <see cref="MediaAssetVersionAddOutcome.NotCommitted"/> rather than <c>VersionTaken</c>, because the
    /// object under this key <em>is</em> ours and does have to go.
    /// </remarks>
    private async Task<MediaAssetVersionAdd> CommitVersionAsync(
        Guid mediaAssetId,
        int versionNumber,
        MediaAssetObject stored,
        string mediaType,
        int width,
        int height,
        string? originalFileName,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await assets.BeginTransactionAsync(cancellationToken);
        var now = clock.UtcNow;

        var version = new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            MediaAssetId = mediaAssetId,
            VersionNumber = versionNumber,
            MediaType = mediaType,
            SizeBytes = stored.SizeBytes,
            Width = width,
            Height = height,
            ContentChecksum = stored.ContentChecksum,
            ObjectKey = stored.ObjectKey,
            OriginalFileName = originalFileName,
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actorMembershipId,
            CreatedAt = now,
        };

        try
        {
            var asset = await assets.FindLiveForUpdateAsync(mediaAssetId, cancellationToken);

            if (asset is null)
            {
                // Deleted between the number read and here. The object is ours, so it goes.
                await RollBackAsync(transaction, stored.ObjectKey);

                return new MediaAssetVersionAdd(MediaAssetVersionAddOutcome.NotFound);
            }

            assets.Add(version);

            // The new version becomes current, which is what makes /content and /download serve it. Audit fields move
            // with it: adding a version is the last thing that happened to this asset.
            asset.CurrentVersionNumber = versionNumber;
            asset.UpdatedAt = now;
            asset.UpdatedByMembershipId = actorMembershipId;

            await assets.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new MediaAssetVersionAdd(
                MediaAssetVersionAddOutcome.Added,
                new MediaAssetVersionServiceModel(
                    version.VersionNumber,
                    version.MediaType,
                    version.Width,
                    version.Height,
                    version.SizeBytes,
                    version.ContentChecksum,
                    version.OriginalFileName,
                    version.Source,
                    version.SourceGeneratedImageId,
                    version.CreatedAt));
        }
        catch (Exception exception) when (exception is DbUpdateException or DbUpdateConcurrencyException)
        {
            // Either the unique (asset, version) index refused the row — the race lost at the database rather than at
            // the store — or the asset moved under us. Both leave the object ours to remove.
            logger.LogError(
                "DAM version could not commit ({ExceptionType}); removing the object written for it.",
                exception.GetType().Name);

            await RollBackAsync(transaction, stored.ObjectKey);

            return new MediaAssetVersionAdd(MediaAssetVersionAddOutcome.NotCommitted);
        }
    }

    public Task<MediaAsset?> FindForDeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        assets.FindForDeleteAsync(mediaAssetId, cancellationToken);

    public Task<(IReadOnlyList<Guid> RecipeIds, int BrandProfileCount, int TestAttachmentCount)>
        FindReferencesAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        assets.FindReferencesAsync(mediaAssetId, cancellationToken);

    public async Task<bool> SoftDeleteAsync(
        MediaAsset asset,
        string actorUserId,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var now = clock.UtcNow;

        asset.DeletedAt = now;
        asset.DeletedByMembershipId = actorMembershipId;

        // UpdatedAt moves too: a tombstone is the last thing that happened to this row, and leaving it pointing at
        // an earlier edit would make the asset look untouched since before it was removed.
        asset.UpdatedAt = now;
        asset.UpdatedByMembershipId = actorMembershipId;

        auditWriter.Record(new AuditEntry(
            actorUserId,
            MediaAuditActions.Deleted,
            MediaAuditActions.ResourceType,
            asset.Id.ToString("D"),
            Guid.NewGuid(),

            // No creator text at all, not even the title. AuditLog says these fields must never carry content,
            // and RecipeBusiness spells out what that means in practice — "no title, no creator text" — so a
            // title here would be this module deciding the rule applies less to it. The asset is identified by
            // ResourceId, and a reader who needs its name reads the tombstone with ?includeDeleted=true.
            "Soft-deleted the asset. No bytes were removed.",

            // A pointer, which is what these fields are for: which version the asset stood at when it went.
            BeforeReference: asset.CurrentVersionNumber.ToString(CultureInfo.InvariantCulture)));

        try
        {
            await assets.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody committed between this transaction's read and its write — the guard working, not an error
            // to log. The audit entry goes with it, because it was never saved.
            return false;
        }
    }

    public Task<bool> CuisineExistsAsync(Guid cuisineId, CancellationToken cancellationToken) =>
        assets.CuisineExistsAsync(cuisineId, cancellationToken);

    public Task<bool> CourseExistsAsync(Guid courseId, CancellationToken cancellationToken) =>
        assets.CourseExistsAsync(courseId, cancellationToken);

    public Task<bool> IsAssetVisibleAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken) =>
        detail.IsVisibleAsync(mediaAssetId, includeDeleted, cancellationToken);


    public async Task<MediaAssetUtilizationRecord?> LogUtilizationAsync(
        Guid mediaAssetId,
        string platformKey,
        DateOnly utilizedOn,
        string? campaignName,
        string? notes,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        if (!await detail.IsVisibleAsync(mediaAssetId, includeDeleted: false, cancellationToken))
        {
            return null;
        }

        var utilization = new MediaAssetUtilization
        {
            Id = Guid.NewGuid(),
            MediaAssetId = mediaAssetId,
            PlatformKey = platformKey,
            UtilizedOn = utilizedOn,

            // Derived, never taken from the request. A calendar date has one day of the week in every zone, so this
            // cannot disagree with the date the creator gave — which a client-supplied day could.
            UtilizedDay = utilizedOn.DayOfWeek,
            CampaignName = campaignName,
            Notes = notes,
            LoggedByMembershipId = actorMembershipId,

            // When the log was written, not when the asset was used. The two are different facts and the row keeps
            // both.
            CreatedAt = clock.UtcNow,
        };

        assets.Add(utilization);
        await assets.SaveChangesAsync(cancellationToken);

        return new MediaAssetUtilizationRecord(
            utilization.Id,
            utilization.PlatformKey,
            utilization.UtilizedOn,
            utilization.UtilizedDay,
            utilization.CampaignName,
            utilization.Notes,
            utilization.CreatedAt);
    }

    public Task<(IReadOnlyList<MediaAssetUtilizationRecord> Rows, bool HasMore, int? Total)> ListUtilizationAsync(
        MediaAssetUtilizationCriteria criteria, CancellationToken cancellationToken) =>
        detail.ListUtilizationAsync(criteria, cancellationToken);


    public Task<MediaAsset?> FindByGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        assets.FindByGeneratedImageAsync(generatedImageId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> UnknownTagsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return [];
        }

        var known = (await assets.FindTagIdsAsync(tagIds, cancellationToken)).ToHashSet();

        return [.. tagIds.Where(tag => !known.Contains(tag))];
    }

    public Task<bool> RecipeExistsAsync(Guid recipeId, CancellationToken cancellationToken) =>
        assets.RecipeExistsAsync(recipeId, cancellationToken);

    public async Task<MediaAssetCreateResult> CreateAsync(
        MediaAssetCreation creation,
        Func<Guid, CancellationToken, Task<OperationResult<SavedPromptRecordServiceModel>>>? savePrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);

        var now = clock.UtcNow;
        var asset = NewAsset(creation, now);
        var version = NewVersion(creation, asset.Id, now);

        // Before any row, and measured as it streams. The inspected size is the limit: one byte more is
        // already not the content that was inspected and scanned.
        var write = await objects.PutVersionAsync(
            asset.Id,
            version.VersionNumber,
            creation.Content,
            creation.MediaType,
            creation.SizeBytes,
            cancellationToken);

        if (write.Outcome is not MediaAssetObjectWriteOutcome.Stored)
        {
            logger.LogWarning(
                "DAM creation could not store its object ({Outcome}); nothing was written.", write.Outcome);

            return new MediaAssetCreateResult(MediaAssetCreateOutcome.StorageUnavailable);
        }

        var stored = write.Object!;

        // A staged image arrives with a checksum its own staging recorded, and the copy has to match it;
        // an upload has none yet, because the store is what computes one as it writes. Either way the row
        // ends up describing bytes something measured, which is what "preserve actual media metadata"
        // means — and either way the size is known in advance and must agree.
        if (stored.SizeBytes != creation.SizeBytes
            || (creation.ContentChecksum is { } expected
                && !string.Equals(stored.ContentChecksum, expected, StringComparison.Ordinal)))
        {
            logger.LogError("DAM storage disagreed with the inspected bytes; the object has been removed.");
            await CompensateAsync(stored.ObjectKey);

            return new MediaAssetCreateResult(MediaAssetCreateOutcome.StorageUnavailable);
        }

        // What the store measured, which for an upload is the first time anything has. The version row
        // never carries a checksum nobody computed.
        version.ObjectKey = stored.ObjectKey;
        version.ContentChecksum = stored.ContentChecksum;

        return await CommitAsync(creation, asset, version, now, savePrompt, cancellationToken);
    }

    /// <summary>
    /// Every row of the creation, in one transaction, with the object removed if it does not commit.
    /// </summary>
    private async Task<MediaAssetCreateResult> CommitAsync(
        MediaAssetCreation creation,
        MediaAsset asset,
        MediaAssetVersion version,
        DateTimeOffset now,
        Func<Guid, CancellationToken, Task<OperationResult<SavedPromptRecordServiceModel>>>? savePrompt,
        CancellationToken cancellationToken)
    {
        // Null when one is already open — a request carrying an Idempotency-Key has one, and committing a
        // transaction this layer did not begin would end somebody else's unit of work.
        await using var transaction = await assets.BeginTransactionAsync(cancellationToken);
        Guid? promptRecordId = null;

        try
        {
            assets.Add(asset);
            assets.Add(version);

            foreach (var tagId in creation.TagIds)
            {
                assets.Add(new MediaAssetTag
                {
                    WorkspaceId = workspace.WorkspaceId,
                    MediaAssetId = asset.Id,
                    WorkspaceTagId = tagId,
                });
            }

            if (creation.RecipeLink is { } link)
            {
                assets.Add(new RecipeAssetLink
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.WorkspaceId,
                    RecipeId = link.RecipeId,
                    MediaAssetId = asset.Id,
                    Role = RecipeAssetRole.Gallery,
                    Caption = link.Caption,
                    SortOrder = await assets.NextRecipeLinkOrderAsync(link.RecipeId, cancellationToken),
                });
            }

            // The staged image becomes Kept, which is what makes its staging bytes redundant: 12.8's
            // retention sweep collects a Kept image's object precisely because a committed asset now owns
            // a copy of it. Inside the transaction, so an image is never marked kept by a creation that
            // did not commit.
            if (creation.SourceGeneratedImageId is { } imageId)
            {
                var image = await generatedImages.FindAsync(imageId, cancellationToken);

                if (image is null || image.Status is not GeneratedImageStatus.Staged)
                {
                    await RollBackAsync(transaction, version.ObjectKey);

                    return new MediaAssetCreateResult(MediaAssetCreateOutcome.NotCommitted);
                }

                image.Status = GeneratedImageStatus.Kept;
                image.StatusChangedAt = now;
            }

            await assets.SaveChangesAsync(cancellationToken);

            if (savePrompt is not null)
            {
                // Inside the transaction. A refusal here takes the asset down with it rather than leaving
                // a prompt nothing can delete beside it.
                var saved = await savePrompt(asset.Id, cancellationToken);

                if (!saved.Succeeded)
                {
                    await RollBackAsync(transaction, version.ObjectKey);

                    return new MediaAssetCreateResult(
                        MediaAssetCreateOutcome.PromptRefused, PromptError: saved.Error);
                }

                promptRecordId = saved.Value!.PromptRecordId;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new MediaAssetCreateResult(MediaAssetCreateOutcome.Created, asset, promptRecordId);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(
                "DAM creation could not commit ({ExceptionType}); removing the object written for it.",
                exception.GetType().Name);

            await RollBackAsync(transaction, version.ObjectKey);

            return new MediaAssetCreateResult(MediaAssetCreateOutcome.NotCommitted);
        }
    }

    /// <summary>
    /// Rolls the rows back and removes the object they would have owned, in that order.
    /// </summary>
    /// <remarks>
    /// Rows first, because a rolled-back transaction is a certainty and a storage delete is a request that
    /// may fail. If the delete then fails, the object is unreferenced and the reconciliation that 12.8
    /// built for staged images is the shape the DAM will need too — recorded here rather than discovered.
    /// </remarks>
    private async Task RollBackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, string objectKey)
    {
        if (transaction is not null)
        {
            // Not cancellable: this runs on the way out of a failure, and a token that has already fired
            // would leave the transaction open and the object orphaned.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await CompensateAsync(objectKey);
    }

    /// <summary>Removes an object whose owning rows did not commit.</summary>
    /// <remarks>
    /// <c>CancellationToken.None</c>, so compensation is not cancelled with the request. A delete that
    /// itself fails is logged and swallowed: the write it compensates has already failed, and throwing
    /// here would replace a recorded outcome with an unhandled exception.
    /// </remarks>
    private async Task CompensateAsync(string objectKey)
    {
        try
        {
            await objects.DeleteAsync(objectKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "A DAM object could not be removed after a failed creation ({ExceptionType}); it is unreferenced.",
                exception.GetType().Name);
        }
    }

    private MediaAsset NewAsset(MediaAssetCreation creation, DateTimeOffset now)
    {
        var metadata = creation.Metadata;

        return new MediaAsset
        {
            Id = Guid.NewGuid(),

            // From the resolved context, never a request field. The ownership interceptor stamps it too;
            // setting it here keeps the graph coherent before it is saved (tenancy.md).
            WorkspaceId = workspace.WorkspaceId,
            Title = metadata.Title!.Trim(),
            Description = Trimmed(metadata.Description),
            Kind = creation.Kind,
            AltText = Trimmed(metadata.AltText),
            ChannelKey = Trimmed(metadata.ChannelKey),
            PlatformKey = Trimmed(metadata.PlatformKey),
            Day = metadata.Day,
            StyleKey = Trimmed(metadata.StyleKey),
            CuisineId = metadata.CuisineId,
            CourseId = metadata.CourseId,
            RightsHolder = Trimmed(metadata.RightsHolder),
            AttributionText = Trimmed(metadata.AttributionText),
            CurrentVersionNumber = FirstVersionNumber,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
        };
    }

    private MediaAssetVersion NewVersion(MediaAssetCreation creation, Guid assetId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            MediaAssetId = assetId,
            VersionNumber = FirstVersionNumber,

            // Every one of these was established by reading the bytes, never from anything a client or a
            // provider declared (media.md).
            MediaType = creation.MediaType,
            SizeBytes = creation.SizeBytes,
            Width = creation.Width,
            Height = creation.Height,
            ContentChecksum = string.Empty,
            OriginalFileName = Trimmed(creation.OriginalFileName),
            Source = creation.Source,
            SourceGeneratedImageId = creation.SourceGeneratedImageId,
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
        };

    /// <summary>An asset is created with its first version, so the number is settled here.</summary>
    private const int FirstVersionNumber = 1;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
