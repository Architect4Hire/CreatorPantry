using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-004's answer, held to its contract.
/// </summary>
/// <remarks>
/// The interesting half is stage 5. The earlier stages check that an answer is well formed, which every
/// validator in this module does; these check that it is honest — that alternatives are ranked, that
/// confidence is not claimed on evidence the answer itself calls unknown, that an allergen consequence
/// arrives with the caution keeping it a consequence rather than a verdict, and that an answer proposing
/// nothing says why.
/// </remarks>
public sealed class AiSubstitutionOutputValidatorTests
{
    private const string Schema = "recipe.substitution.v1";

    // ---- the shape ---------------------------------------------------------------------------------------

    [Fact]
    public void A_sound_answer_validates()
    {
        var result = AiSubstitutionOutputValidator.Validate(Payload(Buttermilk()), Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Single(result.Document!.Substitutions);
        Assert.Equal("soured milk", result.Document.Substitutions[0].Alternative);
    }

    [Fact]
    public void A_schema_version_the_template_did_not_declare_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk(), schema: "recipe.substitution.v2"), Schema);

        Assert.Equal(AiOutputReason.SchemaVersionMismatch, result.Failure!.ReasonCode);
    }

    [Fact]
    public void An_empty_payload_is_rejected_as_correctable()
    {
        var result = AiSubstitutionOutputValidator.Validate("   ", Schema);

        Assert.Equal(AiOutputReason.EmptyPayload, result.Failure!.ReasonCode);
        Assert.True(result.Failure.IsCorrectableByReprompt);
    }

    // ---- claims the type system refuses to carry ---------------------------------------------------------

    /// <summary>
    /// The claim AIREC-004 exists to prevent, refused before any rule runs: <c>Removes</c> is not a member of
    /// <see cref="AiAllergenEffectKind"/>, so an answer asserting it does not deserialize.
    /// </summary>
    /// <remarks>
    /// Rejected rather than dropped, which is the point. A validator that discarded the unknown value would
    /// show the creator an answer the model did not give, with the dangerous claim removed and no sign it was
    /// ever made.
    /// </remarks>
    [Theory]
    [InlineData("Removes")]
    [InlineData("Free")]
    [InlineData("None")]
    public void An_allergen_effect_claiming_removal_is_rejected(string effect)
    {
        var substitution = Buttermilk() with
        {
            AllergenEffects = [new AiAllergenEffect { Allergen = "milk", Effect = AiAllergenEffectKind.Unknown }],
        };

        var payload = Payload(substitution, cautionIndex: 0).Replace("\"Unknown\"", $"\"{effect}\"", StringComparison.Ordinal);

        var result = AiSubstitutionOutputValidator.Validate(payload, Schema);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFailureCategory.OutputSchemaInvalid, result.Failure!.Category);
    }

    /// <summary>
    /// There is no vetted reference corpus behind this capability yet, so a model has no way to name one. A
    /// member for it would let a citation be asserted that nothing could check and no reader could follow.
    /// </summary>
    [Fact]
    public void Evidence_claiming_a_vetted_reference_is_rejected()
    {
        var payload = Payload(Buttermilk()).Replace(
            "\"evidenceBasis\":\"ModelKnowledge\"", "\"evidenceBasis\":\"VettedReference\"", StringComparison.Ordinal);

        var result = AiSubstitutionOutputValidator.Validate(payload, Schema);

        Assert.False(result.Succeeded);
    }

    /// <summary>A field nobody declared is a widened contract, not a bonus.</summary>
    [Fact]
    public void An_undeclared_field_is_rejected()
    {
        var payload = Payload(Buttermilk()).Replace(
            "\"rank\":1", "\"rank\":1,\"isSafeFor\":\"dairy allergy\"", StringComparison.Ordinal);

        var result = AiSubstitutionOutputValidator.Validate(payload, Schema);

        Assert.Equal(AiOutputReason.UnknownField, result.Failure!.ReasonCode);
    }

    // ---- ranking -----------------------------------------------------------------------------------------

    [Fact]
    public void Two_alternatives_claiming_one_rank_are_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk(), Yoghurt() with { Rank = 1 }), Schema);

        Assert.Equal(AiOutputReason.SubstitutionRankInvalid, result.Failure!.ReasonCode);
        Assert.Equal(AiFailureCategory.DomainInvalid, result.Failure.Category);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void A_rank_outside_one_to_n_is_rejected(int rank)
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk(), Yoghurt() with { Rank = rank }), Schema);

        Assert.Equal(AiOutputReason.SubstitutionRankInvalid, result.Failure!.ReasonCode);
    }

    [Fact]
    public void Ranks_given_out_of_order_are_still_a_valid_ranking()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { Rank = 2 }, Yoghurt() with { Rank = 1 }), Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    [Fact]
    public void Two_alternatives_naming_the_same_thing_are_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk(), Yoghurt() with { Alternative = "Soured Milk " }), Schema);

        Assert.Equal(AiOutputReason.DuplicateSubstitution, result.Failure!.ReasonCode);
    }

    // ---- required fields ---------------------------------------------------------------------------------

    /// <summary>
    /// "No guaranteed equivalence" in the form a creator meets it: there is no such thing here as a swap that
    /// needs no testing, however ordinary it looks.
    /// </summary>
    [Fact]
    public void An_alternative_with_nothing_to_test_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { TestRecommendation = "  " }), Schema);

        Assert.Equal(AiOutputReason.SubstitutionFieldMissing, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// An alternative offered without the role it is standing in for is a guess about an ingredient rather
    /// than about the dish, and a creator cannot tell the two apart.
    /// </summary>
    [Fact]
    public void An_alternative_with_no_functional_role_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { FunctionalRole = "" }), Schema);

        Assert.Equal(AiOutputReason.SubstitutionFieldMissing, result.Failure!.ReasonCode);
    }

    [Fact]
    public void An_alternative_with_no_quantity_guidance_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { QuantityGuidance = "" }), Schema);

        Assert.Equal(AiOutputReason.SubstitutionFieldMissing, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// Every one of these defaults to <c>Unspecified</c> when a model omits the property, so without the
    /// check an answer that said nothing would read as having said the first thing in each list.
    /// </summary>
    [Fact]
    public void An_undeclared_confidence_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { Confidence = AiSubstitutionConfidence.Unspecified }), Schema);

        Assert.Equal(AiOutputReason.KindNotDeclared, result.Failure!.ReasonCode);
    }

    [Fact]
    public void An_undeclared_evidence_basis_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { EvidenceBasis = AiEvidenceBasis.Unspecified }), Schema);

        Assert.Equal(AiOutputReason.KindNotDeclared, result.Failure!.ReasonCode);
    }

    // ---- unknown evidence stays unknown ------------------------------------------------------------------

    /// <summary>
    /// AIREC-004's restriction in the form a helpful model reaches it: the guidance may be perfectly sound,
    /// but presenting it as settled while admitting nothing stands behind it asks for a trust nothing earned.
    /// </summary>
    [Fact]
    public void High_confidence_on_unknown_evidence_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with
            {
                Confidence = AiSubstitutionConfidence.High,
                EvidenceBasis = AiEvidenceBasis.Unknown,
            }),
            Schema);

        Assert.Equal(AiOutputReason.ConfidenceUnevidenced, result.Failure!.ReasonCode);
        Assert.Equal(AiFailureCategory.DomainInvalid, result.Failure.Category);
    }

    /// <summary>Offering the same alternative as uncertain is always available, and is the useful answer.</summary>
    [Theory]
    [InlineData(AiSubstitutionConfidence.Low)]
    [InlineData(AiSubstitutionConfidence.Moderate)]
    public void Unknown_evidence_is_fine_at_a_confidence_that_admits_it(AiSubstitutionConfidence confidence)
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { Confidence = confidence, EvidenceBasis = AiEvidenceBasis.Unknown }),
            Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    [Fact]
    public void High_confidence_on_evidence_that_says_something_is_fine()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with
            {
                Confidence = AiSubstitutionConfidence.High,
                EvidenceBasis = AiEvidenceBasis.ModelKnowledge,
            }),
            Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    // ---- plausibility is not safety ----------------------------------------------------------------------

    /// <summary>
    /// The line between culinary plausibility and allergen safety, enforced. "Introduces tree nuts" sitting
    /// among flavour and texture notes reads as one more impact to weigh; the caution is what marks it as the
    /// thing to check against a label and against who is eating.
    /// </summary>
    [Fact]
    public void An_allergen_consequence_with_no_caution_beside_it_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(Payload(Almond()), Schema);

        Assert.Equal(AiOutputReason.AllergenEffectUncautioned, result.Failure!.ReasonCode);
        Assert.Equal(AiFailureCategory.DomainInvalid, result.Failure.Category);
    }

    [Fact]
    public void An_allergen_consequence_with_its_caution_validates()
    {
        var result = AiSubstitutionOutputValidator.Validate(Payload(Almond(), cautionIndex: 0), Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    /// <summary>A caution about the answer as a whole is not a caution about this alternative.</summary>
    [Fact]
    public void A_whole_answer_caution_does_not_cover_one_alternatives_allergens()
    {
        var document = new AiSubstitutionOutputDocument
        {
            SchemaVersion = Schema,
            Substitutions = [Almond()],
            Warnings =
            [
                new AiSubstitutionOutputWarning
                {
                    Kind = AiWarningKind.SafetyCaution,
                    Message = "Check every label.",
                    SubstitutionIndex = null,
                },
            ],
        };

        var result = AiSubstitutionOutputValidator.Validate(Serialize(document), Schema);

        Assert.Equal(AiOutputReason.AllergenEffectUncautioned, result.Failure!.ReasonCode);
    }

    /// <summary>A caution on the wrong alternative leaves this one uncautioned.</summary>
    [Fact]
    public void A_caution_on_another_alternative_does_not_cover_this_one()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk(), Almond() with { Rank = 2 }, cautionIndex: 0), Schema);

        Assert.Equal(AiOutputReason.AllergenEffectUncautioned, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// An unknown allergen status is the case most in need of a caution, not least: it is the one where the
    /// creator has to go and find out.
    /// </summary>
    [Fact]
    public void An_unknown_allergen_status_needs_a_caution_too()
    {
        var substitution = Buttermilk() with
        {
            AllergenEffects =
                [new AiAllergenEffect { Allergen = "soy", Effect = AiAllergenEffectKind.Unknown }],
        };

        var result = AiSubstitutionOutputValidator.Validate(Payload(substitution), Schema);

        Assert.Equal(AiOutputReason.AllergenEffectUncautioned, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// The bypass this rule had while dietary notes were prose: a model could state the conclusion in a note,
    /// leave the structured allergen list empty, and owe no caution at all. Structuring dietary effects is
    /// what closed it, and this is the case that would have walked through.
    /// </summary>
    [Fact]
    public void A_dietary_consequence_with_no_caution_beside_it_is_rejected()
    {
        var substitution = Buttermilk() with
        {
            DietaryEffects = [new AiDietaryEffect { Diet = "vegan", Effect = AiDietaryEffectKind.Conflicts }],
        };

        var result = AiSubstitutionOutputValidator.Validate(Payload(substitution), Schema);

        Assert.Equal(AiOutputReason.AllergenEffectUncautioned, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_dietary_consequence_with_its_caution_validates()
    {
        var substitution = Buttermilk() with
        {
            DietaryEffects =
                [new AiDietaryEffect { Diet = "vegan", Effect = AiDietaryEffectKind.MayConflict }],
        };

        var result = AiSubstitutionOutputValidator.Validate(Payload(substitution, cautionIndex: 0), Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    /// <summary>
    /// The dietary claim that must never be made, refused the way its allergen counterpart is: there is no
    /// member for it, so an answer asserting one does not deserialize.
    /// </summary>
    [Theory]
    [InlineData("Satisfies")]
    [InlineData("Suitable")]
    [InlineData("Meets")]
    public void A_dietary_effect_claiming_a_diet_is_satisfied_is_rejected(string effect)
    {
        var substitution = Buttermilk() with
        {
            DietaryEffects = [new AiDietaryEffect { Diet = "vegan", Effect = AiDietaryEffectKind.Unknown }],
        };

        var payload = Payload(substitution, cautionIndex: 0)
            .Replace("\"Unknown\"", $"\"{effect}\"", StringComparison.Ordinal);

        var result = AiSubstitutionOutputValidator.Validate(payload, Schema);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFailureCategory.OutputSchemaInvalid, result.Failure!.Category);
    }

    [Fact]
    public void An_undeclared_dietary_effect_direction_is_rejected()
    {
        var substitution = Buttermilk() with
        {
            DietaryEffects =
                [new AiDietaryEffect { Diet = "vegan", Effect = AiDietaryEffectKind.Unspecified }],
        };

        var result = AiSubstitutionOutputValidator.Validate(Payload(substitution, cautionIndex: 0), Schema);

        Assert.Equal(AiOutputReason.KindNotDeclared, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_dietary_effect_naming_no_diet_is_rejected()
    {
        var substitution = Buttermilk() with
        {
            DietaryEffects = [new AiDietaryEffect { Diet = "  ", Effect = AiDietaryEffectKind.Conflicts }],
        };

        var result = AiSubstitutionOutputValidator.Validate(Payload(substitution, cautionIndex: 0), Schema);

        Assert.Equal(AiOutputReason.SubstitutionFieldMissing, result.Failure!.ReasonCode);
    }

    /// <summary>
    /// The document has no free-text dietary field left to put a conclusion in. Asserted structurally rather
    /// than by probing prose, because prose is exactly what cannot be asserted about.
    /// </summary>
    [Fact]
    public void There_is_no_free_text_dietary_field_on_a_substitution()
    {
        var textual = typeof(AiIngredientSubstitution).GetProperties()
            .Where(property => property.PropertyType == typeof(IReadOnlyList<string>))
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(textual);
    }

    [Fact]
    public void An_allergen_effect_naming_no_allergen_is_rejected()
    {
        var substitution = Buttermilk() with
        {
            AllergenEffects =
                [new AiAllergenEffect { Allergen = " ", Effect = AiAllergenEffectKind.Introduces }],
        };

        var result = AiSubstitutionOutputValidator.Validate(Payload(substitution, cautionIndex: 0), Schema);

        Assert.Equal(AiOutputReason.SubstitutionFieldMissing, result.Failure!.ReasonCode);
    }

    // ---- no alternative ----------------------------------------------------------------------------------

    /// <summary>
    /// Some ingredients carry the structure of a dish. Proposing nothing is the honest answer for one of
    /// those, and a floor on the count would make inventing something the only way to return successfully.
    /// </summary>
    [Fact]
    public void An_answer_with_no_alternative_validates_when_it_says_why()
    {
        var document = new AiSubstitutionOutputDocument
        {
            SchemaVersion = Schema,
            Warnings =
            [
                new AiSubstitutionOutputWarning
                {
                    Kind = AiWarningKind.SafetyCaution,
                    Message = "The salt here is curing the pork, not seasoning it. Changing it changes how "
                        + "safely the cure works, and that needs a source this cannot check.",
                    SubstitutionIndex = null,
                },
            ],
        };

        var result = AiSubstitutionOutputValidator.Validate(Serialize(document), Schema);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Empty(result.Document!.Substitutions);
    }

    /// <summary>
    /// A silent empty answer is indistinguishable from a call that went wrong, and leaves the creator without
    /// the one thing that would have helped: why this ingredient has no stand-in.
    /// </summary>
    [Fact]
    public void An_answer_with_no_alternative_and_no_explanation_is_rejected()
    {
        var document = new AiSubstitutionOutputDocument { SchemaVersion = Schema };

        var result = AiSubstitutionOutputValidator.Validate(Serialize(document), Schema);

        Assert.Equal(AiOutputReason.SubstitutionAnswerUnexplained, result.Failure!.ReasonCode);
    }

    // ---- bounds ------------------------------------------------------------------------------------------

    [Fact]
    public void More_alternatives_than_the_limit_are_rejected()
    {
        var many = Enumerable.Range(1, AiPolicy.MaxSubstitutionCount + 1)
            .Select(rank => Buttermilk() with { Rank = rank, Alternative = $"option {rank}" })
            .ToArray();

        var result = AiSubstitutionOutputValidator.Validate(Payload(many), Schema);

        Assert.Equal(AiOutputReason.ValueTooLong, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_value_longer_than_its_bound_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with
            {
                QuantityGuidance = new string('x', AiPolicy.SubstitutionFieldMaxLength + 1),
            }),
            Schema);

        Assert.Equal(AiOutputReason.ValueTooLong, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_control_character_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(
            Payload(Buttermilk() with { FlavorImpact = "tangier\u0007" }), Schema);

        Assert.Equal(AiOutputReason.IllegalCharacters, result.Failure!.ReasonCode);
    }

    [Fact]
    public void A_warning_pointing_at_an_alternative_that_does_not_exist_is_rejected()
    {
        var result = AiSubstitutionOutputValidator.Validate(Payload(Buttermilk(), cautionIndex: 4), Schema);

        Assert.Equal(AiOutputReason.WarningIndexInvalid, result.Failure!.ReasonCode);
    }

    // ---- the failure path carries no payload -------------------------------------------------------------

    /// <summary>
    /// The guarantee every validator in this module makes, and it matters most here: a rejected answer's own
    /// words are exactly the words that must not reach a creator or a log.
    /// </summary>
    [Fact]
    public void A_rejection_carries_neither_the_document_nor_the_payload()
    {
        var result = AiSubstitutionOutputValidator.Validate(Payload(Almond()), Schema);

        Assert.Null(result.Document);
        Assert.DoesNotContain("almond", result.Failure!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- fixtures ----------------------------------------------------------------------------------------

    private static AiIngredientSubstitution Buttermilk() => new()
    {
        Rank = 1,
        Alternative = "soured milk",
        FunctionalRole = "the acid that reacts with the soda, plus a little tenderising",
        QuantityGuidance = "the same volume, soured with a tablespoon of lemon juice and left ten minutes",
        FlavorImpact = "slightly less tangy",
        TextureImpact = "a marginally looser crumb",
        Confidence = AiSubstitutionConfidence.Moderate,
        EvidenceBasis = AiEvidenceBasis.ModelKnowledge,
        TestRecommendation = "bake two and check the rise before committing the batch",
    };

    private static AiIngredientSubstitution Yoghurt() => Buttermilk() with
    {
        Rank = 2,
        Alternative = "plain yoghurt, thinned",
    };

    /// <summary>An alternative that brings an allergen with it, and says so.</summary>
    private static AiIngredientSubstitution Almond() => Buttermilk() with
    {
        Alternative = "almond milk, soured",
        AllergenEffects =
            [new AiAllergenEffect { Allergen = "tree nuts", Effect = AiAllergenEffectKind.Introduces }],
    };

    private static string Payload(AiIngredientSubstitution substitution, string? schema = null, int? cautionIndex = null) =>
        Payload([substitution], schema, cautionIndex);

    private static string Payload(
        AiIngredientSubstitution first, AiIngredientSubstitution second, int? cautionIndex = null) =>
        Payload([first, second], schema: null, cautionIndex);

    private static string Payload(
        AiIngredientSubstitution[] substitutions, string? schema = null, int? cautionIndex = null) =>
        Serialize(new AiSubstitutionOutputDocument
        {
            SchemaVersion = schema ?? Schema,
            Substitutions = substitutions,
            Warnings = cautionIndex is { } index
                ?
                [
                    new AiSubstitutionOutputWarning
                    {
                        Kind = AiWarningKind.SafetyCaution,
                        Message = "Check the label, and check with whoever is eating.",
                        SubstitutionIndex = index,
                    },
                ]
                : [],
        });

    private static string Serialize(AiSubstitutionOutputDocument document) =>
        JsonSerializer.Serialize(document, SerializerOptions);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
