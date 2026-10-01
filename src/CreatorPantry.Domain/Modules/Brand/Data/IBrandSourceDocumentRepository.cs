using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public interface IBrandSourceDocumentRepository
{
    /// <summary>Stages a new document with its versions and tag links. Nothing is saved.</summary>
    void Add(BrandSourceDocument document);

    /// <summary>Stages a new tag in the workspace's vocabulary. Nothing is saved.</summary>
    void Add(BrandSourceTag tag);

    /// <summary>
    /// The resolved workspace's tags with these normalized names, retired ones included. Takes no workspace
    /// id: the query filter scopes it, so another workspace's "launch" is not found.
    /// </summary>
    Task<IReadOnlyList<BrandSourceTag>> FindTagsAsync(IReadOnlyCollection<string> normalizedNames, CancellationToken cancellationToken);

    /// <summary>Whether a version row with this id is committed in the resolved workspace.</summary>
    Task<bool> VersionExistsAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the resolved workspace's library, newest edit first, projected in SQL: each document with
    /// its current version's metadata and its latest extraction's outcome. No entity is materialised and no
    /// object key, checksum or text is selected.
    /// </summary>
    Task<(IReadOnlyList<BrandSourceDocumentSummaryRecord> Rows, bool HasMore)> ListAsync(
        BrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>The tag names each of these documents carries, alphabetically. Documents with none are absent.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> TagNamesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);

    /// <summary>
    /// One document of the resolved workspace with its current version's metadata and that version's latest
    /// extraction outcome, or null when there is no such document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes no workspace id and no status: the query filter scopes it, so another workspace's document is not
    /// found rather than found and refused, and whether a removed one may be read is Business's to decide.
    /// </para>
    /// <para>
    /// Projected in SQL like <see cref="ListAsync"/>, so no entity is materialised and <strong>no object key
    /// is selected</strong> — a layer above cannot leak what it was never handed. The row version is selected
    /// because the token a change must quote is built from it.
    /// </para>
    /// </remarks>
    Task<BrandSourceDocumentDetailRecord?> FindDetailAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// What state one document of the resolved workspace is in, or null when there is no such document.
    /// </summary>
    /// <remarks>
    /// One column, for the callers that only need to know whether a document may be acted on at all. The
    /// download uses it rather than <see cref="FindDetailAsync"/>, which would project a version it does not
    /// want and then read a page of tag names nobody is going to show.
    /// </remarks>
    Task<BrandSourceDocumentStatus?> FindStatusAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Where one numbered version of one document keeps its bytes, or null when the workspace has no such
    /// document or the document has no such version.
    /// </summary>
    /// <remarks>
    /// The one read in this module that selects an <c>ObjectKey</c>, and it selects almost nothing else: the
    /// title for the download's filename, and the three facts the response states about the bytes. Both
    /// predicates are in the same query so that a version number belonging to another document, or to another
    /// workspace's document, is simply absent.
    /// </remarks>
    Task<BrandSourceVersionObjectRecord?> FindVersionObjectAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// One document of the resolved workspace, tracked, for a write that must change it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only tracked read in this module, shared by the replacement and the four lifecycle commands. Each
    /// has to change the root — a version counter, a status, an edit stamp — and it is that UPDATE carrying
    /// the row version that decides a race, so the entity must be the one EF will write back, not a
    /// projection of it.
    /// </para>
    /// <para>
    /// Takes no workspace id and no status: the query filter scopes it, and whether a removed or archived
    /// document may be written to is for Business to decide — the lifecycle commands and the replacement
    /// disagree about that, and only one of them can see a tombstone. Its versions are deliberately not
    /// included: a replacement appends one and reads the counter, and loading a document's whole history to
    /// add to it would grow with every replacement.
    /// </para>
    /// </remarks>
    Task<BrandSourceDocument?> FindForUpdateAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// The row version one document of the resolved workspace currently carries, or null when it is gone.
    /// </summary>
    /// <remarks>
    /// What a failed save asks to tell a lost race from a fault: if this differs from the value the write was
    /// composed against, the document moved on and the failure is a conflict, whichever guard caught it.
    /// </remarks>
    Task<byte[]?> CurrentRowVersionAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Everything in the resolved workspace that points at any version of one document, newest guide version
    /// first. Empty when nothing does.
    /// </summary>
    /// <remarks>
    /// Style-guide source links are the only holders that exist. Brand-source chunks will be able to hold a
    /// document too, and this is the query that will learn to union them in; a prompt context never will,
    /// because it is assembled per request and stored nowhere.
    /// </remarks>
    Task<IReadOnlyList<BrandSourceHoldRecord>> HoldsAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>How many versions one document of the resolved workspace has, and how many bytes they hold.</summary>
    Task<BrandSourceDocumentSizeRecord> SizeAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the resolved workspace's removed documents, newest removal first.
    /// </summary>
    /// <remarks>
    /// The only query in this module that looks for <see cref="BrandSourceDocumentStatus.Removed"/> rather
    /// than skipping it. It needs no <c>IgnoreQueryFilters</c>: a tombstone is an ordinary row of its own
    /// workspace, so the global filter scopes it exactly as it scopes a live one — it is the application that
    /// hides a removed document, not the database.
    /// </remarks>
    Task<(IReadOnlyList<RemovedBrandSourceDocumentRecord> Rows, bool HasMore)> ListRemovedAsync(
        RemovedBrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken);


}

internal sealed class BrandSourceDocumentRepository(CreatorPantryDbContext context) : IBrandSourceDocumentRepository
{
    public void Add(BrandSourceDocument document) => context.BrandSourceDocuments.Add(document);

    public void Add(BrandSourceTag tag) => context.BrandSourceTags.Add(tag);

    public async Task<IReadOnlyList<BrandSourceTag>> FindTagsAsync(
        IReadOnlyCollection<string> normalizedNames, CancellationToken cancellationToken) =>
        await context.BrandSourceTags
            .AsNoTracking()
            .Where(tag => normalizedNames.Contains(tag.NormalizedName))
            .ToListAsync(cancellationToken);

    public Task<bool> VersionExistsAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.BrandSourceDocumentVersions.AsNoTracking().AnyAsync(version => version.Id == versionId, cancellationToken);

    public async Task<(IReadOnlyList<BrandSourceDocumentSummaryRecord> Rows, bool HasMore)> ListAsync(
        BrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken)
    {
        // The page is cut from the documents alone, so the limit bounds the work before anything is joined.
        var page = Positioned(Filtered(criteria.Filters), criteria.Position)
            .OrderByDescending(document => document.UpdatedAt)
            .ThenByDescending(document => document.Id)

            // One row past the limit: how the page learns whether another follows, without a count.
            .Take(criteria.Limit + 1);

        var fetched = await page
            .Join(
                context.BrandSourceDocumentVersions.AsNoTracking(),
                document => new { DocumentId = document.Id, Number = document.CurrentVersionNumber },
                version => new { DocumentId = version.BrandSourceDocumentId, Number = version.VersionNumber },
                (document, version) => new { document, version })

            // A join promises no order, so it is stated again over the rows that survived the cut.
            .OrderByDescending(row => row.document.UpdatedAt)
            .ThenByDescending(row => row.document.Id)
            .Select(row => new BrandSourceDocumentSummaryRecord(
                row.document.Id,
                row.document.Title,
                row.document.DocumentType,
                row.document.Purpose,
                row.document.ChannelKey,
                row.document.Audience,
                row.document.Status,
                row.document.CreatedAt,
                row.document.UpdatedAt,
                row.version.Id,
                row.version.VersionNumber,
                row.version.MediaType,
                row.version.SizeBytes,
                row.version.OriginalFileName,
                row.version.CreatedAt,

                // The version's current extraction is its highest ordinal. Three reads of the same row, each
                // an index seek, rather than an intermediate shape re-mapped in memory.
                context.BrandSourceExtractions
                    .Where(extraction => extraction.BrandSourceDocumentVersionId == row.version.Id)
                    .OrderByDescending(extraction => extraction.Ordinal)
                    .Select(extraction => (BrandSourceExtractionStatus?)extraction.Status)
                    .FirstOrDefault(),
                context.BrandSourceExtractions
                    .Where(extraction => extraction.BrandSourceDocumentVersionId == row.version.Id)
                    .OrderByDescending(extraction => extraction.Ordinal)
                    .Select(extraction => (BrandSourceExtractionOrigin?)extraction.Origin)
                    .FirstOrDefault(),
                context.BrandSourceExtractions
                    .Where(extraction => extraction.BrandSourceDocumentVersionId == row.version.Id)
                    .OrderByDescending(extraction => extraction.Ordinal)
                    .Select(extraction => (DateTimeOffset?)extraction.CreatedAt)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> TagNamesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var links = await context.BrandSourceDocumentTags
            .AsNoTracking()
            .Where(link => documentIds.Contains(link.BrandSourceDocumentId))
            .Join(
                context.BrandSourceTags.AsNoTracking(),
                link => link.BrandSourceTagId,
                tag => tag.Id,
                (link, tag) => new { link.BrandSourceDocumentId, tag.Name })
            .OrderBy(row => row.Name)
            .ToListAsync(cancellationToken);

        return links
            .GroupBy(row => row.BrandSourceDocumentId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(row => row.Name)]);
    }

    public Task<BrandSourceDocumentDetailRecord?> FindDetailAsync(
        Guid documentId, CancellationToken cancellationToken) =>
        context.BrandSourceDocuments
            .AsNoTracking()
            .Where(document => document.Id == documentId)
            .Join(
                context.BrandSourceDocumentVersions.AsNoTracking(),
                document => new { DocumentId = document.Id, Number = document.CurrentVersionNumber },
                version => new { DocumentId = version.BrandSourceDocumentId, Number = version.VersionNumber },
                (document, version) => new BrandSourceDocumentDetailRecord(
                    document.Id,
                    document.Title,
                    document.DocumentType,
                    document.Purpose,
                    document.ChannelKey,
                    document.Audience,
                    document.Status,
                    document.ArchivedAt,
                    document.CreatedAt,
                    document.UpdatedAt,
                    document.RowVersion,
                    version.Id,
                    version.VersionNumber,
                    version.MediaType,
                    version.SizeBytes,
                    version.OriginalFileName,
                    version.ContentChecksum,
                    version.CreatedAt,

                    // The version's current extraction is its highest ordinal, read exactly as the list reads
                    // it: three index seeks on one row rather than a shape re-mapped in memory.
                    context.BrandSourceExtractions
                        .Where(extraction => extraction.BrandSourceDocumentVersionId == version.Id)
                        .OrderByDescending(extraction => extraction.Ordinal)
                        .Select(extraction => (BrandSourceExtractionStatus?)extraction.Status)
                        .FirstOrDefault(),
                    context.BrandSourceExtractions
                        .Where(extraction => extraction.BrandSourceDocumentVersionId == version.Id)
                        .OrderByDescending(extraction => extraction.Ordinal)
                        .Select(extraction => (BrandSourceExtractionOrigin?)extraction.Origin)
                        .FirstOrDefault(),
                    context.BrandSourceExtractions
                        .Where(extraction => extraction.BrandSourceDocumentVersionId == version.Id)
                        .OrderByDescending(extraction => extraction.Ordinal)
                        .Select(extraction => (DateTimeOffset?)extraction.CreatedAt)
                        .FirstOrDefault()))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<BrandSourceDocumentStatus?> FindStatusAsync(
        Guid documentId, CancellationToken cancellationToken) =>
        await context.BrandSourceDocuments
            .AsNoTracking()
            .Where(document => document.Id == documentId)
            .Select(document => (BrandSourceDocumentStatus?)document.Status)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<BrandSourceVersionObjectRecord?> FindVersionObjectAsync(
        Guid documentId, int versionNumber, CancellationToken cancellationToken) =>
        context.BrandSourceDocumentVersions
            .AsNoTracking()
            .Where(version => version.BrandSourceDocumentId == documentId && version.VersionNumber == versionNumber)
            .Join(
                context.BrandSourceDocuments.AsNoTracking(),
                version => version.BrandSourceDocumentId,
                document => document.Id,
                (version, document) => new BrandSourceVersionObjectRecord(
                    document.Title,
                    version.VersionNumber,
                    version.MediaType,
                    version.SizeBytes,
                    version.ContentChecksum,
                    version.ObjectKey))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<BrandSourceDocument?> FindForUpdateAsync(Guid documentId, CancellationToken cancellationToken) =>
        context.BrandSourceDocuments.SingleOrDefaultAsync(document => document.Id == documentId, cancellationToken);

    public async Task<byte[]?> CurrentRowVersionAsync(Guid documentId, CancellationToken cancellationToken) =>
        await context.BrandSourceDocuments
            .AsNoTracking()
            .Where(document => document.Id == documentId)
            .Select(document => document.RowVersion)
            .SingleOrDefaultAsync(cancellationToken);


    public async Task<IReadOnlyList<BrandSourceHoldRecord>> HoldsAsync(
        Guid documentId, CancellationToken cancellationToken) =>
        await context.BrandStyleGuideSourceLinks
            .AsNoTracking()
            .Join(
                context.BrandSourceDocumentVersions.AsNoTracking().Where(version => version.BrandSourceDocumentId == documentId),
                link => link.BrandSourceDocumentVersionId,
                version => version.Id,
                (link, version) => new { link.BrandStyleGuideVersionId, version.VersionNumber })
            .Join(
                context.BrandStyleGuideVersions.AsNoTracking(),
                row => row.BrandStyleGuideVersionId,
                guideVersion => guideVersion.Id,
                (row, guideVersion) => new { SourceVersionNumber = row.VersionNumber, guideVersion })
            .Join(
                context.BrandStyleGuides.AsNoTracking(),
                row => row.guideVersion.BrandStyleGuideId,
                guide => guide.Id,
                (row, guide) => new { row.SourceVersionNumber, row.guideVersion, guide })

            // Ordered before the projection, not after: a record constructor is not something SQL can sort by,
            // and ordering the projected rows would move the whole join into memory.
            .OrderByDescending(row => row.guideVersion.VersionNumber)
            .ThenBy(row => row.SourceVersionNumber)
            .Select(row => new BrandSourceHoldRecord(
                row.guide.Id,
                row.guide.DisplayName,
                row.guideVersion.Id,
                row.guideVersion.VersionNumber,
                row.SourceVersionNumber,

                // A version is approved when an approval row exists for it; there is no status column to
                // read, which is what lets the version itself be immutable from the moment it is written.
                context.BrandStyleGuideApprovals.Any(approval => approval.BrandStyleGuideVersionId == row.guideVersion.Id)))
            .ToListAsync(cancellationToken);

    public async Task<BrandSourceDocumentSizeRecord> SizeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        // Aggregated in SQL rather than by materialising the versions: a document's history is unbounded and
        // nothing here needs a single row of it.
        var size = await context.BrandSourceDocumentVersions
            .AsNoTracking()
            .Where(version => version.BrandSourceDocumentId == documentId)
            .GroupBy(_ => 1)
            .Select(group => new BrandSourceDocumentSizeRecord(group.Count(), group.Sum(version => version.SizeBytes)))
            .SingleOrDefaultAsync(cancellationToken);

        // No rows means no group, which the model says cannot happen — a document is created with its first
        // version. Answered as zero rather than thrown: this read exists to describe a document, and failing
        // it over an impossibility would take down a confirmation screen rather than report anything useful.
        return size ?? new BrandSourceDocumentSizeRecord(0, 0);
    }

    public async Task<(IReadOnlyList<RemovedBrandSourceDocumentRecord> Rows, bool HasMore)> ListRemovedAsync(
        RemovedBrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken)
    {
        var removed = context.BrandSourceDocuments
            .AsNoTracking()
            .Where(document => document.Status == BrandSourceDocumentStatus.Removed);

        if (criteria.Position is { } position)
        {
            // Must name the ordering's two columns and directions, as the library's paging does.
            removed = removed.Where(document =>
                document.RemovedAt < position.RemovedAt
                || (document.RemovedAt == position.RemovedAt && document.Id.CompareTo(position.DocumentId) < 0));
        }

        var fetched = await removed
            .OrderByDescending(document => document.RemovedAt)
            .ThenByDescending(document => document.Id)

            // One row past the limit: how the page learns whether another follows, without a count.
            .Take(criteria.Limit + 1)
            .Select(document => new RemovedBrandSourceDocumentRecord(
                document.Id,
                document.Title,
                document.DocumentType,
                document.Purpose,
                document.CurrentVersionNumber,

                // Not null on a removed row: CK_BrandSourceDocuments_Removed_Consistent is what guarantees it.
                document.RemovedAt!.Value,
                document.RemovedByMembershipId!.Value,
                document.CreatedAt,
                document.RowVersion))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    /// <summary>
    /// Every filter and nothing else. No <c>WorkspaceId</c> predicate is written: the global query filter
    /// scopes each table named here, so another workspace's tag or version is not visible to the subqueries.
    /// </summary>
    private IQueryable<BrandSourceDocument> Filtered(BrandSourceDocumentListFilters filters)
    {
        // Always one status, so IX_BrandSourceDocuments_Workspace_Status_UpdatedAt both filters and orders.
        var documents = context.BrandSourceDocuments.AsNoTracking().Where(document => document.Status == filters.Status);

        if (filters.DocumentType is { } documentType)
        {
            documents = documents.Where(document => document.DocumentType == documentType);
        }

        if (filters.ChannelKey is { } channelKey)
        {
            documents = documents.Where(document => document.ChannelKey == channelKey);
        }

        if (filters.TagNames is { Count: > 0 } tagNames)
        {
            // A semi-join, not a join: a document carrying two of the requested tags appears once.
            documents = documents.Where(document => context.BrandSourceDocumentTags.Any(link =>
                link.BrandSourceDocumentId == document.Id
                && context.BrandSourceTags.Any(tag => tag.Id == link.BrandSourceTagId && tagNames.Contains(tag.NormalizedName))));
        }

        if (filters.Search is { } term)
        {
            // Lowered on both sides rather than relying on the collation, as the recipe library does: SQL
            // Server's default is case-insensitive and SQLite's is not.
            documents = documents.Where(document =>
                document.Title.ToLower().Contains(term)
                || context.BrandSourceDocumentVersions.Any(version =>
                    version.BrandSourceDocumentId == document.Id
                    && version.VersionNumber == document.CurrentVersionNumber
                    && version.OriginalFileName.ToLower().Contains(term)));
        }

        return documents;
    }

    /// <summary>Resumes after the previous page's last row. Must name the ordering's two columns and directions.</summary>
    private static IQueryable<BrandSourceDocument> Positioned(
        IQueryable<BrandSourceDocument> documents, BrandSourceDocumentListPosition? position) =>
        position is null
            ? documents
            : documents.Where(document =>
                document.UpdatedAt < position.UpdatedAt
                || (document.UpdatedAt == position.UpdatedAt && document.Id.CompareTo(position.DocumentId) < 0));
}
