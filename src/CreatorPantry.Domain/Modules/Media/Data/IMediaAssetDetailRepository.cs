using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>EF Core access to one asset's detail and its histories (DAM-003), within the resolved workspace.</summary>
public interface IMediaAssetDetailRepository
{
    /// <summary>
    /// Everything one detail read needs, or null when this workspace has no asset with that id.
    /// </summary>
    /// <param name="includeDeleted">
    /// Whether a soft-deleted asset counts as found. False is the ordinary read; true is the explicit request a
    /// creator makes to see their own tombstone.
    /// </param>
    Task<MediaAssetDetailBundle?> FindAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken);


    /// <summary>
    /// Whether this workspace has an asset with that id, without reading anything about it.
    /// </summary>
    /// <remarks>
    /// Exists so the utilization history can check visibility for the price of one <c>EXISTS</c> rather than the
    /// four statements <see cref="FindAsync"/> runs — a history page would otherwise pay for the asset's versions,
    /// tags and links to learn one boolean. False covers an unknown id, another workspace's asset, and a tombstone
    /// when <paramref name="includeDeleted"/> is false, so the caller answers 404 to all three alike.
    /// </remarks>
    Task<bool> IsVisibleAsync(Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken);

    /// <summary>One page of one asset's utilization history, newest first.</summary>
    Task<(IReadOnlyList<MediaAssetUtilizationRecord> Rows, bool HasMore, int? Total)> ListUtilizationAsync(
        MediaAssetUtilizationCriteria criteria, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaAssetDetailRepository"/>
/// <remarks>
/// <para>
/// <strong>The workspace is the global query filter, never a predicate here.</strong> An unknown id and another
/// workspace's asset both arrive as null, which is what lets the layer above answer 404 to both without
/// disclosing which it was (tenancy.md).
/// </para>
/// <para>
/// <strong>Projections throughout, so no object key is ever loaded.</strong> Nothing here materialises a
/// <c>MediaAssetVersion</c>; the version rows select nine columns and <c>ObjectKey</c> is not among them. That is
/// stronger than returning the entity and dropping the field, because a later edit cannot reintroduce it by
/// accident (media.md).
/// </para>
/// <para>
/// <strong>Four statements, not one.</strong> The root with its counts, then versions, tags and recipe links.
/// They are separate reads rather than one <c>Include</c> graph because a single query over three collections is
/// a cartesian product — three versions, four tags and two links would be twenty-four rows carrying the root's
/// columns twenty-four times. <c>AsSplitQuery</c> would do the same thing less explicitly and would still
/// materialise entities.
/// </para>
/// </remarks>
internal sealed class MediaAssetDetailRepository(CreatorPantryDbContext context) : IMediaAssetDetailRepository
{
    public async Task<MediaAssetDetailBundle?> FindAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var asset = await context.MediaAssets
            .AsNoTracking()
            .Where(candidate => candidate.Id == mediaAssetId)

            // The tombstone test is a predicate rather than a second query so that "deleted" and "not there"
            // stay one answer. MediaAsset has no global filter for DeletedAt, deliberately — a global one would
            // put this read out of reach of the flag that is the whole point of it.
            .Where(candidate => includeDeleted || candidate.DeletedAt == null)
            .Select(candidate => new MediaAssetDetailRecord(
                candidate.Id,
                candidate.Title,
                candidate.Description,
                candidate.AltText,
                candidate.Kind,
                candidate.ChannelKey,
                candidate.PlatformKey,
                candidate.Day,
                candidate.StyleKey,
                candidate.CuisineId,
                candidate.CourseId,
                candidate.RightsHolder,
                candidate.AttributionText,
                candidate.CurrentVersionNumber,

                // Counted in the same statement: three facts a panel shows, none worth a round trip. Each
                // correlated subquery runs inside the filtered set, so none can count a neighbour's rows.
                context.MediaAssetVersions.Count(version => version.MediaAssetId == candidate.Id),
                context.MediaAssetUtilizations.Count(use => use.MediaAssetId == candidate.Id),
                context.RecipeAssetLinks.Count(link => link.MediaAssetId == candidate.Id),

                // The two link kinds a removal leaves standing besides recipes (12.10h), counted exactly as
                // FindReferencesAsync counts them for the removal's own report — distinct profiles, and every
                // attachment — so what a creator is warned of beforehand is what they are told afterwards.
                context.BrandAssetLinks
                    .Where(link => link.MediaAssetId == candidate.Id)
                    .Select(link => link.BrandProfileId)
                    .Distinct()
                    .Count(),
                context.TestAttachmentLinks.Count(link => link.MediaAssetId == candidate.Id),
                candidate.DeletedAt,
                candidate.DeletedByMembershipId,
                candidate.CreatedAt,
                candidate.UpdatedAt,
                candidate.RowVersion))
            .FirstOrDefaultAsync(cancellationToken);

        if (asset is null)
        {
            return null;
        }

        var versions = await context.MediaAssetVersions
            .AsNoTracking()
            .Where(version => version.MediaAssetId == mediaAssetId)

            // Newest first, matching every other history here. The version number is unique per asset, so it is
            // a total order on its own and needs no tie-break.
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new MediaAssetVersionRecord(
                version.VersionNumber,
                version.MediaType,
                version.Width,
                version.Height,
                version.SizeBytes,
                version.ContentChecksum,
                version.OriginalFileName,
                version.Source,
                version.SourceGeneratedImageId,
                version.CreatedAt))
            .ToListAsync(cancellationToken);

        // Joined to the workspace's own tag vocabulary so the name comes back with the id. WorkspaceTag is the
        // Recipes module's entity and a foreign key already runs to it from MediaAssetTag, which is what lets it
        // be named here (backend.md).
        var tags = await context.MediaAssetTags
            .AsNoTracking()
            .Where(tag => tag.MediaAssetId == mediaAssetId)
            .Join(
                context.WorkspaceTags,
                tag => tag.WorkspaceTagId,
                vocabulary => vocabulary.Id,

                // The vocabulary row, so the ordering below is on a column. Ordering on a constructed record
                // instead does not translate — EF cannot see through the constructor to the property.
                (tag, vocabulary) => vocabulary)
            .OrderBy(vocabulary => vocabulary.Name)
            .Select(vocabulary => new MediaAssetTagRecord(vocabulary.Id, vocabulary.Name))
            .ToListAsync(cancellationToken);

        var recipeLinks = await context.RecipeAssetLinks
            .AsNoTracking()
            .Where(link => link.MediaAssetId == mediaAssetId)
            .OrderBy(link => link.SortOrder)
            .Select(link => new MediaAssetRecipeLinkRecord(link.RecipeId, link.Role, link.Caption))
            .ToListAsync(cancellationToken);

        return new MediaAssetDetailBundle(asset, versions, tags, recipeLinks);
    }


    public Task<bool> IsVisibleAsync(
        Guid mediaAssetId, bool includeDeleted, CancellationToken cancellationToken) =>
        context.MediaAssets
            .AsNoTracking()
            .Where(candidate => candidate.Id == mediaAssetId)
            .Where(candidate => includeDeleted || candidate.DeletedAt == null)
            .AnyAsync(cancellationToken);

    public async Task<(IReadOnlyList<MediaAssetUtilizationRecord> Rows, bool HasMore, int? Total)>
        ListUtilizationAsync(MediaAssetUtilizationCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var uses = context.MediaAssetUtilizations
            .AsNoTracking()
            .Where(use => use.MediaAssetId == criteria.MediaAssetId);

        // Before the position is applied, so it counts the whole history rather than what is left after the
        // cursor — which is what a panel means by "used 847 times".
        var total = criteria.IncludeTotal
            ? await uses.CountAsync(cancellationToken)
            : (int?)null;

        if (criteria.Position is { } position)
        {
            // Descending, so "after this row" means an earlier day, or the same day with a smaller id. The id is
            // in both the WHERE and the ORDER BY because three uses on one day share a sort value.
            uses = uses.Where(use =>
                use.UtilizedOn < position.UtilizedOn
                || (use.UtilizedOn == position.UtilizedOn && use.Id < position.UtilizationId));
        }

        var rows = await uses
            .OrderByDescending(use => use.UtilizedOn)
            .ThenByDescending(use => use.Id)

            // One more than asked for, so "is there another page" is answered by the read.
            .Take(criteria.Limit + 1)
            .Select(use => new MediaAssetUtilizationRecord(
                use.Id,
                use.PlatformKey,
                use.UtilizedOn,
                use.UtilizedDay,
                use.CampaignName,
                use.Notes,
                use.CreatedAt))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > criteria.Limit;

        return (hasMore ? rows[..criteria.Limit] : rows, hasMore, total);
    }
}
