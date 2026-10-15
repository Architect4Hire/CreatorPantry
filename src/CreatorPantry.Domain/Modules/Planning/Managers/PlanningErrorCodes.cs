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
    public const string TodayTimeZoneNotSet = "planning.today.time_zone_not_set.unprocessable";
}
