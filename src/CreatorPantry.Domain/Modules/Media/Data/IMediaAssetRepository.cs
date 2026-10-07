using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// EF Core access to the DAM, within the resolved workspace.
/// </summary>
/// <remarks>
/// Every read is filtered, so a neighbour's asset is <em>not found</em> rather than found and refused —
/// which is what makes "unknown id" and "someone else's id" one answer at the HTTP boundary (tenancy.md).
/// </remarks>
public interface IMediaAssetRepository
{
    /// <summary>Whether this workspace holds an asset with that id and has not deleted it.</summary>
    Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>The asset and its versions, or null when this workspace holds none with that id.</summary>
    Task<MediaAsset?> FindAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// The asset and its tags, tracked for editing, or null when this workspace has no live asset with that id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Tags but not versions</strong>, because a metadata patch replaces the tag set and must never touch
    /// the bytes. Loading versions would put entities a patch has no business writing into the change tracker,
    /// where <c>ImmutableRecordInterceptor</c> is the only thing that would stop a stray edit reaching the
    /// database.
    /// </para>
    /// <para>
    /// <strong>Tracked, unlike every other read in this module</strong>, because the caller is about to change it
    /// and <c>RowVersion</c> has to be the one the database holds — an untracked copy would have to be re-attached
    /// to be written, which is how an optimistic-concurrency check gets lost.
    /// </para>
    /// <para>
    /// Tombstones are excluded here rather than by the caller: a patch is not a way to edit or restore a deleted
    /// asset, so null covers an unknown id, another workspace's asset and a deleted one alike — one 404 for all
    /// three (tenancy.md).
    /// </para>
    /// </remarks>
    Task<MediaAsset?> FindLiveForUpdateAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// The asset, tracked for editing, <strong>including a tombstone</strong>, or null when this workspace has none
    /// with that id.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="FindLiveForUpdateAsync"/>, a deleted asset is found. That is what lets a repeated deletion
    /// answer with the existing tombstone instead of a 404 — the command is idempotent, and a client that cannot
    /// tell "already done" from "never existed" cannot retry safely. No tags, because a deletion neither reads nor
    /// changes them.
    /// </remarks>
    Task<MediaAsset?> FindForDeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// Where a live asset's current version's bytes are, or null when there is nothing to render.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null covers an unknown id, another workspace's asset, a soft-deleted one, and a live asset whose current
    /// version row is missing — four causes, one answer, because a caller must not be able to tell them apart
    /// (tenancy.md).
    /// </para>
    /// <para>
    /// <strong>The narrowest read that can serve a render, and the only one that selects an object key.</strong>
    /// The detail and search projections deliberately never touch <c>ObjectKey</c>; this one must, because the
    /// bytes cannot be opened without it, so it reads almost nothing else about the asset. The one exception is the
    /// title, which 12.9g needs because the download names its file from it — no description, no alt text, no tags,
    /// no rights. A key read by a query that returned creator content wholesale would be a key sitting in a layer
    /// that publishes things.
    /// </para>
    /// <para>
    /// Matched on <c>CurrentVersionNumber</c> rather than the highest version number: the asset's counter is what
    /// "current" means, and a route that rendered the newest row instead would disagree with every other read.
    /// </para>
    /// </remarks>
    Task<MediaAssetVersionObjectRecord?> FindCurrentVersionObjectAsync(
        Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// Where one named version's bytes are, or null when that version is not this asset's in this workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both identifiers in one predicate, which is the whole point (DAM-008).</strong> The match is on
    /// <c>(MediaAssetId, VersionNumber)</c> together — unique by index — so asking one asset's route for a number
    /// that belongs to another finds nothing rather than that other asset's bytes. The workspace is the global query
    /// filter on top of that.
    /// </para>
    /// <para>
    /// <strong>Never falls back.</strong> A number this asset has no version for is null, not its current version:
    /// serving bytes a caller did not ask for would be worse than refusing, because they would have no way to tell.
    /// A zero or negative number is also null — <c>CK_MediaAssetVersions_VersionNumber_Positive</c> makes those
    /// unrepresentable, so there is nothing to disclose and no reason to answer them differently.
    /// </para>
    /// <para>
    /// A tombstoned asset is excluded here as it is for the current version: removing an asset takes its history
    /// out of reach too, not just its latest bytes.
    /// </para>
    /// </remarks>
    Task<MediaAssetVersionObjectRecord?> FindVersionObjectAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);



    /// <summary>
    /// What still points at this asset, across all three link types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One statement, three counts and the recipe ids. Counted rather than loaded: the caller names recipes through
    /// the Recipes facade and needs nothing else about a brand slot or a test attachment than whether any exist.
    /// </para>
    /// <para>
    /// All three sets are workspace-filtered, so none of these numbers can describe a neighbour's content.
    /// <c>BrandAssetLink</c> is the Brand module's entity and <c>TestAttachmentLink</c> the Recipes module's; both
    /// carry a foreign key into <c>MediaAsset</c>, which is what lets them be named here (backend.md).
    /// </para>
    /// </remarks>
    Task<(IReadOnlyList<Guid> RecipeIds, int BrandProfileCount, int TestAttachmentCount)> FindReferencesAsync(
        Guid mediaAssetId, CancellationToken cancellationToken);



    /// <summary>
    /// The asset whose first version was made from this staged image, or null.
    /// </summary>
    /// <remarks>
    /// The natural replay guard for the generated path: a staged image is <c>Kept</c> exactly once, and a
    /// second attempt to keep it should hand back the asset the first one made rather than a second asset
    /// or an error. No idempotency header is needed for it to work.
    /// </remarks>
    Task<MediaAsset?> FindByGeneratedImageAsync(Guid generatedImageId, CancellationToken cancellationToken);

    /// <summary>The workspace tags among <paramref name="tagIds"/> that this workspace actually has.</summary>
    Task<IReadOnlyList<Guid>> FindTagIdsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <summary>Whether this workspace holds a recipe with that id.</summary>
    Task<bool> RecipeExistsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the shared reference vocabulary has a cuisine with that id.
    /// </summary>
    /// <remarks>
    /// Platform reference data, so no workspace filter applies and none is wanted — a cuisine is shared
    /// (tenancy.md). Checked because both ids are client-supplied into <c>Restrict</c> foreign keys: without this
    /// the write reaches <c>SaveChanges</c> and throws <c>DbUpdateException</c>, which no caller catches, so a
    /// creator naming a stale id gets a 500 instead of a named field.
    /// </remarks>
    Task<bool> CuisineExistsAsync(Guid cuisineId, CancellationToken cancellationToken);

    /// <inheritdoc cref="CuisineExistsAsync"/>
    Task<bool> CourseExistsAsync(Guid courseId, CancellationToken cancellationToken);


    /// <summary>The next sort order for a recipe's asset links, so a new link lands at the end.</summary>
    Task<int> NextRecipeLinkOrderAsync(Guid recipeId, CancellationToken cancellationToken);

    void Add(MediaAsset asset);

    void Add(MediaAssetVersion version);

    void Add(MediaAssetTag tag);

    /// <summary>Drops one tag from an asset, for the replace half of a metadata patch.</summary>
    void Remove(MediaAssetTag tag);


    /// <summary>
    /// The number a new version of a live asset would take, or null when there is no such asset to add one to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>MAX(VersionNumber) + 1</c>, deliberately, rather than the asset's own counter + 1.</strong> The two
    /// agree today because a version insert and the counter bump commit together — but <c>MAX</c> cannot allocate a
    /// number that already exists even if they ever drifted, and "concurrent uploads cannot share a version number"
    /// is the property this read exists to support. The counter is what "current" means; this is what "next" means,
    /// and they are different questions.
    /// </para>
    /// <para>
    /// <strong>Not a reservation.</strong> Two callers reading at the same moment get the same number, and that is
    /// expected — the store's create-only write and the unique <c>(MediaAssetId, VersionNumber)</c> index are what
    /// make only one of them able to use it. A read cannot promise exclusivity and this one does not pretend to.
    /// </para>
    /// <para>
    /// Null for an unknown id, another workspace's asset and a tombstone alike: a removed asset does not gain
    /// versions.
    /// </para>
    /// </remarks>
    Task<int?> NextVersionNumberAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>Stages one utilization record (DAM-009).</summary>
    void Add(MediaAssetUtilization utilization);

    void Add(RecipeAssetLink link);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Begins a transaction on this scope's context, or null when one is already open.</summary>
    /// <remarks>
    /// Null rather than a nested transaction, because the caller must not commit one it did not begin —
    /// an ambient transaction belongs to whoever opened it, which for a request carrying an
    /// <c>Idempotency-Key</c> is the idempotent executor.
    /// </remarks>
    Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetRepository"/>
internal sealed class MediaAssetRepository(CreatorPantryDbContext context) : IMediaAssetRepository
{
    public Task<bool> ExistsAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.MediaAssets
            .AsNoTracking()
            .AnyAsync(asset => asset.Id == mediaAssetId && asset.DeletedAt == null, cancellationToken);

    public Task<MediaAsset?> FindAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.MediaAssets
            .Include(asset => asset.Versions)
            .FirstOrDefaultAsync(asset => asset.Id == mediaAssetId, cancellationToken);

    public Task<MediaAsset?> FindLiveForUpdateAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.MediaAssets
            .Include(asset => asset.Tags)
            .FirstOrDefaultAsync(
                asset => asset.Id == mediaAssetId && asset.DeletedAt == null, cancellationToken);

    public Task<MediaAsset?> FindForDeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.MediaAssets.FirstOrDefaultAsync(asset => asset.Id == mediaAssetId, cancellationToken);

    public Task<MediaAssetVersionObjectRecord?> FindCurrentVersionObjectAsync(
        Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.MediaAssets
            .AsNoTracking()

            // No workspace predicate: the global query filter is the scope, and a tombstone is excluded here so a
            // deleted asset is "nothing to render" rather than a second check somebody could forget.
            .Where(asset => asset.Id == mediaAssetId && asset.DeletedAt == null)

            // A join on (asset, current version number) rather than a correlated subquery. The subquery form reads
            // better but needs the asset's own columns inside it to name the title, and referencing an outer column
            // from a nested projection compiles to SQL APPLY — which SQLite does not support, so every render
            // answered 500 until this became a join. The join translates on both engines.
            .Join(
                context.MediaAssetVersions,
                asset => new { AssetId = asset.Id, Version = asset.CurrentVersionNumber },
                version => new { AssetId = version.MediaAssetId, Version = version.VersionNumber },
                (asset, version) => new MediaAssetVersionObjectRecord(
                    version.VersionNumber,
                    version.ObjectKey,
                    version.MediaType,
                    version.SizeBytes,
                    version.ContentChecksum,
                    version.OriginalFileName,
                    asset.Title))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<MediaAssetVersionObjectRecord?> FindVersionObjectAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
        context.MediaAssets
            .AsNoTracking()
            .Where(asset => asset.Id == mediaAssetId && asset.DeletedAt == null)

            // The number the caller asked for rather than the asset's counter, and joined on both identifiers so a
            // version of some other asset cannot match. Otherwise the same shape as the current-version read.
            .Join(
                context.MediaAssetVersions.Where(version => version.VersionNumber == versionNumber),
                asset => asset.Id,
                version => version.MediaAssetId,
                (asset, version) => new MediaAssetVersionObjectRecord(
                    version.VersionNumber,
                    version.ObjectKey,
                    version.MediaType,
                    version.SizeBytes,
                    version.ContentChecksum,
                    version.OriginalFileName,
                    asset.Title))
            .FirstOrDefaultAsync(cancellationToken);



    public async Task<(IReadOnlyList<Guid> RecipeIds, int BrandProfileCount, int TestAttachmentCount)>
        FindReferencesAsync(Guid mediaAssetId, CancellationToken cancellationToken)
    {
        // Distinct, because an asset linked to one recipe twice — a hero and a gallery slot — is one recipe to go
        // and fix.
        var recipeIds = await context.RecipeAssetLinks
            .AsNoTracking()
            .Where(link => link.MediaAssetId == mediaAssetId)
            .Select(link => link.RecipeId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var brandProfiles = await context.BrandAssetLinks
            .AsNoTracking()
            .Where(link => link.MediaAssetId == mediaAssetId)
            .Select(link => link.BrandProfileId)
            .Distinct()
            .CountAsync(cancellationToken);

        // Attachments rather than test runs: a run may attach the same asset to several issues, and each one is a
        // place the image appears.
        var attachments = await context.TestAttachmentLinks
            .AsNoTracking()
            .CountAsync(link => link.MediaAssetId == mediaAssetId, cancellationToken);

        return (recipeIds, brandProfiles, attachments);
    }


    public async Task<MediaAsset?> FindByGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        var assetId = await context.MediaAssetVersions
            .AsNoTracking()
            .Where(version => version.SourceGeneratedImageId == generatedImageId)
            .Select(version => (Guid?)version.MediaAssetId)
            .FirstOrDefaultAsync(cancellationToken);

        return assetId is null ? null : await FindAsync(assetId.Value, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindTagIdsAsync(
        IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        tagIds.Count == 0
            ? []
            : await context.WorkspaceTags
                .AsNoTracking()
                .Where(tag => tagIds.Contains(tag.Id))
                .Select(tag => tag.Id)
                .ToListAsync(cancellationToken);

    public Task<bool> RecipeExistsAsync(Guid recipeId, CancellationToken cancellationToken) =>
        context.Recipes.AsNoTracking().AnyAsync(recipe => recipe.Id == recipeId, cancellationToken);

    public Task<bool> CuisineExistsAsync(Guid cuisineId, CancellationToken cancellationToken) =>
        context.Cuisines.AsNoTracking().AnyAsync(cuisine => cuisine.Id == cuisineId, cancellationToken);

    public Task<bool> CourseExistsAsync(Guid courseId, CancellationToken cancellationToken) =>
        context.Courses.AsNoTracking().AnyAsync(course => course.Id == courseId, cancellationToken);


    public async Task<int> NextRecipeLinkOrderAsync(Guid recipeId, CancellationToken cancellationToken) =>
        await context.RecipeAssetLinks
            .AsNoTracking()
            .Where(link => link.RecipeId == recipeId)
            .Select(link => (int?)link.SortOrder)
            .MaxAsync(cancellationToken) + 1 ?? 0;

    public void Add(MediaAsset asset) => context.MediaAssets.Add(asset);

    public void Add(MediaAssetVersion version) => context.MediaAssetVersions.Add(version);

    public void Add(MediaAssetTag tag) => context.MediaAssetTags.Add(tag);

    public void Remove(MediaAssetTag tag) => context.MediaAssetTags.Remove(tag);

    public void Add(MediaAssetUtilization utilization) => context.MediaAssetUtilizations.Add(utilization);

    public async Task<int?> NextVersionNumberAsync(Guid mediaAssetId, CancellationToken cancellationToken)
    {
        if (!await context.MediaAssets
            .AsNoTracking()
            .AnyAsync(asset => asset.Id == mediaAssetId && asset.DeletedAt == null, cancellationToken))
        {
            return null;
        }

        // Max over an empty set would throw on a non-nullable int, so the cast is what lets an asset with no
        // versions answer 1 rather than blow up — reachable, because 12.9c models an asset whose version row is
        // missing.
        var highest = await context.MediaAssetVersions
            .AsNoTracking()
            .Where(version => version.MediaAssetId == mediaAssetId)
            .MaxAsync(version => (int?)version.VersionNumber, cancellationToken);

        return (highest ?? 0) + 1;
    }


    public void Add(RecipeAssetLink link) => context.RecipeAssetLinks.Add(link);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);

    public async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken) =>
        context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
}
