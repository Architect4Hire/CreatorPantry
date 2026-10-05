using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary IMG-002's answer crosses to become an <see cref="AiImagePromptOutputDocument"/>, mirroring
/// <see cref="AiConceptOutputValidator"/>'s stages and its two guarantees — the payload appears on neither the
/// success nor the failure path, and nothing is repaired.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Four rules, and the one it deliberately does not have is the numeral ban.</strong> A photography
/// concept may write no figure because a shoot plan has none to write; a prompt legitimately says "4:5" or
/// "three loaves", and this is the text an image model reads. What is refused instead: a food claim, a link
/// or handle or markup, and a rendering directive — the last because the document has no field for one and a
/// model that wants to set a seed will otherwise write it into the prose a creator then has to edit out.
/// </para>
/// <para>
/// <strong>What the prompt asks for and this code does not enforce</strong>: that the prompt describes the
/// shot it was asked for rather than another; that it adds no ingredient the recipe does not list; that it
/// honours the concept's own look; and that it ignores an instruction planted in the brief. Each is a
/// question about the model's behaviour and is examined by the evaluation set, not asserted here.
/// </para>
/// </remarks>
public static class AiImagePromptOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // A provider that answers "avoid": null would otherwise crash this validator with a
        // NullReferenceException instead of being refused: System.Text.Json does not enforce a non-nullable
        // annotation unless asked, and a collection initialiser does not survive an explicit null.
        RespectNullableAnnotations = true,
    };

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiImagePromptOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiImagePromptOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiImagePromptOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? Shape(
        string payload, out AiImagePromptOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiImagePromptOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            // An unknown member is how a seed, a model name or an aspect-ratio field arrives here — refused
            // rather than dropped, so a renderer setting never reaches a creator's saved prompt unnoticed.
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
    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? Domain(
        AiImagePromptOutputDocument document)
    {
        var prompt = document.Prompt?.Trim() ?? string.Empty;

        if (prompt.Length < AiPolicy.ImagePromptMinLength || prompt.Length > AiPolicy.ImagePromptMaxLength)
        {
            return Reject(
                AiOutputReason.ImagePromptLengthOutOfRange,
                $"The prompt is {prompt.Length} characters; between {AiPolicy.ImagePromptMinLength} and "
                    + $"{AiPolicy.ImagePromptMaxLength} are required.",
                correctable: true);
        }

        if (document.Avoid.Count > AiPolicy.MaxImagePromptAvoidCount)
        {
            return Reject(
                AiOutputReason.ImagePromptAvoidInvalid,
                $"The response lists more than {AiPolicy.MaxImagePromptAvoidCount} things to avoid.",
                correctable: true);
        }

        foreach (var avoid in document.Avoid)
        {
            // The separator too: the handler joins this list with "; " into one stored row, so an entry
            // containing the separator would come back as two. The same class of bug the scene overrides had.
            if (string.IsNullOrWhiteSpace(avoid)
                || avoid.Length > AiPolicy.ImagePromptAvoidMaxLength
                || avoid.Contains(';', StringComparison.Ordinal))
            {
                return Reject(
                    AiOutputReason.ImagePromptAvoidInvalid,
                    "Each thing to avoid is a short phrase with no semicolon, and none may be blank.",
                    correctable: true);
            }
        }

        if (document.Warnings.Count > AiPolicy.MaxImagePromptWarnings)
        {
            return Reject(
                AiOutputReason.ImagePromptAvoidInvalid,
                $"The response carries more than {AiPolicy.MaxImagePromptWarnings} warnings.",
                correctable: true);
        }

        foreach (var warning in document.Warnings)
        {
            // Capped at the column's length, not the prompt's: AiWarning.Message is 1,000 characters, so a
            // longer warning would pass validation and then fail the write after the provider had been paid.
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
            var failure = Control(value, what)
                ?? Claims(value, what)
                ?? Text(value, what)
                ?? Directives(value, what);

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    /// <summary>Every piece of text a creator reads or a provider receives.</summary>
    private static IEnumerable<(string Value, string What)> Fields(AiImagePromptOutputDocument document)
    {
        yield return (document.Prompt, "the prompt");

        foreach (var avoid in document.Avoid)
        {
            yield return (avoid, "a thing to avoid");
        }

        // Warnings are read by the creator beside the prompt, so they are held to the same rules — the hole
        // the photography validator had on its first pass.
        foreach (var warning in document.Warnings)
        {
            yield return (warning.Message, "a warning message");
        }
    }

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? Control(string value, string what)
    {
        if (value.Length > AiPolicy.ImagePromptMaxLength)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response contains {what} longer than {AiPolicy.ImagePromptMaxLength} characters.",
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

            // Format characters as well as control ones: a zero-width space would otherwise let "--​seed"
            // or "gluten​free" past every rule below, since the regexes see fragments while the creator
            // reads the directive or the claim. This text is saved and sent to a provider, so it matters more
            // here than anywhere.
            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Format)
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with an invisible formatting character.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? Claims(string value, string what)
    {
        var match = ForbiddenClaim.Match(value);

        return match.Success
            ? Reject(
                AiOutputReason.ImagePromptClaimNotPermitted,
                $"The response made a claim about the food in {what} (\"{Sanitize(match.Value)}\"). A prompt "
                    + "describes a photograph, never what the dish is or does for anyone.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? Text(string value, string what) =>
        ForbiddenText.IsMatch(value)
            ? Reject(
                AiOutputReason.ImagePromptTextNotPermitted,
                $"The response put a link, an email address, an account handle, markup or a code fence in "
                    + $"{what}.",
                correctable: true)
            : null;

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument>? Directives(
        string value, string what)
    {
        var match = RenderDirective.Match(value);

        return match.Success
            ? Reject(
                AiOutputReason.ImagePromptRenderDirectiveNotPermitted,
                $"The response put a rendering directive in {what} (\"{Sanitize(match.Value)}\"). This "
                    + "composes a description of a photograph; what renders it, and with what settings, is not "
                    + "part of the prompt a creator saves.",
                correctable: true)
            : null;
    }

    /// <inheritdoc cref="AiPhotographyConceptOutputValidator"/>
    private static readonly Regex ForbiddenClaim = new(
        @"(?<![A-Za-z])(?:allergen|gluten|dairy|nut|peanut|wheat|sugar)[\s\p{Pd}]?free\b"
            + @"|(?<![A-Za-z])(?:egg|soy|lactose)[\s\p{Pd}]?free\b"
            + @"|\ballergy[\s\p{Pd}]?friendly\b"
            + @"|\bdiabetic[\s\p{Pd}]?friendly\b|\bcertified\s+(?:kosher|halal|organic)\b"
            + @"|\b(?:suitable for|safe for|free from)\b"
            + @"|\blow[\s\p{Pd}]?(?:carb|fat|calorie|sodium)\b|\bhigh[\s\p{Pd}]?protein\b"
            + @"|\b(?:healthy|healthful|nutritious|wholesome|detox)\b|\bimmune[\s\p{Pd}]?boosting\b"
            + @"|\b(?:guaranteed|safe to eat|cooked through|undercooked)\b|\bfood[\s\p{Pd}]?safe\b"
            + @"|\bauthentic\b|\bnon[\s\p{Pd}]?gmo\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <inheritdoc cref="AiPhotographyConceptOutputValidator"/>
    private static readonly Regex ForbiddenText = new(
        @"https?://|www\.|\bmailto:|[\w.%+-]+@[\w-]+\.[A-Za-z]{2,}|(?<![\w])@[\w.]{2,}"
            + @"|<[A-Za-z/!][^>]*>|\]\s*\([^)]*\)|```",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The shapes a rendering setting takes when a model writes it into prose.
    /// </summary>
    /// <remarks>
    /// Provider flags (<c>--ar</c>, <c>--seed</c>, <c>::2</c>), the settings themselves named with a value,
    /// and the provider or model names a prompt sometimes carries as a style instruction. Deliberately not
    /// "4:5" or "three loaves": an aspect ratio stated as part of the picture is a description, and a prompt
    /// that says how the frame is cropped is doing its job.
    /// </remarks>
    private static readonly Regex RenderDirective = new(
        // Provider flags and weightings.
        @"(?:^|\s)--[a-z]+\b|::\s*-?\d"

        // The settings by name, with or without a value: "sampler: Euler a" and "negative prompt: blurry"
        // carry no digit, and an earlier pass required one — which left the smuggling route the avoid-list
        // rule exists to close.
        + @"|\b(?:seed|steps|sampler|cfg|guidance[\s-]?scale|denoise|negative[\s-]?prompt"
        + @"|aspect[\s-]?ratio|style[\s-]?preset|checkpoint|lora)\b"

        // Number-first forms of the same thing: "30 steps", "7 guidance".
        + @"|\b\d+\s*(?:steps|iterations)\b"

        // Pixel dimensions and output sizes, which the prompt body bans by name.
        + @"|\b\d{3,5}\s*[x×]\s*\d{3,5}\b|\b\d+\s*dpi\b|\b[48]k\b"

        // Named renderers. A prompt that ties itself to one is a prompt a creator has to edit when they
        // change tools.
        + @"|\b(?:midjourney|stable[\s-]?diffusion|sdxl|dall[\s-]?e|flux|imagen|firefly|ideogram"
        + @"|leonardo|runway|comfyui|automatic1111|gpt[\s-]?image|nano[\s-]?banana)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static AiOutputValidationOutcome<AiImagePromptOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.ImagePromptLengthOutOfRange
            or AiOutputReason.ImagePromptAvoidInvalid
            or AiOutputReason.ImagePromptClaimNotPermitted
            or AiOutputReason.ImagePromptTextNotPermitted
            or AiOutputReason.ImagePromptRenderDirectiveNotPermitted
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiImagePromptOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
