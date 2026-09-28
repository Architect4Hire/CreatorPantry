using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The allow-list AIREC-003 rests on: which targets a scope permits, which fields within them, and what
/// happens to an answer that reaches outside either.
/// </summary>
/// <remarks>
/// <para>
/// The restriction this enforces is the capability's whole point: a creator who scoped a revision to one
/// section must get a revision of that section. A proposal that reached further is rejected rather than
/// trimmed, because a trimmed answer is one the creator would review without knowing what was removed.
/// </para>
/// <para>
/// Asserted against the tables directly and then again through the validator, so a rule that held in the
/// table but was never consulted would still fail here.
/// </para>
/// </remarks>
public sealed class AiRevisionScopeTests
{
    // ---- the target allow-list ---------------------------------------------------------------------------

    [Theory]
    [InlineData(AiOperationScope.Metadata, AiChangeTargetKind.Recipe, true)]
    [InlineData(AiOperationScope.Metadata, AiChangeTargetKind.Tag, true)]
    [InlineData(AiOperationScope.Metadata, AiChangeTargetKind.InstructionStep, false)]
    [InlineData(AiOperationScope.Metadata, AiChangeTargetKind.Ingredient, false)]
    [InlineData(AiOperationScope.Instructions, AiChangeTargetKind.InstructionStep, true)]
    [InlineData(AiOperationScope.Instructions, AiChangeTargetKind.InstructionGroup, true)]
    [InlineData(AiOperationScope.Instructions, AiChangeTargetKind.Recipe, false)]
    [InlineData(AiOperationScope.Ingredients, AiChangeTargetKind.Ingredient, true)]
    [InlineData(AiOperationScope.Ingredients, AiChangeTargetKind.IngredientGroup, true)]
    [InlineData(AiOperationScope.Ingredients, AiChangeTargetKind.InstructionStep, false)]
    [InlineData(AiOperationScope.WholeRecipe, AiChangeTargetKind.Recipe, true)]
    [InlineData(AiOperationScope.WholeRecipe, AiChangeTargetKind.Ingredient, true)]
    public void A_scope_permits_only_its_own_targets(
        AiOperationScope scope, AiChangeTargetKind target, bool expected)
    {
        Assert.Equal(expected, AiPolicy.AllowedTargets(scope).Contains(target));
    }

    /// <summary>A scope that means "not about a recipe" permits nothing, the same as an undeclared one.</summary>
    [Theory]
    [InlineData(AiOperationScope.Unspecified)]
    [InlineData(AiOperationScope.NotApplicable)]
    public void A_scope_that_names_no_part_of_a_recipe_permits_nothing(AiOperationScope scope)
    {
        Assert.Empty(AiPolicy.AllowedTargets(scope));
    }

    // ---- the field allow-list ----------------------------------------------------------------------------

    /// <summary>
    /// The gap this list closed. <c>Metadata</c> describes itself as the recipe's framing, but it permits the
    /// <c>Recipe</c> target, and a set on the recipe reaches its working notes and storage guidance too — so
    /// a creator who scoped a revision to metadata could have either rewritten.
    /// </summary>
    [Theory]
    [InlineData("notes")]
    [InlineData("storageNotes")]
    [InlineData("attributionText")]
    public void The_metadata_scope_does_not_reach_a_recipes_working_notes(string field)
    {
        Assert.DoesNotContain(field, AiPolicy.AllowedFields(AiOperationScope.Metadata, AiChangeTargetKind.Recipe));

        // And the wider scope does, which is what makes the narrowing a bound rather than a removal.
        Assert.Contains(field, AiPolicy.AllowedFields(AiOperationScope.WholeRecipe, AiChangeTargetKind.Recipe));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("headnote")]
    [InlineData("prepTimeMinutes")]
    [InlineData("cookTimeMinutes")]
    [InlineData("restTimeMinutes")]
    [InlineData("totalTimeMinutes")]
    [InlineData("yieldText")]
    [InlineData("yieldQuantity")]
    [InlineData("servingCount")]
    [InlineData("servingSize")]
    public void The_metadata_scope_reaches_the_recipes_framing(string field)
    {
        Assert.Contains(field, AiPolicy.AllowedFields(AiOperationScope.Metadata, AiChangeTargetKind.Recipe));
    }

    /// <summary>A scope that does not permit the target permits none of its fields either.</summary>
    [Fact]
    public void A_target_outside_the_scope_carries_no_settable_field()
    {
        Assert.Empty(AiPolicy.AllowedFields(AiOperationScope.Metadata, AiChangeTargetKind.InstructionStep));
        Assert.Empty(AiPolicy.AllowedFields(AiOperationScope.Instructions, AiChangeTargetKind.Recipe));
        Assert.Empty(AiPolicy.AllowedFields(AiOperationScope.Ingredients, AiChangeTargetKind.Recipe));
    }

    /// <summary>
    /// Everywhere but the metadata narrowing, the scope defers to the field vocabulary rather than restating
    /// it — one allow-list, so a field added there is reachable the day it ships.
    /// </summary>
    [Theory]
    [InlineData(AiOperationScope.Instructions, AiChangeTargetKind.InstructionStep)]
    [InlineData(AiOperationScope.Ingredients, AiChangeTargetKind.Ingredient)]
    [InlineData(AiOperationScope.WholeRecipe, AiChangeTargetKind.InstructionStep)]
    public void An_unnarrowed_scope_defers_to_the_field_vocabulary(
        AiOperationScope scope, AiChangeTargetKind target)
    {
        Assert.Equal(
            AiDiffFields.For(target).OrderBy(field => field, StringComparer.Ordinal),
            AiPolicy.AllowedFields(scope, target).OrderBy(field => field, StringComparer.Ordinal));
    }

    // ---- which scopes a revision can be asked in ---------------------------------------------------------

    /// <summary>
    /// Derived from the applicability table rather than listed, so a picker cannot offer a section in which
    /// every change would be refused. Media is absent because no change to an asset link can be applied.
    /// </summary>
    [Fact]
    public void Only_the_scopes_with_something_applicable_in_them_can_be_revised()
    {
        Assert.Equal(
            [AiOperationScope.WholeRecipe, AiOperationScope.Ingredients, AiOperationScope.Instructions, AiOperationScope.Metadata],
            AiRevisionScopes.Revisable);
    }

    [Fact]
    public void Every_revisable_scope_really_has_something_applicable_in_it()
    {
        Assert.All(AiRevisionScopes.Revisable, scope =>
            Assert.Contains(
                AiPolicy.AllowedTargets(scope),
                target => AiChangeApplicability.For(target).Count > 0));
    }

    [Theory]
    [InlineData(AiOperationScope.Media)]
    [InlineData(AiOperationScope.NotApplicable)]
    [InlineData(AiOperationScope.Unspecified)]
    public void A_scope_nothing_can_be_applied_in_is_not_revisable(AiOperationScope scope)
    {
        Assert.False(AiRevisionScopes.IsRevisable(scope));
    }

    // ---- enforcement -------------------------------------------------------------------------------------

    private static AiOutputValidationResult Validate(AiOperationScope scope, params AiOutputChange[] changes)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "recipe.revision.v1",
            changes = changes.Select(change => new
            {
                changeKind = change.ChangeKind.ToString(),
                targetKind = change.TargetKind.ToString(),
                targetId = change.TargetId,
                fieldName = change.FieldName,
                afterValue = change.AfterValue,
                proposedPosition = change.ProposedPosition,
            }),
            warnings = Array.Empty<object>(),
        });

        return AiOutputValidator.Validate(payload, "recipe.revision.v1", scope);
    }

    private static AiOutputChange Set(AiChangeTargetKind target, string field, Guid? targetId = null) =>
        new()
        {
            ChangeKind = AiChangeKind.Set,
            TargetKind = target,
            TargetId = targetId,
            FieldName = field,
            AfterValue = "a value",
        };

    /// <summary>The out-of-scope attack, at the target level: a metadata revision reaching into the method.</summary>
    [Fact]
    public void A_change_to_a_target_outside_the_scope_is_rejected()
    {
        var result = Validate(
            AiOperationScope.Metadata,
            Set(AiChangeTargetKind.InstructionStep, "text", Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.OutsideScope, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// And at the field level, which the target check alone could not catch: a metadata revision rewriting the
    /// creator's storage notes through a target the scope does permit.
    /// </summary>
    [Fact]
    public void A_change_to_a_field_outside_the_scope_is_rejected()
    {
        var result = Validate(AiOperationScope.Metadata, Set(AiChangeTargetKind.Recipe, "storageNotes"));

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.OutsideScope, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// Not correctable: a corrective re-ask spends a creator's budget arriving at the same refusal, and a
    /// model that reached outside a bound it was told has already been told it.
    /// </summary>
    [Fact]
    public void An_out_of_scope_answer_is_not_re_asked()
    {
        var result = Validate(AiOperationScope.Metadata, Set(AiChangeTargetKind.Recipe, "notes"));

        Assert.False(result.Succeeded);
        Assert.False(result.Failure!.IsCorrectableByReprompt);
    }

    /// <summary>
    /// One change in scope and one outside it fails the whole answer. Keeping the valid half would hand the
    /// creator a diff that is not the one the model proposed.
    /// </summary>
    [Fact]
    public void One_out_of_scope_change_rejects_the_whole_answer()
    {
        var result = Validate(
            AiOperationScope.Metadata,
            Set(AiChangeTargetKind.Recipe, "title"),
            Set(AiChangeTargetKind.Recipe, "storageNotes"));

        Assert.False(result.Succeeded);
        Assert.Equal(AiOutputReason.OutsideScope, result.Failure!.ReasonCode);
    }

    /// <summary>A field nothing recognises is refused whatever the scope.</summary>
    [Fact]
    public void A_field_that_is_not_a_recipe_field_at_all_is_rejected()
    {
        var result = Validate(AiOperationScope.WholeRecipe, Set(AiChangeTargetKind.Recipe, "sourceUrl"));

        Assert.False(result.Succeeded);
    }

    // ---- what the scope permits ---------------------------------------------------------------------------

    [Fact]
    public void A_multi_field_revision_within_one_scope_is_accepted()
    {
        var result = Validate(
            AiOperationScope.Metadata,
            Set(AiChangeTargetKind.Recipe, "title"),
            Set(AiChangeTargetKind.Recipe, "description"),
            Set(AiChangeTargetKind.Recipe, "headnote"));

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal(3, result.Document!.Changes.Count);
    }

    [Fact]
    public void An_ingredient_revision_is_accepted_now_that_one_can_be_applied()
    {
        var line = Guid.NewGuid();

        var result = Validate(
            AiOperationScope.Ingredients,
            Set(AiChangeTargetKind.Ingredient, "displayText", line),
            Set(AiChangeTargetKind.Ingredient, "quantity", line));

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    /// <summary>
    /// "I looked and there is nothing to change" is a complete answer, and reporting it as a failure would
    /// tell the creator something untrue.
    /// </summary>
    [Fact]
    public void A_revision_that_changes_nothing_is_a_valid_answer()
    {
        var result = Validate(AiOperationScope.Metadata);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Empty(result.Document!.Changes);
    }
}
