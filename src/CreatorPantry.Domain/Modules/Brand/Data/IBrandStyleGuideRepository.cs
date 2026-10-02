using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>A source document version a guide may cite: where it is, and that its document is not removed.</summary>
internal sealed record BrandStyleGuideSourceCandidate(Guid DocumentId, int VersionNumber, Guid VersionId);

/// <summary>A cited source version, named by its document and number.</summary>
internal sealed record BrandStyleGuideCitation(Guid GuideVersionId, Guid DocumentId, int VersionNumber);

/// <summary>The workspace default as it applies to one guide: which of its versions, and what was said.</summary>
internal sealed record BrandStyleGuideActiveRecord(
    Guid VersionId, DateTimeOffset ActivatedAt, string? ActivationReason);


internal interface IBrandStyleGuideRepository
{
    Task<BrandStyleGuide?> FindGuideAsync(Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the resolved workspace has this guide. The query filter supplies the workspace, so another
    /// workspace's guide is simply absent — which is the same answer as one that never existed.
    /// </summary>
    Task<bool> GuideExistsAsync(Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of a guide's version history, newest version first, projected in SQL: no entity is
    /// materialised and <strong>no section, rule or body is read</strong>.
    /// </summary>
    Task<(IReadOnlyList<BrandStyleGuideVersionSummaryRecord> Rows, bool HasMore)> ListVersionsAsync(
        BrandStyleGuideVersionListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// How many of each version's citations name a source document version the document has since superseded.
    /// </summary>
    /// <remarks>
    /// A version with no stale citation is absent from the result rather than present as zero, so the caller
    /// supplies the zero. Only superseded sources count: a cited document the workspace has archived or
    /// removed is still exactly the text the guide was written from, and conflating the two would hand a
    /// client one number it could not take apart.
    /// </remarks>
    /// <param name="guideVersionIds">
    /// Not constrained to one guide, deliberately — the caller passes the ids of the page it has just read.
    /// What keeps that safe is the query filter rather than the argument: an id from another workspace
    /// matches nothing, which <c>BrandGuideSqlServerTests</c> asserts against a real server.
    /// </param>
    Task<IReadOnlyDictionary<Guid, int>> StaleSourceCountsAsync(
        IReadOnlyCollection<Guid> guideVersionIds, CancellationToken cancellationToken);

    /// <summary>The guide's highest-numbered version with its sections and rules.</summary>
    Task<BrandStyleGuideVersion?> FindWorkingVersionAsync(Guid guideId, CancellationToken cancellationToken);

    Task<BrandStyleGuideVersion?> FindVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>
    /// The named versions of one guide, with their sections and rules.
    /// </summary>
    /// <remarks>
    /// Resolved by number within the guide rather than by id, which is what makes another guide's version
    /// unreachable here rather than merely refused: the predicate names the route's guide. A number the guide
    /// does not have is simply absent from the result, and the caller decides what that means.
    /// </remarks>
    Task<IReadOnlyList<BrandStyleGuideVersion>> FindVersionsByNumberAsync(
        Guid guideId, IReadOnlyCollection<int> versionNumbers, CancellationToken cancellationToken);

    /// <summary>The workspace default, only if its version belongs to <paramref name="guideId"/>.</summary>
    Task<BrandStyleGuideActiveRecord?> FindActiveAsync(Guid guideId, CancellationToken cancellationToken);

    Task<BrandStyleGuideApproval?> FindApprovalAsync(Guid versionId, CancellationToken cancellationToken);

    Task<int?> FindVersionNumberAsync(Guid versionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrandStyleGuideCitation>> FindCitationsAsync(
        IReadOnlyCollection<Guid> guideVersionIds, CancellationToken cancellationToken);

    /// <summary>
    /// The resolved workspace's default guide version, with the guide and number it belongs to, or null when
    /// the workspace has none.
    /// </summary>
    /// <remarks>
    /// No workspace argument: <c>WorkspaceId</c> is the table's whole primary key and the global query filter
    /// supplies it, so this cannot read another workspace's decision and there is nothing here to pass wrong.
    /// </remarks>
    Task<BrandStyleGuideWorkspaceDefault?> FindWorkspaceDefaultAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The resolved workspace's default row, tracked, for an activation that is about to repoint it.
    /// </summary>
    /// <remarks>
    /// Tracked where every other read here is <c>AsNoTracking</c>, because this one is read to be written: the
    /// entity's <c>RowVersion</c> becomes the <c>WHERE</c> clause of the update, which is what makes two
    /// simultaneous activations resolve to one winner rather than to whichever wrote last.
    /// </remarks>
    Task<BrandStyleGuideDefault?> TrackWorkspaceDefaultAsync(CancellationToken cancellationToken);

    /// <summary>Stages the workspace's first default. Nothing is saved.</summary>
    void AddDefault(BrandStyleGuideDefault workspaceDefault);

    /// <summary>Stages a new guide with its first version. Nothing is saved.</summary>
    void Add(BrandStyleGuide guide, BrandStyleGuideVersion version);

    /// <summary>
    /// Stages one further version of an existing guide, with its sections, rules and source links. Nothing is
    /// saved.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Add"/> rather than an overload taking a null guide: that one creates a guide and
    /// this one does not touch the guide row at all, and a single method with an optional parent would be one
    /// <c>if</c> away from inserting a second guide or from leaving a version parentless.
    /// </remarks>
    void AddVersion(BrandStyleGuideVersion version);

    /// <summary>
    /// The versions of the named documents that exist in the resolved workspace and are not removed. The query
    /// filter supplies the workspace, so another workspace's document is simply absent.
    /// </summary>
    Task<IReadOnlyList<BrandStyleGuideSourceCandidate>> FindSourceVersionsAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideRepository(CreatorPantryDbContext context) : IBrandStyleGuideRepository
{
    public Task<BrandStyleGuide?> FindGuideAsync(Guid guideId, CancellationToken cancellationToken) =>
        context.BrandStyleGuides.AsNoTracking().FirstOrDefaultAsync(guide => guide.Id == guideId, cancellationToken);

    public Task<bool> GuideExistsAsync(Guid guideId, CancellationToken cancellationToken) =>
        context.BrandStyleGuides.AsNoTracking().AnyAsync(guide => guide.Id == guideId, cancellationToken);

    public async Task<(IReadOnlyList<BrandStyleGuideVersionSummaryRecord> Rows, bool HasMore)> ListVersionsAsync(
        BrandStyleGuideVersionListCriteria criteria, CancellationToken cancellationToken)
    {
        var versions = context.BrandStyleGuideVersions
            .AsNoTracking()
            .Where(version => version.BrandStyleGuideId == criteria.GuideId);

        if (criteria.Position is { } position)
        {
            // Strict, and on one column: a version number is unique within its guide, so resuming after one
            // can neither skip a sibling nor return it twice.
            versions = versions.Where(version => version.VersionNumber < position.VersionNumber);
        }

        // The page is cut from the versions alone, so the limit bounds the work before anything is counted.
        var page = versions
            .OrderByDescending(version => version.VersionNumber)

            // One row past the limit: how the page learns whether another follows, without a count.
            .Take(criteria.Limit + 1);

        var fetched = await page
            // Stated again over the rows that survived the cut: the outer query of a Take has no order of
            // its own.
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new BrandStyleGuideVersionSummaryRecord(
                version.Id,
                version.VersionNumber,
                version.ChangeReason,
                version.CreatedAt,
                version.CreatedByMembershipId,

                // Approval is a row beside the version rather than a column on it, so "approved" is its
                // existence. Three index seeks per row rather than three joins over the whole history.
                context.BrandStyleGuideApprovals.Any(approval => approval.BrandStyleGuideVersionId == version.Id),
                context.BrandStyleGuideSourceLinks.Count(link => link.BrandStyleGuideVersionId == version.Id),

                // The workspace default is the one stored activation decision; at most one row can match.
                context.BrandStyleGuideDefaults.Any(active => active.BrandStyleGuideVersionId == version.Id)))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> StaleSourceCountsAsync(
        IReadOnlyCollection<Guid> guideVersionIds, CancellationToken cancellationToken)
    {
        if (guideVersionIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        // A citation pins an exact source version, so it is stale when its document has moved past it. Every
        // set here is workspace-owned, so the global filter scopes all three and no workspace is named.
        var counts = await (from link in context.BrandStyleGuideSourceLinks.AsNoTracking()
                            join sourceVersion in context.BrandSourceDocumentVersions.AsNoTracking()
                                on link.BrandSourceDocumentVersionId equals sourceVersion.Id
                            join document in context.BrandSourceDocuments.AsNoTracking()
                                on sourceVersion.BrandSourceDocumentId equals document.Id
                            where guideVersionIds.Contains(link.BrandStyleGuideVersionId)
                                && document.CurrentVersionNumber > sourceVersion.VersionNumber
                            group link by link.BrandStyleGuideVersionId into stale
                            select new { GuideVersionId = stale.Key, Count = stale.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(row => row.GuideVersionId, row => row.Count);
    }

    public Task<BrandStyleGuideVersion?> FindWorkingVersionAsync(Guid guideId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideVersions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(version => version.Sections)
            .Include(version => version.Rules)
            .Where(version => version.BrandStyleGuideId == guideId)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<BrandStyleGuideVersion?> FindVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideVersions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(version => version.Sections)
            .Include(version => version.Rules)
            .FirstOrDefaultAsync(version => version.Id == versionId, cancellationToken);

    public async Task<IReadOnlyList<BrandStyleGuideVersion>> FindVersionsByNumberAsync(
        Guid guideId, IReadOnlyCollection<int> versionNumbers, CancellationToken cancellationToken) =>
        await context.BrandStyleGuideVersions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(version => version.Sections)
            .Include(version => version.Rules)
            .Where(version => version.BrandStyleGuideId == guideId && versionNumbers.Contains(version.VersionNumber))
            .ToListAsync(cancellationToken);

    public Task<BrandStyleGuideActiveRecord?> FindActiveAsync(Guid guideId, CancellationToken cancellationToken) =>
        (from active in context.BrandStyleGuideDefaults.AsNoTracking()
         join version in context.BrandStyleGuideVersions.AsNoTracking()
             on active.BrandStyleGuideVersionId equals version.Id
         where version.BrandStyleGuideId == guideId
         select new BrandStyleGuideActiveRecord(version.Id, active.ActivatedAt, active.Reason))
        .FirstOrDefaultAsync(cancellationToken);

    public Task<BrandStyleGuideApproval?> FindApprovalAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideApprovals.AsNoTracking()
            .FirstOrDefaultAsync(approval => approval.BrandStyleGuideVersionId == versionId, cancellationToken);

    public Task<int?> FindVersionNumberAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideVersions.AsNoTracking()
            .Where(version => version.Id == versionId)
            .Select(version => (int?)version.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<BrandStyleGuideCitation>> FindCitationsAsync(
        IReadOnlyCollection<Guid> guideVersionIds, CancellationToken cancellationToken) =>
        await (from link in context.BrandStyleGuideSourceLinks.AsNoTracking()
               join version in context.BrandSourceDocumentVersions.AsNoTracking()
                   on link.BrandSourceDocumentVersionId equals version.Id
               where guideVersionIds.Contains(link.BrandStyleGuideVersionId)
               select new BrandStyleGuideCitation(link.BrandStyleGuideVersionId, version.BrandSourceDocumentId, version.VersionNumber))
            .ToListAsync(cancellationToken);

    public Task<BrandStyleGuideWorkspaceDefault?> FindWorkspaceDefaultAsync(CancellationToken cancellationToken) =>
        (from active in context.BrandStyleGuideDefaults.AsNoTracking()
         join version in context.BrandStyleGuideVersions.AsNoTracking()
             on active.BrandStyleGuideVersionId equals version.Id
         select new BrandStyleGuideWorkspaceDefault(
             version.BrandStyleGuideId,
             version.Id,
             version.VersionNumber,
             active.ActivatedAt,
             active.ActivatedByMembershipId,
             active.Reason))
        .FirstOrDefaultAsync(cancellationToken);

    // No predicate at all: the workspace is the key and the query filter supplies it, so at most one row is
    // visible here however many the table holds.
    public Task<BrandStyleGuideDefault?> TrackWorkspaceDefaultAsync(CancellationToken cancellationToken) =>
        context.BrandStyleGuideDefaults.FirstOrDefaultAsync(cancellationToken);

    public void AddDefault(BrandStyleGuideDefault workspaceDefault) =>
        context.BrandStyleGuideDefaults.Add(workspaceDefault);

    public void Add(BrandStyleGuide guide, BrandStyleGuideVersion version)
    {
        context.BrandStyleGuides.Add(guide);
        context.BrandStyleGuideVersions.Add(version);
    }

    public void AddVersion(BrandStyleGuideVersion version) =>
        context.BrandStyleGuideVersions.Add(version);

    public async Task<IReadOnlyList<BrandStyleGuideSourceCandidate>> FindSourceVersionsAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken) =>
        await (from version in context.BrandSourceDocumentVersions.AsNoTracking()
               join document in context.BrandSourceDocuments.AsNoTracking()
                   on version.BrandSourceDocumentId equals document.Id
               where documentIds.Contains(document.Id) && document.Status != BrandSourceDocumentStatus.Removed
               select new BrandStyleGuideSourceCandidate(document.Id, version.VersionNumber, version.Id))
            .ToListAsync(cancellationToken);
}
