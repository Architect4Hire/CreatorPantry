using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// What one piece of creative work is about: the creator's own words for it, the channels it is for, the day
/// it belongs to, and the records it draws on (baseline B-31).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Private creator content.</strong> Workspace-owned, carrying the global query filter like every other
/// creator record. <see cref="WorkingTitle"/> and <see cref="PictureBrief"/> are the creator's words and are
/// never logged and never put in an audit summary.
/// </para>
/// <para>
/// <strong>It points; it never copies.</strong> A context holds no recipe text, no image bytes and no prompt
/// body — each <see cref="CreativeContextReference"/> names a record by id and the owning module stays the only
/// source of truth for it (content.md). <c>CreativeContextModelShapeTests</c> fails on a column that would hold
/// a copy.
/// </para>
/// <para>
/// <strong>No step and no progress.</strong> Where a creator has got to in a journey belongs to 13A's
/// <c>WorkflowRun</c>, which links here by id. This row says what the work is about, and that is the same
/// answer whichever screen is asking.
/// </para>
/// <para>
/// <strong>Archived, not removed.</strong> <see cref="ArchivedAt"/> takes a context out of the recent list
/// while leaving it readable by anything that already names it.
/// </para>
/// </remarks>
public class CreativeContext : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The creator's own working name for this piece of work. Optional, and theirs to word.</summary>
    public string? WorkingTitle { get; set; }

    /// <summary>The picture the creator has in mind, in their words. Optional; never rewritten by the system.</summary>
    public string? PictureBrief { get; set; }

    /// <summary>
    /// What the creator chose to work from — their own description, the idea they picked, or both — or null
    /// until they have chosen.
    /// </summary>
    /// <remarks>
    /// Recorded rather than inferred from <see cref="WorkingBrief"/>: a brief that reads exactly like the
    /// description may still be one they combined and then cut back, and a resumed run has to show the choice
    /// they made, not a guess at it.
    /// </remarks>
    public CreativeContextBriefSource? BriefSource { get; set; }

    /// <summary>
    /// The brief the creator chose to work from, as they last left it, or null until they have chosen.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PictureBrief"/> on purpose. That is what they wrote before any idea was
    /// suggested, and no choice made afterwards touches it. This is what the looks are planned from — that
    /// description, a picked idea's wording, or the two together — and is theirs to edit from then on. Private
    /// creator content like the other two: never logged, never in an audit summary.
    /// </remarks>
    public string? WorkingBrief { get; set; }

    /// <summary>The day of the week this work is for, or null.</summary>
    public DayOfWeek? Day { get; set; }

    /// <summary>
    /// The weekly theme this work belongs to, as a <c>WorkspaceWeeklyTheme.Key</c>, or null.
    /// </summary>
    /// <remarks>
    /// A weak reference by key and deliberately not a foreign key, which is the contract
    /// <see cref="WorkspaceWeeklyTheme"/> documents for every consumer: a retired theme still resolves, and a
    /// deleted one resolves to nothing rather than breaking the record that named it. The write seam validates
    /// the key through the weekly-theme facade.
    /// </remarks>
    public string? WeeklyThemeKey { get; set; }

    /// <summary>When the context left the recent list, or null while it is live.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Not a foreign key, matching every other root here: authorship outlives membership.</summary>
    public Guid CreatedByMembershipId { get; set; }

    /// <summary>The optimistic concurrency token the context's edits check.</summary>
    public byte[] RowVersion { get; set; } = [];

    public List<CreativeContextChannel> Channels { get; set; } = [];

    public List<CreativeContextReference> References { get; set; } = [];
}
