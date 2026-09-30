using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Validates a model's SEO package against <see cref="AiSeoPackageOutputDocument"/> and the configured
/// <see cref="SeoRules"/>. Rejects and never repairs: a title one character too long fails, it is not trimmed
/// into something the model never said.
/// </summary>
/// <remarks>
/// <para>
/// The length and format rules are enforced here, deterministically, from configuration — the prompt is told the
/// same limits but nothing relies on the model obeying them. Which assets and recipes exist is a fact about one
/// request, checked by the handler afterwards (<see cref="AiOutputReason.SeoItemNotInSource"/>).
/// </para>
/// <para>
/// Lengths are measured on the string as written, which is the string that is stored, in user-perceived characters; text that could measure
/// differently from how it is stored (line breaks, doubled spaces, padding) is refused.
/// Plain text only, with the same refusal of markup, links, handles, hashtags and invisible characters as the
/// editorial package: a link is an unverified claim about somewhere else, and there is no page to link to.
/// </para>
/// </remarks>
public static class AiSeoPackageOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Regex MarkupOrLink = new(
        @"\b(?:https?|ftp|data|mailto|javascript):|www\.|\[\.\]|\(dot\)|\bdot (?:com|net|org)\b|\b[a-z0-9-]{2,}\.(?:com|net|org|io|co|uk|app|dev|me|ly|shop|blog|info|xyz|de|ca|fr|es|it|nl|au|us|tv|ai)\b|\S+@\S+\.\S+|(?<!\w)[@#][a-z]\w|<\s*/?\s*[a-z!]|\]\(|`{3}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A phrase is lowercase words of letters and digits, joined by single spaces, hyphens or apostrophes.
    private static readonly Regex PhraseFormat = new(
        @"^[\p{L}\p{N}]+(?:['’-]?[\p{L}\p{N}]+)*(?: [\p{L}\p{N}]+(?:['’-]?[\p{L}\p{N}]+)*)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "Image of", "picture of": a screen reader already says it is an image, so the prefix is noise to hear.
    private static readonly Regex RedundantAltPrefix = new(
        @"^\s*(?:an? |the )?(?:image|picture|photo|photograph|graphic|screenshot|illustration)\s+(?:of|showing|depicting)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] ServerFindingPrefixes = ["[editorial.", "[seo.", "[model]"];

    /// <param name="context">
    /// What this request made available — the sections asked for, the images, the candidate recipes. When given, an
    /// answer that names anything outside it is refused <em>here</em>, so the gateway's one corrective re-ask can
    /// apply, instead of sinking the whole package after the call.
    /// </param>
    public static AiOutputValidationOutcome<AiSeoPackageOutputDocument> Validate(
        string? payload, string expectedSchemaVersion, SeoRules rules, AiSeoRequestContext? context = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);
        ArgumentNullException.ThrowIfNull(rules);

        var failure = Envelope(payload) ?? SchemaVersion(payload!, expectedSchemaVersion);

        if (failure is not null)
        {
            return failure;
        }

        failure = Shape(payload!, out var document)
            ?? Nulls(document!)
            ?? Hygiene(document!, rules)
            ?? Domain(document!, rules)
            ?? (context is null ? null : Request(document!, context));

        return failure ?? AiOutputValidationOutcome<AiSeoPackageOutputDocument>.Success(document!);
    }

    public static AiOutputValidatorDelegate<AiSeoPackageOutputDocument> AsDelegate(SeoRules rules, AiSeoRequestContext? context = null) =>
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion, rules, context);

    /// <summary>
    /// The answer against the request: only sections that were asked for, only images of this version, only
    /// recipes that were offered. A fact about one request, so it needs the context; nothing here touches a store.
    /// </summary>
    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Request(
        AiSeoPackageOutputDocument document, AiSeoRequestContext context)
    {
        if (AiSeoSectionCatalog.Present(document.Sections).Any(section => !context.Requested.Contains(section)))
        {
            return Reject(
                AiOutputReason.EditorialSectionNotRequested,
                "The answer wrote a section the request did not ask for; write only the sections named.",
                correctable: true);
        }

        foreach (var alt in document.Sections.AltText)
        {
            if (!context.AssetCaptions.TryGetValue(alt.AssetLinkId, out var caption))
            {
                return Reject(
                    AiOutputReason.SeoItemNotInSource,
                    "An alt text names an image that is not one of the recipe's images; use only the ids given.",
                    correctable: true);
            }

            if (alt.Basis is AiSeoAltTextBasis.Caption && string.IsNullOrWhiteSpace(caption))
            {
                return Reject(
                    AiOutputReason.SeoAltTextInvalid,
                    "An alt text claims to rest on a caption its image does not have; use basis RecipeTitle.",
                    correctable: true);
            }
        }

        return document.Sections.InternalLinks.Any(link => link.RecipeId == context.RecipeId || !context.CandidateIds.Contains(link.RecipeId))
            ? Reject(
                AiOutputReason.SeoItemNotInSource,
                "A link idea points at this recipe or at one that was not offered; use only the ids given.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? SchemaVersion(string payload, string expected)
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

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Shape(
        string payload, out AiSeoPackageOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiSeoPackageOutputDocument>(payload, OutputJson);
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

    /// <summary>JSON null into a non-nullable member is a shape failure, not a <see cref="NullReferenceException"/> later.</summary>
    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Nulls(AiSeoPackageOutputDocument document)
    {
        var sections = document.Sections;

        var missing = sections is null
            || document.Warnings is null
            || sections.KeyPhrases is null || sections.AltText is null || sections.InternalLinks is null
            || sections.KeyPhrases.Any(item => item is null)
            || sections.AltText.Any(item => item is null)
            || sections.InternalLinks.Any(item => item is null)
            || document.Warnings.Any(item => item is null);

        return missing
            ? Reject(AiOutputReason.ShapeInvalid, "The response has a null where the schema requires a value.", correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Hygiene(
        AiSeoPackageOutputDocument document, SeoRules rules)
    {
        var sections = document.Sections;

        if (sections.KeyPhrases.Count > rules.KeyPhraseMaxCount
            || sections.AltText.Count > AiPolicy.MaxSeoAltTexts
            || sections.InternalLinks.Count > rules.MaxInternalLinks
            || document.Warnings.Count > AiPolicy.MaxSeoWarnings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {rules.KeyPhraseMaxCount} key phrases, {AiPolicy.MaxSeoAltTexts} alt texts, "
                    + $"{rules.MaxInternalLinks} internal links or {AiPolicy.MaxSeoWarnings} warnings.",
                correctable: true);
        }

        foreach (var (text, limit, what) in Texts(document, rules))
        {
            var failure = CheckString(text, limit, what)
                ?? (what == "a warning message" ? null : SingleLine(text, what));

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static IEnumerable<(string? Text, int Limit, string What)> Texts(AiSeoPackageOutputDocument document, SeoRules rules)
    {
        var sections = document.Sections;

        // Generous here, because the configured length rules give the exact verdict, in Domain; this only stops a
        // pathological string being inspected at all.
        const int Backstop = 4000;

        if (sections.SeoTitle is not null) yield return (sections.SeoTitle.Text, Backstop, "a title");
        if (sections.MetaDescription is not null) yield return (sections.MetaDescription.Text, Backstop, "a meta description");

        foreach (var phrase in sections.KeyPhrases) yield return (phrase.Phrase, Backstop, "a key phrase");
        foreach (var alt in sections.AltText) yield return (alt.Text, Backstop, "an alt text");

        foreach (var link in sections.InternalLinks)
        {
            yield return (link.AnchorText, Backstop, "an anchor text");
            yield return (link.Reason, Backstop, "a link reason");
        }

        foreach (var warning in document.Warnings) yield return (warning.Message, AiPolicy.MessageMaxLength - AiPolicy.ModelWarningLabel.Length, "a warning message");
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? CheckString(string? value, int maxLength, string what)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            return Reject(AiOutputReason.ValueTooLong, $"The response contains {what} longer than {maxLength} characters.", correctable: true);
        }

        if (value.Any(character =>
            (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
            || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format))
        {
            return Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with a control or invisible character.",
                correctable: true);
        }

        // Raw and canonical, so a fullwidth or otherwise dressed-up address is read as the address it is.
        return MarkupOrLink.IsMatch(value) || MarkupOrLink.IsMatch(AiEditorialProse.Canonicalize(value))
            ? Reject(AiOutputReason.IllegalCharacters, $"The response contains {what} with markup or a link; write plain text.", correctable: true)
            : null;
    }

    /// <summary>
    /// One line, single spaces, no padding. What is stored is the string the model wrote, so the limits below are
    /// measured on that string; text whose stored form could differ from its measured form is refused instead.
    /// </summary>
    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? SingleLine(string? value, string what) =>
        value is not null
            && (value.Any(character => character is '\t' or '\n' or '\r')
                || value != value.Trim()
                || value.Contains("  ", StringComparison.Ordinal))
            ? Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with a line break, tab, doubled space or padding; write it on one line.",
                correctable: true)
            : null;

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Domain(
        AiSeoPackageOutputDocument document, SeoRules rules)
    {
        var sections = document.Sections;

        if (Texts(document, rules).Any(entry => string.IsNullOrWhiteSpace(entry.Text))
            || sections.AltText.Any(item => item.AssetLinkId == Guid.Empty)
            || sections.InternalLinks.Any(item => item.RecipeId == Guid.Empty))
        {
            return Reject(
                AiOutputReason.EditorialFieldMissing,
                "A title, description, phrase, alt text, anchor, reason or warning is empty, or an alt text or link names no asset or recipe.",
                correctable: true);
        }

        if (document.Warnings.Any(warning => warning.Kind is AiWarningKind.Unspecified))
        {
            return Reject(AiOutputReason.KindNotDeclared, "Every warning declares its kind.", correctable: true);
        }

        if (document.Warnings.Any(warning => ServerFindingPrefixes.Any(prefix => warning.Message.Contains(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            return Reject(AiOutputReason.DomainInvalid, "A warning imitates a server finding; write the caution in your own words.", correctable: true);
        }

        return Length(sections.SeoTitle?.Text, rules.TitleMinLength, rules.TitleMaxLength, "title")
            ?? Length(sections.MetaDescription?.Text, rules.MetaDescriptionMinLength, rules.MetaDescriptionMaxLength, "meta description")
            ?? KeyPhrases(sections.KeyPhrases, rules)
            ?? AltTexts(sections.AltText, rules)
            ?? Links(sections.InternalLinks, rules);
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Length(string? text, int min, int max, string what)
    {
        if (text is null)
        {
            return null;
        }

        var length = Graphemes(text);

        return length < min || length > max
            ? Reject(
                AiOutputReason.SeoLengthOutOfRange,
                $"The {what} is {length} characters; it must be {min} to {max}.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? KeyPhrases(
        IReadOnlyList<AiSeoKeyPhrase> phrases, SeoRules rules)
    {
        if (phrases.Count == 0)
        {
            return null;
        }

        if (phrases.Count < rules.KeyPhraseMinCount)
        {
            return Reject(
                AiOutputReason.SeoKeyPhraseInvalid,
                $"Key phrases come as a set of {rules.KeyPhraseMinCount} to {rules.KeyPhraseMaxCount}; {phrases.Count} were given.",
                correctable: true);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in phrases)
        {
            var phrase = entry.Phrase;
            var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (phrase != phrase.ToLowerInvariant()
                || !PhraseFormat.IsMatch(phrase)
                || words > rules.KeyPhraseMaxWords
                || Graphemes(phrase) < rules.KeyPhraseMinLength
                || Graphemes(phrase) > rules.KeyPhraseMaxLength
                || !seen.Add(phrase))
            {
                return Reject(
                    AiOutputReason.SeoKeyPhraseInvalid,
                    $"A key phrase must be unique, lowercase words only, at most {rules.KeyPhraseMaxWords} words and "
                        + $"{rules.KeyPhraseMinLength} to {rules.KeyPhraseMaxLength} characters.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? AltTexts(
        IReadOnlyList<AiSeoAltText> alts, SeoRules rules)
    {
        if (alts.Any(alt => alt.Basis is AiSeoAltTextBasis.Unspecified))
        {
            return Reject(AiOutputReason.KindNotDeclared, "Every alt text declares what it was written from.", correctable: true);
        }

        foreach (var alt in alts)
        {
            var text = alt.Text;

            if (Graphemes(text) > rules.AltTextMaxLength || RedundantAltPrefix.IsMatch(text))
            {
                return Reject(
                    AiOutputReason.SeoAltTextInvalid,
                    $"Alt text must be at most {rules.AltTextMaxLength} characters and must not begin \"image of\", \"picture of\" or similar.",
                    correctable: true);
            }
        }

        var distinctTexts = alts.Select(alt => alt.Text.ToLowerInvariant()).Distinct().Count();

        return distinctTexts != alts.Count || alts.Select(alt => alt.AssetLinkId).Distinct().Count() != alts.Count
            ? Reject(
                AiOutputReason.SeoAltTextInvalid,
                "Two images share an alt text, or one image has two; each image needs its own.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument>? Links(
        IReadOnlyList<AiSeoInternalLink> links, SeoRules rules)
    {
        if (links.Any(link => Graphemes(link.AnchorText) > rules.AnchorTextMaxLength
            || link.Reason.Length > AiPolicy.SeoReasonMaxLength))
        {
            return Reject(
                AiOutputReason.SeoLinkInvalid,
                $"An anchor must be at most {rules.AnchorTextMaxLength} characters and a reason at most {AiPolicy.SeoReasonMaxLength}.",
                correctable: true);
        }

        return links.Select(link => link.RecipeId).Distinct().Count() != links.Count
            ? Reject(AiOutputReason.SeoLinkInvalid, "Two link ideas point at the same recipe.", correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiSeoPackageOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.EditorialFieldMissing
            or AiOutputReason.SeoLengthOutOfRange
            or AiOutputReason.SeoKeyPhraseInvalid
            or AiOutputReason.SeoAltTextInvalid
            or AiOutputReason.SeoLinkInvalid
            or AiOutputReason.KindNotDeclared
            or AiOutputReason.DomainInvalid
            or AiOutputReason.EditorialSectionNotRequested
            or AiOutputReason.SeoItemNotInSource
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiSeoPackageOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    /// <summary>
    /// Length as a reader counts it: user-perceived characters, so an emoji or a letter with its accent is one.
    /// Not pixel width, which search engines truncate on and which this does not compute.
    /// </summary>
    private static int Graphemes(string text) => new StringInfo(text).LengthInTextElements;

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
