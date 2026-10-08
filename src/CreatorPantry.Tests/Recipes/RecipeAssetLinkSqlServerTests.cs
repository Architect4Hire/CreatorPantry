using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What 12.10i added to <c>RecipeAssetLinks</c>, on the engine that has to enforce it.
/// </summary>
/// <remarks>
/// <para>
/// The fixture runs <c>Database.Migrate()</c>, so this class existing is the evidence that SQL Server accepts
/// the migration: a link now has a second and third path to <c>Workspaces</c> — through the asset version it
/// pins and the step it illustrates — which is the shape "may cause cycles or multiple cascade paths" refuses,
/// and which SQLite does not check at all.
/// </para>
/// <para>
/// It is also the only place the link commands' concurrency can be shown. The endpoint tests run on SQLite,
/// where a row version is assigned once and never advances; whether a link spends the token it was composed
/// against is a question only <c>rowversion</c> answers.
/// </para>
/// </remarks>
public sealed class RecipeAssetLinkSqlServerTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly RecipeVersionFacts FirstVersion =
        new(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Created.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Linking_spends_the_token_it_was_composed_against()
    {
        var (recipeId, token) = await CreateAsync("Token cake");

        var linked = await LinkAsync(recipeId, token, RecipeAssetRole.Social);

        Assert.True(linked.Succeeded);
        Assert.NotEqual(token, linked.Value!.ConcurrencyToken);
        Assert.Equal(2, linked.Value.CurrentVersion!.VersionNumber);

        // The second writer, still holding the token both read: refused, and nothing of theirs written.
        var stale = await LinkAsync(recipeId, token, RecipeAssetRole.Process);

        Assert.False(stale.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, stale.Error!.Code);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var roles = await SqlServerRecipeFixture.Db(scope).RecipeAssetLinks
            .Where(link => link.RecipeId == recipeId)
            .Select(link => link.Role)
            .ToListAsync(Ct);

        Assert.Contains(RecipeAssetRole.Social, roles);
        Assert.DoesNotContain(RecipeAssetRole.Process, roles);
    }

    [Fact]
    public async Task Unlinking_spends_the_token_too_and_leaves_the_asset_where_it_was()
    {
        var (recipeId, token) = await CreateAsync("Unlink cake");
        var linked = await LinkAsync(recipeId, token, RecipeAssetRole.Social);
        var linkId = linked.Value!.AssetLinks.Single(link => link.Role == RecipeAssetRole.Social).Id;

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var business = scope.ServiceProvider.GetRequiredService<IRecipeAssetLinkBusiness>();

            var stale = await business.UnlinkAsync(recipeId, linkId, token, Ct);
            Assert.Equal(RecipeErrorCodes.RecipeConflict, stale.Error!.Code);
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var business = scope.ServiceProvider.GetRequiredService<IRecipeAssetLinkBusiness>();

            var unlinked = await business.UnlinkAsync(recipeId, linkId, linked.Value.ConcurrencyToken, Ct);

            Assert.True(unlinked.Succeeded);
            Assert.NotEqual(linked.Value.ConcurrencyToken, unlinked.Value!.ConcurrencyToken);
            Assert.DoesNotContain(unlinked.Value.AssetLinks, link => link.Id == linkId);
        }

        await using var check = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        Assert.True(await SqlServerRecipeFixture.Db(check).MediaAssets
            .AnyAsync(asset => asset.Id == SqlServerRecipeFixture.MediaAssetIdA && asset.DeletedAt == null, Ct));
    }

    /// <summary>
    /// The step key is <c>Restrict</c>: the database will not delete a step out from under its picture. That is
    /// what makes the demotion in <see cref="RecipeAssetLinkRules"/> a requirement rather than a courtesy.
    /// </summary>
    [Fact]
    public async Task A_step_cannot_be_deleted_from_under_its_image_until_the_link_is_demoted()
    {
        var (recipeId, _) = await CreateAsync("Step cake");
        Guid stepId;

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            stepId = await db.RecipeInstructionSteps.Where(step => step.RecipeId == recipeId).Select(step => step.Id).FirstAsync(Ct);

            db.RecipeAssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdA,
                Role = RecipeAssetRole.Step,
                InstructionStepId = stepId,
                SortOrder = 10,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);

            db.RecipeInstructionSteps.Remove(await db.RecipeInstructionSteps.SingleAsync(step => step.Id == stepId, Ct));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            var link = await db.RecipeAssetLinks.SingleAsync(candidate => candidate.InstructionStepId == stepId, Ct);

            RecipeAssetLinkRules.Demote(link);
            db.RecipeInstructionSteps.Remove(await db.RecipeInstructionSteps.SingleAsync(step => step.Id == stepId, Ct));
            await db.SaveChangesAsync(Ct);

            db.ChangeTracker.Clear();

            var kept = await db.RecipeAssetLinks.SingleAsync(candidate => candidate.Id == link.Id, Ct);
            Assert.Equal(RecipeAssetRole.Process, kept.Role);
            Assert.Null(kept.InstructionStepId);
        }
    }

    /// <summary>
    /// A recipe with a step image still deletes in one statement: the link goes by its cascade from the recipe
    /// in the same statement as the step it would otherwise have pinned in place.
    /// </summary>
    [Fact]
    public async Task Deleting_the_recipe_still_takes_its_step_images_with_it()
    {
        // Seeded without a version: a recipe with history is kept by its versions' own key, which is a different
        // rule from the one under test.
        var doomed = SqlServerRecipeFixture.NewRecipe("Doomed cake", SqlServerRecipeFixture.TagIdA);
        var recipeId = doomed.Id;

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.Recipes.Add(doomed);
            await db.SaveChangesAsync(Ct);

            var stepId = await db.RecipeInstructionSteps.Where(step => step.RecipeId == recipeId).Select(step => step.Id).FirstAsync(Ct);

            db.RecipeAssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdA,
                Role = RecipeAssetRole.Step,
                InstructionStepId = stepId,
                SortOrder = 10,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);

            // Only the root is tracked, so every child row is the database's to remove.
            db.Recipes.Remove(await db.Recipes.SingleAsync(recipe => recipe.Id == recipeId, Ct));
            await db.SaveChangesAsync(Ct);

            Assert.False(await db.RecipeAssetLinks.AnyAsync(link => link.RecipeId == recipeId, Ct));
            Assert.False(await db.RecipeInstructionSteps.AnyAsync(step => step.RecipeId == recipeId, Ct));
        }
    }

    [Fact]
    public async Task The_database_refuses_a_step_image_with_no_step_and_a_step_on_any_other_role()
    {
        var (recipeId, _) = await CreateAsync("Paired cake");

        foreach (var (role, withStep) in ((RecipeAssetRole, bool)[])[(RecipeAssetRole.Step, false), (RecipeAssetRole.Social, true)])
        {
            await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
            var db = SqlServerRecipeFixture.Db(scope);
            var stepId = await db.RecipeInstructionSteps.Where(step => step.RecipeId == recipeId).Select(step => step.Id).FirstAsync(Ct);

            db.RecipeAssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdA,
                Role = role,
                InstructionStepId = withStep ? stepId : null,
                SortOrder = 20,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task A_pin_must_name_a_version_that_asset_has()
    {
        var (recipeId, _) = await CreateAsync("Pinned cake");

        // An asset of this test's own, with exactly one version.
        var asset = SeededMediaAsset.For(SqlServerRecipeFixture.WorkspaceA, at: SqlServerRecipeFixture.Now);

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.MediaAssets.Add(asset);
            db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.RecipeAssetLinks.Add(Pinned(recipeId, asset.Id, versionNumber: 2, sortOrder: 30));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.RecipeAssetLinks.Add(Pinned(recipeId, asset.Id, versionNumber: 1, sortOrder: 30));
            await db.SaveChangesAsync(Ct);
        }

        // And the pin is of that asset: version 1 of another asset does not satisfy it.
        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.RecipeAssetLinks.Add(Pinned(recipeId, SqlServerRecipeFixture.MediaAssetIdA, versionNumber: 1, sortOrder: 31));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    /// <summary>
    /// The backstop behind the application's checks: with those bypassed, the composite keys themselves refuse
    /// a link in A that names B's asset, or B's step.
    /// </summary>
    [Fact]
    public async Task The_keys_refuse_a_link_naming_another_workspaces_asset_or_step()
    {
        var (recipeId, _) = await CreateAsync("A's own cake");
        Guid stepInB;

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            var recipeInB = SqlServerRecipeFixture.NewRecipe(
                "B's cake", SqlServerRecipeFixture.TagIdB, SqlServerRecipeFixture.MediaAssetIdB);

            db.Recipes.Add(recipeInB);
            await db.SaveChangesAsync(Ct);

            stepInB = recipeInB.InstructionGroups.First().Steps.First().Id;
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.RecipeAssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdB,
                Role = RecipeAssetRole.Social,
                SortOrder = 40,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA))
        {
            var db = SqlServerRecipeFixture.Db(scope);
            db.RecipeAssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipeId,
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdA,
                Role = RecipeAssetRole.Step,
                InstructionStepId = stepInB,
                SortOrder = 41,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    /// <summary>The same rule one layer up: workspace B cannot link, through the command, to a recipe of A's.</summary>
    [Fact]
    public async Task The_command_cannot_reach_another_workspaces_recipe()
    {
        var (recipeId, token) = await CreateAsync("A's cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);
        var result = await scope.ServiceProvider.GetRequiredService<IRecipeAssetLinkBusiness>().LinkAsync(
            recipeId,
            new CanonicalRecipeAssetLink
            {
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdB,
                Role = RecipeAssetRole.Social,
                ExpectedConcurrencyToken = token,
            },
            RecipeAssetLinkTargetState.Linkable,
            Ct);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    // ---- Helpers ----

    private async Task<(Guid RecipeId, string Token)> CreateAsync(string title)
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = SqlServerRecipeFixture.NewRecipe(title, SqlServerRecipeFixture.TagIdA);

        await scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>().CreateAsync(recipe, FirstVersion, [], Ct);

        return (recipe.Id, RecipeConcurrencyToken.From(recipe.RowVersion));
    }

    private async Task<Domain.Managers.Results.OperationResult<RecipeDetailServiceModel>> LinkAsync(
        Guid recipeId, string token, RecipeAssetRole role)
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<IRecipeAssetLinkBusiness>().LinkAsync(
            recipeId,
            new CanonicalRecipeAssetLink
            {
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdA,
                Role = role,
                ExpectedConcurrencyToken = token,
            },
            RecipeAssetLinkTargetState.Linkable,
            Ct);
    }

    private static RecipeAssetLink Pinned(Guid recipeId, Guid assetId, int versionNumber, int sortOrder) =>
        new()
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            MediaAssetId = assetId,
            MediaAssetVersionNumber = versionNumber,
            Role = RecipeAssetRole.Social,
            SortOrder = sortOrder,
        };
}
