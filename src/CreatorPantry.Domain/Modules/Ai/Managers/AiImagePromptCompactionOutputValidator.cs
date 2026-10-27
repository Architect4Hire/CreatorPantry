using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary a shorter image prompt crosses to become an <see cref="AiImagePromptCompactionOutputDocument"/>
/// (AP.3.2), mirroring <see cref="AiImagePromptOutputValidator"/>'s stages: the payload appears on neither path,
/// and nothing is repaired.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The budget is a hard ceiling, and an answer over it is refused, never cut.</strong> The model is asked
/// for nine tenths of it (<see cref="AiPolicy.ImagePromptCompactionAimPercent"/>); this holds it to all of it,
/// and an answer one character over gets the gateway's single corrective re-ask and then fails. Cutting it here
/// would send an image model a sentence nobody wrote.
/// </para>
/// <para>
/// <strong>The prose rules are IMG-002's own</strong>, through <see cref="AiImagePromptOutputValidator.ProseFailure"/>:
/// a shorter prompt is the same artefact as a composed one, sent to the same model and saved to the same library.
/// </para>
/// <para>
/// <strong>What this cannot check</strong>: that the shorter prompt describes the same picture, adds nothing, and
/// lists everything it dropped. Those are questions about the model's behaviour, and the live check measures them.
/// </para>
/// </remarks>
public static class AiImagePromptCompactionOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // "leftOut": null is refused rather than crashing the validator, for the reason IMG-002's gives.
        RespectNullableAnnotations = true,
    };

    /// <summary>Validates one model answer against its schema version and the budget the request was given.</summary>
    public static AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument> Validate(
        string? payload, string expectedSchemaVersion, int promptBudget)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(promptBudget, AiPolicy.ImagePromptMinLength);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Domain(document!, promptBudget)
            ?? AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls, for one request's budget.</summary>
    public static AiOutputValidatorDelegate<AiImagePromptCompactionOutputDocument> For(int promptBudget) =>
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion, promptBudget);

    private static AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument>? Envelope(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Reject(AiOutputReason.EmptyPayload, "The provider returned no content.", correctable: true);
        }

        return Encoding.UTF8.GetByteCount(payload) > AiPolicy.OutputPayloadMaxBytes
            ? Reject(
                AiOutputReason.PayloadTooLarge,
                $"The response exceeded {AiPolicy.OutputPayloadMaxBytes} bytes and was not parsed.",
                correctable: false)
            : null;
    }

    private static AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument>? SchemaVersion(
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

            if (!string.Equals(version.GetString(), expected, StringComparison.Ordinal))
            {
                return Reject(
                    AiOutputReason.SchemaVersionMismatch,
                    $"The response declares schema '{Sanitize(version.GetString())}' but '{expected}' was "
                        + "required.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument>? Shape(
        string payload, out AiImagePromptCompactionOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiImagePromptCompactionOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            // An unknown member is how an avoid list, a seed or a model name arrives here. An avoid list matters
            // most: the creator's own travels beside this answer, and a second one would have to be reconciled
            // with it.
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

    /// <summary>The rules the type system cannot state.</summary>
    private static AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument>? Domain(
        AiImagePromptCompactionOutputDocument document, int promptBudget)
    {
        var prompt = document.Prompt?.Trim() ?? string.Empty;

        if (prompt.Length < AiPolicy.ImagePromptMinLength || prompt.Length > promptBudget)
        {
            return Reject(
                AiOutputReason.ImagePromptLengthOutOfRange,
                $"The shorter prompt is {prompt.Length} characters; between {AiPolicy.ImagePromptMinLength} and "
                    + $"{promptBudget} are required.",
                correctable: true);
        }

        // The creator's avoid list is appended under this heading when the picture is asked for. A shorter prompt
        // carrying one of its own would send the model two lists, one of which the creator never wrote.
        if (prompt.Contains("avoid the following", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                AiOutputReason.ImagePromptAvoidInvalid,
                "The shorter prompt carries an avoid list of its own. The creator's is kept apart from it and added "
                    + "when the picture is asked for.",
                correctable: true);
        }

        if (document.LeftOut.Count > AiPolicy.MaxImagePromptLeftOutCount)
        {
            return Reject(
                AiOutputReason.ImagePromptLeftOutInvalid,
                $"The response lists more than {AiPolicy.MaxImagePromptLeftOutCount} things left out.",
                correctable: true);
        }

        foreach (var phrase in document.LeftOut)
        {
            if (string.IsNullOrWhiteSpace(phrase) || phrase.Length > AiPolicy.ImagePromptLeftOutMaxLength)
            {
                return Reject(
                    AiOutputReason.ImagePromptLeftOutInvalid,
                    $"Each thing left out is a phrase of at most {AiPolicy.ImagePromptLeftOutMaxLength} "
                        + "characters, and none may be blank.",
                    correctable: true);
            }
        }

        if (document.Warnings.Count > AiPolicy.MaxImagePromptWarnings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxImagePromptWarnings} warnings.",
                correctable: true);
        }

        foreach (var warning in document.Warnings)
        {
            if (string.IsNullOrWhiteSpace(warning.Message)
                || warning.Message.Length > AiPolicy.MessageMaxLength)
            {
                return Reject(
                    AiOutputReason.ValueTooLong,
                    $"A warning is a sentence of at most {AiPolicy.MessageMaxLength} characters, and none may "
                        + "be blank.",
                    correctable: true);
            }
        }

        foreach (var (value, what) in Fields(document))
        {
            if (AiImagePromptOutputValidator.ProseFailure(value, what) is { } failure)
            {
                return AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument>.Failed(failure);
            }
        }

        return null;
    }

    /// <summary>Every piece of text a creator reads or a provider receives.</summary>
    private static IEnumerable<(string Value, string What)> Fields(AiImagePromptCompactionOutputDocument document)
    {
        yield return (document.Prompt, "the prompt");

        foreach (var phrase in document.LeftOut)
        {
            yield return (phrase, "a thing left out");
        }

        foreach (var warning in document.Warnings)
        {
            yield return (warning.Message, "a warning message");
        }
    }

    private static AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.ImagePromptLengthOutOfRange
            or AiOutputReason.ImagePromptLeftOutInvalid
            or AiOutputReason.ImagePromptAvoidInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiImagePromptCompactionOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
