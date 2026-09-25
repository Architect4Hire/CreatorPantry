using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The server-calculated diff. Covers the five cases 8.11 names — forged before, missing field, reorder,
/// duplicate change, stale source — the last of which is the caller's check and is asserted where it lives.
/// </summary>
/// <remarks>
/// Rewritten in 8.12 when the diff stopped offering changes nobody could accept. Ingredients were the whole
/// subject of several of these tests, and they are now refused outright; the cases they covered are the same
/// cases, asked of an instruction step instead. <see cref="AiChangeApplicabilityTests"/> covers the gate that
/// replaced them.
/// </remarks>
public sealed class AiDiffCalculatorTests
{
    private static readonly Guid GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IngredientId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StepGroupId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StepId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SecondStepId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid TagId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // ---- before values come from the source -----------------------------------------------------------

    [Fact]
    public void A_set_reads_its_before_value_from_the_pinned_version()
    {
        var result = Calculate(Set(AiChangeTargetKind.Recipe, null, "headnote", "A warmer opening."));

        Assert.True(result.Succeeded, result.Failure?.Message);

        var change = Assert.Single(result.Changes!);
        Assert.Equal("The original headnote.", change.BeforeValue);
        Assert.Equal("A warmer opening.", change.AfterValue);
    }

    [Fact]
    public void A_set_on_a_child_reads_that_child()
    {
        var result = Calculate(Set(AiChangeTargetKind.InstructionStep, StepId, "text", "Mix thoroughly."));

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal("Mix.", Assert.Single(result.Changes!).BeforeValue);
    }

    /// <summary>
    /// A field the source leaves empty has a null before value, which is a real answer and not a missing one.
    /// </summary>
    [Fact]
    public void An_empty_field_has_a_null_before_value()
    {
        var result = Calculate(Set(AiChangeTargetKind.Recipe, null, "notes", "Some notes."));

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Null(Assert.Single(result.Changes!).BeforeValue);
    }

    /// <summary>Numbers format invariantly, so a before and after compare like for like.</summary>
    [Theory]
    [InlineData("prepTimeMinutes", "15")]
    [InlineData("yieldQuantity", "8")]
    public void Numeric_before_values_are_formatted_invariantly(string field, string expected)
    {
        var result = Calculate(Set(AiChangeTargetKind.Recipe, null, field, "20"));

        Assert.Equal(expected, Assert.Single(result.Changes!).BeforeValue);
    }

    // ---- forged before --------------------------------------------------------------------------------

    /// <summary>
    /// The contract gives a model nowhere to put a before value, so the only way to test forgery is to confirm
    /// the calculator reads the source regardless of anything the answer contains. A forged claim cannot
    /// survive because it is never consulted.
    /// </summary>
    [Fact]
    public void A_before_value_is_never_taken_from_the_model()
    {
        Assert.DoesNotContain(
            typeof(AiOutputChange).GetProperties(),
            property => property.Name.Contains("Before", StringComparison.OrdinalIgnoreCase));

        // And the resolved change's before value equals the source, not anything the model said.
        var result = Calculate(Set(AiChangeTargetKind.Recipe, null, "title", "Focaccia, Improved"));

        Assert.Equal("Focaccia", Assert.Single(result.Changes!).BeforeValue);
    }

    // ---- missing field --------------------------------------------------------------------------------

    [Fact]
    public void A_field_that_is_not_settable_on_that_kind_is_refused()
    {
        var result = Calculate(Set(AiChangeTargetKind.InstructionStep, StepId, "headnote", "x"));

        AssertFailed(result, AiOutputReason.FieldNameMisplaced);
        Assert.Contains("Settable:", result.Failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model naming a Guid is a model inventing an identifier. Reference resolution belongs to the matching
    /// seam, and changing a unit changes what a quantity means.
    /// </summary>
    [Theory]
    [InlineData(AiChangeTargetKind.InstructionStep, "techniqueId")]
    [InlineData(AiChangeTargetKind.InstructionStep, "temperatureUnitId")]
    [InlineData(AiChangeTargetKind.Recipe, "cuisineId")]
    [InlineData(AiChangeTargetKind.Recipe, "yieldUnitId")]
    public void No_identifier_field_is_ever_settable(AiChangeTargetKind kind, string field)
    {
        Assert.False(AiDiffFields.IsSettable(kind, field));
        AssertFailed(Calculate(Set(kind, TargetFor(kind), field, Guid.NewGuid().ToString())),
            AiOutputReason.FieldNameMisplaced);
    }

    [Fact]
    public void No_settable_field_anywhere_is_an_identifier()
    {
        foreach (var kind in Enum.GetValues<AiChangeTargetKind>())
        {
            Assert.DoesNotContain(
                AiDiffFields.For(kind),
                field => field.EndsWith("Id", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_target_the_pinned_version_does_not_contain_is_refused()
    {
        var result = Calculate(Set(AiChangeTargetKind.InstructionStep, Guid.NewGuid(), "text", "Mix."));

        AssertFailed(result, AiOutputReason.TargetNotInSource);
    }

    /// <summary>
    /// "A quarter of an hour" is not a number. Accepting it would either store nonsense or force every later
    /// calculation to re-parse it, and the creator would discover the problem at acceptance.
    /// </summary>
    [Theory]
    [InlineData(AiChangeTargetKind.Recipe, "prepTimeMinutes", "quarter of an hour")]
    [InlineData(AiChangeTargetKind.InstructionStep, "durationMinutes", "a good while")]
    [InlineData(AiChangeTargetKind.InstructionStep, "temperatureValue", "hot")]
    public void A_value_the_field_cannot_hold_is_refused(
        AiChangeTargetKind kind,
        string field,
        string value) =>
        AssertFailed(
            Calculate(Set(kind, TargetFor(kind), field, value)),
            AiOutputReason.DomainInvalid);

    [Fact]
    public void Clearing_a_field_is_allowed()
    {
        var result = Calculate(Set(AiChangeTargetKind.Recipe, null, "description", null));

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Null(Assert.Single(result.Changes!).AfterValue);
    }

    // ---- applicability --------------------------------------------------------------------------------

    /// <summary>
    /// The gate 8.12 added, and the reason it exists: a creator must never review a change, accept it, and then
    /// be told the system has no way to apply it.
    /// </summary>
    [Theory]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.Ingredient)]
    [InlineData(AiChangeKind.Remove, AiChangeTargetKind.Ingredient)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.IngredientGroup)]
    [InlineData(AiChangeKind.Remove, AiChangeTargetKind.Equipment)]
    [InlineData(AiChangeKind.Move, AiChangeTargetKind.AssetLink)]
    public void A_change_the_recipe_seam_cannot_apply_is_refused(
        AiChangeKind kind,
        AiChangeTargetKind target)
    {
        var result = Calculate(new AiOutputChange
        {
            ChangeKind = kind,
            TargetKind = target,
            TargetId = TargetFor(target),
            FieldName = kind is AiChangeKind.Set ? "displayText" : null,
            AfterValue = "something",
        });

        AssertFailed(result, AiOutputReason.NotApplicable);
    }

    /// <summary>
    /// An added instruction step is refused for a structural reason, not a policy one: a change row carries one
    /// value, and a step is text, a note, a duration and a temperature.
    /// </summary>
    [Fact]
    public void An_added_instruction_step_is_refused()
    {
        var result = Calculate(new AiOutputChange
        {
            ChangeKind = AiChangeKind.Add,
            TargetKind = AiChangeTargetKind.InstructionStep,
            TargetId = StepGroupId,
            AfterValue = "Rest for ten minutes.",
            ProposedPosition = 2,
        });

        AssertFailed(result, AiOutputReason.NotApplicable);
    }

    // ---- reorder --------------------------------------------------------------------------------------

    [Fact]
    public void A_move_within_the_source_resolves()
    {
        var result = Calculate(new AiOutputChange
        {
            ChangeKind = AiChangeKind.Move,
            TargetKind = AiChangeTargetKind.InstructionStep,
            TargetId = StepId,
            ProposedPosition = 1,
        });

        Assert.True(result.Succeeded, result.Failure?.Message);

        var change = Assert.Single(result.Changes!);
        Assert.Equal(1, change.ProposedPosition);

        // A move reads no field, so there is no before value to read either.
        Assert.Null(change.BeforeValue);
        Assert.Null(change.FieldName);
    }

    [Fact]
    public void A_move_past_the_end_is_refused()
    {
        var result = Calculate(new AiOutputChange
        {
            ChangeKind = AiChangeKind.Move,
            TargetKind = AiChangeTargetKind.InstructionStep,
            TargetId = StepId,
            ProposedPosition = 99,
        });

        AssertFailed(result, AiOutputReason.PositionMisplaced);
    }

    [Fact]
    public void A_move_of_something_absent_is_refused()
    {
        var result = Calculate(new AiOutputChange
        {
            ChangeKind = AiChangeKind.Move,
            TargetKind = AiChangeTargetKind.InstructionStep,
            TargetId = Guid.NewGuid(),
            ProposedPosition = 0,
        });

        AssertFailed(result, AiOutputReason.TargetNotInSource);
    }

    /// <summary>
    /// A tag is the one child a change row can add, because a tag's whole content is its name. The row it adds
    /// does not exist yet, so an addition is the one kind that resolves without an existing target.
    /// </summary>
    [Fact]
    public void An_added_tag_resolves_without_an_existing_row()
    {
        var result = Calculate(new AiOutputChange
        {
            ChangeKind = AiChangeKind.Add,
            TargetKind = AiChangeTargetKind.Tag,
            AfterValue = "focaccia",
        });

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal("focaccia", Assert.Single(result.Changes!).AfterValue);
    }

    /// <summary>Review order is preserved, which is the order the model offered the changes in.</summary>
    [Fact]
    public void Sort_order_follows_the_order_the_model_offered()
    {
        var result = Calculate(
            Set(AiChangeTargetKind.Recipe, null, "title", "One"),
            Set(AiChangeTargetKind.Recipe, null, "headnote", "Two"),
            Set(AiChangeTargetKind.InstructionStep, StepId, "text", "Three"));

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal([0, 1, 2], result.Changes!.Select(change => change.SortOrder));
    }

    // ---- duplicate change ------------------------------------------------------------------------------

    [Fact]
    public void Two_changes_on_one_field_are_refused()
    {
        var result = Calculate(
            Set(AiChangeTargetKind.Recipe, null, "title", "One"),
            Set(AiChangeTargetKind.Recipe, null, "title", "Two"));

        AssertFailed(result, AiOutputReason.ConflictingChanges);
    }

    [Fact]
    public void Two_changes_on_one_row_are_refused()
    {
        var result = Calculate(
            new AiOutputChange
            {
                ChangeKind = AiChangeKind.Remove,
                TargetKind = AiChangeTargetKind.InstructionStep,
                TargetId = StepId,
                AfterValue = null,
                ProposedPosition = null,
            },
            new AiOutputChange
            {
                ChangeKind = AiChangeKind.Move,
                TargetKind = AiChangeTargetKind.InstructionStep,
                TargetId = StepId,
                ProposedPosition = 0,
            });

        AssertFailed(result, AiOutputReason.ConflictingChanges);
    }

    [Fact]
    public void Two_different_fields_on_one_row_are_allowed()
    {
        var result = Calculate(
            Set(AiChangeTargetKind.InstructionStep, StepId, "text", "Mix well."),
            Set(AiChangeTargetKind.InstructionStep, StepId, "note", "Use a dough hook."));

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal(2, result.Changes!.Count);
    }

    // ---- empty ----------------------------------------------------------------------------------------

    /// <summary>"Nothing needs changing" survives the diff as an empty diff, not as a failure.</summary>
    [Fact]
    public void An_answer_with_no_changes_produces_an_empty_diff()
    {
        var result = AiDiffCalculator.Calculate(Source(), new AiOutputDocument { SchemaVersion = "v1" });

        Assert.True(result.Succeeded);
        Assert.Empty(result.Changes!);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static Guid? TargetFor(AiChangeTargetKind kind) => kind switch
    {
        AiChangeTargetKind.Recipe => null,
        AiChangeTargetKind.Ingredient => IngredientId,
        AiChangeTargetKind.InstructionStep => StepId,
        AiChangeTargetKind.InstructionGroup => StepGroupId,
        AiChangeTargetKind.Tag => TagId,
        _ => GroupId,
    };

    private static AiOutputChange Set(
        AiChangeTargetKind kind,
        Guid? targetId,
        string field,
        string? afterValue) =>
        new()
        {
            ChangeKind = AiChangeKind.Set,
            TargetKind = kind,
            TargetId = targetId,
            FieldName = field,
            AfterValue = afterValue,
        };

    private static AiDiffResult Calculate(params AiOutputChange[] changes) =>
        AiDiffCalculator.Calculate(
            Source(),
            new AiOutputDocument { SchemaVersion = "v1", Changes = changes });

    private static void AssertFailed(AiDiffResult result, string reasonCode)
    {
        Assert.False(result.Succeeded, "expected a failure but the diff resolved");
        Assert.Null(result.Changes);
        Assert.Equal(reasonCode, result.Failure!.ReasonCode);

        // A diff failure is never worth re-asking for: the model addressed content that is not there, or said
        // something the field cannot hold, and asking again does not change either.
        Assert.False(result.Failure.IsCorrectableByReprompt);
    }

    /// <summary>
    /// A small pinned version: one ingredient group with one ingredient, one step group with two steps, one tag.
    /// </summary>
    /// <remarks>
    /// The ingredient stays in the fixture although no change may address one any more. It is what proves the
    /// refusal is a rule rather than a missing row — a diff that refused because the ingredient was not there
    /// would pass the applicability tests for the wrong reason.
    /// </remarks>
    private static RecipeSnapshotDocument Source() => new()
    {
        SchemaVersion = 1,
        Recipe = new RecipeSnapshotHeader
        {
            Title = "Focaccia",
            Headnote = "The original headnote.",
            Description = "A description.",
            PrepTimeMinutes = 15,
            YieldQuantity = 8,
        },
        IngredientGroups =
        [
            new RecipeSnapshotIngredientGroup
            {
                Id = GroupId,
                Title = "Dough",
                Ingredients =
                [
                    new RecipeSnapshotIngredient
                    {
                        Id = IngredientId,
                        DisplayText = "2 cups flour",
                        Quantity = 2,
                        IsOptional = false,
                    },
                ],
            },
        ],
        InstructionGroups =
        [
            new RecipeSnapshotInstructionGroup
            {
                Id = StepGroupId,
                Title = "Method",
                Steps =
                [
                    new RecipeSnapshotInstructionStep { Id = StepId, Text = "Mix.", SortOrder = 0 },
                    new RecipeSnapshotInstructionStep
                    {
                        Id = SecondStepId,
                        Text = "Bake.",
                        SortOrder = 1,
                    },
                ],
            },
        ],
        Tags = [new RecipeSnapshotTag { WorkspaceTagId = TagId }],
    };
}
