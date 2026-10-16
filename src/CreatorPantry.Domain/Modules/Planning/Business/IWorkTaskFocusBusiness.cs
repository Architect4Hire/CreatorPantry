using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for a person's top three (13A.32).</summary>
/// <remarks>
/// <para>
/// <strong>Only what is on their day.</strong> A task may be chosen when it is the caller's and the daily view would
/// show it: late, planned for today, in progress, waiting, or finished today. A finished one stays choosable so that
/// choosing again does not drop what was already ticked off.
/// </para>
/// <para>
/// <strong>Choosing changes no task.</strong> Status, day, priority and place in a list are untouched; the choice
/// is a row of its own, keyed on today.
/// </para>
/// </remarks>
public interface IWorkTaskFocusBusiness
{
    /// <summary>Replaces the caller's top three for today with the tasks named, in that order.</summary>
    /// <returns>
    /// The choice as it now stands, or <see cref="PlanningErrorCodes.TopThreeInvalid"/>,
    /// <see cref="PlanningErrorCodes.TodayTimeZoneNotSet"/>, <see cref="PlanningErrorCodes.TopThreeUnprocessable"/>
    /// or <see cref="PlanningErrorCodes.TopThreeNotSaved"/>.
    /// </returns>
    Task<OperationResult<WorkTaskTopThreeServiceModel>> SetAsync(
        SetWorkTaskTopThreeViewModel model, CancellationToken cancellationToken);
}

internal sealed class WorkTaskFocusBusiness(
    IWorkTaskFocusDataLayer dataLayer,
    IWorkspaceContext workspace,
    IBrandProfileFacade brandProfiles,
    ITimeZoneConverter zones,
    IClock clock) : IWorkTaskFocusBusiness
{
    public async Task<OperationResult<WorkTaskTopThreeServiceModel>> SetAsync(
        SetWorkTaskTopThreeViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (WorkTaskTopThreeInputChecks.Set(model).ToList() is { Count: > 0 } invalid)
        {
            return Failure(OperationError.Validation(
                PlanningErrorCodes.TopThreeInvalid, "That top three could not be read.", invalid));
        }

        var profile = await brandProfiles.GetAsync(cancellationToken);
        var zone = profile.Succeeded ? profile.Value!.TimeZoneId : null;

        // "Today" is the choice's key, so without a zone there is nothing honest to file it under — the daily view's
        // own refusal, with its own remedy.
        if (zone is not { Length: > 0 } || !zones.IsValidZone(zone))
        {
            return Failure(new OperationError(
                PlanningErrorCodes.TodayTimeZoneNotSet,
                "Set your workspace's time zone in brand settings to plan your day.",
                new Dictionary<string, string[]>()));
        }

        var today = zones.LocalDate(clock.UtcNow, zone);
        var startsAt = zones.ToUtc(today, TimeOnly.MinValue, zone).Utc;
        var endsAt = zones.ToUtc(today.AddDays(1), TimeOnly.MinValue, zone).Utc;

        var ids = model.TaskIds!;
        var found = (await dataLayer.TasksAsync(ids, cancellationToken)).ToDictionary(task => task.Id);

        // One answer for every way a task can fail to be on the caller's day, so a top three cannot be used to ask
        // whether an id is somebody else's task or another workspace's.
        if (ids.Any(id => !found.TryGetValue(id, out var task) || !IsOnDay(task, today, startsAt, endsAt)))
        {
            return Failure(OperationError.Validation(
                PlanningErrorCodes.TopThreeUnprocessable,
                "That top three could not be chosen.",
                [("taskIds", "Choose from the tasks on your day.")]));
        }

        if (!await dataLayer.ReplaceAsync(workspace.MembershipId, today, ids, cancellationToken))
        {
            return Failure(new OperationError(
                PlanningErrorCodes.TopThreeNotSaved,
                "Your top three could not be saved just then. Nothing was written, so you can send it again as it stands.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<WorkTaskTopThreeServiceModel>.Success(new WorkTaskTopThreeServiceModel
        {
            Date = today,
            TaskIds = [.. ids],
        });
    }

    /// <summary>
    /// Whether the daily view would show this task to the caller: the same rules as its groups (13A.25), stated
    /// once more here because the choice is judged against tasks one at a time rather than read as lists.
    /// </summary>
    private bool IsOnDay(WorkTask task, DateOnly today, DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        task.AssigneeMembershipId == workspace.MembershipId
        && task switch
        {
            { Status: WorkTaskStatus.Done, CompletedAt: { } completed } => completed >= startsAt && completed < endsAt,
            { Status: WorkTaskStatus.InProgress or WorkTaskStatus.Waiting } => true,
            { DueDate: { } due } when due < today => true,
            { Status: WorkTaskStatus.Backlog, ScheduledDate: { } day } => day == today,
            _ => false,
        };

    private static OperationResult<WorkTaskTopThreeServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskTopThreeServiceModel>.Failure(error);
}
