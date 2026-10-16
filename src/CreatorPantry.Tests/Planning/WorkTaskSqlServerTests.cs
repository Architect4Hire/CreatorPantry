using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
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
    public async Task An_edit_moves_the_token_and_a_concurrent_edit_of_labels_alone_is_refused()
    {
        // The edit route's concurrency, where it can be proved: under SQLite a row version never moves (13A.27).
        var (task, tag) = await SeedLabelledTaskAsync(WorkspaceA, labelled: false);
        var (_, otherTag) = await SeedLabelledTaskAsync(WorkspaceA, labelled: false);

        await using var firstScope = Resolved(WorkspaceA);
        await using var secondScope = Resolved(WorkspaceA);
        var first = DataLayer(firstScope);
        var second = DataLayer(secondScope);

        var mine = (await first.FindForUpdateAsync(task.Id, Ct))!;
        var theirs = (await second.FindForUpdateAsync(task.Id, Ct))!;
        var before = mine.RowVersion.ToArray();

        // What Business does for an edit of labels alone: it touches the task's own row so the token moves.
        mine.UpdatedAt = Now.AddMinutes(1);
        Assert.Equal(WorkTaskWrite.Saved, await first.UpdateAsync(mine, new HashSet<Guid> { tag.Id }, regroup: false, Ct));
        Assert.NotEqual(before, mine.RowVersion);

        theirs.UpdatedAt = Now.AddMinutes(2);
        Assert.Equal(WorkTaskWrite.Stale, await second.UpdateAsync(theirs, new HashSet<Guid> { otherTag.Id }, regroup: false, Ct));

        var labels = await InAsync(WorkspaceA, db => db.WorkTaskLabels.AsNoTracking()
            .Where(label => label.WorkTaskId == task.Id).Select(label => label.WorkspaceTagId).ToListAsync(Ct));
        Assert.Equal([tag.Id], labels);
        Assert.Equal(mine.RowVersion, (await StoredAsync(task.Id)).RowVersion);
    }

    [Fact]
    public async Task A_task_planned_for_another_day_by_an_edit_goes_to_the_bottom_of_that_days_list()
    {
        var member = Guid.NewGuid();
        var monday = Task_();
        monday.AssigneeMembershipId = monday.OwnerMembershipId = member;
        monday.PlanningWeekStart = monday.ScheduledDate = Monday;
        monday.SortRank = 5 * WorkTaskPolicy.RankGap;

        var tuesday = Task_();
        tuesday.AssigneeMembershipId = tuesday.OwnerMembershipId = member;
        tuesday.PlanningWeekStart = Monday;
        tuesday.ScheduledDate = Monday.AddDays(1);

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(monday, tuesday);
            return await db.SaveChangesAsync(Ct);
        });

        await using var scope = Resolved(WorkspaceA);
        var layer = DataLayer(scope);
        var moving = (await layer.FindForUpdateAsync(tuesday.Id, Ct))!;
        moving.ScheduledDate = Monday;
        moving.UpdatedAt = Now.AddMinutes(1);

        Assert.Equal(WorkTaskWrite.Saved, await layer.UpdateAsync(moving, labelIds: null, regroup: true, Ct));
        Assert.Equal(6 * WorkTaskPolicy.RankGap, (await StoredAsync(tuesday.Id)).SortRank);
    }

    [Fact]
    public async Task Two_moves_of_one_task_race_and_the_second_moves_nothing()
    {
        // The move route's concurrency, where it can be proved: under SQLite a row version never moves (13A.28).
        var (task, _) = await SeedLabelledTaskAsync(WorkspaceA, labelled: false);

        await using var firstScope = Resolved(WorkspaceA);
        await using var secondScope = Resolved(WorkspaceA);
        var first = DataLayer(firstScope);
        var second = DataLayer(secondScope);

        var mine = (await first.FindForUpdateAsync(task.Id, Ct))!;
        var theirs = (await second.FindForUpdateAsync(task.Id, Ct))!;

        mine.Status = WorkTaskStatus.InProgress;
        mine.UpdatedAt = Now.AddMinutes(1);
        Assert.Equal(WorkTaskWrite.Saved, await first.MoveAsync(mine, beforeTaskId: null, Ct));

        theirs.Status = WorkTaskStatus.Waiting;
        theirs.UpdatedAt = Now.AddMinutes(2);
        Assert.Equal(WorkTaskWrite.Stale, await second.MoveAsync(theirs, beforeTaskId: null, Ct));

        Assert.Equal(WorkTaskStatus.InProgress, (await StoredAsync(task.Id)).Status);
    }

    [Fact]
    public async Task A_neighbour_that_changes_while_a_list_is_re_spaced_refuses_the_whole_move()
    {
        var member = Guid.NewGuid();
        var low = Planned(member, Monday, rank: 1);
        var high = Planned(member, Monday, rank: 2);
        var moving = Planned(member, Monday.AddDays(1), rank: 1);

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(low, high, moving);
            return await db.SaveChangesAsync(Ct);
        });

        await using var scope = Resolved(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // Somebody edits a neighbour after the list was read for re-spacing and before the move is saved.
        var racing = new NeighbourEditedAfterRead(new WorkTaskRepository(db), () => InAsync(WorkspaceA, async other =>
        {
            var neighbour = await other.WorkTasks.SingleAsync(row => row.Id == low.Id, Ct);
            neighbour.Title = "Edited meanwhile";
            return await other.SaveChangesAsync(Ct);
        }));
        var layer = new WorkTaskDataLayer(racing, db);

        var tracked = (await layer.FindForUpdateAsync(moving.Id, Ct))!;
        tracked.ScheduledDate = Monday;
        tracked.UpdatedAt = Now.AddMinutes(1);

        // Ranks 1 and 2 leave no room, so the move must re-space Monday — and the edited neighbour refuses it.
        Assert.Equal(WorkTaskWrite.Stale, await layer.MoveAsync(tracked, high.Id, Ct));

        var after = await InAsync(WorkspaceA, other => other.WorkTasks.AsNoTracking()
            .Where(row => row.Id == low.Id || row.Id == high.Id || row.Id == moving.Id)
            .ToDictionaryAsync(row => row.Id, Ct));
        Assert.Equal(1, after[low.Id].SortRank);
        Assert.Equal("Edited meanwhile", after[low.Id].Title);
        Assert.Equal(2, after[high.Id].SortRank);
        Assert.Equal(Monday.AddDays(1), after[moving.Id].ScheduledDate);
        Assert.Equal(1, after[moving.Id].SortRank);
    }

    [Fact]
    public async Task Two_reorders_of_one_task_race_and_the_second_writes_nothing()
    {
        // The reorder route's concurrency, where it can be proved (13A.28a).
        var member = Guid.NewGuid();
        var first = Planned(member, Monday, rank: 1024);
        var second = Planned(member, Monday, rank: 2048);
        var third = Planned(member, Monday, rank: 3072);

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(first, second, third);
            return await db.SaveChangesAsync(Ct);
        });

        await using var mineScope = Resolved(WorkspaceA);
        await using var theirScope = Resolved(WorkspaceA);
        var mine = DataLayer(mineScope);
        var theirs = DataLayer(theirScope);

        var myCopy = (await mine.FindForUpdateAsync(third.Id, Ct))!;
        var theirCopy = (await theirs.FindForUpdateAsync(third.Id, Ct))!;

        Assert.Equal(WorkTaskWrite.Saved, await mine.ReorderAsync(myCopy, first.Id, Now.AddMinutes(1), Ct));
        Assert.Equal(WorkTaskWrite.Stale, await theirs.ReorderAsync(theirCopy, second.Id, Now.AddMinutes(2), Ct));

        // The first reorder's place stands: before the first task, not before the second.
        Assert.True((await StoredAsync(third.Id)).SortRank < first.SortRank);
    }

    [Fact]
    public async Task A_reorder_whose_re_spacing_meets_a_neighbour_edited_meanwhile_writes_nothing()
    {
        var member = Guid.NewGuid();
        var low = Planned(member, Monday, rank: 1);
        var high = Planned(member, Monday, rank: 2);
        var moving = Planned(member, Monday, rank: 3);

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(low, high, moving);
            return await db.SaveChangesAsync(Ct);
        });

        await using var scope = Resolved(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var layer = new WorkTaskDataLayer(
            new NeighbourEditedAfterRead(new WorkTaskRepository(db), () => InAsync(WorkspaceA, async other =>
            {
                var neighbour = await other.WorkTasks.SingleAsync(row => row.Id == low.Id, Ct);
                neighbour.Title = "Edited meanwhile";
                return await other.SaveChangesAsync(Ct);
            })),
            db);

        var tracked = (await layer.FindForUpdateAsync(moving.Id, Ct))!;

        // Ranks 1 and 2 leave no room before "high", so the list must be re-spaced — and the edit refuses it.
        Assert.Equal(WorkTaskWrite.Stale, await layer.ReorderAsync(tracked, high.Id, Now.AddMinutes(1), Ct));

        var ranks = await InAsync(WorkspaceA, other => other.WorkTasks.AsNoTracking()
            .Where(row => row.Id == low.Id || row.Id == high.Id || row.Id == moving.Id)
            .ToDictionaryAsync(row => row.Id, row => row.SortRank, Ct));
        Assert.Equal(1, ranks[low.Id]);
        Assert.Equal(2, ranks[high.Id]);
        Assert.Equal(3, ranks[moving.Id]);
    }

    [Fact]
    public async Task The_list_of_no_day_is_found_by_its_null_day_in_sql_servers_own_dialect()
    {
        // Every other list is keyed by a day; this one by the absence of one, which EF must write as IS NULL.
        var member = Guid.NewGuid();
        var first = Planned(member, Monday, rank: 1024);
        var second = Planned(member, Monday, rank: 2048);
        var dayed = Planned(member, Monday, rank: 1536);
        first.ScheduledDate = second.ScheduledDate = null;

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(first, second, dayed);
            return await db.SaveChangesAsync(Ct);
        });

        await using var scope = Resolved(WorkspaceA);
        var layer = DataLayer(scope);
        var tracked = (await layer.FindForUpdateAsync(second.Id, Ct))!;

        Assert.Equal(WorkTaskWrite.Saved, await layer.ReorderAsync(tracked, first.Id, Now.AddMinutes(1), Ct));
        Assert.True((await StoredAsync(second.Id)).SortRank < first.SortRank);

        // A task with a day is in another list, so it cannot be a neighbour here.
        var again = (await layer.FindForUpdateAsync(first.Id, Ct))!;
        Assert.Equal(WorkTaskWrite.PlaceUnusable, await layer.ReorderAsync(again, dayed.Id, Now.AddMinutes(2), Ct));
    }

    [Fact]
    public async Task Two_completions_race_and_the_second_writes_nothing_and_so_does_an_edit_that_lost_to_one()
    {
        // The completion route's concurrency, where it can be proved (13A.28b).
        var (task, _) = await SeedLabelledTaskAsync(WorkspaceA, labelled: false);

        await using var firstScope = Resolved(WorkspaceA);
        await using var secondScope = Resolved(WorkspaceA);
        await using var editScope = Resolved(WorkspaceA);
        var first = DataLayer(firstScope);
        var second = DataLayer(secondScope);
        var editor = DataLayer(editScope);

        var mine = (await first.FindForUpdateAsync(task.Id, Ct))!;
        var theirs = (await second.FindForUpdateAsync(task.Id, Ct))!;
        var edited = (await editor.FindForUpdateAsync(task.Id, Ct))!;

        Complete(mine, Now.AddMinutes(1));
        Assert.Equal(WorkTaskWrite.Saved, await first.SaveAsync(mine, Ct));

        Complete(theirs, Now.AddMinutes(2));
        Assert.Equal(WorkTaskWrite.Stale, await second.SaveAsync(theirs, Ct));

        edited.Title = "Edited on a stale copy";
        edited.UpdatedAt = Now.AddMinutes(3);
        Assert.Equal(WorkTaskWrite.Stale, await editor.UpdateAsync(edited, labelIds: null, regroup: false, Ct));

        var stored = await StoredAsync(task.Id);
        Assert.Equal(WorkTaskStatus.Done, stored.Status);
        Assert.Equal(Now.AddMinutes(1), stored.CompletedAt);
        Assert.Equal(task.Title, stored.Title);

        static void Complete(WorkTask copy, DateTimeOffset at)
        {
            copy.CompletedFromStatus = copy.Status;
            copy.Status = WorkTaskStatus.Done;
            copy.CompletedAt = at;
            copy.CompletedByMembershipId = copy.AssigneeMembershipId;
            copy.UpdatedAt = at;
        }
    }

    [Fact]
    public async Task A_step_has_one_task_per_person_from_add_to_my_plan_and_hand_made_ones_do_not_count()
    {
        // 13A.29's index, in SQL Server's own dialect: filtered on the origin, so only "Add to my plan" is held to it.
        var owner = Guid.NewGuid();
        var run = Guid.NewGuid();

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(FromStep(owner, run, "concept"));
            db.WorkTasks.Add(ByHand(owner, run, "concept"));
            db.WorkTasks.Add(ByHand(owner, run, "concept"));
            db.WorkTasks.Add(FromStep(Guid.NewGuid(), run, "concept"));
            db.WorkTasks.Add(FromStep(owner, run, "shoot"));
            return await db.SaveChangesAsync(Ct);
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(FromStep(owner, run, "concept"));
            return await db.SaveChangesAsync(Ct);
        }));

        // Another workspace's same owner, run and step is another workspace's task.
        await InAsync(WorkspaceB, async db =>
        {
            db.WorkTasks.Add(FromStep(owner, run, "concept"));
            return await db.SaveChangesAsync(Ct);
        });

        // And a step's task is a task for a step.
        var stepless = FromStep(owner, run, "concept");
        stepless.WorkflowStepKey = null;
        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(stepless);
            return await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task A_confirm_that_loses_the_race_answers_with_the_winners_task_rather_than_a_second()
    {
        var owner = Guid.NewGuid();
        var run = Guid.NewGuid();
        var winner = FromStep(owner, run, "concept");

        await using var scope = Resolved(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var layer = new WorkTaskDataLayer(
            new StepTaskSavedMeanwhile(new WorkTaskRepository(db), () => InAsync(WorkspaceA, async other =>
            {
                other.WorkTasks.Add(winner);
                return await other.SaveChangesAsync(Ct);
            })),
            db);

        var added = await layer.AddFromWorkflowStepAsync(FromStep(owner, run, "concept"), Ct);

        Assert.NotNull(added);
        Assert.False(added.Value.Created);
        Assert.Equal(winner.Id, added.Value.Task.Id);
        Assert.Equal(1, await InAsync(WorkspaceA, other => other.WorkTasks.CountAsync(task => task.WorkflowRunId == run, Ct)));
    }

    public static TheoryData<string> RefusedTemplateShapes() =>
    [
        "blank title",
        "weekly with no weekday",
        "daily with a weekday",
        "weekday bits past Sunday",
        "ends before it starts",
        "next before it starts",
        "next after it ends",
        "ended with a next date",
        "needs attention without a reason",
        "a reason without needing attention",
        "half a lease",
        "blank zone",
    ];

    [Theory]
    [MemberData(nameof(RefusedTemplateShapes))]
    public async Task Every_invalid_routine_is_refused_in_sql_servers_own_dialect(string shape)
    {
        var template = new WorkTaskTemplate
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = Guid.NewGuid(),
            Title = "Check comments",
            Frequency = WorkTaskRecurrenceFrequency.Daily,
            StartsOn = Monday,
            TimeZoneId = "America/Chicago",
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        switch (shape)
        {
            case "blank title": template.Title = " "; break;
            case "weekly with no weekday": template.Frequency = WorkTaskRecurrenceFrequency.Weekly; break;
            case "daily with a weekday": template.Weekdays = WorkTaskWeekdays.Monday; break;
            case "weekday bits past Sunday":
                template.Frequency = WorkTaskRecurrenceFrequency.Weekly;
                template.Weekdays = (WorkTaskWeekdays)128;
                break;
            case "ends before it starts": template.EndsOn = Monday.AddDays(-1); break;
            case "next before it starts": template.NextOccurrenceDate = Monday.AddDays(-1); break;
            case "next after it ends":
                template.EndsOn = Monday.AddDays(3);
                template.NextOccurrenceDate = Monday.AddDays(4);
                break;
            case "ended with a next date":
                template.Status = WorkTaskTemplateStatus.Ended;
                template.NextOccurrenceDate = Monday;
                break;
            case "needs attention without a reason": template.Status = WorkTaskTemplateStatus.NeedsAttention; break;
            case "a reason without needing attention": template.AttentionReason = WorkTaskTemplateAttention.OwnerUnavailable; break;
            case "half a lease": template.LeasedBy = Guid.NewGuid(); break;
            case "blank zone": template.TimeZoneId = " "; break;
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTaskTemplates.Add(template);
            return await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task A_routine_with_occurrences_cannot_be_deleted_only_paused_or_ended()
    {
        var templateId = await SeedTemplateAsync(WorkspaceA, Guid.NewGuid());
        var occurrence = Task_();
        occurrence.WorkTaskTemplateId = templateId;
        occurrence.RecurrenceDate = Monday;

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(occurrence);
            return await db.SaveChangesAsync(Ct);
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTaskTemplates.Remove(await db.WorkTaskTemplates.SingleAsync(row => row.Id == templateId, Ct));
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

        // An occurrence's routine is a real key since 13A.30, so it must exist.
        if (task.WorkTaskTemplateId is { } templateId)
        {
            await SeedTemplateAsync(WorkspaceA, templateId);
        }

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
        var template = await SeedTemplateAsync(WorkspaceA, Guid.NewGuid());
        var theirs = await SeedTemplateAsync(WorkspaceB, Guid.NewGuid());
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

        // The next occurrence is a different task, and so is another workspace's routine on the same date.
        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(Occurrence(template, Monday.AddDays(1)));
            return await db.SaveChangesAsync(Ct);
        });
        await InAsync(WorkspaceB, async db =>
        {
            db.WorkTasks.Add(Occurrence(theirs, Monday));
            return await db.SaveChangesAsync(Ct);
        });

        // And a task cannot claim a routine of another workspace's.
        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(Occurrence(theirs, Monday.AddDays(2)));
            return await db.SaveChangesAsync(Ct);
        }));

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
    // ---- Top three (13A.32) ----

    [Fact]
    public async Task A_top_three_reordered_replaces_itself_without_meeting_its_own_unique_index()
    {
        var person = Guid.NewGuid();
        var (first, second, third) = (Task_(), Task_(), Task_());
        foreach (var task in new[] { first, second, third })
        {
            task.AssigneeMembershipId = task.OwnerMembershipId = person;
        }

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(first, second, third);

            return await db.SaveChangesAsync(Ct);
        });

        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        Assert.True(await ReplaceFocusAsync(WorkspaceA, person, today, [first.Id, second.Id, third.Id]));

        // Every task changes place: the second becomes first, which a row-by-row update would refuse.
        Assert.True(await ReplaceFocusAsync(WorkspaceA, person, today, [second.Id, third.Id, first.Id]));
        Assert.Equal([second.Id, third.Id, first.Id], await ChosenAsync(WorkspaceA, person, today));

        // And clearing it clears it.
        Assert.True(await ReplaceFocusAsync(WorkspaceA, person, today, []));
        Assert.Empty(await ChosenAsync(WorkspaceA, person, today));
    }

    [Fact]
    public async Task A_top_three_goes_with_its_task_and_another_workspace_neither_sees_nor_borrows_it()
    {
        var task = Task_();
        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Add(task);

            return await db.SaveChangesAsync(Ct);
        });

        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.True(await ReplaceFocusAsync(WorkspaceA, task.AssigneeMembershipId, today, [task.Id]));

        // Workspace B cannot read A's choice, and cannot choose A's task: the composite key refuses it outright.
        Assert.Empty(await ChosenAsync(WorkspaceB, task.AssigneeMembershipId, today));
        Assert.False(await ReplaceFocusAsync(WorkspaceB, task.AssigneeMembershipId, today, [task.Id]));
        Assert.Equal([task.Id], await ChosenAsync(WorkspaceA, task.AssigneeMembershipId, today));

        // The store holds at most three places.
        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkTaskFocuses.Add(new WorkTaskFocus
            {
                AssigneeMembershipId = task.AssigneeMembershipId,
                FocusDate = today.AddDays(1),
                Position = 4,
                WorkTaskId = task.Id,
            });

            return await db.SaveChangesAsync(Ct);
        }));

        // The one delete path: the task takes its places with it.
        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.Remove(await db.WorkTasks.SingleAsync(row => row.Id == task.Id, Ct));

            return await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(0, await InAsync(WorkspaceA, db => CountAsync(db, "WorkTaskFocuses", WorkspaceA)));
    }

    [Fact]
    public async Task Replacing_one_persons_top_three_leaves_every_other_persons_and_workspaces_rows_alone()
    {
        var (me, colleague) = (Guid.NewGuid(), Guid.NewGuid());
        var (mine, theirs, mineAgain, inB) = (Task_(), Task_(), Task_(), Task_());
        mine.AssigneeMembershipId = mineAgain.AssigneeMembershipId = me;
        theirs.AssigneeMembershipId = colleague;

        // The same person id in workspace B, on the same day: only the workspace tells the two apart.
        inB.AssigneeMembershipId = me;

        await InAsync(WorkspaceA, async db =>
        {
            db.WorkTasks.AddRange(mine, theirs, mineAgain);

            return await db.SaveChangesAsync(Ct);
        });
        await InAsync(WorkspaceB, async db =>
        {
            db.WorkTasks.Add(inB);

            return await db.SaveChangesAsync(Ct);
        });

        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.True(await ReplaceFocusAsync(WorkspaceA, me, today, [mine.Id]));
        Assert.True(await ReplaceFocusAsync(WorkspaceA, colleague, today, [theirs.Id]));
        Assert.True(await ReplaceFocusAsync(WorkspaceB, me, today, [inB.Id]));

        // Replaced, and then cleared: neither touches a row that is not mine in this workspace.
        Assert.True(await ReplaceFocusAsync(WorkspaceA, me, today, [mineAgain.Id]));
        Assert.True(await ReplaceFocusAsync(WorkspaceA, me, today, []));

        Assert.Empty(await ChosenAsync(WorkspaceA, me, today));
        Assert.Equal([theirs.Id], await ChosenAsync(WorkspaceA, colleague, today));
        Assert.Equal([inB.Id], await ChosenAsync(WorkspaceB, me, today));

        // Counted beneath the filter, so a row the filter hid could not make this pass.
        Assert.Equal(1, await InAsync(WorkspaceA, db => CountAsync(db, "WorkTaskFocuses", WorkspaceA)));
        Assert.Equal(1, await InAsync(WorkspaceA, db => CountAsync(db, "WorkTaskFocuses", WorkspaceB)));
    }

    private Task<bool> ReplaceFocusAsync(Guid workspaceId, Guid person, DateOnly day, Guid[] taskIds) =>
        InAsync(workspaceId, db =>
            new WorkTaskFocusDataLayer(new WorkTaskFocusRepository(db), db).ReplaceAsync(person, day, taskIds, Ct));

    private Task<IReadOnlyList<Guid>> ChosenAsync(Guid workspaceId, Guid person, DateOnly day) =>
        InAsync(workspaceId, db => new WorkTaskFocusRepository(db).ChosenAsync(person, day, Ct));

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

    private static WorkTask Planned(Guid member, DateOnly day, long rank)
    {
        var task = Task_();
        task.OwnerMembershipId = task.AssigneeMembershipId = member;
        task.ScheduledDate = day;
        task.PlanningWeekStart = WorkTaskWeek.StartOf(day);
        task.SortRank = rank;

        return task;
    }

    /// <summary>
    /// The real repository, except that reading a list for re-spacing lets somebody else's write land straight
    /// after — the window a move must not lose a neighbour's edit in.
    /// </summary>
    private sealed class NeighbourEditedAfterRead(IWorkTaskRepository inner, Func<Task<int>> meanwhile) : DelegatingWorkTaskRepository(inner)
    {
        public override async Task<List<WorkTask>> GroupForUpdateAsync(
            Guid assigneeMembershipId, WorkTaskStatus status, DateOnly? scheduledDate, CancellationToken cancellationToken)
        {
            var list = await base.GroupForUpdateAsync(assigneeMembershipId, status, scheduledDate, cancellationToken);
            await meanwhile();

            return list;
        }
    }

    /// <summary>
    /// The real repository, except that the first look for a step's task misses one that somebody else's confirm
    /// is about to save — so the insert that follows meets the unique index, as a true race would.
    /// </summary>
    private sealed class StepTaskSavedMeanwhile(IWorkTaskRepository inner, Func<Task<int>> meanwhile) : DelegatingWorkTaskRepository(inner)
    {
        private bool _looked;

        public override async Task<WorkTask?> FindFromWorkflowStepAsync(
            Guid ownerMembershipId, Guid workflowRunId, string workflowStepKey, CancellationToken cancellationToken)
        {
            if (!_looked)
            {
                _looked = true;
                await meanwhile();

                return null;
            }

            return await base.FindFromWorkflowStepAsync(ownerMembershipId, workflowRunId, workflowStepKey, cancellationToken);
        }
    }

    /// <summary>A plain daily routine with the given id, so an occurrence has a real routine to point at.</summary>
    private Task<Guid> SeedTemplateAsync(Guid workspaceId, Guid templateId) =>
        InAsync(workspaceId, async db =>
        {
            db.WorkTaskTemplates.Add(new WorkTaskTemplate
            {
                Id = templateId,
                OwnerMembershipId = Guid.NewGuid(),
                Title = "Check comments",
                Frequency = WorkTaskRecurrenceFrequency.Daily,
                StartsOn = Monday,
                TimeZoneId = "America/Chicago",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync(Ct);

            return templateId;
        });

    private static WorkTask FromStep(Guid owner, Guid run, string step)
    {
        var task = ByHand(owner, run, step);
        task.Origin = WorkTaskOrigin.WorkflowStep;

        return task;
    }

    private static WorkTask ByHand(Guid owner, Guid run, string step)
    {
        var task = Task_();
        task.OwnerMembershipId = task.AssigneeMembershipId = owner;
        task.WorkflowRunId = run;
        task.WorkflowStepKey = step;

        return task;
    }

    private static WorkTaskDataLayer DataLayer(AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return new WorkTaskDataLayer(new WorkTaskRepository(db), db);
    }

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
