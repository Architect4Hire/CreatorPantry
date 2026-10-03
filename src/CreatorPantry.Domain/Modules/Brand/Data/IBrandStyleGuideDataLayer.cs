using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

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

/// <summary>
/// The workspace's one stored activation decision, whichever guide holds it.
/// </summary>
/// <remarks>
/// Not guide-scoped, unlike the <c>Active</c> side of <see cref="BrandStyleGuideRead"/>: an activation has to
/// compare against the default — and report what it replaced — even when that default belongs to a different
/// guide than the one in the route, which is the question a guide-scoped read cannot answer.
/// </remarks>
public sealed record BrandStyleGuideWorkspaceDefault(
    Guid GuideId,
    Guid VersionId,
    int VersionNumber,
    DateTimeOffset ActivatedAt,
    Guid ActivatedByMembershipId,
    string? Reason);

/// <summary>
/// Everything an activation has to decide on, read before anything is written.
/// </summary>
/// <param name="Version">
/// The named version with its sections and rules, or null when the guide has no version of that number. The
/// guide itself is known to exist by the time this is returned.
/// </param>
/// <param name="IsApproved">Whether an approval row exists for the version. A draft has none.</param>
/// <param name="StaleSourceCount">
/// How many of the version's citations name a source document version the document has since superseded.
/// </param>
/// <param name="CurrentDefault">The workspace's default as it stands, or null when it has none.</param>
public sealed record BrandStyleGuideActivationRead(
    BrandStyleGuide Guide,
    BrandStyleGuideVersion? Version,
    bool IsApproved,
    int StaleSourceCount,
    BrandStyleGuideWorkspaceDefault? CurrentDefault);

/// <summary>
/// Everything an approval has to decide on, read before anything is written.
/// </summary>
/// <param name="Version">
/// The named version with its sections and rules, or null when the guide has no version of that number. The
/// guide itself is known to exist by the time this is returned.
/// </param>
/// <param name="Approval">
/// The version's existing approval, or null. Not a bool, unlike
/// <see cref="BrandStyleGuideActivationRead.IsApproved"/>: approving an approved version answers with the
/// original approver, time and words rather than this request's.
/// </param>
/// <remarks>
/// The workspace default is deliberately absent. Approval says a version is finished and does not look at, or
/// touch, which version the workspace writes with — that is activation's one decision.
/// </remarks>
public sealed record BrandStyleGuideApprovalRead(
    BrandStyleGuide Guide,
    BrandStyleGuideVersion? Version,
    BrandStyleGuideApproval? Approval);

/// <summary>Why an approval was not written. <see cref="None"/> means it was.</summary>
public enum BrandStyleGuideApprovalWrite
{
    None = 0,

    /// <summary>
    /// Another request approved the same version between this one's read and its save. The caller is asked to
    /// read again and decide, and a retry finds the version approved.
    /// </summary>
    Conflict = 1,
}

/// <summary>Why an activation was not written. <see cref="None"/> means it was.</summary>
public enum BrandStyleGuideActivationWrite
{
    None = 0,

    /// <summary>
    /// The default row moved between the read and the save. The caller is told the same thing a mismatched
    /// expectation is told: re-read and decide again.
    /// </summary>
    Conflict = 1,
}

/// <summary>
/// The guide and the working version an accepted proposal is about to be laid over.
/// </summary>
/// <param name="Working">
/// The highest-numbered version with its sections and rules. Never null: a guide always has version 1.
/// </param>
/// <param name="Citations">What that version already cites, by document and pinned version number.</param>
/// <param name="WorkingStaleSourceCount">
/// How many of those the owning document has since replaced.
/// </param>
/// <remarks>
/// <strong><paramref name="WorkingStaleSourceCount"/> is read here rather than only after a write</strong>
/// because accepting guidance identical to what the guide already says writes no version at all, and the reply
/// still has to be able to say how stale the citations of the version that stands are. Computed with the same
/// query the version history and the activation check use.
/// </remarks>
public sealed record BrandStyleGuideProposalTargetRead(
    BrandStyleGuide Guide,
    BrandStyleGuideVersion Working,
    IReadOnlyList<(Guid DocumentId, int VersionNumber)> Citations,
    int WorkingStaleSourceCount);

/// <summary>
/// The workspace's active guide version, with the guide it belongs to and when it was activated.
/// </summary>
/// <remarks>
/// Narrower than <see cref="BrandStyleGuideRead"/>: there is no working version here, because a caller asking
/// what the workspace writes in is asking about the version that was activated, not the one someone is still
/// editing.
/// </remarks>
public sealed record BrandActiveStyleGuideRead(
    BrandStyleGuide Guide,
    BrandStyleGuideVersionRead Version,
    DateTimeOffset ActivatedAt,
    string? ActivationReason,
    int StaleSourceCount = 0);

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

    /// <summary>
    /// Reads the guide, the named version and the workspace's current default: everything an activation
    /// decides on.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the resolved workspace has no such guide — which is also what another workspace's
    /// guide looks like.
    /// </returns>
    Task<BrandStyleGuideActivationRead?> ReadForActivationAsync(
        Guid guideId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// The workspace's current default on its own, for reporting a save that lost a race. Null when the
    /// workspace has none.
    /// </summary>
    Task<BrandStyleGuideWorkspaceDefault?> ReadWorkspaceDefaultAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the guide, the named version and any approval it already has: everything an approval decides on.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the resolved workspace has no such guide — which is also what another workspace's
    /// guide looks like.
    /// </returns>
    Task<BrandStyleGuideApprovalRead?> ReadForApprovalAsync(
        Guid guideId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Writes one version's approval and the audit entry, in one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transaction boundary for an approval. The approval row and the audit entry commit together or
    /// neither does, and so does the idempotency record the facade's executor staged on this same
    /// <c>DbContext</c> — which is what stops a replay from returning a response for a write that rolled back.
    /// </para>
    /// <para>
    /// <strong>Nothing of the version is touched.</strong> A version is immutable and stays so: this adds a
    /// row beside it, which is what makes "approved" a fact about the version rather than a column somebody
    /// could flip back.
    /// </para>
    /// <para>
    /// Two requests approving the same version at once is the one race, and the primary key on
    /// <c>(WorkspaceId, BrandStyleGuideVersionId)</c> decides it. Only requests carrying *different*
    /// idempotency keys can reach it: the executor's own record claims one scope at a time.
    /// </para>
    /// <para>
    /// <strong>The loser is refused rather than handed the winner's approval</strong>, although the version is
    /// approved either way, and that is deliberate. Recovering from the failed insert means clearing the
    /// change tracker, which detaches the idempotency record the executor staged on this scope — so a success
    /// returned after it would commit a record whose stored result was never written, and a later replay of
    /// that key would answer from it. A conflict rolls the whole attempt back instead, and the retry it asks
    /// for finds the version approved and answers with the original approver.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see cref="BrandStyleGuideApprovalWrite.None"/> on success, or
    /// <see cref="BrandStyleGuideApprovalWrite.Conflict"/> when another request approved the same version
    /// under this one.
    /// </returns>
    Task<BrandStyleGuideApprovalWrite> ApproveAsync(
        Guid versionId,
        string? reason,
        Guid membershipId,
        DateTimeOffset now,
        AuditEntry audit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Repoints the workspace default at <paramref name="versionId"/> and records the audit entry, in one
    /// save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transaction boundary for an activation. The default row and the audit entry commit together or
    /// neither does, and so does the idempotency record the facade's executor staged on this same
    /// <c>DbContext</c> — which is what stops a replay from returning a response for a write that rolled back.
    /// </para>
    /// <para>
    /// Three of the guarantees here are the schema's rather than this method's, and hold even if Business's
    /// checks were bypassed: the foreign key targets <c>BrandStyleGuideApprovals</c>, so an unapproved version
    /// cannot be stored as the default; <c>WorkspaceId</c> is the whole primary key, so a second default
    /// cannot exist as a rival; and <c>RowVersion</c> decides which of two simultaneous activations wins.
    /// </para>
    /// <para>
    /// <strong>The two paths lose differently.</strong> An update carries <c>RowVersion</c> in its
    /// <c>WHERE</c> clause, so its loser arrives as a <c>DbUpdateConcurrencyException</c>. An insert has no
    /// row version to check — the guard is the primary key — so its loser arrives as an ordinary
    /// <c>DbUpdateException</c>, indistinguishable by type from a schema refusal. Both are reported as the
    /// same conflict, and the insert path tells them apart by looking: a default that exists now is one
    /// another activation wrote.
    /// </para>
    /// </remarks>
    /// <param name="current">
    /// The default as Business read it, which decides whether this writes an insert or an update. Passed down
    /// rather than read again here so the choice is made once, against the same read every other decision in
    /// this activation was made against.
    /// </param>
    /// <returns>
    /// <see cref="BrandStyleGuideActivationWrite.None"/> on success, or
    /// <see cref="BrandStyleGuideActivationWrite.Conflict"/> when the default moved under the save.
    /// </returns>
    Task<BrandStyleGuideActivationWrite> ActivateAsync(
        BrandStyleGuideWorkspaceDefault? current,
        Guid versionId,
        string? reason,
        Guid membershipId,
        DateTimeOffset now,
        AuditEntry audit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one guide of the resolved workspace with the working version an accepted proposal will be laid
    /// over, or null if there is none — which is also what another workspace's guide looks like.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="ReadAsync"/>: no approval, no activation, no parent version number, and the
    /// active version is not resolved. None of them bears on what a new version should say, and each is a
    /// further query inside a transaction another module is holding open.
    /// </remarks>
    Task<BrandStyleGuideProposalTargetRead?> ReadForProposalAsync(
        Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes one further version of a guide with its sections, rules and source links, plus the audit entry,
    /// and reports how many of its citations are stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No transaction of its own, and that is the point.</strong> This is called from inside the
    /// transaction the AI module's acceptance seam opened, so the version, the proposal's per-change
    /// dispositions, the operation's terminal status and both audit entries commit together or none of them do.
    /// Opening a second transaction here would throw on the nesting; committing one would break the atomicity
    /// the caller exists to provide.
    /// </para>
    /// <para>
    /// <strong>It saves rather than merely staging</strong>, for the same reason the recipe module's
    /// proposal create does: the caller needs the rows to exist before it reads back what they imply — here the
    /// stale-citation count, which is computed with the identical query the version history and the activation
    /// check use, so three routes cannot disagree about what "stale" means.
    /// </para>
    /// <para>
    /// The change tracker is cleared on failure and left alone on success: the caller has its own entities
    /// staged in this same scope and still to save, so clearing on the way out would discard them.
    /// </para>
    /// </remarks>
    /// <returns>
    /// How many of the new version's cited source versions the owning document has since replaced. Zero when
    /// every citation is still its document's current version.
    /// </returns>
    Task<int> AddVersionAsync(
        BrandStyleGuideVersion version, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// The guide version this workspace has made its default, whichever guide holds it, or null when it has
    /// none.
    /// </summary>
    /// <remarks>
    /// <strong>Keyed on the workspace rather than on a guide, which is what makes it new.</strong>
    /// <see cref="ReadAsync"/> answers "this guide, and its active version if the default happens to be one of
    /// its own" — a caller that does not already know which guide holds the default cannot use it, and an
    /// assembler reaching for the workspace's brand voice is exactly that caller. No fallback to a latest
    /// approved version: a workspace with no activation has no active guide, and saying otherwise would ground a
    /// generation on something nobody chose.
    /// </remarks>
    Task<BrandActiveStyleGuideRead?> ReadActiveAsync(CancellationToken cancellationToken);
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

    public async Task<BrandActiveStyleGuideRead?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        // The workspace's one activation decision. No predicate: WorkspaceId is that table's whole primary key
        // and the query filter supplies it, so there is nothing here to pass wrong.
        if (await guides.FindWorkspaceDefaultAsync(cancellationToken) is not { } active
            || await guides.FindGuideAsync(active.GuideId, cancellationToken) is not { } guide
            || await guides.FindVersionAsync(active.VersionId, cancellationToken) is not { } version)
        {
            return null;
        }

        var citations = await guides.FindCitationsAsync([version.Id], cancellationToken);

        // The same count the version history publishes, so "stale" means one thing wherever a creator meets it.
        var stale = await guides.StaleSourceCountsAsync([version.Id], cancellationToken);

        return new BrandActiveStyleGuideRead(
            guide,
            new BrandStyleGuideVersionRead(
                version,
                version.ParentVersionId is { } parent
                    ? await guides.FindVersionNumberAsync(parent, cancellationToken)
                    : null,

                // Read rather than assumed. Activation requires an approval, so this is never null in practice —
                // and a caller deciding whether to ground a generation on this version should be told what is
                // recorded rather than what the write path promised.
                await guides.FindApprovalAsync(version.Id, cancellationToken),
                [.. citations.Select(citation => (citation.DocumentId, citation.VersionNumber))]),
            active.ActivatedAt,
            active.Reason,
            stale.TryGetValue(version.Id, out var staleCount) ? staleCount : 0);
    }

    public async Task<BrandStyleGuideProposalTargetRead?> ReadForProposalAsync(
        Guid guideId, CancellationToken cancellationToken)
    {
        // The guide and its working version, both or neither. A guide always has version 1, so a guide with no
        // working version is a broken row rather than a state to handle — and reporting absence is still the
        // right answer, because the caller's only move either way is to refuse.
        if (await guides.FindGuideAsync(guideId, cancellationToken) is not { } guide
            || await guides.FindWorkingVersionAsync(guideId, cancellationToken) is not { } working)
        {
            return null;
        }

        var citations = await guides.FindCitationsAsync([working.Id], cancellationToken);
        var stale = await guides.StaleSourceCountsAsync([working.Id], cancellationToken);

        return new BrandStyleGuideProposalTargetRead(
            guide,
            working,
            [.. citations.Select(citation => (citation.DocumentId, citation.VersionNumber))],
            stale.TryGetValue(working.Id, out var count) ? count : 0);
    }

    public async Task<int> AddVersionAsync(
        BrandStyleGuideVersion version, AuditEntry audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);

        guides.AddVersion(version);
        auditWriter.Record(audit);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Nothing may stay staged for a later save on this scope to commit. The transaction the caller holds
            // rolls back on its own; this is about what is left in memory afterwards.
            context.ChangeTracker.Clear();
            throw;
        }

        // After the save, so the rows this version's links were written as are visible to the query — and it is
        // the same query the version history and the activation check use, which is what keeps three routes from
        // disagreeing about which citations are stale.
        var stale = await guides.StaleSourceCountsAsync([version.Id], cancellationToken);

        return stale.TryGetValue(version.Id, out var count) ? count : 0;
    }

    public async Task<BrandStyleGuideActivationRead?> ReadForActivationAsync(
        Guid guideId, int versionNumber, CancellationToken cancellationToken)
    {
        // The guide first, and on its own, so "no such guide" stays a different answer from "no such version"
        // — and so an archived guide is recognised as archived rather than as absent.
        if (await guides.FindGuideAsync(guideId, cancellationToken) is not { } guide)
        {
            return null;
        }

        // By number within the route's guide, which is what makes another guide's version unreachable here
        // rather than merely refused.
        var version = (await guides.FindVersionsByNumberAsync(guideId, [versionNumber], cancellationToken))
            .FirstOrDefault();

        var currentDefault = await guides.FindWorkspaceDefaultAsync(cancellationToken);

        if (version is null)
        {
            return new BrandStyleGuideActivationRead(guide, null, IsApproved: false, StaleSourceCount: 0, currentDefault);
        }

        var staleSourceCounts = await guides.StaleSourceCountsAsync([version.Id], cancellationToken);

        return new BrandStyleGuideActivationRead(
            guide,
            version,
            await guides.FindApprovalAsync(version.Id, cancellationToken) is not null,
            staleSourceCounts.TryGetValue(version.Id, out var stale) ? stale : 0,
            currentDefault);
    }

    public Task<BrandStyleGuideWorkspaceDefault?> ReadWorkspaceDefaultAsync(CancellationToken cancellationToken) =>
        guides.FindWorkspaceDefaultAsync(cancellationToken);

    public async Task<BrandStyleGuideApprovalRead?> ReadForApprovalAsync(
        Guid guideId, int versionNumber, CancellationToken cancellationToken)
    {
        // The guide first, and on its own, so "no such guide" stays a different answer from "no such version"
        // — and so an archived guide is recognised as archived rather than as absent.
        if (await guides.FindGuideAsync(guideId, cancellationToken) is not { } guide)
        {
            return null;
        }

        // By number within the route's guide, which is what makes another guide's version unreachable here
        // rather than merely refused.
        var version = (await guides.FindVersionsByNumberAsync(guideId, [versionNumber], cancellationToken))
            .FirstOrDefault();

        return version is null
            ? new BrandStyleGuideApprovalRead(guide, null, null)
            : new BrandStyleGuideApprovalRead(guide, version, await guides.FindApprovalAsync(version.Id, cancellationToken));
    }

    public async Task<BrandStyleGuideApprovalWrite> ApproveAsync(
        Guid versionId,
        string? reason,
        Guid membershipId,
        DateTimeOffset now,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        // WorkspaceId is left to WorkspaceOwnershipInterceptor, as on every other workspace-owned insert: a
        // server-stamped value cannot be the one a request supplied.
        guides.AddApproval(new BrandStyleGuideApproval
        {
            BrandStyleGuideVersionId = versionId,
            Reason = reason,
            ApprovedByMembershipId = membershipId,
            ApprovedAt = now,
        });

        auditWriter.Record(audit);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Nothing committed, and nothing may stay staged for the next save on this scope to commit.
            context.ChangeTracker.Clear();

            // The row is write-once with the version as its key, so the only insert this loses is to another
            // approval of the same version. Asked by looking, because an insert has no row version to check
            // and a primary-key refusal is indistinguishable by type from a schema one.
            if (await guides.FindApprovalAsync(versionId, cancellationToken) is not null)
            {
                return BrandStyleGuideApprovalWrite.Conflict;
            }

            throw;
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        return BrandStyleGuideApprovalWrite.None;
    }

    public async Task<BrandStyleGuideActivationWrite> ActivateAsync(
        BrandStyleGuideWorkspaceDefault? current,
        Guid versionId,
        string? reason,
        Guid membershipId,
        DateTimeOffset now,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        if (current is null)
        {
            // WorkspaceId is left to WorkspaceOwnershipInterceptor, as on every other workspace-owned insert:
            // a server-stamped value cannot be the one a request supplied.
            guides.AddDefault(new BrandStyleGuideDefault
            {
                BrandStyleGuideVersionId = versionId,
                Reason = reason,
                ActivatedByMembershipId = membershipId,
                ActivatedAt = now,
            });
        }
        else
        {
            // Tracked, unlike every read above: this row is read to be written, and its RowVersion becomes
            // the UPDATE's WHERE clause.
            if (await guides.TrackWorkspaceDefaultAsync(cancellationToken) is not { } tracked)
            {
                // Business read a default and there is none now, so something removed it under this request.
                // Inserting instead would write a decision against a workspace state nobody has seen.
                return BrandStyleGuideActivationWrite.Conflict;
            }

            tracked.BrandStyleGuideVersionId = versionId;
            tracked.Reason = reason;
            tracked.ActivatedByMembershipId = membershipId;
            tracked.ActivatedAt = now;
        }

        auditWriter.Record(audit);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Nothing committed, and nothing may stay staged for the next save on this scope to commit.
            context.ChangeTracker.Clear();

            // The two paths lose differently. An update carries RowVersion in its WHERE clause, so its loser
            // is a DbUpdateConcurrencyException. An insert has no row version to check — the guard is the
            // primary key on WorkspaceId — so its loser is an ordinary DbUpdateException, which is also what a
            // schema refusal looks like. Telling those apart by exception type is not possible, so ask the
            // question that matters: is there a default now that this request did not write? If so, another
            // activation wrote it, which is the same conflict a mismatched expectation reports.
            if (exception is DbUpdateConcurrencyException
                || (current is null && await guides.FindWorkspaceDefaultAsync(cancellationToken) is not null))
            {
                return BrandStyleGuideActivationWrite.Conflict;
            }

            throw;
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        return BrandStyleGuideActivationWrite.None;
    }
}
