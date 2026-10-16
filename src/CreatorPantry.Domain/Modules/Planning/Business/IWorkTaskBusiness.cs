using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for one person's work tasks (13A.24).</summary>
public interface IWorkTaskBusiness
{
    /// <summary>Captures one task for the caller in the resolved workspace.</summary>
    Task<OperationResult<WorkTaskServiceModel>> CreateAsync(
        CreateWorkTaskViewModel model, CancellationToken cancellationToken);

    /// <summary>The caller's own day in the resolved workspace (13A.25).</summary>
    Task<OperationResult<TodayWorkTasksServiceModel>> GetTodayAsync(
        bool includeSuggestions, CancellationToken cancellationToken);
}

internal sealed class WorkTaskBusiness(
    IWorkTaskDataLayer dataLayer,
    IWorkTaskFocusDataLayer focus,
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    IBrandProfileFacade brandProfiles,
    WorkTaskLinkChecks links,
    ITimeZoneConverter zones,
    IClock clock) : IWorkTaskBusiness
{
    private const string CannotCapture = "That task could not be captured as described.";

    public async Task<OperationResult<WorkTaskServiceModel>> CreateAsync(
        CreateWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a worker or plugin that reached Business without the facade's validator.
        if (WorkTaskInputChecks.Create(model).ToList() is { Count: > 0 } invalid)
        {
            return Failure(OperationError.Validation(PlanningErrorCodes.WorkTaskInvalid, CannotCapture, invalid));
        }

        var linking = new WorkTaskLinkRequest(
            model.RecipeId,
            model.ContentCardId,
            model.MediaAssetId,
            model.WorkflowRunId,
            WorkTaskInputChecks.Normalize(model.WorkflowStepKey));

        // A label is a tag the workspace offers now: absent, retired and another workspace's all read the same.
        var labelIds = model.LabelIds ?? [];
        if (labelIds.Count > 0)
        {
            var active = await dataLayer.ActiveTagIdsAsync(labelIds, cancellationToken);
            if (labelIds.Any(id => !active.Contains(id)))
            {
                return Failure(OperationError.Validation(
                    PlanningErrorCodes.WorkTaskLabelUnprocessable,
                    CannotCapture,
                    [("labelIds", "One of those is not a tag of this workspace that a task can be given.")]));
            }
        }

        if (await links.RefusedAsync(linking, cancellationToken) is { Count: > 0 } refused)
        {
            return Failure(OperationError.Validation(
                PlanningErrorCodes.WorkTaskLinkUnprocessable, CannotCapture, refused));
        }

        var now = clock.UtcNow;
        var membershipId = workspace.MembershipId;

        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = membershipId,

            // The owner, until a hand-off says otherwise: "my tasks" has to answer on the day it is captured.
            AssigneeMembershipId = membershipId,
            Title = WorkTaskInputChecks.Normalize(model.Title)!,
            Description = WorkTaskInputChecks.Normalize(model.Description),

            // Never the client's. Capture means "not started", and a quick capture that could arrive already
            // in progress would make the status mean something other than where the work stands.
            Status = WorkTaskStatus.Backlog,
            Priority = model.Priority ?? WorkTaskPriority.Normal,
            ScheduledDate = model.ScheduledDate,
            PlanningWeekStart = await PlanningWeekAsync(model, cancellationToken),
            WorkflowRunId = model.WorkflowRunId,
            WorkflowStepKey = WorkTaskInputChecks.Normalize(model.WorkflowStepKey),
            RecipeId = model.RecipeId,
            ContentCardId = model.ContentCardId,
            MediaAssetId = model.MediaAssetId,
            EstimateMinutes = model.EstimateMinutes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (!await dataLayer.CreateAsync(task, labelIds, cancellationToken))
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotSaved,
                "That task could not be saved just then. Nothing was written, so you can send it again as it stands.",
                new Dictionary<string, string[]>()));
        }

        var names = await workspaces.FindMemberDisplayNamesAsync([membershipId], cancellationToken);

        var labels = labelIds.Count == 0
            ? WorkTaskMapper.NoLabels
            : WorkTaskMapper.LabelsByTask(await dataLayer.LabelsForAsync([task.Id], cancellationToken));

        return OperationResult<WorkTaskServiceModel>.Success(WorkTaskMapper.ToServiceModel(task, names, labels));
    }

    public async Task<OperationResult<TodayWorkTasksServiceModel>> GetTodayAsync(
        bool includeSuggestions, CancellationToken cancellationToken)
    {
        var profile = await brandProfiles.GetAsync(cancellationToken);
        var zone = profile.Succeeded ? profile.Value!.TimeZoneId : null;

        // Without a zone there is no honest "today". Guessing one does not fail visibly: it files an
        // evening's work under tomorrow and the creator never learns why their day looked wrong.
        if (zone is not { Length: > 0 } || !zones.IsValidZone(zone))
        {
            return OperationResult<TodayWorkTasksServiceModel>.Failure(new OperationError(
                PlanningErrorCodes.TodayTimeZoneNotSet,
                "Set your workspace's time zone in brand settings to see your day.",
                new Dictionary<string, string[]>()));
        }

        var today = zones.LocalDate(clock.UtcNow, zone);

        // The day as instants, which is what "finished today" needs. The end is the next day's midnight and
        // is exclusive, so a task completed exactly at midnight belongs to one day only. Both go through the
        // converter rather than being assumed 24 hours apart: a day that springs forward is 23 hours long,
        // and in a zone whose change happens at midnight the day does not start at 00:00 at all.
        var startsAt = zones.ToUtc(today, TimeOnly.MinValue, zone).Utc;
        var endsAt = zones.ToUtc(today.AddDays(1), TimeOnly.MinValue, zone).Utc;

        var day = await dataLayer.ReadDayAsync(
            workspace.MembershipId, today, startsAt, endsAt, includeSuggestions, cancellationToken);

        // One lookup for every person named anywhere on the page: the caller is the assignee throughout, but
        // a task handed to them was captured by somebody else (13A.27).
        var names = await workspaces.FindMemberDisplayNamesAsync(
            [.. new[] { day.Overdue, day.ScheduledToday, day.InProgress, day.Waiting, day.CompletedToday, day.Unplanned }
                .SelectMany(group => group)
                .SelectMany(task => new[] { task.OwnerMembershipId, task.AssigneeMembershipId })
                .Distinct()],
            cancellationToken);

        var labels = WorkTaskMapper.LabelsByTask(day.Labels);

        var overdue = Shown(day.Overdue, names);
        var scheduledToday = Shown(day.ScheduledToday, names);
        var inProgress = Shown(day.InProgress, names);
        var waiting = Shown(day.Waiting, names);
        var completedToday = Shown(day.CompletedToday, names);

        // The choice names tasks; only those the page shows are passed on, so a pointer never dangles.
        var shown = new[] { overdue, scheduledToday, inProgress, waiting, completedToday }
            .SelectMany(group => group)
            .Select(task => task.Id)
            .ToHashSet();
        var chosen = await focus.ChosenAsync(workspace.MembershipId, today, cancellationToken);

        return OperationResult<TodayWorkTasksServiceModel>.Success(new TodayWorkTasksServiceModel
        {
            Date = today,
            TimeZoneId = zone,
            Overdue = overdue,
            OverdueHasMore = WasCut(day.Overdue),
            ScheduledToday = scheduledToday,
            ScheduledTodayHasMore = WasCut(day.ScheduledToday),
            InProgress = inProgress,
            InProgressHasMore = WasCut(day.InProgress),
            Waiting = waiting,
            WaitingHasMore = WasCut(day.Waiting),
            CompletedToday = completedToday,
            CompletedTodayHasMore = WasCut(day.CompletedToday),
            TopThree = [.. chosen.Where(shown.Contains)],
            Suggested =
            [
                .. day.Unplanned.Select(task => new SuggestedWorkTaskServiceModel
                {
                    Task = WorkTaskMapper.ToServiceModel(task, names, labels),
                    Reason = ReasonFor(task, today),
                }),
            ],
        });

        IReadOnlyList<WorkTaskServiceModel> Shown(IReadOnlyList<WorkTask> group, IReadOnlyDictionary<Guid, string> who) =>
            [.. group.Take(WorkTaskPolicy.TodayGroupMaxRows).Select(task => WorkTaskMapper.ToServiceModel(task, who, labels))];

        // The data layer asked for one more than it will show, so a longer list means there were others.
        static bool WasCut(IReadOnlyList<WorkTask> group) => group.Count > WorkTaskPolicy.TodayGroupMaxRows;
    }

    /// <summary>
    /// Why an unplanned task is worth a look. Derived from the task's own fields and nothing else, so the
    /// same task always reads the same way.
    /// </summary>
    /// <remarks>
    /// No date is quoted: a sentence is not the place to format one, and the task beside it carries its own
    /// <c>dueDate</c> for a client that wants to show it.
    /// </remarks>
    private static string ReasonFor(WorkTask task, DateOnly today) => task switch
    {
        // Not yet late — the overdue group has taken those already — but close enough to say so.
        { DueDate: { } due } when due <= today.AddDays(SoonInDays) => "Due soon, and not planned for a day yet.",
        { Priority: WorkTaskPriority.High } => "High priority, and not planned for a day yet.",
        _ => "Next in your backlog.",
    };

    /// <summary>How far ahead a due date still counts as soon, for a suggestion's wording.</summary>
    private const int SoonInDays = 7;

    /// <summary>
    /// The week the task belongs to: the one the creator named, the one holding the day they named, or — for
    /// a capture that named neither — the one holding today where the workspace keeps its calendar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "current planning context" a quick capture defaults to (13A.24). The task still has no
    /// day, so it is in that week's unscheduled backlog and on nobody's daily list: capture never quietly
    /// commits someone to doing the thing today.
    /// </para>
    /// <para>
    /// <strong>A workspace with no scheduling zone set gets no week rather than a guessed one.</strong> There
    /// is no honest "today" without a zone, and refusing the capture over a setting would break the one thing
    /// this command is for. Such a task is in no week until someone plans it, which is why the weekly view
    /// (13A.26) has to show the tasks with no week beside the selected week's own.
    /// </para>
    /// </remarks>
    private async Task<DateOnly?> PlanningWeekAsync(CreateWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        if (model.PlanningWeekStart is { } named)
        {
            return named;
        }

        if (model.ScheduledDate is { } day)
        {
            return WorkTaskWeek.StartOf(day);
        }

        var profile = await brandProfiles.GetAsync(cancellationToken);
        var zone = profile.Succeeded ? profile.Value!.TimeZoneId : null;

        return zone is { Length: > 0 } && zones.IsValidZone(zone)
            ? WorkTaskWeek.StartOf(zones.LocalDate(clock.UtcNow, zone))
            : null;
    }

    private static OperationResult<WorkTaskServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskServiceModel>.Failure(error);
}
