using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for one person's week of work (13A.26).</summary>
public interface IWorkTaskWeekBusiness
{
    /// <summary>
    /// The caller's own week in the resolved workspace: the week starting on <paramref name="weekStart"/>, or
    /// the one holding today in the workspace's zone when it is null.
    /// </summary>
    Task<OperationResult<WeekWorkTasksServiceModel>> GetWeekAsync(DateOnly? weekStart, CancellationToken cancellationToken);
}

internal sealed class WorkTaskWeekBusiness(
    IWorkTaskWeekDataLayer dataLayer,
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    IBrandProfileFacade brandProfiles,
    ITimeZoneConverter zones,
    IClock clock) : IWorkTaskWeekBusiness
{
    public async Task<OperationResult<WeekWorkTasksServiceModel>> GetWeekAsync(
        DateOnly? weekStart, CancellationToken cancellationToken)
    {
        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (weekStart is { } named && !WeekWorkTasksInputs.IsAllowed(named))
        {
            return OperationResult<WeekWorkTasksServiceModel>.Failure(OperationError.Validation(
                PlanningErrorCodes.WeekInvalid,
                "That is not a week that can be read.",
                [("weekStart", $"Name the week by the {WorkTaskWeek.StartsOn} it starts on.")]));
        }

        var profile = await brandProfiles.GetAsync(cancellationToken);
        var zone = profile.Succeeded ? profile.Value!.TimeZoneId : null;

        // Even a week named outright needs a zone: "finished this week" runs between two local midnights, and a
        // guessed zone would quietly move Sunday evening's work into the next week.
        if (zone is not { Length: > 0 } || !zones.IsValidZone(zone))
        {
            return OperationResult<WeekWorkTasksServiceModel>.Failure(new OperationError(
                PlanningErrorCodes.WeekTimeZoneNotSet,
                "Set your workspace's time zone in brand settings to see your week.",
                new Dictionary<string, string[]>()));
        }

        var today = zones.LocalDate(clock.UtcNow, zone);
        var currentWeek = WorkTaskWeek.StartOf(today);
        var start = weekStart ?? currentWeek;
        var isCurrentWeek = start == currentWeek;

        // The week as instants. Both ends go through the converter rather than being assumed seven days of 24
        // hours apart: a week the clocks spring forward in is 167 hours long, one they fall back in is 169, and
        // in a zone whose change happens at midnight a day does not start at 00:00 at all.
        var startsAt = zones.ToUtc(start, TimeOnly.MinValue, zone).Utc;
        var endsAt = zones.ToUtc(start.AddDays(WorkTaskWeek.Length), TimeOnly.MinValue, zone).Utc;

        var read = await dataLayer.ReadWeekAsync(
            workspace.MembershipId, start, startsAt, endsAt, isCurrentWeek, cancellationToken);

        // One lookup for every person named anywhere in the week.
        var names = await workspaces.FindMemberDisplayNamesAsync(
            [.. new[] { read.Days, read.Backlog, read.Unplanned, read.CarriedOver, read.InProgress, read.Waiting, read.Completed }
                .SelectMany(group => group)
                .SelectMany(task => new[] { task.OwnerMembershipId, task.AssigneeMembershipId })
                .Distinct()],
            cancellationToken);

        var labels = WorkTaskMapper.LabelsByTask(read.Labels);
        var totals = read.Totals;
        var days = Enumerable.Range(0, WorkTaskWeek.Length).Select(offset =>
        {
            var date = start.AddDays(offset);
            var tasks = read.Days.Where(task => task.ScheduledDate == date).ToList();
            var count = totals.Day(date);

            return new WorkTaskWeekDayServiceModel
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                IsToday = date == today,
                Tasks = [.. tasks.Select(task => WorkTaskMapper.ToServiceModel(task, names, labels))],
                HasMore = count.TaskCount > tasks.Count,
                Totals = Totals(count),
            };
        }).ToList();

        var planned = Enumerable.Range(0, WorkTaskWeek.Length)
            .Select(offset => totals.Day(start.AddDays(offset)))
            .Aggregate(totals.Backlog, (sum, day) => sum.Add(day));

        return OperationResult<WeekWorkTasksServiceModel>.Success(new WeekWorkTasksServiceModel
        {
            WeekStart = start,
            WeekEnd = start.AddDays(WorkTaskWeek.Length - 1),
            Today = today,
            TimeZoneId = zone,
            IsCurrentWeek = isCurrentWeek,
            Planned = Totals(planned),
            Days = days,
            Backlog = Group(read.Backlog, totals.Backlog),
            Unplanned = Group(read.Unplanned, totals.Unplanned),
            CarriedOver = Group(read.CarriedOver, totals.CarriedOver),
            InProgress = Group(read.InProgress, totals.InProgress),
            Waiting = Group(read.Waiting, totals.Waiting),
            Completed = Group(read.Completed, totals.Completed),
        });

        WorkTaskGroupServiceModel Group(IReadOnlyList<WorkTask> tasks, WorkTaskCount count) => new()
        {
            Tasks = [.. tasks.Select(task => WorkTaskMapper.ToServiceModel(task, names, labels))],
            HasMore = count.TaskCount > tasks.Count,
            Totals = Totals(count),
        };
    }

    private static WorkTaskTotalsServiceModel Totals(WorkTaskCount count) => new()
    {
        TaskCount = count.TaskCount,
        EstimatedTaskCount = count.EstimatedTaskCount,
        EstimatedMinutes = count.EstimatedMinutes,
    };
}
