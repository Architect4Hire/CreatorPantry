using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary AIREC-002's answer crosses to become an <see cref="AiRecipeDraftOutputDocument"/>, mirroring
/// <see cref="AiConceptOutputValidator"/>'s stages and its two guarantees — the payload appears on neither the
/// success nor the failure path, and nothing is repaired — over a document with its own shape and its own
/// domain rules.
/// </summary>
/// <remarks>
/// A first-draft operation names no recipe, so there is no <see cref="AiOperationScope"/> question to ask here
/// the way <see cref="AiOutputValidator"/> asks one of a diff's targets. <see cref="AsDelegate"/> still accepts
/// one, to match <see cref="AiOutputValidatorDelegate{TDocument}"/>, and ignores it.
/// </remarks>
public static class AiRecipeDraftOutputValidator
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

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiRecipeDraftOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Hygiene(document!)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiRecipeDraftOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiRecipeDraftOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? SchemaVersion(string payload, string expected)
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

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? Shape(
        string payload, out AiRecipeDraftOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiRecipeDraftOutputDocument>(payload, OutputJson);
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

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? Hygiene(AiRecipeDraftOutputDocument document)
    {
        return CheckString(document.Title, AiPolicy.RecipeDraftTitleMaxLength, "the title")
            ?? CheckString(document.Description, AiPolicy.RecipeDraftDescriptionMaxLength, "the description")
            ?? CheckString(document.Notes, AiPolicy.RecipeDraftNotesMaxLength, "the notes")
            ?? HygieneYield(document.Yield)
            ?? HygieneTiming(document.Timing)
            ?? HygieneIngredientGroups(document.IngredientGroups)
            ?? HygieneInstructionGroups(document.Instructions)
            ?? HygieneEquipment(document.Equipment)
            ?? HygieneUnresolvedQuestions(document.UnresolvedQuestions)
            ?? HygieneWarnings(document.Warnings);
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? HygieneYield(AiRecipeDraftYield? yield)
    {
        if (yield is null)
        {
            return null;
        }

        return CheckString(yield.YieldText, AiPolicy.RecipeDraftYieldTextMaxLength, "the yield text")
            ?? CheckString(yield.YieldUnitText, AiPolicy.RecipeDraftYieldUnitTextMaxLength, "the yield unit")
            ?? CheckNonNegative(yield.YieldQuantity, "the yield quantity")
            ?? CheckNonNegative(yield.ServingCount, "the serving count")
            ?? CheckNonNegative(yield.ServingSize, "the serving size");
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? HygieneTiming(AiRecipeDraftTiming? timing)
    {
        if (timing is null)
        {
            return null;
        }

        return CheckNonNegative(timing.PrepTimeMinutes, "the prep time")
            ?? CheckNonNegative(timing.CookTimeMinutes, "the cook time")
            ?? CheckNonNegative(timing.RestTimeMinutes, "the rest time")
            ?? CheckNonNegative(timing.TotalTimeMinutes, "the total time");
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? HygieneIngredientGroups(
        IReadOnlyList<AiRecipeDraftIngredientGroup> groups)
    {
        foreach (var group in groups)
        {
            var failure = CheckString(group.Title, AiPolicy.RecipeDraftGroupTitleMaxLength, "an ingredient group heading");
            if (failure is not null)
            {
                return failure;
            }

            if (group.Ingredients.Count > AiPolicy.MaxIngredientLinesPerGroup)
            {
                return Reject(
                    AiOutputReason.RecipeDraftTooManyItems,
                    $"An ingredient group lists more than {AiPolicy.MaxIngredientLinesPerGroup} lines.",
                    correctable: true);
            }

            foreach (var line in group.Ingredients)
            {
                failure = CheckString(line.DisplayText, AiPolicy.RecipeDraftLineTextMaxLength, "an ingredient line")
                    ?? CheckString(
                        line.IngredientNameText, AiPolicy.RecipeDraftIngredientNameTextMaxLength, "an ingredient name")
                    ?? CheckString(line.UnitText, AiPolicy.RecipeDraftUnitTextMaxLength, "an ingredient unit")
                    ?? CheckString(line.PreparationNote, AiPolicy.RecipeDraftNoteMaxLength, "a preparation note")
                    ?? CheckNonNegative(line.Quantity, "an ingredient quantity")
                    ?? CheckNonNegative(line.QuantityUpper, "an ingredient quantity's upper bound")
                    ?? CheckRange(line.Quantity, line.QuantityUpper);

                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? HygieneInstructionGroups(
        IReadOnlyList<AiRecipeDraftInstructionGroup> groups)
    {
        foreach (var group in groups)
        {
            var failure = CheckString(group.Title, AiPolicy.RecipeDraftGroupTitleMaxLength, "an instruction group heading");
            if (failure is not null)
            {
                return failure;
            }

            if (group.Steps.Count > AiPolicy.MaxInstructionStepsPerGroup)
            {
                return Reject(
                    AiOutputReason.RecipeDraftTooManyItems,
                    $"An instruction group lists more than {AiPolicy.MaxInstructionStepsPerGroup} steps.",
                    correctable: true);
            }

            foreach (var step in group.Steps)
            {
                failure = CheckString(step.Text, AiPolicy.RecipeDraftStepTextMaxLength, "an instruction step")
                    ?? CheckString(step.Note, AiPolicy.RecipeDraftNoteMaxLength, "a step note")
                    ?? CheckNonNegative(step.DurationMinutes, "a step duration");

                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? HygieneEquipment(
        IReadOnlyList<AiRecipeDraftEquipmentItem> equipment)
    {
        if (equipment.Count > AiPolicy.MaxEquipmentItems)
        {
            return Reject(
                AiOutputReason.RecipeDraftTooManyItems,
                $"The response lists more than {AiPolicy.MaxEquipmentItems} equipment items.",
                correctable: true);
        }

        foreach (var item in equipment)
        {
            var failure = CheckString(item.DisplayText, AiPolicy.RecipeDraftLineTextMaxLength, "an equipment item")
                ?? CheckString(item.Note, AiPolicy.RecipeDraftNoteMaxLength, "an equipment note");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? HygieneUnresolvedQuestions(
        IReadOnlyList<string> questions)
    {
        if (questions.Count > AiPolicy.MaxUnresolvedQuestions)
        {
            return Reject(
                AiOutputReason.RecipeDraftTooManyItems,
                $"The response lists more than {AiPolicy.MaxUnresolvedQuestions} unresolved questions.",
                correctable: true);
        }

        foreach (var question in questions)
        {
            var failure = CheckString(question, AiPolicy.RecipeDraftUnresolvedQuestionMaxLength, "an unresolved question");
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? HygieneWarnings(
        IReadOnlyList<AiRecipeDraftWarning> warnings)
    {
        // Capped so a genuinely important SafetyCaution cannot be diluted among an unbounded number of
        // low-value entries -- the same reason every other list on this document has a ceiling.
        if (warnings.Count > AiPolicy.MaxRecipeDraftWarnings)
        {
            return Reject(
                AiOutputReason.RecipeDraftTooManyItems,
                $"The response lists more than {AiPolicy.MaxRecipeDraftWarnings} warnings.",
                correctable: true);
        }

        foreach (var warning in warnings)
        {
            // Never valid on a stored row -- the check constraint refuses it -- so this is caught here with a
            // reason a caller can act on, rather than surfacing as a database constraint violation later.
            if (warning.Kind is AiWarningKind.Unspecified)
            {
                return Reject(
                    AiOutputReason.KindNotDeclared,
                    "A warning did not declare what kind it is.",
                    correctable: true);
            }

            var failure = CheckString(warning.Message, AiPolicy.MessageMaxLength, "a warning message");
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    /// <summary>Stage 5: the rules the type system cannot state.</summary>
    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? Domain(AiRecipeDraftOutputDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Title))
        {
            return Reject(AiOutputReason.RecipeDraftFieldMissing, "The draft has no title.", correctable: true);
        }

        if (document.IngredientGroups.Count < AiPolicy.MinIngredientGroups
            || document.IngredientGroups.Count > AiPolicy.MaxIngredientGroups)
        {
            return Reject(
                AiOutputReason.RecipeDraftGroupCountOutOfRange,
                $"The draft proposed {document.IngredientGroups.Count} ingredient group(s); between "
                    + $"{AiPolicy.MinIngredientGroups} and {AiPolicy.MaxIngredientGroups} are required.",
                correctable: true);
        }

        if (document.Instructions.Count < AiPolicy.MinInstructionGroups
            || document.Instructions.Count > AiPolicy.MaxInstructionGroups)
        {
            return Reject(
                AiOutputReason.RecipeDraftGroupCountOutOfRange,
                $"The draft proposed {document.Instructions.Count} instruction group(s); between "
                    + $"{AiPolicy.MinInstructionGroups} and {AiPolicy.MaxInstructionGroups} are required.",
                correctable: true);
        }

        if (document.IngredientGroups.Any(group => group.Ingredients.Count == 0))
        {
            return Reject(
                AiOutputReason.RecipeDraftEmptyGroup,
                "An ingredient group was proposed with no ingredients.",
                correctable: true);
        }

        if (document.Instructions.Any(group => group.Steps.Count == 0))
        {
            return Reject(
                AiOutputReason.RecipeDraftEmptyGroup,
                "An instruction group was proposed with no steps.",
                correctable: true);
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? CheckRange(decimal? quantity, decimal? upper)
    {
        if (quantity is { } low && upper is { } high && high <= low)
        {
            return Reject(
                AiOutputReason.DomainInvalid,
                "An ingredient quantity range's upper bound does not exceed its lower bound.",
                correctable: true);
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? CheckNonNegative(decimal? value, string what)
    {
        if (value is { } number && number < 0)
        {
            return Reject(AiOutputReason.DomainInvalid, $"The response proposes a negative value for {what}.", correctable: true);
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? CheckNonNegative(int? value, string what)
    {
        if (value is { } number && number < 0)
        {
            return Reject(AiOutputReason.DomainInvalid, $"The response proposes a negative value for {what}.", correctable: true);
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument>? CheckString(
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

    private static AiOutputValidationOutcome<AiRecipeDraftOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.RecipeDraftGroupCountOutOfRange
            or AiOutputReason.RecipeDraftEmptyGroup
            or AiOutputReason.RecipeDraftFieldMissing
            or AiOutputReason.RecipeDraftTooManyItems
            or AiOutputReason.DomainInvalid
            or AiOutputReason.KindNotDeclared
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiRecipeDraftOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
