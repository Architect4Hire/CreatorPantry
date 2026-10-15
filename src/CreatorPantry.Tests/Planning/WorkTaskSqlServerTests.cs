using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// Work tasks against a real SQL Server (13A.23), for what SQLite cannot answer: that the migration's DDL is
/// accepted — one cascade path from the workspace to each table — that its checks and filtered indexes are in a
/// dialect SQL Server parses and enforces, that the row version actually moves, and that a workspace's tasks
/// are invisible and untouchable from another.
/// </summary>
public sealed class WorkTaskSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A Monday, the first day of the week the planned tasks below sit in.</summary>
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private readonly SqlServerTestDatabase _container = new();

    private ServiceProvider? _provider;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

        // The migration, not EnsureCreated: this is where the generated DDL is proved to be accepted.
        await db.Database.MigrateAsync();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
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

    [Fact]
    public async Task A_task_with_its_labels_saves_and_another_workspace_can_neither_see_nor_touch_it()
    {
        var (task, _) = await SeedLabelledTaskAsync(WorkspaceA);

        Assert.Equal(1, await InAsync(WorkspaceA, db => db.WorkTaskLabels.CountAsync(label => label.WorkTaskId == task.Id, Ct)));

        await InAsync(WorkspaceB, async db =>
        {
            Assert.Null(await db.WorkTasks.FirstOrDefaultAsync(row => row.Id == task.Id, Ct));
            Assert.Null(await db.WorkTasks.FindAsync([task.Id], Ct));
            Assert.Equal(0, await db.WorkTaskLabels.CountAsync(label => label.WorkTaskId == task.Id, Ct));

            // The filter governs set-based writes too: B naming A's task by id changes and removes nothing.
            Assert.Equal(0, await db.WorkTasks.Where(row => row.Id == task.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Taken over"), Ct));
            Assert.Equal(0, await db.WorkTaskLabels.Where(label => label.WorkTaskId == task.Id).ExecuteDeleteAsync(Ct));
            Assert.Equal(0, await db.WorkTasks.Where(row => row.Id == task.Id).ExecuteDeleteAsync(Ct));

            return 0;
        });

        var kept = await StoredAsync(task.Id);
        Assert.Equal("Shoot the galette", kept.Title);
        Assert.Equal(1, await InAsync(WorkspaceA, db => db.WorkTaskLabels.CountAsync(label => label.WorkTaskId == task.Id, Ct)));
    }

    [Fact]
    public async Task A_task_cannot_be_inserted_for_another_workspace()
    {
        var foreign = Task_();
        foreign.WorkspaceId = WorkspaceB;

        await Assert.ThrowsAsync<InvalidOperationException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(foreign);
            return await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task A_task_cannot_be_moved_to_another_workspace_by_an_update()
    {
        var (task, _) = await SeedLabelledTaskAsync(WorkspaceA, labelled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InAsync(WorkspaceA, async db =>
        {
            var tracked = await db.WorkTasks.SingleAsync(row => row.Id == task.Id, Ct);
            tracked.WorkspaceId = WorkspaceB;
            return await db.SaveChangesAsync(Ct);
        }));

        Assert.Equal(WorkspaceA, (await StoredAsync(task.Id)).WorkspaceId);
    }

    [Fact]
    public async Task A_label_cannot_hang_off_another_workspaces_task_or_carry_another_workspaces_tag()
    {
        var (theirTask, theirTag) = await SeedLabelledTaskAsync(WorkspaceB);
        var (ourTask, ourTag) = await SeedLabelledTaskAsync(WorkspaceA, labelled: false);

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTaskLabels.Add(new WorkTaskLabel { WorkTaskId = theirTask.Id, WorkspaceTagId = ourTag.Id });
            return await db.SaveChangesAsync(Ct);
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTaskLabels.Add(new WorkTaskLabel { WorkTaskId = ourTask.Id, WorkspaceTagId = theirTag.Id });
            return await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task Links_to_records_that_do_not_exist_save_because_a_link_is_a_pointer_not_a_key()
    {
        // Not a key, so a to-do never holds up a recipe's removal nor cascades into one. Whether the record exists
        // in this workspace is the write seam's question, asked of the module that owns it.
        var task = Task_();
        task.RecipeId = Guid.NewGuid();
        task.ContentCardId = Guid.NewGuid();
        task.MediaAssetId = Guid.NewGuid();
        task.WorkflowRunId = Guid.NewGuid();
        task.WorkflowStepKey = "capture";

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(task);
            return await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(task.RecipeId, (await StoredAsync(task.Id)).RecipeId);
    }

    public static TheoryData<string> AcceptedShapes() =>
    [
        "minimal",
        "planned day",
        "planned week only",
        "done",
        "longest estimate",
        "template occurrence",
        "workflow step",
    ];

    [Theory]
    [MemberData(nameof(AcceptedShapes))]
    public async Task Every_valid_shape_is_accepted(string shape)
    {
        var task = Task_();
        Shape(task, shape);

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(task);
            return await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(task.Status, (await StoredAsync(task.Id)).Status);
    }

    public static TheoryData<string> RefusedShapes() =>
    [
        "blank title",
        "blank description",
        "status zero",
        "status past done",
        "priority zero",
        "done without completion",
        "completion without done",
        "completed from done",
        "day without week",
        "day before its week",
        "zero estimate",
        "estimate over a day",
        "template without occurrence",
        "occurrence without template",
        "step without run",
        "blank step",
    ];

    [Theory]
    [MemberData(nameof(RefusedShapes))]
    public async Task Every_invalid_shape_is_refused_in_sql_servers_own_dialect(string shape)
    {
        var task = Task_();
        Shape(task, shape);

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(task);
            return await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task A_template_occurrence_is_one_task_per_workspace()
    {
        var template = Guid.NewGuid();
        var first = Occurrence(template, Monday);

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(first);
            return await db.SaveChangesAsync(Ct);
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(Occurrence(template, Monday));
            return await db.SaveChangesAsync(Ct);
        }));

        // The next occurrence, and the same one in another workspace, are different tasks.
        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(Occurrence(template, Monday.AddDays(1)));
            return await db.SaveChangesAsync(Ct);
        });
        await InAsync(WorkspaceB, async db =>
        {
            db.WorkTasks.Add(Occurrence(template, Monday));
            return await db.SaveChangesAsync(Ct);
        });

        static WorkTask Occurrence(Guid template, DateOnly date)
        {
            var task = Task_();
            task.WorkTaskTemplateId = template;
            task.RecurrenceDate = date;

            return task;
        }
    }

    [Fact]
    public async Task The_row_version_moves_and_a_stale_write_is_refused()
    {
        var (seeded, _) = await SeedLabelledTaskAsync(WorkspaceA, labelled: false);

        await using var firstScope = Resolved(WorkspaceA);
        await using var secondScope = Resolved(WorkspaceA);
        var first = firstScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var mine = await first.WorkTasks.SingleAsync(row => row.Id == seeded.Id, Ct);
        var theirs = await second.WorkTasks.SingleAsync(row => row.Id == seeded.Id, Ct);
        var before = mine.RowVersion.ToArray();

        mine.Priority = WorkTaskPriority.High;
        await first.SaveChangesAsync(Ct);
        Assert.NotEqual(before, mine.RowVersion);

        theirs.Title = "Edited on a stale copy";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Removing_a_task_takes_its_labels_and_a_tag_in_use_cannot_be_removed()
    {
        var (task, tag) = await SeedLabelledTaskAsync(WorkspaceA);

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkspaceTags.Remove(await db.WorkspaceTags.SingleAsync(row => row.Id == tag.Id, Ct));
            return await db.SaveChangesAsync(Ct);
        }));

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Remove(await db.WorkTasks.SingleAsync(row => row.Id == task.Id, Ct));
            return await db.SaveChangesAsync(Ct);
        });

        await InAsync(WorkspaceA, async db =>
        {
            Assert.Equal(0, await db.WorkTaskLabels.CountAsync(label => label.WorkTaskId == task.Id, Ct));
            Assert.NotNull(await db.WorkspaceTags.SingleOrDefaultAsync(row => row.Id == tag.Id, Ct));

            return 0;
        });
    }

    [Fact]
    public async Task Erasing_a_workspace_removes_its_tasks_and_labels_and_leaves_another_workspaces_alone()
    {
        await SeedLabelledTaskAsync(WorkspaceA);
        var (survivor, _) = await SeedLabelledTaskAsync(WorkspaceB);

        // The workspace row is not workspace-owned, so it is removed from an unresolved scope.
        await using var scope = _provider!.CreateAsyncScope();
        var unresolved = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        unresolved.Workspaces.Remove(await unresolved.Workspaces.SingleAsync(item => item.Id == WorkspaceA, Ct));
        await unresolved.SaveChangesAsync(Ct);

        // Counted with raw SQL rather than IgnoreQueryFilters: the erasure is what is under test.
        Assert.Equal(0, await CountAsync(unresolved, "WorkTasks", WorkspaceA));
        Assert.Equal(0, await CountAsync(unresolved, "WorkTaskLabels", WorkspaceA));
        Assert.Equal(1, await CountAsync(unresolved, "WorkTasks", WorkspaceB));
        Assert.Equal(1, await CountAsync(unresolved, "WorkTaskLabels", WorkspaceB));
        Assert.Equal(survivor.Title, (await InAsync(WorkspaceB, db => db.WorkTasks.AsNoTracking().SingleAsync(row => row.Id == survivor.Id, Ct))).Title);
    }

    private static void Shape(WorkTask task, string shape)
    {
        switch (shape)
        {
            case "minimal":
                break;
            case "planned day":
                task.PlanningWeekStart = Monday;
                task.ScheduledDate = Monday.AddDays(3);
                task.DueDate = Monday.AddDays(4);
                break;
            case "planned week only":
                task.PlanningWeekStart = Monday;
                break;
            case "done":
                task.Status = WorkTaskStatus.Done;
                task.CompletedAt = Now;
                task.CompletedByMembershipId = task.AssigneeMembershipId;
                task.CompletedFromStatus = WorkTaskStatus.InProgress;
                break;
            case "longest estimate":
                task.EstimateMinutes = WorkTaskPolicy.EstimateMaxMinutes;
                break;
            case "template occurrence":
                task.WorkTaskTemplateId = Guid.NewGuid();
                task.RecurrenceDate = Monday;
                break;
            case "workflow step":
                task.WorkflowRunId = Guid.NewGuid();
                task.WorkflowStepKey = "today";
                break;
            case "blank title":
                task.Title = "   ";
                break;
            case "blank description":
                task.Description = " ";
                break;
            case "status zero":
                task.Status = 0;
                break;
            case "status past done":
                task.Status = (WorkTaskStatus)5;
                break;
            case "priority zero":
                task.Priority = 0;
                break;
            case "done without completion":
                task.Status = WorkTaskStatus.Done;
                break;
            case "completion without done":
                task.CompletedAt = Now;
                task.CompletedByMembershipId = task.AssigneeMembershipId;
                task.CompletedFromStatus = WorkTaskStatus.Backlog;
                break;
            case "completed from done":
                task.Status = WorkTaskStatus.Done;
                task.CompletedAt = Now;
                task.CompletedByMembershipId = task.AssigneeMembershipId;
                task.CompletedFromStatus = WorkTaskStatus.Done;
                break;
            case "day without week":
                task.ScheduledDate = Monday;
                break;
            case "day before its week":
                task.PlanningWeekStart = Monday;
                task.ScheduledDate = Monday.AddDays(-1);
                break;
            case "zero estimate":
                task.EstimateMinutes = 0;
                break;
            case "estimate over a day":
                task.EstimateMinutes = WorkTaskPolicy.EstimateMaxMinutes + 1;
                break;
            case "template without occurrence":
                task.WorkTaskTemplateId = Guid.NewGuid();
                break;
            case "occurrence without template":
                task.RecurrenceDate = Monday;
                break;
            case "step without run":
                task.WorkflowStepKey = "today";
                break;
            case "blank step":
                task.WorkflowRunId = Guid.NewGuid();
                task.WorkflowStepKey = " ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "no such shape");
        }
    }

    /// <summary>A minimal task as quick capture makes one: a title, the caller as owner and assignee, the backlog.</summary>
    private static WorkTask Task_()
    {
        var member = Guid.NewGuid();

        return new WorkTask
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = member,
            AssigneeMembershipId = member,
            Title = "Shoot the galette",
            SortRank = WorkTaskPolicy.RankGap,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    private Task<(WorkTask Task, WorkspaceTag Tag)> SeedLabelledTaskAsync(Guid workspaceId, bool labelled = true) =>
        InAsync(workspaceId, async db =>
        {
            var tag = new WorkspaceTag
            {
                Id = Guid.NewGuid(),
                Name = "Photo day",
                NormalizedName = $"photo day {Guid.NewGuid():N}",
                CreatedAt = Now,
            };
            var task = Task_();

            db.WorkspaceTags.Add(tag);
            db.WorkTasks.Add(task);
            if (labelled)
            {
                db.WorkTaskLabels.Add(new WorkTaskLabel { WorkTaskId = task.Id, WorkspaceTagId = tag.Id });
            }

            await db.SaveChangesAsync(Ct);

            return (task, tag);
        });

    private Task<WorkTask> StoredAsync(Guid taskId) =>
        InAsync(WorkspaceA, db => db.WorkTasks.AsNoTracking().SingleAsync(row => row.Id == taskId, Ct));

    private static Task<int> CountAsync(CreatorPantryDbContext db, string table, Guid workspaceId) =>
        db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table} WHERE WorkspaceId = {{0}}", workspaceId).SingleAsync(Ct);

    private AsyncServiceScope Resolved(Guid workspaceId)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "user-1");

        return scope;
    }

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = Resolved(workspaceId);

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
