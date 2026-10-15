using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// Workflow runs against a real SQL Server (13A.3), for what SQLite cannot answer: that the migration's DDL is
/// accepted — one cascade path from the workspace to each table — that its checks and filtered index are in a
/// dialect SQL Server parses and enforces, and that the row version actually moves.
/// </summary>
public sealed class WorkflowRunSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

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
    public async Task A_run_with_its_steps_and_links_saves_and_another_workspace_cannot_see_it()
    {
        var run = await SeedRunAsync(WorkspaceA);

        await InAsync(WorkspaceA, async db =>
        {
            Assert.Equal(2, await db.WorkflowStepRuns.CountAsync(step => step.WorkflowRunId == run.Id, Ct));
            Assert.Equal(1, await db.WorkflowRunLinks.CountAsync(link => link.WorkflowRunId == run.Id, Ct));

            return 0;
        });

        await InAsync(WorkspaceB, async db =>
        {
            Assert.Null(await db.WorkflowRuns.FirstOrDefaultAsync(row => row.Id == run.Id, Ct));
            Assert.Equal(0, await db.WorkflowStepRuns.CountAsync(step => step.WorkflowRunId == run.Id, Ct));
            Assert.Equal(0, await db.WorkflowRunLinks.CountAsync(link => link.WorkflowRunId == run.Id, Ct));

            return 0;
        });
    }

    [Fact]
    public async Task A_step_or_link_cannot_hang_off_another_workspaces_run()
    {
        var theirs = await SeedRunAsync(WorkspaceB);

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkflowStepRuns.Add(WorkflowRunSeeds.Step(theirs, "method"));
            return await db.SaveChangesAsync(Ct);
        }));

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(theirs, Now));
            return await db.SaveChangesAsync(Ct);
        }));
    }

    [Theory]
    [InlineData("recipes/42/kitchen", true)]
    [InlineData("/recipes", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(@"\\evil.example/recipes", false)]
    [InlineData("recipes/../../other/recipes", false)]
    public async Task The_route_check_is_enforced_in_sql_servers_own_dialect(string route, bool accepted)
    {
        var run = WorkflowRunSeeds.Run(Now);
        run.LastRoute = route;

        var save = () => InAsync(WorkspaceA, async db =>
        {
            db.WorkflowRuns.Add(run);
            return await db.SaveChangesAsync(Ct);
        });

        if (accepted)
        {
            await save();
        }
        else
        {
            await Assert.ThrowsAsync<DbUpdateException>(save);
        }
    }

    [Fact]
    public async Task The_row_version_moves_and_a_stale_write_is_refused()
    {
        var run = await SeedRunAsync(WorkspaceA);

        await using var firstScope = Resolved(WorkspaceA);
        await using var secondScope = Resolved(WorkspaceA);
        var firstDb = firstScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var first = await firstDb.WorkflowRuns.SingleAsync(row => row.Id == run.Id, Ct);
        var second = await secondDb.WorkflowRuns.SingleAsync(row => row.Id == run.Id, Ct);
        var readWith = first.RowVersion.ToArray();

        first.CurrentStepKey = "ingredients";
        first.UpdatedAt = Now.AddMinutes(1);
        await firstDb.SaveChangesAsync(Ct);

        Assert.NotEqual(readWith, first.RowVersion);

        // Read before the first write landed, so its UPDATE quotes a version the row no longer has.
        second.CurrentStepKey = "method";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Ct));
    }

    /// <summary>
    /// The draft save's guard where it counts (13A.7): the data layer saves against the version the caller read, so
    /// the token moves on a save and a save quoting a superseded one writes nothing.
    /// </summary>
    [Fact]
    public async Task A_save_against_the_version_read_lands_and_one_against_a_superseded_version_does_not()
    {
        var seeded = await SeedRunAsync(WorkspaceA);

        byte[] read;
        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            var (run, steps, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;
            read = run.RowVersion.ToArray();

            steps.Single(step => step.StepKey == "start").DraftResourceKind = WorkflowResourceKind.CreativeContext;
            steps.Single(step => step.StepKey == "start").DraftResourceId = Guid.NewGuid();
            steps.Single(step => step.StepKey == "start").DraftSavedAt = Now;
            run.UpdatedAt = Now.AddMinutes(1);

            Assert.Equal(WorkflowRunWrite.Saved, await data.SaveAsync(run, read, Ct));
            Assert.NotEqual(read, run.RowVersion);
        }

        // A second writer that read before the first one saved: refused, and nothing of theirs lands.
        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            var (run, steps, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;
            var late = Guid.NewGuid();
            steps.Single(step => step.StepKey == "start").DraftResourceId = late;
            run.UpdatedAt = Now.AddMinutes(2);

            Assert.Equal(WorkflowRunWrite.Stale, await data.SaveAsync(run, read, Ct));

            var stored = await InAsync(WorkspaceA, check => check.WorkflowStepRuns.AsNoTracking()
                .SingleAsync(step => step.WorkflowRunId == seeded.Id && step.StepKey == "start", Ct));
            Assert.NotEqual(late, stored.DraftResourceId);
        }
    }

    /// <summary>
    /// Save and exit's write where it counts (13A.10): a route the server filled is accepted by SQL Server's own route
    /// check and moves the token with the step's draft, and a writer quoting the superseded token lands neither.
    /// </summary>
    [Fact]
    public async Task A_save_and_exit_lands_its_route_and_draft_together_and_a_stale_one_lands_neither()
    {
        var seeded = await SeedRunAsync(WorkspaceA);
        var route = $"image-studio/context/{Guid.NewGuid():D}";

        byte[] read;
        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            var (run, steps, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;
            read = run.RowVersion.ToArray();

            steps.Single(step => step.StepKey == "start").DraftResourceKind = WorkflowResourceKind.CreativeContext;
            steps.Single(step => step.StepKey == "start").DraftResourceId = Guid.NewGuid();
            steps.Single(step => step.StepKey == "start").DraftSavedAt = Now;
            run.LastRoute = route;
            run.UpdatedAt = Now.AddMinutes(1);

            Assert.Equal(WorkflowRunWrite.Saved, await data.SaveAsync(run, read, Ct));
            Assert.NotEqual(read, run.RowVersion);
        }

        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            var (run, steps, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;
            var late = Guid.NewGuid();
            steps.Single(step => step.StepKey == "start").DraftResourceId = late;
            run.LastRoute = "social-studio";
            run.UpdatedAt = Now.AddMinutes(2);

            Assert.Equal(WorkflowRunWrite.Stale, await data.SaveAsync(run, read, Ct));

            var stored = await InAsync(WorkspaceA, check => check.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == seeded.Id, Ct));
            var start = await InAsync(WorkspaceA, check => check.WorkflowStepRuns.AsNoTracking()
                .SingleAsync(step => step.WorkflowRunId == seeded.Id && step.StepKey == "start", Ct));
            Assert.Equal(route, stored.LastRoute);
            Assert.Equal(WorkflowRunStatus.InProgress, stored.Status);
            Assert.NotEqual(late, start.DraftResourceId);
        }
    }

    /// <summary>
    /// Finishing where it counts (13A.10a): a completed run on no step is what SQL Server's own checks accept, written
    /// against the version read, and its steps and links stay exactly as they were.
    /// </summary>
    [Fact]
    public async Task A_finished_run_is_on_no_step_and_keeps_its_steps_and_links()
    {
        var seeded = await SeedRunAsync(WorkspaceA);

        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            var (run, _, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;
            var read = run.RowVersion.ToArray();

            run.Status = WorkflowRunStatus.Completed;
            run.CompletedAt = Now.AddMinutes(1);
            run.CurrentStepKey = null;
            run.UpdatedAt = Now.AddMinutes(1);

            Assert.Equal(WorkflowRunWrite.Saved, await data.SaveAsync(run, read, Ct));
            Assert.NotEqual(read, run.RowVersion);
        }

        var stored = await InAsync(WorkspaceA, db => db.WorkflowRuns.AsNoTracking().SingleAsync(item => item.Id == seeded.Id, Ct));
        Assert.Equal(WorkflowRunStatus.Completed, stored.Status);
        Assert.Null(stored.CurrentStepKey);
        Assert.Equal(2, await InAsync(WorkspaceA, db => db.WorkflowStepRuns.CountAsync(step => step.WorkflowRunId == seeded.Id, Ct)));
        Assert.Equal(1, await InAsync(WorkspaceA, db => db.WorkflowRunLinks.CountAsync(link => link.WorkflowRunId == seeded.Id, Ct)));
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Abandoned, true)]
    [InlineData(WorkflowRunStatus.InProgress, false)]
    [InlineData(WorkflowRunStatus.Completed, false)]
    public async Task Only_a_run_stopped_by_starting_over_points_at_its_replacement(WorkflowRunStatus status, bool accepted)
    {
        var run = WorkflowRunSeeds.Run(Now);
        run.Status = status;
        run.CurrentStepKey = status == WorkflowRunStatus.Completed ? null : run.CurrentStepKey;
        run.CompletedAt = status == WorkflowRunStatus.Completed ? Now : null;
        run.AbandonedAt = status == WorkflowRunStatus.Abandoned ? Now : null;
        run.ReplacedByRunId = Guid.NewGuid();

        var save = () => InAsync(WorkspaceA, async db =>
        {
            db.WorkflowRuns.Add(run);
            return await db.SaveChangesAsync(Ct);
        });

        if (accepted)
        {
            await save();
        }
        else
        {
            Assert.Contains(
                "CK_WorkflowRuns_ReplacedBy_Abandoned",
                (await Assert.ThrowsAsync<DbUpdateException>(save)).InnerException?.Message,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A Start over that loses (13A.9b) runs inside the idempotency executor's transaction, which commits after it
    /// answers with the winner. The new run was added in the same save as the refused UPDATE, so it must go with it:
    /// otherwise every lost race would leave a run nothing points at.
    /// </summary>
    [Fact]
    public async Task A_start_over_refused_as_stale_inside_a_transaction_that_commits_leaves_no_new_run()
    {
        var seeded = await SeedRunAsync(WorkspaceA);
        byte[] read;
        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            read = (await db.WorkflowRuns.AsNoTracking().SingleAsync(row => row.Id == seeded.Id, Ct)).RowVersion.ToArray();
            var winner = await db.WorkflowRuns.SingleAsync(row => row.Id == seeded.Id, Ct);
            winner.UpdatedAt = Now.AddMinutes(1);
            await db.SaveChangesAsync(Ct);
        }

        var replacement = WorkflowRunSeeds.Run(Now);
        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var (run, _, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = Now.AddMinutes(2);
            run.ReplacedByRunId = replacement.Id;
            run.UpdatedAt = Now.AddMinutes(2);

            var write = await data.RestartAsync(
                run, read, replacement, [WorkflowRunSeeds.Step(replacement, "start")], [WorkflowRunSeeds.Link(replacement, Now)], Ct);

            Assert.Equal(WorkflowRunWrite.Stale, write);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(0, await InAsync(WorkspaceA, db => CountAsync(db, "WorkflowRuns", WorkspaceA, replacement.Id)));
        var kept = await InAsync(WorkspaceA, db => db.WorkflowRuns.AsNoTracking().SingleAsync(row => row.Id == seeded.Id, Ct));
        Assert.Equal(WorkflowRunStatus.InProgress, kept.Status);
        Assert.Null(kept.ReplacedByRunId);
    }

    /// <summary>
    /// A stop where it counts (13A.10b): the run's stop and its audit row are one save, so a stop quoting a superseded
    /// token lands neither — no audit row records a stop that did not happen.
    /// </summary>
    [Fact]
    public async Task A_stop_lands_with_its_audit_row_and_a_stale_one_lands_neither()
    {
        var seeded = await SeedRunAsync(WorkspaceA);
        var stopper = Guid.NewGuid();
        var audit = new AuditEntry(
            "user-1", WorkflowAuditActions.RunAbandoned, WorkflowAuditActions.RunResourceType, seeded.Id.ToString("D"), Guid.NewGuid(), "Stopped.");

        byte[] read;
        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            var (run, _, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;
            read = run.RowVersion.ToArray();

            // A rival change lands first, so the stop below quotes a version the row no longer has.
            await InAsync(WorkspaceA, async rival =>
            {
                (await rival.WorkflowRuns.SingleAsync(row => row.Id == seeded.Id, Ct)).Title = "Renamed elsewhere";
                return await rival.SaveChangesAsync(Ct);
            });

            Stop(run, stopper, "Too busy this week");
            Assert.Equal(WorkflowRunWrite.Stale, await data.AbandonAsync(run, read, audit, Ct));
        }

        Assert.Equal(WorkflowRunStatus.InProgress, (await StoredAsync(seeded.Id)).Status);
        Assert.Equal(0, await AuditRowsAsync(seeded.Id));

        await using (var scope = Resolved(WorkspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var data = new WorkflowRunDataLayer(new WorkflowRunRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>());
            var (run, _, _) = (await data.GetForUpdateAsync(seeded.Id, Ct))!;

            Stop(run, stopper, "Too busy this week");
            Assert.Equal(WorkflowRunWrite.Saved, await data.AbandonAsync(run, run.RowVersion.ToArray(), audit, Ct));
        }

        var stored = await StoredAsync(seeded.Id);
        Assert.Equal(WorkflowRunStatus.Abandoned, stored.Status);
        Assert.Equal(stopper, stored.AbandonedByMembershipId);
        Assert.Equal("Too busy this week", stored.AbandonReason);
        Assert.Equal("start", stored.CurrentStepKey);
        Assert.Equal(1, await AuditRowsAsync(seeded.Id));
        Assert.Equal(2, await InAsync(WorkspaceA, db => db.WorkflowStepRuns.CountAsync(step => step.WorkflowRunId == seeded.Id, Ct)));
        Assert.Equal(1, await InAsync(WorkspaceA, db => db.WorkflowRunLinks.CountAsync(link => link.WorkflowRunId == seeded.Id, Ct)));

        static void Stop(WorkflowRun run, Guid by, string reason)
        {
            run.Status = WorkflowRunStatus.Abandoned;
            run.AbandonedAt = Now.AddMinutes(1);
            run.AbandonedByMembershipId = by;
            run.AbandonReason = reason;
            run.UpdatedAt = Now.AddMinutes(1);
        }
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Abandoned, "Not needed", true, true)]
    [InlineData(WorkflowRunStatus.Abandoned, null, true, true)]
    [InlineData(WorkflowRunStatus.Abandoned, "   ", false, false)]
    [InlineData(WorkflowRunStatus.InProgress, "Not needed", false, false)]
    [InlineData(WorkflowRunStatus.InProgress, null, true, false)]
    [InlineData(WorkflowRunStatus.Completed, null, true, false)]
    public async Task Who_stopped_a_run_and_why_belong_to_a_stopped_run_only(
        WorkflowRunStatus status, string? reason, bool stoppedBy, bool accepted)
    {
        var run = WorkflowRunSeeds.Run(Now);
        run.Status = status;
        run.CurrentStepKey = status == WorkflowRunStatus.Completed ? null : run.CurrentStepKey;
        run.CompletedAt = status == WorkflowRunStatus.Completed ? Now : null;
        run.AbandonedAt = status == WorkflowRunStatus.Abandoned ? Now : null;
        run.AbandonedByMembershipId = stoppedBy ? Guid.NewGuid() : null;
        run.AbandonReason = reason;

        var save = () => InAsync(WorkspaceA, async db =>
        {
            db.WorkflowRuns.Add(run);
            return await db.SaveChangesAsync(Ct);
        });

        if (accepted)
        {
            await save();
        }
        else
        {
            Assert.Matches(
                "CK_WorkflowRuns_Abandon(edBy|Reason)_Abandoned",
                (await Assert.ThrowsAsync<DbUpdateException>(save)).InnerException?.Message ?? string.Empty);
        }
    }

    /// <summary>
    /// The run list where it counts (13A.11): what needs a look is matched on strings SQL Server builds from a run's own
    /// columns against list parameters, and a keyset over <c>(UpdatedAt, Id)</c> must page through runs that share a
    /// moment in SQL Server's own <c>uniqueidentifier</c> order — neither of which SQLite can vouch for.
    /// </summary>
    [Fact]
    public async Task The_list_finds_what_needs_a_look_and_pages_in_sql_servers_own_dialect()
    {
        var shared = Now.AddMinutes(-30);
        var outdated = await SeedListedAsync(WorkspaceA, run => (run.DefinitionVersion, run.UpdatedAt) = (1, Now.AddMinutes(-2)));
        var blocked = await SeedListedAsync(WorkspaceA, run => (run.CurrentStepKey, run.UpdatedAt) = ("ingredients", Now.AddMinutes(-1)));
        var idle = await SeedListedAsync(WorkspaceA, run => run.UpdatedAt = Now.AddDays(-10));
        await SeedListedAsync(WorkspaceA, run => run.UpdatedAt = shared);
        await SeedListedAsync(WorkspaceA, run => run.UpdatedAt = shared);
        await SeedListedAsync(WorkspaceA, run =>
        {
            (run.DefinitionVersion, run.UpdatedAt, run.Status, run.CurrentStepKey) = (1, shared, WorkflowRunStatus.Completed, null);
            run.CompletedAt = shared;
        });
        var theirs = await SeedListedAsync(WorkspaceB, run => run.DefinitionVersion = 1);

        var attention = new WorkflowRunAttentionCriteria(
            Now - WorkflowRunPolicy.IdleAfter,
            [WorkflowRunAttentionCriteria.VersionKey("recipe.create", 1)],
            [WorkflowRunAttentionCriteria.StepKey("recipe.create", 2, "ingredients")]);

        var needing = await ListAsync(WorkflowRunListView.Attention, attention, from: null, limit: 100);
        Assert.Equal([idle, outdated, blocked], needing.Rows.Select(row => row.Id));

        // Paged two at a time, the whole list comes back once each and in the order one page of it has.
        var whole = (await ListAsync(WorkflowRunListView.All, attention, from: null, limit: 100)).Rows.Select(row => row.Id).ToList();
        Assert.Equal(6, whole.Count);
        Assert.DoesNotContain(theirs, whole);

        var paged = new List<Guid>();
        WorkflowRunListPosition? position = null;
        bool more;
        do
        {
            var (rows, hasMore) = await ListAsync(WorkflowRunListView.All, attention, position, limit: 2);
            paged.AddRange(rows.Select(row => row.Id));
            position = rows.Count > 0 ? new WorkflowRunListPosition(rows[^1].UpdatedAt, rows[^1].Id) : null;
            more = hasMore;
        }
        while (more);

        Assert.Equal(whole, paged);

        Task<(IReadOnlyList<WorkflowRunSummaryRecord> Rows, bool HasMore)> ListAsync(
            WorkflowRunListView view, WorkflowRunAttentionCriteria criteria, WorkflowRunListPosition? from, int limit) =>
            InAsync(WorkspaceA, db => new WorkflowRunListRepository(db).ListAsync(
                new WorkflowRunListCriteria(view, null, Guid.NewGuid(), criteria, "test", from, limit), Ct));
    }

    private Task<Guid> SeedListedAsync(Guid workspaceId, Action<WorkflowRun> shape) =>
        InAsync(workspaceId, async db =>
        {
            var run = WorkflowRunSeeds.Run(Now);
            run.DefinitionVersion = 2;
            shape(run);
            db.WorkflowRuns.Add(run);
            await db.SaveChangesAsync(Ct);

            return run.Id;
        });

    private Task<WorkflowRun> StoredAsync(Guid runId) =>
        InAsync(WorkspaceA, db => db.WorkflowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, Ct));

    private Task<int> AuditRowsAsync(Guid runId) =>
        InAsync(WorkspaceA, db => db.AuditLogs.CountAsync(
            row => row.Action == WorkflowAuditActions.RunAbandoned && row.ResourceId == runId.ToString("D"), Ct));

    [Fact]
    public async Task Only_assigned_runs_are_in_the_assignee_index()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var filtered = await db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sys.indexes WHERE name = {0} AND has_filter = 1",
                "IX_WorkflowRuns_Workspace_Assignee_Status_Updated")
            .SingleAsync(Ct);

        Assert.Equal(1, filtered);
    }

    [Fact]
    public async Task Erasing_a_workspace_erases_its_runs_and_nothing_of_anyone_elses()
    {
        var workspaceId = Guid.NewGuid();
        await using (var seed = _provider!.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Doomed", Slug = "doomed", CreatedAt = Now });
            await db.SaveChangesAsync(Ct);
        }

        await SeedRunAsync(workspaceId, "doomed");
        var survivor = await SeedRunAsync(WorkspaceB);

        // The workspace row is not workspace-owned, so it is removed from an unresolved scope.
        await using var scope = _provider!.CreateAsyncScope();
        var unresolved = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        unresolved.Workspaces.Remove(await unresolved.Workspaces.SingleAsync(item => item.Id == workspaceId, Ct));
        await unresolved.SaveChangesAsync(Ct);

        // Counted with raw SQL rather than IgnoreQueryFilters: the erasure is what is under test.
        foreach (var table in new[] { "WorkflowRuns", "WorkflowStepRuns", "WorkflowRunLinks" })
        {
            Assert.Equal(0, await CountAsync(unresolved, table, workspaceId));
        }

        Assert.Equal(1, await CountAsync(unresolved, "WorkflowRuns", WorkspaceB, survivor.Id));
    }

    private static Task<int> CountAsync(CreatorPantryDbContext db, string table, Guid workspaceId, Guid? runId = null) =>
        runId is { } id
            ? db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table} WHERE WorkspaceId = {{0}} AND Id = {{1}}", workspaceId, id).SingleAsync(Ct)
            : db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table} WHERE WorkspaceId = {{0}}", workspaceId).SingleAsync(Ct);

    private Task<WorkflowRun> SeedRunAsync(Guid workspaceId, string? slug = null) =>
        InAsync(workspaceId, async db =>
        {
            var run = WorkflowRunSeeds.Run(Now);
            db.WorkflowRuns.Add(run);
            db.WorkflowStepRuns.AddRange(WorkflowRunSeeds.Step(run, "start"), WorkflowRunSeeds.Step(run, "ingredients"));
            db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now));
            await db.SaveChangesAsync(Ct);

            return run;
        }, slug);

    private AsyncServiceScope Resolved(Guid workspaceId, string? slug = null)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            slug ?? (workspaceId == WorkspaceA ? "workspace-a" : "workspace-b"),
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "user-1");

        return scope;
    }

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work, string? slug = null)
    {
        await using var scope = Resolved(workspaceId, slug);

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
