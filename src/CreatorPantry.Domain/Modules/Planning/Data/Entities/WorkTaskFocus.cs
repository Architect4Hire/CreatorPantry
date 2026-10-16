using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Planning.Data.Entities;

/// <summary>One of the tasks a person chose as their top three for one day (13A.32).</summary>
/// <remarks>
/// <para>
/// <strong>An emphasis, not a state of the task.</strong> Choosing a task changes nothing about it — no status,
/// day, priority or place in its list — so it is a row of its own rather than a field on <see cref="WorkTask"/>,
/// and picking a top three never collides with an edit to one of them.
/// </para>
/// <para>
/// <strong>Keyed on the day.</strong> A choice belongs to the local date it was made for, so tomorrow starts with
/// none and nothing has to clear yesterday's. The rows of earlier days are kept: they are three per person per day
/// at most, and they cascade with their task.
/// </para>
/// <para>
/// <strong>Not cleaned up on a hand-off.</strong> A chosen task handed to someone else simply stops being named: the
/// daily view passes on only ids it shows to the caller. Handed back the same day, it is chosen again — the
/// creator's own choice, still theirs.
/// </para>
/// <para>
/// Interior to its task for deletion, as a label is: a composite foreign key into the task, which cascades, and
/// none to <c>Workspaces</c>, so SQL Server sees one delete path.
/// </para>
/// </remarks>
public class WorkTaskFocus : IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Whose top three this is: the person doing the task, as the day's lists are keyed.</summary>
    public Guid AssigneeMembershipId { get; set; }

    /// <summary>The local day, in the workspace's scheduling zone, the choice was made for.</summary>
    public DateOnly FocusDate { get; set; }

    /// <summary>1, 2 or 3: the order the person put them in.</summary>
    public int Position { get; set; }

    public Guid WorkTaskId { get; set; }
}
