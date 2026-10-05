using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary AIREC-006's answer crosses to become an <see cref="AiRecipeReviewOutputDocument"/>, mirroring
/// <see cref="AiSubstitutionOutputValidator"/>'s five stages and its two guarantees — the payload appears on
/// neither the success nor the failure path, and nothing is repaired — over a document with its own shape and
/// its own domain rules.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stage 5 is where AIREC-006's restriction is actually kept.</strong> The other stages check that an
/// answer is well formed. <see cref="Domain"/> checks that it is honest and that it stays inside the bound this
/// capability draws for itself: that an unsupported claim says what is unknown, that a finding needing a
/// reference check says so, that an allergen or dietary conflict arrives with the caution and the severity
/// floor that keep it a consequence rather than a verdict, and that an answer proposing nothing says why.
/// </para>
/// <para>
/// There is no <see cref="AiOperationScope"/> question to ask here, for the reason
/// <see cref="AiSubstitutionOutputValidator"/> gives: a review addresses no change to the recipe, so there is
/// nothing for a target-scope check to examine. <see cref="AsDelegate"/> still accepts a scope, to match
/// <see cref="AiOutputValidatorDelegate{TDocument}"/>, and ignores it.
/// </para>
/// </remarks>
public static class AiRecipeReviewOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // A provider that answers an explicit null for a non-nullable collection — "changes": null — would
        // otherwise crash this validator with a NullReferenceException rather than being refused: System.Text.Json
        // does not enforce a non-nullable annotation unless asked, and a collection initialiser does not survive
        // an explicit null. With this, the null is a classified, correctable rejection like any other bad shape.
        RespectNullableAnnotations = true,
    };

    /// <summary>Every category a finding needing a reference check floor covers. See <see cref="ReferenceCheckFloor"/>.</summary>
    private static readonly AiRecipeReviewFindingCategory[] SafetyCategories =
    [
        AiRecipeReviewFindingCategory.AllergenConflict,
        AiRecipeReviewFindingCategory.DietaryConflict,
    ];

    /// <summary>Field kinds that name a child of the recipe, and so require an <see cref="AiRecipeReviewFieldRef.EntityId"/>.</summary>
    private static readonly AiRecipeReviewFieldKind[] ChildFieldKinds =
    [
        AiRecipeReviewFieldKind.IngredientLine,
        AiRecipeReviewFieldKind.Step,
        AiRecipeReviewFieldKind.Equipment,
    ];

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiRecipeReviewOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Hygiene(document!)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiRecipeReviewOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiRecipeReviewOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? Shape(
        string payload, out AiRecipeReviewOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiRecipeReviewOutputDocument>(payload, OutputJson);
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

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? Hygiene(
        AiRecipeReviewOutputDocument document)
    {
        if (document.Findings.Count > AiPolicy.MaxFindingCount)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response proposed more than {AiPolicy.MaxFindingCount} findings.",
                correctable: true);
        }

        if (document.Warnings.Count > AiPolicy.MaxReviewWarnings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxReviewWarnings} warnings.",
                correctable: true);
        }

        foreach (var finding in document.Findings)
        {
            var failure = CheckString(finding.Summary, AiPolicy.FindingFieldMaxLength, "a finding summary")
                ?? CheckString(finding.EvidenceNote, AiPolicy.FindingFieldMaxLength, "an evidence note")
                ?? CheckString(finding.Allergen, AiPolicy.FindingSafetyNameMaxLength, "an allergen name")
                ?? CheckString(finding.Diet, AiPolicy.FindingSafetyNameMaxLength, "a diet name")
                ?? CheckUnknownFactors(finding.UnknownFactors);

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

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? CheckUnknownFactors(
        IReadOnlyList<string> factors)
    {
        if (factors.Count > AiPolicy.MaxUnknownFactorsPerFinding)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response lists more than {AiPolicy.MaxUnknownFactorsPerFinding} unknown factors on one "
                    + "finding.",
                correctable: true);
        }

        foreach (var factor in factors)
        {
            var failure = CheckString(factor, AiPolicy.FindingUnknownFactorMaxLength, "an unknown factor");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? CheckString(
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

    /// <summary>Stage 5: the rules the type system cannot state, and which AIREC-006 turns on.</summary>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? Domain(
        AiRecipeReviewOutputDocument document) =>
        Enums(document)
            ?? RequiredFields(document)
            ?? FieldRefShape(document)
            ?? SafetyEffectShape(document)
            ?? UnsupportedClaimExplained(document)
            ?? AllergenOrDietarySeverityFloor(document)
            ?? ReferenceCheckFloor(document)
            ?? ReferenceCheckedFindingsAreCautioned(document)
            ?? WarningIndexes(document)
            ?? ExplainedEmptyAnswer(document);

    /// <remarks>
    /// <c>Unspecified</c> is the default every one of these enums gets when a model omits the property, so
    /// without this an answer that said nothing about a finding's category, severity, confidence, evidence or
    /// field kind would read as having said the first thing in each list.
    /// </remarks>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? Enums(
        AiRecipeReviewOutputDocument document)
    {
        foreach (var finding in document.Findings)
        {
            if (finding.Category is AiRecipeReviewFindingCategory.Unspecified
                || finding.Severity is AiRecipeReviewSeverity.Unspecified
                || finding.EvidenceBasis is AiEvidenceBasis.Unspecified
                || finding.Confidence is AiRecipeReviewConfidence.Unspecified
                || finding.FieldRef.FieldKind is AiRecipeReviewFieldKind.Unspecified)
            {
                return Reject(
                    AiOutputReason.KindNotDeclared,
                    "Every finding declares its category, its severity, its confidence, its evidence, and "
                        + "which field it is about.",
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

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? RequiredFields(
        AiRecipeReviewOutputDocument document)
    {
        if (document.Findings.Any(finding => string.IsNullOrWhiteSpace(finding.Summary)))
        {
            return Reject(
                AiOutputReason.FindingFieldMissing, "Every finding needs a summary.", correctable: true);
        }

        return null;
    }

    /// <remarks>
    /// A finding about the recipe as a whole, its title, or another scalar field has nothing to name; one about
    /// an ingredient line, a step, or an equipment item must name which one. Neither direction is optional: an
    /// id on a scalar field points nowhere in particular, and a missing id on a line or step points at nothing
    /// at all.
    /// </remarks>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? FieldRefShape(
        AiRecipeReviewOutputDocument document)
    {
        foreach (var finding in document.Findings)
        {
            var needsEntityId = ChildFieldKinds.Contains(finding.FieldRef.FieldKind);
            var hasEntityId = finding.FieldRef.EntityId is { } id && id != Guid.Empty;

            if (needsEntityId != hasEntityId)
            {
                return Reject(
                    AiOutputReason.FindingFieldRefInvalid,
                    "A finding about an ingredient line, a step, or an equipment item must name which one, and "
                        + "a finding about any other field must not.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <summary>
    /// A finding names an allergen and an effect exactly when its category is <c>AllergenConflict</c>, a diet
    /// and an effect exactly when its category is <c>DietaryConflict</c>, and neither on any other category.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the stage that refuses the claim AIREC-006 must never make.</strong> AIREC-004 keeps
    /// "this is safe for a peanut allergy" unwritable because <see cref="AiAllergenEffectKind"/> has no
    /// <c>Removes</c> member; a finding categorised <c>AllergenConflict</c> is now required to carry that same
    /// enum rather than leaving the claim to a free-text summary, so the same absent member closes the same
    /// channel here. An answer that tried to say more than <c>Introduces</c>/<c>MayIntroduce</c>/<c>Unknown</c>
    /// fails shape validation rather than being read as a certificate of safety.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? SafetyEffectShape(
        AiRecipeReviewOutputDocument document)
    {
        foreach (var finding in document.Findings)
        {
            var hasAllergen = !string.IsNullOrWhiteSpace(finding.Allergen) || finding.AllergenEffect is not null;
            var hasDiet = !string.IsNullOrWhiteSpace(finding.Diet) || finding.DietaryEffect is not null;

            var valid = finding.Category switch
            {
                AiRecipeReviewFindingCategory.AllergenConflict =>
                    !string.IsNullOrWhiteSpace(finding.Allergen)
                        && finding.AllergenEffect is { } allergenEffect
                        && allergenEffect is not AiAllergenEffectKind.Unspecified
                        && !hasDiet,

                AiRecipeReviewFindingCategory.DietaryConflict =>
                    !string.IsNullOrWhiteSpace(finding.Diet)
                        && finding.DietaryEffect is { } dietaryEffect
                        && dietaryEffect is not AiDietaryEffectKind.Unspecified
                        && !hasAllergen,

                _ => !hasAllergen && !hasDiet,
            };

            if (!valid)
            {
                return Reject(
                    AiOutputReason.SafetyEffectMisplaced,
                    "An allergen conflict names the allergen and how it runs, a dietary conflict names the diet "
                        + "and how it runs, and no other finding names either.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? UnsupportedClaimExplained(
        AiRecipeReviewOutputDocument document)
    {
        var unexplained = document.Findings.Any(finding =>
            finding.Category is AiRecipeReviewFindingCategory.UnsupportedClaim
            && (finding.UnknownFactors.Count == 0
                || finding.UnknownFactors.Any(string.IsNullOrWhiteSpace)));

        return unexplained
            ? Reject(
                AiOutputReason.UnsupportedClaimUnexplained,
                "A finding categorised as an unsupported claim does not say what is unknown.",
                correctable: true)
            : null;
    }

    /// <summary>An allergen or dietary finding is reported at least <see cref="AiRecipeReviewSeverity.Major"/>, or it does not arrive.</summary>
    /// <remarks>
    /// The category alone is what draws this floor: whatever a model's own sense of a finding's importance,
    /// an allergen or dietary consequence is never a minor polish item.
    /// </remarks>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? AllergenOrDietarySeverityFloor(
        AiRecipeReviewOutputDocument document)
    {
        var tooLow = document.Findings.Any(finding =>
            SafetyCategories.Contains(finding.Category)
            && finding.Severity is not (AiRecipeReviewSeverity.Major or AiRecipeReviewSeverity.Critical));

        return tooLow
            ? Reject(
                AiOutputReason.SafetyFindingSeverityTooLow,
                "An allergen or dietary conflict must be reported as at least major severity.",
                correctable: true)
            : null;
    }

    /// <remarks>
    /// AIREC-006's own floor: every allergen or dietary conflict, and every major or critical finding of any
    /// category, needs checking against a reference this system does not have — and says so, rather than
    /// leaving that to be inferred from the category or the severity alone. Widened past <c>Critical</c> alone
    /// because the capability's own prompt asks for this on a preservation, raw-ingredient, or temperature
    /// finding regardless of exactly how severe a model judges it, and a <c>Major</c> finding in any of those
    /// categories is exactly the case this restriction exists for.
    /// </remarks>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? ReferenceCheckFloor(
        AiRecipeReviewOutputDocument document)
    {
        var missing = document.Findings.Any(finding =>
            !finding.RequiresReferenceCheck
            && (SafetyCategories.Contains(finding.Category)
                || finding.Severity is AiRecipeReviewSeverity.Major or AiRecipeReviewSeverity.Critical));

        return missing
            ? Reject(
                AiOutputReason.ReferenceCheckRequired,
                "An allergen conflict, a dietary conflict, or a major or critical finding must say it needs "
                    + "checking against a reference.",
                correctable: true)
            : null;
    }

    /// <summary>Every finding that needs a reference check arrives with a safety caution, or it does not arrive.</summary>
    /// <remarks>
    /// Keyed off <see cref="AiRecipeReviewFinding.RequiresReferenceCheck"/> rather than category, because
    /// <see cref="AiWarningKind.SafetyCaution"/> is documented to cover preservation and temperature findings
    /// exactly as it covers allergen and dietary ones — a critical finding about an undercooked step owes the
    /// same caution an allergen conflict does, not a lesser one because its category happens to be
    /// <see cref="AiRecipeReviewFindingCategory.LikelyFailure"/> rather than
    /// <see cref="AiRecipeReviewFindingCategory.AllergenConflict"/>.
    /// </remarks>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? ReferenceCheckedFindingsAreCautioned(
        AiRecipeReviewOutputDocument document)
    {
        for (var index = 0; index < document.Findings.Count; index++)
        {
            var finding = document.Findings[index];

            if (!finding.RequiresReferenceCheck)
            {
                continue;
            }

            var cautioned = document.Warnings.Any(warning =>
                warning.Kind is AiWarningKind.SafetyCaution && warning.FindingIndex == index);

            if (!cautioned)
            {
                return Reject(
                    AiOutputReason.SafetyFindingUncautioned,
                    "A finding that needs checking against a reference carries no safety caution beside it.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? WarningIndexes(
        AiRecipeReviewOutputDocument document)
    {
        var outOfRange = document.Warnings.Any(warning =>
            warning.FindingIndex is { } index && (index < 0 || index >= document.Findings.Count));

        return outOfRange
            ? Reject(
                AiOutputReason.WarningIndexInvalid,
                "A warning points at a finding that does not exist.",
                correctable: true)
            : null;
    }

    /// <remarks>
    /// Proposing no finding is allowed and is sometimes the only honest answer. Proposing none in silence is
    /// not: a creator cannot tell it from a call that went wrong, and it leaves them without the one thing that
    /// would have helped — that the recipe was actually reviewed and read as fine.
    /// </remarks>
    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument>? ExplainedEmptyAnswer(
        AiRecipeReviewOutputDocument document)
    {
        if (document.Findings.Count > 0)
        {
            return null;
        }

        return document.Warnings.Any(warning => warning.FindingIndex is null)
            ? null
            : Reject(
                AiOutputReason.ReviewAnswerUnexplained,
                "The response proposed no finding and did not say why.",
                correctable: true);
    }

    private static AiOutputValidationOutcome<AiRecipeReviewOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.FindingFieldMissing
            or AiOutputReason.FindingFieldRefInvalid
            or AiOutputReason.SafetyEffectMisplaced
            or AiOutputReason.UnsupportedClaimUnexplained
            or AiOutputReason.ReferenceCheckRequired
            or AiOutputReason.SafetyFindingUncautioned
            or AiOutputReason.SafetyFindingSeverityTooLow
            or AiOutputReason.ReviewAnswerUnexplained
            or AiOutputReason.KindNotDeclared
            or AiOutputReason.WarningIndexInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiRecipeReviewOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
