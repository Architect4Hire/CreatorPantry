using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The boundary AIREC-002's answer crosses, mirroring <see cref="AiConceptOutputValidatorTests"/>'s structure
/// over <see cref="AiRecipeDraftOutputValidator"/>'s own document and its own domain rules. The schema-validity
/// cases the eval harness's fixtures cover (a minimal valid draft, an unknown field, an empty group) are not
/// repeated exhaustively here; this covers what a direct test conveniently reaches: hygiene bounds, the
/// nested-list count ceilings, and the domain rules the type system cannot state.
/// </summary>
public sealed class AiRecipeDraftOutputValidatorTests
{
    private const string Schema = "fixture.recipe-draft.v1";

    [Fact]
    public void A_sound_answer_validates_into_its_document()
    {
        var result = Validate(MinimalDraft());

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal("Weeknight Mapo Tofu", result.Document!.Title);
        Assert.Single(result.Document.IngredientGroups);
        Assert.Single(result.Document.Instructions);
    }

    [Fact]
    public void Omitted_optional_sections_default_to_empty_or_null_rather_than_failing()
    {
        var result = Validate(MinimalDraft());

        var document = result.Document!;
        Assert.Null(document.Description);
        Assert.Null(document.Yield);
        Assert.Null(document.Timing);
        Assert.Empty(document.Equipment);
        Assert.Empty(document.UnresolvedQuestions);
        Assert.Empty(document.Warnings);
    }

    // ---- payload bounds --------------------------------------------------------------------------------

    [Fact]
    public void A_blank_payload_is_refused_as_correctable()
    {
        var result = Validate("   ");

        AssertFailed(result, AiOutputReason.EmptyPayload);
        Assert.True(result.Failure!.IsCorrectableByReprompt);
    }

    [Fact]
    public void Malformed_json_is_refused()
    {
        AssertFailed(Validate("not json at all"), AiOutputReason.MalformedJson);
    }

    [Fact]
    public void A_schema_version_mismatch_is_refused_before_any_shape_complaint()
    {
        var result = Validate("""{ "schemaVersion": "wrong.version", "title": 1 }""");

        AssertFailed(result, AiOutputReason.SchemaVersionMismatch);
    }

    // ---- shape ------------------------------------------------------------------------------------------

    [Fact]
    public void An_unknown_property_is_refused_rather_than_ignored()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu", "measurementUnitId": "11111111-1111-1111-1111-111111111111" } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.UnknownField);
    }

    // ---- hygiene ------------------------------------------------------------------------------------------

    [Fact]
    public void A_title_longer_than_the_bound_is_refused()
    {
        var payload = Draft(title: new string('x', AiPolicy.RecipeDraftTitleMaxLength + 1));

        AssertFailed(Validate(payload), AiOutputReason.ValueTooLong);
    }

    [Fact]
    public void A_control_character_in_a_string_is_refused()
    {
        // The literal two-character JSON escape "\u0007", not the control byte itself -- a raw control byte
        // embedded straight into the payload is not valid JSON at all, and would be caught one stage earlier
        // as malformed JSON rather than exercising this stage's own control-character check.
        var payload = Draft(title: "Weeknight Mapo Tofu\\u0007");

        AssertFailed(Validate(payload), AiOutputReason.IllegalCharacters);
    }

    [Fact]
    public void More_ingredient_lines_in_one_group_than_the_bound_is_refused()
    {
        var lines = string.Join(
            ",", Enumerable.Range(0, AiPolicy.MaxIngredientLinesPerGroup + 1).Select(i => $$"""{ "displayText": "Item {{i}}" }"""));

        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ {{lines}} ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.RecipeDraftTooManyItems);
    }

    // ---- domain -------------------------------------------------------------------------------------------

    [Fact]
    public void A_blank_title_is_refused()
    {
        AssertFailed(Validate(Draft(title: "   ")), AiOutputReason.RecipeDraftFieldMissing);
    }

    [Fact]
    public void No_ingredient_groups_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.RecipeDraftGroupCountOutOfRange);
    }

    [Fact]
    public void An_ingredient_group_with_no_lines_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.RecipeDraftEmptyGroup);
    }

    [Fact]
    public void An_instruction_group_with_no_steps_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu" } ] } ],
              "instructions": [ { "steps": [] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.RecipeDraftEmptyGroup);
    }

    [Fact]
    public void A_negative_quantity_is_refused_rather_than_silently_kept()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu", "quantity": -2 } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.DomainInvalid);
    }

    [Fact]
    public void A_quantity_range_whose_upper_bound_does_not_exceed_its_lower_bound_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu", "quantity": 3, "quantityUpper": 2 } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.DomainInvalid);
    }

    [Fact]
    public void A_negative_prep_time_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "timing": { "prepTimeMinutes": -5 },
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu" } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.DomainInvalid);
    }

    [Fact]
    public void An_optional_group_flag_round_trips()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "title": "Optional garnish", "isOptional": true, "ingredients": [ { "displayText": "Scallions" } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ]
            }
            """;

        var result = Validate(payload);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.True(result.Document!.IngredientGroups[0].IsOptional);
    }

    [Fact]
    public void An_unresolved_question_and_a_warning_both_round_trip()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu" } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ],
              "unresolvedQuestions": [ "What pan size?" ],
              "warnings": [ { "kind": "Assumption", "message": "Assumed a weeknight time budget." } ]
            }
            """;

        var result = Validate(payload);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal("What pan size?", Assert.Single(result.Document!.UnresolvedQuestions));
        Assert.Equal(AiWarningKind.Assumption, Assert.Single(result.Document.Warnings).Kind);
    }

    [Fact]
    public void More_warnings_than_the_bound_is_refused()
    {
        var warnings = string.Join(
            ",", Enumerable.Range(0, AiPolicy.MaxRecipeDraftWarnings + 1).Select(i => $$"""{ "kind": "Assumption", "message": "Note {{i}}" }"""));

        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu" } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ],
              "warnings": [ {{warnings}} ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.RecipeDraftTooManyItems);
    }

    [Fact]
    public void A_warning_with_an_undeclared_kind_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "title": "Weeknight Mapo Tofu",
              "ingredientGroups": [ { "ingredients": [ { "displayText": "Tofu" } ] } ],
              "instructions": [ { "steps": [ { "text": "Simmer." } ] } ],
              "warnings": [ { "kind": "Unspecified", "message": "Note" } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.KindNotDeclared);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string MinimalDraft() => Draft();

    private static string Draft(string title = "Weeknight Mapo Tofu") => $$"""
        {
          "schemaVersion": "{{Schema}}",
          "title": "{{title}}",
          "ingredientGroups": [ { "ingredients": [ { "displayText": "1 block silken tofu, cubed" } ] } ],
          "instructions": [ { "steps": [ { "text": "Simmer until heated through." } ] } ]
        }
        """;

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument> Validate(string? payload) =>
        AiRecipeDraftOutputValidator.Validate(payload, Schema);

    private static void AssertFailed(AiOutputValidationOutcome<AiRecipeDraftOutputDocument> result, string reasonCode)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.Document);
        Assert.Equal(reasonCode, result.Failure!.ReasonCode);
    }
}
