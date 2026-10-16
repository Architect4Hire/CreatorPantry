using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// The weekly view's queries on both engines (13A.26). The per-day cap is a window over a partition and the
/// totals are one grouped statement with conditional keys — the two shapes most likely to translate on one
/// engine and not the other — so each is run against SQLite, which the endpoint tests use, and SQL Server,
/// which production uses.
/// </summary>
public abstract class WorkTaskWeekRepositoryContract
{
    protected static readonly DateOnly Monday = new(2026, 10, 12);

    protected static readonly DateTimeOffset StartsAt = new(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);

    protected static readonly DateTimeOffset EndsAt = new(2026, 10, 19, 5, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Guid WorkspaceA { get; }

    protected abstract Guid WorkspaceB { get; }

    protected abstract Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work);

    [Fact]
    public async Task Each_day_is_capped_on_its_own_and_read_in_day_then_rank_order()
    {
        var me = Guid.NewGuid();
        await SeedAsync(WorkspaceA,
            Task_("Mon 3", me, scheduled: Monday, rank: 3),
            Task_("Mon 1", me, scheduled: Monday, rank: 1),
            Task_("Mon 2", me, scheduled: Monday, rank: 2),
            Task_("Tue 1", me, scheduled: Monday.AddDays(1), rank: 1),
            Task_("Sun 1", me, scheduled: Monday.AddDays(6), rank: 9),
            Task_("Next Mon", me, scheduled: Monday.AddDays(7), rank: 1));

        var days = await InAsync(WorkspaceA, db => new WorkTaskWeekRepository(db).ScheduledInWeekAsync(me, Monday, 2, Ct));

        Assert.Equal(["Mon 1", "Mon 2", "Tue 1", "Sun 1"], days.Select(task => task.Title));
    }

    [Fact]
    public async Task Backlog_unplanned_and_carried_over_divide_the_tasks_off_the_days_by_week()
    {
        var me = Guid.NewGuid();
        await SeedAsync(WorkspaceA,
            Task_("This week", me, week: Monday),
            Task_("No week", me),
            Task_("Two weeks ago", me, week: Monday.AddDays(-14)),
            Task_("Last week, Tuesday", me, scheduled: Monday.AddDays(-6)),
            Task_("Last week, no day", me, week: Monday.AddDays(-7)),
            Task_("Next week", me, week: Monday.AddDays(7)),
            Task_("On a day", me, scheduled: Monday.AddDays(2)));

        var (backlog, unplanned, carried) = await InAsync(WorkspaceA, async db =>
        {
            var repository = new WorkTaskWeekRepository(db);

            return (
                await repository.PlannedForWeekAsync(me, Monday, 10, Ct),
                await repository.UnplannedAsync(me, Monday, 10, Ct),
                await repository.CarriedOverAsync(me, Monday, 10, Ct));
        });

        Assert.Equal(["This week"], backlog.Select(task => task.Title));
        Assert.Equal(["No week"], unplanned.Select(task => task.Title));
        Assert.Equal(["Two weeks ago", "Last week, no day", "Last week, Tuesday"], carried.Select(task => task.Title));
    }

    [Fact]
    public async Task Totals_agree_with_the_lists_and_leave_an_unknown_estimate_unknown()
    {
        var me = Guid.NewGuid();
        await SeedAsync(WorkspaceA,
        [
            Task_("Mon a", me, scheduled: Monday, estimate: 30),
            Task_("Mon b", me, scheduled: Monday, estimate: 60),
            Task_("Mon c", me, scheduled: Monday),
            Task_("Wed", me, scheduled: Monday.AddDays(2)),
            Task_("Backlog", me, week: Monday, estimate: 45),
            Task_("Unplanned", me, estimate: 5),
            Task_("Carried", me, week: Monday.AddDays(-7), estimate: 600),
            Task_("Next week", me, week: Monday.AddDays(7), estimate: 999),
            Task_("Started", me, WorkTaskStatus.InProgress, scheduled: Monday.AddDays(-7), estimate: 120),
            Task_("Waiting", me, WorkTaskStatus.Waiting),
            Done_("Done in the week", me, StartsAt.AddDays(2), estimate: 15),
            Done_("Done the week before", me, StartsAt.AddMinutes(-1), estimate: 999),
            Done_("Done at the next week's first instant", me, EndsAt, estimate: 999),

            // Somebody else's, in every group, which no total may count.
            .. EveryGroup(Guid.NewGuid(), "Not mine"),
        ]);

        var totals = await InAsync(WorkspaceA, db =>
            new WorkTaskWeekRepository(db).TotalsAsync(me, Monday, StartsAt, EndsAt, withCarriedOver: true, Ct));

        Assert.Equal(new WorkTaskCount(3, 2, 90), totals.Day(Monday));
        Assert.Equal(new WorkTaskCount(1, 0, null), totals.Day(Monday.AddDays(2)));
        Assert.Equal(WorkTaskCount.None, totals.Day(Monday.AddDays(1)));
        Assert.Equal(new WorkTaskCount(1, 1, 45), totals.Backlog);
        Assert.Equal(new WorkTaskCount(1, 1, 5), totals.Unplanned);
        Assert.Equal(new WorkTaskCount(1, 1, 600), totals.CarriedOver);
        Assert.Equal(new WorkTaskCount(1, 1, 120), totals.InProgress);
        Assert.Equal(new WorkTaskCount(1, 0, null), totals.Waiting);
        Assert.Equal(new WorkTaskCount(1, 1, 15), totals.Completed);

        var notCarried = await InAsync(WorkspaceA, db =>
            new WorkTaskWeekRepository(db).TotalsAsync(me, Monday, StartsAt, EndsAt, withCarriedOver: false, Ct));
        Assert.Equal(WorkTaskCount.None, notCarried.CarriedOver);
        Assert.Equal(new WorkTaskCount(1, 1, 45), notCarried.Backlog);
    }

    [Fact]
    public async Task Another_workspaces_tasks_are_in_no_list_and_no_total()
    {
        // The same membership id in both workspaces, so only the workspace filter stands between them.
        var me = Guid.NewGuid();
        await SeedAsync(WorkspaceB, EveryGroup(me, "B's"));

        var (lists, totals) = await InAsync(WorkspaceA, async db =>
        {
            var repository = new WorkTaskWeekRepository(db);
            IReadOnlyList<WorkTask>[] lists =
            [
                await repository.ScheduledInWeekAsync(me, Monday, 10, Ct),
                await repository.PlannedForWeekAsync(me, Monday, 10, Ct),
                await repository.UnplannedAsync(me, Monday, 10, Ct),
                await repository.CarriedOverAsync(me, Monday, 10, Ct),
                await repository.InStatusAsync(me, WorkTaskStatus.InProgress, 10, Ct),
                await repository.InStatusAsync(me, WorkTaskStatus.Waiting, 10, Ct),
            ];

            return (lists, await repository.TotalsAsync(me, Monday, StartsAt, EndsAt, withCarriedOver: true, Ct));
        });

        Assert.All(lists, Assert.Empty);
        AssertNothingCounted(totals);
    }

    [Fact]
    public async Task Another_members_tasks_are_in_no_list_and_no_total()
    {
        var me = Guid.NewGuid();
        await SeedAsync(WorkspaceA, EveryGroup(Guid.NewGuid(), "Theirs"));

        var totals = await InAsync(WorkspaceA, db =>
            new WorkTaskWeekRepository(db).TotalsAsync(me, Monday, StartsAt, EndsAt, withCarriedOver: true, Ct));
        var days = await InAsync(WorkspaceA, db => new WorkTaskWeekRepository(db).ScheduledInWeekAsync(me, Monday, 10, Ct));

        Assert.Empty(days);
        AssertNothingCounted(totals);
    }

    /// <summary>One estimated task in every group of the week of <see cref="Monday"/>, for one person.</summary>
    private static WorkTask[] EveryGroup(Guid assignee, string name) =>
    [
        Task_($"{name} Monday", assignee, scheduled: Monday, estimate: 999),
        Task_($"{name} backlog", assignee, week: Monday, estimate: 999),
        Task_($"{name} unplanned", assignee, estimate: 999),
        Task_($"{name} carried", assignee, week: Monday.AddDays(-7), estimate: 999),
        Task_($"{name} started", assignee, WorkTaskStatus.InProgress, estimate: 999),
        Task_($"{name} waiting", assignee, WorkTaskStatus.Waiting, estimate: 999),
        Done_($"{name} done", assignee, StartsAt.AddDays(1), estimate: 999),
    ];

    private static void AssertNothingCounted(WorkTaskWeekTotals totals)
    {
        Assert.Empty(totals.Days);
        Assert.All(
            new[] { totals.Backlog, totals.Unplanned, totals.CarriedOver, totals.InProgress, totals.Waiting, totals.Completed },
            count => Assert.Equal(WorkTaskCount.None, count));
    }

    private Task SeedAsync(Guid workspaceId, params WorkTask[] tasks) =>
        InAsync(workspaceId, async db =>
        {
            db.WorkTasks.AddRange(tasks);

            return await db.SaveChangesAsync(Ct);
        });

    private static WorkTask Task_(
        string title,
        Guid assignee,
        WorkTaskStatus status = WorkTaskStatus.Backlog,
        DateOnly? scheduled = null,
        DateOnly? week = null,
        long rank = WorkTaskPolicy.RankGap,
        int? estimate = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = assignee,
            AssigneeMembershipId = assignee,
            Title = title,
            Status = status,
            ScheduledDate = scheduled,
            PlanningWeekStart = week ?? (scheduled is { } day ? WorkTaskWeek.StartOf(day) : null),
            EstimateMinutes = estimate,
            SortRank = rank,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

    private static WorkTask Done_(string title, Guid assignee, DateTimeOffset completedAt, int? estimate = null)
    {
        var task = Task_(title, assignee, WorkTaskStatus.Done, estimate: estimate);
        task.CompletedAt = completedAt;
        task.CompletedByMembershipId = assignee;
        task.CompletedFromStatus = WorkTaskStatus.InProgress;

        return task;
    }
}

/// <summary>The contract on SQLite, the engine the endpoint tests run on.</summary>
public sealed class WorkTaskWeekRepositorySqliteTests : WorkTaskWeekRepositoryContract, IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    protected override Guid WorkspaceA => RecipeAggregateFixture.WorkspaceA;

    protected override Guid WorkspaceB => RecipeAggregateFixture.WorkspaceB;

    protected override async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        using var scope = _fixture.ScopeFor(workspaceId);

        return await work(RecipeAggregateFixture.Db(scope));
    }
}

/// <summary>The contract on SQL Server, through the real migration.</summary>
public sealed class WorkTaskWeekRepositorySqlServerTests : WorkTaskWeekRepositoryContract, IAsyncLifetime
{
    private static readonly Guid A = Guid.NewGuid();

    private static readonly Guid B = Guid.NewGuid();

    private readonly SqlServerTestDatabase _container = new();

    private ServiceProvider? _provider;

    protected override Guid WorkspaceA => A;

    protected override Guid WorkspaceB => B;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
        db.Workspaces.AddRange(
            new Workspace { Id = A, Name = "A", Slug = "workspace-a", CreatedAt = now },
            new Workspace { Id = B, Name = "B", Slug = "workspace-b", CreatedAt = now });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    protected override async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, workspaceId == A ? "workspace-a" : "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner, "user-1");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
