using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>EF Core access to the library's search (DAM-002), within the resolved workspace.</summary>
public interface IMediaAssetSearchRepository
{
    /// <summary>One page of the filtered library, and the total when the caller asked for it.</summary>
    Task<(IReadOnlyList<MediaAssetSearchRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        MediaAssetSearchCriteria criteria, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetSearchRepository"/>
/// <remarks>
/// <para>
/// <strong>The workspace is the global query filter, never a predicate here.</strong> Two filtered indexes do
/// the seeking — <c>IX_MediaAssets_Workspace_CreatedAt</c> for the default ordering and
/// <c>IX_MediaAssets_Workspace_Title</c> for the alphabetical one — and both exclude soft-deleted rows in the
/// index itself, so a tombstone never enters the seek rather than being filtered out of it.
/// </para>
/// <para>
/// <strong>Everything else is a residual predicate, deliberately.</strong> Channel, platform, day, style,
/// cuisine, course, the date bounds and the free-text match are evaluated after one of those seeks has
/// narrowed the read to one workspace, exactly as <c>RecipeConfiguration</c> argues at length: a creator's
/// library is thousands of rows, not millions, and an index per filter combination would be paid for on every
/// edit to save nothing measurable on a read.
/// </para>
/// <para>
/// <strong>Tags and the recipe link are semi-joins, not joins.</strong> An asset carrying two of the
/// requested tags must appear once, and a join would duplicate it; <c>IX_MediaAssetTags_Workspace_Tag_Asset</c>
/// and <c>IX_RecipeAssetLinks_Workspace_MediaAsset</c> serve them.
/// </para>
/// </remarks>
internal sealed class MediaAssetSearchRepository(CreatorPantryDbContext context) : IMediaAssetSearchRepository
{
    public async Task<(IReadOnlyList<MediaAssetSearchRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        MediaAssetSearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var filtered = Filtered(criteria.Filters);

        // Before the position is applied, so it counts the whole filtered set rather than what is left after
        // the cursor — which is what a library screen means by "of how many".
        var total = criteria.IncludeTotal
            ? await filtered.CountAsync(cancellationToken)
            : (int?)null;

        var page = Ordered(filtered, criteria)

            // One more than asked for, so "is there another page" is answered by the read rather than by
            // comparing a count against a page size the caller may not have chosen.
            .Take(criteria.Limit + 1);

        var rows = await Project(page, criteria.Sort).ToListAsync(cancellationToken);
        var hasMore = rows.Count > criteria.Limit;

        return (hasMore ? rows[..criteria.Limit] : rows, hasMore, total);
    }

    private IQueryable<MediaAsset> Filtered(MediaAssetSearchFilters filters)
    {
        // No WorkspaceId predicate: the global query filter is the scope, and the index leads with it.
        // DeletedAt is stated because the filtered indexes are built on it — not as a second guard.
        var assets = context.MediaAssets.AsNoTracking().Where(asset => asset.DeletedAt == null);

        if (filters.Search is { } term)
        {
            // Lowered on both sides rather than relying on the collation, matching the recipe library: SQL
            // Server's default is case-insensitive and SQLite's is not, and a search should mean the same
            // thing in a test as in production. Forfeits a seek, which a substring match never had.
            assets = assets.Where(asset =>
                asset.Title.ToLower().Contains(term)
                || (asset.Description != null && asset.Description.ToLower().Contains(term)));
        }

        if (filters.ChannelKey is { } channel)
        {
            assets = assets.Where(asset => asset.ChannelKey == channel);
        }

        if (filters.PlatformKey is { } platform)
        {
            assets = assets.Where(asset => asset.PlatformKey == platform);
        }

        if (filters.Day is { } day)
        {
            assets = assets.Where(asset => asset.Day == day);
        }

        if (filters.StyleKey is { } style)
        {
            assets = assets.Where(asset => asset.StyleKey == style);
        }

        if (filters.CuisineIds is { Count: > 0 } cuisines)
        {
            assets = assets.Where(asset => asset.CuisineId != null && cuisines.Contains(asset.CuisineId.Value));
        }

        if (filters.CourseIds is { Count: > 0 } courses)
        {
            assets = assets.Where(asset => asset.CourseId != null && courses.Contains(asset.CourseId.Value));
        }

        if (filters.TagIds is { Count: > 0 } tags)
        {
            // Any of them, matching RecipeSearch and the brand source list. A semi-join, so an asset
            // carrying two of the requested tags appears once.
            assets = assets.Where(asset => context.MediaAssetTags.Any(tag =>
                tag.MediaAssetId == asset.Id && tags.Contains(tag.WorkspaceTagId)));
        }

        if (filters.RecipeId is { } recipeId)
        {
            assets = assets.Where(asset => context.RecipeAssetLinks.Any(link =>
                link.MediaAssetId == asset.Id && link.RecipeId == recipeId));
        }

        if (filters.CreatedOnOrAfter is { } from)
        {
            assets = assets.Where(asset => asset.CreatedAt >= from);
        }

        if (filters.CreatedBefore is { } before)
        {
            // Half-open, so adjacent ranges neither overlap nor leave a gap.
            assets = assets.Where(asset => asset.CreatedAt < before);
        }

        return assets;
    }

    /// <summary>
    /// Applies the ordering and the keyset position together, because they have to agree.
    /// </summary>
    /// <remarks>
    /// The id is part of both the <c>ORDER BY</c> and the <c>WHERE</c>, so the page is a total order: two
    /// assets created in the same tick, or sharing a title, cannot repeat or vanish between pages. Titles are
    /// deliberately not unique — two of a creator's photographs may share one — which is exactly why the
    /// tie-break has to be in the key rather than assumed away.
    /// </remarks>
    private static IQueryable<MediaAsset> Ordered(
        IQueryable<MediaAsset> assets, MediaAssetSearchCriteria criteria)
    {
        if (criteria.Sort is MediaAssetSearchSort.Title)
        {
            if (criteria.Position is { } titlePosition)
            {
                assets = assets.Where(asset =>
                    string.Compare(asset.Title, titlePosition.Title) > 0
                    || (asset.Title == titlePosition.Title && asset.Id > titlePosition.MediaAssetId));
            }

            return assets.OrderBy(asset => asset.Title).ThenBy(asset => asset.Id);
        }

        if (criteria.Position is { } position)
        {
            // Descending, so "after this row" means strictly older, or the same instant with a smaller id.
            assets = assets.Where(asset =>
                asset.CreatedAt < position.CreatedAt
                || (asset.CreatedAt == position.CreatedAt && asset.Id < position.MediaAssetId));
        }

        return assets.OrderByDescending(asset => asset.CreatedAt).ThenByDescending(asset => asset.Id);
    }

    /// <summary>
    /// Projects the row entirely in SQL, including the current version's media facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The version comes from a correlated read on <c>(MediaAssetId, VersionNumber)</c>, which
    /// <c>IX_MediaAssetVersions_Workspace_Asset_VersionNumber</c> exists for. A grid needs dimensions to lay
    /// out, and the alternative is a client fetching every asset to find out.
    /// </para>
    /// <para>
    /// <strong>No bytes, no object key, no URL</strong> — a search must not read the objects of every asset
    /// it lists, and nothing here is or becomes an address (media.md).
    /// </para>
    /// <para>
    /// The media facts are nullable because the projection is a left join in all but name: an asset whose
    /// current version is somehow missing lists with its metadata rather than disappearing from the library,
    /// which is the more useful failure.
    /// </para>
    /// </remarks>
    private IQueryable<MediaAssetSearchRecord> Project(
        IQueryable<MediaAsset> assets, MediaAssetSearchSort sort) =>
        assets.Select(asset => new
        {
            Asset = asset,
            Version = context.MediaAssetVersions
                .Where(version => version.MediaAssetId == asset.Id
                    && version.VersionNumber == asset.CurrentVersionNumber)
                .Select(version => new { version.MediaType, version.Width, version.Height, version.SizeBytes })
                .FirstOrDefault(),
        })
        .Select(row => new MediaAssetSearchRecord(
            row.Asset.Id,
            row.Asset.Title,
            row.Asset.Description,
            row.Asset.Kind,
            row.Asset.AltText,
            row.Asset.ChannelKey,
            row.Asset.PlatformKey,
            row.Asset.Day,
            row.Asset.StyleKey,
            row.Asset.CuisineId,
            row.Asset.CourseId,
            row.Asset.CurrentVersionNumber,
            row.Version == null ? null : row.Version.MediaType,
            row.Version == null ? 0 : row.Version.Width,
            row.Version == null ? 0 : row.Version.Height,
            row.Version == null ? 0L : row.Version.SizeBytes,
            row.Asset.CreatedAt,
            row.Asset.UpdatedAt,

            // Carried rather than formatted, because the row computes its own cursor halves from it. That
            // keeps everything above translatable: a format string in a Select is not SQL, and a projection
            // EF could only evaluate on the client would read every column to produce two strings.
            sort));
}
