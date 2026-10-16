namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>The stable codes the planning routes refuse with.</summary>
public static class PlanningErrorCodes
{
    /// <summary>
    /// The task could not be read as described: no title, a title or description past its length, a priority
    /// that is not one, a week that does not start on a <see cref="WorkTaskWeek.StartsOn"/>, a day outside its
    /// week, or a step key with no run. Carries field errors.
    /// </summary>
    public const string WorkTaskInvalid = "planning.work_task.invalid";

    /// <summary>The caller's role may read this workspace but not keep work in it.</summary>
    public const string WorkTaskForbidden = "planning.work_task.forbidden";

    /// <summary>
    /// A record the task points at cannot be linked.
    /// </summary>
    /// <remarks>
    /// <strong>One code and one sentence whether the record never existed, can no longer be used, or belongs
    /// to another workspace</strong>, so capturing a task cannot be used to ask what a neighbour owns. 422
    /// rather than 400 by the suffix rule, and that is the right reading: the request is shaped correctly and
    /// the server simply cannot accept what it names. Field errors say which link failed.
    /// </remarks>
    public const string WorkTaskLinkUnprocessable = "planning.work_task.link.unprocessable";

    /// <summary>The task could not be written just then and nothing was. Safe to send again as it stands.</summary>
    public const string WorkTaskNotSaved = "planning.work_task.conflict";

    /// <summary>
    /// The daily view was asked for in a workspace that has set no scheduling zone (13A.25).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without a zone there is no honest answer to "what is today", and a guessed day boundary does not fail
    /// visibly — it quietly files a creator's evening work under tomorrow. 422 rather than 400 by the suffix
    /// rule, and that is the right reading: the request is shaped correctly and the server cannot answer it.
    /// The remedy is a setting, so the message names brand settings rather than anything about the request.
    /// </para>
    /// </remarks>
    /// <summary>No such task, another workspace's, or one the caller neither owns nor is doing (13A.27).</summary>
    public const string WorkTaskNotFound = "planning.work_task.not_found";

    /// <summary>The task has changed since the read an edit was composed against (13A.27).</summary>
    public const string WorkTaskStale = "planning.work_task.stale.conflict";

    /// <summary>A newly added label that is not an active tag of this workspace (13A.27).</summary>
    public const string WorkTaskLabelUnprocessable = "planning.work_task.label.unprocessable";

    /// <summary>Somebody the task cannot be handed to, or a hand-off the caller may not make (13A.27).</summary>
    public const string WorkTaskAssigneeUnprocessable = "planning.work_task.assignee.unprocessable";

    /// <summary>A finished task, which is reopened rather than moved (13A.28).</summary>
    public const string WorkTaskCompleted = "planning.work_task.completed.conflict";

    /// <summary>A move placed before a task that is not in the list it is moving to (13A.28).</summary>
    public const string WorkTaskMoveUnprocessable = "planning.work_task.move.unprocessable";

    /// <summary>A task that is not finished, so there is nothing to reopen (13A.28b).</summary>
    public const string WorkTaskOpen = "planning.work_task.open.conflict";

    /// <summary>A workflow step with nothing left to plan: the run is over, or the step is done or passed (13A.29).</summary>
    public const string WorkTaskStepUnprocessable = "planning.work_task.step.unprocessable";

    public const string TodayTimeZoneNotSet = "planning.today.time_zone_not_set.unprocessable";

    /// <summary>A top three that could not be read: more than three, one named twice, or not an id (13A.32).</summary>
    public const string TopThreeInvalid = "planning.top_three.invalid";

    /// <summary>
    /// A top three naming a task that is not on the caller's day (13A.32): one answer whether it is finished on an
    /// earlier day, planned for another, somebody else's, another workspace's, or was never a task.
    /// </summary>
    public const string TopThreeUnprocessable = "planning.top_three.unprocessable";

    /// <summary>The top three could not be written just then and nothing was. Safe to send again as it stands.</summary>
    public const string TopThreeNotSaved = "planning.top_three.conflict";

    /// <summary>A week asked for by a day that is not a Monday, or not written as yyyy-MM-dd (13A.26).</summary>
    public const string WeekInvalid = "planning.week.invalid";

    /// <summary>The weekly view, in a workspace with no scheduling zone to read the week's edges in (13A.26).</summary>
    public const string WeekTimeZoneNotSet = "planning.week.time_zone_not_set.unprocessable";
}
