using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Turning accepted changes into the ordinary partial edit that applies them: which fields are reached, which
/// are left alone, and which changes are refused before anything is written.
/// </summary>
/// <remarks>
/// Pure, over an aggregate built in this file. What matters most here is <em>absence</em>: a patch that
/// submitted a field no accepted change mentioned would rewrite it with the value it already had, and on the
/// instruction list or the tag set that would silently delete the creator's content.
/// </remarks>
public sealed class RecipeProposalApplicationTests
{
    private const string GroupTitle = "Bake";

    // ---- the recipe's own fields ----------------------------------------------------------------------

    [Fact]
    public void An_accepted_header_change_submits_only_that_field()
    {
        var patch = Plan(Set(ProposedRecipeTarget.Recipe, null, "headnote", "A warmer opening."));

        Assert.True(patch.Headnote.IsSubmitted);
        Assert.Equal("A warmer opening.", patch.Headnote.Value);

        // Everything else is absent, which is what leaves it alone. A submitted field holding its current value
        // would be indistinguishable from an edit, and the instruction and tag cases below are where that
        // matters most.
        Assert.False(patch.Title.IsSubmitted);
        Assert.False(patch.Description.IsSubmitted);
        Assert.False(patch.Instructions.IsSubmitted);
        Assert.False(patch.Tags.IsSubmitted);
    }

    [Fact]
    public void Several_accepted_header_changes_compose()
    {
        var patch = Plan(
            Set(ProposedRecipeTarget.Recipe, null, "title", "Plum cake"),
            Set(ProposedRecipeTarget.Recipe, null, "prepTimeMinutes", "35"));

        Assert.Equal("Plum cake", patch.Title.Value);
        Assert.Equal(35, patch.PrepTimeMinutes.Value);
    }

    /// <summary>A null value clears the field, which is a real accepted change rather than a missing one.</summary>
    [Fact]
    public void A_null_value_clears_the_field()
    {
        var patch = Plan(Set(ProposedRecipeTarget.Recipe, null, "notes", null));

        Assert.True(patch.Notes.IsSubmitted);
        Assert.Null(patch.Notes.Value);
    }

    /// <summary>
    /// The diff a creator reviewed was strings, so parsing happens once, here, against the field it is destined
    /// for. A value that cannot live there is refused by name rather than stored as something else.
    /// </summary>
    [Theory]
    [InlineData("prepTimeMinutes", "about half an hour")]
    [InlineData("yieldQuantity", "a dozen-ish")]
    public void A_header_value_that_will_not_parse_is_refused(string field, string value)
    {
        var plan = RecipeProposalApplication.Plan(
            Loaded(), [Set(ProposedRecipeTarget.Recipe, null, field, value)]);

        Assert.False(plan.Succeeded);
        Assert.Null(plan.Patch);
        Assert.Contains(value, plan.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// No reference id and no status. The proposal contract has no field for either, so there is nothing to
    /// translate — and a change that named one would be refused rather than quietly dropped.
    /// </summary>
    [Theory]
    [InlineData("cuisineId")]
    [InlineData("yieldUnitId")]
    [InlineData("status")]
    public void A_field_no_proposal_may_set_is_refused(string field)
    {
        var plan = RecipeProposalApplication.Plan(
            Loaded(), [Set(ProposedRecipeTarget.Recipe, null, field, Guid.NewGuid().ToString())]);

        Assert.False(plan.Succeeded);
        Assert.Contains(field, plan.Error!, StringComparison.Ordinal);
    }

    // ---- the method ----------------------------------------------------------------------------------

    /// <summary>
    /// Changing one step re-emits every step, because the patch's instruction list replaces wholesale. The
    /// re-emitted steps come from the live aggregate, never from the model's answer.
    /// </summary>
    [Fact]
    public void An_accepted_step_change_re_emits_the_whole_method_from_the_recipe()
    {
        var loaded = Loaded();
        var recipe = loaded.Recipe.Recipe;
        var group = recipe.InstructionGroups.Single();
        var first = group.Steps.First();

        var plan = RecipeProposalApplication.Plan(
            loaded, [Set(ProposedRecipeTarget.InstructionStep, first.Id, "text", "Cream the butter.")]);

        var submitted = Assert.Single(plan.Patch!.Instructions.Value);

        Assert.Equal(group.Id, submitted.Id);
        Assert.Equal(GroupTitle, submitted.Title);
        Assert.Equal(2, submitted.Steps.Count);

        // The changed step carries the accepted text; the untouched one carries the recipe's own.
        Assert.Equal("Cream the butter.", submitted.Steps[0].Text);
        Assert.Equal("Cool on a rack.", submitted.Steps[1].Text);

        // Ids are preserved on both, which is what makes the reconciler update in place instead of replacing
        // the method with two new steps and deleting the creator's originals.
        Assert.Equal([first.Id, group.Steps.Last().Id], submitted.Steps.Select(step => step.Id));
    }

    /// <summary>
    /// A field a step change does not mention survives. The reconciler applies whatever the submitted step
    /// says, so dropping a duration here would clear it without anyone having asked.
    /// </summary>
    [Fact]
    public void A_step_change_preserves_the_fields_it_does_not_name()
    {
        var loaded = Loaded();
        var first = loaded.Recipe.Recipe.InstructionGroups.Single().Steps.First();

        var plan = RecipeProposalApplication.Plan(
            loaded, [Set(ProposedRecipeTarget.InstructionStep, first.Id, "note", "Watch the edges.")]);

        var step = plan.Patch!.Instructions.Value[0].Steps[0];

        Assert.Equal("Watch the edges.", step.Note);
        Assert.Equal(first.Text, step.Text);
        Assert.Equal(first.DurationMinutes, step.DurationMinutes);
        Assert.Equal(first.TemperatureValue, step.TemperatureValue);
        Assert.Equal(first.TemperatureUnitId, step.TemperatureUnitId);
        Assert.Equal(first.TechniqueId, step.TechniqueId);
    }

    [Fact]
    public void An_accepted_group_title_change_is_applied()
    {
        var loaded = Loaded();
        var group = loaded.Recipe.Recipe.InstructionGroups.Single();

        var plan = RecipeProposalApplication.Plan(
            loaded, [Set(ProposedRecipeTarget.InstructionGroup, group.Id, "title", "Bake and cool")]);

        Assert.Equal("Bake and cool", plan.Patch!.Instructions.Value[0].Title);
    }

    [Fact]
    public void An_accepted_removal_drops_the_step_from_the_submitted_method()
    {
        var loaded = Loaded();
        var first = loaded.Recipe.Recipe.InstructionGroups.Single().Steps.First();

        var plan = RecipeProposalApplication.Plan(
            loaded,
            [new ProposedRecipeChange(
                ProposedRecipeChangeKind.Remove, ProposedRecipeTarget.InstructionStep, first.Id, null, null, null)]);

        var steps = plan.Patch!.Instructions.Value[0].Steps;

        Assert.Single(steps);
        Assert.Equal("Cool on a rack.", steps[0].Text);
    }

    [Fact]
    public void An_accepted_move_reorders_the_submitted_method()
    {
        var loaded = Loaded();
        var group = loaded.Recipe.Recipe.InstructionGroups.Single();
        var first = group.Steps.First();
        var second = group.Steps.Last();

        var plan = RecipeProposalApplication.Plan(
            loaded,
            [new ProposedRecipeChange(
                ProposedRecipeChangeKind.Move, ProposedRecipeTarget.InstructionStep, first.Id, null, null, 1)]);

        Assert.Equal(
            [second.Id, first.Id],
            plan.Patch!.Instructions.Value[0].Steps.Select(step => step.Id));
    }

    /// <summary>
    /// A position past the end means "put it last". Refusing a change the creator already reviewed and accepted
    /// over an off-by-something in a sibling count would be the least useful possible answer.
    /// </summary>
    [Fact]
    public void A_move_past_the_end_puts_the_step_last()
    {
        var loaded = Loaded();
        var first = loaded.Recipe.Recipe.InstructionGroups.Single().Steps.First();

        var plan = RecipeProposalApplication.Plan(
            loaded,
            [new ProposedRecipeChange(
                ProposedRecipeChangeKind.Move, ProposedRecipeTarget.InstructionStep, first.Id, null, null, 99)]);

        Assert.Equal(first.Id, plan.Patch!.Instructions.Value[0].Steps.Last().Id);
    }

    /// <summary>
    /// A step with no text is not a step. The reconciler would happily write an empty one, so the refusal
    /// belongs here, where it can name the change that asked for it.
    /// </summary>
    [Fact]
    public void Clearing_a_steps_text_is_refused()
    {
        var loaded = Loaded();
        var first = loaded.Recipe.Recipe.InstructionGroups.Single().Steps.First();

        var plan = RecipeProposalApplication.Plan(
            loaded, [Set(ProposedRecipeTarget.InstructionStep, first.Id, "text", "   ")]);

        Assert.False(plan.Succeeded);
        Assert.Contains("no text", plan.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_that_is_no_longer_in_the_recipe_is_refused()
    {
        var plan = RecipeProposalApplication.Plan(
            Loaded(), [Set(ProposedRecipeTarget.InstructionStep, Guid.NewGuid(), "text", "Stir.")]);

        Assert.False(plan.Succeeded);
        Assert.Contains("no longer part of the recipe", plan.Error!, StringComparison.Ordinal);
    }

    // ---- tags ----------------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_tag_addition_submits_the_whole_set()
    {
        var plan = RecipeProposalApplication.Plan(
            Loaded(),
            [new ProposedRecipeChange(
                ProposedRecipeChangeKind.Add, ProposedRecipeTarget.Tag, null, null, "autumn", null)]);

        Assert.Equal(
            ["autumn", "weeknight"],
            plan.Patch!.Tags.Value.Select(tag => tag.NormalizedName));
    }

    [Fact]
    public void An_accepted_tag_removal_drops_it_from_the_submitted_set()
    {
        var plan = RecipeProposalApplication.Plan(
            Loaded(),
            [new ProposedRecipeChange(
                ProposedRecipeChangeKind.Remove, ProposedRecipeTarget.Tag, TagId, null, null, null)]);

        Assert.Empty(plan.Patch!.Tags.Value);
    }

    /// <summary>A recipe's tags are a set, so adding one it already has under another spelling changes nothing.</summary>
    [Fact]
    public void Adding_a_tag_the_recipe_already_has_is_a_no_op()
    {
        var plan = RecipeProposalApplication.Plan(
            Loaded(),
            [new ProposedRecipeChange(
                ProposedRecipeChangeKind.Add, ProposedRecipeTarget.Tag, null, null, "Weeknight", null)]);

        Assert.Equal(["weeknight"], plan.Patch!.Tags.Value.Select(tag => tag.NormalizedName));
    }

    [Fact]
    public void Removing_a_tag_the_recipe_does_not_have_is_refused()
    {
        var plan = RecipeProposalApplication.Plan(
            Loaded(),
            [new ProposedRecipeChange(
                ProposedRecipeChangeKind.Remove, ProposedRecipeTarget.Tag, Guid.NewGuid(), null, null, null)]);

        Assert.False(plan.Succeeded);
        Assert.Contains("no longer on the recipe", plan.Error!, StringComparison.Ordinal);
    }

    // ---- the concurrency token -----------------------------------------------------------------------

    /// <summary>
    /// The patch quotes the token of the recipe it was built from, so the merge path's own check passes and the
    /// guard that actually matters — the pinned version id — is the caller's.
    /// </summary>
    [Fact]
    public void The_patch_quotes_the_recipe_it_was_built_from()
    {
        var loaded = Loaded();

        var plan = RecipeProposalApplication.Plan(
            loaded, [Set(ProposedRecipeTarget.Recipe, null, "headnote", "New.")]);

        Assert.Equal(
            RecipeConcurrencyToken.From(loaded.Recipe.Recipe.RowVersion),
            plan.Patch!.ExpectedConcurrencyToken);
    }

    /// <summary>
    /// No reason text. The version records its source and the proposal it came from, which says more exactly
    /// where the edit came from than a sentence would — and writing prose in the creator's voice about an edit
    /// they only approved would be putting words in their mouth.
    /// </summary>
    [Fact]
    public void The_patch_invents_no_reason()
    {
        Assert.Null(Plan(Set(ProposedRecipeTarget.Recipe, null, "headnote", "New.")).Reason);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static readonly Guid TagId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static ProposedRecipeChange Set(
        ProposedRecipeTarget target,
        Guid? targetId,
        string field,
        string? value) =>
        new(ProposedRecipeChangeKind.Set, target, targetId, field, value, null);

    private static CanonicalRecipePatch Plan(params ProposedRecipeChange[] changes)
    {
        var plan = RecipeProposalApplication.Plan(Loaded(), changes);

        Assert.True(plan.Succeeded, plan.Error);

        return plan.Patch!;
    }

    /// <summary>
    /// A recipe with two steps in one group and one tag, which is the smallest shape that can show a reorder, a
    /// removal, and a field left alone.
    /// </summary>
    private static TaggedRecipe Loaded()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        var group = recipe.InstructionGroups.Single();

        group.Title = GroupTitle;
        group.Steps.Add(new RecipeInstructionStep
        {
            Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            RecipeId = recipe.Id,
            RecipeInstructionGroupId = group.Id,
            SortOrder = group.Steps.Max(step => step.SortOrder) + 1,
            Text = "Cool on a rack.",
        });

        recipe.Tags.Clear();
        recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = TagId });

        return new TaggedRecipe(
            RecipeAggregateFixture.Complete(recipe),
            [new WorkspaceTag { Id = TagId, Name = "weeknight", NormalizedName = "weeknight" }]);
    }
}
