using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Validates one half of a style test drive against <see cref="AiBrandStyleSamplesOutputDocument"/> (11A.24).
/// Rejects and never repairs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same validator runs over both halves, and that is the point.</strong> If the half written
/// without the guide were held to looser rules than the half written with it, the screen would be comparing two
/// things the server had treated differently. Length floors and ceilings, hygiene and warnings are identical;
/// the only difference between the two calls is the material in the prompt.
/// </para>
/// <para>
/// <strong>Nothing is trimmed or padded.</strong> A sample over its limit fails and a sample under the floor
/// fails, because the alternative is a column the server co-wrote being shown to a creator as the model's own
/// writing — which is precisely what a comparison must not contain.
/// </para>
/// <para>
/// <strong>Hashtags are allowed in the caption and nowhere else; handles are allowed nowhere.</strong> A
/// hashtag names nothing and is part of how a caption reads, so refusing it would make the social sample a poor
/// demonstration of a social voice. A handle names a real account, which is an unverified claim about somebody
/// else — the same reason the editorial and SEO validators refuse links.
/// </para>
/// </remarks>
public static class AiBrandStyleSamplesOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // A provider that answers an explicit null for a non-nullable collection — "changes": null — would
        // otherwise crash this validator with a NullReferenceException rather than being refused: System.Text.Json
        // does not enforce a non-nullable annotation unless asked, and a collection initialiser does not survive
        // an explicit null. With this, the null is a classified, correctable rejection like any other bad shape.
        RespectNullableAnnotations = true,
    };

    // The editorial and SEO pattern, less the handle and hashtag alternation, which are judged separately below.
    private static readonly Regex LinkOrMarkup = new(
        @"\b(?:https?|ftp|data|mailto|javascript):|www\.|\[\.\]|\(dot\)|\bdot (?:com|net|org)\b|\b[a-z0-9-]{2,}\.(?:com|net|org|io|co|uk|app|dev|me|ly|shop|blog|info|xyz|de|ca|fr|es|it|nl|au|us|tv|ai)\b|\S+@\S+\.\S+|<\s*/?\s*[a-z!]|\]\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Handle = new(
        @"(?<!\w)@[\p{L}\p{N}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Hashtag = new(
        @"(?<!\w)#[\p{L}\p{N}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] ServerFindingPrefixes =
        ["[editorial.", "[seo.", "[brand.", "[brand_context.", "[model]"];

    public static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        var failure = Envelope(payload) ?? SchemaVersion(payload!, expectedSchemaVersion);

        if (failure is not null)
        {
            return failure;
        }

        failure = Shape(payload!, out var document)
            ?? Nulls(document!)
            ?? Hygiene(document!)
            ?? Domain(document!);

        return failure ?? AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>.Success(document!);
    }

    public static AiOutputValidatorDelegate<AiBrandStyleSamplesOutputDocument> AsDelegate() =>
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Shape(
        string payload, out AiBrandStyleSamplesOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiBrandStyleSamplesOutputDocument>(payload, OutputJson);
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

    /// <summary>JSON null into a non-nullable member is a shape failure, not a later null dereference.</summary>
    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Nulls(
        AiBrandStyleSamplesOutputDocument document)
    {
        var samples = document.Samples;

        var missing = samples is null
            || document.Warnings is null
            || samples.BlogIntro is null || samples.SocialCaption is null || samples.ImagePrompt is null
            || samples.BlogIntro.Text is null || samples.SocialCaption.Text is null || samples.ImagePrompt.Text is null
            || document.Warnings.Any(warning => warning is null)
            || document.Warnings.Any(warning => warning.Message is null);

        return missing
            ? Reject(
                AiOutputReason.ShapeInvalid,
                "The response has a null where the schema requires a value.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Hygiene(
        AiBrandStyleSamplesOutputDocument document)
    {
        if (document.Warnings.Count > AiPolicy.MaxStyleSampleWarnings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxStyleSampleWarnings} warnings.",
                correctable: true);
        }

        foreach (var sample in AiBrandStyleSampleCatalog.All)
        {
            var text = AiBrandStyleSampleCatalog.TextOf(document.Samples, sample);
            var what = Describe(sample);

            var failure = Characters(text, AiBrandStyleSampleCatalog.MaxLengthOf(sample) * 2, what)
                ?? Links(text, sample, what)
                ?? Spacing(text, sample, what);

            if (failure is not null)
            {
                return failure;
            }
        }

        foreach (var warning in document.Warnings)
        {
            var failure = Characters(
                warning.Message,
                AiPolicy.MessageMaxLength - AiPolicy.ModelWarningLabel.Length,
                "a warning message");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    /// <summary>
    /// A backstop on the raw string before anything inspects it, plus control and invisible characters. The
    /// exact verdict on length is <see cref="Domain"/>'s, measured in the characters a reader counts.
    /// </summary>
    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Characters(
        string value, int backstop, string what)
    {
        if (value.Length > backstop)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response contains {what} longer than {backstop} characters.",
                correctable: true);
        }

        return value.Any(character =>
            (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
            || char.GetUnicodeCategory(character) is UnicodeCategory.Format)
            ? Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with a control or invisible character.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// No link, address or markup anywhere, and no handle anywhere. A hashtag is allowed in the caption only —
    /// see the type's remarks for why those two are judged differently.
    /// </summary>
    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Links(
        string value, AiBrandStyleSample sample, string what)
    {
        // Raw and canonical, so a fullwidth or otherwise dressed-up address is read as the address it is.
        var canonical = AiEditorialProse.Canonicalize(value);

        if (LinkOrMarkup.IsMatch(value) || LinkOrMarkup.IsMatch(canonical))
        {
            return Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with markup or a link; write plain text.",
                correctable: true);
        }

        if (Handle.IsMatch(value) || Handle.IsMatch(canonical))
        {
            return Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} naming an account; a sample names nobody.",
                correctable: true);
        }

        return sample is not AiBrandStyleSample.SocialCaption
            && (Hashtag.IsMatch(value) || Hashtag.IsMatch(canonical))
            ? Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with a hashtag; only the caption takes one.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// What is stored is the string the model wrote, so what is measured has to be that string: no tab, no
    /// carriage return, no doubled space and no padding. A blog introduction and a caption may break into
    /// paragraphs; an image prompt is one line, because that is what a prompt is.
    /// </summary>
    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Spacing(
        string value, AiBrandStyleSample sample, string what)
    {
        var allowsNewLines = sample is not AiBrandStyleSample.ImagePrompt;

        return value.Any(character => character is '\t' or '\r' || (character is '\n' && !allowsNewLines))
            || value != value.Trim()
            || value.Contains("  ", StringComparison.Ordinal)
            ? Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with a tab, doubled space, padding or an unexpected line break.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>? Domain(
        AiBrandStyleSamplesOutputDocument document)
    {
        foreach (var sample in AiBrandStyleSampleCatalog.All)
        {
            var text = AiBrandStyleSampleCatalog.TextOf(document.Samples, sample);
            var length = Graphemes(text);
            var what = Describe(sample);

            if (string.IsNullOrWhiteSpace(text) || length < AiPolicy.StyleSampleMinLength)
            {
                return Reject(
                    AiOutputReason.StyleSampleMissing,
                    $"The response has {what} of {length} characters; a sample is at least "
                        + $"{AiPolicy.StyleSampleMinLength}. All three are required.",
                    correctable: true);
            }

            var max = AiBrandStyleSampleCatalog.MaxLengthOf(sample);

            if (length > max)
            {
                return Reject(
                    AiOutputReason.StyleSampleLengthOutOfRange,
                    $"The response has {what} of {length} characters; it must be at most {max}.",
                    correctable: true);
            }
        }

        if (document.Warnings.Any(warning => warning.Kind is AiWarningKind.Unspecified))
        {
            return Reject(AiOutputReason.KindNotDeclared, "Every warning declares its kind.", correctable: true);
        }

        if (document.Warnings.Any(warning => warning.Sample is AiBrandStyleSample.Unspecified))
        {
            return Reject(
                AiOutputReason.ShapeInvalid,
                "A warning names a sample without saying which; leave it out to warn about the whole answer.",
                correctable: true);
        }

        if (document.Warnings.Any(warning => string.IsNullOrWhiteSpace(warning.Message)))
        {
            return Reject(AiOutputReason.EditorialFieldMissing, "A warning has no message.", correctable: true);
        }

        return document.Warnings.Any(warning =>
            ServerFindingPrefixes.Any(prefix => warning.Message.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            ? Reject(
                AiOutputReason.DomainInvalid,
                "A warning imitates a server finding; write the caution in your own words.",
                correctable: true)
            : null;
    }

    private static string Describe(AiBrandStyleSample sample) => sample switch
    {
        AiBrandStyleSample.BlogIntro => "a blog introduction",
        AiBrandStyleSample.SocialCaption => "a social caption",
        AiBrandStyleSample.ImagePrompt => "an image prompt",
        _ => "a sample",
    };

    private static AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.StyleSampleMissing
            or AiOutputReason.StyleSampleLengthOutOfRange
            or AiOutputReason.EditorialFieldMissing
            or AiOutputReason.KindNotDeclared
            or AiOutputReason.DomainInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    /// <summary>Length as a reader counts it: user-perceived characters, so an emoji is one.</summary>
    private static int Graphemes(string text) => new StringInfo(text).LengthInTextElements;

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
