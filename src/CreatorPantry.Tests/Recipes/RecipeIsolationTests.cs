using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Workspace A and Workspace B, each holding a complete recipe with the same shape, proving that no part of
/// the aggregate crosses between them — the two-workspace coverage tenancy.md requires of every
/// workspace-scoped feature, at the layer that exists today.
/// </summary>
public sealed class RecipeIsolationTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Every_entity_in_the_aggregate_is_scoped_to_its_own_workspace()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's cake");
        await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's cake");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        // Each set is asserted separately rather than through the root, because the global query filter is
        // applied per entity type: a child that stopped implementing IWorkspaceOwned would still be reachable
        // through an Include and would only show up in an assertion that queries its own set directly.
        Assert.Equal("A's cake", (await db.Recipes.SingleAsync(token)).Title);
        Assert.Single(await db.RecipeIngredientGroups.ToListAsync(token));
        Assert.Single(await db.RecipeIngredients.ToListAsync(token));
        Assert.Single(await db.RecipeInstructionGroups.ToListAsync(token));
        Assert.Single(await db.RecipeInstructionSteps.ToListAsync(token));
        Assert.Single(await db.RecipeEquipment.ToListAsync(token));
        Assert.Single(await db.RecipeAssetLinks.ToListAsync(token));

        Assert.All(
            await db.RecipeIngredients.ToListAsync(token),
            ingredient => Assert.Equal(RecipeAggregateFixture.WorkspaceA, ingredient.WorkspaceId));
    }

    [Fact]
    public async Task Another_workspaces_recipe_is_invisible_rather_than_forbidden()
    {
        var foreignRecipeId = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's cake");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // Nothing distinguishes "does not exist" from "belongs to someone else", which is what lets the read
        // seam answer 404 to both without the caller learning that the recipe exists at all.
        Assert.Null(await db.Recipes
            .SingleOrDefaultAsync(recipe => recipe.Id == foreignRecipeId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_child_cannot_be_attached_to_another_workspaces_recipe()
    {
        var foreignRecipeId = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's cake");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // The interceptor stamps WorkspaceId = A, so the composite foreign key looks for (A, B's recipe) and
        // finds nothing. This is the whole reason WorkspaceId is part of the key rather than beside it: the
        // rejection comes from the database, not from remembering to check.
        db.RecipeIngredientGroups.Add(new RecipeIngredientGroup
        {
            Id = Guid.NewGuid(),
            RecipeId = foreignRecipeId,
            Title = "Smuggled in",
            SortOrder = 5,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_recipe_cannot_be_written_into_another_workspace()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Planted in B");
        recipe.WorkspaceId = RecipeAggregateFixture.WorkspaceB;
        db.Recipes.Add(recipe);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("resolved workspace", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_recipe_removes_its_whole_aggregate_and_nothing_else()
    {
        var doomed = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's cake");
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's other cake");
        await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's cake");

        await using (var deleteScope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA))
        {
            var deleteDb = RecipeAggregateFixture.Db(deleteScope);
            var recipe = await deleteDb.Recipes.SingleAsync(r => r.Id == doomed, TestContext.Current.CancellationToken);
            deleteDb.Recipes.Remove(recipe);
            await deleteDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        // The surviving recipe in the same workspace keeps every child: the cascade follows the aggregate,
        // not the workspace.
        Assert.Equal("A's other cake", (await db.Recipes.SingleAsync(token)).Title);
        Assert.Single(await db.RecipeIngredients.ToListAsync(token));
        Assert.Single(await db.RecipeInstructionSteps.ToListAsync(token));
        Assert.Single(await db.RecipeEquipment.ToListAsync(token));
        Assert.Single(await db.RecipeAssetLinks.ToListAsync(token));

        await using var otherScope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB);
        Assert.Single(await RecipeAggregateFixture.Db(otherScope).RecipeIngredients.ToListAsync(token));
    }

    [Fact]
    public async Task A_line_cannot_be_put_in_another_workspaces_group()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceB, "B's cake");

        Guid foreignGroupId;
        Guid foreignRecipeId;
        await using (var otherScope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            var group = await RecipeAggregateFixture.Db(otherScope).RecipeIngredientGroups
                .SingleAsync(TestContext.Current.CancellationToken);
            foreignGroupId = group.Id;
            foreignRecipeId = group.RecipeId;
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // The grandchild hop: (WorkspaceId, RecipeId, GroupId) against the group's alternate key. Stamped
        // with A, so no row matches, whatever the recipe and group ids say.
        db.RecipeIngredients.Add(new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = foreignRecipeId,
            RecipeIngredientGroupId = foreignGroupId,
            SortOrder = 9,
            DisplayText = "smuggled salt",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_line_cannot_claim_a_different_recipe_than_its_group()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's cake");
        var otherRecipeId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's other cake");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var group = await db.RecipeIngredientGroups
            .FirstAsync(candidate => candidate.RecipeId != otherRecipeId, token);

        // Both rows are this workspace's, so no filter and no interceptor has anything to say. Carrying
        // RecipeId in the group's alternate key is the only thing that catches this, and it is the case the
        // composite key uniquely buys.
        db.RecipeIngredients.Add(new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = otherRecipeId,
            RecipeIngredientGroupId = group.Id,
            SortOrder = 9,
            DisplayText = "a line in two recipes at once",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
    }

    [Fact]
    public async Task A_recipe_cannot_be_moved_to_another_workspace_by_an_update()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's cake");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = await db.Recipes.SingleAsync(TestContext.Current.CancellationToken);
        recipe.WorkspaceId = RecipeAggregateFixture.WorkspaceB;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        // Refused by EF before WorkspaceOwnershipInterceptor is ever consulted: WorkspaceId is part of the
        // alternate key the children point at, and a key property cannot be modified on a tracked entity.
        // A stronger guarantee than the interceptor's, and a side effect of the composite-key design worth
        // recording — the interceptor remains the backstop for the children, which have no such key.
        Assert.Contains("part of a key", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_child_cannot_be_moved_to_another_workspace_by_an_update()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's cake");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var ingredient = await db.RecipeIngredients.SingleAsync(TestContext.Current.CancellationToken);
        ingredient.WorkspaceId = RecipeAggregateFixture.WorkspaceB;

        // A child's WorkspaceId is part of a foreign key, not a key of its own, so EF permits the change and
        // the interceptor is what refuses it.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ownership is immutable", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recipes_cannot_be_read_before_a_workspace_is_resolved()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, "A's cake");

        // Fail-closed, not fail-quiet: an unresolved scope throws rather than reporting an empty workspace,
        // which is the contract IWorkspaceContext itself has.
        await using var scope = _fixture.UnresolvedScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipeAggregateFixture.Db(scope).Recipes.ToListAsync(TestContext.Current.CancellationToken));
    }

    private async Task<Guid> SeedAsync(Guid workspaceId, string title)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe(title);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe.Id;
    }
}
