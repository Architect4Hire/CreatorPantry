using CreatorPantry.Domain.Managers.Audit;
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

    // ---- The restore seam ----

    /// <summary>
    /// Loads the unit a restore operates on, in its own scope — so a test can hold two independent readers
    /// of the same row, which is the only way the concurrency question can be asked honestly.
    /// </summary>
    private async Task<RecipeRestoreUnit> LoadForRestoreAsync(
        AsyncServiceScope scope, Guid recipeId, int versionNumber) =>
        (await DataLayer(scope).GetForRestoreAsync(recipeId, versionNumber, TestContext.Current.CancellationToken))!;

    private static RecipeVersionFacts RestoredFrom(Guid versionId) =>
        new(RecipeVersionSource.Restore, RecipeVersionReadiness.Draft, "Put it back.", versionId);

    [Fact]
    public async Task A_restore_unit_carries_the_tracked_recipe_and_the_named_versions_document()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var unit = await LoadForRestoreAsync(scope, created.RecipeId, 1);

        Assert.Equal(created.RecipeId, unit.Recipe.Recipe.Recipe.Id);
        Assert.Equal(created.VersionId, unit.Snapshot!.Id);
        Assert.Equal("Olive oil cake", RecipeSnapshotSerializer.Deserialize(unit.Snapshot.Document).Recipe.Title);
    }

    /// <summary>
    /// Two kinds of absence, kept apart. A readable recipe with no such version is a unit with no snapshot;
    /// only an invisible recipe is a null unit — and collapsing them would tell a creator who mistyped a
    /// number that their recipe does not exist.
    /// </summary>
    [Fact]
    public async Task A_version_number_this_recipe_does_not_have_is_a_unit_with_no_snapshot()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var unit = await LoadForRestoreAsync(scope, created.RecipeId, 9);

        Assert.NotNull(unit);
        Assert.Null(unit.Snapshot);
    }

    [Fact]
    public async Task A_restore_unit_for_an_unknown_recipe_is_null()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        Assert.Null(await DataLayer(scope).GetForRestoreAsync(
            Guid.NewGuid(), 1, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The two-workspace case at the layer where the query filter decides it, and with the id known exactly.
    /// Indistinguishable from an id that never existed, which is the point.
    /// </summary>
    [Fact]
    public async Task A_restore_unit_across_the_workspace_boundary_is_null_too()
    {
        var created = await CreateInAsync(SqlServerRecipeFixture.WorkspaceB, "B's cake");

        await using var inA = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        Assert.Null(await DataLayer(inA).GetForRestoreAsync(
            created.RecipeId, 1, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The lineage, against a real save: the new version numbers one past what was current, its parent is the
    /// version it replaced, and its restored-from id is the version its content came from. Two different
    /// ancestors, and the whole reason the second column exists.
    /// </summary>
    [Fact]
    public async Task A_restore_writes_a_new_version_naming_what_it_replaced_and_what_it_came_from()
    {
        var created = await CreateAsync("Olive oil cake");

        // An ordinary edit first, so there is a version 2 to restore over — and so "parent" and
        // "restored from" are demonstrably different rows.
        await using var editScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var editing = await LoadForUpdateAsync(editScope, created.RecipeId);
        editing.Recipe.Recipe.Title = "Lemon olive oil cake";
        var edited = await DataLayer(editScope).UpdateAsync(
            editing, NextVersion, null, TestContext.Current.CancellationToken);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var unit = await LoadForRestoreAsync(scope, created.RecipeId, 1);
        var readToken = unit.Recipe.Recipe.Recipe.RowVersion;

        var document = RecipeSnapshotSerializer.Deserialize(unit.Snapshot!.Document);
        Assert.True(RecipeSnapshotReconciler.Apply(unit.Recipe.Recipe.Recipe, document, unit.Recipe.Tags));

        var outcome = await DataLayer(scope).RestoreAsync(
            unit.Recipe, RestoredFrom(unit.Snapshot.Id), unit.Recipe.Tags, TestContext.Current.CancellationToken);

        Assert.False(outcome.Conflicted);
        Assert.Equal(3, outcome.Version!.VersionNumber);
        Assert.Equal(RecipeVersionSource.Restore, outcome.Version.Source);

        // What it replaced: the version that was current when the restore ran.
        Assert.Equal(edited.Version!.Id, outcome.Version.ParentVersionId);

        // Where the content came from: version 1.
        Assert.Equal(created.VersionId, outcome.Version.RestoredFromVersionId);

        // The state the restore was composed against, knowable before the save.
        Assert.Equal(readToken, outcome.Version.BasedOnRecipeRowVersion);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await DataLayer(readScope).GetDetailAsync(created.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal("Olive oil cake", reread!.Recipe.Recipe.Title);
        Assert.Equal(3, reread.Recipe.CurrentVersion!.VersionNumber);
    }

    /// <summary>
    /// The restored row itself is never touched. Asserted against the database rather than trusted from the
    /// interface, because "never reactivate an old row as current" is the restriction this route turns on.
    /// </summary>
    [Fact]
    public async Task A_restore_leaves_the_version_it_restored_exactly_as_it_was()
    {
        var created = await CreateAsync("Olive oil cake");
        var token = TestContext.Current.CancellationToken;

        await using var beforeScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var before = await SqlServerRecipeFixture.Db(beforeScope).RecipeVersions
            .AsNoTracking()
            .SingleAsync(version => version.Id == created.VersionId, token);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var unit = await LoadForRestoreAsync(scope, created.RecipeId, 1);
        unit.Recipe.Recipe.Recipe.Title = "Something restored";
        await DataLayer(scope).RestoreAsync(
            unit.Recipe, RestoredFrom(unit.Snapshot!.Id), unit.Recipe.Tags, token);

        await using var afterScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(afterScope);

        var after = await db.RecipeVersions.AsNoTracking().SingleAsync(version => version.Id == created.VersionId, token);

        Assert.Equal(before.VersionNumber, after.VersionNumber);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.Readiness, after.Readiness);
        Assert.Equal(before.Reason, after.Reason);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ParentVersionId, after.ParentVersionId);

        // And the history grew rather than being rewritten.
        Assert.Equal(2, await db.RecipeVersions.CountAsync(version => version.RecipeId == created.RecipeId, token));
    }

    /// <summary>
    /// The race no in-memory check can catch: the row moved between the read and the save. Nothing commits,
    /// and nothing is left staged on the context either — a refused restore left pending would be committed
    /// by the next <c>SaveChanges</c> on this scope.
    /// </summary>
    [Fact]
    public async Task A_restore_onto_a_recipe_that_moved_on_conflicts_and_writes_nothing()
    {
        var created = await CreateAsync("Contested cake");
        var token = TestContext.Current.CancellationToken;

        await using var firstScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        await using var secondScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var editing = await LoadForUpdateAsync(firstScope, created.RecipeId);
        var restoring = await LoadForRestoreAsync(secondScope, created.RecipeId, 1);

        editing.Recipe.Recipe.Title = "The editor won";
        await DataLayer(firstScope).UpdateAsync(editing, NextVersion, null, token);

        restoring.Recipe.Recipe.Recipe.Title = "The restore lost";
        var loser = await DataLayer(secondScope).RestoreAsync(
            restoring.Recipe, RestoredFrom(restoring.Snapshot!.Id), restoring.Recipe.Tags, token);

        Assert.True(loser.Conflicted);

        var db = SqlServerRecipeFixture.Db(secondScope);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State != EntityState.Detached);

        await db.SaveChangesAsync(token);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await DataLayer(readScope).GetDetailAsync(created.RecipeId, token);

        Assert.Equal("The editor won", reread!.Recipe.Recipe.Title);
        Assert.Equal(2, reread.Recipe.CurrentVersion!.VersionNumber);
        Assert.Equal(
            2,
            await SqlServerRecipeFixture.Db(readScope).RecipeVersions
                .CountAsync(version => version.RecipeId == created.RecipeId, token));
    }

    /// <summary>
    /// A failure part-way through the unit rolls the whole thing back: the recipe keeps its content and the
    /// history gains nothing. A recipe changed without the version recording it would be history with a hole
    /// in it.
    /// </summary>
    [Fact]
    public async Task A_mid_save_failure_during_a_restore_leaves_the_recipe_and_its_history_untouched()
    {
        var created = await CreateAsync("Unchanged cake");
        var token = TestContext.Current.CancellationToken;

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA, ThrowOnVersionInsert.Instance);
        var unit = await LoadForRestoreAsync(scope, created.RecipeId, 1);
        unit.Recipe.Recipe.Recipe.Title = "Should never land";

        await Assert.ThrowsAsync<InvalidOperationException>(() => DataLayer(scope).RestoreAsync(
            unit.Recipe, RestoredFrom(unit.Snapshot!.Id), unit.Recipe.Tags, token));

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await Repository(readScope).GetCompleteAsync(created.RecipeId, token);

        Assert.Equal("Unchanged cake", reread!.Recipe.Title);
        Assert.Equal(1, reread.CurrentVersion!.VersionNumber);
        Assert.Equal(
            1,
            await SqlServerRecipeFixture.Db(readScope).RecipeVersions
                .CountAsync(version => version.RecipeId == created.RecipeId, token));
    }

    /// <summary>
    /// The database refuses a restored-from id that names a version of another workspace's recipe, because
    /// the foreign key is composite. That is the containment being structural rather than a check the code
    /// above has to remember.
    /// </summary>
    [Fact]
    public async Task A_restored_from_id_cannot_name_another_workspaces_version()
    {
        var mine = await CreateAsync("A's cake");
        var theirs = await CreateInAsync(SqlServerRecipeFixture.WorkspaceB, "B's cake");
        var token = TestContext.Current.CancellationToken;

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var unit = await LoadForRestoreAsync(scope, mine.RecipeId, 1);
        unit.Recipe.Recipe.Recipe.Title = "Borrowed lineage";

        await Assert.ThrowsAsync<DbUpdateException>(() => DataLayer(scope).RestoreAsync(
            unit.Recipe, RestoredFrom(theirs.VersionId), unit.Recipe.Tags, token));
    }

    /// <summary>
    /// A restored-from id on a version that is not a restore is refused by
    /// <c>CK_RecipeVersions_RestoredFrom_Source</c>, and so is a restore with no id — the pairing cannot
    /// drift, exactly as the proposal columns cannot.
    /// </summary>
    [Fact]
    public async Task The_restored_from_id_and_the_source_must_agree()
    {
        var created = await CreateAsync("Paired cake");
        var token = TestContext.Current.CancellationToken;

        await using var mismatched = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var first = await LoadForRestoreAsync(mismatched, created.RecipeId, 1);
        first.Recipe.Recipe.Recipe.Title = "An edit claiming a restore's lineage";

        await Assert.ThrowsAsync<DbUpdateException>(() => DataLayer(mismatched).RestoreAsync(
            first.Recipe,
            new RecipeVersionFacts(
                RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, null, first.Snapshot!.Id),
            first.Recipe.Tags,
            token));

        await using var missing = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var second = await LoadForRestoreAsync(missing, created.RecipeId, 1);
        second.Recipe.Recipe.Recipe.Title = "A restore claiming no source";

        await Assert.ThrowsAsync<DbUpdateException>(() => DataLayer(missing).RestoreAsync(
            second.Recipe,
            new RecipeVersionFacts(RecipeVersionSource.Restore, RecipeVersionReadiness.Draft, null),
            second.Recipe.Tags,
            token));
    }

    // ---- The archive seam ----

    private static AuditEntry ArchiveEntry(Guid recipeId) =>
        new(
            "user-1",
            RecipeAuditActions.Archived,
            RecipeAuditActions.ResourceType,
            recipeId.ToString("D"),
            Guid.NewGuid(),
            "Archived the recipe.",
            BeforeReference: nameof(RecipeStatus.Draft),
            AfterReference: nameof(RecipeStatus.Archived));

    /// <summary>
    /// The status change and its audit entry commit together, and the token moves — which the SQLite
    /// endpoint tests cannot show, because their customizer never bumps a row version.
    /// </summary>
    [Fact]
    public async Task Archiving_writes_the_status_and_its_audit_entry_in_one_unit()
    {
        var created = await CreateAsync("Olive oil cake");
        var token = TestContext.Current.CancellationToken;

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        var readWith = loaded.Recipe.Recipe.RowVersion;

        loaded.Recipe.Recipe.Status = RecipeStatus.Archived;
        var committed = await DataLayer(scope).TrySetStatusAsync(loaded, ArchiveEntry(created.RecipeId), token);

        Assert.True(committed);

        // The server generated a new token, so every edit composed against the old state is now stale.
        Assert.NotEqual(readWith, loaded.Recipe.Recipe.RowVersion);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(readScope);

        var reread = await Repository(readScope).GetCompleteAsync(created.RecipeId, token);
        Assert.Equal(RecipeStatus.Archived, reread!.Recipe.Status);

        var entry = await db.AuditLogs.SingleAsync(log => log.ResourceId == created.RecipeId.ToString("D"), token);
        Assert.Equal(RecipeAuditActions.Archived, entry.Action);
        Assert.Equal(SqlServerRecipeFixture.WorkspaceA, entry.WorkspaceId);

        // No version: a version records what a recipe said, and shelving it changes none of that.
        Assert.Equal(1, await db.RecipeVersions.CountAsync(version => version.RecipeId == created.RecipeId, token));
    }

    /// <summary>
    /// The archived recipe keeps everything. Asserted against the database rather than the read model,
    /// because "archive is not a delete" is a claim about rows.
    /// </summary>
    [Fact]
    public async Task Archiving_removes_no_rows()
    {
        var created = await CreateAsync("Olive oil cake");
        var token = TestContext.Current.CancellationToken;

        await using var beforeScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var before = await Repository(beforeScope).GetCompleteAsync(created.RecipeId, token);
        var counts = (
            before!.Recipe.IngredientGroups.Count,
            before.Recipe.InstructionGroups.Count,
            before.Recipe.Equipment.Count,
            before.Recipe.Tags.Count);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var loaded = await LoadForUpdateAsync(scope, created.RecipeId);
        loaded.Recipe.Recipe.Status = RecipeStatus.Archived;
        await DataLayer(scope).TrySetStatusAsync(loaded, ArchiveEntry(created.RecipeId), token);

        await using var afterScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var after = await Repository(afterScope).GetCompleteAsync(created.RecipeId, token);

        Assert.Equal(
            counts,
            (after!.Recipe.IngredientGroups.Count,
                after.Recipe.InstructionGroups.Count,
                after.Recipe.Equipment.Count,
                after.Recipe.Tags.Count));
    }

    /// <summary>
    /// The race no in-memory check catches: the row moved between the read and the save. Nothing commits —
    /// not the status, and not the audit entry that would otherwise claim it did.
    /// </summary>
    [Fact]
    public async Task An_archive_onto_a_recipe_that_moved_on_conflicts_and_writes_nothing()
    {
        var created = await CreateAsync("Contested cake");
        var token = TestContext.Current.CancellationToken;

        await using var firstScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        await using var secondScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var editing = await LoadForUpdateAsync(firstScope, created.RecipeId);
        var archiving = await LoadForUpdateAsync(secondScope, created.RecipeId);

        editing.Recipe.Recipe.Title = "The editor won";
        await DataLayer(firstScope).UpdateAsync(editing, NextVersion, null, token);

        archiving.Recipe.Recipe.Status = RecipeStatus.Archived;
        var committed = await DataLayer(secondScope).TrySetStatusAsync(
            archiving, ArchiveEntry(created.RecipeId), token);

        Assert.False(committed);

        // Nothing staged either, or the next SaveChanges on this scope would commit a refused transition.
        var db = SqlServerRecipeFixture.Db(secondScope);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State != EntityState.Detached);

        await db.SaveChangesAsync(token);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await Repository(readScope).GetCompleteAsync(created.RecipeId, token);

        Assert.Equal("The editor won", reread!.Recipe.Title);
        Assert.NotEqual(RecipeStatus.Archived, reread.Recipe.Status);
        Assert.Equal(
            0,
            await SqlServerRecipeFixture.Db(readScope).AuditLogs
                .CountAsync(log => log.ResourceId == created.RecipeId.ToString("D"), token));
    }

    // ---- The duplicate seam ----

    [Fact]
    public async Task A_duplicate_source_read_returns_the_named_versions_document()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var (visible, source) = await DataLayer(scope).FindDuplicateSourceAsync(
            created.RecipeId, 1, TestContext.Current.CancellationToken);

        Assert.True(visible);
        Assert.Equal(created.VersionId, source!.Id);
        Assert.Equal("Olive oil cake", RecipeSnapshotSerializer.Deserialize(source.Document).Recipe.Title);
    }

    /// <summary>
    /// Omitting the version reads the most recent one — and because every change that alters a recipe writes
    /// a version, that document is the recipe as it currently stands. This is the invariant the duplicate
    /// route relies on to have one code path and lineage that always names a real version.
    /// </summary>
    [Fact]
    public async Task A_duplicate_source_read_with_no_version_returns_the_current_content()
    {
        var created = await CreateAsync("Olive oil cake");
        var token = TestContext.Current.CancellationToken;

        await using var editScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var editing = await LoadForUpdateAsync(editScope, created.RecipeId);
        editing.Recipe.Recipe.Title = "Lemon olive oil cake";
        var edited = await DataLayer(editScope).UpdateAsync(editing, NextVersion, null, token);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var (visible, source) = await DataLayer(scope).FindDuplicateSourceAsync(created.RecipeId, null, token);

        Assert.True(visible);
        Assert.Equal(edited.Version!.Id, source!.Id);
        Assert.Equal(2, source.VersionNumber);

        // The live aggregate and that document say the same thing.
        var live = await DataLayer(scope).GetDetailAsync(created.RecipeId, token);
        Assert.Equal(
            live!.Recipe.Recipe.Title,
            RecipeSnapshotSerializer.Deserialize(source.Document).Recipe.Title);
    }

    [Fact]
    public async Task A_duplicate_source_read_for_an_unknown_recipe_reports_it_invisible()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var (visible, source) = await DataLayer(scope).FindDuplicateSourceAsync(
            Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        Assert.False(visible);
        Assert.Null(source);
    }

    /// <summary>
    /// The two-workspace case, with the id known exactly. Indistinguishable from an id that never existed —
    /// which is what lets the seam above answer 404 to both.
    /// </summary>
    [Fact]
    public async Task A_duplicate_source_read_across_the_workspace_boundary_reports_it_invisible()
    {
        var created = await CreateInAsync(SqlServerRecipeFixture.WorkspaceB, "B's cake");

        await using var inA = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var (visible, source) = await DataLayer(inA).FindDuplicateSourceAsync(
            created.RecipeId, 1, TestContext.Current.CancellationToken);

        Assert.False(visible);
        Assert.Null(source);
    }

    /// <summary>
    /// Visible, with no such version: a different answer from an invisible recipe, so a creator who mistyped
    /// a number is not told their recipe does not exist.
    /// </summary>
    [Fact]
    public async Task A_duplicate_source_read_for_a_missing_version_reports_the_recipe_visible()
    {
        var created = await CreateAsync("Olive oil cake");

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var (visible, source) = await DataLayer(scope).FindDuplicateSourceAsync(
            created.RecipeId, 9, TestContext.Current.CancellationToken);

        Assert.True(visible);
        Assert.Null(source);
    }

    /// <summary>
    /// The copy written through the create path: its own row, its own children, its own version 1, and a
    /// lineage edge the database accepts. Then the halves are proved independent — editing the source leaves
    /// the copy alone.
    /// </summary>
    [Fact]
    public async Task A_duplicate_is_written_as_an_independent_recipe_with_its_own_first_version()
    {
        var created = await CreateAsync("Olive oil cake");
        var token = TestContext.Current.CancellationToken;

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var (_, source) = await DataLayer(scope).FindDuplicateSourceAsync(created.RecipeId, null, token);

        var copy = RecipeSnapshotDuplicator.Duplicate(
            RecipeSnapshotSerializer.Deserialize(source!.Document), Guid.NewGuid());
        copy.Title = "Olive oil and rosemary cake";
        copy.Status = RecipeStatus.Draft;
        copy.DuplicatedFromVersionId = source.Id;
        copy.CreatedByMembershipId = Guid.NewGuid();
        copy.UpdatedByMembershipId = copy.CreatedByMembershipId;
        copy.CreatedAt = SqlServerRecipeFixture.Now;
        copy.UpdatedAt = SqlServerRecipeFixture.Now;

        var written = await DataLayer(scope).CreateAsync(
            copy,
            new RecipeVersionFacts(RecipeVersionSource.Duplicate, RecipeVersionReadiness.Draft, null),
            [],
            token);

        Assert.Equal(1, written.VersionNumber);

        await using var readScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var reread = await Repository(readScope).GetCompleteAsync(written.RecipeId, token);

        Assert.Equal("Olive oil and rosemary cake", reread!.Recipe.Title);
        Assert.Equal(RecipeStatus.Draft, reread.Recipe.Status);
        Assert.Equal(source.Id, reread.Recipe.DuplicatedFromVersionId);
        Assert.Equal(RecipeVersionSource.Duplicate, reread.CurrentVersion!.Source);
        Assert.Equal(2, reread.Recipe.IngredientGroups.Count);

        // Independent: an edit to the source moves the source on and leaves the copy exactly as it is.
        await using var editScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var editing = await LoadForUpdateAsync(editScope, created.RecipeId);
        editing.Recipe.Recipe.Title = "Source moved on";
        await DataLayer(editScope).UpdateAsync(editing, NextVersion, null, token);

        await using var afterScope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var after = await Repository(afterScope).GetCompleteAsync(written.RecipeId, token);

        Assert.Equal("Olive oil and rosemary cake", after!.Recipe.Title);
        Assert.Equal(1, after.CurrentVersion!.VersionNumber);
    }

    /// <summary>
    /// The database refuses lineage naming a version of another workspace's recipe, because the foreign key
    /// is composite. Containment structural rather than a check the code above has to remember.
    /// </summary>
    [Fact]
    public async Task A_duplicated_from_id_cannot_name_another_workspaces_version()
    {
        var theirs = await CreateInAsync(SqlServerRecipeFixture.WorkspaceB, "B's cake");
        var token = TestContext.Current.CancellationToken;

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var copy = SqlServerRecipeFixture.NewRecipe("Borrowed lineage", SqlServerRecipeFixture.TagIdA);
        copy.DuplicatedFromVersionId = theirs.VersionId;

        await Assert.ThrowsAsync<DbUpdateException>(() => DataLayer(scope).CreateAsync(
            copy, FirstVersion, [], token));
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

    // ---- Search ----

    /// <summary>
    /// The DataLayer's only decision here is whether to issue the second statement at all, and it reads that off
    /// the criteria rather than deciding it. A total nobody asked for is a query nobody reads.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_total_is_counted_only_when_the_criteria_asks_for_one(bool includeTotal)
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        // Unique per theory case as well as per test: both cases run against the same database, so a title shared
        // between them would have the second one counting the first one's recipe.
        var title = $"Countable cake {includeTotal}";

        var db = SqlServerRecipeFixture.Db(scope);
        db.Recipes.Add(SqlServerRecipeFixture.NewRecipe(title, SqlServerRecipeFixture.TagIdA));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Narrowed to this test's own recipes. Every test in this class shares one database, so an unfiltered
        // total would count whatever its neighbours had left behind.
        var (rows, hasMore, total) = await DataLayer(scope).SearchAsync(
            new RecipeSearchCriteria(
                new RecipeSearchFilters(Search: RecipeSearchPolicy.NormalizeSearch(title)),
                "scope",
                IncludeTotal: includeTotal),
            TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.False(hasMore);

        // Null and zero are different answers: one means nobody asked, the other means nothing matched.
        Assert.Equal(includeTotal ? 1 : null, total);
    }

    /// <summary>
    /// The count spans every page, not the one just read — so it must disagree with the row count whenever the
    /// page is smaller than the set.
    /// </summary>
    [Fact]
    public async Task The_total_counts_past_the_end_of_the_page()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        for (var index = 0; index < 3; index++)
        {
            db.Recipes.Add(SqlServerRecipeFixture.NewRecipe($"Pageable loaf {index}", SqlServerRecipeFixture.TagIdA));
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (rows, hasMore, total) = await DataLayer(scope).SearchAsync(
            new RecipeSearchCriteria(
                new RecipeSearchFilters(Search: RecipeSearchPolicy.NormalizeSearch("Pageable loaf")),
                "scope",
                RequestedLimit: 1),
            TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.True(hasMore);
        Assert.Equal(3, total);
    }

    // ---- Version history ----

    [Fact]
    public async Task A_history_read_returns_the_recipes_versions()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var recipe = SqlServerRecipeFixture.NewRecipe("Historic cake", SqlServerRecipeFixture.TagIdA);

        var created = await DataLayer(scope).CreateAsync(
            recipe, FirstVersion, [], TestContext.Current.CancellationToken);

        var page = await DataLayer(scope).ListVersionsAsync(
            new RecipeVersionHistoryCriteria(created.RecipeId, "scope"), TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        var row = Assert.Single(page!.Value.Rows);

        Assert.Equal(created.VersionId, row.Id);
        Assert.Equal(1, row.VersionNumber);
        Assert.Equal("Created.", row.Reason);
    }

    /// <summary>
    /// The existence probe, and the reason it is there. Every recipe has a version, so an empty page would say
    /// "no such recipe" as plainly as a 404 — this returns <c>null</c> instead, which is the distinction the
    /// read seam turns into one uniform answer.
    /// </summary>
    [Fact]
    public async Task A_history_read_for_an_unknown_recipe_is_null_rather_than_an_empty_page()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var page = await DataLayer(scope).ListVersionsAsync(
            new RecipeVersionHistoryCriteria(Guid.NewGuid(), "scope"), TestContext.Current.CancellationToken);

        Assert.Null(page);
    }

    /// <summary>
    /// The probe runs on every page, not only the first — which is what this pins, because a regression
    /// narrowing it to <c>Position is null</c> would pass every other test here.
    /// </summary>
    /// <remarks>
    /// A cursor proves where a previous page ended, not that the recipe is still visible. Over HTTP the
    /// workspace is re-resolved per request, so the exposure today is nil; the caller this guards against is
    /// the one that does not arrive over HTTP — a worker or an AI plugin that resolves a context once and then
    /// pages.
    /// </remarks>
    [Fact]
    public async Task A_resumed_page_is_probed_for_visibility_too()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var page = await DataLayer(scope).ListVersionsAsync(
            new RecipeVersionHistoryCriteria(
                Guid.NewGuid(),
                "scope",
                PositionAfterVersion(4)),
            TestContext.Current.CancellationToken);

        Assert.Null(page);
    }

    /// <summary>
    /// And the same answer for another workspace's recipe, named exactly by a caller who knows its id. This is
    /// the two-workspace case at the layer where the query filter is what decides it.
    /// </summary>
    [Fact]
    public async Task A_history_read_across_the_workspace_boundary_is_null_too()
    {
        await using var inB = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);
        var created = await DataLayer(inB).CreateAsync(
            SqlServerRecipeFixture.NewRecipe("B's cake", SqlServerRecipeFixture.TagIdB),
            FirstVersion,
            [],
            TestContext.Current.CancellationToken);

        await using var inA = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var page = await DataLayer(inA).ListVersionsAsync(
            new RecipeVersionHistoryCriteria(created.RecipeId, "scope"), TestContext.Current.CancellationToken);

        // Indistinguishable from an id that never existed — which is exactly the point.
        Assert.Null(page);
    }

    /// <summary>A position built the way the read seam builds one: through a real encoded cursor.</summary>
    private static RecipeVersionHistoryPosition PositionAfterVersion(int versionNumber)
    {
        var encoded = Domain.Managers.Paging.ReferenceCursor.Encode(
            versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("D"),
            "scope");

        Assert.True(Domain.Managers.Paging.ReferenceCursor.TryDecode(encoded, out var cursor));
        Assert.True(RecipeVersionHistoryPosition.TryCreate(cursor!, out var position));

        return position!;
    }

    private static IRecipeDataLayer DataLayer(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeDataLayer>();

    private static IRecipeRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeRepository>();
}
