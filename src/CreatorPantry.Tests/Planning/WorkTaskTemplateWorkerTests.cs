using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Planning;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Facade;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// The routine worker end to end (13A.30): claim, resolve the workspace as the service, make a week of occurrences
/// in the routine's own zone — once — and stop rather than guess when it cannot go on. Run on both engines.
/// </summary>
public abstract class WorkTaskTemplateWorkerContract : IAsyncLifetime
{
    protected static readonly Guid WorkspaceA = Guid.NewGuid();

    protected static readonly Guid WorkspaceB = Guid.NewGuid();

    protected static readonly Guid OwnerA = Guid.NewGuid();

    protected static readonly Guid ViewerA = Guid.NewGuid();

    protected static readonly Guid OwnerB = Guid.NewGuid();

    private const string Chicago = "America/Chicago";

    /// <summary>Thursday 14 October 2038, lunchtime in Chicago: an ordinary week with no clock change in it.</summary>
    private static readonly DateTimeOffset ThursdayNoon = new(2038, 10, 14, 17, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Thursday = new(2038, 10, 14);

    private readonly FakeTimeProvider _clock = new(ThursdayNoon);

    private ServiceProvider? _provider;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The engine: the context options, and whatever creating its schema takes.</summary>
    protected abstract Task<Action<DbContextOptionsBuilder>> StartEngineAsync();

    protected abstract Task CreateSchemaAsync(CreatorPantryDbContext db);

    protected abstract ValueTask StopEngineAsync();

    public async ValueTask InitializeAsync()
    {
        var options = await StartEngineAsync();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(_clock)
            .AddApplicationTime()
            .AddTenancy()
            .AddAudit()
            .AddPlanningRecurrence()
            .AddDbContext<CreatorPantryDbContext>(options)
            .BuildServiceProvider(validateScopes: true);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await CreateSchemaAsync(db);

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = ThursdayNoon },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = ThursdayNoon });

        // WorkspaceMembership.UserId is a real key into AspNetUsers, so each membership needs a user row.
        db.Users.AddRange(User("owner-a"), User("viewer-a"), User("owner-b"));
        db.WorkspaceMemberships.AddRange(
            Membership(OwnerA, WorkspaceA, "owner-a", WorkspaceRole.Contributor),
            Membership(ViewerA, WorkspaceA, "viewer-a", WorkspaceRole.Viewer),
            Membership(OwnerB, WorkspaceB, "owner-b", WorkspaceRole.Contributor));
        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await StopEngineAsync();
    }

    // ---- Making a week ----

    [Fact]
    public async Task A_daily_routine_makes_today_and_the_six_days_after_and_a_second_pass_makes_nothing_more()
    {
        var routine = await SeedAsync(WorkspaceA, Daily(OwnerA));

        await RunAsync();
        await RunAsync();

        var tasks = await OccurrencesAsync(WorkspaceA, routine.Id);
        Assert.Equal(Enumerable.Range(0, 7).Select(Thursday.AddDays), tasks.Select(task => task.RecurrenceDate!.Value));
        Assert.All(tasks, task =>
        {
            Assert.Equal(task.RecurrenceDate, task.ScheduledDate);
            Assert.Equal(WorkTaskWeek.StartOf(task.ScheduledDate!.Value), task.PlanningWeekStart);
            Assert.Equal(OwnerA, task.OwnerMembershipId);
            Assert.Equal(OwnerA, task.AssigneeMembershipId);
            Assert.Equal(WorkTaskStatus.Backlog, task.Status);
            Assert.Equal("Check comments", task.Title);
            Assert.Equal(WorkTaskPriority.High, task.Priority);
            Assert.Equal(15, task.EstimateMinutes);
            Assert.Null(task.DueDate);
        });

        var stored = await TemplateAsync(WorkspaceA, routine.Id);
        Assert.Equal(Thursday.AddDays(7), stored.NextOccurrenceDate);
        Assert.Null(stored.LeasedBy);
        Assert.Equal(WorkTaskTemplateStatus.Active, stored.Status);
    }

    [Fact]
    public async Task A_weekly_routine_falls_only_on_its_weekdays()
    {
        var routine = Daily(OwnerA);
        routine.Frequency = WorkTaskRecurrenceFrequency.Weekly;
        routine.Weekdays = WorkTaskWeekdays.Monday | WorkTaskWeekdays.Wednesday | WorkTaskWeekdays.Friday;
        await SeedAsync(WorkspaceA, routine);

        await RunAsync();

        Assert.Equal(
            [Thursday.AddDays(1), Thursday.AddDays(4), Thursday.AddDays(6)],
            (await OccurrencesAsync(WorkspaceA, routine.Id)).Select(task => task.RecurrenceDate!.Value));
    }

    [Fact]
    public async Task A_routine_that_reaches_its_last_date_ends_with_nothing_next()
    {
        var routine = Daily(OwnerA);
        routine.EndsOn = Thursday.AddDays(2);
        await SeedAsync(WorkspaceA, routine);

        await RunAsync();

        Assert.Equal(3, (await OccurrencesAsync(WorkspaceA, routine.Id)).Count);
        var stored = await TemplateAsync(WorkspaceA, routine.Id);
        Assert.Equal(WorkTaskTemplateStatus.Ended, stored.Status);
        Assert.Null(stored.NextOccurrenceDate);
    }

    [Fact]
    public async Task A_missed_date_is_not_made_late()
    {
        var routine = Daily(OwnerA);
        routine.NextOccurrenceDate = Thursday.AddDays(-10);
        await SeedAsync(WorkspaceA, routine);

        await RunAsync();

        Assert.Equal(Thursday, (await OccurrencesAsync(WorkspaceA, routine.Id)).Min(task => task.RecurrenceDate));
    }

    [Fact]
    public async Task An_occurrence_already_made_is_not_made_again()
    {
        var routine = await SeedAsync(WorkspaceA, Daily(OwnerA));

        // What an earlier attempt would have left: Friday's task, made and saved.
        var friday = Occurrence(routine, Thursday.AddDays(1), "Made earlier");
        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(friday);
            return await db.SaveChangesAsync(Ct);
        });

        await RunAsync();

        var tasks = await OccurrencesAsync(WorkspaceA, routine.Id);
        Assert.Equal(7, tasks.Count);
        Assert.Equal("Made earlier", Assert.Single(tasks, task => task.RecurrenceDate == Thursday.AddDays(1)).Title);
    }

    // ---- Changes never edit a task ----

    [Fact]
    public async Task Changing_a_routine_never_touches_a_task_already_made_open_or_finished()
    {
        var routine = await SeedAsync(WorkspaceA, Daily(OwnerA));
        await RunAsync();

        // One of the week's tasks finished; then the routine renamed and narrowed to Mondays.
        await InAsync(WorkspaceA, async db =>
        {
            var finished = await db.WorkTasks.SingleAsync(task => task.WorkTaskTemplateId == routine.Id && task.RecurrenceDate == Thursday, Ct);
            finished.CompletedFromStatus = finished.Status;
            finished.Status = WorkTaskStatus.Done;
            finished.CompletedAt = ThursdayNoon;
            finished.CompletedByMembershipId = OwnerA;

            var template = await db.WorkTaskTemplates.SingleAsync(row => row.Id == routine.Id, Ct);
            template.Title = "Answer comments";
            template.Frequency = WorkTaskRecurrenceFrequency.Weekly;
            template.Weekdays = WorkTaskWeekdays.Monday;

            return await db.SaveChangesAsync(Ct);
        });

        var before = await OccurrencesAsync(WorkspaceA, routine.Id);

        _clock.Advance(TimeSpan.FromDays(7));
        await RunAsync();

        var after = await OccurrencesAsync(WorkspaceA, routine.Id);
        foreach (var task in before)
        {
            var same = Assert.Single(after, row => row.Id == task.Id);
            Assert.Equal(task.Title, same.Title);
            Assert.Equal(task.Status, same.Status);
            Assert.Equal(task.ScheduledDate, same.ScheduledDate);
        }

        // Only the new Monday is new, and it carries the new name.
        var added = after.Where(task => before.All(old => old.Id != task.Id)).ToList();
        var monday = Assert.Single(added);
        Assert.Equal(DayOfWeek.Monday, monday.RecurrenceDate!.Value.DayOfWeek);
        Assert.Equal("Answer comments", monday.Title);
    }

    // ---- Zone and clock changes ----

    [Fact]
    public async Task The_routines_own_zone_decides_today_not_utc_and_not_a_later_brand_setting()
    {
        // Thursday noon in Chicago is already Friday morning in Auckland.
        var routine = Daily(OwnerA);
        routine.TimeZoneId = "Pacific/Auckland";
        await SeedAsync(WorkspaceA, routine);

        await RunAsync();

        Assert.Equal(Thursday.AddDays(1), (await OccurrencesAsync(WorkspaceA, routine.Id)).Min(task => task.RecurrenceDate));
    }

    [Fact]
    public async Task Today_turns_at_local_midnight_either_side_of_the_clocks_falling_back()
    {
        // Chicago leaves CDT on Sunday 7 November 2038. 05:59Z on the 8th is 23:59 on the 7th, CST.
        _clock.SetUtcNow(new DateTimeOffset(2038, 11, 8, 5, 59, 0, TimeSpan.Zero));
        var lateSunday = await SeedAsync(WorkspaceA, Daily(OwnerA));
        await RunAsync();
        var sundays = await OccurrencesAsync(WorkspaceA, lateSunday.Id);

        _clock.SetUtcNow(new DateTimeOffset(2038, 11, 8, 6, 0, 0, TimeSpan.Zero));
        var mondayMidnight = await SeedAsync(WorkspaceA, Daily(OwnerA));
        await RunAsync();

        // Seven dates across the change, the first being the local Sunday the clocks fell back on.
        Assert.Equal(Enumerable.Range(0, 7).Select(new DateOnly(2038, 11, 7).AddDays), sundays.Select(task => task.RecurrenceDate!.Value));
        Assert.Equal(new DateOnly(2038, 11, 8), (await OccurrencesAsync(WorkspaceA, mondayMidnight.Id)).Min(task => task.RecurrenceDate));
    }

    // ---- Stopping rather than guessing ----

    [Fact]
    public async Task A_routine_whose_owner_cannot_keep_work_stops_and_makes_nothing()
    {
        var viewers = await SeedAsync(WorkspaceA, Daily(ViewerA));
        var gone = await SeedAsync(WorkspaceA, Daily(Guid.NewGuid()));

        await RunAsync();

        foreach (var routine in new[] { viewers, gone })
        {
            Assert.Empty(await OccurrencesAsync(WorkspaceA, routine.Id));
            var stored = await TemplateAsync(WorkspaceA, routine.Id);
            Assert.Equal(WorkTaskTemplateStatus.NeedsAttention, stored.Status);
            Assert.Equal(WorkTaskTemplateAttention.OwnerUnavailable, stored.AttentionReason);
        }
    }

    [Fact]
    public async Task A_routine_whose_zone_is_not_a_zone_stops_and_makes_nothing()
    {
        var routine = Daily(OwnerA);
        routine.TimeZoneId = "Mars/Olympus_Mons";
        await SeedAsync(WorkspaceA, routine);

        await RunAsync();

        Assert.Empty(await OccurrencesAsync(WorkspaceA, routine.Id));
        var stored = await TemplateAsync(WorkspaceA, routine.Id);
        Assert.Equal(WorkTaskTemplateStatus.NeedsAttention, stored.Status);
        Assert.Equal(WorkTaskTemplateAttention.TimeZoneInvalid, stored.AttentionReason);
    }

    [Fact]
    public async Task A_paused_routine_or_one_another_pass_holds_is_left_alone_and_a_lapsed_hold_is_taken()
    {
        var paused = Daily(OwnerA);
        paused.Status = WorkTaskTemplateStatus.Paused;
        var held = Daily(OwnerA);
        held.LeasedBy = Guid.NewGuid();
        held.LeaseExpiresAt = ThursdayNoon.AddMinutes(3);
        var lapsed = Daily(OwnerA);
        lapsed.LeasedBy = Guid.NewGuid();
        lapsed.LeaseExpiresAt = ThursdayNoon.AddMinutes(-1);
        await SeedAsync(WorkspaceA, paused);
        await SeedAsync(WorkspaceA, held);
        await SeedAsync(WorkspaceA, lapsed);

        await RunAsync();

        Assert.Empty(await OccurrencesAsync(WorkspaceA, paused.Id));
        Assert.Empty(await OccurrencesAsync(WorkspaceA, held.Id));
        Assert.Equal(7, (await OccurrencesAsync(WorkspaceA, lapsed.Id)).Count);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task Each_workspaces_routines_make_tasks_in_that_workspace_alone()
    {
        var ours = await SeedAsync(WorkspaceA, Daily(OwnerA));
        var theirs = await SeedAsync(WorkspaceB, Daily(OwnerB));

        await RunAsync();

        Assert.Equal(7, (await OccurrencesAsync(WorkspaceA, ours.Id)).Count);
        Assert.Empty(await OccurrencesAsync(WorkspaceA, theirs.Id));
        Assert.Equal(7, (await OccurrencesAsync(WorkspaceB, theirs.Id)).Count);
        Assert.Empty(await OccurrencesAsync(WorkspaceB, ours.Id));
        Assert.All(await OccurrencesAsync(WorkspaceB, theirs.Id), task => Assert.Equal(OwnerB, task.OwnerMembershipId));
    }

    [Fact]
    public async Task Another_workspaces_routine_handed_to_this_workspace_is_skipped_and_nothing_is_written()
    {
        // The leak path itself: a claim for B's routine worked under A's resolved context.
        var theirs = Daily(OwnerB);
        var token = Guid.NewGuid();
        theirs.LeasedBy = token;
        theirs.LeaseExpiresAt = ThursdayNoon.AddMinutes(5);
        await SeedAsync(WorkspaceB, theirs);

        var run = await MaterializeInAsync(WorkspaceA, theirs.Id, token);

        Assert.Equal(WorkTaskTemplateOutcome.Skipped, run.Outcome);
        Assert.Empty(await OccurrencesAsync(WorkspaceA, theirs.Id));
        Assert.Empty(await OccurrencesAsync(WorkspaceB, theirs.Id));
        Assert.Equal(token, (await TemplateAsync(WorkspaceB, theirs.Id)).LeasedBy);
    }

    [Fact]
    public async Task A_routine_whose_owner_belongs_to_another_workspace_stops_rather_than_making_their_tasks_here()
    {
        var routine = await SeedAsync(WorkspaceA, Daily(OwnerB));

        await RunAsync();

        Assert.Empty(await OccurrencesAsync(WorkspaceA, routine.Id));
        Assert.Equal(WorkTaskTemplateAttention.OwnerUnavailable, (await TemplateAsync(WorkspaceA, routine.Id)).AttentionReason);
    }

    [Fact]
    public async Task A_pass_whose_hold_was_taken_or_lapsed_writes_nothing()
    {
        var taken = Daily(OwnerA);
        taken.LeasedBy = Guid.NewGuid();
        taken.LeaseExpiresAt = ThursdayNoon.AddMinutes(5);
        var lapsed = Daily(OwnerA);
        var token = Guid.NewGuid();
        lapsed.LeasedBy = token;
        lapsed.LeaseExpiresAt = ThursdayNoon.AddMinutes(-1);
        await SeedAsync(WorkspaceA, taken);
        await SeedAsync(WorkspaceA, lapsed);

        // A pass holding a token that is not the routine's now, and one whose own token has lapsed.
        Assert.Equal(WorkTaskTemplateOutcome.Skipped, (await MaterializeInAsync(WorkspaceA, taken.Id, Guid.NewGuid())).Outcome);
        Assert.Equal(WorkTaskTemplateOutcome.Skipped, (await MaterializeInAsync(WorkspaceA, lapsed.Id, token)).Outcome);

        Assert.Empty(await OccurrencesAsync(WorkspaceA, taken.Id));
        Assert.Empty(await OccurrencesAsync(WorkspaceA, lapsed.Id));
    }

    // ---- Helpers ----

    private async Task<WorkTaskTemplateRun> MaterializeInAsync(Guid workspaceId, Guid templateId, Guid leaseToken)
    {
        await using var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, workspaceId == WorkspaceA ? "workspace-a" : "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner, "test");

        return await scope.ServiceProvider.GetRequiredService<IWorkTaskTemplateFacade>().MaterializeAsync(templateId, leaseToken, Ct);
    }


    private async Task RunAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkTaskTemplateWorker>().RunPendingAsync(Ct);
    }

    private static WorkTaskTemplate Daily(Guid owner) => new()
    {
        Id = Guid.NewGuid(),
        OwnerMembershipId = owner,
        Title = "Check comments",
        Priority = WorkTaskPriority.High,
        EstimateMinutes = 15,
        Frequency = WorkTaskRecurrenceFrequency.Daily,
        Weekdays = WorkTaskWeekdays.None,
        StartsOn = new DateOnly(2038, 1, 1),
        TimeZoneId = Chicago,
        CreatedAt = ThursdayNoon,
        UpdatedAt = ThursdayNoon,
    };

    private static WorkTask Occurrence(WorkTaskTemplate routine, DateOnly date, string title) => new()
    {
        Id = Guid.NewGuid(),
        OwnerMembershipId = routine.OwnerMembershipId,
        AssigneeMembershipId = routine.OwnerMembershipId,
        Title = title,
        ScheduledDate = date,
        PlanningWeekStart = WorkTaskWeek.StartOf(date),
        WorkTaskTemplateId = routine.Id,
        RecurrenceDate = date,
        SortRank = WorkTaskPolicy.RankGap,
        CreatedAt = ThursdayNoon,
        UpdatedAt = ThursdayNoon,
    };

    private Task<WorkTaskTemplate> SeedAsync(Guid workspaceId, WorkTaskTemplate routine) =>
        InAsync(workspaceId, async db =>
        {
            db.WorkTaskTemplates.Add(routine);
            await db.SaveChangesAsync(Ct);

            return routine;
        });

    private Task<List<WorkTask>> OccurrencesAsync(Guid workspaceId, Guid templateId) =>
        InAsync(workspaceId, db => db.WorkTasks.AsNoTracking()
            .Where(task => task.WorkTaskTemplateId == templateId)
            .OrderBy(task => task.RecurrenceDate)
            .ToListAsync(Ct));

    private Task<WorkTaskTemplate> TemplateAsync(Guid workspaceId, Guid templateId) =>
        InAsync(workspaceId, db => db.WorkTaskTemplates.AsNoTracking().SingleAsync(row => row.Id == templateId, Ct));

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, workspaceId == WorkspaceA ? "workspace-a" : "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner, "test");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private static ApplicationUser User(string id) => new()
    {
        Id = id,
        UserName = id,
        NormalizedUserName = id.ToUpperInvariant(),
        Email = $"{id}@example.com",
        NormalizedEmail = $"{id.ToUpperInvariant()}@EXAMPLE.COM",
        DisplayName = id,
        CreatedAt = ThursdayNoon,
    };

    private static WorkspaceMembership Membership(Guid id, Guid workspaceId, string userId, WorkspaceRole role) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        UserId = userId,
        Role = role,
        Status = WorkspaceMembershipStatus.Active,
        JoinedAt = ThursdayNoon,
    };
}

/// <summary>The routine worker on SQLite.</summary>
public sealed class WorkTaskTemplateWorkerSqliteTests : WorkTaskTemplateWorkerContract
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override Task<Action<DbContextOptionsBuilder>> StartEngineAsync()
    {
        _connection.Open();

        return Task.FromResult<Action<DbContextOptionsBuilder>>(options => options
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
    }

    protected override Task CreateSchemaAsync(CreatorPantryDbContext db) => db.Database.EnsureCreatedAsync(Ct);

    protected override async ValueTask StopEngineAsync() => await _connection.DisposeAsync();
}

/// <summary>The routine worker on SQL Server, through the real migration: the claim's guarded update and every check.</summary>
public sealed class WorkTaskTemplateWorkerSqlServerTests : WorkTaskTemplateWorkerContract
{
    private readonly SqlServerTestDatabase _container = new();

    protected override async Task<Action<DbContextOptionsBuilder>> StartEngineAsync()
    {
        await _container.StartAsync();

        return options => options.UseSqlServer(_container.GetConnectionString());
    }

    protected override Task CreateSchemaAsync(CreatorPantryDbContext db) => db.Database.MigrateAsync(Ct);

    protected override ValueTask StopEngineAsync() => _container.DisposeAsync();
}
