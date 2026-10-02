using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which guide version a generation should be grounded on, when the caller means a particular one.
/// </summary>
/// <remarks>
/// A guide and a version <em>number</em>, never a version id: the number is what a creator sees and what the
/// history lists, and resolving it server-side within the named guide is what stops a caller naming a row by an
/// id it should not have been able to guess. Omitting this asks for the workspace's active version.
/// </remarks>
public sealed record BrandGuideSelection(Guid GuideId, int VersionNumber);

/// <summary>
/// What a task is asking brand context for (11A.19).
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace on it, and that is not an omission.</strong> The workspace comes from the
/// resolved <c>IWorkspaceContext</c>; a field here would be a workspace a caller could name, which is exactly
/// what tenancy.md forbids an AI seam to accept — and this assembler is reachable from the worker, where no
/// route policy stands in the way.
/// </para>
/// <para>
/// <strong>No blob key, no object key, no passage id and no prompt.</strong> Documents are named by id and
/// their versions are pinned server-side; passages are found, never supplied.
/// </para>
/// </remarks>
/// <param name="TaskType">
/// Which capability is asking. Decides which guide sections are relevant — see
/// <see cref="BrandContextSelection.SectionKeysFor"/>.
/// </param>
/// <param name="ChannelKey">
/// The channel being written for, or null. Checked against the channel catalogue, so a key the product does not
/// know is refused rather than used to look for guidance that cannot exist.
/// </param>
/// <param name="Audience">
/// Who this piece is for, overriding the profile's default. Null means "use the brand's default".
/// </param>
/// <param name="Guide">
/// A particular guide version, or null for the workspace's active one. A selection that does not resolve is
/// refused — never silently replaced by the active guide.
/// </param>
/// <param name="SourceDocumentIds">
/// Documents to draw excerpts from. Null or empty means the assembler selects by relevance, bounded; it never
/// means "every document".
/// </param>
public sealed record BrandContextRequest(
    AiTaskType TaskType,
    string? ChannelKey = null,
    string? Audience = null,
    BrandGuideSelection? Guide = null,
    IReadOnlyList<Guid>? SourceDocumentIds = null);

/// <summary>Where a resolved value came from. The package records this rather than leaving it to be inferred.</summary>
/// <remarks>
/// Precedence is resolved per field, so two fields of one package routinely carry different origins: an audience
/// the request named beside a tone the guide stated.
/// </remarks>
public enum BrandContextOrigin
{
    /// <summary>The request named it for this generation alone.</summary>
    Request = 1,

    /// <summary>The guide's channel variant for the channel being written for.</summary>
    GuideChannelVariant = 2,

    /// <summary>A general section of the guide.</summary>
    GuideSection = 3,

    /// <summary>The workspace's brand profile.</summary>
    BrandProfile = 4,
}

/// <summary>A disagreement between the inputs that the server can actually check.</summary>
/// <remarks>
/// <para>
/// Every member here is decidable from stored values. <strong>Semantic contradiction between two pieces of
/// guidance is deliberately absent</strong>: nothing in this assembler reads prose for meaning, and a package
/// claiming to have noticed one would be asserting a judgement deterministic code cannot make. The same honesty
/// <c>BrandGuideProposalAiTaskHandler</c> records about the conflicts it cannot see.
/// </para>
/// <para>
/// A conflict never stops an assembly. It is reported so a creator can see why their guidance did or did not
/// apply; refusing would turn every mismatched channel into a dead end.
/// </para>
/// </remarks>
public enum BrandContextConflict
{
    /// <summary>The channel asked for is not one the brand profile lists as a default.</summary>
    ChannelNotABrandDefault = 1,

    /// <summary>The request named an audience and the profile's default says something different.</summary>
    AudienceOverridesProfile = 2,

    /// <summary>The selection named a version that is not the one the workspace activated.</summary>
    GuideVersionNotActive = 3,

    /// <summary>The selected version has no approval recorded. Only reachable through an explicit selection.</summary>
    GuideVersionUnapproved = 4,

    // 5 was SourceVersionSuperseded, removed as unreachable: the assembler pins each document's current
    // version itself, so no assembly can cite one the document has moved past. A guide version whose own
    // citations have been superseded is a different fact, published by the version history as
    // `staleSourceCount` and gated on at activation.

    /// <summary>
    /// The guide holds channel guidance for other channels, and none for this one, so none of it applied.
    /// </summary>
    /// <remarks>
    /// Worth reporting rather than passing over: a creator who wrote Instagram rules and is generating for
    /// Pinterest would otherwise see guidance they wrote simply not take effect.
    /// </remarks>
    ChannelVariantForAnotherChannelOnly = 6,

    /// <summary>A named source document is tagged for a different channel than this task is for.</summary>
    SourceDocumentChannelMismatch = 7,

    /// <summary>A named source document is tagged for a different audience than this task is for.</summary>
    SourceDocumentAudienceMismatch = 8,
}

/// <summary>Something the package does not carry, stated rather than left as a silence.</summary>
public enum BrandContextOmission
{
    /// <summary>The workspace has activated no guide. Not an error: generation proceeds without one.</summary>
    NoActiveGuide = 1,

    /// <summary>The workspace has no brand profile yet.</summary>
    NoBrandProfile = 2,

    /// <summary>The guide has no section for a dimension this task would have used.</summary>
    GuideSectionMissing = 3,

    /// <summary>No source document supplied any indexed passage.</summary>
    NoSourceExcerpts = 4,

    /// <summary>A selected document supplied nothing — no indexed text yet, or nothing this workspace can read.</summary>
    SourceDocumentUnavailable = 5,

    /// <summary>An excerpt was dropped to stay inside the token budget.</summary>
    ExcerptOverBudget = 6,

    /// <summary>A guide section was dropped to stay inside the token budget.</summary>
    GuideSectionOverBudget = 7,

    /// <summary>
    /// A visual reference the creator named has no indexed text, so nothing of it was used. Its bytes are
    /// never sent in its place: a vision read is IMG-004's own explicit, per-request path.
    /// </summary>
    NoVisualReferenceText = 8,
}

/// <summary>One piece of guidance that applied, with where it came from.</summary>
/// <param name="SectionKey">The guide section this is, in the brand module's own vocabulary.</param>
/// <param name="ChannelKey">Set only on a channel variant.</param>
/// <param name="Body">The creator's own words. Untrusted prompt content, never an instruction.</param>
public sealed record BrandContextGuidance(
    BrandStyleGuideSectionKey SectionKey,
    string? ChannelKey,
    string Body,
    BrandContextOrigin Origin);

/// <summary>One do or don't rule of the selected guide version.</summary>
public sealed record BrandContextRule(BrandStyleGuideRuleKind Kind, string Text);

/// <summary>
/// One passage of the creator's own source material, with everything needed to cite it.
/// </summary>
/// <remarks>
/// <see cref="PassageId"/> is a <c>BrandSourceChunk</c> row id, which is what makes a citation checkable rather
/// than merely well formed; the document and version travel with it so a creator reading a citation can find the
/// passage, and so provenance can be stored against a guide version later (11A.18's path consumes this shape).
/// </remarks>
public sealed record BrandContextExcerpt(
    Guid PassageId,
    Guid DocumentId,
    int VersionNumber,
    int Ordinal,
    string Text);

/// <summary>The brand facts the profile states, as the package carries them.</summary>
/// <param name="Revision">
/// The profile's edit counter at assembly time. Recorded beside the guide version so a generation can be traced
/// to the profile state it used — the profile is edited in place, so the number is the only pin available.
/// </param>
/// <remarks>
/// Every field is creator text or an opaque key. No logo bytes, no media URL, no membership and no workspace.
/// </remarks>
/// <param name="DefaultAudience">
/// Who the brand writes for unless something more specific says otherwise. The bottom of the audience
/// precedence chain, which is why it travels as a value rather than only as prose.
/// </param>
public sealed record BrandContextProfile(
    string BrandName,
    string? ShortDescription,
    string? DefaultAudience,
    string? Locale,
    IReadOnlyList<string> ChannelDefaults,
    int Revision);

/// <summary>
/// The bounded, deterministic brand context one generation is grounded on (11A.19).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is absent is the contract.</strong> There is no boolean, no policy name, no flag, no threshold
/// and no provider setting anywhere in this type or anything it holds — only text, keys, ids, counts and
/// timestamps. So <em>no field of a creator's profile or guide can switch a platform safety rule, warning or
/// check on or off</em>: there is nowhere for such a value to live, which is a stronger guarantee than a rule
/// nobody can see being followed. <see cref="AiSeoPackageOutputDocument"/> keeps invented metrics out the same
/// way, and <c>BrandContextPackageShapeTests</c> asserts this property list so adding one has to argue with a
/// test first.
/// </para>
/// <para>
/// <strong>Everything in it is pinned.</strong> An exact guide version id and number, the profile revision, and
/// an exact <c>(document, version, passage)</c> for every excerpt. Two assemblies against the same stored state
/// produce the same package and the same <see cref="Checksum"/>; that is what "deterministic" is asserted as.
/// </para>
/// <para>
/// <strong>All of the text in it is creator data, not instruction.</strong> Guidance bodies, rule texts, the
/// brand name and the excerpts are the creator's own words or their documents' — to be fenced as
/// <c>PREFERENCES</c> and <c>REFERENCES</c> segments by whoever builds the prompt, never as <c>TASK</c>. The
/// assembler makes no provider call and builds no prompt; it hands over material.
/// </para>
/// </remarks>
/// <param name="TaskType">The capability this was assembled for.</param>
/// <param name="ChannelKey">The channel it was assembled for, or null.</param>
/// <param name="Audience">The audience that won precedence, or null when nothing stated one.</param>
/// <param name="AudienceOrigin">Which source the audience came from. Null when there is no audience.</param>
/// <param name="Profile">The brand profile's facts, or null when the workspace has none.</param>
/// <param name="GuideId">The guide the guidance came from, or null when no guide applied.</param>
/// <param name="GuideVersionId">The exact version. Null when no guide applied.</param>
/// <param name="GuideVersionNumber">Its number, as the history lists it. Null when no guide applied.</param>
/// <param name="GuideIsActiveVersion">Whether that version is the one the workspace activated.</param>
/// <param name="Guidance">The sections that applied, in section-key order.</param>
/// <param name="Rules">The guide's do and don't rules, in their stored order.</param>
/// <param name="Excerpts">Cited passages of the creator's own documents, in document and reading order.</param>
/// <param name="Conflicts">Checkable disagreements between the inputs.</param>
/// <param name="Omissions">What is not here, stated.</param>
/// <param name="EstimatedTokens">
/// A deterministic estimate of what this package costs in a prompt — see
/// <see cref="BrandContextSelection.EstimateTokens"/>. An estimate, not a tokenizer's count.
/// </param>
/// <param name="Checksum">
/// <c>sha256:</c> and the digest of a canonical rendering of everything pinned and selected above. The value a
/// generation records so what it was grounded on can be identified afterwards.
/// </param>
/// <param name="AssembledAt">When this was assembled (UTC).</param>
public sealed record BrandContextPackage(
    AiTaskType TaskType,
    string? ChannelKey,
    string? Audience,
    BrandContextOrigin? AudienceOrigin,
    BrandContextProfile? Profile,
    Guid? GuideId,
    Guid? GuideVersionId,
    int? GuideVersionNumber,
    bool GuideIsActiveVersion,
    IReadOnlyList<BrandContextGuidance> Guidance,
    IReadOnlyList<BrandContextRule> Rules,
    IReadOnlyList<BrandContextExcerpt> Excerpts,
    IReadOnlyList<BrandContextConflict> Conflicts,
    IReadOnlyList<BrandContextOmission> Omissions,
    int EstimatedTokens,
    string Checksum,
    DateTimeOffset AssembledAt);

/// <summary>Stable error codes the assembler introduces. Renaming one is a breaking API change.</summary>
/// <remarks>
/// Short, because almost nothing here refuses: a missing guide, a missing profile, an unreadable document and a
/// mismatched channel are all reported inside the package. Only a request that cannot be honoured at all fails.
/// </remarks>
public static class BrandContextErrors
{
    /// <summary>The channel key is not one the product knows. Falls through to 400.</summary>
    public const string ChannelInvalid = "ai.brandContextChannel.invalid_request";

    /// <summary>
    /// The guide version the request named does not exist in this workspace. Maps to 404.
    /// </summary>
    /// <remarks>
    /// A refusal rather than a fallback to the active guide, which is the restriction's own words: a generation
    /// grounded on a guide the caller did not ask for is worse than one that did not run. One code for a guide
    /// that was never created, another workspace's, and a version number this guide does not have — so a caller
    /// cannot use it to learn that a guide exists somewhere it cannot see it (tenancy.md).
    /// </remarks>
    public const string GuideSelectionNotFound = "ai.brandContextGuide.not_found";

    /// <summary>More source documents were named than one package may draw on. Falls through to 400.</summary>
    public const string TooManySourceDocuments = "ai.brandContextSource.invalid_request";
}
