using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary AIREC-004's answer crosses to become an <see cref="AiSubstitutionOutputDocument"/>, mirroring
/// <see cref="AiOutputValidator"/>'s five stages and its two guarantees — the payload appears on neither the
/// success nor the failure path, and nothing is repaired — over a document with its own shape and its own
/// domain rules.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stage 5 is where this capability's restriction is actually kept.</strong> The other stages check
/// that an answer is well formed. <see cref="Domain"/> checks that it is honest: that alternatives are really
/// ranked, that confidence is not claimed on evidence the answer itself calls unknown, that an allergen
/// consequence arrives with the caution that keeps it a consequence rather than a verdict, and that an answer
/// proposing nothing says why. Each of those is a sentence in AIREC-004's RESTRICTION with a reason code
/// behind it.
/// </para>
/// <para>
/// A substitution addresses no change to the recipe, so there is no <see cref="AiOperationScope"/> question to
/// ask here the way <see cref="AiOutputValidator"/> asks one of a diff's targets — the scope check that
/// matters happens by there being nothing for it to check.
/// <see cref="AsDelegate"/> still accepts a scope, to match
/// <see cref="AiOutputValidatorDelegate{TDocument}"/>, and ignores it.
/// </para>
/// </remarks>
public static class AiSubstitutionOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiSubstitutionOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Hygiene(document!)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiSubstitutionOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiSubstitutionOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? Envelope(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Reject(AiOutputReason.EmptyPayload, "The provider returned no content.", correctable: true);
        }

        if (Encoding.UTF8.GetByteCount(payload) > AiPolicy.OutputPayloadMaxBytes)
        {
            return Reject(
                AiOutputReason.PayloadTooLarge,
                $"The response exceeded {AiPolicy.OutputPayloadMaxBytes} bytes and was not parsed.",
                correctable: false);
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? SchemaVersion(
        string payload, string expected)
    {
        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            return Reject(
                AiOutputReason.MalformedJson,
                $"The response is not valid JSON ({Sanitize(exception.Message)}).",
                correctable: true);
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind is not JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind is not JsonValueKind.String)
            {
                return Reject(
                    AiOutputReason.SchemaVersionMissing,
                    "The response does not declare a string 'schemaVersion'.",
                    correctable: true);
            }

            var declared = version.GetString();

            if (!string.Equals(declared, expected, StringComparison.Ordinal))
            {
                return Reject(
                    AiOutputReason.SchemaVersionMismatch,
                    $"The response declares schema '{Sanitize(declared)}' but '{expected}' was required.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <remarks>
    /// This is the stage that refuses <c>"effect": "Removes"</c> and <c>"evidenceBasis": "VettedReference"</c>.
    /// Neither is a member of its enum, so neither deserializes — the claim is rejected rather than dropped,
    /// and the creator is told the answer failed instead of being shown a quietly edited version of it.
    /// </remarks>
    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? Shape(
        string payload, out AiSubstitutionOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiSubstitutionOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            var unknownMember = exception.Message.Contains("could not be mapped", StringComparison.Ordinal);

            return Reject(
                unknownMember ? AiOutputReason.UnknownField : AiOutputReason.ShapeInvalid,
                $"The response does not match the required schema ({Sanitize(exception.Message)}).",
                correctable: true);
        }

        return document is null
            ? Reject(AiOutputReason.ShapeInvalid, "The response deserialized to nothing.", correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? Hygiene(
        AiSubstitutionOutputDocument document)
    {
        if (document.Substitutions.Count > AiPolicy.MaxSubstitutionCount)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response proposed more than {AiPolicy.MaxSubstitutionCount} alternatives.",
                correctable: true);
        }

        if (document.Warnings.Count > AiPolicy.MaxSubstitutionWarnings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxSubstitutionWarnings} warnings.",
                correctable: true);
        }

        foreach (var substitution in document.Substitutions)
        {
            var failure = CheckString(substitution.Alternative, AiPolicy.SubstitutionFieldMaxLength, "an alternative")
                ?? CheckString(substitution.FunctionalRole, AiPolicy.SubstitutionFieldMaxLength, "a functional role")
                ?? CheckString(
                    substitution.QuantityGuidance, AiPolicy.SubstitutionFieldMaxLength, "a quantity guidance note")
                ?? CheckString(substitution.TechniqueImpact, AiPolicy.SubstitutionFieldMaxLength, "a technique impact")
                ?? CheckString(substitution.FlavorImpact, AiPolicy.SubstitutionFieldMaxLength, "a flavour impact")
                ?? CheckString(substitution.TextureImpact, AiPolicy.SubstitutionFieldMaxLength, "a texture impact")
                ?? CheckString(substitution.EvidenceNote, AiPolicy.SubstitutionFieldMaxLength, "an evidence note")
                ?? CheckString(
                    substitution.TestRecommendation, AiPolicy.SubstitutionFieldMaxLength, "a test recommendation")
                ?? CheckAllergens(substitution.AllergenEffects)
                ?? CheckDiets(substitution.DietaryEffects);

            if (failure is not null)
            {
                return failure;
            }
        }

        foreach (var warning in document.Warnings)
        {
            var failure = CheckString(warning.Message, AiPolicy.MessageMaxLength, "a warning message");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? CheckAllergens(
        IReadOnlyList<AiAllergenEffect> effects)
    {
        if (effects.Count > AiPolicy.SubstitutionListMaxItems)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response lists more than {AiPolicy.SubstitutionListMaxItems} allergen effects on one "
                    + "alternative.",
                correctable: true);
        }

        foreach (var effect in effects)
        {
            var failure = CheckString(effect.Allergen, AiPolicy.SubstitutionListItemMaxLength, "an allergen name");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? CheckDiets(
        IReadOnlyList<AiDietaryEffect> effects)
    {
        if (effects.Count > AiPolicy.SubstitutionListMaxItems)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response lists more than {AiPolicy.SubstitutionListMaxItems} dietary effects on one "
                    + "alternative.",
                correctable: true);
        }

        foreach (var effect in effects)
        {
            var failure = CheckString(effect.Diet, AiPolicy.SubstitutionListItemMaxLength, "a diet name");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? CheckString(
        string? value, int maxLength, string what)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response contains {what} longer than {maxLength} characters.",
                correctable: true);
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with a control character.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <summary>Stage 5: the rules the type system cannot state, and which AIREC-004 turns on.</summary>
    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? Domain(
        AiSubstitutionOutputDocument document) =>
        Enums(document)
            ?? RequiredFields(document)
            ?? Ranking(document)
            ?? DistinctAlternatives(document)
            ?? WarningIndexes(document)
            ?? EvidencedConfidence(document)
            ?? CautionedConsequences(document)
            ?? ExplainedEmptyAnswer(document);

    /// <remarks>
    /// <c>Unspecified</c> is the default every one of these enums gets when a model omits the property, so
    /// without this an answer that said nothing about confidence, evidence or an allergen's direction would
    /// read as having said the first thing in each list.
    /// </remarks>
    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? Enums(
        AiSubstitutionOutputDocument document)
    {
        foreach (var substitution in document.Substitutions)
        {
            if (substitution.Confidence is AiSubstitutionConfidence.Unspecified
                || substitution.EvidenceBasis is AiEvidenceBasis.Unspecified
                || substitution.AllergenEffects.Any(effect => effect.Effect is AiAllergenEffectKind.Unspecified)
                || substitution.DietaryEffects.Any(effect => effect.Effect is AiDietaryEffectKind.Unspecified))
            {
                return Reject(
                    AiOutputReason.KindNotDeclared,
                    "Every alternative declares its confidence and its evidence, and every allergen and "
                        + "dietary effect says which way it runs.",
                    correctable: true);
            }
        }

        if (document.Warnings.Any(warning => warning.Kind is AiWarningKind.Unspecified))
        {
            return Reject(
                AiOutputReason.KindNotDeclared, "Every warning declares its kind.", correctable: true);
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? RequiredFields(
        AiSubstitutionOutputDocument document)
    {
        foreach (var substitution in document.Substitutions)
        {
            if (string.IsNullOrWhiteSpace(substitution.Alternative)
                || string.IsNullOrWhiteSpace(substitution.FunctionalRole)
                || string.IsNullOrWhiteSpace(substitution.QuantityGuidance)
                || string.IsNullOrWhiteSpace(substitution.TestRecommendation))
            {
                return Reject(
                    AiOutputReason.SubstitutionFieldMissing,
                    "Every alternative needs a name, the role it is standing in for, how much to use, and "
                        + "what to test before trusting it.",
                    correctable: true);
            }

            if (substitution.AllergenEffects.Any(effect => string.IsNullOrWhiteSpace(effect.Allergen)))
            {
                return Reject(
                    AiOutputReason.SubstitutionFieldMissing,
                    "An allergen effect does not name an allergen.",
                    correctable: true);
            }

            if (substitution.DietaryEffects.Any(effect => string.IsNullOrWhiteSpace(effect.Diet)))
            {
                return Reject(
                    AiOutputReason.SubstitutionFieldMissing,
                    "A dietary effect does not name a diet.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <remarks>
    /// Exactly <c>1..N</c>. A duplicate rank, a gap, a zero or a negative all fail the same way — each means
    /// the answer did not rank what AIREC-004 asked it to rank, and none is more recoverable than another.
    /// </remarks>
    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? Ranking(
        AiSubstitutionOutputDocument document)
    {
        var expected = Enumerable.Range(1, document.Substitutions.Count);
        var actual = document.Substitutions.Select(substitution => substitution.Rank).Order();

        return expected.SequenceEqual(actual)
            ? null
            : Reject(
                AiOutputReason.SubstitutionRankInvalid,
                $"The {document.Substitutions.Count} alternatives must be ranked 1 to "
                    + $"{document.Substitutions.Count}, once each.",
                correctable: true);
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? DistinctAlternatives(
        AiSubstitutionOutputDocument document)
    {
        var distinct = document.Substitutions
            .Select(substitution => substitution.Alternative.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return distinct == document.Substitutions.Count
            ? null
            : Reject(
                AiOutputReason.DuplicateSubstitution,
                "Two alternatives name the same thing, so they are not distinct options.",
                correctable: true);
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? WarningIndexes(
        AiSubstitutionOutputDocument document)
    {
        var outOfRange = document.Warnings.Any(warning =>
            warning.SubstitutionIndex is { } index
            && (index < 0 || index >= document.Substitutions.Count));

        return outOfRange
            ? Reject(
                AiOutputReason.WarningIndexInvalid,
                "A warning points at an alternative that does not exist.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// High confidence requires evidence the answer does not itself call unknown.
    /// </summary>
    /// <remarks>
    /// "Unknown evidence stays unknown" is AIREC-004's restriction, and this is the form it takes when a model
    /// is being helpful rather than careless: the guidance may be perfectly good, but presenting it as settled
    /// while admitting nothing stands behind it asks a creator to trust a confidence that was not earned.
    /// Offering the same alternative at <see cref="AiSubstitutionConfidence.Low"/> is always available.
    /// </remarks>
    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? EvidencedConfidence(
        AiSubstitutionOutputDocument document)
    {
        var unevidenced = document.Substitutions.Any(substitution =>
            substitution.Confidence is AiSubstitutionConfidence.High
            && substitution.EvidenceBasis is AiEvidenceBasis.Unknown);

        return unevidenced
            ? Reject(
                AiOutputReason.ConfidenceUnevidenced,
                "An alternative claims high confidence while declaring its evidence unknown.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// An allergen or dietary consequence arrives with a safety caution, or it does not arrive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line AIREC-004 draws between culinary plausibility and allergen safety, enforced. "Introduces tree
    /// nuts" sitting among flavour and texture notes reads as one more impact to weigh; the caution beside it
    /// is what marks it as the thing the creator has to check against a label and against who is eating. The
    /// caution is required even for <see cref="AiAllergenEffectKind.Unknown"/> and
    /// <see cref="AiDietaryEffectKind.Unknown"/> — an unknown status is the case most in need of one.
    /// </para>
    /// <para>
    /// <strong>Dietary effects count, and they were not structured at first.</strong> While they were prose,
    /// this rule keyed off the allergen list alone, so an answer could state a dietary — or even an allergen —
    /// conclusion in a note, leave the structured list empty, and owe no caution at all. Structuring them is
    /// what closed that; extending this rule is what makes the structure carry the same weight.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? CautionedConsequences(
        AiSubstitutionOutputDocument document)
    {
        for (var index = 0; index < document.Substitutions.Count; index++)
        {
            var substitution = document.Substitutions[index];

            if (substitution.AllergenEffects.Count == 0 && substitution.DietaryEffects.Count == 0)
            {
                continue;
            }

            var cautioned = document.Warnings.Any(warning =>
                warning.Kind is AiWarningKind.SafetyCaution && warning.SubstitutionIndex == index);

            if (!cautioned)
            {
                return Reject(
                    AiOutputReason.AllergenEffectUncautioned,
                    "An alternative states an allergen or dietary consequence with no safety caution beside it.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <remarks>
    /// Proposing nothing is allowed and is sometimes the only honest answer. Proposing nothing in silence is
    /// not: a creator cannot tell it from a call that went wrong, and it leaves them without the one thing
    /// that would have helped — why this ingredient has no stand-in.
    /// </remarks>
    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument>? ExplainedEmptyAnswer(
        AiSubstitutionOutputDocument document)
    {
        if (document.Substitutions.Count > 0)
        {
            return null;
        }

        return document.Warnings.Any(warning => warning.SubstitutionIndex is null)
            ? null
            : Reject(
                AiOutputReason.SubstitutionAnswerUnexplained,
                "The response proposed no alternative and did not say why.",
                correctable: true);
    }

    private static AiOutputValidationOutcome<AiSubstitutionOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.SubstitutionRankInvalid
            or AiOutputReason.SubstitutionFieldMissing
            or AiOutputReason.DuplicateSubstitution
            or AiOutputReason.ConfidenceUnevidenced
            or AiOutputReason.AllergenEffectUncautioned
            or AiOutputReason.SubstitutionAnswerUnexplained
            or AiOutputReason.KindNotDeclared
            or AiOutputReason.WarningIndexInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiSubstitutionOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
