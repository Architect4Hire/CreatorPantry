using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The snapshot-pair query against a real SQL Server: that it translates at all, that a version number is
/// scoped to its recipe, that a version with no archived document is absent rather than half-present, and that
/// the workspace filter holds across the join into the archive.
/// </summary>
/// <remarks>
/// Each test starts from an empty recipe table. Deleting is done in SQL because <c>RecipeVersion</c> is an
/// immutable record and <c>ImmutableRecordInterceptor</c> refuses to delete one through the change tracker.
/// </remarks>
public sealed class RecipeVersionSnapshotRepositoryTests(SqlServerRecipeFixture fixture)
    : IClassFixture<SqlServerRecipeFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        await SqlServerRecipeFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM RecipeVersionSnapshots;
            DELETE FROM RecipeVersions;
            DELETE FROM Recipes;
            """,
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Two_numbers_come_back_with_their_documents()
    {
        var recipeId = await AddAsync(versions: 4);

        var rows = await FindAsync(recipeId, 2, 4);

        Assert.Equal([2, 4], rows.Select(row => row.VersionNumber).Order());
        Assert.All(rows, row => Assert.Contains("Olive oil cake", row.Document, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same number twice returns one row. Business reads it as both sides; a repository that padded the
    /// result to two would be inventing a version.
    /// </summary>
    [Fact]
    public async Task The_same_number_twice_returns_one_row()
    {
        var recipeId = await AddAsync(versions: 3);

        Assert.Equal(2, Assert.Single(await FindAsync(recipeId, 2, 2)).VersionNumber);
    }

    [Fact]
    public async Task A_number_the_recipe_does_not_have_is_simply_absent()
    {
        var recipeId = await AddAsync(versions: 2);

        var rows = await FindAsync(recipeId, 1, 9);

        Assert.Equal(1, Assert.Single(rows).VersionNumber);
    }

    /// <summary>
    /// The case a version number can make worse than an id would: numbers collide across recipes by design,
    /// so without the recipe predicate this query would return every recipe's version 1 in the workspace.
    /// </summary>
    [Fact]
    public async Task A_number_belonging_to_another_recipe_matches_nothing()
    {
        var mine = await AddAsync(versions: 1, title: "Mine");
        await AddAsync(versions: 4, title: "Someone else's");

        var rows = await FindAsync(mine, 1, 4);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.VersionNumber);
        Assert.Contains("Mine", row.Document, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version with no stored document is not returned at all. No write path produces one, so this proves
    /// the join is inner rather than describing a state the API can reach — and an inner join is what keeps
    /// "there is nothing here to compare" from arriving one layer up as a null document.
    /// </summary>
    [Fact]
    public async Task A_version_with_no_archived_document_is_not_returned()
    {
        var recipeId = await AddAsync(versions: 2, snapshotOnVersions: [1]);

        Assert.Equal(1, Assert.Single(await FindAsync(recipeId, 1, 2)).VersionNumber);
    }

    /// <summary>
    /// Read from the other workspace's scope, the same recipe id and the same version numbers find nothing.
    /// The query names no <c>WorkspaceId</c> — the global filter is what makes the recipe predicate safe on
    /// its own, and it has to hold on both sides of the join into <c>RecipeVersionSnapshots</c>.
    /// </summary>
    [Fact]
    public async Task Another_workspace_cannot_read_the_archive_by_naming_the_recipe()
    {
        var recipeId = await AddAsync(versions: 3);

        Assert.Empty(await FindAsync(recipeId, 1, 3, SqlServerRecipeFixture.WorkspaceB));
    }

    [Fact]
    public async Task The_projection_carries_the_versions_own_account_of_itself()
    {
        var recipeId = await AddAsync(versions: 1);

        var row = Assert.Single(await FindAsync(recipeId, 1, 1));

        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(RecipeVersionSource.CreatorEdit, row.Source);
        Assert.Equal(RecipeVersionReadiness.Draft, row.Readiness);
        Assert.Equal(Now.AddMinutes(1), row.CreatedAt);
    }

    // --- Helpers -----------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<RecipeVersionSnapshotRecord>> FindAsync(
        Guid recipeId, int first, int second, Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<IRecipeVersionRepository>()
            .FindSnapshotsAsync(recipeId, first, second, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds one recipe and a chain of versions beneath it. <c>WorkspaceId</c> is never set on anything — the
    /// ownership interceptor stamps it from the resolved context, and feature code assigning it is a defect.
    /// </summary>
    /// <param name="snapshotOnVersions">
    /// Which version numbers get a stored document. Every one of them by default; naming a subset is how the
    /// unreachable "version with no archive" state is set up.
    /// </param>
    private async Task<Guid> AddAsync(
        int versions,
        string title = "Olive oil cake",
        Guid? workspaceId = null,
        IReadOnlyCollection<int>? snapshotOnVersions = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            UpdatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        db.Recipes.Add(recipe);

        Guid? parentId = null;
        for (var number = 1; number <= versions; number++)
        {
            var version = new RecipeVersion
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                VersionNumber = number,
                Source = RecipeVersionSource.CreatorEdit,
                Readiness = RecipeVersionReadiness.Draft,
                Reason = number == 1 ? null : $"Edit {number}",
                CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                CreatedAt = Now.AddMinutes(number),
                ParentVersionId = parentId,
                SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            };

            if (snapshotOnVersions is null || snapshotOnVersions.Contains(number))
            {
                version.Snapshot = new RecipeVersionSnapshot
                {
                    RecipeVersionId = version.Id,
                    Document = RecipeSnapshotSerializer.Serialize(
                        RecipeSnapshotMapper.Capture(new CompleteRecipe(recipe, null))),
                };
            }

            parentId = version.Id;
            db.RecipeVersions.Add(version);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe.Id;
    }
}
