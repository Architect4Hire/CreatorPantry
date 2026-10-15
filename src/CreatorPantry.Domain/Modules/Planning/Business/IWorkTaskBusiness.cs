using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Workflows.Facade;

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
    IWorkspaceContext workspace,
    IWorkspaceFacade workspaces,
    IBrandProfileFacade brandProfiles,
    IRecipeFacade recipes,
    IContentCardLookupFacade contentCards,
    IMediaAssetLookupFacade mediaAssets,
    IWorkflowRunFacade workflowRuns,
    ITimeZoneConverter zones,
    IClock clock) : IWorkTaskBusiness
{
    /// <summary>
    /// One sentence for every link that could not be used, whichever of the reasons it was. Telling an
    /// absent record from a neighbour's would make capturing a task a way to ask what a neighbour owns.
    /// </summary>
    private const string NotFoundHere = "That is not something this workspace can point a task at.";

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

        if (await RefusedLinksAsync(model, cancellationToken) is { Count: > 0 } refused)
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
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (!await dataLayer.CreateAsync(task, cancellationToken))
        {
            return Failure(new OperationError(
                PlanningErrorCodes.WorkTaskNotSaved,
                "That task could not be saved just then. Nothing was written, so you can send it again as it stands.",
                new Dictionary<string, string[]>()));
        }

        var names = await workspaces.FindMemberDisplayNamesAsync([membershipId], cancellationToken);

        return OperationResult<WorkTaskServiceModel>.Success(ToServiceModel(task, names));
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

        return OperationResult<TodayWorkTasksServiceModel>.Success(new TodayWorkTasksServiceModel
        {
            Date = today,
            TimeZoneId = zone,
            Overdue = Shown(day.Overdue, names),
            OverdueHasMore = WasCut(day.Overdue),
            ScheduledToday = Shown(day.ScheduledToday, names),
            ScheduledTodayHasMore = WasCut(day.ScheduledToday),
            InProgress = Shown(day.InProgress, names),
            InProgressHasMore = WasCut(day.InProgress),
            Waiting = Shown(day.Waiting, names),
            WaitingHasMore = WasCut(day.Waiting),
            CompletedToday = Shown(day.CompletedToday, names),
            CompletedTodayHasMore = WasCut(day.CompletedToday),
            Suggested =
            [
                .. day.Unplanned.Select(task => new SuggestedWorkTaskServiceModel
                {
                    Task = ToServiceModel(task, names),
                    Reason = ReasonFor(task, today),
                }),
            ],
        });

        IReadOnlyList<WorkTaskServiceModel> Shown(IReadOnlyList<WorkTask> group, IReadOnlyDictionary<Guid, string> who) =>
            [.. group.Take(WorkTaskPolicy.TodayGroupMaxRows).Select(task => ToServiceModel(task, who))];

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

    private static WorkTaskServiceModel ToServiceModel(WorkTask task, IReadOnlyDictionary<Guid, string> names) =>
        new()
        {
            Id = task.Id,
            Title = task.Title,
            Description = task.Description,
            Status = task.Status,
            Priority = task.Priority,
            ScheduledDate = task.ScheduledDate,
            PlanningWeekStart = task.PlanningWeekStart,
            DueDate = task.DueDate,
            EstimateMinutes = task.EstimateMinutes,
            Owner = Member(task.OwnerMembershipId, names),
            Assignee = Member(task.AssigneeMembershipId, names),
            Links = new WorkTaskLinksServiceModel
            {
                WorkflowRunId = task.WorkflowRunId,
                WorkflowStepKey = task.WorkflowStepKey,
                RecipeId = task.RecipeId,
                ContentCardId = task.ContentCardId,
                MediaAssetId = task.MediaAssetId,
            },
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            CompletedAt = task.CompletedAt,
            ConcurrencyToken = WorkTaskConcurrencyToken.From(task.RowVersion),
        };

    /// <summary>Null for somebody who has since left the workspace: a task outlives the membership.</summary>
    private static WorkTaskMemberServiceModel Member(Guid membershipId, IReadOnlyDictionary<Guid, string> names) =>
        new() { MembershipId = membershipId, DisplayName = names.GetValueOrDefault(membershipId) };

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

    /// <summary>
    /// Each link, checked through the module that owns the record, in the resolved workspace. Every refusal
    /// is collected rather than returned at the first, so a creator fixes one request instead of several.
    /// </summary>
    private async Task<List<(string Field, string Message)>> RefusedLinksAsync(
        CreateWorkTaskViewModel model, CancellationToken cancellationToken)
    {
        var refused = new List<(string Field, string Message)>();

        if (model.RecipeId is { } recipeId
            && !(await recipes.ListTitlesAsync([recipeId], cancellationToken)).Any(candidate => candidate.Id == recipeId))
        {
            refused.Add(("recipeId", NotFoundHere));
        }

        if (model.ContentCardId is { } cardId && !await contentCards.ExistsAsync(cardId, cancellationToken))
        {
            refused.Add(("contentCardId", NotFoundHere));
        }

        // No version: a task follows the picture as it stands, exactly as it follows a recipe rather than one
        // of its versions.
        if (model.MediaAssetId is { } assetId
            && await mediaAssets.ResolveLinkTargetAsync(assetId, null, cancellationToken) is not MediaAssetLinkTarget.Linkable)
        {
            refused.Add(("mediaAssetId", NotFoundHere));
        }

        if (model.WorkflowRunId is { } runId)
        {
            var run = await workflowRuns.GetAsync(runId, cancellationToken);
            if (!run.Succeeded)
            {
                refused.Add(("workflowRunId", NotFoundHere));
            }
            else if (WorkTaskInputChecks.Normalize(model.WorkflowStepKey) is { } stepKey
                && !run.Value!.Steps.Any(step => string.Equals(step.Key, stepKey, StringComparison.Ordinal)))
            {
                // The run is this workspace's, so naming the step is no disclosure: it says nothing the
                // caller could not read from the run itself.
                refused.Add(("workflowStepKey", "That run has no such step."));
            }
        }

        return refused;
    }

    private static OperationResult<WorkTaskServiceModel> Failure(OperationError error) =>
        OperationResult<WorkTaskServiceModel>.Failure(error);
}
