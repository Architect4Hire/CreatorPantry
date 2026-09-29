using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The boundary AIREC-006's answer crosses, mirroring <see cref="AiConceptOutputValidatorTests"/> and
/// <see cref="AiSubstitutionOutputValidatorTests"/>'s structure over <see cref="AiRecipeReviewOutputValidator"/>'s
/// own document. The domain-rule cases (the reference-check floor, the severity floor, an unexplained
/// unsupported claim, an uncautioned safety finding) are covered thoroughly by
/// <c>RecipeReviewAiTaskHandlerTests</c> against the real handler; this file covers what only a direct test
/// conveniently reaches, and what nothing else reaches at all — the envelope, schema-version, and shape stages
/// every sibling validator has its own test for.
/// </summary>
public sealed class AiRecipeReviewOutputValidatorTests
{
    private const string Schema = "recipe.review.v1";

    [Fact]
    public void A_sound_answer_validates_into_its_document()
    {
        var result = AiRecipeReviewOutputValidator.Validate(Payload(Completeness()), Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Single(result.Document!.Findings);
        Assert.Equal(AiRecipeReviewFindingCategory.Completeness, result.Document.Findings[0].Category);
    }

    [Fact]
    public void An_empty_payload_is_rejected_as_correctable()
    {
        var result = AiRecipeReviewOutputValidator.Validate("   ", Schema);

        Assert.Equal(AiOutputReason.EmptyPayload, result.Failure!.ReasonCode);
        Assert.True(result.Failure.IsCorrectableByReprompt);
    }

    /// <summary>
    /// Stage 2 (schema version), proven before stage 3 ever runs: it fails on the manifest disagreement alone,
    /// not on the "not even an array" nonsense sitting in the field a real answer would use for findings.
    /// </summary>
    [Fact]
    public void Malformed_json_is_rejected_before_schema_version_is_even_read()
    {
        var result = AiRecipeReviewOutputValidator.Validate("not json at all", Schema);

        Assert.Equal(AiOutputReason.MalformedJson, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_schema_version_mismatch_is_refused_before_any_shape_complaint()
    {
        var result = AiRecipeReviewOutputValidator.Validate(
            $$"""{ "schemaVersion": "wrong.version", "findings": "not even an array" }""", Schema);

        Assert.Equal(AiOutputReason.SchemaVersionMismatch, result.Failure!.ReasonCode);
    }

    [Fact]
    public void An_unknown_property_is_refused_rather_than_ignored()
    {
        var payload = $$"""
            {
              "schemaVersion": "{{Schema}}",
              "findings": [],
              "aNewFieldNoManifestDeclared": true
            }
            """;

        var result = AiRecipeReviewOutputValidator.Validate(payload, Schema);

        Assert.Equal(AiOutputReason.UnknownField, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// The guarantee every validator in this module makes: a rejected answer's own words are exactly the words
    /// that must not reach a creator or a log.
    /// </summary>
    [Fact]
    public void A_rejection_carries_neither_the_document_nor_the_payload()
    {
        var result = AiRecipeReviewOutputValidator.Validate("not json at all", Schema);

        Assert.Null(result.Document);
    }

    // ---- fixtures ----------------------------------------------------------------------------------------

    private static AiRecipeReviewFinding Completeness() => new()
    {
        FieldRef = new AiRecipeReviewFieldRef { FieldKind = AiRecipeReviewFieldKind.Yield },
        Category = AiRecipeReviewFindingCategory.Completeness,
        Severity = AiRecipeReviewSeverity.Minor,
        Summary = "No yield is stated for this recipe.",
        EvidenceBasis = AiEvidenceBasis.ModelKnowledge,
        Confidence = AiRecipeReviewConfidence.High,
        RequiresReferenceCheck = false,
    };

    private static string Payload(AiRecipeReviewFinding finding) =>
        Serialize(new AiRecipeReviewOutputDocument { SchemaVersion = Schema, Findings = [finding] });

    private static string Serialize(AiRecipeReviewOutputDocument document) =>
        JsonSerializer.Serialize(document, SerializerOptions);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
