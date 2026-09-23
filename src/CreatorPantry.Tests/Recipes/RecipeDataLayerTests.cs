using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The data layer's composed operations, against a real SQL Server. For the create transaction the question
/// is not "does it save" but "can it ever half-save" — a recipe without its first version is a recipe whose
/// history begins after its content did, and nothing downstream could tell that had happened. For the detail
/// read it is whether two separate queries add up to one complete recipe, and whether either of them can see
/// across a workspace boundary.
/// </summary>
public sealed class RecipeDataLayerTests(SqlServerRecipeFixture fixture) : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly RecipeVersionFacts FirstVersion =
        new(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Created.");

    [Fact]
    public async Task A_create_writes_the_recipe_its_children_and_version_one_together()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = SqlServerRecipeFixture.NewRecipe("Olive oil cake", SqlServerRecipeFixture.TagIdA);

        var created = await DataLayer(scope).CreateAsync(recipe, FirstVersion, [], TestContext.Current.CancellationToken);

        Assert.Equal(recipe.Id, created.RecipeId);
        Assert.Equal(1, created.VersionNumber);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(readScope);
        var token = TestContext.Current.CancellationToken;

        var loaded = await Repository(readScope).GetCompleteAsync(created.RecipeId, token);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Recipe.IngredientGroups.Count);
        Assert.Equal(created.VersionId, loaded.CurrentVersion!.Id);

        Assert.NotNull(await db.RecipeVersionSnapshots.SingleOrDefaultAsync(
            snapshot => snapshot.RecipeVersionId == created.VersionId, token));
    }

    [Fact]
    public async Task Version_one_records_the_content_that_was_written()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = SqlServerRecipeFixture.NewRecipe("Snapshot me", SqlServerRecipeFixture.TagIdA);

        var created = await DataLayer(scope).CreateAsync(recipe, FirstVersion, [], TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var stored = await SqlServerRecipeFixture.Db(readScope).RecipeVersionSnapshots
            .SingleAsync(snapshot => snapshot.RecipeVersionId == created.VersionId, TestContext.Current.CancellationToken);

        // The archive has to describe what actually landed, not an empty shell — the failure CompleteRecipe
        // exists to prevent, verified here on the path that really writes one.
        var document = RecipeSnapshotSerializer.Deserialize(stored.Document);

        Assert.Equal("Snapshot me", document.Recipe.Title);
        Assert.Equal(6, document.IngredientGroups.Sum(group => group.Ingredients.Count));
        Assert.Equal(4, document.InstructionGroups.Sum(group => group.Steps.Count));
        Assert.Single(document.Tags);
    }

    [Fact]
    public async Task The_version_agrees_with_the_recipe_about_who_and_when()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = SqlServerRecipeFixture.NewRecipe("Olive oil cake", SqlServerRecipeFixture.TagIdA);

        var created = await DataLayer(scope).CreateAsync(recipe, FirstVersion, [], TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var version = await SqlServerRecipeFixture.Db(readScope).RecipeVersions
            .SingleAsync(candidate => candidate.Id == created.VersionId, TestContext.Current.CancellationToken);

        Assert.Equal(recipe.CreatedByMembershipId, version.CreatedByMembershipId);
        Assert.Equal(recipe.CreatedAt, version.CreatedAt);

        // Null on purpose: version 1 superseded nothing, so there is no prior row version to quote.
        Assert.Null(version.BasedOnRecipeRowVersion);
    }

    [Fact]
    public async Task A_database_failure_writing_the_version_leaves_no_recipe_behind()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = SqlServerRecipeFixture.NewRecipe("Doomed cake", SqlServerRecipeFixture.TagIdA);

        // AiProposalAccepted with no proposal id violates CK_RecipeVersions_Proposal_Source. The recipe and
        // all its children are already in the same insert batch when the version row is rejected, so this
        // exercises the engine rolling back work it had already accepted — not EF merely declining to send.
        var doomed = new RecipeVersionFacts(RecipeVersionSource.AiProposalAccepted, RecipeVersionReadiness.Draft, null);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => DataLayer(scope).CreateAsync(recipe, doomed, [], TestContext.Current.CancellationToken));

        await AssertNothingSurvivedAsync(recipe.Id);
    }

    [Fact]
    public async Task A_mid_save_failure_leaves_no_recipe_behind()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA, ThrowOnVersionInsert.Instance);
        var recipe = SqlServerRecipeFixture.NewRecipe("Also doomed", SqlServerRecipeFixture.TagIdA);

        // The other shape of failure: something in the application throws while the unit is being written.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DataLayer(scope).CreateAsync(recipe, FirstVersion, [], TestContext.Current.CancellationToken));

        await AssertNothingSurvivedAsync(recipe.Id);
    }

    [Fact]
    public async Task A_created_recipe_is_invisible_to_the_other_workspace()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = SqlServerRecipeFixture.NewRecipe("A's cake", SqlServerRecipeFixture.TagIdA);
        var created = await DataLayer(scope).CreateAsync(recipe, FirstVersion, [], TestContext.Current.CancellationToken);

        await using var otherScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);
        var token = TestContext.Current.CancellationToken;

        Assert.Null(await Repository(otherScope).GetCompleteAsync(created.RecipeId, token));
        Assert.Null(await SqlServerRecipeFixture.Db(otherScope).RecipeVersions
            .SingleOrDefaultAsync(version => version.Id == created.VersionId, token));
    }

    // ---- The detail read ----

    [Fact]
    public async Task A_detail_read_returns_the_aggregate_its_version_and_its_tag_names()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await DataLayer(scope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Recipe.Recipe.IngredientGroups.Count);
        Assert.Equal(created.VersionId, loaded.Recipe.CurrentVersion!.Id);

        // The second read is the whole reason this method exists rather than the repository being called
        // directly: a tag link carries an id, and a client cannot render an id.
        Assert.Equal("Weeknight", Assert.Single(loaded.Tags).Name);
    }

    [Fact]
    public async Task A_detail_read_of_an_unknown_recipe_finds_nothing()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        Assert.Null(await DataLayer(scope).GetDetailAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_detail_read_from_the_other_workspace_finds_nothing()
    {
        var created = await CreateAsync("A's cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);

        // Indistinguishable from the unknown-id case above, which is what lets the read seam answer 404 to
        // both without disclosing that the recipe exists.
        Assert.Null(await DataLayer(scope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken));
    }

    // ---- The update seam ----

    private static readonly RecipeVersionFacts NextVersion =
        new(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, "Edited.");

    /// <summary>
    /// Loads a recipe for editing in its own scope, so that a test can hold two independent readers of the
    /// same row — which is the only way the concurrency question can be asked honestly.
    /// </summary>
    private async Task<TaggedRecipe> LoadForUpdateAsync(AsyncServiceScope scope, Guid recipeId) =>
        (await DataLayer(scope).GetForUpdateAsync(recipeId, TestContext.Current.CancellationToken))!;

    [Fact]
    public async Task An_edit_writes_the_next_version_and_moves_the_recipe_on()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        var readToken = loaded.Recipe.Recipe.RowVersion;

        loaded.Recipe.Recipe.Title = "Lemon olive oil cake";
        var outcome = await DataLayer(scope).UpdateAsync(loaded, NextVersion, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Conflicted);
        Assert.Equal(2, outcome.Version!.VersionNumber);
        Assert.Equal(created.VersionId, outcome.Version.ParentVersionId);

        // The state the edit was composed against, which is knowable before the save — unlike the token the
        // save is about to generate.
        Assert.Equal(readToken, outcome.Version.BasedOnRecipeRowVersion);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await Repository(readScope).GetCompleteAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal("Lemon olive oil cake", reread!.Recipe.Title);
        Assert.Equal(2, reread.CurrentVersion!.VersionNumber);

        // The whole mechanism rests on this: the server bumps the token on every update, so the next writer
        // holding the old one is refused.
        Assert.NotEqual(readToken, reread.Recipe.RowVersion);
    }

    [Fact]
    public async Task The_edited_aggregate_carries_the_token_the_save_generated()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        var readWith = loaded.Recipe.Recipe.RowVersion;

        loaded.Recipe.Recipe.Title = "Lemon olive oil cake";
        await DataLayer(scope).UpdateAsync(loaded, NextVersion, null, TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var stored = await Repository(readScope).GetCompleteAsync(created.RecipeId, TestContext.Current.CancellationToken);

        // The instance the response is mapped from, not a re-read. Business answers an edit from this very
        // aggregate, so if EF ever stopped writing the generated token back onto it, every client following
        // the documented rebind flow would be refused on its next save — and nothing else in the suite would
        // notice, because SQLite never moves the token at all.
        Assert.NotEqual(readWith, loaded.Recipe.Recipe.RowVersion);
        Assert.Equal(stored!.Recipe.RowVersion, loaded.Recipe.Recipe.RowVersion);
    }

    [Fact]
    public async Task A_second_edit_chains_onto_the_first()
    {
        var created = await CreateAsync("First");

        await using var firstScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var first = await LoadForUpdateAsync(firstScope, created.RecipeId);
        var firstToken = first.Recipe.Recipe.RowVersion;
        first.Recipe.Recipe.Title = "Second";
        var second = await DataLayer(firstScope).UpdateAsync(first, NextVersion, null, TestContext.Current.CancellationToken);

        await using var secondScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reloaded = await LoadForUpdateAsync(secondScope, created.RecipeId);
        var secondToken = reloaded.Recipe.Recipe.RowVersion;
        reloaded.Recipe.Recipe.Title = "Third";
        var third = await DataLayer(secondScope).UpdateAsync(reloaded, NextVersion, null, TestContext.Current.CancellationToken);

        // Numbering and lineage were only ever proven for the 1 → 2 step, which is the step where the parent
        // happens to be version 1 and "one past what we read" happens to be 2.
        Assert.Equal(3, third.Version!.VersionNumber);
        Assert.Equal(second.Version!.Id, third.Version.ParentVersionId);
        Assert.Equal(secondToken, third.Version.BasedOnRecipeRowVersion);
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_conflict_is_not_reported_as_one()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "Never landed";

        // AiProposalAccepted with no proposal id violates CK_RecipeVersions_Proposal_Source — a genuine
        // integrity failure, raised by the engine as an ordinary DbUpdateException, with the recipe row
        // untouched. It must not be swallowed: a creator told "this recipe has changed since you opened it"
        // about a recipe nobody else is touching has no way out and nothing anyone can act on.
        var doomed = new RecipeVersionFacts(RecipeVersionSource.AiProposalAccepted, RecipeVersionReadiness.Draft, null);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => DataLayer(scope).UpdateAsync(loaded, doomed, null, TestContext.Current.CancellationToken));

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await Repository(readScope).GetCompleteAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal("Olive oil cake", reread!.Recipe.Title);
        Assert.Equal(1, reread.CurrentVersion!.VersionNumber);
    }

    [Fact]
    public async Task A_conflicted_edit_leaves_nothing_staged_on_the_context()
    {
        var created = await CreateAsync("Contested cake");

        await using var firstScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        await using var secondScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var first = await LoadForUpdateAsync(firstScope, created.RecipeId);
        var second = await LoadForUpdateAsync(secondScope, created.RecipeId);

        first.Recipe.Recipe.Title = "First writer wins";
        await DataLayer(firstScope).UpdateAsync(first, NextVersion, null, TestContext.Current.CancellationToken);

        second.Recipe.Recipe.Title = "Second writer loses";
        second.Recipe.Recipe.Headnote = "And this must not survive either.";
        var loser = await DataLayer(secondScope).UpdateAsync(
            second, NextVersion, [new RecipeTagName("Citrus", "citrus")], TestContext.Current.CancellationToken);

        Assert.True(loser.Conflicted);

        // "Nothing was written" has to be true of the context too, not only of the database. A refused edit
        // left staged would be committed by the next SaveChanges on this scope — an audit row, an outbox
        // message, anything — and the recipe would change with no version recording it.
        var db = SqlServerRecipeFixture.Db(secondScope);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State != EntityState.Detached);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await DataLayer(readScope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal("First writer wins", reread!.Recipe.Recipe.Title);
        Assert.Equal("Weeknight", Assert.Single(reread.Tags).Name);
        Assert.Equal(
            0,
            await SqlServerRecipeFixture.Db(readScope).WorkspaceTags
                .CountAsync(tag => tag.NormalizedName == "citrus", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_new_version_records_the_content_the_edit_produced()
    {
        var created = await CreateAsync("Before");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "After";

        var outcome = await DataLayer(scope).UpdateAsync(loaded, NextVersion, null, TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var stored = await SqlServerRecipeFixture.Db(readScope).RecipeVersionSnapshots
            .SingleAsync(snapshot => snapshot.RecipeVersionId == outcome.Version!.Id, TestContext.Current.CancellationToken);

        var document = RecipeSnapshotSerializer.Deserialize(stored.Document);

        Assert.Equal("After", document.Recipe.Title);

        // The children were never touched by the edit, and the snapshot has to carry them anyway — an
        // archive that recorded only what changed could not be restored from.
        Assert.Equal(6, document.IngredientGroups.Sum(group => group.Ingredients.Count));
    }

    [Fact]
    public async Task Version_one_is_left_exactly_as_it_was()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "Something else";
        await DataLayer(scope).UpdateAsync(loaded, NextVersion, null, TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var first = await SqlServerRecipeFixture.Db(readScope).RecipeVersions
            .SingleAsync(version => version.Id == created.VersionId, TestContext.Current.CancellationToken);
        var snapshot = await SqlServerRecipeFixture.Db(readScope).RecipeVersionSnapshots
            .SingleAsync(candidate => candidate.RecipeVersionId == created.VersionId, TestContext.Current.CancellationToken);

        // History is appended to, never rewritten. The interceptor would refuse an update to this row, but
        // the claim worth asserting is that nothing even tried.
        Assert.Equal(1, first.VersionNumber);
        Assert.Equal("Created.", first.Reason);
        Assert.Equal("Olive oil cake", RecipeSnapshotSerializer.Deserialize(snapshot.Document).Recipe.Title);
    }

    [Fact]
    public async Task The_second_writer_holding_a_stale_row_is_refused()
    {
        var created = await CreateAsync("Contested cake");

        // Two scopes, two DbContexts, two independent reads of the same row — the shape of two creators with
        // the recipe open at once.
        await using var firstScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        await using var secondScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var first = await LoadForUpdateAsync(firstScope, created.RecipeId);
        var second = await LoadForUpdateAsync(secondScope, created.RecipeId);

        first.Recipe.Recipe.Title = "First writer wins";
        var winner = await DataLayer(firstScope).UpdateAsync(first, NextVersion, null, TestContext.Current.CancellationToken);
        Assert.False(winner.Conflicted);

        second.Recipe.Recipe.Title = "Second writer loses";
        var loser = await DataLayer(secondScope).UpdateAsync(second, NextVersion, null, TestContext.Current.CancellationToken);

        // The lost update this whole seam exists to prevent. Without the row version the second save would
        // silently destroy the first creator's work.
        Assert.True(loser.Conflicted);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await Repository(readScope).GetCompleteAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal("First writer wins", reread!.Recipe.Title);

        // And no duplicate version number: the concurrency check is what allocates the number, not the read.
        Assert.Equal(2, reread.CurrentVersion!.VersionNumber);
        Assert.Equal(
            2,
            await SqlServerRecipeFixture.Db(readScope).RecipeVersions
                .CountAsync(version => version.RecipeId == created.RecipeId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failure_part_way_through_an_edit_leaves_the_recipe_as_it_was()
    {
        var created = await CreateAsync("Unchanged cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA, ThrowOnVersionInsert.Instance);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "Never landed";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DataLayer(scope).UpdateAsync(loaded, NextVersion, null, TestContext.Current.CancellationToken));

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await Repository(readScope).GetCompleteAsync(created.RecipeId, TestContext.Current.CancellationToken);

        // The inverse of AssertNothingSurvivedAsync: here the recipe must survive, and the half-applied edit
        // must not. A recipe changed without the version recording it would be history with a hole in it.
        Assert.Equal("Unchanged cake", reread!.Recipe.Title);
        Assert.Equal(1, reread.CurrentVersion!.VersionNumber);
        Assert.Equal(
            1,
            await SqlServerRecipeFixture.Db(readScope).RecipeVersions
                .CountAsync(version => version.RecipeId == created.RecipeId, TestContext.Current.CancellationToken));
    }

    // ---- Tags ----

    [Fact]
    public async Task A_replaced_tag_set_adds_removes_and_reuses_in_one_unit()
    {
        var created = await CreateAsync("Tagged cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "Retagged cake";

        // "weeknight" is the tag it already carries, so it should be kept rather than re-created; "citrus" is
        // new to this workspace and becomes a vocabulary row in the same transaction.
        var outcome = await DataLayer(scope).UpdateAsync(
            loaded,
            NextVersion,
            [new RecipeTagName("Weeknight", "weeknight"), new RecipeTagName("Citrus", "citrus")],
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Conflicted);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await DataLayer(readScope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken);
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(
            (string[])["citrus", "weeknight"],
            reread!.Tags.Select(tag => tag.NormalizedName).Order());

        // Reused, not duplicated: the workspace's vocabulary must not grow a second "weeknight" because a
        // recipe listed the tag it already had.
        Assert.Equal(
            SqlServerRecipeFixture.TagIdA,
            reread.Tags.Single(tag => tag.NormalizedName == "weeknight").Id);
        Assert.Equal(
            1,
            await SqlServerRecipeFixture.Db(readScope).WorkspaceTags.CountAsync(tag => tag.NormalizedName == "weeknight", token));
    }

    [Fact]
    public async Task A_tag_named_the_same_as_the_other_workspaces_is_created_rather_than_borrowed()
    {
        // Workspace B is seeded with its own "weeknight". A lookup that escaped the query filter would
        // silently link this recipe to another workspace's vocabulary row.
        var created = await CreateInAsync(SqlServerRecipeFixture.WorkspaceB, "B's cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "B's retagged cake";

        await DataLayer(scope).UpdateAsync(
            loaded, NextVersion, [new RecipeTagName("Weeknight", "weeknight")], TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);
        var reread = await DataLayer(readScope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal(SqlServerRecipeFixture.TagIdB, Assert.Single(reread!.Tags).Id);
    }

    [Fact]
    public async Task An_empty_tag_set_removes_every_link()
    {
        var created = await CreateAsync("Tagged cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "Untagged cake";

        await DataLayer(scope).UpdateAsync(loaded, NextVersion, [], TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await DataLayer(readScope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Empty(reread!.Tags);
        Assert.Empty(reread.Recipe.Recipe.Tags);
    }

    [Fact]
    public async Task Tags_handed_down_as_null_are_left_alone()
    {
        var created = await CreateAsync("Tagged cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Title = "Still tagged";

        await DataLayer(scope).UpdateAsync(loaded, NextVersion, null, TestContext.Current.CancellationToken);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await DataLayer(readScope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal("Weeknight", Assert.Single(reread!.Tags).Name);
    }

    // ---- Reading for an update ----

    [Fact]
    public async Task A_read_for_an_update_returns_the_whole_aggregate_and_its_current_version()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await DataLayer(scope).GetForUpdateAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Recipe.Recipe.IngredientGroups.Count);
        Assert.Equal(created.VersionId, loaded.Recipe.CurrentVersion!.Id);
        Assert.Equal("Weeknight", Assert.Single(loaded.Tags).Name);

        // Without a token there is nothing for the write to check against, and the seam falls back to
        // last-write-wins by omission.
        Assert.NotEmpty(loaded.Recipe.Recipe.RowVersion);
    }

    [Fact]
    public async Task A_read_for_an_update_cannot_see_the_other_workspace()
    {
        var created = await CreateAsync("A's cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);

        // Indistinguishable from an unknown id, which is what lets the seam answer 404 to both without
        // disclosing that the recipe exists.
        Assert.Null(await DataLayer(scope).GetForUpdateAsync(created.RecipeId, TestContext.Current.CancellationToken));
        Assert.Null(await DataLayer(scope).GetForUpdateAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    private Task<CreatedRecipe> CreateAsync(string title) =>
        CreateInAsync(SqlServerRecipeFixture.WorkspaceA, title);

    private async Task<CreatedRecipe> CreateInAsync(Guid workspaceId, string title)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var tagId = workspaceId == SqlServerRecipeFixture.WorkspaceA
            ? SqlServerRecipeFixture.TagIdA
            : SqlServerRecipeFixture.TagIdB;

        return await DataLayer(scope).CreateAsync(
            SqlServerRecipeFixture.NewRecipe(title, tagId),
            FirstVersion,
            [],
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Nothing of the aggregate, its children, or its version is left anywhere in the database.
    /// </summary>
    private async Task AssertNothingSurvivedAsync(Guid recipeId)
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);
        var token = TestContext.Current.CancellationToken;

        Assert.Null(await Repository(scope).GetCompleteAsync(recipeId, token));

        // Asserted per table rather than through the aggregate root: a rollback that left orphaned children
        // behind would still make the root read as absent, and nothing else would ever notice them.
        Assert.Empty(await db.RecipeIngredientGroups.Where(group => group.RecipeId == recipeId).ToListAsync(token));
        Assert.Empty(await db.RecipeIngredients.Where(line => line.RecipeId == recipeId).ToListAsync(token));
        Assert.Empty(await db.RecipeInstructionGroups.Where(group => group.RecipeId == recipeId).ToListAsync(token));
        Assert.Empty(await db.RecipeInstructionSteps.Where(step => step.RecipeId == recipeId).ToListAsync(token));
        Assert.Empty(await db.RecipeEquipment.Where(item => item.RecipeId == recipeId).ToListAsync(token));
        Assert.Empty(await db.RecipeAssetLinks.Where(link => link.RecipeId == recipeId).ToListAsync(token));
        Assert.Empty(await db.RecipeTags.Where(tag => tag.RecipeId == recipeId).ToListAsync(token));
        Assert.Empty(await db.RecipeVersions.Where(version => version.RecipeId == recipeId).ToListAsync(token));
    }

    private static IRecipeDataLayer DataLayer(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>();

    private static IRecipeRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeRepository>();
}
