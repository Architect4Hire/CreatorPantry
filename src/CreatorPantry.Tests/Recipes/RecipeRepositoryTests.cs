using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The repository against a real SQL Server, with the schema built by the migrations rather than from the
/// model. Every test uses two workspaces.
/// </summary>
public sealed class RecipeRepositoryTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static IRecipeRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeRepository>();

    [Fact]
    public async Task A_saved_aggregate_comes_back_whole()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        var recipe = loaded!.Recipe;

        // Every collection, because a split query that assembled one of them wrongly would still return a
        // plausible-looking recipe.
        Assert.Equal(2, recipe.IngredientGroups.Count);
        Assert.All(recipe.IngredientGroups, group => Assert.Equal(3, group.Ingredients.Count));
        Assert.Equal(2, recipe.InstructionGroups.Count);
        Assert.All(recipe.InstructionGroups, group => Assert.Equal(2, group.Steps.Count));
        Assert.Equal(2, recipe.Equipment.Count);
        Assert.Equal(2, recipe.AssetLinks.Count);
        Assert.Single(recipe.Tags);
    }

    [Fact]
    public async Task Children_come_back_in_their_sort_order()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = (await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken))!.Recipe;

        // The fixture inserts every collection out of order, so insertion order and sort order disagree.
        // Without the ordered includes this passes by accident on some engines and fails on others.
        Assert.Equal([0, 1], recipe.IngredientGroups.Select(group => group.SortOrder));
        Assert.All(recipe.IngredientGroups, group => Assert.Equal([0, 1, 2], group.Ingredients.Select(line => line.SortOrder)));
        Assert.Equal([0, 1], recipe.InstructionGroups.Select(group => group.SortOrder));
        Assert.All(recipe.InstructionGroups, group => Assert.Equal([0, 1], group.Steps.Select(step => step.SortOrder)));
        Assert.Equal([0, 1], recipe.Equipment.Select(item => item.SortOrder));
        Assert.Equal([0, 1], recipe.AssetLinks.Select(link => link.SortOrder));
    }

    [Fact]
    public async Task Another_workspaces_recipe_is_not_found()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceB, SqlServerRecipeFixture.TagIdB);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        // Indistinguishable from a recipe that does not exist, which is what lets the read seam answer 404
        // to both without disclosing that this one is real.
        Assert.Null(await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        Assert.Null(await Repository(scope).GetCompleteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_workspace_reads_only_its_own_copy()
    {
        var inA = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA, "Shared title");
        var inB = await AddAsync(SqlServerRecipeFixture.WorkspaceB, SqlServerRecipeFixture.TagIdB, "Shared title");

        await using var scopeA = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        await using var scopeB = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);

        Assert.NotNull(await Repository(scopeA).GetCompleteAsync(inA, TestContext.Current.CancellationToken));
        Assert.Null(await Repository(scopeA).GetCompleteAsync(inB, TestContext.Current.CancellationToken));
        Assert.NotNull(await Repository(scopeB).GetCompleteAsync(inB, TestContext.Current.CancellationToken));
        Assert.Null(await Repository(scopeB).GetCompleteAsync(inA, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Adding_an_aggregate_stamps_the_workspace_on_every_child()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = (await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken))!.Recipe;

        // Never assigned by the repository or the caller — the ownership interceptor sets it. If a child ever
        // arrived with Guid.Empty, the composite foreign key would have refused the insert, so this is really
        // asserting that stamping happens before the key is checked.
        Assert.Equal(SqlServerRecipeFixture.WorkspaceA, recipe.WorkspaceId);
        Assert.All(recipe.IngredientGroups, group => Assert.Equal(SqlServerRecipeFixture.WorkspaceA, group.WorkspaceId));
        Assert.All(
            recipe.IngredientGroups.SelectMany(group => group.Ingredients),
            line => Assert.Equal(SqlServerRecipeFixture.WorkspaceA, line.WorkspaceId));
        Assert.All(recipe.Tags, tag => Assert.Equal(SqlServerRecipeFixture.WorkspaceA, tag.WorkspaceId));
    }

    [Fact]
    public async Task A_recipe_with_no_history_reports_no_current_version()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken);

        Assert.Null(loaded!.CurrentVersion);
    }

    [Fact]
    public async Task The_current_version_is_the_highest_numbered_one()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA);

        await using (var writeScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(writeScope);

            // Written out of order on purpose: version 3 first, so "the most recent row" and "the highest
            // number" are different answers and the query has to pick the right one.
            foreach (var number in (int[])[3, 1, 2])
            {
                db.RecipeVersions.Add(NewVersion(recipeId, number));
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken);

        Assert.Equal(3, loaded!.CurrentVersion!.VersionNumber);
    }

    [Fact]
    public async Task Reading_a_recipe_does_not_read_its_archive()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA);

        await using (var writeScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(writeScope);
            var version = NewVersion(recipeId, 1);
            version.Snapshot = new RecipeVersionSnapshot
            {
                RecipeVersionId = version.Id,
                Document = """{"schemaVersion":1,"recipe":{"title":"archived"}}""",
            };
            db.RecipeVersions.Add(version);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken);

        // The whole reason the snapshot lives in its own table. If this ever comes back populated, someone
        // has added an Include and every recipe read now drags the full history with it.
        Assert.NotNull(loaded!.CurrentVersion);
        Assert.Null(loaded.CurrentVersion!.Snapshot);
    }

    [Fact]
    public async Task A_loaded_aggregate_captures_to_a_complete_snapshot()
    {
        var recipeId = await AddAsync(SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.TagIdA);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await Repository(scope).GetCompleteAsync(recipeId, TestContext.Current.CancellationToken);

        // The pairing CompleteRecipe exists for: what the repository returns is exactly what Capture accepts,
        // and the document that comes out is not the empty one a partially-loaded aggregate would produce.
        var document = RecipeSnapshotMapper.Capture(loaded!);

        Assert.Equal(2, document.IngredientGroups.Count);
        Assert.Equal(6, document.IngredientGroups.Sum(group => group.Ingredients.Count));
        Assert.Equal(4, document.InstructionGroups.Sum(group => group.Steps.Count));
        Assert.Single(document.Tags);
    }

    // ---- The tag vocabulary a detail read names its tags from ----

    [Fact]
    public async Task Tags_are_found_by_id_with_the_name_the_workspace_gave_them()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var found = await Tags(scope).FindByIdsAsync(
            [SqlServerRecipeFixture.TagIdA], TestContext.Current.CancellationToken);

        Assert.Equal("Weeknight", Assert.Single(found).Name);
    }

    [Fact]
    public async Task Another_workspaces_identically_named_tag_is_not_found_by_its_id()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        // Both workspaces have a tag named "Weeknight". Asking for B's id from A's scope must return nothing —
        // matching on the name would have hidden this, which is why the two rows deliberately share one.
        var found = await Tags(scope).FindByIdsAsync(
            [SqlServerRecipeFixture.TagIdB], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public async Task An_unknown_id_is_simply_absent_rather_than_an_error()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var found = await Tags(scope).FindByIdsAsync(
            [SqlServerRecipeFixture.TagIdA, Guid.NewGuid()], TestContext.Current.CancellationToken);

        // Fewer rows than ids asked for is a normal answer: the caller knows which links it holds.
        Assert.Equal(SqlServerRecipeFixture.TagIdA, Assert.Single(found).Id);
    }

    [Fact]
    public async Task No_ids_returns_nothing()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        Assert.Empty(await Tags(scope).FindByIdsAsync([], TestContext.Current.CancellationToken));
    }

    private static IWorkspaceTagRepository Tags(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceTagRepository>();

    private async Task<Guid> AddAsync(Guid workspaceId, Guid tagId, string title = "Olive oil cake")
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var recipe = SqlServerRecipeFixture.NewRecipe(title, tagId);

        Repository(scope).Add(recipe);

        // SaveChanges is the DataLayer's job in production; these tests stand in for it, because the
        // repository deliberately does not own the transaction.
        await SqlServerRecipeFixture.Db(scope).SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe.Id;
    }

    private static RecipeVersion NewVersion(Guid recipeId, int number) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipeId,
        VersionNumber = number,
        Source = RecipeVersionSource.CreatorEdit,
        Readiness = RecipeVersionReadiness.Draft,
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = SqlServerRecipeFixture.Now,
        SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
    };
}
