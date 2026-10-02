using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>One stored passage as the repository projects it, in SQL. No vector is read.</summary>
/// <param name="WorkspaceId">
/// Projected so Business can assert it, not because a caller needs it.
/// </param>
/// <remarks>
/// <strong><paramref name="WorkspaceId"/> exists for defence in depth.</strong> Isolation here rests on the
/// global query filter; without the column on the row there is nothing for a later layer to check it against, so
/// a filter that stopped applying — a convention regression, a stray <c>IgnoreQueryFilters</c> — would be
/// invisible until a creator saw a neighbour's writing in their own brand guidance.
/// </remarks>
internal sealed record BrandSourcePassageRecord(
    Guid WorkspaceId,
    Guid PassageId,
    Guid DocumentId,
    int VersionNumber,
    int Ordinal,
    string Text);

internal interface IBrandSourcePassageRepository
{
    /// <summary>
    /// The passages of the named document versions' <strong>current</strong> chunk sets, in document and
    /// reading order, at most <paramref name="perVersion"/> from each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three workspace-owned sets are joined — chunks, their sets, and the document versions — so the global
    /// query filter scopes all three and no workspace is named here. A selector naming another workspace's
    /// document matches nothing rather than being refused by a check.
    /// </para>
    /// <para>
    /// <strong>Ordinal order, not similarity.</strong> The embeddings exist and are deliberately not read: the
    /// stored vector is a SQL Server <c>vector(1536)</c> and ranking it needs <c>VECTOR_DISTANCE</c>, which the
    /// test database does not have. Reading order is a stable, explainable selection that cites the same rows a
    /// ranked one would, so a later change to <c>ORDER BY</c> leaves this method's contract — and every stored
    /// citation — untouched. It is not a claim that these are the most relevant passages.
    /// </para>
    /// <para>
    /// Only <see cref="BrandSourceChunkSetStatus.Current"/> sets are read. A <c>Building</c> set is incomplete
    /// and a <c>Superseded</c> one describes text the document has moved past; grounding on either would cite a
    /// passage the creator can no longer see.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<BrandSourcePassageRecord>> ListCurrentPassagesAsync(
        IReadOnlyCollection<BrandSourcePassageSelector> selectors,
        int perVersion,
        CancellationToken cancellationToken);
}

internal sealed class BrandSourcePassageRepository(CreatorPantryDbContext context) : IBrandSourcePassageRepository
{
    public async Task<IReadOnlyList<BrandSourcePassageRecord>> ListCurrentPassagesAsync(
        IReadOnlyCollection<BrandSourcePassageSelector> selectors,
        int perVersion,
        CancellationToken cancellationToken)
    {
        if (selectors.Count == 0 || perVersion <= 0)
        {
            return [];
        }

        var documentIds = selectors.Select(selector => selector.DocumentId).Distinct().ToList();
        var versionNumbers = selectors.Select(selector => selector.VersionNumber).Distinct().ToList();

        // One query over the candidate (document, number) space rather than one per selector, then the exact
        // pairs are kept in memory below. A composite IN over tuples is not something the provider translates,
        // and the cross-product here is bounded by the caller's own cap on how many versions it may name.
        var rows = await (from chunk in context.BrandSourceChunks.AsNoTracking()
                          join set in context.BrandSourceChunkSets.AsNoTracking()
                              on chunk.BrandSourceChunkSetId equals set.Id
                          join version in context.BrandSourceDocumentVersions.AsNoTracking()
                              on set.BrandSourceDocumentVersionId equals version.Id
                          where set.Status == BrandSourceChunkSetStatus.Current
                              && documentIds.Contains(set.BrandSourceDocumentId)
                              && versionNumbers.Contains(version.VersionNumber)

                              // The set names a document and a version, and the version names a document too.
                              // Only the set's extraction foreign key is composite, so nothing in the schema
                              // forces those two document references to agree — and a set joined to a version of
                              // a different document would attribute its passages to the wrong one, which is a
                              // citation pointing somewhere the creator never selected.
                              && version.BrandSourceDocumentId == set.BrandSourceDocumentId
                          orderby set.BrandSourceDocumentId, version.VersionNumber, chunk.Ordinal
                          select new BrandSourcePassageRecord(
                              chunk.WorkspaceId,
                              chunk.Id,
                              set.BrandSourceDocumentId,
                              version.VersionNumber,
                              chunk.Ordinal,

                              // The passage text, and never the embedding: a 1536-float vector is of no use to
                              // a caller assembling a prompt and is expensive to materialise.
                              chunk.Text))
            .ToListAsync(cancellationToken);

        var wanted = selectors.ToHashSet();

        return
        [
            .. rows
                .Where(row => wanted.Contains(new BrandSourcePassageSelector(row.DocumentId, row.VersionNumber)))
                .GroupBy(row => (row.DocumentId, row.VersionNumber))
                .SelectMany(group => group.Take(perVersion)),
        ];
    }
}
