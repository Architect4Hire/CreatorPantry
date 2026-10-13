using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary LF.3.1's answer crosses to become an <see cref="AiLongFormSectionOutputDocument"/>, mirroring
/// <see cref="AiLongFormOutlineOutputValidator"/>'s stages and its two guarantees — the payload appears on
/// neither the success nor the failure path, and nothing is repaired.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it refuses is what makes a body unusable as a section</strong>: no body; a second section, a
/// heading or a word count arriving as an unknown field; a heading line, a link, an address, a table, HTML or
/// a code fence inside the body; a control or invisible character; a body so long it is a runaway answer; and
/// a warning that is not a warning.
/// </para>
/// <para>
/// <strong>A body over the section ceiling is not refused.</strong> The ceiling is the content module's, it
/// is measured after generation, and an over-length body is returned whole with a finding beside it. Only
/// <see cref="AiPolicy.LongFormSectionBodyMaxLength"/> — four times that ceiling — is a refusal, because past
/// it the answer is no longer a section that ran long.
/// </para>
/// <para>
/// <strong>What it deliberately does not refuse.</strong> A figure the sources do not state, a safety,
/// allergen, dietary or nutrition claim, storage advice, a search metric and a visual detail are found
/// afterwards by the claim scanners and reported beside the section. Those words name dishes as often as they
/// assert anything, and a creator is better served by a section with a caution on it than by no section.
/// </para>
/// <para>
/// <strong>What the prompt asks for and no code enforces</strong>: that the body is about the section it was
/// asked for, that it reads like the creator, that it works from accepted copy where it was told to, and that
/// an instruction planted in the material is ignored. Each is a question about the model's behaviour.
/// </para>
/// </remarks>
public static class AiLongFormSectionOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
    };

    /// <summary>What marks a finding as the server's, or a warning as the model's.</summary>
    private static readonly string[] ServerFindingPrefixes =
        ["[editorial.", "[seo.", "[posts.", "[long_form.", "[content.", "[brand.", "[brand_context.", "[model]"];

    /// <summary>
    /// What a body may not carry: HTML, a code fence or span, a link or an image, a heading line of either
    /// kind, a rule, a table row, and a web or email address with or without a scheme in front of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A section's heading is the plan's and every heading level is the export's (LF.5.1), so a heading inside
    /// a body is either a second section or a level nothing downstream expects. No address was supplied to
    /// write around, so one in a body is invented.
    /// </para>
    /// <para>
    /// Emphasis, lists and block quotes are not here: they are what a body is allowed to be made of.
    /// </para>
    /// </remarks>
    private static readonly Regex Markup = new(
        string.Join(
            '|',

            // HTML, code spans and fences, inline and reference links, images.
            @"<[A-Za-z/!][^>]*>",
            @"`|~~~",
            @"\]\(|\]\[|!\[",
            @"^[ \t]{0,3}\[[^\]\r\n]+\]:",

            // A heading line, also when it sits inside a quote or a list item.
            @"^[ \t]{0,3}(?:>[ \t]*|[-*+][ \t]+|\d+[.)][ \t]+)*#{1,6}(?:[ \t]|\r?$)",

            // A line of text underlined into a heading, and a rule written with or without spaces.
            @"(?<=\S[^\r\n]*\r?\n)[ \t]{0,3}(?:=+|-{2,})[ \t]*\r?$",
            @"^[ \t]*(?:[-*_=][ \t]*){3,}\r?$",

            // A table row.
            @"^[ \t]*\|.*\|[ \t]*\r?$",

            // An address: with a scheme, without one, or to a person. The email form names what may not sit
            // either side of the sign, so it cannot be made to retry its way across a long run of them.
            @"\b(?:https?|ftp|mailto|tel|sms|javascript|data):",
            @"\bwww\.",
            @"[^\s@]@[^\s@.]+\.[^\s@]",
            @"\b[a-z0-9-]+\.(?:com|net|org|io|co|uk|me|ly|app|dev|xyz|info|biz|blog|shop|site|kitchen)\b"),
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline,
        TimeSpan.FromMilliseconds(250));

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiLongFormSectionOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiLongFormSectionOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiLongFormSectionOutputDocument> AsDelegate =>
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiLongFormSectionOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiLongFormSectionOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiLongFormSectionOutputDocument>? Shape(
        string payload, out AiLongFormSectionOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiLongFormSectionOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            // An unknown member is how a second section, a heading or a word count arrives here — refused
            // rather than dropped, so a section never quietly carries more than the creator asked for.
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
    private static AiOutputValidationOutcome<AiLongFormSectionOutputDocument>? Domain(
        AiLongFormSectionOutputDocument document)
    {
        var body = document.Body;

        if (string.IsNullOrWhiteSpace(body))
        {
            return Reject(AiOutputReason.LongFormSectionBodyInvalid, "The response has no body.", correctable: true);
        }

        // In UTF-16 units, so the bound holds whatever the text is made of and the pattern below is never run
        // over more than this. Not the section ceiling: see the class remarks.
        if (body.Length > AiPolicy.LongFormSectionBodyMaxLength)
        {
            return Reject(
                AiOutputReason.LongFormSectionBodyTooLong,
                $"The response's body is {Number(body.Length)} characters, which is far too long for one "
                    + "section. Write this one section only, and shorter.",
                correctable: true);
        }

        bool marked;

        try
        {
            marked = Markup.IsMatch(body);
        }
        catch (RegexMatchTimeoutException)
        {
            // A body the check could not finish reading is not one it passed. Refused here, as an answer, so
            // the attempt that was paid for is recorded — an exception would lose it and be paid for again.
            return Reject(
                AiOutputReason.LongFormSectionMarkupInvalid,
                "The body could not be checked for markup in the time allowed.",
                correctable: false);
        }

        if (marked)
        {
            return Reject(
                AiOutputReason.LongFormSectionMarkupInvalid,
                "The body carries a heading, a link, a web or email address, a table, HTML or code. A body is "
                    + "paragraphs, lists and emphasis only.",
                correctable: true);
        }

        return Characters(body, "the body") ?? Warnings(document);
    }

    private static AiOutputValidationOutcome<AiLongFormSectionOutputDocument>? Warnings(
        AiLongFormSectionOutputDocument document)
    {
        if (document.Warnings.Count > AiPolicy.MaxLongFormSectionWarnings)
        {
            return Reject(
                AiOutputReason.LongFormSectionWarningInvalid,
                $"The response carries more than {AiPolicy.MaxLongFormSectionWarnings} warnings.",
                correctable: true);
        }

        foreach (var warning in document.Warnings)
        {
            // Capped at the column's length less the label the handler stores a model warning under.
            if (warning.Kind is AiWarningKind.Unspecified
                || !Enum.IsDefined(warning.Kind)
                || string.IsNullOrWhiteSpace(warning.Message)
                || warning.Message.Length > AiPolicy.MessageMaxLength - AiPolicy.ModelWarningLabel.Length)
            {
                return Reject(
                    AiOutputReason.LongFormSectionWarningInvalid,
                    "A warning declares its kind and is a sentence that fits the space a warning has.",
                    correctable: true);
            }

            if (ServerFindingPrefixes.Any(prefix => warning.Message.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return Reject(
                    AiOutputReason.LongFormSectionWarningInvalid,
                    "A warning imitates a server finding; write the caution in your own words.",
                    correctable: true);
            }

            if (Characters(warning.Message, "a warning message") is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiLongFormSectionOutputDocument>? Characters(string value, string what) =>
        AiOutputCharacters.Find(value) switch
        {
            AiOutputCharacterFault.Control => Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with a control character.",
                correctable: true),
            AiOutputCharacterFault.Invisible => Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with an invisible formatting character.",
                correctable: true),
            _ => null,
        };

    private static AiOutputValidationOutcome<AiLongFormSectionOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.LongFormSectionBodyInvalid
            or AiOutputReason.LongFormSectionBodyTooLong
            or AiOutputReason.LongFormSectionMarkupInvalid
            or AiOutputReason.LongFormSectionWarningInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiLongFormSectionOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
