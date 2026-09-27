using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The boundary AIREC-001's answer crosses, mirroring <see cref="AiOutputValidatorTests"/>'s structure over
/// <see cref="AiConceptOutputValidator"/>'s own document and its own domain rules. The schema-validity and
/// refusal-safety cases the eval harness's fixtures already cover (minimum count, duplicate titles, an
/// unknown field) are not repeated here; this covers what only a direct test conveniently reaches: hygiene
/// bounds, the "not specified" defaulting contract, and warning-index checking.
/// </summary>
public sealed class AiConceptOutputValidatorTests
{
    private const string Schema = "fixture.concepts.v1";

    [Fact]
    public void A_sound_answer_validates_into_its_document()
    {
        var result = Validate(TwoConcepts());

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal(2, result.Document!.Concepts.Count);
        Assert.Equal("Mapo Tofu Reimagined", result.Document.Concepts[0].Title);
    }

    [Fact]
    public void Omitted_optional_fields_default_to_empty_rather_than_failing()
    {
        var result = Validate(TwoConcepts());

        var concept = result.Document!.Concepts[0];
        Assert.Empty(concept.Assumptions);
        Assert.Empty(concept.SuggestedIngredients);
        Assert.Empty(concept.DietaryNotes);
        Assert.Null(concept.TimeBudgetNote);
        Assert.Null(concept.SkillLevelFit);
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
        var result = Validate($$"""{ "schemaVersion": "wrong.version", "concepts": "not even an array" }""");

        AssertFailed(result, AiOutputReason.SchemaVersionMismatch);
    }

    // ---- shape ------------------------------------------------------------------------------------------

    [Fact]
    public void An_unknown_property_is_refused_rather_than_ignored()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "concepts": [
                { "title": "A", "summary": "s", "distinctnessRationale": "r", "estimatedCalories": 400 },
                { "title": "B", "summary": "s", "distinctnessRationale": "r" }
              ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.UnknownField);
    }

    // ---- hygiene ------------------------------------------------------------------------------------------

    [Fact]
    public void A_field_longer_than_the_bound_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "concepts": [
                { "title": "{{new string('x', AiPolicy.ConceptFieldMaxLength + 1)}}", "summary": "s", "distinctnessRationale": "r" },
                { "title": "B", "summary": "s", "distinctnessRationale": "r" }
              ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.ValueTooLong);
    }

    [Fact]
    public void A_control_character_in_a_string_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "concepts": [
                { "title": "A\u0007", "summary": "s", "distinctnessRationale": "r" },
                { "title": "B", "summary": "s", "distinctnessRationale": "r" }
              ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.IllegalCharacters);
    }

    /// <summary>
    /// A list packed with more entries than <see cref="AiPolicy.ConceptListMaxItems"/> allows is refused here,
    /// before the handler ever joins it into one string long enough to exceed <c>AiPolicy.ChangeValueMaxLength</c>
    /// as a raw SQL truncation error.
    /// </summary>
    [Fact]
    public void More_list_items_than_the_bound_is_refused()
    {
        var items = string.Join(",", Enumerable.Range(0, AiPolicy.ConceptListMaxItems + 1).Select(i => $"\"item {i}\""));

        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "concepts": [
                { "title": "A", "summary": "s", "distinctnessRationale": "r", "assumptions": [{{items}}] },
                { "title": "B", "summary": "s", "distinctnessRationale": "r" }
              ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.ValueTooLong);
    }

    // ---- domain -------------------------------------------------------------------------------------------

    [Fact]
    public void More_concepts_than_the_ceiling_is_refused()
    {
        var concepts = string.Join(
            ",",
            Enumerable.Range(0, AiPolicy.MaxConceptCount + 1)
                .Select(i => $$"""{ "title": "Concept {{i}}", "summary": "s", "distinctnessRationale": "r" }"""));

        AssertFailed(
            Validate($$"""{ "schemaVersion": "{{Schema}}", "concepts": [{{concepts}}] }"""),
            AiOutputReason.ConceptCountOutOfRange);
    }

    [Fact]
    public void A_blank_title_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "concepts": [
                { "title": "   ", "summary": "s", "distinctnessRationale": "r" },
                { "title": "B", "summary": "s", "distinctnessRationale": "r" }
              ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.ConceptFieldMissing);
    }

    [Fact]
    public void A_warning_pointing_at_a_concept_that_does_not_exist_is_refused()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "concepts": [
                { "title": "A", "summary": "s", "distinctnessRationale": "r" },
                { "title": "B", "summary": "s", "distinctnessRationale": "r" }
              ],
              "warnings": [ { "kind": "Assumption", "message": "out of range", "conceptIndex": 5 } ]
            }
            """;

        AssertFailed(Validate(payload), AiOutputReason.WarningIndexInvalid);
    }

    [Fact]
    public void A_whole_answer_warning_with_no_concept_index_is_accepted()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "concepts": [
                { "title": "A", "summary": "s", "distinctnessRationale": "r" },
                { "title": "B", "summary": "s", "distinctnessRationale": "r" }
              ],
              "warnings": [ { "kind": "Assumption", "message": "No exclusions were named." } ]
            }
            """;

        var result = Validate(payload);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Null(Assert.Single(result.Document!.Warnings).ConceptIndex);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string TwoConcepts() => $$"""
        {
          "schemaVersion": "{{Schema}}",
          "concepts": [
            { "title": "Mapo Tofu Reimagined", "summary": "A spicier take.", "distinctnessRationale": "Uses fermented broad bean paste." },
            { "title": "Dan Dan Noodle Bowl", "summary": "A quicker noodle dish.", "distinctnessRationale": "Cold-tossed rather than braised." }
          ]
        }
        """;

    private static AiOutputValidationOutcome<AiConceptOutputDocument> Validate(string? payload) =>
        AiConceptOutputValidator.Validate(payload, Schema);

    private static void AssertFailed(AiOutputValidationOutcome<AiConceptOutputDocument> result, string reasonCode)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.Document);
        Assert.Equal(reasonCode, result.Failure!.ReasonCode);
    }
}
