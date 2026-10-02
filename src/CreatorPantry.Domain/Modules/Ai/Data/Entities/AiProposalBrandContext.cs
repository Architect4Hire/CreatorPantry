using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// The brand context one proposal was generated from (11A.20): which guide version and profile revision it was
/// grounded on, what it cost, and the checksum naming the assembled package exactly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A row exists whenever brand context was asked for, even when nothing came back.</strong> A workspace
/// with no profile and no active guide still produces a package — one carrying omissions — and recording that is
/// the point: it is what later explains why a piece reads in no particular voice. Absence of a row means the
/// generation never asked, either because its task type grounds in nothing (see
/// <see cref="BrandContextSelection.SectionKeysFor"/>) or because the creator turned brand voice off.
/// </para>
/// <para>
/// <strong>No guide name, and no excerpt text.</strong> The guide is named by id and version number; a display
/// name here would make this module a second place a brand's names live, and a rename would then have to be
/// chased into AI history. The excerpt text is not copied either — <see cref="Checksum"/> already pins the exact
/// words the package carried, which is the fact an audit needs, and duplicating creator prose into the AI
/// module's tables would add a second copy to erase.
/// </para>
/// <para>
/// <strong>Only ever written beside a proposal.</strong> A failed generation produces no proposal and therefore
/// no row, so this table answers "what grounded this output", not "what was assembled and then thrown away".
/// </para>
/// </remarks>
public class AiProposalBrandContext : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiProposalId { get; set; }

    /// <summary>The channel written for, or null. Null for every writing task built so far; see 11A.20.</summary>
    public string? ChannelKey { get; set; }

    /// <summary>The audience the package resolved to, whatever it came from.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Where <see cref="Audience"/> came from — the request, the profile, a guide section, a channel variant.
    /// </summary>
    /// <remarks>
    /// Recorded rather than inferred, and the reason is the same one the assembler gives for resolving
    /// precedence per field: a creator reading this afterwards cannot tell an audience they typed from one their
    /// brand profile supplied, and those are different facts about the same text.
    /// </remarks>
    public BrandContextOrigin? AudienceOrigin { get; set; }

    /// <summary>The brand profile revision the package read, or null when the workspace has no profile.</summary>
    public int? BrandProfileRevision { get; set; }

    public Guid? BrandGuideId { get; set; }

    public Guid? BrandGuideVersionId { get; set; }

    /// <summary>The version number the creator sees, beside the id the row is keyed on.</summary>
    public int? BrandGuideVersionNumber { get; set; }

    /// <summary>
    /// Whether the version used was the workspace's active one at assembly time.
    /// </summary>
    /// <remarks>
    /// False is not an error: a creator may pin an older version deliberately. It is recorded here and said out
    /// loud as a proposal warning, which together are what keep a stale guide from being used silently.
    /// </remarks>
    public bool GuideWasActiveVersion { get; set; }

    /// <summary>
    /// The package's content checksum, as <see cref="BrandContextSelection.Checksum"/> computed it.
    /// </summary>
    /// <remarks>
    /// Computed after the token budget, so it names what the package actually carried rather than what was
    /// selected before trimming. Two generations sharing it were grounded on identical brand content.
    /// </remarks>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>What the package was estimated to cost. An estimate, and labelled one everywhere it surfaces.</summary>
    public int EstimatedTokens { get; set; }

    /// <summary>How many guide sections survived the budget. Counted because the bodies are not stored.</summary>
    public int GuidanceSectionCount { get; set; }

    /// <summary>How many structured rules travelled. Counted because the texts are not stored.</summary>
    public int RuleCount { get; set; }

    public DateTimeOffset AssembledAt { get; set; }

    /// <summary>
    /// The passages the package cited. Count these for the excerpt total; there is no separate column for it, so
    /// the rows and the number can never disagree.
    /// </summary>
    public ICollection<AiProposalBrandSource> Sources { get; set; } = [];
}
