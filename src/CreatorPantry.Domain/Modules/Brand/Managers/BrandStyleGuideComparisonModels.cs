namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What happened to one compared item between two guide versions.
/// </summary>
/// <remarks>
/// <para>
/// These are exactly the states <c>CpDiffLegendComponent</c> renders, so the wire and the legend share one
/// vocabulary rather than needing a translation table between them.
/// </para>
/// <para>
/// <strong>One state rather than presence-plus-movement.</strong> The recipe module's comparison keeps those
/// two as independent facts, because a recipe line can be reworded and dragged in the same edit.
/// Nothing here can be both: a section is keyed and has no order to move in, a rule is identified by its own
/// text so it cannot be reworded and stay the same rule, and a citation set is unordered. The states are
/// therefore mutually exclusive, and a closed set a client switches over is the simpler truthful shape.
/// Each part below documents which members it can emit.
/// </para>
/// <para>This enum may grow; a client that tolerates unknown members is what the contract asks for.</para>
/// </remarks>
public enum BrandStyleGuideComparisonState
{
    /// <summary>Present on both sides and identical.</summary>
    Unchanged = 0,

    /// <summary>Present only in the <c>to</c> version.</summary>
    Added = 1,

    /// <summary>Present only in the <c>from</c> version.</summary>
    Removed = 2,

    /// <summary>Present on both sides, saying something different. Sections and sources only.</summary>
    Changed = 3,

    /// <summary>Present on both sides, unchanged, in a different position. Rules only.</summary>
    Moved = 4,
}

/// <summary>
/// One prose section, compared.
/// </summary>
/// <remarks>
/// Identified by <see cref="SectionKey"/> and <see cref="ChannelKey"/> together, which
/// <c>UX_BrandStyleGuideSections_Workspace_Version_Key_Channel</c> makes unique within a version and which
/// <see cref="BrandStyleGuideSectionKey"/> promises is never renumbered or reused. Emits
/// <see cref="BrandStyleGuideComparisonState.Added"/>, <see cref="BrandStyleGuideComparisonState.Removed"/>,
/// <see cref="BrandStyleGuideComparisonState.Changed"/> and
/// <see cref="BrandStyleGuideComparisonState.Unchanged"/> — never
/// <see cref="BrandStyleGuideComparisonState.Moved"/>, because sections have no stored order.
/// </remarks>
/// <param name="ChannelKey">The channel a variant section bends for; null on every other key.</param>
/// <param name="FromBody">What the section said in the <c>from</c> version; null when it was added.</param>
/// <param name="ToBody">What it says in the <c>to</c> version; null when it was removed.</param>
public sealed record BrandStyleGuideSectionComparisonServiceModel(
    BrandStyleGuideSectionKey SectionKey,
    string? ChannelKey,
    BrandStyleGuideComparisonState State,
    string? FromBody,
    string? ToBody);

/// <summary>
/// One do or don't rule, compared.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identified by what it says</strong> — <see cref="Kind"/> and <see cref="Text"/> — because that is
/// the only stable identity a rule has across two versions: the rows carry fresh ids per version and nothing
/// links one version's rule to another's. So a rule that was reworded is reported as one
/// <see cref="BrandStyleGuideComparisonState.Removed"/> and one
/// <see cref="BrandStyleGuideComparisonState.Added"/>, and <see cref="BrandStyleGuideComparisonState.Changed"/>
/// is never emitted here. The alternative — pairing unmatched rules by position to call one of them "changed"
/// — would report two unrelated rules as a single edit whenever a rewrite and an insert landed together.
/// </para>
/// <para>
/// <strong>Ranks, not sort orders.</strong> <c>SortOrder</c> is unique across both kinds within a version and
/// can be renumbered without anything moving, so position is reported as a 0-based rank among rules of the
/// <em>same kind</em>. Inserting a don't therefore never moves a do.
/// </para>
/// </remarks>
/// <param name="FromRank">Its rank among same-kind rules in the <c>from</c> version; null when added.</param>
/// <param name="ToRank">Its rank in the <c>to</c> version; null when removed.</param>
public sealed record BrandStyleGuideRuleComparisonServiceModel(
    BrandStyleGuideRuleKind Kind,
    string Text,
    BrandStyleGuideComparisonState State,
    int? FromRank,
    int? ToRank);

/// <summary>
/// One cited source document, compared.
/// </summary>
/// <remarks>
/// <para>
/// Identified by <see cref="DocumentId"/>, so re-pinning a guide from one version of a document to another —
/// the ordinary fix for a stale citation — reads as a single
/// <see cref="BrandStyleGuideComparisonState.Changed"/> rather than as a removal beside an addition. Never
/// <see cref="BrandStyleGuideComparisonState.Moved"/>: citations are a set, not a list.
/// </para>
/// <para>
/// The source-link key is (workspace, guide version, source document version), so a version may legally cite
/// two versions of the same document. Where that happens the cited numbers are sorted ascending on each side
/// and paired by position within that one document, which leaves the ordinary single-citation case as plain
/// "same document, different number". Every candidate in such a pairing concerns the same document, which is
/// what makes it defensible where the same trick on rules would not be.
/// </para>
/// </remarks>
/// <param name="FromVersionNumber">The document version the <c>from</c> side cited; null when added.</param>
/// <param name="ToVersionNumber">The version the <c>to</c> side cites; null when removed.</param>
public sealed record BrandStyleGuideSourceComparisonServiceModel(
    Guid DocumentId,
    BrandStyleGuideComparisonState State,
    int? FromVersionNumber,
    int? ToVersionNumber);

/// <summary>
/// Everything that differs between two versions of one guide, part by part.
/// </summary>
/// <remarks>
/// The three parts are the three collections a version owns, and they are exhaustive: a guide's name, purpose
/// and archived state live on the guide root rather than on a version, so they are outside a comparison of
/// two versions by construction. Every item either side holds appears exactly once in its part, including the
/// unchanged ones, so a client renders a stable frame rather than only a list of edits.
/// </remarks>
public sealed record BrandStyleGuideComparison
{
    public required IReadOnlyList<BrandStyleGuideSectionComparisonServiceModel> Sections { get; init; }

    public required IReadOnlyList<BrandStyleGuideRuleComparisonServiceModel> Rules { get; init; }

    public required IReadOnlyList<BrandStyleGuideSourceComparisonServiceModel> Sources { get; init; }

    /// <summary>False only when every item in every part is <see cref="BrandStyleGuideComparisonState.Unchanged"/>.</summary>
    public bool HasChanges =>
        Sections.Any(section => section.State is not BrandStyleGuideComparisonState.Unchanged)
        || Rules.Any(rule => rule.State is not BrandStyleGuideComparisonState.Unchanged)
        || Sources.Any(source => source.State is not BrandStyleGuideComparisonState.Unchanged);
}

/// <summary>
/// One of the two versions a comparison read, as it describes itself. What it held is in the comparison.
/// </summary>
/// <remarks>
/// Narrower than <see cref="BrandStyleGuideVersionSummaryServiceModel"/>, which answers "what is this guide's
/// history" and carries counts and staleness for a list. This answers "which two versions am I looking at",
/// and the history's other fields answer nothing about that — publishing them here would be fields this route
/// has no reading for, which api-contract.md makes breaking to remove later.
/// </remarks>
public sealed record BrandStyleGuideComparisonSideServiceModel(
    Guid VersionId,
    int VersionNumber,
    BrandStyleGuideVersionStatus Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// Two versions of one guide and everything that differs between them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The comparison is the server's, and it is the only one.</strong> The route returns the answer
/// rather than the two versions, because a client that recomputed a diff from two snapshots could disagree
/// with the server about what changed — and this is the diff an approval will be read against.
/// </para>
/// <para>
/// <strong>Read-only, and not cached.</strong> Both versions are immutable, so this answer is stable forever
/// and is the most cacheable read in the module; it is still not cached, for the reason the guide's other
/// reads give. Nothing here writes: comparing two versions leaves no record that it happened, approves
/// nothing and activates nothing.
/// </para>
/// </remarks>
public sealed record BrandStyleGuideVersionComparisonServiceModel
{
    /// <summary>The version read as <c>from</c>. Not necessarily the earlier one.</summary>
    /// <remarks>
    /// The caller chooses the direction: a creator asking what reverting would cost reads the newer version as
    /// <c>from</c>. The comparison describes the change <em>from</em> this version <em>to</em> the other,
    /// whichever way round their numbers run.
    /// </remarks>
    public required BrandStyleGuideComparisonSideServiceModel From { get; init; }

    /// <inheritdoc cref="From"/>
    public required BrandStyleGuideComparisonSideServiceModel To { get; init; }

    public required BrandStyleGuideComparison Comparison { get; init; }
}
