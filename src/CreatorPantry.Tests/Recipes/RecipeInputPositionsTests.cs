using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Where a refusal about one line or one step is reported.
/// </summary>
/// <remarks>
/// The rules themselves are unchanged and tested elsewhere; what these pin is the <em>key</em> each failure
/// arrives under, because that is the whole point of the split: a list-level problem stays on the list, and a
/// problem with one line names that line so an editor can mark it.
/// </remarks>
public sealed class RecipeInputPositionsTests
{
    private readonly CreateRecipeViewModelValidator _create = new();
    private readonly UpdateRecipeViewModelValidator _update = new();

    private static RecipeIngredientInputViewModel Line(string text = "2 cups flour") =>
        new() { DisplayText = text };

    private static RecipeIngredientGroupInputViewModel Group(params RecipeIngredientInputViewModel?[] lines) =>
        new() { Ingredients = lines };

    private static CreateRecipeViewModel Creating(params RecipeIngredientGroupInputViewModel?[] groups) =>
        new() { Title = "Olive oil cake", IngredientGroups = groups };

    private static UpdateRecipeViewModel Updating(params RecipeIngredientGroupInputViewModel?[] groups) =>
        new()
        {
            ExpectedConcurrencyToken = "AAAAAAAAB9E=",
            Title = PatchField<string?>.Submitted("Olive oil cake"),
            Status = PatchField<SettableRecipeStatusViewModel?>.Submitted(SettableRecipeStatusViewModel.Draft),
            IngredientGroups = PatchField<IReadOnlyList<RecipeIngredientGroupInputViewModel?>?>.Submitted(groups),
        };

    /// <summary>The key format itself, since a client resolves a position out of it.</summary>
    [Fact]
    public void A_line_path_names_its_group_and_its_position()
    {
        Assert.Equal("IngredientGroups[0].ingredients[2].quantity", RecipeInputPositions.IngredientLinePath(0, 2, "quantity"));
        Assert.Equal("IngredientGroups[1].title", RecipeInputPositions.IngredientGroupPath(1, "title"));
        Assert.Equal("Instructions[0].steps[3].text", RecipeInputPositions.InstructionStepPath(0, 3, "text"));
    }

    [Fact]
    public void A_refused_quantity_is_reported_at_the_line_that_carries_it()
    {
        var model = Creating(Group(Line(), Line() with { Quantity = 0m }, Line()));

        var failure = Assert.Single(_create.Validate(model).Errors);

        Assert.Equal("IngredientGroups[0].ingredients[1].quantity", failure.PropertyName);
        Assert.Equal("A quantity must be greater than zero.", failure.ErrorMessage);
    }

    [Fact]
    public void The_same_holds_on_an_edit()
    {
        var model = Updating(Group(Line(), Line() with { Quantity = -1m }));

        var failure = Assert.Single(_update.Validate(model).Errors);

        Assert.Equal("IngredientGroups[0].ingredients[1].quantity", failure.PropertyName);
    }

    /// <summary>Second group, second line — both indices are the submitted position, not a running count.</summary>
    [Fact]
    public void A_position_counts_within_its_own_group()
    {
        var model = Creating(Group(Line(), Line()), Group(Line(), Line() with { Quantity = 0m }));

        var failure = Assert.Single(_create.Validate(model).Errors);

        Assert.Equal("IngredientGroups[1].ingredients[1].quantity", failure.PropertyName);
    }

    /// <summary>
    /// A line wrong in two ways says so in one refusal. The old chain stopped at the first, so a creator fixed
    /// one thing, saved, and was told about the next.
    /// </summary>
    [Fact]
    public void Everything_wrong_with_one_line_is_reported_at_once()
    {
        var model = Creating(Group(Line("") with { Quantity = 0m }));

        var failures = _create.Validate(model).Errors;

        Assert.Contains(failures, f => f.PropertyName == "IngredientGroups[0].ingredients[0].displayText");
        Assert.Contains(failures, f => f.PropertyName == "IngredientGroups[0].ingredients[0].quantity");
    }

    [Fact]
    public void Two_refused_lines_are_both_reported_at_their_own_positions()
    {
        var model = Creating(Group(Line() with { Quantity = 0m }, Line(), Line() with { Quantity = 0m }));

        var failures = _create.Validate(model).Errors;

        Assert.Equal(
            ["IngredientGroups[0].ingredients[0].quantity", "IngredientGroups[0].ingredients[2].quantity"],
            failures.Select(f => f.PropertyName).Order());
    }

    [Fact]
    public void A_refused_group_heading_is_reported_at_that_group()
    {
        var model = Creating(Group(Line()), new RecipeIngredientGroupInputViewModel { Title = new string('x', 500) });

        var failure = Assert.Single(_create.Validate(model).Errors);

        Assert.Equal("IngredientGroups[1].title", failure.PropertyName);
    }

    /// <summary>
    /// A total is not about any one line, so it keeps the list's own key — which is also what the section-level
    /// message in the editor binds to.
    /// </summary>
    [Fact]
    public void A_list_level_refusal_keeps_the_list_key()
    {
        var lines = Enumerable.Range(0, RecipePolicy.MaxIngredientLinesPerRecipe + 1).Select(_ => Line()).ToArray();
        var model = Creating(Group(lines));

        var failure = Assert.Single(_create.Validate(model).Errors);

        Assert.Equal("IngredientGroups", failure.PropertyName);
        Assert.Contains("at most", failure.ErrorMessage);
    }

    /// <summary>
    /// A null entry in the list is dropped, not refused, and nothing is reported at its position either.
    /// </summary>
    /// <remarks>
    /// Pinned as it is rather than as it should be. The list-level guard reads
    /// <c>!AllLines(...).Any(line => line is null)</c>, but <c>AllLines</c> filters with <c>OfType</c>, so the
    /// nulls are gone before the guard sees them and it can never fire — which predates the per-position split
    /// and is not its to change, since that would alter which requests are refused. The per-position pass skips
    /// a null for its own reason: there is no line there for a creator to fix.
    /// </remarks>
    [Fact]
    public void A_blank_entry_is_dropped_rather_than_refused()
    {
        var model = Creating(Group(Line(), null));

        Assert.True(_create.Validate(model).IsValid);
    }

    /// <summary>A valid submission is still valid — the split must not have invented a refusal.</summary>
    [Fact]
    public void A_well_formed_list_is_accepted()
    {
        Assert.True(_create.Validate(Creating(Group(Line(), Line()), Group(Line()))).IsValid);
        Assert.True(_update.Validate(Updating(Group(Line(), Line()))).IsValid);
    }

    /// <summary>A create still refuses an id, and now says which line carried it.</summary>
    [Fact]
    public void A_create_that_names_an_existing_line_is_refused_at_that_line()
    {
        var model = Creating(Group(Line(), Line() with { Id = Guid.NewGuid() }));

        var failure = Assert.Single(_create.Validate(model).Errors);

        Assert.Equal("IngredientGroups[0].ingredients[1].id", failure.PropertyName);
        Assert.Equal("A new recipe's ingredients cannot name an existing line.", failure.ErrorMessage);
    }

    [Fact]
    public void A_refused_step_is_reported_at_that_step()
    {
        var model = new CreateRecipeViewModel
        {
            Title = "Olive oil cake",
            Instructions =
            [
                new RecipeInstructionGroupInputViewModel
                {
                    Steps = [new RecipeInstructionStepInputViewModel { Text = "Mix." }, new RecipeInstructionStepInputViewModel { Text = "   " }],
                },
            ],
        };

        var failure = Assert.Single(_create.Validate(model).Errors);

        Assert.Equal("Instructions[0].steps[1].text", failure.PropertyName);
        Assert.Equal("An instruction step cannot be blank.", failure.ErrorMessage);
    }
}
