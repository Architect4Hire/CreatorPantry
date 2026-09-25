using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary a model's answer crosses to become something the domain will look at.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no clock, no context, no persistence, no provider. It takes a raw string and returns either a typed
/// <see cref="AiOutputDocument"/> or an <see cref="AiOutputFailure"/>, and the payload appears on neither
/// path — a caller holding a failure cannot reach the JSON through it. That is what keeps raw model output out
/// of Business and out of the database.
/// </para>
/// <para>
/// <strong>Nothing is repaired.</strong> A truncated document is not completed, an unknown field is not
/// dropped, a wrong version is not coerced. Guessing at what a model meant produces a proposal the creator
/// never reviewed and the model never made. Where a re-ask could plausibly fix the answer, the failure says
/// so and the execution wrapper decides whether to spend an attempt on it.
/// </para>
/// </remarks>
public static class AiOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },

        // An extra property is a model answering a contract other than the one it was given. Ignoring it would
        // silently discard whatever it was trying to say, which is the failure mode this boundary exists to
        // make visible.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    /// <param name="payload">The provider's raw response text.</param>
    /// <param name="expectedSchemaVersion">The template's declared output schema version.</param>
    /// <param name="scope">The operation's scope, which bounds what a change may address.</param>
    public static AiOutputValidationResult Validate(
        string? payload,
        string expectedSchemaVersion,
        AiOperationScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Hygiene(document!)
            ?? Domain(document!, scope)
            ?? AiOutputValidationResult.Success(document!);
    }

    /// <summary>Stage 0: bound the payload before spending anything parsing it.</summary>
    private static AiOutputValidationResult? Envelope(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Reject(
                AiOutputReason.EmptyPayload,
                "The provider returned no content.",
                correctable: true);
        }

        // Counted in UTF-8 bytes rather than characters, because that is what the limit is protecting.
        if (Encoding.UTF8.GetByteCount(payload) > AiPolicy.OutputPayloadMaxBytes)
        {
            return Reject(
                AiOutputReason.PayloadTooLarge,
                $"The response exceeded {AiPolicy.OutputPayloadMaxBytes} bytes and was not parsed.",
                correctable: false);
        }

        return null;
    }

    /// <summary>
    /// Stage 2: the declared schema version, read before the body is deserialized.
    /// </summary>
    /// <remarks>
    /// Before the shape, deliberately. A different version means a different contract, so every complaint the
    /// shape stage could make would be about the wrong one — and "unknown field: servings" is a far worse
    /// explanation of a version mismatch than the mismatch itself.
    /// </remarks>
    private static AiOutputValidationResult? SchemaVersion(string payload, string expected)
    {
        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            // Stage 1. Truncated output lands here: the parser reaches the end of the document mid-value, and
            // nothing completes it.
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
                // The declared version is echoed because it came from a closed set the template controls, and
                // a mismatch is unexplainable without it. It is length-bounded like everything else here.
                return Reject(
                    AiOutputReason.SchemaVersionMismatch,
                    $"The response declares schema '{Sanitize(declared)}' but '{expected}' was required.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <summary>Stage 3: strict deserialization. Extra fields, wrong types and missing members all fail here.</summary>
    private static AiOutputValidationResult? Shape(string payload, out AiOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiOutputDocument>(payload, OutputJson);
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

    /// <summary>
    /// Stage 4: every string is bounded and free of characters that have no business in recipe text.
    /// </summary>
    /// <remarks>
    /// This is where a hostile string is handled, and handling it means <em>bounding</em> it, not
    /// interpreting it. A value containing system-prompt delimiters, injection wording or
    /// <c>{{placeholder}}</c> syntax is ordinary data and passes through untouched; what is refused is a value
    /// long enough to be a storage or display problem, and control characters that would corrupt the text
    /// wherever it is eventually shown. Tab, carriage return and line feed are exempt, because instruction
    /// text legitimately contains them.
    /// </remarks>
    private static AiOutputValidationResult? Hygiene(AiOutputDocument document)
    {
        foreach (var change in document.Changes)
        {
            var failure = CheckString(change.FieldName, AiPolicy.FieldNameMaxLength, "a field name")
                ?? CheckString(change.AfterValue, AiPolicy.ChangeValueMaxLength, "a proposed value");

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

    private static AiOutputValidationResult? CheckString(string? value, int maxLength, string what)
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

    /// <summary>
    /// Stage 5: the rules the type system cannot state. These mirror the database's check constraints, so a
    /// document that passes here cannot fail on insert.
    /// </summary>
    private static AiOutputValidationResult? Domain(AiOutputDocument document, AiOperationScope scope)
    {
        var allowed = AiPolicy.AllowedTargets(scope);

        foreach (var change in document.Changes)
        {
            if (change.ChangeKind is AiChangeKind.Unspecified
                || change.TargetKind is AiChangeTargetKind.Unspecified)
            {
                return Reject(
                    AiOutputReason.KindNotDeclared,
                    "A change does not declare its kind or its target kind.",
                    correctable: true);
            }

            if (!allowed.Contains(change.TargetKind))
            {
                return Reject(
                    AiOutputReason.OutsideScope,
                    $"A change targets {change.TargetKind}, which is outside the operation's {scope} scope.",
                    correctable: false);
            }

            var setsAField = change.ChangeKind is AiChangeKind.Set;

            if (setsAField != (change.FieldName is not null))
            {
                return Reject(
                    AiOutputReason.FieldNameMisplaced,
                    "A field name belongs to a set and to nothing else.",
                    correctable: true);
            }

            var placesAChild = change.ChangeKind is AiChangeKind.Add or AiChangeKind.Move;

            if (change.ProposedPosition is { } position && (!placesAChild || position < 0))
            {
                return Reject(
                    AiOutputReason.PositionMisplaced,
                    "A position belongs to an addition or a move, and is never negative.",
                    correctable: true);
            }

            if (change.ChangeKind is AiChangeKind.Move && change.ProposedPosition is null)
            {
                return Reject(
                    AiOutputReason.PositionMisplaced,
                    "A move must say where it puts the child.",
                    correctable: true);
            }

            if (change.AfterValue is null && change.ProposedPosition is null
                && change.ChangeKind is not AiChangeKind.Remove)
            {
                return Reject(
                    AiOutputReason.ChangeSaysNothing,
                    "A change proposes neither a value nor a position.",
                    correctable: true);
            }
        }

        return Conflicts(document) ?? WarningIndices(document);
    }

    /// <summary>
    /// Two changes addressing one row in incompatible ways.
    /// </summary>
    /// <remarks>
    /// Additions are excluded: adding two ingredients to one group is the ordinary case, not a conflict.
    /// Everything else addressing the same row must be a set of distinct fields — a row cannot be removed and
    /// edited, removed twice, or moved twice, and a diff containing any of those has no single meaning.
    /// </remarks>
    private static AiOutputValidationResult? Conflicts(AiOutputDocument document)
    {
        var groups = document.Changes
            .Where(change => change.ChangeKind is not AiChangeKind.Add)
            .GroupBy(change => (change.TargetKind, change.TargetId));

        foreach (var group in groups)
        {
            var structural = group.Count(change => change.ChangeKind is AiChangeKind.Remove or AiChangeKind.Move);
            var fields = group.Where(change => change.ChangeKind is AiChangeKind.Set)
                .Select(change => change.FieldName!)
                .ToList();

            var conflicting = structural > 1
                || (structural > 0 && group.Any(change => change.ChangeKind is AiChangeKind.Remove) && group.Count() > 1)
                || fields.Count != fields.Distinct(StringComparer.Ordinal).Count();

            if (conflicting)
            {
                return Reject(
                    AiOutputReason.ConflictingChanges,
                    $"Two changes address the same {group.Key.TargetKind} in ways that cannot both apply.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationResult? WarningIndices(AiOutputDocument document)
    {
        var outOfRange = document.Warnings
            .Any(warning => warning.ChangeIndex is { } index
                && (index < 0 || index >= document.Changes.Count));

        return outOfRange
            ? Reject(
                AiOutputReason.WarningIndexInvalid,
                "A warning points at a change that does not exist.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationResult Reject(string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.KindNotDeclared
            or AiOutputReason.FieldNameMisplaced
            or AiOutputReason.PositionMisplaced
            or AiOutputReason.ChangeSaysNothing
            or AiOutputReason.ConflictingChanges
            or AiOutputReason.OutsideScope
            or AiOutputReason.WarningIndexInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationResult.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    /// <summary>
    /// Makes a parser's own words safe to keep: single-line, and short enough that a fragment of the payload
    /// cannot ride along inside it.
    /// </summary>
    /// <remarks>
    /// <c>System.Text.Json</c> exception messages name a path and a position rather than quoting content, but
    /// that is its behaviour rather than a contract, so the value is bounded here regardless.
    /// </remarks>
    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
