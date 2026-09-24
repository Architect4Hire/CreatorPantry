using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Creation invariants and mapping, against a recording DataLayer. No database: what is under test is what
/// Business decides — the aggregate it hands down on a create, and the shape it publishes on a read.
/// </summary>
public sealed class RecipeBusinessTests
{
    private static readonly Guid ActorMembershipId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingRecipeDataLayer _dataLayer = new();
    private readonly IRecipeBusiness _business;

    public RecipeBusinessTests() =>
        _business = new ServiceCollection()
            .AddSingleton<IRecipeDataLayer>(_dataLayer)
            .AddSingleton<IWorkspaceContext>(new StubWorkspaceContext(ActorMembershipId))
            .AddSingleton<IClock>(new StubClock(Now))
            .AddSingleton<IRecipeBusiness, RecipeBusiness>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeBusiness>();

    private Task<Domain.Managers.Results.OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CreateRecipeViewModel input,
        MeasurementDimension? yieldUnitDimension = null) =>
        _business.CreateAsync(CanonicalCreateRecipe.From(input), yieldUnitDimension, TestContext.Current.CancellationToken);

    // ---- Valid ----

    [Fact]
    public async Task A_title_alone_creates_a_recipe()
    {
        var result = await CreateAsync(new CreateRecipeViewModel { Title = "Olive oil cake" });

        Assert.True(result.Succeeded);
        Assert.Equal("Olive oil cake", _dataLayer.Recipe!.Title);
        Assert.Equal(RecipeStatus.Draft, _dataLayer.Recipe.Status);
    }

    [Fact]
    public async Task The_owner_comes_from_the_authenticated_context()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        // Never from the request: the contract has no field for it, and this is where that becomes true.
        Assert.Equal(ActorMembershipId, _dataLayer.Recipe!.CreatedByMembershipId);
        Assert.Equal(ActorMembershipId, _dataLayer.Recipe.UpdatedByMembershipId);
    }

    [Fact]
    public async Task The_workspace_is_left_for_the_interceptor_to_stamp()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        // Business assigning WorkspaceId is the defect tenancy.md names. Empty here is correct.
        Assert.Equal(Guid.Empty, _dataLayer.Recipe!.WorkspaceId);
    }

    [Fact]
    public async Task Timestamps_come_from_one_read_of_the_clock()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        Assert.Equal(Now, _dataLayer.Recipe!.CreatedAt);
        Assert.Equal(Now, _dataLayer.Recipe.UpdatedAt);
    }

    [Fact]
    public async Task Version_one_is_a_creator_edit_with_no_reason()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        Assert.Equal(RecipeVersionSource.CreatorEdit, _dataLayer.Version!.Source);
        Assert.Null(_dataLayer.Version.Reason);
    }

    [Theory]
    [InlineData(SettableRecipeStatusViewModel.Draft, RecipeStatus.Draft, RecipeVersionReadiness.Draft)]
    [InlineData(SettableRecipeStatusViewModel.Ready, RecipeStatus.Ready, RecipeVersionReadiness.Ready)]
    [InlineData(SettableRecipeStatusViewModel.Archived, RecipeStatus.Archived, RecipeVersionReadiness.Draft)]
    public async Task Version_one_inherits_the_recipes_editorial_state(
        SettableRecipeStatusViewModel requested,
        RecipeStatus expectedStatus,
        RecipeVersionReadiness expectedReadiness)
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake", Status = requested });

        // Both halves, because the requested status and the state stored are now two types: the mapping has to
        // land on the right domain state, and the version has to inherit it. Archived is included even though
        // the create validator refuses it — Business is reached by more than one route, and only Ready makes a
        // version ready.
        Assert.Equal(expectedStatus, _dataLayer.Recipe!.Status);
        Assert.Equal(expectedReadiness, _dataLayer.Version!.Readiness);
    }

    // ---- Invalid time ----

    [Fact]
    public async Task A_total_time_below_the_longest_phase_is_rejected()
    {
        var result = await CreateAsync(new CreateRecipeViewModel
        {
            Title = "Cake",
            CookTimeMinutes = 60,
            TotalTimeMinutes = 45,
        });

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, result.Error!.Code);
        Assert.Contains("totalTimeMinutes", result.Error.FieldErrors.Keys);
        Assert.Null(_dataLayer.Recipe);
    }

    [Fact]
    public async Task A_total_time_below_the_sum_but_above_every_phase_is_accepted()
    {
        // The case the rule is deliberately built to allow: prep overlaps cooking and resting is unattended,
        // so 45 total for 30+30+30 of phases is a creator statement, not an error.
        var result = await CreateAsync(new CreateRecipeViewModel
        {
            Title = "Cake",
            PrepTimeMinutes = 30,
            CookTimeMinutes = 30,
            RestTimeMinutes = 30,
            TotalTimeMinutes = 45,
        });

        Assert.True(result.Succeeded);
        Assert.Equal(45, _dataLayer.Recipe!.TotalTimeMinutes);
    }

    [Fact]
    public async Task A_total_time_with_no_phases_is_accepted()
    {
        var result = await CreateAsync(new CreateRecipeViewModel { Title = "Cake", TotalTimeMinutes = 20 });

        Assert.True(result.Succeeded);
    }

    // ---- Invalid yield ----

    [Fact]
    public async Task A_yield_measured_in_degrees_is_rejected()
    {
        var result = await CreateAsync(
            new CreateRecipeViewModel { Title = "Cake", YieldQuantity = 12m, YieldUnitId = Guid.NewGuid() },
            MeasurementDimension.Temperature);

        Assert.False(result.Succeeded);
        Assert.Contains("yieldUnitId", result.Error!.FieldErrors.Keys);
        Assert.Null(_dataLayer.Recipe);
    }

    [Fact]
    public async Task A_yield_unit_dimension_is_derived_rather_than_accepted()
    {
        var unitId = Guid.NewGuid();

        await CreateAsync(
            new CreateRecipeViewModel { Title = "Cake", YieldQuantity = 12m, YieldUnitId = unitId },
            MeasurementDimension.Count);

        // The request has no field for the dimension at all; it arrives as a resolved fact from the Facade.
        Assert.Equal(unitId, _dataLayer.Recipe!.YieldUnitId);
        Assert.Equal(MeasurementDimension.Count, _dataLayer.Recipe.YieldUnitDimension);
    }

    [Fact]
    public async Task No_yield_unit_means_no_dimension_even_if_one_is_supplied()
    {
        await CreateAsync(
            new CreateRecipeViewModel { Title = "Cake", YieldText = "makes 12" },
            MeasurementDimension.Count);

        Assert.Null(_dataLayer.Recipe!.YieldUnitDimension);
    }

    // ---- Tags ----

    [Fact]
    public async Task Tags_are_handed_to_the_data_layer_as_canonicalized()
    {
        var input = new CreateRecipeViewModel { Title = "Cake", Tags = ["Weeknight", "Freezer friendly"] };

        await CreateAsync(input);

        // Business forwards what it was given rather than re-deriving it. What those names normalize to, and
        // in what order, is CanonicalCreateRecipe.s business and is tested there.
        Assert.Equal(CanonicalCreateRecipe.From(input).Tags, _dataLayer.Tags);
    }

    // ---- Instructions (create) ----

    [Fact]
    public async Task Instruction_groups_and_steps_are_built_fresh_with_new_ids_and_position_order()
    {
        var input = new CreateRecipeViewModel
        {
            Title = "Cake",
            Instructions =
            [
                new RecipeInstructionGroupInputViewModel
                {
                    Title = "Batter",
                    Steps =
                    [
                        new RecipeInstructionStepInputViewModel { Text = "Cream the butter and sugar." },
                        new RecipeInstructionStepInputViewModel { Text = "Beat in the eggs." },
                    ],
                },
            ],
        };

        await CreateAsync(input);

        var group = Assert.Single(_dataLayer.Recipe!.InstructionGroups);
        Assert.NotEqual(Guid.Empty, group.Id);
        Assert.Equal("Batter", group.Title);
        Assert.Equal(0, group.SortOrder);

        var steps = group.Steps.OrderBy(step => step.SortOrder).ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("Cream the butter and sugar.", steps[0].Text);
        Assert.Equal(0, steps[0].SortOrder);
        Assert.Equal("Beat in the eggs.", steps[1].Text);
        Assert.Equal(1, steps[1].SortOrder);
        Assert.NotEqual(steps[0].Id, steps[1].Id);
    }

    [Fact]
    public async Task A_steps_temperature_dimension_is_derived_as_temperature_never_trusted_from_the_request()
    {
        var unitId = Guid.NewGuid();
        var input = new CreateRecipeViewModel
        {
            Title = "Cake",
            Instructions =
            [
                new RecipeInstructionGroupInputViewModel
                {
                    Steps = [new RecipeInstructionStepInputViewModel { Text = "Bake.", TemperatureValue = 180m, TemperatureUnitId = unitId }],
                },
            ],
        };

        await CreateAsync(input);

        var step = _dataLayer.Recipe!.InstructionGroups.Single().Steps.Single();
        Assert.Equal(180m, step.TemperatureValue);
        Assert.Equal(unitId, step.TemperatureUnitId);
        Assert.Equal(MeasurementDimension.Temperature, step.TemperatureUnitDimension);
    }

    [Fact]
    public async Task No_instructions_is_a_recipe_with_no_method_yet()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        Assert.Empty(_dataLayer.Recipe!.InstructionGroups);
    }

    // ---- Nothing reaches the DataLayer when an invariant fails ----

    [Fact]
    public async Task A_rejected_request_never_reaches_the_data_layer()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake", CookTimeMinutes = 60, TotalTimeMinutes = 1 });

        Assert.Equal(0, _dataLayer.Calls);
    }

    // ---- The detail read ----

    [Fact]
    public async Task A_detail_read_publishes_the_creators_own_text_and_the_references_beside_it()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        _dataLayer.Detail = Tagged(recipe);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        Assert.Equal(recipe.Id, detail.Id);
        Assert.Equal(recipe.Title, detail.Title);
        Assert.Equal(recipe.Headnote, detail.Headnote);
        Assert.Equal(recipe.StorageNotes, detail.StorageNotes);
        Assert.Equal(recipe.AttributionText, detail.AttributionText);
        Assert.Equal(recipe.CuisineId, detail.CuisineId);
        Assert.Equal(recipe.YieldUnitId, detail.YieldUnitId);
        Assert.Equal(recipe.Status, detail.Status);
        Assert.Equal(recipe.CreatedAt, detail.CreatedAt);
        Assert.Equal(recipe.UpdatedAt, detail.UpdatedAt);

        var line = Assert.Single(Assert.Single(detail.IngredientGroups).Ingredients);

        // The entered wording, unmodified, alongside the additive reading of it — the order of precedence
        // recipes.md sets, made visible on the wire.
        Assert.Equal("2 cups (240 g) all-purpose flour, sifted", line.DisplayText);
        Assert.Equal("all-purpose flour", line.IngredientNameText);
        Assert.Equal(IngredientMatchStatus.Matched, line.MatchStatus);
        Assert.Equal(IngredientScaling.Fixed, line.ScalingBehavior);
    }

    [Fact]
    public async Task A_detail_read_withholds_ownership_and_authorship()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        _dataLayer.Detail = Tagged(recipe);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        // Asserted on the type rather than on a value: a property added later would be serialized, and no
        // value assertion here would notice.
        var published = detail.GetType().GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain(nameof(Recipe.WorkspaceId), published);
        Assert.DoesNotContain(nameof(Recipe.CreatedByMembershipId), published);
        Assert.DoesNotContain(nameof(Recipe.UpdatedByMembershipId), published);
        Assert.DoesNotContain(nameof(Recipe.RowVersion), published);
    }

    [Fact]
    public async Task The_concurrency_token_carries_the_row_version_opaquely()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        recipe.RowVersion = [1, 2, 3, 4, 5, 6, 7, 8];
        _dataLayer.Detail = Tagged(recipe);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        Assert.Equal(Convert.ToBase64String(recipe.RowVersion), detail.ConcurrencyToken);
    }

    [Fact]
    public async Task A_detail_read_names_the_tags_the_recipe_carries()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        _dataLayer.Detail = Tagged(recipe, tags: [Tag(RecipeAggregateFixture.TagIdA, "Weeknight")]);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        var tag = Assert.Single(detail.Tags);
        Assert.Equal(RecipeAggregateFixture.TagIdA, tag.WorkspaceTagId);
        Assert.Equal("Weeknight", tag.Name);
    }

    [Fact]
    public async Task A_vocabulary_row_with_no_matching_link_adds_no_tag()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        _dataLayer.Detail = Tagged(recipe, tags:
        [
            Tag(RecipeAggregateFixture.TagIdA, "Weeknight"),
            Tag(RecipeAggregateFixture.TagIdB, "Freezer"),
        ]);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        // The links are the truth about what the recipe carries; the vocabulary rows only supply names.
        Assert.Equal("Weeknight", Assert.Single(detail.Tags).Name);
    }

    [Fact]
    public async Task A_link_whose_vocabulary_row_is_missing_is_dropped_rather_than_named_blankly()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        _dataLayer.Detail = Tagged(recipe);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        // Only reachable through the race TaggedRecipe documents. One fewer chip is a better answer than a
        // nameless one, and far better than refusing the creator their own recipe.
        Assert.Empty(detail.Tags);
    }

    [Fact]
    public async Task Child_order_is_whatever_the_data_layer_returned()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        _dataLayer.Detail = Tagged(recipe);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        // The repository returns each collection sorted, and the fixture's sort orders are a non-zero 3 — so a
        // mapper that dropped SortOrder would be visible here, and one that re-sorted would be hiding a query
        // that had stopped doing so.
        Assert.Equal(3, Assert.Single(detail.IngredientGroups).SortOrder);
        Assert.Equal(3, Assert.Single(detail.InstructionGroups).SortOrder);
        Assert.Equal(3, Assert.Single(detail.Equipment).SortOrder);
        Assert.Equal(3, Assert.Single(detail.AssetLinks).SortOrder);
    }

    [Fact]
    public async Task The_current_version_is_reported_as_metadata_only()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = 4,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Ready,
            Reason = "Doubled the salt.",
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };
        _dataLayer.Detail = Tagged(recipe, version);

        var detail = (await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken)).Value!;

        var summary = detail.CurrentVersion!;
        Assert.Equal(version.Id, summary.Id);
        Assert.Equal(4, summary.VersionNumber);
        Assert.Equal(RecipeVersionReadiness.Ready, summary.Readiness);
        Assert.Equal("Doubled the salt.", summary.Reason);

        // Nothing of the archive, and nothing of the lineage: reading a recipe is not reading its history.
        var published = summary.GetType().GetProperties().Select(property => property.Name).ToList();
        Assert.DoesNotContain(nameof(RecipeVersion.Snapshot), published);
        Assert.DoesNotContain(nameof(RecipeVersion.ParentVersionId), published);
        Assert.DoesNotContain(nameof(RecipeVersion.BasedOnRecipeRowVersion), published);
    }

    [Fact]
    public async Task A_recipe_with_no_history_still_reads()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        _dataLayer.Detail = Tagged(recipe);

        var result = await _business.GetDetailAsync(recipe.Id, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value!.CurrentVersion);
    }

    [Fact]
    public async Task A_recipe_the_data_layer_cannot_see_is_not_found()
    {
        _dataLayer.Detail = null;

        var result = await _business.GetDetailAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);

        // No field errors: nothing about the request was malformed, and naming a field would imply otherwise.
        Assert.Empty(result.Error.FieldErrors);
    }

    // ---- The update seam ----

    /// <summary>Eight bytes, base64 — the token a read would have published for <see cref="Stored"/>.</summary>
    private const string Token = "AQIDBAUGBwg=";

    private static readonly Guid CurrentVersionId = Guid.NewGuid();

    /// <summary>A recipe as it already stands, with a concurrency token an edit can quote.</summary>
    private static Recipe Stored(Action<Recipe>? adjust = null)
    {
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Olive oil cake",
            Headnote = "The one my grandmother made.",
            Status = RecipeStatus.Draft,
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
            RowVersion = [1, 2, 3, 4, 5, 6, 7, 8],
        };

        adjust?.Invoke(recipe);

        return recipe;
    }

    private static RecipeVersion CurrentVersion(Recipe recipe) => new()
    {
        Id = CurrentVersionId,
        RecipeId = recipe.Id,
        VersionNumber = 3,
        Source = RecipeVersionSource.CreatorEdit,
        Readiness = RecipeVersionReadiness.Draft,
        CreatedAt = recipe.UpdatedAt,
        CreatedByMembershipId = recipe.UpdatedByMembershipId,
    };

    private static PatchField<T> Set<T>(T value) => PatchField<T>.Submitted(value);

    /// <summary>Loads <paramref name="recipe"/> as the recipe under edit and applies <paramref name="edit"/>.</summary>
    private Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
        Recipe recipe,
        UpdateRecipeViewModel edit,
        MeasurementDimension? submittedYieldUnitDimension = null,
        params WorkspaceTag[] tags)
    {
        _dataLayer.Detail = Tagged(recipe, CurrentVersion(recipe), tags);

        return _business.UpdateAsync(
            recipe.Id,
            CanonicalRecipePatch.From(edit),
            submittedYieldUnitDimension,
            TestContext.Current.CancellationToken);
    }

    private static UpdateRecipeViewModel Edit() => new() { ExpectedConcurrencyToken = Token };

    [Fact]
    public async Task Only_submitted_fields_are_changed()
    {
        var recipe = Stored();

        var result = await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon olive oil cake") });

        Assert.True(result.Succeeded);
        Assert.Equal("Lemon olive oil cake", recipe.Title);

        // The field nobody mentioned. A client that has never heard of headnotes must not be able to blank
        // one by omission, which is the failure mode this whole contract exists to prevent.
        Assert.Equal("The one my grandmother made.", recipe.Headnote);
    }

    [Fact]
    public async Task A_field_submitted_as_null_is_cleared()
    {
        var recipe = Stored();

        await UpdateAsync(recipe, Edit() with { Headnote = Set<string?>(null) });

        Assert.Null(recipe.Headnote);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_nothing()
    {
        var recipe = Stored();

        var result = await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Olive oil cake") });

        Assert.True(result.Succeeded);

        // Not an optimisation. A version recording no change is noise in a history the creator reads, and
        // stamping UpdatedAt for it would invalidate every token their collaborators are holding.
        Assert.Equal(0, _dataLayer.Calls);
        Assert.Equal(Now.AddDays(-1), recipe.UpdatedAt);
        Assert.Equal(3, result.Value!.CurrentVersion!.VersionNumber);
    }

    [Fact]
    public async Task A_reason_on_its_own_is_still_nothing()
    {
        var recipe = Stored();

        await UpdateAsync(recipe, Edit() with { Reason = "Because I said so." });

        // A reason explains a change; it is not one, and a version whose only content is an explanation of
        // nothing would be worse than no version at all.
        Assert.Equal(0, _dataLayer.Calls);
    }

    [Fact]
    public async Task A_stale_token_is_a_conflict_and_nothing_is_written()
    {
        var recipe = Stored();

        var result = await UpdateAsync(
            recipe,
            new UpdateRecipeViewModel { ExpectedConcurrencyToken = "CAcGBQQDAgE=", Title = Set<string?>("Something else") });

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
        Assert.Equal(0, _dataLayer.Calls);

        // Refused before anything was merged, so the recipe is exactly as it was read.
        Assert.Equal("Olive oil cake", recipe.Title);
    }

    [Fact]
    public async Task A_save_refused_by_the_database_is_the_same_conflict()
    {
        _dataLayer.Conflict = true;
        var recipe = Stored();

        var result = await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon cake") });

        // Someone saved between this read and this write. The caller cannot tell it from the stale-token
        // case, and should not have to: the remedy is the same.
        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
    }

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        _dataLayer.Detail = null;

        var result = await _business.UpdateAsync(
            Guid.NewGuid(), CanonicalRecipePatch.From(Edit()), null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
    }

    [Fact]
    public async Task The_edit_is_stamped_with_the_clock_and_the_authenticated_member()
    {
        var recipe = Stored();

        await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon cake") });

        Assert.Equal(Now, recipe.UpdatedAt);
        Assert.Equal(ActorMembershipId, recipe.UpdatedByMembershipId);

        // Authorship is not rewritten by an edit: who wrote the recipe and who last changed it are two facts.
        Assert.NotEqual(ActorMembershipId, recipe.CreatedByMembershipId);
    }

    [Fact]
    public async Task An_edit_records_a_creator_edit_carrying_the_creators_reason()
    {
        var recipe = Stored();

        await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon cake"), Reason = "  Brighter.  " });

        Assert.Equal(RecipeVersionSource.CreatorEdit, _dataLayer.Version!.Source);
        Assert.Equal("Brighter.", _dataLayer.Version.Reason);
    }

    [Theory]
    [InlineData(SettableRecipeStatusViewModel.Ready, RecipeVersionReadiness.Ready)]
    [InlineData(SettableRecipeStatusViewModel.Draft, RecipeVersionReadiness.Draft)]
    [InlineData(SettableRecipeStatusViewModel.Archived, RecipeVersionReadiness.Draft)]
    public async Task The_version_inherits_the_editorial_state_the_edit_leaves_behind(
        SettableRecipeStatusViewModel status, RecipeVersionReadiness expected)
    {
        var recipe = Stored();

        // A title change rides along so that every case is a real edit — submitting the status a recipe is
        // already in is a no-op, which is a different test.
        await UpdateAsync(
            recipe,
            Edit() with { Title = Set<string?>("Lemon cake"), Status = Set<SettableRecipeStatusViewModel?>(status) });

        Assert.Equal(expected, _dataLayer.Version!.Readiness);
    }

    [Fact]
    public async Task A_status_submitted_as_null_is_refused_rather_than_read_as_draft()
    {
        // Ready rather than Archived, which this test used to seed: an archived recipe now refuses every
        // edit before the body is examined, which would mask the thing under test. Ready shows it just as
        // well — SettableRecipeStatus.ToDomain(null) is Draft, so a submitted null read as a value rather
        // than as a mistake would quietly un-ready the recipe.
        var recipe = Stored(stored => stored.Status = RecipeStatus.Ready);

        var result = await UpdateAsync(recipe, Edit() with { Status = Set<SettableRecipeStatusViewModel?>(null) });

        // The validator refuses this too, so nothing reaches here over HTTP. The guard is for the callers no
        // MVC pipeline protects — a worker, an AI plugin, a future facade overload — where the alternative is
        // a recipe quietly losing its editorial state to an ordinary-looking creator edit.
        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, result.Error!.Code);
        Assert.Contains("status", result.Error.FieldErrors.Keys);
        Assert.Equal(RecipeStatus.Ready, recipe.Status);
        Assert.Equal(0, _dataLayer.Calls);
    }

    [Fact]
    public async Task An_unsubmitted_status_leaves_an_archived_recipe_archived()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon cake") });

        Assert.Equal(RecipeStatus.Archived, recipe.Status);
    }

    // ---- Invariants, judged against the merged recipe ----

    [Fact]
    public async Task A_total_time_is_judged_against_times_the_edit_never_mentioned()
    {
        var recipe = Stored(stored => stored.CookTimeMinutes = 90);

        var result = await UpdateAsync(recipe, Edit() with { TotalTimeMinutes = Set<int?>(30) });

        // The request alone looks fine. It is only wrong because of a cook time it never named, which is why
        // this check cannot live at the edge.
        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, result.Error!.Code);
        Assert.Contains("totalTimeMinutes", result.Error.FieldErrors.Keys);
        Assert.Equal(0, _dataLayer.Calls);
    }

    [Fact]
    public async Task Shortening_a_step_can_make_a_previously_impossible_total_valid()
    {
        var recipe = Stored(stored =>
        {
            stored.CookTimeMinutes = 90;
            stored.TotalTimeMinutes = 90;
        });

        var result = await UpdateAsync(recipe, Edit() with { CookTimeMinutes = Set<int?>(20) });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Clearing_a_yield_quantity_under_a_unit_the_edit_never_mentioned_is_refused()
    {
        var recipe = Stored(stored =>
        {
            stored.YieldQuantity = 12m;
            stored.YieldUnitId = Guid.NewGuid();
            stored.YieldUnitDimension = MeasurementDimension.Count;
        });

        var result = await UpdateAsync(recipe, Edit() with { YieldQuantity = Set<decimal?>(null) });

        // Mirrors CK_Recipes_YieldUnit_RequiresQuantity, which would otherwise surface as a 500.
        Assert.False(result.Succeeded);
        Assert.Contains("yieldQuantity", result.Error!.FieldErrors.Keys);
    }

    [Fact]
    public async Task Clearing_the_unit_and_the_quantity_together_is_allowed()
    {
        var recipe = Stored(stored =>
        {
            stored.YieldQuantity = 12m;
            stored.YieldUnitId = Guid.NewGuid();
            stored.YieldUnitDimension = MeasurementDimension.Count;
        });

        var result = await UpdateAsync(
            recipe,
            Edit() with { YieldQuantity = Set<decimal?>(null), YieldUnitId = Set<Guid?>(null) });

        Assert.True(result.Succeeded);
        Assert.Null(recipe.YieldUnitId);

        // The dimension goes with the unit. Leaving it behind would pin a composite foreign key to a unit
        // that is no longer named.
        Assert.Null(recipe.YieldUnitDimension);
    }

    [Fact]
    public async Task A_yield_cannot_be_moved_to_a_temperature_unit()
    {
        var recipe = Stored(stored => stored.YieldQuantity = 12m);

        var result = await UpdateAsync(
            recipe,
            Edit() with { YieldUnitId = Set<Guid?>(Guid.NewGuid()) },
            MeasurementDimension.Temperature);

        Assert.False(result.Succeeded);
        Assert.Contains("yieldUnitId", result.Error!.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_submitted_unit_carries_its_dimension_onto_the_recipe()
    {
        var unitId = Guid.NewGuid();
        var recipe = Stored(stored => stored.YieldQuantity = 12m);

        await UpdateAsync(recipe, Edit() with { YieldUnitId = Set<Guid?>(unitId) }, MeasurementDimension.Count);

        Assert.Equal(unitId, recipe.YieldUnitId);
        Assert.Equal(MeasurementDimension.Count, recipe.YieldUnitDimension);
    }

    // ---- Tags ----

    [Fact]
    public async Task Submitting_the_tags_a_recipe_already_has_is_not_a_change()
    {
        var recipe = Stored();
        recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = TagId });

        await UpdateAsync(
            recipe,
            // Differently capitalised, deliberately: tag identity is the normalized name, so this is the
            // same tag and not a reason to rewrite the recipe's links.
            Edit() with { Tags = Set<IReadOnlyList<string?>?>(["Weeknight"]) },
            tags: Tag(TagId, "weeknight"));

        Assert.Equal(0, _dataLayer.Calls);
    }

    [Fact]
    public async Task A_different_set_of_tags_is_handed_down_whole()
    {
        var recipe = Stored();
        recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = TagId });

        await UpdateAsync(
            recipe,
            Edit() with { Tags = Set<IReadOnlyList<string?>?>(["citrus"]) },
            tags: Tag(TagId, "weeknight"));

        // The complete set the recipe should end up with, not a delta: the DataLayer removes what the set
        // does not name.
        var tag = Assert.Single(_dataLayer.Tags!);
        Assert.Equal("citrus", tag.NormalizedName);
    }

    [Fact]
    public async Task Tags_are_left_alone_when_the_edit_does_not_mention_them()
    {
        var recipe = Stored();

        await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon cake") });

        Assert.Null(_dataLayer.Tags);
    }

    // ---- Instructions (update) ----

    [Fact]
    public async Task A_new_group_with_no_id_is_added()
    {
        var recipe = Stored();

        await UpdateAsync(
            recipe,
            Edit() with { Instructions = Instructions(Group(id: null, title: "Batter", Step(id: null, "Cream butter and sugar."))) });

        var group = Assert.Single(recipe.InstructionGroups);
        Assert.NotEqual(Guid.Empty, group.Id);
        Assert.Equal("Batter", group.Title);
        Assert.Equal("Cream butter and sugar.", group.Steps.Single().Text);
    }

    [Fact]
    public async Task An_existing_group_and_step_are_updated_in_place_by_id()
    {
        var groupId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var recipe = Stored(stored => stored.InstructionGroups.Add(
            ExistingGroup(groupId, "Batter", ExistingStep(stepId, "Cream butter."))));

        await UpdateAsync(
            recipe,
            Edit() with
            {
                Instructions = Instructions(Group(groupId, "Cake batter", Step(stepId, "Cream the butter well."))),
            });

        var group = Assert.Single(recipe.InstructionGroups);
        Assert.Equal(groupId, group.Id); // same row, not replaced
        Assert.Equal("Cake batter", group.Title);
        var step = Assert.Single(group.Steps);
        Assert.Equal(stepId, step.Id);
        Assert.Equal("Cream the butter well.", step.Text);
    }

    [Fact]
    public async Task A_group_not_named_by_the_submission_is_removed()
    {
        var keepId = Guid.NewGuid();
        var removeId = Guid.NewGuid();
        var recipe = Stored(stored =>
        {
            stored.InstructionGroups.Add(ExistingGroup(keepId, "Batter", ExistingStep(Guid.NewGuid(), "Mix.")));
            stored.InstructionGroups.Add(ExistingGroup(removeId, "Frosting", ExistingStep(Guid.NewGuid(), "Whip.")));
        });

        await UpdateAsync(recipe, Edit() with { Instructions = Instructions(Group(keepId, "Batter", Step(null, "Mix."))) });

        var group = Assert.Single(recipe.InstructionGroups);
        Assert.Equal(keepId, group.Id);
    }

    [Fact]
    public async Task A_step_not_named_by_the_submission_is_removed_from_its_group()
    {
        var groupId = Guid.NewGuid();
        var keepStepId = Guid.NewGuid();
        var recipe = Stored(stored => stored.InstructionGroups.Add(ExistingGroup(
            groupId, "Batter", ExistingStep(keepStepId, "Mix."), ExistingStep(Guid.NewGuid(), "Pour."))));

        await UpdateAsync(recipe, Edit() with { Instructions = Instructions(Group(groupId, "Batter", Step(keepStepId, "Mix."))) });

        var step = Assert.Single(recipe.InstructionGroups.Single().Steps);
        Assert.Equal(keepStepId, step.Id);
    }

    [Fact]
    public async Task Reordering_steps_updates_their_sort_order()
    {
        var groupId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var recipe = Stored(stored => stored.InstructionGroups.Add(
            ExistingGroup(groupId, null, ExistingStep(firstId, "Mix.", sortOrder: 0), ExistingStep(secondId, "Pour.", sortOrder: 1))));

        // The same two steps, listed in the opposite order.
        await UpdateAsync(
            recipe,
            Edit() with { Instructions = Instructions(Group(groupId, null, Step(secondId, "Pour."), Step(firstId, "Mix."))) });

        var group = recipe.InstructionGroups.Single();
        Assert.Equal(0, group.Steps.Single(step => step.Id == secondId).SortOrder);
        Assert.Equal(1, group.Steps.Single(step => step.Id == firstId).SortOrder);
    }

    [Fact]
    public async Task Resubmitting_the_exact_same_instructions_writes_nothing()
    {
        var groupId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var recipe = Stored(stored => stored.InstructionGroups.Add(
            ExistingGroup(groupId, "Batter", ExistingStep(stepId, "Mix."))));

        var result = await UpdateAsync(
            recipe,
            Edit() with { Instructions = Instructions(Group(groupId, "Batter", Step(stepId, "Mix."))) });

        Assert.True(result.Succeeded);

        // Not an optimisation, for the same reason a no-op scalar edit writes nothing: a version recording
        // no change is noise in a history a creator reads, and would invalidate collaborators' tokens for
        // nothing.
        Assert.Equal(0, _dataLayer.Calls);
    }

    [Fact]
    public async Task An_id_naming_no_step_of_this_recipe_is_treated_as_a_new_step_not_an_error()
    {
        var groupId = Guid.NewGuid();
        var recipe = Stored(stored => stored.InstructionGroups.Add(ExistingGroup(groupId, "Batter")));

        // A foreign or bogus id, indistinguishable to this recipe. Refusing it would first have to decide
        // which of those it is, and answering that at all is the disclosure tenancy.md forbids.
        var result = await UpdateAsync(
            recipe,
            Edit() with { Instructions = Instructions(Group(groupId, "Batter", Step(Guid.NewGuid(), "Mix."))) });

        Assert.True(result.Succeeded);
        var step = Assert.Single(recipe.InstructionGroups.Single().Steps);
        Assert.Equal("Mix.", step.Text);
    }

    [Fact]
    public async Task Instructions_left_unsubmitted_are_left_exactly_as_they_are()
    {
        var groupId = Guid.NewGuid();
        var recipe = Stored(stored => stored.InstructionGroups.Add(ExistingGroup(groupId, "Batter", ExistingStep(Guid.NewGuid(), "Mix."))));

        await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon cake") });

        Assert.Single(recipe.InstructionGroups);
    }

    [Fact]
    public async Task Submitting_an_empty_list_clears_every_group()
    {
        var recipe = Stored(stored => stored.InstructionGroups.Add(ExistingGroup(Guid.NewGuid(), "Batter", ExistingStep(Guid.NewGuid(), "Mix."))));

        await UpdateAsync(recipe, Edit() with { Instructions = Instructions() });

        Assert.Empty(recipe.InstructionGroups);
    }

    [Fact]
    public async Task A_steps_temperature_dimension_is_derived_on_update_too()
    {
        var groupId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var recipe = Stored(stored => stored.InstructionGroups.Add(ExistingGroup(groupId, "Bake", ExistingStep(stepId, "Bake."))));

        await UpdateAsync(
            recipe,
            Edit() with
            {
                Instructions = Instructions(Group(
                    groupId, "Bake", Step(stepId, "Bake.", temperatureValue: 180m, temperatureUnitId: unitId))),
            });

        var step = recipe.InstructionGroups.Single().Steps.Single();
        Assert.Equal(MeasurementDimension.Temperature, step.TemperatureUnitDimension);
    }

    private static RecipeInstructionGroup ExistingGroup(Guid id, string? title, params RecipeInstructionStep[] steps)
    {
        var group = new RecipeInstructionGroup { Id = id, Title = title, SortOrder = 0 };
        foreach (var step in steps)
        {
            group.Steps.Add(step);
        }

        return group;
    }

    private static RecipeInstructionStep ExistingStep(Guid id, string text, int sortOrder = 0) =>
        new() { Id = id, Text = text, SortOrder = sortOrder };

    private static PatchField<IReadOnlyList<RecipeInstructionGroupInputViewModel?>?> Instructions(
        params RecipeInstructionGroupInputViewModel[] groups) =>
        PatchField<IReadOnlyList<RecipeInstructionGroupInputViewModel?>?>.Submitted(groups);

    private static RecipeInstructionGroupInputViewModel Group(Guid? id, string? title, params RecipeInstructionStepInputViewModel[] steps) =>
        new() { Id = id, Title = title, Steps = steps };

    private static RecipeInstructionStepInputViewModel Step(
        Guid? id, string text, decimal? temperatureValue = null, Guid? temperatureUnitId = null) =>
        new() { Id = id, Text = text, TemperatureValue = temperatureValue, TemperatureUnitId = temperatureUnitId };

    private static readonly Guid TagId = Guid.NewGuid();

    private static TaggedRecipe Tagged(Recipe recipe, RecipeVersion? version = null, params WorkspaceTag[] tags) =>
        new(new CompleteRecipe(recipe, version), tags);

    // ---- The restore seam ----

    private static readonly Guid RestoredVersionId = Guid.NewGuid();

    /// <summary>
    /// Loads <paramref name="recipe"/> as the recipe under restore, with <paramref name="archived"/> as the
    /// version being restored, and performs the restore.
    /// </summary>
    /// <param name="archived">
    /// The content of the version named, or <c>null</c> to make this recipe have no such version.
    /// </param>
    private Task<OperationResult<RecipeDetailServiceModel>> RestoreAsync(
        Recipe recipe,
        Recipe? archived,
        string? token = Token,
        string? reason = null,
        int versionNumber = 2,
        params WorkspaceTag[] tags)
    {
        _dataLayer.Detail = Tagged(recipe, CurrentVersion(recipe), tags);
        _dataLayer.RestoreSource = archived is null
            ? null
            : new RecipeVersionSnapshotRecord(
                RestoredVersionId,
                versionNumber,
                RecipeVersionSource.CreatorEdit,
                RecipeVersionReadiness.Ready,
                Now.AddDays(-2),
                RecipeSnapshotSerializer.Serialize(
                    RecipeSnapshotMapper.Capture(new CompleteRecipe(archived, null))));

        return _business.RestoreVersionAsync(
            recipe.Id,
            versionNumber,
            CanonicalRestoreRecipeVersion.From(new RestoreRecipeVersionViewModel
            {
                ExpectedConcurrencyToken = token,
                Reason = reason,
            }),
            TestContext.Current.CancellationToken);
    }

    /// <summary>The archived state: the stored recipe as it was, under a different title.</summary>
    private static Recipe Archived(Recipe stored, Action<Recipe>? adjust = null)
    {
        var archived = Stored();
        archived.Id = stored.Id;
        archived.Title = "Olive oil and rosemary cake";
        adjust?.Invoke(archived);

        return archived;
    }

    [Fact]
    public async Task A_restore_puts_the_archived_content_back()
    {
        var recipe = Stored();

        var result = await RestoreAsync(recipe, Archived(recipe));

        Assert.True(result.Succeeded);
        Assert.Equal("Olive oil and rosemary cake", recipe.Title);
        Assert.Equal("Olive oil and rosemary cake", result.Value!.Title);
    }

    /// <summary>
    /// Where the content came from is Business's decision and travels in the facts it hands down; what the
    /// restore <em>replaced</em> is mechanical and stays the DataLayer's, derived from the aggregate it
    /// already holds — so the parent edge is asserted in <c>RecipeDataLayerTests</c>, against a real save.
    /// </summary>
    [Fact]
    public async Task A_restored_version_records_the_version_its_content_came_from()
    {
        var recipe = Stored();

        var result = await RestoreAsync(recipe, Archived(recipe));

        Assert.Equal(RecipeVersionSource.Restore, _dataLayer.Version!.Source);
        Assert.Equal(RestoredVersionId, _dataLayer.Version.RestoredFromVersionId);

        // One past the version that was current, and published as the recipe's new current version.
        Assert.Equal(4, result.Value!.CurrentVersion!.VersionNumber);
    }

    /// <summary>The actor is whoever restored, never the author of the version whose content came back.</summary>
    [Fact]
    public async Task A_restore_is_attributed_to_whoever_performed_it()
    {
        var recipe = Stored();

        await RestoreAsync(recipe, Archived(recipe));

        Assert.Equal(ActorMembershipId, recipe.UpdatedByMembershipId);
        Assert.Equal(Now, recipe.UpdatedAt);
    }

    [Fact]
    public async Task A_reason_is_recorded_on_the_version_the_restore_writes()
    {
        var recipe = Stored();

        await RestoreAsync(recipe, Archived(recipe), reason: "  Tuesday's edit broke the bake time.  ");

        Assert.Equal("Tuesday's edit broke the bake time.", _dataLayer.Version!.Reason);
    }

    /// <summary>
    /// Readiness follows the status the restore just put back, by the same rule an edit follows. Restoring a
    /// version that was Ready produces a ready version, because the recipe now says what that version said.
    /// </summary>
    [Theory]
    [InlineData(RecipeStatus.Ready, RecipeVersionReadiness.Ready)]
    [InlineData(RecipeStatus.Draft, RecipeVersionReadiness.Draft)]
    public async Task Readiness_follows_the_restored_status(RecipeStatus status, RecipeVersionReadiness readiness)
    {
        var recipe = Stored();

        await RestoreAsync(recipe, Archived(recipe, archived => archived.Status = status));

        Assert.Equal(status, recipe.Status);
        Assert.Equal(readiness, _dataLayer.Version!.Readiness);
    }

    /// <summary>
    /// A restore is a content write, so an archived recipe refuses it — REC-006's freeze covers every write,
    /// not only the obvious one. This is also what keeps the lifecycle honest: a version restore cannot be
    /// used to un-archive a recipe behind the audited command's back.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_refuses_a_version_restore()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        var result = await RestoreAsync(recipe, Archived(recipe, archived => archived.Status = RecipeStatus.Draft));

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, result.Error!.Code);
        Assert.Equal(RecipeStatus.Archived, recipe.Status);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// The second defence against "replays cannot create extra versions", and the one that works without an
    /// idempotency key: a restore onto content the recipe already has writes nothing at all.
    /// </summary>
    [Fact]
    public async Task A_restore_that_changes_nothing_writes_nothing()
    {
        var recipe = Stored();

        var result = await RestoreAsync(recipe, Archived(recipe, archived => archived.Title = recipe.Title));

        Assert.True(result.Succeeded);
        Assert.Equal(0, _dataLayer.Calls);
        Assert.Equal(Now.AddDays(-1), recipe.UpdatedAt);
        Assert.Equal(3, result.Value!.CurrentVersion!.VersionNumber);
    }

    [Fact]
    public async Task A_recipe_that_is_not_visible_is_not_found()
    {
        var result = await _business.RestoreVersionAsync(
            Guid.NewGuid(),
            2,
            CanonicalRestoreRecipeVersion.From(new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = Token }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
    }

    /// <summary>
    /// Its own code, and it names the route segment at fault. Distinct from the recipe's not-found and safe to
    /// be: by the time this can be returned the caller has already been shown that the recipe exists.
    /// </summary>
    [Fact]
    public async Task A_version_the_recipe_does_not_have_is_refused_naming_the_route_segment()
    {
        var recipe = Stored();

        var result = await RestoreAsync(recipe, archived: null, versionNumber: 9);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Equal(["This recipe has no version 9."], result.Error.FieldErrors["versionNumber"]);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// Answered before the token is checked, deliberately: version 9 will not appear on a second look, so
    /// telling this caller to reload would send them round a loop that cannot end.
    /// </summary>
    [Fact]
    public async Task An_unknown_version_is_reported_even_when_the_token_is_also_stale()
    {
        var recipe = Stored();

        var result = await RestoreAsync(recipe, archived: null, token: "CAcGBQQDAgE=", versionNumber: 9);

        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
    }

    /// <summary>
    /// A stale token is a recoverable conflict, and nothing is reconciled before it is checked — so a refused
    /// restore is answered from the state it was composed against rather than from a half-applied one.
    /// </summary>
    [Fact]
    public async Task A_stale_token_is_a_conflict_and_changes_nothing()
    {
        var recipe = Stored();

        var result = await RestoreAsync(recipe, Archived(recipe), token: "CAcGBQQDAgE=");

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
        Assert.Equal(0, _dataLayer.Calls);
        Assert.Equal("Olive oil cake", recipe.Title);
    }

    /// <summary>The race the token check above cannot see: the row moved between the read and the save.</summary>
    [Fact]
    public async Task A_recipe_that_moves_on_during_the_save_is_a_conflict()
    {
        var recipe = Stored();
        _dataLayer.Conflict = true;

        var result = await RestoreAsync(recipe, Archived(recipe));

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
    }

    /// <summary>
    /// The tag ids come from the archive, and the vocabulary is read for exactly those — which is what lets a
    /// tag whose row is gone be dropped instead of sent to a <c>Restrict</c> foreign key to fail.
    /// </summary>
    [Fact]
    public async Task The_archived_tag_ids_drive_the_vocabulary_read()
    {
        var recipe = Stored();
        var archived = Archived(recipe);
        archived.Tags.Add(new RecipeTag { RecipeId = archived.Id, WorkspaceTagId = TagId });
        _dataLayer.Vocabulary = [Tag(TagId, "Weeknight")];

        var result = await RestoreAsync(recipe, archived);

        Assert.Equal([TagId], _dataLayer.RequestedTagIds);
        Assert.Equal([TagId], recipe.Tags.Select(link => link.WorkspaceTagId));
        Assert.Equal(["Weeknight"], result.Value!.Tags.Select(tag => tag.Name));
    }

    /// <summary>A document naming no tags asks the vocabulary nothing.</summary>
    [Fact]
    public async Task A_document_with_no_tags_reads_no_vocabulary()
    {
        var recipe = Stored();

        await RestoreAsync(recipe, Archived(recipe));

        Assert.Null(_dataLayer.RequestedTagIds);
    }

    /// <summary>
    /// A document written by a newer build than the one serving the request is a server fault, and a 500 is
    /// the truthful status — giving it an error code would invite a client to handle something it cannot.
    /// </summary>
    [Fact]
    public async Task A_document_this_build_cannot_read_raises()
    {
        var recipe = Stored();
        _dataLayer.Detail = Tagged(recipe, CurrentVersion(recipe));
        _dataLayer.RestoreSource = new RecipeVersionSnapshotRecord(
            RestoredVersionId,
            2,
            RecipeVersionSource.CreatorEdit,
            RecipeVersionReadiness.Draft,
            Now.AddDays(-2),
            RecipeSnapshotSerializer.Serialize(
                RecipeSnapshotMapper.Capture(new CompleteRecipe(Archived(recipe), null))
                    with { SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion + 1 }));

        await Assert.ThrowsAsync<NotSupportedException>(() => _business.RestoreVersionAsync(
            recipe.Id,
            2,
            CanonicalRestoreRecipeVersion.From(new RestoreRecipeVersionViewModel { ExpectedConcurrencyToken = Token }),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, _dataLayer.Calls);
    }

    // ---- The archive seam ----

    private const string ActorUserId = "user-1";

    private Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(Recipe recipe, string? token = Token)
    {
        _dataLayer.Detail = Tagged(recipe, CurrentVersion(recipe));

        return _business.ArchiveAsync(recipe.Id, ActorUserId, token, TestContext.Current.CancellationToken);
    }

    private Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(Recipe recipe, string? token = Token)
    {
        _dataLayer.Detail = Tagged(recipe, CurrentVersion(recipe));

        return _business.UnarchiveAsync(recipe.Id, ActorUserId, token, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Archiving_moves_the_recipe_and_records_the_move()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Ready);

        var result = await ArchiveAsync(recipe);

        Assert.True(result.Succeeded);
        Assert.Equal(RecipeStatus.Archived, recipe.Status);
        Assert.Equal(RecipeStatus.Archived, result.Value!.Status);

        var audit = _dataLayer.Audit!;
        Assert.Equal(RecipeAuditActions.Archived, audit.Action);
        Assert.Equal(RecipeAuditActions.ResourceType, audit.ResourceType);
        Assert.Equal(recipe.Id.ToString("D"), audit.ResourceId);
        Assert.Equal(ActorUserId, audit.ActorUserId);

        // State names, not content: the audit log requires its references to stay safe to display.
        Assert.Equal("Ready", audit.BeforeReference);
        Assert.Equal("Archived", audit.AfterReference);
        Assert.NotEqual(Guid.Empty, audit.CorrelationId);
    }

    /// <summary>The audit row's actor is the authenticated caller, and the recipe's is their membership.</summary>
    [Fact]
    public async Task Archiving_is_attributed_to_whoever_did_it()
    {
        var recipe = Stored();

        await ArchiveAsync(recipe);

        Assert.Equal(ActorMembershipId, recipe.UpdatedByMembershipId);
        Assert.Equal(Now, recipe.UpdatedAt);
        Assert.Equal(ActorUserId, _dataLayer.Audit!.ActorUserId);
    }

    [Fact]
    public async Task Unarchiving_brings_the_recipe_back_as_a_draft()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        var result = await UnarchiveAsync(recipe);

        Assert.True(result.Succeeded);
        Assert.Equal(RecipePolicy.UnarchivedStatus, recipe.Status);
        Assert.Equal(RecipeStatus.Draft, recipe.Status);
        Assert.Equal(RecipeAuditActions.Unarchived, _dataLayer.Audit!.Action);
        Assert.Equal("Archived", _dataLayer.Audit.BeforeReference);
        Assert.Equal("Draft", _dataLayer.Audit.AfterReference);
    }

    /// <summary>
    /// Not the state it held before, because nothing records what that was. A recipe coming off the shelf is
    /// being picked back up, and calling it Ready again is the creator's to do.
    /// </summary>
    [Fact]
    public async Task Unarchiving_does_not_restore_a_previous_ready_state()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        await UnarchiveAsync(recipe);

        Assert.NotEqual(RecipeStatus.Ready, recipe.Status);
    }

    /// <summary>
    /// A repeated command is a success that does nothing: no write, no audit entry claiming a transition
    /// that did not happen, and no stamped timestamp invalidating collaborators' tokens.
    /// </summary>
    [Fact]
    public async Task Archiving_an_archived_recipe_changes_nothing()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        var result = await ArchiveAsync(recipe);

        Assert.True(result.Succeeded);
        Assert.Equal(RecipeStatus.Archived, result.Value!.Status);
        Assert.Equal(0, _dataLayer.Calls);
        Assert.Null(_dataLayer.Audit);
        Assert.Equal(Now.AddDays(-1), recipe.UpdatedAt);
    }

    [Fact]
    public async Task Unarchiving_a_recipe_that_is_not_archived_changes_nothing()
    {
        var recipe = Stored();

        var result = await UnarchiveAsync(recipe);

        Assert.True(result.Succeeded);
        Assert.Equal(RecipeStatus.Draft, result.Value!.Status);
        Assert.Equal(0, _dataLayer.Calls);
        Assert.Null(_dataLayer.Audit);
    }

    /// <summary>
    /// Checked before the already-in-that-state shortcut, deliberately: a creator quoting a stale token has
    /// not seen the recipe as it now stands, and answering "already archived" would hide a collaborator's
    /// edit from them.
    /// </summary>
    [Fact]
    public async Task A_stale_token_is_a_conflict_even_when_the_state_already_matches()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        var result = await ArchiveAsync(recipe, token: "CAcGBQQDAgE=");

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
        Assert.Equal(0, _dataLayer.Calls);
    }

    [Fact]
    public async Task A_recipe_that_moves_on_during_an_archive_is_a_conflict()
    {
        var recipe = Stored();
        _dataLayer.StatusConflict = true;

        var result = await ArchiveAsync(recipe);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, result.Error!.Code);
    }

    [Fact]
    public async Task Archiving_a_recipe_that_is_not_visible_is_not_found()
    {
        var result = await _business.ArchiveAsync(
            Guid.NewGuid(), ActorUserId, Token, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// The freeze is not applied to the lifecycle commands themselves, or an archived recipe could never come
    /// back — stated because gating every write on the state is the obvious mistake to make here.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_still_accepts_the_command_that_brings_it_back()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        Assert.True((await UnarchiveAsync(recipe)).Succeeded);
    }

    [Fact]
    public async Task An_archived_recipe_refuses_an_ordinary_edit()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        var result = await UpdateAsync(recipe, Edit() with { Title = Set<string?>("Lemon cake") });

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, result.Error!.Code);
        Assert.Equal("Olive oil cake", recipe.Title);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// Ahead of the body's own invariants, because no edit can succeed while the recipe is archived —
    /// reporting field errors first would have a client fix four of them and then discover the real problem.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_reports_being_archived_before_it_reports_a_bad_body()
    {
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        var result = await UpdateAsync(recipe, Edit() with { TotalTimeMinutes = Set<int?>(-5) });

        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, result.Error!.Code);
    }

    // ---- The duplicate seam ----

    /// <summary>
    /// Loads <paramref name="archived"/> as the version a duplicate will copy, and performs the duplicate.
    /// </summary>
    /// <param name="archived">The source content, or <c>null</c> to make the version unavailable.</param>
    private Task<OperationResult<CreatedRecipeServiceModel>> DuplicateAsync(
        Recipe? archived,
        string title = "Olive oil and rosemary cake",
        int? sourceVersionNumber = null,
        bool sourceVisible = true)
    {
        _dataLayer.DuplicateSourceVisible = sourceVisible;
        _dataLayer.RestoreSource = archived is null
            ? null
            : new RecipeVersionSnapshotRecord(
                RestoredVersionId,
                sourceVersionNumber ?? 3,
                RecipeVersionSource.CreatorEdit,
                RecipeVersionReadiness.Ready,
                Now.AddDays(-2),
                RecipeSnapshotSerializer.Serialize(
                    RecipeSnapshotMapper.Capture(new CompleteRecipe(archived, null))));

        return _business.DuplicateAsync(
            Guid.NewGuid(),
            CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel
            {
                Title = title,
                SourceVersionNumber = sourceVersionNumber,
            }),
            TestContext.Current.CancellationToken);
    }

    /// <summary>A populated recipe to copy, with children whose ids the copy must not reuse.</summary>
    private static Recipe Source()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        recipe.Status = RecipeStatus.Ready;

        return recipe;
    }

    [Fact]
    public async Task A_duplicate_copies_the_archived_content_under_the_requested_title()
    {
        var source = Source();

        var result = await DuplicateAsync(source, title: "  Olive oil and rosemary cake  ");

        Assert.True(result.Succeeded);
        Assert.Equal("Olive oil and rosemary cake", _dataLayer.Recipe!.Title);
        Assert.Equal("Olive oil and rosemary cake", result.Value!.Title);

        // Everything the creator wrote comes across.
        Assert.Equal(source.Headnote, _dataLayer.Recipe.Headnote);
        Assert.Equal(source.AttributionText, _dataLayer.Recipe.AttributionText);
        Assert.Equal(source.SourceUrl, _dataLayer.Recipe.SourceUrl);
        Assert.Equal(source.YieldText, _dataLayer.Recipe.YieldText);
        Assert.Single(_dataLayer.Recipe.IngredientGroups);
        Assert.Single(_dataLayer.Recipe.InstructionGroups);
        Assert.Single(_dataLayer.Recipe.Equipment);
        Assert.Single(_dataLayer.Recipe.AssetLinks);
    }

    /// <summary>
    /// Whatever the source said. A copy of a finished recipe is not itself finished, and duplicating an
    /// archived recipe must not produce an archived copy the creator then has to go and find.
    /// </summary>
    [Theory]
    [InlineData(RecipeStatus.Ready)]
    [InlineData(RecipeStatus.Archived)]
    [InlineData(RecipeStatus.Draft)]
    public async Task A_copy_is_always_a_draft(RecipeStatus sourceStatus)
    {
        var source = Source();
        source.Status = sourceStatus;

        var result = await DuplicateAsync(source);

        Assert.Equal(RecipeStatus.Draft, _dataLayer.Recipe!.Status);
        Assert.Equal(RecipeStatus.Draft, result.Value!.Status);
        Assert.Equal(RecipeVersionReadiness.Draft, _dataLayer.Version!.Readiness);
    }

    [Fact]
    public async Task A_copys_first_version_says_it_was_duplicated()
    {
        await DuplicateAsync(Source());

        Assert.Equal(RecipeVersionSource.Duplicate, _dataLayer.Version!.Source);
        Assert.Null(_dataLayer.Version.Reason);

        // Restore lineage belongs to a restore. A duplicate's provenance is on the recipe.
        Assert.Null(_dataLayer.Version.RestoredFromVersionId);
        Assert.Equal(RestoredVersionId, _dataLayer.Recipe!.DuplicatedFromVersionId);
    }

    /// <summary>
    /// The lineage names the version that was copied, not the version the source recipe was itself copied
    /// from. A copy of a copy records its own parentage.
    /// </summary>
    [Fact]
    public async Task A_copy_of_a_copy_records_what_it_copied()
    {
        var source = Source();
        source.DuplicatedFromVersionId = Guid.NewGuid();

        await DuplicateAsync(source);

        Assert.Equal(RestoredVersionId, _dataLayer.Recipe!.DuplicatedFromVersionId);
    }

    [Fact]
    public async Task A_copy_is_attributed_to_whoever_made_it()
    {
        var source = Source();

        await DuplicateAsync(source);

        // Never the author of the recipe it came from.
        Assert.NotEqual(source.CreatedByMembershipId, _dataLayer.Recipe!.CreatedByMembershipId);
        Assert.Equal(ActorMembershipId, _dataLayer.Recipe.CreatedByMembershipId);
        Assert.Equal(ActorMembershipId, _dataLayer.Recipe.UpdatedByMembershipId);
        Assert.Equal(Now, _dataLayer.Recipe.CreatedAt);
        Assert.Equal(Now, _dataLayer.Recipe.UpdatedAt);
    }

    /// <summary>
    /// The concurrency token, the source's history and its own identity are all left behind. The copy is a
    /// new row, and the interceptor is what gives it a workspace.
    /// </summary>
    [Fact]
    public async Task A_copy_inherits_no_identity_ownership_or_audit_from_its_source()
    {
        var source = Source();

        await DuplicateAsync(source);

        var copy = _dataLayer.Recipe!;

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(Guid.Empty, copy.WorkspaceId);
        Assert.Empty(copy.RowVersion);
        Assert.NotEqual(
            source.IngredientGroups.Single().Id,
            copy.IngredientGroups.Single().Id);
        Assert.NotEqual(
            source.InstructionGroups.Single().Steps.Single().Id,
            copy.InstructionGroups.Single().Steps.Single().Id);
    }

    /// <summary>
    /// Omitting the version is not a different operation — it asks the layer below for the current one, and
    /// that is the only difference.
    /// </summary>
    [Fact]
    public async Task Omitting_the_version_asks_for_the_current_one()
    {
        await DuplicateAsync(Source());

        Assert.Null(_dataLayer.DuplicateRequest!.Value.VersionNumber);
    }

    [Fact]
    public async Task A_named_version_is_passed_down_as_asked()
    {
        await DuplicateAsync(Source(), sourceVersionNumber: 2);

        Assert.Equal(2, _dataLayer.DuplicateRequest!.Value.VersionNumber);
    }

    [Fact]
    public async Task A_source_recipe_that_is_not_visible_is_not_found()
    {
        var result = await DuplicateAsync(Source(), sourceVisible: false);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
        Assert.Equal(0, _dataLayer.Calls);
    }

    [Fact]
    public async Task A_version_the_source_does_not_have_is_refused_naming_the_field()
    {
        var result = await DuplicateAsync(archived: null, sourceVersionNumber: 9);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Equal(["This recipe has no version 9."], result.Error.FieldErrors["sourceVersionNumber"]);
        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// A visible recipe with no archived version at all is not the caller's mistake — they named no version —
    /// so the refusal names no field. Unreachable through this API, where every recipe has version 1.
    /// </summary>
    [Fact]
    public async Task A_source_with_no_archived_version_is_refused_without_naming_a_field()
    {
        var result = await DuplicateAsync(archived: null);

        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
    }

    /// <summary>
    /// The archive's tag ids drive a vocabulary read, and the rows come back as names so the create's own
    /// tag path can reuse them rather than this seam growing a second one.
    /// </summary>
    [Fact]
    public async Task The_archived_tags_are_resolved_and_handed_to_the_create_by_name()
    {
        var source = Source();
        _dataLayer.Vocabulary = [Tag(RecipeAggregateFixture.TagIdA, "Weeknight")];

        await DuplicateAsync(source);

        Assert.Equal([RecipeAggregateFixture.TagIdA], _dataLayer.RequestedTagIds);
        Assert.Equal(["weeknight"], _dataLayer.Tags!.Select(tag => tag.NormalizedName));
        Assert.Equal(["Weeknight"], _dataLayer.Tags!.Select(tag => tag.Name));
    }

    /// <summary>
    /// A tag whose vocabulary row is gone resolves to nothing and is simply not applied — the copy loses a
    /// tag rather than the request losing a 500 to a foreign key.
    /// </summary>
    [Fact]
    public async Task A_tag_no_longer_in_the_vocabulary_is_not_applied()
    {
        _dataLayer.Vocabulary = [];

        await DuplicateAsync(Source());

        Assert.Empty(_dataLayer.Tags!);
    }

    [Fact]
    public async Task A_source_document_this_build_cannot_read_raises()
    {
        _dataLayer.DuplicateSourceVisible = true;
        _dataLayer.RestoreSource = new RecipeVersionSnapshotRecord(
            RestoredVersionId,
            3,
            RecipeVersionSource.CreatorEdit,
            RecipeVersionReadiness.Draft,
            Now.AddDays(-2),
            RecipeSnapshotSerializer.Serialize(
                RecipeSnapshotMapper.Capture(new CompleteRecipe(Source(), null))
                    with { SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion + 1 }));

        await Assert.ThrowsAsync<NotSupportedException>(() => _business.DuplicateAsync(
            Guid.NewGuid(),
            CanonicalDuplicateRecipe.From(new DuplicateRecipeViewModel { Title = "Copy" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, _dataLayer.Calls);
    }

    /// <summary>
    /// The workspace reaches the write from the resolved context, never from the archive — a document carries
    /// no workspace, and a restore that could set one would be a restore that could move a recipe.
    /// </summary>
    [Fact]
    public async Task A_restore_never_changes_a_recipes_workspace()
    {
        var recipe = Stored();
        var workspaceId = recipe.WorkspaceId;

        await RestoreAsync(recipe, Archived(recipe));

        Assert.Equal(workspaceId, recipe.WorkspaceId);
    }

    private static WorkspaceTag Tag(Guid id, string name) =>
        new() { Id = id, Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAt = Now };

    // ---- Search ----

    /// <summary>
    /// The cursor is minted here, not in the repository, and it is minted from the criteria's scope — which is
    /// what binds it to the workspace, ordering and filters it was issued for.
    /// </summary>
    [Fact]
    public async Task A_page_with_more_to_come_carries_a_cursor_built_from_its_last_row()
    {
        var last = SummaryRow(Now.AddMinutes(-1));
        _dataLayer.Page = ([SummaryRow(Now), last], true, 7);

        var page = await SearchAsync(scope: "a-scope");

        Assert.Equal(7, page.TotalCount);
        Assert.Equal(2, page.Items.Count);

        // The exact cursor the shared PageBuilder would mint for that row under that scope — so a change to how
        // a page ends shows up here rather than as a client that cannot turn a page.
        Assert.Equal(
            Domain.Managers.Paging.ReferenceCursor.Encode(last.SortValue, last.TieBreaker, "a-scope"),
            page.NextCursor);
    }

    /// <summary>
    /// The last page carries no cursor, which is how a client knows to stop — rather than by comparing the row
    /// count against a page size the server may have clamped.
    /// </summary>
    [Fact]
    public async Task The_last_page_carries_no_cursor()
    {
        _dataLayer.Page = ([SummaryRow(Now)], false, 1);

        Assert.Null((await SearchAsync()).NextCursor);
    }

    [Fact]
    public async Task An_empty_library_is_an_empty_page_rather_than_a_failure()
    {
        _dataLayer.Page = ([], false, 0);

        var page = await SearchAsync();

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);

        // Zero is a count. Null would mean nobody asked, and the two must not be confused.
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task A_total_nobody_asked_for_is_absent_rather_than_zero()
    {
        _dataLayer.Page = ([SummaryRow(Now)], false, null);

        Assert.Null((await SearchAsync()).TotalCount);
    }

    /// <summary>
    /// The membership ids the repository row carries do not reach the wire — the same rule the detail read
    /// follows. Asserted over the published property set, so a field added later has to be considered rather
    /// than slipping through.
    /// </summary>
    [Fact]
    public async Task A_summary_publishes_no_membership_or_ownership_column()
    {
        _dataLayer.Page = ([SummaryRow(Now)], false, null);

        var summary = Assert.Single((await SearchAsync()).Items);
        var published = summary.GetType().GetProperties().Select(property => property.Name);

        Assert.Equal(
            (string[])
            [
                "Id", "Title", "Description", "Status", "CuisineId", "CourseId", "CreatedAt", "UpdatedAt",
                "LatestVersionNumber", "LatestVersionReadiness", "HasUnmatchedIngredients",
            ],
            published);
    }

    [Fact]
    public async Task A_summary_carries_the_rows_own_values()
    {
        var row = SummaryRow(Now);
        _dataLayer.Page = ([row], false, null);

        var summary = Assert.Single((await SearchAsync()).Items);

        Assert.Equal(row.Id, summary.Id);
        Assert.Equal(row.Title, summary.Title);
        Assert.Equal(row.Status, summary.Status);
        Assert.Equal(row.CuisineId, summary.CuisineId);
        Assert.Equal(row.UpdatedAt, summary.UpdatedAt);
        Assert.Equal(row.LatestVersionNumber, summary.LatestVersionNumber);
        Assert.Equal(row.LatestVersionReadiness, summary.LatestVersionReadiness);
        Assert.Equal(row.HasUnmatchedIngredients, summary.HasUnmatchedIngredients);
    }

    private Task<RecipeSearchPageServiceModel> SearchAsync(string scope = "scope") =>
        _business.SearchAsync(
            new RecipeSearchCriteria(new RecipeSearchFilters(), scope),
            TestContext.Current.CancellationToken);

    // ---- Version history ----

    /// <summary>
    /// The one refusal this read has, and it is the same one an unknown recipe gets — because the layer
    /// beneath cannot tell an absent recipe from another workspace's, and this one must not be able to either.
    /// </summary>
    [Fact]
    public async Task A_recipe_that_is_not_visible_is_not_found_rather_than_an_empty_history()
    {
        _dataLayer.History = null;

        var result = await HistoryAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    /// <summary>
    /// A recipe with no visible versions is not reachable today — creation writes version 1 in the same
    /// transaction — but if it ever were, it is an empty history and not a missing recipe. The distinction is
    /// carried by the layer below returning a page rather than null, and this is what pins it.
    /// </summary>
    [Fact]
    public async Task A_visible_recipe_with_no_versions_is_an_empty_page_rather_than_a_failure()
    {
        _dataLayer.History = ([], false);

        var result = await HistoryAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Null(result.Value.NextCursor);
    }

    /// <summary>
    /// The cursor is minted here, not in the repository, and from the criteria's scope — which is what binds it
    /// to the workspace and recipe it was issued for.
    /// </summary>
    [Fact]
    public async Task A_history_page_with_more_to_come_carries_a_cursor_built_from_its_last_row()
    {
        var last = HistoryRow(versionNumber: 2);
        _dataLayer.History = ([HistoryRow(versionNumber: 3), last], true);

        var page = (await HistoryAsync(scope: "a-scope")).Value!;

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(
            Domain.Managers.Paging.ReferenceCursor.Encode(last.SortValue, last.TieBreaker, "a-scope"),
            page.NextCursor);
    }

    [Fact]
    public async Task The_last_history_page_carries_no_cursor()
    {
        _dataLayer.History = ([HistoryRow(versionNumber: 1)], false);

        Assert.Null((await HistoryAsync()).Value!.NextCursor);
    }

    /// <summary>
    /// What a history entry publishes, pinned as the exact property set — so the snapshot, the membership id
    /// and the lineage token stay out by decision rather than by nobody having added them yet.
    /// </summary>
    [Fact]
    public async Task A_history_entry_publishes_no_membership_column_and_no_snapshot()
    {
        _dataLayer.History = ([HistoryRow(versionNumber: 1)], false);

        var entry = Assert.Single((await HistoryAsync()).Value!.Items);
        var published = entry.GetType().GetProperties().Select(property => property.Name);

        Assert.Equal(
            (string[])
            [
                "Id", "VersionNumber", "Source", "Readiness", "Reason", "CreatedAt", "CreatedByName",
                "ParentVersionId", "RestoredFromVersionId", "AiProposalId",
            ],
            published);
    }

    /// <summary>
    /// Business leaves the author unnamed and hands the membership up instead: naming it means reading two
    /// other modules, which this layer may not do. The Facade spends the ids — see
    /// <c>RecipeFacadeTests</c> — and this pins that the division holds rather than that someone remembered
    /// to leave the field null.
    /// </summary>
    [Fact]
    public async Task A_history_entry_leaves_the_author_for_the_facade_to_name()
    {
        var row = HistoryRow(versionNumber: 4);
        _dataLayer.History = ([row], false);

        var result = (await HistoryResultAsync()).Value!;

        Assert.Null(Assert.Single(result.Page.Items).CreatedByName);
        Assert.Equal(AuthorMembershipId, result.AuthorMembershipByVersionId[row.Id]);
    }

    [Fact]
    public async Task A_history_entry_carries_the_rows_own_values()
    {
        var row = HistoryRow(versionNumber: 4);
        _dataLayer.History = ([row], false);

        var entry = Assert.Single((await HistoryAsync()).Value!.Items);

        Assert.Equal(row.Id, entry.Id);
        Assert.Equal(row.VersionNumber, entry.VersionNumber);
        Assert.Equal(row.Source, entry.Source);
        Assert.Equal(row.Readiness, entry.Readiness);
        Assert.Equal(row.Reason, entry.Reason);
        Assert.Equal(row.CreatedAt, entry.CreatedAt);
        Assert.Equal(row.ParentVersionId, entry.ParentVersionId);
        Assert.Equal(row.AiProposalId, entry.AiProposalId);
    }

    // ---- Version comparison ----

    /// <summary>
    /// A recipe the caller may not see and a version number it does not have are different facts. Collapsing
    /// them would tell a creator who mistyped a number that their recipe does not exist.
    /// </summary>
    [Fact]
    public async Task An_invisible_recipe_is_reported_as_a_missing_recipe()
    {
        _dataLayer.Snapshots = null;

        var result = await CompareAsync(1, 2);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task A_missing_version_names_the_parameter_at_fault()
    {
        _dataLayer.Snapshots = [SnapshotRow(1, "Olive oil cake")];

        var result = await CompareAsync(1, 9);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Contains("to", result.Error.FieldErrors.Keys);
        Assert.DoesNotContain("from", result.Error.FieldErrors.Keys);
    }

    [Fact]
    public async Task Both_missing_versions_are_reported_together()
    {
        _dataLayer.Snapshots = [];

        var result = await CompareAsync(8, 9);

        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Equal(["from", "to"], result.Error.FieldErrors.Keys.Order());
    }

    /// <summary>
    /// The repository returns one row when both numbers are the same. Read as both sides rather than as a
    /// missing version: comparing a version with itself is a legitimate question whose answer is "nothing".
    /// </summary>
    [Fact]
    public async Task One_row_answers_a_comparison_of_a_version_with_itself()
    {
        var row = SnapshotRow(2, "Olive oil cake");
        _dataLayer.Snapshots = [row];

        var result = await CompareAsync(2, 2);

        Assert.True(result.Succeeded);
        Assert.Equal(row.Id, result.Value!.From.VersionId);
        Assert.Equal(row.Id, result.Value.To.VersionId);
        Assert.False(result.Value.Comparison.HasChanges);
    }

    [Fact]
    public async Task A_comparison_reads_the_stored_documents_and_publishes_the_difference()
    {
        _dataLayer.Snapshots =
        [
            SnapshotRow(1, "Olive oil cake"),
            SnapshotRow(2, "Olive oil and rosemary cake"),
        ];

        var result = await CompareAsync(1, 2);

        Assert.True(result.Succeeded);

        var change = Assert.Single(result.Value!.Comparison[RecipeComparisonSection.Metadata].FieldChanges);
        Assert.Equal(RecipeComparisonField.Title, change.Field);
        Assert.Equal("Olive oil cake", change.From);
        Assert.Equal("Olive oil and rosemary cake", change.To);
    }

    /// <summary>
    /// The caller chooses the direction, and a creator weighing a revert reads the newer version as the
    /// left-hand side. The rows come back unordered, so Business must match them by number rather than by
    /// position.
    /// </summary>
    [Fact]
    public async Task The_sides_follow_the_requested_direction_rather_than_the_row_order()
    {
        _dataLayer.Snapshots =
        [
            SnapshotRow(1, "Olive oil cake"),
            SnapshotRow(2, "Olive oil and rosemary cake"),
        ];

        var result = await CompareAsync(2, 1);

        Assert.Equal(2, result.Value!.From.VersionNumber);

        var change = Assert.Single(result.Value.Comparison[RecipeComparisonSection.Metadata].FieldChanges);
        Assert.Equal("Olive oil and rosemary cake", change.From);
        Assert.Equal("Olive oil cake", change.To);
    }

    [Fact]
    public async Task The_recipe_and_both_numbers_reach_the_data_layer()
    {
        var recipeId = Guid.NewGuid();
        _dataLayer.Snapshots = [SnapshotRow(3, "Olive oil cake"), SnapshotRow(7, "Olive oil cake")];

        await _business.CompareVersionsAsync(recipeId, 3, 7, TestContext.Current.CancellationToken);

        Assert.Equal((recipeId, 3, 7), _dataLayer.SnapshotRequest);
    }

    /// <summary>
    /// An archive this build cannot read raises rather than answering, and it raises the exception that says
    /// which kind of unreadable it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberate, and pinned here so it stays deliberate. <c>MinimumReadableSchemaVersion</c> guarantees
    /// documents written by older builds stay readable, so the only way to a refused schema version is a
    /// document written by a <em>newer</em> build than the one serving the request — an older instance
    /// reading forward during a rolling deploy. That is a server fault; a 500 is the truthful status, and an
    /// error code would invite a client to handle a condition it can do nothing about.
    /// </para>
    /// <para>
    /// The cost is real and worth knowing: the first request after <c>CurrentSchemaVersion</c> is bumped
    /// will 500 on every instance still running the previous build. If that becomes unacceptable, the change
    /// is a caught translation here, not a silent one lower down.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_archive_this_build_cannot_read_raises_rather_than_answering()
    {
        var future = SnapshotRow(1, "Olive oil cake") with
        {
            Document = RecipeSnapshotSerializer.Serialize(new RecipeSnapshotDocument
            {
                SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion + 1,
                Recipe = new RecipeSnapshotHeader { Title = "Written by a later build" },
            }),
        };

        _dataLayer.Snapshots = [future, SnapshotRow(2, "Olive oil cake")];

        await Assert.ThrowsAsync<NotSupportedException>(() => CompareAsync(1, 2));
    }

    [Fact]
    public async Task A_stored_document_that_is_not_a_snapshot_raises_rather_than_answering()
    {
        _dataLayer.Snapshots =
        [
            SnapshotRow(1, "Olive oil cake") with { Document = "{ not a snapshot" },
            SnapshotRow(2, "Olive oil cake"),
        ];

        await Assert.ThrowsAsync<InvalidOperationException>(() => CompareAsync(1, 2));
    }

    private Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareAsync(int from, int to) =>
        _business.CompareVersionsAsync(Guid.NewGuid(), from, to, TestContext.Current.CancellationToken);

    /// <summary>One archived version, whose document differs from the others only in the recipe's title.</summary>
    private static RecipeVersionSnapshotRecord SnapshotRow(int versionNumber, string title) =>
        new(Guid.NewGuid(),
            versionNumber,
            RecipeVersionSource.CreatorEdit,
            RecipeVersionReadiness.Draft,
            CreatedAt: Now.AddMinutes(versionNumber),
            Document: RecipeSnapshotSerializer.Serialize(new RecipeSnapshotDocument
            {
                SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
                Recipe = new RecipeSnapshotHeader { Title = title },
            }));

    private Task<OperationResult<RecipeVersionHistoryPageResult>>
        HistoryResultAsync(string scope = "scope") =>
        _business.GetVersionHistoryAsync(
            new RecipeVersionHistoryCriteria(Guid.NewGuid(), scope),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The page alone, for the assertions that are about paging rather than about authorship. The authorship
    /// map travels beside the page because Business cannot resolve it — see
    /// <see cref="RecipeVersionHistoryPageResult"/> — and unwrapping here keeps that from being restated at
    /// every call site that does not care.
    /// </summary>
    private async Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<RecipeVersionHistoryServiceModel>>>
        HistoryAsync(string scope = "scope")
    {
        var result = await HistoryResultAsync(scope);

        return result.Succeeded
            ? OperationResult<Domain.Managers.Paging.CursorPageServiceModel<RecipeVersionHistoryServiceModel>>
                .Success(result.Value!.Page)
            : OperationResult<Domain.Managers.Paging.CursorPageServiceModel<RecipeVersionHistoryServiceModel>>
                .Failure(result.Error!);
    }

    /// <summary>The membership every seeded history row is attributed to.</summary>
    private static readonly Guid AuthorMembershipId = Guid.NewGuid();

    private static RecipeVersionHistoryRecord HistoryRow(int versionNumber) =>
        new(Guid.NewGuid(),
            versionNumber,
            AuthorMembershipId,
            RecipeVersionSource.CreatorEdit,
            RecipeVersionReadiness.Draft,
            Reason: versionNumber == 1 ? null : $"Edit {versionNumber}",
            CreatedAt: Now.AddMinutes(versionNumber),
            ParentVersionId: versionNumber == 1 ? null : Guid.NewGuid(),
            RestoredFromVersionId: null,
            AiProposalId: null);

    private static RecipeSummaryRecord SummaryRow(DateTimeOffset updatedAt) =>
        new(Guid.NewGuid(),
            "Olive oil cake",
            "A cake.",
            RecipeStatus.Ready,
            CuisineId: Guid.NewGuid(),
            CourseId: null,
            CreatedByMembershipId: ActorMembershipId,
            UpdatedByMembershipId: ActorMembershipId,
            CreatedAt: Now,
            UpdatedAt: updatedAt,
            LatestVersionNumber: 3,
            LatestVersionReadiness: RecipeVersionReadiness.Ready,
            HasUnmatchedIngredients: true,
            Sort: RecipeSearchSort.RecentlyUpdated);

    private sealed class RecordingRecipeDataLayer : IRecipeDataLayer
    {
        public int Calls { get; private set; }

        public Recipe? Recipe { get; private set; }

        public RecipeVersionFacts? Version { get; private set; }

        public IReadOnlyCollection<RecipeTagName>? Tags { get; private set; }

        /// <summary>What <see cref="GetDetailAsync"/> will answer with. Null stands for "no such recipe".</summary>
        public TaggedRecipe? Detail { get; set; }

        public Guid? RequestedRecipeId { get; private set; }

        public Task<CreatedRecipe> CreateAsync(
            Recipe recipe,
            RecipeVersionFacts version,
            IReadOnlyCollection<RecipeTagName> tags,
            CancellationToken cancellationToken)
        {
            Calls++;
            (Recipe, Version, Tags) = (recipe, version, tags);

            return Task.FromResult(new CreatedRecipe(recipe.Id, Guid.NewGuid(), 1));
        }

        public Task<TaggedRecipe?> GetDetailAsync(Guid recipeId, CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;

            return Task.FromResult(Detail);
        }

        /// <summary>What <see cref="SearchAsync"/> answers with.</summary>
        public (IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore, int? Total) Page { get; set; } = ([], false, null);

        /// <summary>The criteria the last search was asked for, so a test can assert what was passed down.</summary>
        public RecipeSearchCriteria? SearchCriteria { get; private set; }

        public Task<(IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore, int? Total)> SearchAsync(
            RecipeSearchCriteria criteria,
            CancellationToken cancellationToken)
        {
            Calls++;
            SearchCriteria = criteria;

            return Task.FromResult(Page);
        }

        /// <summary>
        /// What <see cref="ListVersionsAsync"/> answers with. <c>null</c> stands for "no such recipe", which is
        /// a different fact from an empty page and must stay one.
        /// </summary>
        public (IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)? History { get; set; } = ([], false);

        /// <summary>The criteria the last history read was asked for, so a test can assert what was passed down.</summary>
        public RecipeVersionHistoryCriteria? HistoryCriteria { get; private set; }

        public Task<(IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)?> ListVersionsAsync(
            RecipeVersionHistoryCriteria criteria,
            CancellationToken cancellationToken)
        {
            Calls++;
            HistoryCriteria = criteria;

            return Task.FromResult(History);
        }

        /// <summary>
        /// What <see cref="FindVersionSnapshotsAsync"/> answers with. <c>null</c> stands for "no such recipe",
        /// which is a different fact from finding no matching versions and must stay one.
        /// </summary>
        public IReadOnlyList<RecipeVersionSnapshotRecord>? Snapshots { get; set; } = [];

        /// <summary>The version numbers the last comparison asked for, so a test can assert what was passed down.</summary>
        public (Guid RecipeId, int First, int Second)? SnapshotRequest { get; private set; }

        public Task<IReadOnlyList<RecipeVersionSnapshotRecord>?> FindVersionSnapshotsAsync(
            Guid recipeId,
            int firstVersionNumber,
            int secondVersionNumber,
            CancellationToken cancellationToken)
        {
            Calls++;
            SnapshotRequest = (recipeId, firstVersionNumber, secondVersionNumber);

            return Task.FromResult(Snapshots);
        }

        /// <summary>Answers with <see cref="Detail"/>, like the read does: these tests hold one recipe.</summary>
        public Task<TaggedRecipe?> GetForUpdateAsync(Guid recipeId, CancellationToken cancellationToken)
        {
            RequestedRecipeId = recipeId;

            // Deliberately not counted. Calls exists so a test can prove an edit that changes nothing never
            // reaches the write, and a read every update must make would drown that assertion.
            return Task.FromResult(Detail);
        }

        public Task<RecipeUpdateOutcome> UpdateAsync(
            TaggedRecipe loaded,
            RecipeVersionFacts version,
            IReadOnlyCollection<RecipeTagName>? tags,
            CancellationToken cancellationToken)
        {
            Calls++;
            (Recipe, Version, Tags) = (loaded.Recipe.Recipe, version, tags);
            Updated = loaded;

            return Task.FromResult(Conflict
                ? RecipeUpdateOutcome.Conflict()
                : RecipeUpdateOutcome.Applied(
                    new RecipeVersion
                    {
                        Id = Guid.NewGuid(),
                        RecipeId = loaded.Recipe.Recipe.Id,
                        VersionNumber = (loaded.Recipe.CurrentVersion?.VersionNumber ?? 0) + 1,
                        ParentVersionId = loaded.Recipe.CurrentVersion?.Id,
                        Source = version.Source,
                        Readiness = version.Readiness,
                        Reason = version.Reason,
                        CreatedAt = loaded.Recipe.Recipe.UpdatedAt,
                        CreatedByMembershipId = loaded.Recipe.Recipe.UpdatedByMembershipId,
                    },
                    loaded.Tags));
        }

        /// <summary>Set to make the next write answer as though someone else had saved first.</summary>
        public bool Conflict { get; set; }

        /// <summary>The unit the last write was handed, so a test can inspect what Business changed.</summary>
        public TaggedRecipe? Updated { get; private set; }

        /// <summary>
        /// What <see cref="GetForRestoreAsync"/> answers with for the snapshot half of the unit. <c>null</c>
        /// stands for "this recipe has no such version", which <see cref="Detail"/> being null does not: that
        /// one means the recipe itself is invisible, and the two are different answers.
        /// </summary>
        public RecipeVersionSnapshotRecord? RestoreSource { get; set; }

        /// <summary>What the last restore asked for, so a test can assert what was passed down.</summary>
        public (Guid RecipeId, int VersionNumber)? RestoreRequest { get; private set; }

        public Task<RecipeRestoreUnit?> GetForRestoreAsync(
            Guid recipeId,
            int versionNumber,
            CancellationToken cancellationToken)
        {
            RequestedRecipeId = recipeId;
            RestoreRequest = (recipeId, versionNumber);

            // Uncounted, for the reason GetForUpdateAsync gives: every restore reads, and counting the read
            // would drown the assertion that a restore changing nothing never reaches the write.
            return Task.FromResult(Detail is null ? null : new RecipeRestoreUnit(Detail, RestoreSource));
        }

        /// <summary>The workspace's tag vocabulary, as far as these tests are concerned.</summary>
        public IReadOnlyList<WorkspaceTag> Vocabulary { get; set; } = [];

        /// <summary>The ids the last vocabulary read asked for, so a test can assert the archive drove it.</summary>
        public IReadOnlyCollection<Guid>? RequestedTagIds { get; private set; }

        public Task<IReadOnlyList<WorkspaceTag>> FindWorkspaceTagsAsync(
            IReadOnlyCollection<Guid> tagIds,
            CancellationToken cancellationToken)
        {
            RequestedTagIds = tagIds;

            // Uncounted, as above, and filtered rather than returned whole: the point of this read is that an
            // id with no vocabulary row comes back missing.
            return Task.FromResult<IReadOnlyList<WorkspaceTag>>(
                [.. Vocabulary.Where(tag => tagIds.Contains(tag.Id))]);
        }

        /// <summary>The vocabulary rows the last restore was handed, so a test can assert what it resolved.</summary>
        public IReadOnlyList<WorkspaceTag>? RestoredTags { get; private set; }

        /// <summary>Set to make the next lifecycle command answer as though someone else had saved first.</summary>
        public bool StatusConflict { get; set; }

        /// <summary>The audit entry the last lifecycle command staged, so a test can assert what it recorded.</summary>
        public AuditEntry? Audit { get; private set; }

        /// <summary>The status the recipe carried when the last lifecycle command reached the write.</summary>
        public RecipeStatus? WrittenStatus { get; private set; }

        public Task<bool> TrySetStatusAsync(
            TaggedRecipe loaded,
            AuditEntry audit,
            CancellationToken cancellationToken)
        {
            Calls++;
            Recipe = loaded.Recipe.Recipe;
            Updated = loaded;
            Audit = audit;
            WrittenStatus = loaded.Recipe.Recipe.Status;

            return Task.FromResult(!StatusConflict);
        }

        /// <summary>
        /// Whether the source recipe of a duplicate is visible. Separate from <see cref="RestoreSource"/>
        /// being null, which means the recipe is readable and has no such version.
        /// </summary>
        public bool DuplicateSourceVisible { get; set; } = true;

        /// <summary>What the last duplicate asked for, so a test can assert what was passed down.</summary>
        public (Guid RecipeId, int? VersionNumber)? DuplicateRequest { get; private set; }

        public Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindDuplicateSourceAsync(
            Guid recipeId,
            int? versionNumber,
            CancellationToken cancellationToken)
        {
            RequestedRecipeId = recipeId;
            DuplicateRequest = (recipeId, versionNumber);

            // Uncounted, as the other reads are: Calls counts writes, so a test can prove a refused request
            // never reached one.
            return Task.FromResult(DuplicateSourceVisible
                ? (true, RestoreSource)
                : (false, (RecipeVersionSnapshotRecord?)null));
        }

        public Task<RecipeUpdateOutcome> RestoreAsync(
            TaggedRecipe loaded,
            RecipeVersionFacts version,
            IReadOnlyList<WorkspaceTag> tags,
            CancellationToken cancellationToken)
        {
            Calls++;
            (Recipe, Version) = (loaded.Recipe.Recipe, version);
            Updated = loaded;
            RestoredTags = tags;

            return Task.FromResult(Conflict
                ? RecipeUpdateOutcome.Conflict()
                : RecipeUpdateOutcome.Applied(
                    new RecipeVersion
                    {
                        Id = Guid.NewGuid(),
                        RecipeId = loaded.Recipe.Recipe.Id,
                        VersionNumber = (loaded.Recipe.CurrentVersion?.VersionNumber ?? 0) + 1,
                        ParentVersionId = loaded.Recipe.CurrentVersion?.Id,
                        RestoredFromVersionId = version.RestoredFromVersionId,
                        Source = version.Source,
                        Readiness = version.Readiness,
                        Reason = version.Reason,
                        CreatedAt = loaded.Recipe.Recipe.UpdatedAt,
                        CreatedByMembershipId = loaded.Recipe.Recipe.UpdatedByMembershipId,
                    },
                    tags));
        }
    }

    private sealed class StubWorkspaceContext(Guid membershipId) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public string WorkspaceSlug => "workspace-a";

        public Guid MembershipId { get; } = membershipId;

        public WorkspaceRole Role => WorkspaceRole.Owner;
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
