using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The test-run data layer's composed operations, against a real SQL Server with the schema built by the
/// migrations.
/// </summary>
/// <remarks>
/// Two questions only this layer can answer. For the target lookup: whether "the recipe is not visible" and
/// "that version does not exist" stay separable, since one 404 hides a workspace and the other names a field.
/// For the write: whether it can ever half-save — a run without its issues is a record of a cook that reported
/// nothing wrong, which is a different and wrong claim.
/// </remarks>
public sealed class RecipeTestRunDataLayerTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static IRecipeTestRunDataLayer DataLayer(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeTestRunDataLayer>();

    [Fact]
    public async Task A_create_writes_the_run_its_observations_and_its_issues_together()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, "Together");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        var observation = NewObservation(run.Id, 0, "The crumb was close.");
        run.Observations.Add(observation);
        run.Issues.Add(NewIssue(run, observation.Id));

        var created = await DataLayer(scope).CreateAsync(run, token);

        Assert.Equal(run.Id, created.TestRunId);
        Assert.Single(created.ObservationIds);
        Assert.Single(created.IssueIds);

        // Read back in a fresh scope so nothing is answered from the change tracker.
        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(readScope);

        var stored = await db.RecipeTestRuns
            .Include(candidate => candidate.Observations)
            .Include(candidate => candidate.Issues)
            .SingleAsync(candidate => candidate.Id == run.Id, token);

        Assert.Equal(seeded.VersionId, stored.RecipeVersionId);
        Assert.Equal("The crumb was close.", stored.Observations.Single().Text);

        // The issue kept the link to the note it came from, across the save.
        Assert.Equal(stored.Observations.Single().Id, stored.Issues.Single().TestObservationId);

        // Stamped by the ownership interceptor rather than by anything above it.
        Assert.Equal(SqlServerRecipeFixture.WorkspaceA, stored.WorkspaceId);
        Assert.All(stored.Issues, issue => Assert.Equal(SqlServerRecipeFixture.WorkspaceA, issue.WorkspaceId));

        // Server-generated, and what a later update will have to quote.
        Assert.NotEmpty(stored.RowVersion);
    }

    [Fact]
    public async Task An_invisible_recipe_and_a_missing_version_are_different_answers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, "Separable");

        // A recipe nobody in this workspace can see: null, with nothing said about its versions.
        Assert.Null(await DataLayer(scope).FindTargetAsync(Guid.NewGuid(), 1, token));

        // A visible recipe and a version it does not have: a real target whose version id is null, which is
        // what lets the refusal name the field the caller sent instead of hiding the recipe.
        var missingVersion = await DataLayer(scope).FindTargetAsync(seeded.RecipeId, 99, token);
        Assert.NotNull(missingVersion);
        Assert.Null(missingVersion!.VersionId);

        var found = await DataLayer(scope).FindTargetAsync(seeded.RecipeId, RecipePolicy.FirstVersionNumber, token);
        Assert.Equal(seeded.VersionId, found!.VersionId);
        Assert.Equal(RecipeStatus.Draft, found.Status);
    }

    [Fact]
    public async Task Another_workspaces_recipe_is_invisible_to_the_target_lookup()
    {
        var token = TestContext.Current.CancellationToken;

        SeededTestTarget seeded;
        await using (var owner = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB))
        {
            seeded = await SeedRecipeAsync(owner, "B's recipe", tagId: SqlServerRecipeFixture.TagIdB);
        }

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        // Not "found and refused" — not found, which is what makes the 404 above indistinguishable from an
        // unknown id.
        Assert.Null(await DataLayer(scope).FindTargetAsync(seeded.RecipeId, RecipePolicy.FirstVersionNumber, token));
    }

    [Fact]
    public async Task The_target_lookup_reports_an_archived_recipe_rather_than_hiding_it()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, "Shelved", RecipeStatus.Archived);

        // Archived is a state, not an absence: the recipe is readable and the refusal it earns is a 409 whose
        // remedy is to bring it back, so this lookup must not answer null.
        var target = await DataLayer(scope).FindTargetAsync(seeded.RecipeId, RecipePolicy.FirstVersionNumber, token);

        Assert.Equal(RecipeStatus.Archived, target!.Status);
        Assert.Equal(seeded.VersionId, target.VersionId);
    }

    [Fact]
    public async Task A_run_edited_by_somebody_else_refuses_the_losing_save()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, "Race");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.Observations.Add(NewObservation(run.Id, 0, "First write-up."));
        await DataLayer(scope).CreateAsync(run, token);

        // Two people with the same test open. Each scope has its own DbContext, so each holds its own copy of
        // the run at the row version it read — which is the situation a shared tracker would hide.
        await using var first = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        await using var second = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var mine = await DataLayer(first).GetForUpdateAsync(seeded.RecipeId, run.Id, token);
        var theirs = await DataLayer(second).GetForUpdateAsync(seeded.RecipeId, run.Id, token);

        theirs!.Rating = 5;
        Assert.True((await DataLayer(second).UpdateAsync(theirs, token)).Succeeded);

        // The loser quotes a row version the database has moved past, so its UPDATE matches no row.
        mine!.Rating = 1;
        Assert.False((await DataLayer(first).UpdateAsync(mine, token)).Succeeded);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(readScope);
        var stored = await db.RecipeTestRuns.SingleAsync(candidate => candidate.Id == run.Id, token);

        // The winner's edit stands whole, and the loser wrote nothing at all.
        Assert.Equal(5, stored.Rating);
    }

    [Fact]
    public async Task A_refused_save_leaves_nothing_staged_for_the_next_one()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, "Nothing staged");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        await DataLayer(scope).CreateAsync(run, token);

        await using var loser = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var mine = await DataLayer(loser).GetForUpdateAsync(seeded.RecipeId, run.Id, token);

        await using (var winner = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var theirs = await DataLayer(winner).GetForUpdateAsync(seeded.RecipeId, run.Id, token);
            theirs!.SummaryNotes = "Theirs.";
            await DataLayer(winner).UpdateAsync(theirs, token);
        }

        // Refused, and the tracker cleared with it. Without that, a second save on this scope would commit the
        // edit its caller was already told had failed — which is why the DataLayer clears rather than leaving
        // it to whoever saves next.
        mine!.SummaryNotes = "Mine.";
        Assert.False((await DataLayer(loser).UpdateAsync(mine, token)).Succeeded);

        var db = SqlServerRecipeFixture.Db(loser);
        Assert.Empty(db.ChangeTracker.Entries());
        await db.SaveChangesAsync(token);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var stored = await SqlServerRecipeFixture.Db(readScope).RecipeTestRuns
            .SingleAsync(candidate => candidate.Id == run.Id, token);

        Assert.Equal("Theirs.", stored.SummaryNotes);
    }

    [Fact]
    public async Task An_issue_cannot_be_resolved_twice_even_when_two_requests_race()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, "Resolve race");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        var issue = NewIssue(run, observationId: null);
        run.Issues.Add(issue);
        await DataLayer(scope).CreateAsync(run, token);

        // Both requests read the issue as unresolved, which is the race the unique index exists to settle —
        // the readable check in Business cannot close it.
        await using var first = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        await using var second = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var beforeFirst = await DataLayer(first)
            .FindIssueForResolutionAsync(seeded.RecipeId, run.Id, issue.Id, token);
        var beforeSecond = await DataLayer(second)
            .FindIssueForResolutionAsync(seeded.RecipeId, run.Id, issue.Id, token);

        Assert.False(beforeFirst!.AlreadyResolved);
        Assert.False(beforeSecond!.AlreadyResolved);

        // And the tested version's number came back with them, which is what a correction version is judged
        // against.
        Assert.Equal(RecipePolicy.FirstVersionNumber, beforeFirst.TestedVersionNumber);

        Assert.True((await DataLayer(first).ResolveAsync(NewResolution(issue), token)).Succeeded);

        // The second is refused by UX_TestIssueResolutions_Workspace_Recipe_Issue, translated to an outcome
        // rather than allowed to surface as a fault.
        Assert.False((await DataLayer(second).ResolveAsync(NewResolution(issue), token)).Succeeded);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(readScope);

        Assert.Equal(
            1,
            await db.TestIssueResolutions.CountAsync(
                resolution => resolution.TestIssueId == issue.Id, token));
    }

    [Fact]
    public async Task Another_workspace_cannot_load_a_run_for_editing()
    {
        var token = TestContext.Current.CancellationToken;

        SeededTestTarget seeded;
        Guid runId;

        await using (var owner = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB))
        {
            seeded = await SeedRecipeAsync(owner, "B's test", tagId: SqlServerRecipeFixture.TagIdB);
            var run = NewRun(seeded.RecipeId, seeded.VersionId);
            await DataLayer(owner).CreateAsync(run, token);
            runId = run.Id;
        }

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        // Not found rather than found and refused, which is what makes the route's 404 the same answer for an
        // unknown id and somebody else's.
        Assert.Null(await DataLayer(scope).GetForUpdateAsync(seeded.RecipeId, runId, token));
        Assert.Null(await DataLayer(scope).FindIssueForResolutionAsync(
            seeded.RecipeId, runId, Guid.NewGuid(), token));
    }

    [Fact]
    public async Task An_edit_that_adds_updates_and_removes_children_commits_as_one()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, "Reconcile");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        var keep = NewObservation(run.Id, 0, "Kept.");
        var drop = NewObservation(run.Id, 1, "Dropped.");
        run.Observations.Add(keep);
        run.Observations.Add(drop);
        run.Issues.Add(NewIssue(run, keep.Id));
        await DataLayer(scope).CreateAsync(run, token);

        await using var editScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await DataLayer(editScope).GetForUpdateAsync(seeded.RecipeId, run.Id, token);

        // One save doing all three things at once — an update, an insert and a delete against a parent whose own
        // row version is being checked. That combination is what the endpoint tests cannot exercise: under
        // SQLite the row version is a default expression rather than a server-maintained token, and a batch
        // mixing child writes with a version-checked parent update reports a row count EF reads as a conflict.
        var existing = loaded!.Observations.Single(observation => observation.Text == "Kept.");
        existing.Text = "Kept and reworded.";
        loaded.Observations.Remove(loaded.Observations.Single(observation => observation.Text == "Dropped."));
        loaded.Observations.Add(new TestObservation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = loaded.WorkspaceId,
            RecipeTestRunId = loaded.Id,
            Kind = TestObservationKind.Appearance,
            Text = "Added.",
            SortOrder = 1,
        });
        loaded.Issues.Single().Severity = TestIssueSeverity.Blocking;
        loaded.Rating = 5;

        Assert.True((await DataLayer(editScope).UpdateAsync(loaded, token)).Succeeded);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var stored = await SqlServerRecipeFixture.Db(readScope).RecipeTestRuns
            .Include(candidate => candidate.Observations)
            .Include(candidate => candidate.Issues)
            .SingleAsync(candidate => candidate.Id == run.Id, token);

        Assert.Equal(5, stored.Rating);
        Assert.Equal(
            ["Kept and reworded.", "Added."],
            [.. stored.Observations.OrderBy(observation => observation.SortOrder).Select(observation => observation.Text)]);
        Assert.Equal(TestIssueSeverity.Blocking, stored.Issues.Single().Severity);

        // The issue still points at the note that survived, which is what keeping ids through a reconciliation
        // is for.
        Assert.Equal(existing.Id, stored.Issues.Single().TestObservationId);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task Each_kind_of_child_change_commits_on_its_own(string operation)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var seeded = await SeedRecipeAsync(scope, $"Isolate {operation}");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.Observations.Add(NewObservation(run.Id, 0, "First."));
        await DataLayer(scope).CreateAsync(run, token);

        await using var editScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await DataLayer(editScope).GetForUpdateAsync(seeded.RecipeId, run.Id, token);

        switch (operation)
        {
            case "update":
                loaded!.Observations.Single().Text = "Reworded.";
                break;
            case "add":
                loaded!.Observations.Add(new TestObservation
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = loaded.WorkspaceId,
                    RecipeTestRunId = loaded.Id,
                    Kind = TestObservationKind.Aroma,
                    Text = "Second.",
                    SortOrder = 1,
                });
                break;
            default:
                loaded!.Observations.Remove(loaded.Observations.Single());
                break;
        }

        // A new child must be tracked as Added, not Modified. Asserted because the distinction is invisible
        // until it fails: with a Guid key left store-generated by convention, EF reads a pre-assigned id as
        // evidence the row might already exist, and the UPDATE it then sends matches nothing — reported as a
        // concurrency conflict nobody caused. TestObservationConfiguration's ValueGeneratedNever is what
        // prevents it.
        Assert.DoesNotContain(
            Microsoft.EntityFrameworkCore.EntityState.Modified,
            SqlServerRecipeFixture.Db(editScope).ChangeTracker.Entries<TestObservation>()
                .Where(entry => entry.Entity.Text is "Second.")
                .Select(entry => entry.State));

        Assert.True((await DataLayer(editScope).UpdateAsync(loaded!, token)).Succeeded);
    }

    private static TestIssueResolution NewResolution(TestIssue issue) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = issue.RecipeId,
        TestIssueId = issue.Id,
        Kind = TestIssueResolutionKind.WontFix,
        ResolvedByMembershipId = Guid.NewGuid(),
        ResolvedAt = SqlServerRecipeFixture.Now,
    };

    /// <summary>
    /// The read the detail route is built on, at the layer where the query filter either holds or does not.
    /// </summary>
    /// <remarks>
    /// Asserted against the method rather than against <c>db.RecipeTestRuns</c>, which is a different claim: this
    /// read carries three <c>Include</c>s, and an `Include` of a set that stopped being workspace-owned would
    /// still pass a test that only queried the root. The positive half is in the same test so that "null" is
    /// known to mean isolation rather than a seed that never wrote anything.
    /// </remarks>
    [Fact]
    public async Task A_read_of_one_test_cannot_reach_another_workspaces_run()
    {
        var token = TestContext.Current.CancellationToken;

        Guid foreignRecipeId;
        Guid foreignRunId;

        await using (var inB = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB))
        {
            // B's own tag, because the default is A's and tags are workspace-owned: the database refuses the
            // cross-workspace foreign key, which is the isolation working rather than a fixture to route around.
            var seeded = await SeedRecipeAsync(inB, "B's own bake", tagId: SqlServerRecipeFixture.TagIdB);
            var run = NewRun(seeded.RecipeId, seeded.VersionId);
            var observation = NewObservation(run.Id, 0, "B's crumb was close.");
            run.Observations.Add(observation);
            run.Issues.Add(NewIssue(run, observation.Id));

            await DataLayer(inB).CreateAsync(run, token);

            foreignRecipeId = seeded.RecipeId;
            foreignRunId = run.Id;

            // B reads its own, so the refusal below is isolation rather than a run that was never written.
            var own = await DataLayer(inB).GetForReadAsync(seeded.RecipeId, run.Id, token);
            Assert.NotNull(own);
            Assert.Single(own.Observations);
            Assert.Single(own.Issues);
        }

        await using var inA = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var ownRecipe = await SeedRecipeAsync(inA, "A's own bake");

        // Both of B's ids together, which is the strongest form of the ask: nothing is guessed.
        Assert.Null(await DataLayer(inA).GetForReadAsync(foreignRecipeId, foreignRunId, token));

        // And B's run id under A's own readable recipe, which is the case the recipe-visibility check in
        // Business cannot catch — only the query filter can.
        Assert.Null(await DataLayer(inA).GetForReadAsync(ownRecipe.RecipeId, foreignRunId, token));
    }

    private static RecipeTestRun NewRun(Guid recipeId, Guid versionId) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipeId,
        RecipeVersionId = versionId,
        TestedAt = SqlServerRecipeFixture.Now,
        TestedByMembershipId = Guid.NewGuid(),
        Outcome = TestRunOutcome.SucceededWithIssues,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
        CreatedAt = SqlServerRecipeFixture.Now,
        UpdatedAt = SqlServerRecipeFixture.Now,
    };

    private static TestObservation NewObservation(Guid runId, int sortOrder, string text) => new()
    {
        Id = Guid.NewGuid(),
        RecipeTestRunId = runId,
        Kind = TestObservationKind.Texture,
        Text = text,
        SortOrder = sortOrder,
    };

    private static TestIssue NewIssue(RecipeTestRun run, Guid? observationId) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = run.RecipeId,
        RecipeTestRunId = run.Id,
        Severity = TestIssueSeverity.Major,
        Title = "Crumb too dense",
        TestObservationId = observationId,
        SortOrder = 0,
    };

    /// <param name="tagId">
    /// A tag of the scope's own workspace. Tags are workspace-owned, so seeding workspace B's recipe with
    /// workspace A's tag is a foreign key the database refuses — which is the isolation working, not a
    /// fixture to route around.
    /// </param>
    private static async Task<SeededTestTarget> SeedRecipeAsync(
        AsyncServiceScope scope,
        string title,
        RecipeStatus status = RecipeStatus.Draft,
        Guid? tagId = null)
    {
        var db = SqlServerRecipeFixture.Db(scope);

        // From the resolved context rather than a parameter: the scope already knows whose workspace it
        // is, and RecipeAssetLink's composite foreign key refuses the other one's asset.
        var workspaceId = scope.ServiceProvider.GetRequiredService<IWorkspaceContext>().WorkspaceId;
        var recipe = SqlServerRecipeFixture.NewRecipe(
            title,
            tagId ?? SqlServerRecipeFixture.TagIdA,
            SqlServerRecipeFixture.MediaAssetIdFor(workspaceId));
        recipe.Status = status;

        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = RecipePolicy.FirstVersionNumber,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = SqlServerRecipeFixture.Now,
            SnapshotSchemaVersion = 1,
        };

        db.Recipes.Add(recipe);
        db.RecipeVersions.Add(version);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();

        return new SeededTestTarget(recipe.Id, version.Id);
    }

    private sealed record SeededTestTarget(Guid RecipeId, Guid VersionId);
}
