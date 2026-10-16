using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Data.Entities;

/// <summary>
/// One person's routine — "check comments every morning", "plan the week on Mondays" — that makes a task on each
/// day it comes round (13A.30). Workspace-owned.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A template makes tasks; it never edits them.</strong> Each occurrence is an ordinary task carrying
/// <see cref="WorkTask.WorkTaskTemplateId"/> and <see cref="WorkTask.RecurrenceDate"/>, and the store keeps one
/// per occurrence. Changing, pausing or ending the template changes what is made from now on — never a task
/// already made, open or finished.
/// </para>
/// <para>
/// <strong>Its zone is its own.</strong> <see cref="TimeZoneId"/> is copied from the workspace's brand settings
/// when the template is made and is not read from them again, so a later edit there does not move a routine's
/// days. Occurrences are calendar dates in it; "today" is the only thing an instant decides.
/// </para>
/// <para>
/// <strong>Paused or ended, never deleted.</strong> Its occurrences point at it with a restricted key.
/// </para>
/// </remarks>
public class WorkTaskTemplate : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>Whose routine it is. Each occurrence is theirs, captured by and assigned to them.</summary>
    public Guid OwnerMembershipId { get; set; }

    /// <summary>The title each occurrence gets.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public WorkTaskPriority Priority { get; set; } = WorkTaskPriority.Normal;

    public int? EstimateMinutes { get; set; }

    public WorkTaskRecurrenceFrequency Frequency { get; set; }

    /// <summary>The weekdays a weekly routine comes round on; <see cref="WorkTaskWeekdays.None"/> for a daily one.</summary>
    public WorkTaskWeekdays Weekdays { get; set; }

    public DateOnly StartsOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    /// <summary>An IANA zone, copied when the template was made and never re-read from brand settings.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>
    /// The next date not yet made. Null while it has not been worked out — a template the worker has not yet
    /// seen — and once the template has ended.
    /// </summary>
    public DateOnly? NextOccurrenceDate { get; set; }

    public WorkTaskTemplateStatus Status { get; set; } = WorkTaskTemplateStatus.Active;

    /// <summary>Why it stopped on its own. Set with <see cref="WorkTaskTemplateStatus.NeedsAttention"/> and only with it.</summary>
    public WorkTaskTemplateAttention? AttentionReason { get; set; }

    /// <summary>The worker pass holding the template, while it makes occurrences.</summary>
    public Guid? LeasedBy { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
