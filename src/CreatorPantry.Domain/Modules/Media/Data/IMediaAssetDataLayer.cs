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

    /// <inheritdoc cref="IMediaAssetRepository.FindByGeneratedImageAsync"/>
    Task<MediaAsset?> FindByGeneratedImageAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>The workspace tags among <paramref name="tagIds"/> that do not exist here.</summary>
    Task<IReadOnlyList<Guid>> UnknownTagsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <inheritdoc cref="IMediaAssetRepository.RecipeExistsAsync"/>
    Task<bool> RecipeExistsAsync(Guid recipeId, CancellationToken cancellationToken);

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
    IMediaAssetObjectGateway objects,
    IGeneratedImageRepository generatedImages,
    IWorkspaceContext workspace,
    IClock clock,
    ILogger<MediaAssetDataLayer> logger) : IMediaAssetDataLayer
{
    public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        assets.ExistsAsync(mediaAssetId, cancellationToken);

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
