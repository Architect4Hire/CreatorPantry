using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary AIREC-001's answer crosses to become an <see cref="AiConceptOutputDocument"/>, mirroring
/// <see cref="AiOutputValidator"/>'s stages and its two guarantees — the payload appears on neither the
/// success nor the failure path, and nothing is repaired — over a document with its own shape and its own
/// domain rules rather than the recipe-diff document's.
/// </summary>
/// <remarks>
/// A concept-generation operation names no recipe, so there is no <see cref="AiOperationScope"/> question to
/// ask here the way <see cref="AiOutputValidator"/> asks one of a diff's targets. <see cref="AsDelegate"/>
/// still accepts one, to match <see cref="AiOutputValidatorDelegate{TDocument}"/>, and ignores it.
/// </remarks>
public static class AiConceptOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiConceptOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Hygiene(document!)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiConceptOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiConceptOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiConceptOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiConceptOutputDocument>? SchemaVersion(string payload, string expected)
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

    private static AiOutputValidationOutcome<AiConceptOutputDocument>? Shape(
        string payload, out AiConceptOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiConceptOutputDocument>(payload, OutputJson);
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

    private static AiOutputValidationOutcome<AiConceptOutputDocument>? Hygiene(AiConceptOutputDocument document)
    {
        foreach (var concept in document.Concepts)
        {
            var failure = CheckString(concept.Title, AiPolicy.ConceptFieldMaxLength, "a concept title")
                ?? CheckString(concept.Summary, AiPolicy.ConceptFieldMaxLength, "a concept summary")
                ?? CheckString(
                    concept.DistinctnessRationale, AiPolicy.ConceptFieldMaxLength, "a distinctness rationale")
                ?? CheckString(concept.TimeBudgetNote, AiPolicy.ConceptFieldMaxLength, "a time budget note")
                ?? CheckString(concept.SkillLevelFit, AiPolicy.ConceptFieldMaxLength, "a skill level note")
                ?? CheckList(concept.Assumptions, "an assumption")
                ?? CheckList(concept.SuggestedIngredients, "a suggested ingredient")
                ?? CheckList(concept.DietaryNotes, "a dietary note");

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

    private static AiOutputValidationOutcome<AiConceptOutputDocument>? CheckList(
        IReadOnlyList<string> values, string what)
    {
        if (values.Count > AiPolicy.ConceptListMaxItems)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response lists more than {AiPolicy.ConceptListMaxItems} {what} entries on one concept.",
                correctable: true);
        }

        foreach (var value in values)
        {
            var failure = CheckString(value, AiPolicy.ConceptListItemMaxLength, what);

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiConceptOutputDocument>? CheckString(
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

    /// <summary>Stage 5: the rules the type system cannot state.</summary>
    private static AiOutputValidationOutcome<AiConceptOutputDocument>? Domain(AiConceptOutputDocument document)
    {
        if (document.Concepts.Count < AiPolicy.MinConceptCount || document.Concepts.Count > AiPolicy.MaxConceptCount)
        {
            return Reject(
                AiOutputReason.ConceptCountOutOfRange,
                $"The response proposed {document.Concepts.Count} concept(s); between "
                    + $"{AiPolicy.MinConceptCount} and {AiPolicy.MaxConceptCount} are required.",
                correctable: true);
        }

        foreach (var concept in document.Concepts)
        {
            if (string.IsNullOrWhiteSpace(concept.Title)
                || string.IsNullOrWhiteSpace(concept.Summary)
                || string.IsNullOrWhiteSpace(concept.DistinctnessRationale))
            {
                return Reject(
                    AiOutputReason.ConceptFieldMissing,
                    "Every concept needs a title, a summary, and a distinctness rationale.",
                    correctable: true);
            }
        }

        var distinctTitles = document.Concepts
            .Select(concept => concept.Title.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (distinctTitles != document.Concepts.Count)
        {
            return Reject(
                AiOutputReason.DuplicateConceptTitle,
                "Two proposed concepts share a title, so they are not distinct.",
                correctable: true);
        }

        var outOfRange = document.Warnings
            .Any(warning => warning.ConceptIndex is { } index
                && (index < 0 || index >= document.Concepts.Count));

        return outOfRange
            ? Reject(
                AiOutputReason.WarningIndexInvalid,
                "A warning points at a concept that does not exist.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiConceptOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.ConceptCountOutOfRange
            or AiOutputReason.DuplicateConceptTitle
            or AiOutputReason.ConceptFieldMissing
            or AiOutputReason.WarningIndexInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiConceptOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
