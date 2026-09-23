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
        var recipe = Stored(stored => stored.Status = RecipeStatus.Archived);

        var result = await UpdateAsync(recipe, Edit() with { Status = Set<SettableRecipeStatusViewModel?>(null) });

        // The validator refuses this too, so nothing reaches here over HTTP. The guard is for the callers no
        // MVC pipeline protects — a worker, an AI plugin, a future facade overload — where the alternative is
        // an archived recipe quietly reappearing in the working set as an ordinary-looking creator edit.
        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, result.Error!.Code);
        Assert.Contains("status", result.Error.FieldErrors.Keys);
        Assert.Equal(RecipeStatus.Archived, recipe.Status);
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

    private static WorkspaceTag Tag(Guid id, string name) =>
        new() { Id = id, Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAt = Now };

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
