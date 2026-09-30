using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Workspace A and Workspace B, each holding a test run of the same shape, proving that no part of the
/// test-run aggregate crosses between them — the two-workspace coverage tenancy.md requires of every
/// workspace-scoped feature, at the layer that exists today.
/// </summary>
public sealed class RecipeTestRunIsolationTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Every_entity_in_the_aggregate_is_scoped_to_its_own_workspace()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's dense crumb");
        await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's dense crumb");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        // Each set is asserted separately rather than through the root, because the global query filter is
        // applied per entity type: a child that stopped implementing IWorkspaceOwned would still be reachable
        // through an Include and would only show up in an assertion that queries its own set directly.
        Assert.Single(await db.RecipeTestRuns.ToListAsync(token));
        Assert.Single(await db.TestObservations.ToListAsync(token));
        Assert.Single(await db.TestAttachmentLinks.ToListAsync(token));
        Assert.Single(await db.TestIssueResolutions.ToListAsync(token));

        var issue = Assert.Single(await db.TestIssues.ToListAsync(token));
        Assert.Equal("A's dense crumb", issue.Title);
        Assert.Equal(RecipeAggregateFixture.WorkspaceA, issue.WorkspaceId);
    }

    [Fact]
    public async Task Another_workspaces_test_run_is_invisible_rather_than_forbidden()
    {
        var foreign = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's dense crumb");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // Nothing distinguishes "does not exist" from "belongs to someone else", which is what lets the read
        // seam answer 404 to both without the caller learning that the test exists at all.
        Assert.Null(await db.RecipeTestRuns
            .SingleOrDefaultAsync(run => run.Id == foreign.RunId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_attachment_cannot_be_hung_off_another_workspaces_issue()
    {
        var foreign = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's dense crumb");
        var own = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's dense crumb");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // The interceptor stamps WorkspaceId = A, so the composite key looks for (A, B's issue) and finds
        // nothing. This is the whole reason WorkspaceId is part of the key rather than beside it: the
        // rejection comes from the database, not from remembering to check.
        db.TestAttachmentLinks.Add(new TestAttachmentLink
        {
            Id = Guid.NewGuid(),
            RecipeTestRunId = own.RunId,
            TestIssueId = foreign.IssueId,
            MediaAssetId = Guid.NewGuid(),
            SortOrder = 1,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_resolution_cannot_cite_another_workspaces_version_as_the_correction()
    {
        var foreign = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's dense crumb");
        var own = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's dense crumb");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // A second issue, because the first is already resolved and one resolution per issue is enforced.
        var issue = new TestIssue
        {
            Id = Guid.NewGuid(),
            RecipeId = own.RecipeId,
            RecipeTestRunId = own.RunId,
            Severity = TestIssueSeverity.Minor,
            Title = "Second look",
            SortOrder = 1,
        };
        db.TestIssues.Add(issue);

        db.TestIssueResolutions.Add(new TestIssueResolution
        {
            Id = Guid.NewGuid(),
            RecipeId = own.RecipeId,
            TestIssueId = issue.Id,
            Kind = TestIssueResolutionKind.Fixed,
            ResolvedByMembershipId = Guid.NewGuid(),
            ResolvedAt = RecipeAggregateFixture.Now,
            ResolutionRecipeVersionId = foreign.VersionId,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A recipe, a version, a test run against it, and one of every child — the same shape in whichever
    /// workspace is asked for, so isolation cannot pass by the two being distinguishable.
    /// </summary>
    private async Task<SeededRun> SeedAsync(Guid workspaceId, string issueTitle)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var recipe = RecipeAggregateFixture.NewRecipe("Cake");
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

        var run = new RecipeTestRun
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeVersionId = version.Id,
            TestedAt = RecipeAggregateFixture.Now,
            TestedByMembershipId = Guid.NewGuid(),
            Outcome = TestRunOutcome.SucceededWithIssues,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
            UpdatedAt = RecipeAggregateFixture.Now,
        };
        db.RecipeTestRuns.Add(run);

        var observation = new TestObservation
        {
            Id = Guid.NewGuid(),
            RecipeTestRunId = run.Id,
            Kind = TestObservationKind.Texture,
            Text = "The crumb was close.",
            SortOrder = 0,
        };
        db.TestObservations.Add(observation);

        var issue = new TestIssue
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeTestRunId = run.Id,
            Severity = TestIssueSeverity.Major,
            Title = issueTitle,
            TestObservationId = observation.Id,
            SortOrder = 0,
        };
        db.TestIssues.Add(issue);

        db.TestIssueResolutions.Add(new TestIssueResolution
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            TestIssueId = issue.Id,
            Kind = TestIssueResolutionKind.Fixed,
            Notes = "Raised the hydration.",
            ResolvedByMembershipId = Guid.NewGuid(),
            ResolvedAt = RecipeAggregateFixture.Now,
        });

        db.TestAttachmentLinks.Add(new TestAttachmentLink
        {
            Id = Guid.NewGuid(),
            RecipeTestRunId = run.Id,
            TestIssueId = issue.Id,
            MediaAssetId = Guid.NewGuid(),
            SortOrder = 0,
        });

        await db.SaveChangesAsync(token);

        return new SeededRun(recipe.Id, version.Id, run.Id, issue.Id);
    }

    private sealed record SeededRun(Guid RecipeId, Guid VersionId, Guid RunId, Guid IssueId);
}
