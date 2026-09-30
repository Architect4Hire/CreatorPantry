using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The invariants the test-run aggregate's EF configuration puts in the database rather than in whichever
/// layer happens to write next. Each one is exercised by trying to break it.
/// </summary>
public sealed class RecipeTestRunConstraintTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_test_run_must_name_a_version_of_the_recipe_it_claims_to_test()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var cake = await SeedRecipeAsync(db, "Cake");
        var bread = await SeedRecipeAsync(db, "Bread");

        // The recipe of one and the version of the other. Both rows exist and both belong to workspace A, so
        // nothing but the composite key can catch this — a foreign key on RecipeVersionId alone would accept
        // it and the test would be filed against a recipe nobody cooked.
        db.RecipeTestRuns.Add(NewRun(cake.RecipeId, bread.VersionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_test_run_cannot_name_another_workspaces_version()
    {
        Guid foreignVersionId;
        Guid foreignRecipeId;

        await using (var other = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            (foreignRecipeId, foreignVersionId) = await SeedRecipeAsync(RecipeAggregateFixture.Db(other), "B's cake");
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // The interceptor stamps WorkspaceId = A, so the key looks for (A, B's recipe, B's version) and finds
        // nothing. The rejection comes from the database rather than from remembering to check.
        db.RecipeTestRuns.Add(NewRun(foreignRecipeId, foreignVersionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task A_rating_outside_the_scale_is_refused(int rating)
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var seeded = await SeedRecipeAsync(db, "Cake");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.Rating = rating;
        db.RecipeTestRuns.Add(run);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_actual_yield_may_not_be_measured_in_degrees()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var seeded = await SeedRecipeAsync(db, "Cake");

        // "Made 180 °C of cake." The composite foreign key would accept Celsius — it is a real unit — so the
        // check constraint is what refuses it, exactly as CK_Recipes_YieldUnit_Dimension does for the recipe.
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.ActualYieldQuantity = 180m;
        run.ActualYieldUnitId = RecipeAggregateFixture.CelsiusId;
        run.ActualYieldUnitDimension = MeasurementDimension.Temperature;
        db.RecipeTestRuns.Add(run);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_actual_yield_unit_with_nothing_to_measure_is_refused()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var seeded = await SeedRecipeAsync(db, "Cake");

        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.ActualYieldUnitId = RecipeAggregateFixture.EachId;
        run.ActualYieldUnitDimension = MeasurementDimension.Count;
        db.RecipeTestRuns.Add(run);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_yield_with_no_unit_at_all_is_accepted()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var seeded = await SeedRecipeAsync(db, "Cake");

        // The reverse of the rule above, and a real thing testers write: "got 10, not 12" is a complete
        // result with no unit in it.
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.ActualYieldText = "got 10, not 12";
        run.ActualYieldQuantity = 10m;
        db.RecipeTestRuns.Add(run);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await db.RecipeTestRuns.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_actual_time_beyond_a_year_is_refused()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var seeded = await SeedRecipeAsync(db, "Cake");

        // The common paste error: a millisecond value into a field that means minutes.
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.ActualCookTimeMinutes = RecipePolicy.MaxTimeMinutes + 1;
        db.RecipeTestRuns.Add(run);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_total_time_smaller_than_the_sum_of_its_parts_is_accepted()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var seeded = await SeedRecipeAsync(db, "Cake");

        // Nothing sums the parts here, deliberately: a real kitchen overlaps prep with cooking, so an elapsed
        // total below the sum is the correct measurement rather than a contradiction.
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.ActualPrepTimeMinutes = 30;
        run.ActualCookTimeMinutes = 40;
        run.ActualTotalTimeMinutes = 50;
        db.RecipeTestRuns.Add(run);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(50, (await db.RecipeTestRuns.SingleAsync(TestContext.Current.CancellationToken)).ActualTotalTimeMinutes);
    }

    [Fact]
    public async Task An_issue_must_state_its_severity()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        // Zero is not a severity. Left to default it would report a blocking problem as a cosmetic one, and
        // every reader after that point would believe it.
        db.TestIssues.Add(new TestIssue
        {
            Id = Guid.NewGuid(),
            RecipeId = run.RecipeId,
            RecipeTestRunId = run.RunId,
            Severity = default,
            Title = "Crumb too dense",
            SortOrder = 0,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Two_observations_in_one_run_cannot_share_a_position()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        db.TestObservations.AddRange(
            NewObservation(run.RunId, sortOrder: 0),
            NewObservation(run.RunId, sortOrder: 0));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_observation_can_be_corrected_by_whoever_wrote_it()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        var observation = NewObservation(run.RunId, sortOrder: 0);
        db.TestObservations.Add(observation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The deliberate asymmetry in this aggregate. These are the tester's own words about their own cook,
        // and recipes.md protects creator-entered text from being rewritten by the system, not from being
        // corrected by the person who wrote it. Only TestIssueResolution below is frozen.
        observation.Text = "The crumb was dense, not merely close.";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "The crumb was dense, not merely close.",
            (await db.TestObservations.SingleAsync(TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task An_observation_an_issue_cites_cannot_be_removed_out_from_under_it()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        var observation = NewObservation(run.RunId, sortOrder: 0);
        db.TestObservations.Add(observation);
        db.TestIssues.Add(NewIssue(run, observationId: observation.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Cleared first, so the database is what answers. With the citing issue tracked, EF resolves the
        // delete on the client instead — see A_tracked_issue_loses_its_citation_before_the_database_sees_it,
        // which records what it does there and why the seam cannot rely on this constraint alone.
        db.ChangeTracker.Clear();

        // Restrict rather than Cascade: removing the note would otherwise silently delete the issue raised
        // from it. SetNull is not available while WorkspaceId is part of the key, so the seam has to say what
        // becomes of the issue first.
        db.TestObservations.Remove(
            await db.TestObservations.SingleAsync(TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_tracked_issue_loses_its_citation_before_the_database_sees_it()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;
        var run = await SeedRunAsync(db);

        var observation = NewObservation(run.RunId, sortOrder: 0);
        db.TestObservations.Add(observation);
        var issue = NewIssue(run, observationId: observation.Id);
        db.TestIssues.Add(issue);
        await db.SaveChangesAsync(token);

        // The same delete as above, with the issue still tracked. EF resolves the optional reference on the
        // client — it nulls TestObservationId and the save succeeds — so the database never gets the chance
        // to refuse it. Recorded rather than asserted away, because it is a real limit on what the Restrict
        // above guarantees: it protects the stored data, not the in-memory graph an edit seam is holding.
        // The seam that removes observations therefore has to decide what becomes of a citing issue itself.
        db.TestObservations.Remove(observation);
        await db.SaveChangesAsync(token);

        Assert.Null((await db.TestIssues.SingleAsync(token)).TestObservationId);
        Assert.Empty(await db.TestObservations.ToListAsync(token));
    }

    [Fact]
    public async Task An_issue_may_be_resolved_only_once()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);
        db.TestIssueResolutions.Add(NewResolution(issue));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A replayed or duplicated resolution command is refused by the unique index rather than by a race
        // the seam has to win.
        db.TestIssueResolutions.Add(NewResolution(issue));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_resolution_cannot_cite_another_recipes_version_as_the_correction()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var run = await SeedRunAsync(db);
        var otherRecipe = await SeedRecipeAsync(db, "Bread");

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);

        // TESTRUN-002's "the resolution version must belong to the same recipe", as a key rather than as a
        // validator's promise. RecipeId reached this row through the issue's own foreign key, so it is the
        // run's recipe by construction and the bread version is unreferenceable from here.
        var resolution = NewResolution(issue);
        resolution.ResolutionRecipeVersionId = otherRecipe.VersionId;
        db.TestIssueResolutions.Add(resolution);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_resolution_that_fixed_nothing_cannot_name_a_correction_version()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);

        // Otherwise a history can report an issue as declined while pointing at the version that fixed it.
        var resolution = NewResolution(issue);
        resolution.Kind = TestIssueResolutionKind.WontFix;
        resolution.ResolutionRecipeVersionId = run.VersionId;
        db.TestIssueResolutions.Add(resolution);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_override_reason_needs_a_version_to_override()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);

        var resolution = NewResolution(issue);
        resolution.PredatingVersionOverrideReason = "The older version had it right all along.";
        db.TestIssueResolutions.Add(resolution);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_resolution_cannot_be_edited_or_withdrawn()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);
        var resolution = NewResolution(issue);
        db.TestIssueResolutions.Add(resolution);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The one frozen row in an otherwise editable aggregate: a resolution records a decision taken at a
        // moment, and softening it afterwards would make the record of that decision untrue.
        resolution.Notes = "Actually we left it alone.";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        db.ChangeTracker.Clear();

        var stored = await db.TestIssueResolutions.SingleAsync(TestContext.Current.CancellationToken);
        db.TestIssueResolutions.Remove(stored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_issue_itself_stays_editable_when_its_resolution_does_not()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);
        db.TestIssueResolutions.Add(NewResolution(issue));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Resolving an issue does not write to the issue, and does not freeze it either. Severity is the
        // tester's judgement and stays theirs to revise.
        issue.Severity = TestIssueSeverity.Blocking;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            TestIssueSeverity.Blocking,
            (await db.TestIssues.SingleAsync(TestContext.Current.CancellationToken)).Severity);
    }

    [Fact]
    public async Task Deleting_a_run_takes_every_child_with_it()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var run = await SeedRunAsync(db);
        var token = TestContext.Current.CancellationToken;

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);
        db.TestObservations.Add(NewObservation(run.RunId, sortOrder: 0));
        db.TestIssueResolutions.Add(NewResolution(issue));
        db.TestAttachmentLinks.Add(new TestAttachmentLink
        {
            Id = Guid.NewGuid(),
            RecipeTestRunId = run.RunId,
            MediaAssetId = Guid.NewGuid(),
            SortOrder = 0,
        });
        await db.SaveChangesAsync(token);

        // Cleared so the cascade happens in the database rather than in the change tracker. That is not an
        // incidental detail of this test — see A_run_delete_is_refused_while_its_resolutions_are_tracked.
        db.ChangeTracker.Clear();

        db.RecipeTestRuns.Remove(await db.RecipeTestRuns.SingleAsync(token));
        await db.SaveChangesAsync(token);

        // The resolution goes too, two levels down, and its immutability does not save it: IImmutableRecord
        // refuses deletes EF is tracking, and this one happens in the database by cascade. That is the correct
        // outcome — the decision it records is about an issue that no longer exists.
        Assert.Empty(await db.TestObservations.ToListAsync(token));
        Assert.Empty(await db.TestIssues.ToListAsync(token));
        Assert.Empty(await db.TestIssueResolutions.ToListAsync(token));
        Assert.Empty(await db.TestAttachmentLinks.ToListAsync(token));

        // And the version it was recorded against is untouched. A test is evidence about a recipe; deleting
        // the evidence never reaches back into the canonical history.
        Assert.Equal(1, await db.RecipeVersions.CountAsync(token));
    }

    [Fact]
    public async Task A_run_delete_is_refused_while_its_resolutions_are_tracked()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;
        var run = await SeedRunAsync(db);

        var issue = NewIssue(run);
        db.TestIssues.Add(issue);
        db.TestIssueResolutions.Add(NewResolution(issue));
        await db.SaveChangesAsync(token);

        // The same delete the test above performs, with the resolution still in the change tracker. EF
        // cascades to tracked children on the client, which marks the resolution Deleted, which
        // ImmutableRecordInterceptor refuses — so whether deleting a run succeeds depends on what happens to
        // be loaded.
        //
        // Asserted rather than avoided, because it is the one thing the delete seam will have to know:
        // removing a test run must not have its resolutions loaded. Nothing here can be fixed locally — the
        // interceptor cannot tell a cascade from a deliberate delete, and teaching it to would weaken the
        // guarantee for RecipeVersion and AiProposal as well.
        db.RecipeTestRuns.Remove(await db.RecipeTestRuns.SingleAsync(token));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(token));
        Assert.Contains(nameof(TestIssueResolution), refusal.Message);
    }

    private static RecipeTestRun NewRun(Guid recipeId, Guid versionId) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipeId,
        RecipeVersionId = versionId,
        TestedAt = RecipeAggregateFixture.Now,
        TestedByMembershipId = Guid.NewGuid(),
        Outcome = TestRunOutcome.Succeeded,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
        CreatedAt = RecipeAggregateFixture.Now,
        UpdatedAt = RecipeAggregateFixture.Now,
    };

    private static TestObservation NewObservation(Guid runId, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        RecipeTestRunId = runId,
        Kind = TestObservationKind.Texture,
        Text = "The crumb was close.",
        SortOrder = sortOrder,
    };

    private static TestIssue NewIssue(SeededRun run, Guid? observationId = null) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = run.RecipeId,
        RecipeTestRunId = run.RunId,
        Severity = TestIssueSeverity.Major,
        Title = "Crumb too dense",
        TestObservationId = observationId,
        SortOrder = 0,
    };

    private static TestIssueResolution NewResolution(TestIssue issue) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = issue.RecipeId,
        TestIssueId = issue.Id,
        Kind = TestIssueResolutionKind.Fixed,
        Notes = "Raised the hydration.",
        ResolvedByMembershipId = Guid.NewGuid(),
        ResolvedAt = RecipeAggregateFixture.Now,
    };

    /// <summary>A recipe with one captured version, in whichever workspace the scope resolved to.</summary>
    private static async Task<(Guid RecipeId, Guid VersionId)> SeedRecipeAsync(CreatorPantryDbContext db, string title)
    {
        var recipe = RecipeAggregateFixture.NewRecipe(title);
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = RecipePolicy.FirstVersionNumber,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
            SnapshotSchemaVersion = 1,
        };

        db.Recipes.Add(recipe);
        db.RecipeVersions.Add(version);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (recipe.Id, version.Id);
    }

    /// <summary>A recipe, a version, and a saved test run against it.</summary>
    private static async Task<SeededRun> SeedRunAsync(CreatorPantryDbContext db)
    {
        var seeded = await SeedRecipeAsync(db, "Cake");
        var run = NewRun(seeded.RecipeId, seeded.VersionId);

        db.RecipeTestRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new SeededRun(seeded.RecipeId, seeded.VersionId, run.Id);
    }

    private sealed record SeededRun(Guid RecipeId, Guid VersionId, Guid RunId);
}
