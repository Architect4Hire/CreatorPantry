using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The workflow run's schema and isolation over the two-workspace SQLite fixture (13A.2): the check constraints,
/// the workspace-paired keys, the uniqueness, the cascade and the query filter. Not that SQL Server accepts the
/// DDL or bumps the row version — the migration is 13A.3's, and a SQL Server test arrives with it.
/// </summary>
public sealed class WorkflowRunAggregateTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;

    private static readonly Guid A = RecipeAggregateFixture.WorkspaceA;

    private static readonly Guid B = RecipeAggregateFixture.WorkspaceB;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_its_steps_and_its_links_save_and_read_back_in_their_workspace()
    {
        var run = await SeedRunAsync(A, withParts: true);

        await InAsync(A, async db =>
        {
            var stored = await db.WorkflowRuns.SingleAsync(row => row.Id == run.Id, Ct);
            Assert.Equal(A, stored.WorkspaceId);
            Assert.NotEmpty(stored.RowVersion);
            Assert.Equal(2, await db.WorkflowStepRuns.CountAsync(step => step.WorkflowRunId == run.Id, Ct));
            Assert.Single(await db.WorkflowRunLinks.Where(link => link.WorkflowRunId == run.Id).ToListAsync(Ct));

            return 0;
        });
    }

    [Fact]
    public async Task Another_workspaces_run_steps_and_links_are_not_there_to_read()
    {
        var theirs = await SeedRunAsync(B, withParts: true);

        await InAsync(A, async db =>
        {
            Assert.Null(await db.WorkflowRuns.FirstOrDefaultAsync(run => run.Id == theirs.Id, Ct));
            Assert.Empty(await db.WorkflowStepRuns.Where(step => step.WorkflowRunId == theirs.Id).ToListAsync(Ct));
            Assert.Empty(await db.WorkflowRunLinks.Where(link => link.WorkflowRunId == theirs.Id).ToListAsync(Ct));

            return 0;
        });
    }

    [Fact]
    public async Task A_step_or_link_stamped_for_one_workspace_cannot_hang_off_anothers_run()
    {
        var theirs = await SeedRunAsync(B);

        // Stamped for A by the interceptor, so (A, theirs.Id) names no run and the composite key refuses it.
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(A, db => db.WorkflowStepRuns.Add(WorkflowRunSeeds.Step(theirs, "start"))));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(A, db => db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(theirs, Now))));
    }

    [Fact]
    public async Task A_run_cannot_be_written_into_another_workspace_or_moved_to_one()
    {
        // Named for B while A is resolved: the interceptor refuses it before the database is asked.
        var foreign = WorkflowRunSeeds.Run(Now);
        foreign.WorkspaceId = B;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(A, db => db.WorkflowRuns.Add(foreign)));

        // Ownership is immutable: a loaded run cannot be re-pointed at another workspace.
        var run = await SeedRunAsync(A);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveAsync(A, db => db.WorkflowRuns.Single(row => row.Id == run.Id).WorkspaceId = B));

        // And from B, A's run is not there to remove.
        await InAsync(B, async db =>
        {
            Assert.Null(await db.WorkflowRuns.FirstOrDefaultAsync(row => row.Id == run.Id, Ct));

            return 0;
        });
    }

    [Fact]
    public async Task A_step_key_appears_once_per_run_and_freely_across_runs()
    {
        var first = await SeedRunAsync(A);
        var second = await SeedRunAsync(A);

        await SaveAsync(A, db => db.WorkflowStepRuns.Add(WorkflowRunSeeds.Step(first, "start")));
        await SaveAsync(A, db => db.WorkflowStepRuns.Add(WorkflowRunSeeds.Step(second, "start")));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(A, db => db.WorkflowStepRuns.Add(WorkflowRunSeeds.Step(first, "start"))));
    }

    [Fact]
    public async Task A_record_is_linked_to_a_run_once()
    {
        var run = await SeedRunAsync(A);
        var recipeId = Guid.NewGuid();

        await SaveAsync(A, db => db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now, recipeId)));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            SaveAsync(A, db => db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now, recipeId, stepKey: "ingredients"))));
    }

    [Fact]
    public async Task A_link_is_a_pointer_and_not_a_foreign_key()
    {
        // No recipe has this id. The link saves anyway: a run neither cascades into a recipe nor stands in the way
        // of one, and a record that is gone is a warning when the run is read (13A.6).
        var run = await SeedRunAsync(A);

        await SaveAsync(A, db => db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now, Guid.NewGuid())));
    }

    [Fact]
    public async Task Deleting_a_run_takes_its_steps_and_links_with_it()
    {
        var run = await SeedRunAsync(A, withParts: true);

        await SaveAsync(A, db => db.WorkflowRuns.Remove(db.WorkflowRuns.Single(row => row.Id == run.Id)));

        await InAsync(A, async db =>
        {
            Assert.Empty(await db.WorkflowStepRuns.Where(step => step.WorkflowRunId == run.Id).ToListAsync(Ct));
            Assert.Empty(await db.WorkflowRunLinks.Where(link => link.WorkflowRunId == run.Id).ToListAsync(Ct));

            return 0;
        });
    }

    public static TheoryData<string> WellFormedRuns() => ["in progress", "saved and left", "completed", "abandoned", "abandoned before a step", "assigned"];

    [Theory]
    [MemberData(nameof(WellFormedRuns))]
    public async Task A_well_formed_run_saves(string shape)
    {
        var run = WorkflowRunSeeds.Run(Now);
        switch (shape)
        {
            case "saved and left":
                run.LastRoute = "recipes/2f1a/kitchen";
                run.CompletedStepCount = 2;
                run.SkippedStepCount = 1;
                break;
            case "completed":
                run.Status = WorkflowRunStatus.Completed;
                run.CurrentStepKey = null;
                run.CompletedAt = Now;
                run.CompletedStepCount = 4;
                run.SkippedStepCount = 1;
                break;
            case "abandoned":
                run.Status = WorkflowRunStatus.Abandoned;
                run.AbandonedAt = Now;
                break;
            case "abandoned before a step":
                run.Status = WorkflowRunStatus.Abandoned;
                run.CurrentStepKey = null;
                run.AbandonedAt = Now;
                break;
            case "assigned":
                run.AssignedMembershipId = Guid.NewGuid();
                break;
        }

        await SaveAsync(A, db => db.WorkflowRuns.Add(run));
    }

    public static TheoryData<string> MisshapenRuns() =>
    [
        "status out of range", "blank key", "version zero", "blank title", "no steps", "negative progress",
        "progress past the end", "in progress on no step", "blank current step", "completed on a step",
        "completed with no time", "completed time while in progress", "abandoned with no time",
        "abandoned time while in progress", "rooted route", "scheme route", "blank route", "backslash route",
        "climbing route",
    ];

    [Theory]
    [MemberData(nameof(MisshapenRuns))]
    public async Task A_misshapen_run_is_refused(string shape)
    {
        var run = WorkflowRunSeeds.Run(Now);
        switch (shape)
        {
            case "status out of range": run.Status = (WorkflowRunStatus)9; break;
            case "blank key": run.DefinitionKey = " "; break;
            case "version zero": run.DefinitionVersion = 0; break;
            case "blank title": run.Title = "  "; break;
            case "no steps": run.StepCount = 0; break;
            case "negative progress": run.CompletedStepCount = -1; break;
            case "progress past the end": run.CompletedStepCount = 4; run.SkippedStepCount = 2; break;
            case "in progress on no step": run.CurrentStepKey = null; break;
            case "blank current step": run.CurrentStepKey = " "; break;
            case "completed on a step": run.Status = WorkflowRunStatus.Completed; run.CompletedAt = Now; break;
            case "completed with no time": run.Status = WorkflowRunStatus.Completed; run.CurrentStepKey = null; break;
            case "completed time while in progress": run.CompletedAt = Now; break;
            case "abandoned with no time": run.Status = WorkflowRunStatus.Abandoned; break;
            case "abandoned time while in progress": run.AbandonedAt = Now; break;
            case "rooted route": run.LastRoute = "/recipes"; break;
            case "scheme route": run.LastRoute = "javascript:alert(1)"; break;
            case "blank route": run.LastRoute = " "; break;
            case "backslash route": run.LastRoute = @"\\evil.example/recipes"; break;
            case "climbing route": run.LastRoute = "recipes/../../other-workspace/recipes"; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(A, db => db.WorkflowRuns.Add(run)));
    }

    public static TheoryData<string> WellFormedSteps() => ["not started", "in progress", "completed", "skipped", "skipped with a reason", "completed without a start", "with a draft"];

    [Theory]
    [MemberData(nameof(WellFormedSteps))]
    public async Task A_well_formed_step_saves(string shape)
    {
        var run = await SeedRunAsync(A);
        var step = Shape(WorkflowRunSeeds.Step(run, "start"), shape);

        await SaveAsync(A, db => db.WorkflowStepRuns.Add(step));
    }

    public static TheoryData<string> MisshapenSteps() =>
    [
        "status out of range", "blank key", "started while not started", "completed with no time",
        "completed with no one", "completion recorded while in progress", "skipped with no time",
        "skipped with no one", "skip recorded while in progress", "a reason with no skip", "blank reason",
        "draft kind alone", "draft id alone", "draft with no save time", "draft save time alone", "draft kind out of range",
    ];

    [Theory]
    [MemberData(nameof(MisshapenSteps))]
    public async Task A_misshapen_step_is_refused(string shape)
    {
        var run = await SeedRunAsync(A);
        var step = Shape(WorkflowRunSeeds.Step(run, "start"), shape);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(A, db => db.WorkflowStepRuns.Add(step)));
    }

    [Theory]
    [InlineData("kind out of range")]
    [InlineData("blank step")]
    public async Task A_misshapen_link_is_refused(string shape)
    {
        var run = await SeedRunAsync(A);
        var link = WorkflowRunSeeds.Link(run, Now);
        if (shape == "kind out of range")
        {
            link.ResourceKind = (WorkflowResourceKind)0;
        }
        else
        {
            link.StepKey = " ";
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(A, db => db.WorkflowRunLinks.Add(link)));
    }

    /// <summary>
    /// Every kind the contract names can be linked (13A.5 widened the checks from Recipe alone). The range is read
    /// from the enum, so a kind added later is a model change a migration has to capture.
    /// </summary>
    [Fact]
    public async Task Every_kind_the_contract_names_can_be_linked()
    {
        var run = await SeedRunAsync(A);

        foreach (var kind in Enum.GetValues<WorkflowResourceKind>())
        {
            var link = WorkflowRunSeeds.Link(run, Now);
            link.ResourceKind = kind;
            await SaveAsync(A, db => db.WorkflowRunLinks.Add(link));
        }
    }

    [Fact]
    public async Task A_link_with_no_step_is_the_record_the_run_started_from()
    {
        var run = await SeedRunAsync(A);

        await SaveAsync(A, db => db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now, stepKey: null)));
    }

    private static WorkflowStepRun Shape(WorkflowStepRun step, string shape)
    {
        var who = Guid.NewGuid();

        switch (shape)
        {
            case "not started":
                break;
            case "in progress":
                step.Status = WorkflowStepRunStatus.InProgress;
                step.StartedAt = Now;
                break;
            case "completed":
                step.Status = WorkflowStepRunStatus.Completed;
                step.StartedAt = Now;
                step.CompletedAt = Now;
                step.CompletedByMembershipId = who;
                break;
            case "completed without a start":
                step.Status = WorkflowStepRunStatus.Completed;
                step.CompletedAt = Now;
                step.CompletedByMembershipId = who;
                break;
            case "skipped":
                step.Status = WorkflowStepRunStatus.Skipped;
                step.SkippedAt = Now;
                step.SkippedByMembershipId = who;
                break;
            case "skipped with a reason":
                step.Status = WorkflowStepRunStatus.Skipped;
                step.SkippedAt = Now;
                step.SkippedByMembershipId = who;
                step.SkipReason = "I already know my yield.";
                break;
            case "with a draft":
                step.Status = WorkflowStepRunStatus.InProgress;
                step.StartedAt = Now;
                step.DraftResourceKind = WorkflowResourceKind.Recipe;
                step.DraftResourceId = Guid.NewGuid();
                step.DraftSavedAt = Now;
                break;

            case "status out of range": step.Status = (WorkflowStepRunStatus)0; break;
            case "blank key": step.StepKey = "  "; break;
            case "started while not started": step.StartedAt = Now; break;
            case "completed with no time": step.Status = WorkflowStepRunStatus.Completed; step.CompletedByMembershipId = who; break;
            case "completed with no one": step.Status = WorkflowStepRunStatus.Completed; step.CompletedAt = Now; break;
            case "completion recorded while in progress":
                step.Status = WorkflowStepRunStatus.InProgress;
                step.CompletedAt = Now;
                step.CompletedByMembershipId = who;
                break;
            case "skipped with no time": step.Status = WorkflowStepRunStatus.Skipped; step.SkippedByMembershipId = who; break;
            case "skipped with no one": step.Status = WorkflowStepRunStatus.Skipped; step.SkippedAt = Now; break;
            case "skip recorded while in progress":
                step.Status = WorkflowStepRunStatus.InProgress;
                step.SkippedAt = Now;
                step.SkippedByMembershipId = who;
                break;
            case "a reason with no skip": step.SkipReason = "Not today."; break;
            case "blank reason":
                step.Status = WorkflowStepRunStatus.Skipped;
                step.SkippedAt = Now;
                step.SkippedByMembershipId = who;
                step.SkipReason = " ";
                break;
            case "draft kind alone": step.DraftResourceKind = WorkflowResourceKind.Recipe; break;
            case "draft id alone": step.DraftResourceId = Guid.NewGuid(); break;
            case "draft with no save time":
                step.DraftResourceKind = WorkflowResourceKind.Recipe;
                step.DraftResourceId = Guid.NewGuid();
                break;
            case "draft save time alone": step.DraftSavedAt = Now; break;
            case "draft kind out of range":
                step.DraftResourceKind = (WorkflowResourceKind)99;
                step.DraftResourceId = Guid.NewGuid();
                step.DraftSavedAt = Now;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        return step;
    }

    private Task<WorkflowRun> SeedRunAsync(Guid workspaceId, bool withParts = false) =>
        InAsync(workspaceId, async db =>
        {
            var run = WorkflowRunSeeds.Run(Now);
            db.WorkflowRuns.Add(run);

            if (withParts)
            {
                db.WorkflowStepRuns.AddRange(WorkflowRunSeeds.Step(run, "start"), WorkflowRunSeeds.Step(run, "ingredients"));
                db.WorkflowRunLinks.Add(WorkflowRunSeeds.Link(run, Now));
            }

            await db.SaveChangesAsync(Ct);

            return run;
        });

    private Task SaveAsync(Guid workspaceId, Action<CreatorPantryDbContext> change) =>
        InAsync(workspaceId, async db =>
        {
            change(db);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);

        return await work(RecipeAggregateFixture.Db(scope));
    }
}
