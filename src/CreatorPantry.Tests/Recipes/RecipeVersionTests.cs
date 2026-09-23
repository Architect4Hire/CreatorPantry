using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The guarantees a version history is worth nothing without: that history cannot be rewritten, that an
/// ordinary recipe delete cannot take it with it, and that versions are scoped to their workspace like
/// everything else.
/// </summary>
public sealed class RecipeVersionTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_version_cannot_be_edited_once_written()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        await SeedVersionAsync(db);

        var version = await db.RecipeVersions.SingleAsync(TestContext.Current.CancellationToken);
        version.Reason = "quietly rewriting the past";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("immutable once written", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_version_cannot_be_deleted()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        await SeedVersionAsync(db);

        db.RecipeVersions.Remove(await db.RecipeVersions.SingleAsync(TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("cannot be deleted", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stored_snapshot_cannot_be_edited()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        await SeedVersionAsync(db);

        // The content is where a rewrite would actually be worth attempting; protecting only the metadata
        // row would leave the archive editable.
        var snapshot = await db.RecipeVersionSnapshots.SingleAsync(TestContext.Current.CancellationToken);
        snapshot.Document = "{}";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_a_recipe_whose_versions_are_tracked_is_refused_by_ef()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var recipe = await SeedVersionAsync(db);

        // With the version tracked in this context, EF refuses at Remove() rather than at SaveChanges:
        // severing a required relationship it cannot cascade is rejected before any SQL is composed.
        var failure = Assert.Throws<InvalidOperationException>(() => db.Recipes.Remove(recipe));

        Assert.Contains("severed", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_recipe_whose_versions_are_not_loaded_is_refused_by_the_database()
    {
        Guid recipeId;
        await using (var seed = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA))
        {
            recipeId = (await SeedVersionAsync(RecipeAggregateFixture.Db(seed))).Id;
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // The case that actually matters: a repository that loads a recipe and deletes it, never having
        // touched the versions. Nothing in the change tracker knows they exist, so the foreign key is the
        // only thing standing between an ordinary delete and a destroyed history.
        db.Recipes.Remove(await db.Recipes.SingleAsync(candidate => candidate.Id == recipeId, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Version_numbers_are_unique_within_a_recipe_and_reusable_across_recipes()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var first = await SeedVersionAsync(db);
        var second = RecipeAggregateFixture.NewRecipe("Another cake");
        db.Recipes.Add(second);
        await db.SaveChangesAsync(token);

        // Version 1 of a different recipe is not a collision.
        AddVersion(db, second, versionNumber: 1);
        await db.SaveChangesAsync(token);
        Assert.Equal(2, await db.RecipeVersions.CountAsync(token));

        // A second version 1 of the same recipe is.
        AddVersion(db, first, versionNumber: 1);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
    }

    [Fact]
    public async Task A_proposal_id_is_only_allowed_on_a_version_that_came_from_a_proposal()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var version = AddVersion(db, recipe, versionNumber: 1);
        version.Source = RecipeVersionSource.CreatorEdit;
        version.AiProposalId = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Versions_are_scoped_to_their_workspace()
    {
        await using (var seedB = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            await SeedVersionAsync(RecipeAggregateFixture.Db(seedB));
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        await SeedVersionAsync(db);
        var token = TestContext.Current.CancellationToken;

        Assert.Single(await db.RecipeVersions.ToListAsync(token));
        Assert.Single(await db.RecipeVersionSnapshots.ToListAsync(token));
        Assert.Equal(
            RecipeAggregateFixture.WorkspaceA,
            (await db.RecipeVersions.SingleAsync(token)).WorkspaceId);
    }

    [Fact]
    public async Task A_version_cannot_descend_from_another_workspaces_version()
    {
        Guid foreignVersionId;
        await using (var seedB = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            var otherDb = RecipeAggregateFixture.Db(seedB);
            await SeedVersionAsync(otherDb);
            foreignVersionId = (await otherDb.RecipeVersions.SingleAsync(TestContext.Current.CancellationToken)).Id;
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The lineage edge carries WorkspaceId too, so a history cannot be grafted onto another workspace's.
        var version = AddVersion(db, recipe, versionNumber: 1);
        version.ParentVersionId = foreignVersionId;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_version_chain_records_what_it_was_derived_from()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(token);

        var first = AddVersion(db, recipe, versionNumber: 1);
        await db.SaveChangesAsync(token);

        var second = AddVersion(db, recipe, versionNumber: 2);
        second.ParentVersionId = first.Id;
        second.Source = RecipeVersionSource.Restore;
        await db.SaveChangesAsync(token);

        var stored = await db.RecipeVersions.SingleAsync(version => version.VersionNumber == 2, token);

        // A restore appends; it does not rewrite. Version 1 is still there and still says what it said.
        Assert.Equal(first.Id, stored.ParentVersionId);
        Assert.Equal(2, await db.RecipeVersions.CountAsync(token));
    }

    [Fact]
    public async Task Version_numbers_start_at_one()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        AddVersion(db, recipe, versionNumber: 0);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_version_cannot_be_its_own_parent()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var version = AddVersion(db, recipe, versionNumber: 1);
        version.ParentVersionId = version.Id;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_version_cannot_name_another_workspaces_recipe()
    {
        Guid foreignRecipeId;
        await using (var seedB = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            var otherDb = RecipeAggregateFixture.Db(seedB);
            var foreign = RecipeAggregateFixture.NewRecipe("B's cake");
            otherDb.Recipes.Add(foreign);
            await otherDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            foreignRecipeId = foreign.Id;
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // The primary ownership edge on this entity, and the analogue of the test that covers the six
        // aggregate children. Stamped with A, so (A, B's recipe) matches nothing.
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = foreignRecipeId,
            VersionNumber = 1,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
            SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        };
        db.RecipeVersions.Add(version);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_version_cannot_be_written_into_another_workspace()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var version = AddVersion(db, recipe, versionNumber: 1);
        version.WorkspaceId = RecipeAggregateFixture.WorkspaceB;

        // Asserting the behaviour rather than the message: two interceptors could each have grounds to
        // refuse this, and which one speaks first is registration order, not a contract.
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_workspaces_version_is_invisible_rather_than_forbidden()
    {
        Guid foreignVersionId;
        await using (var seedB = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            var otherDb = RecipeAggregateFixture.Db(seedB);
            await SeedVersionAsync(otherDb);
            foreignVersionId = (await otherDb.RecipeVersions.SingleAsync(TestContext.Current.CancellationToken)).Id;
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // Nothing distinguishes "no such version" from "someone else's version", which is what lets the read
        // seam answer 404 to both without disclosing that the version exists.
        Assert.Null(await db.RecipeVersions
            .SingleOrDefaultAsync(version => version.Id == foreignVersionId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Versions_cannot_be_read_before_a_workspace_is_resolved()
    {
        await using (var seed = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA))
        {
            await SeedVersionAsync(RecipeAggregateFixture.Db(seed));
        }

        // Matters most for the background path that will eventually write versions: an unresolved job scope
        // must throw, not quietly read an empty history.
        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.RecipeVersions.ToListAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.RecipeVersionSnapshots.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_snapshot_cannot_be_attached_to_another_workspaces_version()
    {
        Guid foreignVersionId;
        await using (var seedB = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            var otherDb = RecipeAggregateFixture.Db(seedB);
            await SeedVersionAsync(otherDb);
            foreignVersionId = (await otherDb.RecipeVersions.SingleAsync(TestContext.Current.CancellationToken)).Id;
        }

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.RecipeVersionSnapshots.Add(new RecipeVersionSnapshot
        {
            RecipeVersionId = foreignVersionId,
            Document = RecipeSnapshotSerializer.Serialize(
                RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(RecipeAggregateFixture.NewRecipe("smuggled")))),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<Recipe> SeedVersionAsync(CreatorPantryDbContext db)
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        AddVersion(db, recipe, versionNumber: 1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe;
    }

    private static RecipeVersion AddVersion(CreatorPantryDbContext db, Recipe recipe, int versionNumber)
    {
        var document = RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe));
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = versionNumber,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
            SnapshotSchemaVersion = document.SchemaVersion,
        };

        version.Snapshot = new RecipeVersionSnapshot
        {
            RecipeVersionId = version.Id,
            Document = RecipeSnapshotSerializer.Serialize(document),
        };

        db.RecipeVersions.Add(version);
        return version;
    }
}
