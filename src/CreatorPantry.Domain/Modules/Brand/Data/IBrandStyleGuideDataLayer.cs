using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>One version of a guide as stored, with what is attached to it.</summary>
public sealed record BrandStyleGuideVersionRead(
    BrandStyleGuideVersion Version,
    int? ParentVersionNumber,
    BrandStyleGuideApproval? Approval,
    IReadOnlyList<(Guid DocumentId, int VersionNumber)> Citations);

/// <summary>A guide with its working version and, if the workspace default is one of its own, the active one.</summary>
public sealed record BrandStyleGuideRead(
    BrandStyleGuide Guide,
    BrandStyleGuideVersionRead Working,
    BrandStyleGuideVersionRead? Active,
    DateTimeOffset? ActivatedAt,
    string? ActivationReason);

/// <summary>
/// One page of a guide's version history, with the stale-citation counts read for the page's own rows.
/// </summary>
/// <param name="StaleSourceCounts">
/// Keyed by guide version id, and holding only the versions that have a stale citation. A version absent from
/// it has none.
/// </param>
public sealed record BrandStyleGuideVersionListPage(
    IReadOnlyList<BrandStyleGuideVersionSummaryRecord> Rows,
    bool HasMore,
    IReadOnlyDictionary<Guid, int> StaleSourceCounts);

/// <summary>One side of a comparison as stored: the version, whether it is approved, and what it cites.</summary>
/// <remarks>
/// Narrower than <see cref="BrandStyleGuideVersionRead"/>, which also resolves the parent version's number —
/// a second query per version that a comparison has no reading for.
/// </remarks>
public sealed record BrandStyleGuideComparisonVersionRead(
    BrandStyleGuideVersion Version,
    bool IsApproved,
    IReadOnlyList<(Guid DocumentId, int VersionNumber)> Citations);

/// <summary>
/// Whichever of the two requested versions the guide actually has. Either side is null when the guide has no
/// version of that number; the guide itself is known to exist by the time this is returned.
/// </summary>
public sealed record BrandStyleGuideComparisonRead(
    BrandStyleGuideComparisonVersionRead? From,
    BrandStyleGuideComparisonVersionRead? To);

public interface IBrandStyleGuideDataLayer
{
    /// <summary>
    /// Reads one guide of the resolved workspace, or null if there is none — which is also what another
    /// workspace's guide looks like.
    /// </summary>
    Task<BrandStyleGuideRead?> ReadAsync(Guid guideId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandStyleGuideRepository.GuideExistsAsync"/>
    Task<bool> GuideExistsAsync(Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the two versions a comparison needs, with their sections, rules, citations and approval state.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the resolved workspace has no such guide — which is also what another workspace's
    /// guide looks like. Otherwise the read, with either side null if the guide lacks that version number.
    /// </returns>
    /// <remarks>
    /// The guide's existence is settled before any version number is looked up, so "no such guide" and "no
    /// such version" stay two separate answers, and the second one can name a parameter without disclosing
    /// anything: by then the guide is known to be readable.
    /// </remarks>
    Task<BrandStyleGuideComparisonRead?> ReadForComparisonAsync(
        Guid guideId, int fromVersionNumber, int toVersionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// One page of a guide's version history: the projected rows, whether another page follows, and the
    /// stale-citation count for each row that has one.
    /// </summary>
    Task<BrandStyleGuideVersionListPage> ListVersionsAsync(
        BrandStyleGuideVersionListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves each cited (document, version number) to the exact version id in the resolved workspace.
    /// An entry is null when it cannot be used, for any reason, and the reasons are not distinguished.
    /// </summary>
    Task<IReadOnlyList<Guid?>> ResolveSourceVersionsAsync(
        IReadOnlyList<BrandStyleGuideSourceInput> sources, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the guide, its first version with every section, rule and source link, and the audit entry in
    /// one save, so none can commit without the others.
    /// </summary>
    Task CreateAsync(
        BrandStyleGuide guide, BrandStyleGuideVersion version, AuditEntry audit, CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideDataLayer(
    IBrandStyleGuideRepository guides,
    IAuditWriter auditWriter,
    CreatorPantryDbContext context) : IBrandStyleGuideDataLayer
{
    public async Task<BrandStyleGuideRead?> ReadAsync(Guid guideId, CancellationToken cancellationToken)
    {
        if (await guides.FindGuideAsync(guideId, cancellationToken) is not { } guide
            || await guides.FindWorkingVersionAsync(guideId, cancellationToken) is not { } working)
        {
            return null;
        }

        var activeRecord = await guides.FindActiveAsync(guideId, cancellationToken);
        var active = activeRecord is null
            ? null
            : activeRecord.VersionId == working.Id
                ? working
                : await guides.FindVersionAsync(activeRecord.VersionId, cancellationToken);

        var versions = new[] { working, active }.Where(version => version is not null).Select(version => version!).ToList();
        var citations = await guides.FindCitationsAsync([.. versions.Select(version => version.Id).Distinct()], cancellationToken);

        async Task<BrandStyleGuideVersionRead> Attach(BrandStyleGuideVersion version) => new(
            version,
            version.ParentVersionId is { } parent ? await guides.FindVersionNumberAsync(parent, cancellationToken) : null,
            await guides.FindApprovalAsync(version.Id, cancellationToken),
            [.. citations.Where(citation => citation.GuideVersionId == version.Id)
                .Select(citation => (citation.DocumentId, citation.VersionNumber))]);

        return new BrandStyleGuideRead(
            guide,
            await Attach(working),
            active is null ? null : await Attach(active),
            activeRecord?.ActivatedAt,
            activeRecord?.ActivationReason);
    }

    public Task<bool> GuideExistsAsync(Guid guideId, CancellationToken cancellationToken) =>
        guides.GuideExistsAsync(guideId, cancellationToken);

    public async Task<BrandStyleGuideComparisonRead?> ReadForComparisonAsync(
        Guid guideId, int fromVersionNumber, int toVersionNumber, CancellationToken cancellationToken)
    {
        if (!await guides.GuideExistsAsync(guideId, cancellationToken))
        {
            return null;
        }

        var sameVersion = fromVersionNumber == toVersionNumber;
        var numbers = sameVersion ? new[] { fromVersionNumber } : [fromVersionNumber, toVersionNumber];

        var versions = await guides.FindVersionsByNumberAsync(guideId, numbers, cancellationToken);
        var citations = await guides.FindCitationsAsync([.. versions.Select(version => version.Id)], cancellationToken);

        async Task<BrandStyleGuideComparisonVersionRead> Attach(BrandStyleGuideVersion version) => new(
            version,
            await guides.FindApprovalAsync(version.Id, cancellationToken) is not null,
            [.. citations.Where(citation => citation.GuideVersionId == version.Id)
                .Select(citation => (citation.DocumentId, citation.VersionNumber))]);

        var from = versions.FirstOrDefault(version => version.VersionNumber == fromVersionNumber);
        var fromRead = from is null ? null : await Attach(from);

        // One number was asked for twice, so the one row is both sides — read and attached once.
        if (sameVersion)
        {
            return new BrandStyleGuideComparisonRead(fromRead, fromRead);
        }

        var to = versions.FirstOrDefault(version => version.VersionNumber == toVersionNumber);

        return new BrandStyleGuideComparisonRead(fromRead, to is null ? null : await Attach(to));
    }

    public async Task<BrandStyleGuideVersionListPage> ListVersionsAsync(
        BrandStyleGuideVersionListCriteria criteria, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await guides.ListVersionsAsync(criteria, cancellationToken);

        // A second read for the page's own versions, at most a page of ids, rather than a three-table
        // subquery per row. Skipped entirely when nothing on the page cites anything.
        var staleSourceCounts = await guides.StaleSourceCountsAsync(
            [.. rows.Where(row => row.SourceCount > 0).Select(row => row.Id)], cancellationToken);

        return new BrandStyleGuideVersionListPage(rows, hasMore, staleSourceCounts);
    }

    public async Task<IReadOnlyList<Guid?>> ResolveSourceVersionsAsync(
        IReadOnlyList<BrandStyleGuideSourceInput> sources, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var found = await guides.FindSourceVersionsAsync(
            [.. sources.Select(source => source.DocumentId!.Value).Distinct()], cancellationToken);

        return
        [
            .. sources.Select(source => found
                .Where(candidate => candidate.DocumentId == source.DocumentId && candidate.VersionNumber == source.VersionNumber)
                .Select(candidate => (Guid?)candidate.VersionId)
                .FirstOrDefault()),
        ];
    }

    public async Task CreateAsync(
        BrandStyleGuide guide, BrandStyleGuideVersion version, AuditEntry audit, CancellationToken cancellationToken)
    {
        guides.Add(guide, version);
        auditWriter.Record(audit);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Nothing may stay staged for the next save on this scope to commit.
            context.ChangeTracker.Clear();
            throw;
        }
    }
}
