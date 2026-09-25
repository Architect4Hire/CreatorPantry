using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// One thing the creator is told alongside a proposal: an assumption the model made, or a caution about what
/// it produced.
/// </summary>
/// <remarks>
/// <para>
/// Immutable. A warning records what was surfaced at review time, and softening or withdrawing one after the
/// fact would make the record of what a creator was shown untrue.
/// </para>
/// <para>
/// <strong>Absence means nothing.</strong> A proposal with no <see cref="AiWarningKind.SafetyCaution"/> has
/// not been found safe — recipes.md and ai.md both forbid reading missing data as a clean result, and that
/// applies to this table as much as to allergen traits.
/// </para>
/// </remarks>
public class AiWarning : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiProposalId { get; set; }

    public AiWarningKind Kind { get; set; }

    /// <summary>What to tell the creator, in prose.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The change this is about, when it is about one. Null for a warning about the proposal as a whole.
    /// </summary>
    public Guid? AiStructuredChangeId { get; set; }

    /// <summary>The order to show these in.</summary>
    public int SortOrder { get; set; }
}
