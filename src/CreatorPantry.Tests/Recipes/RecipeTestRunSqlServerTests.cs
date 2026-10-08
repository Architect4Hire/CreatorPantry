using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The test-run tables against a real SQL Server, with the schema built by the migrations rather than from
/// the model.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This class existing is most of its value.</strong> The fixture runs <c>Database.Migrate()</c>, so
/// if SQL Server refuses any of the DDL this migration emits, every test here fails at initialisation. That
/// matters more for this change than for most: five tables whose foreign keys form a tree through
/// <c>Workspaces</c> is exactly the shape that trips "may cause cycles or multiple cascade paths", and SQLite
/// — where <see cref="RecipeTestRunConstraintTests"/> runs — does not enforce that rule at all. A green
/// SQLite suite is no evidence about it.
/// </para>
/// <para>
/// The assertions below are the ones whose behaviour could differ between the two engines: composite foreign
/// keys spanning three columns, and a cascade that has to travel two levels down through the database rather
/// than through EF's change tracker.
/// </para>
/// </remarks>
public sealed class RecipeTestRunSqlServerTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    [Fact]
    public async Task A_test_run_round_trips_against_its_version()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var seeded = await SeedRecipeAsync(db, "Round trip");
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        run.ActualYieldText = "got 10, not 12";
        run.ActualYieldQuantity = 10m;
        run.ActualYieldUnitId = SqlServerRecipeFixture.GramId;
        run.ActualYieldUnitDimension = Domain.Managers.Reference.MeasurementDimension.Mass;
        run.Rating = 4;

        db.RecipeTestRuns.Add(run);
        await db.SaveChangesAsync(token);

        db.ChangeTracker.Clear();

        var stored = await db.RecipeTestRuns.SingleAsync(candidate => candidate.Id == run.Id, token);

        Assert.Equal(seeded.VersionId, stored.RecipeVersionId);
        Assert.Equal(10m, stored.ActualYieldQuantity);
        Assert.Equal(4, stored.Rating);

        // Server-generated, and the reason an update from a stale copy is a recoverable conflict rather than
        // a lost one.
        Assert.NotEmpty(stored.RowVersion);
    }

    [Fact]
    public async Task A_test_run_must_name_a_version_of_the_recipe_it_claims_to_test()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var cake = await SeedRecipeAsync(db, "Cake");
        var bread = await SeedRecipeAsync(db, "Bread");

        // The three-column foreign key, on the engine that has to enforce it.
        db.RecipeTestRuns.Add(NewRun(cake.RecipeId, bread.VersionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_a_run_cascades_two_levels_down()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var seeded = await SeedRecipeAsync(db, "Cascade");
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        db.RecipeTestRuns.Add(run);

        var issue = new TestIssue
        {
            Id = Guid.NewGuid(),
            RecipeId = seeded.RecipeId,
            RecipeTestRunId = run.Id,
            Severity = TestIssueSeverity.Blocking,
            Title = "Collapsed in the tin",
            SortOrder = 0,
        };
        db.TestIssues.Add(issue);

        var resolutionId = Guid.NewGuid();
        db.TestIssueResolutions.Add(new TestIssueResolution
        {
            Id = resolutionId,
            RecipeId = seeded.RecipeId,
            TestIssueId = issue.Id,
            Kind = TestIssueResolutionKind.Fixed,
            ResolvedByMembershipId = Guid.NewGuid(),
            ResolvedAt = SqlServerRecipeFixture.Now,
            ResolutionRecipeVersionId = seeded.VersionId,
        });

        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        db.RecipeTestRuns.Remove(await db.RecipeTestRuns.SingleAsync(candidate => candidate.Id == run.Id, token));
        await db.SaveChangesAsync(token);

        // Run -> issue -> resolution, entirely inside the database: neither child was tracked, so nothing but
        // the cascade could have removed them. The immutability of the resolution does not block this, and
        // should not — the decision it records is about an issue that no longer exists.
        Assert.False(await db.TestIssues.AnyAsync(candidate => candidate.Id == issue.Id, token));
        Assert.False(await db.TestIssueResolutions.AnyAsync(candidate => candidate.Id == resolutionId, token));

        // The version it was recorded against is untouched.
        Assert.True(await db.RecipeVersions.AnyAsync(candidate => candidate.Id == seeded.VersionId, token));
    }

    [Fact]
    public async Task An_issue_may_be_resolved_only_once()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var seeded = await SeedRecipeAsync(db, "Resolve once");
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        db.RecipeTestRuns.Add(run);

        var issue = new TestIssue
        {
            Id = Guid.NewGuid(),
            RecipeId = seeded.RecipeId,
            RecipeTestRunId = run.Id,
            Severity = TestIssueSeverity.Minor,
            Title = "Slightly pale",
            SortOrder = 0,
        };
        db.TestIssues.Add(issue);
        db.TestIssueResolutions.Add(NewResolution(issue));
        await db.SaveChangesAsync(token);

        db.ChangeTracker.Clear();
        db.TestIssueResolutions.Add(NewResolution(issue));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
    }

    /// <summary>
    /// The composite key on a test image's asset (12.10k), on the engine that enforces it: an attachment in
    /// workspace A cannot name workspace B's asset, whatever the application's own check was told.
    /// </summary>
    [Fact]
    public async Task An_attachment_cannot_name_another_workspaces_asset()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var seeded = await SeedRecipeAsync(db, "Attachment keys");
        var run = NewRun(seeded.RecipeId, seeded.VersionId);
        db.RecipeTestRuns.Add(run);
        await db.SaveChangesAsync(token);

        TestAttachmentLink Attachment(Guid assetId, int order) => new()
        {
            Id = Guid.NewGuid(), RecipeTestRunId = run.Id, MediaAssetId = assetId, SortOrder = order,
        };

        db.TestAttachmentLinks.Add(Attachment(SqlServerRecipeFixture.MediaAssetIdB, 0));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));

        db.ChangeTracker.Clear();

        db.TestAttachmentLinks.Add(Attachment(SqlServerRecipeFixture.MediaAssetIdA, 0));
        await db.SaveChangesAsync(token);
    }

    private static RecipeTestRun NewRun(Guid recipeId, Guid versionId) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipeId,
        RecipeVersionId = versionId,
        TestedAt = SqlServerRecipeFixture.Now,
        TestedByMembershipId = Guid.NewGuid(),
        Outcome = TestRunOutcome.Succeeded,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
        CreatedAt = SqlServerRecipeFixture.Now,
        UpdatedAt = SqlServerRecipeFixture.Now,
    };

    private static TestIssueResolution NewResolution(TestIssue issue) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = issue.RecipeId,
        TestIssueId = issue.Id,
        Kind = TestIssueResolutionKind.WontFix,
        ResolvedByMembershipId = Guid.NewGuid(),
        ResolvedAt = SqlServerRecipeFixture.Now,
    };

    private static async Task<(Guid RecipeId, Guid VersionId)> SeedRecipeAsync(CreatorPantryDbContext db, string title)
    {
        var recipe = SqlServerRecipeFixture.NewRecipe(title, SqlServerRecipeFixture.TagIdA);
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

        return (recipe.Id, version.Id);
    }
}
