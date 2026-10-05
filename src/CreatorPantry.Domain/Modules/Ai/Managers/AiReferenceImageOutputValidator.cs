using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary IMG-004's answer crosses to become an <see cref="AiReferenceImageOutputDocument"/>, mirroring
/// <see cref="AiImagePromptOutputValidator"/>'s stages and its two guarantees — the payload appears on neither
/// the success nor the failure path, and nothing is repaired.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It adds one rule its sibling does not have, and it is the point of this capability.</strong> A
/// reading of someone's photograph may not identify a person in it or assert who owns a brand, a product or
/// the picture — two of the four things IMG-004's RESTRICTION forbids, and the two that are visible in the
/// text. The third, a food-safety or dietary verdict, is caught by the same claim ban every image capability
/// applies.
/// </para>
/// <para>
/// <strong>The fourth it cannot catch, and says so rather than pretending.</strong> "Do not infer unseen
/// ingredients" is a question about whether a named thing was visible, which nothing in this file can see.
/// The structural half is that <see cref="AiReferenceImageAspect"/> has no ingredient aspect to invite one;
/// the rest is the template's instruction and the evaluation set's fixtures.
/// </para>
/// <para>
/// <strong>What else the prompt asks for and this code does not enforce</strong>: that the reading describes
/// the attached image rather than a generic photograph; that the confidence stated matches how visible the
/// thing actually is; and that an instruction photographed into the image is not obeyed. Each is behaviour,
/// and a fixture against a fake provider cannot demonstrate any of them.
/// </para>
/// </remarks>
public static class AiReferenceImageOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // As every sibling validator: a provider answering "observations": null would otherwise throw a
        // NullReferenceException out of this method instead of being refused.
        RespectNullableAnnotations = true,
    };

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiReferenceImageOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiReferenceImageOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiReferenceImageOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Shape(
        string payload, out AiReferenceImageOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiReferenceImageOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            // An unknown member is how "personIdentified", "brandOwner" or a seed field arrives here —
            // refused rather than dropped, so a claim this document has no room for cannot be smuggled in
            // beside one it does.
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
    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Domain(
        AiReferenceImageOutputDocument document) =>
        Observations(document)
        ?? Prompt(document)
        ?? Avoid(document)
        ?? Warnings(document)
        ?? Text(document);

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Observations(
        AiReferenceImageOutputDocument document)
    {
        if (document.Observations.Count < AiPolicy.MinReferenceImageObservations
            || document.Observations.Count > AiPolicy.MaxReferenceImageObservations)
        {
            return Reject(
                AiOutputReason.ReferenceImageObservationsInvalid,
                $"The response carries {document.Observations.Count} observations; between "
                    + $"{AiPolicy.MinReferenceImageObservations} and "
                    + $"{AiPolicy.MaxReferenceImageObservations} are required.",
                correctable: true);
        }

        var seen = new HashSet<AiReferenceImageAspect>();

        foreach (var observation in document.Observations)
        {
            // Both enums refuse their zero member by name. Without this an answer that omitted either field
            // would deserialize to Unspecified and be stored as a reading of nothing, or as a reading whose
            // confidence the creator would read as the first level in the list.
            if (observation.Aspect is AiReferenceImageAspect.Unspecified
                || !Enum.IsDefined(observation.Aspect))
            {
                return Reject(
                    AiOutputReason.ReferenceImageObservationsInvalid,
                    "Every observation says which property of the photograph it is about.",
                    correctable: true);
            }

            if (observation.Confidence is AiReferenceImageConfidence.Unspecified
                || !Enum.IsDefined(observation.Confidence))
            {
                return Reject(
                    AiOutputReason.ReferenceImageObservationsInvalid,
                    "Every observation states how sure it is: clear, probable, or unclear.",
                    correctable: true);
            }

            if (!seen.Add(observation.Aspect))
            {
                // The rows are keyed by aspect, so a duplicate would overwrite rather than accumulate — the
                // creator would silently see one of two readings with no sign the other existed.
                return Reject(
                    AiOutputReason.ReferenceImageObservationsInvalid,
                    "The response observes the same property of the photograph twice.",
                    correctable: true);
            }

            var text = observation.Text?.Trim() ?? string.Empty;

            if (text.Length == 0 || text.Length > AiPolicy.ReferenceImageObservationMaxLength)
            {
                return Reject(
                    AiOutputReason.ReferenceImageObservationsInvalid,
                    $"Each observation is between 1 and {AiPolicy.ReferenceImageObservationMaxLength} "
                        + "characters, and none may be blank.",
                    correctable: true);
            }

            // The one part of "do not infer unseen ingredients" that is enforceable from the text alone.
            // Nothing here can know whether a named ingredient was visible — that is the evaluation set's
            // question — but it can know that "made with browned butter" is a statement about composition,
            // and composition is not something a photograph shows with certainty. So the claim is allowed
            // and the certainty is not: say it as Probable, or do not say it.
            if (observation.Confidence is AiReferenceImageConfidence.Clear
                && CompositionClaim.IsMatch(text))
            {
                return Reject(
                    AiOutputReason.ReferenceImageObservationsInvalid,
                    "An observation about what something is made with or contains cannot be clear from a "
                        + "photograph. Record it as probable, or leave it out.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Prompt(
        AiReferenceImageOutputDocument document)
    {
        var prompt = document.Prompt?.Trim() ?? string.Empty;

        // The same bounds IMG-002's prompt carries: it is the same artefact, saved to the same library
        // through the same route, and two different minimum lengths would be two different ideas of what a
        // prompt is.
        return prompt.Length < AiPolicy.ImagePromptMinLength
            || prompt.Length > AiPolicy.ImagePromptMaxLength
            ? Reject(
                AiOutputReason.ImagePromptLengthOutOfRange,
                $"The prompt is {prompt.Length} characters; between {AiPolicy.ImagePromptMinLength} and "
                    + $"{AiPolicy.ImagePromptMaxLength} are required.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Avoid(
        AiReferenceImageOutputDocument document)
    {
        if (document.Avoid.Count > AiPolicy.MaxImagePromptAvoidCount)
        {
            return Reject(
                AiOutputReason.ImagePromptAvoidInvalid,
                $"The response lists more than {AiPolicy.MaxImagePromptAvoidCount} things to avoid.",
                correctable: true);
        }

        foreach (var avoid in document.Avoid)
        {
            // The separator too: the handler joins this list into one stored row, so an entry containing it
            // would come back as two.
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

        return null;
    }

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Warnings(
        AiReferenceImageOutputDocument document)
    {
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
            // longer warning would pass here and fail the write after the provider had been paid.
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

        return null;
    }

    /// <summary>Every piece of text a creator reads, held to the same rules.</summary>
    /// <remarks>
    /// Observations and warnings are included, not just the prompt. The photography validator shipped its
    /// first pass with warnings exempt, which left the whole claim ban bypassable by writing the claim into a
    /// warning the creator reads beside the thing it is about.
    /// </remarks>
    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Text(
        AiReferenceImageOutputDocument document)
    {
        foreach (var (value, what) in Fields(document))
        {
            // Control first, because it refuses the invisible runes the comparison copy exists to survive.
            // Then every ban twice: once over what the creator will read, once over the composed, stripped
            // copy, so a claim assembled out of fragments is caught by whichever pass sees it whole.
            var failure = Control(value, what)
                ?? Bans(value, what)
                ?? Bans(Comparable(value), what);

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    /// <summary>The four content bans, in one place so both passes apply exactly the same rules.</summary>
    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Bans(string value, string what) =>
        Claims(value, what) ?? Identity(value, what) ?? Links(value, what) ?? Directives(value, what);

    private static IEnumerable<(string Value, string What)> Fields(AiReferenceImageOutputDocument document)
    {
        yield return (document.Prompt, "the prompt");

        foreach (var observation in document.Observations)
        {
            yield return (observation.Text, $"the {observation.Aspect} observation");
        }

        foreach (var avoid in document.Avoid)
        {
            yield return (avoid, "a thing to avoid");
        }

        foreach (var warning in document.Warnings)
        {
            yield return (warning.Message, "a warning message");
        }
    }

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Control(
        string value, string what)
    {
        if (value.Length > AiPolicy.ImagePromptMaxLength)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response contains {what} longer than {AiPolicy.ImagePromptMaxLength} characters.",
                correctable: true);
        }

        // Iterated as runes, not chars: a tag character (U+E0000 block) is a surrogate pair, so a char-wise
        // walk sees two Surrogates and no Format, and the whole invisible-character rule misses it.
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value is not ('\t' or '\n' or '\r'))
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with a control character.",
                    correctable: true);
            }

            // Every invisible way to break a word apart, because each one would let "owned by" or
            // "gluten free" past every regex below while the creator reads the claim intact: format
            // characters (a zero-width space), the combining grapheme joiner, variation selectors, and the
            // tag block. Combining marks in general are *not* refused — a decomposed accent is ordinary
            // text, and the normalised copy below is what stops one hiding a claim.
            if (Invisible(rune))
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with an invisible formatting character.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <summary>Whether a rune is invisible in a way that could break a word apart.</summary>
    private static bool Invisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format
        || rune.Value is 0x034F or 0x2060 or 0x3164 or 0xFFA0
        || rune.Value is >= 0xFE00 and <= 0xFE0F
        || rune.Value is >= 0xE0000 and <= 0xE007F;

    /// <summary>
    /// The text the ban patterns are matched against: composed, and with every invisible rune removed.
    /// </summary>
    /// <remarks>
    /// Belt and braces with <see cref="Invisible"/> above, which refuses those runes outright. This exists
    /// because the refusal is a judgement about <em>which</em> code points are invisible, and that list will
    /// never be complete — normalising and stripping means a code point nobody listed still cannot split
    /// "gluten free" into two fragments the regex reads as innocent. NFKC also folds the compatibility forms
    /// of letters, so a fullwidth or mathematical spelling cannot evade a pattern either.
    /// </remarks>
    private static string Comparable(string value)
    {
        var normalized = value.IsNormalized(NormalizationForm.FormKC)
            ? value
            : value.Normalize(NormalizationForm.FormKC);

        var builder = new StringBuilder(normalized.Length);

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (!Invisible(rune) && Rune.GetUnicodeCategory(rune) is not UnicodeCategory.NonSpacingMark)
            {
                builder.Append(rune);
            }
        }

        return builder.ToString();
    }

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Claims(
        string value, string what)
    {
        var match = ForbiddenClaim.Match(value);

        return match.Success
            ? Reject(
                AiOutputReason.ImagePromptClaimNotPermitted,
                $"The response made a claim about the food in {what} (\"{Sanitize(match.Value)}\"). A reading "
                    + "of a photograph describes what is visible, never what the dish is or does for anyone.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// The rule this capability adds: no identifying a person, and no asserting who owns what.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Identity is two regexes rather than one, deliberately.</strong> A single pattern wide enough to
    /// catch "the chef is identifiable as…" also catches "a pair of hands appears to be tearing the loaf",
    /// which is a sound observation about a photograph and must not be refused. So identity is only refused
    /// when a person is mentioned <em>and</em> the language of identification is used — the co-occurrence is
    /// the claim, and either alone is ordinary description.
    /// </para>
    /// <para>
    /// <strong>Ownership needs no co-occurrence.</strong> "Owned by", "trademarked", "courtesy of",
    /// "photographed by" assert a fact about a real company or a real photographer no matter what else the
    /// sentence says, and a reading of a creator's reference image has no way to know any of them.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Identity(
        string value, string what)
    {
        var ownership = ForbiddenOwnership.Match(value);

        if (ownership.Success)
        {
            return Reject(
                AiOutputReason.ReferenceImageIdentityClaimNotPermitted,
                $"The response asserted ownership or authorship in {what} "
                    + $"(\"{Sanitize(ownership.Value)}\"). A reading of a photograph cannot know who owns a "
                    + "brand, a product or the picture itself.",
                correctable: true);
        }

        if (!PersonMention.IsMatch(value))
        {
            return null;
        }

        // Either the language of identification, or the shape of a name beside a person. The second is the
        // case no vocabulary list can catch: a name identifies somebody without using any word about
        // identifying.
        var identification = PersonIdentification.Match(value);

        if (!identification.Success)
        {
            identification = NameShape.Match(value);
        }

        return identification.Success
            ? Reject(
                AiOutputReason.ReferenceImageIdentityClaimNotPermitted,
                $"The response identified a person in {what} (\"{Sanitize(identification.Value)}\"). Who "
                    + "someone is, is not something a photograph can be read for.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Links(string value, string what) =>
        ForbiddenText.IsMatch(value)
            ? Reject(
                AiOutputReason.ImagePromptTextNotPermitted,
                $"The response put a link, an email address, an account handle, markup or a code fence in "
                    + $"{what}.",
                correctable: true)
            : null;

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument>? Directives(
        string value, string what)
    {
        var match = RenderDirective.Match(value);

        return match.Success
            ? Reject(
                AiOutputReason.ImagePromptRenderDirectiveNotPermitted,
                $"The response put a rendering directive in {what} (\"{Sanitize(match.Value)}\"). This reads "
                    + "a photograph and composes a description of one; what renders an image, and with what "
                    + "settings, is not part of the prompt a creator saves.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// The food claims a reading of a photograph may not make.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third copy of this pattern in the module, and the duplication is deliberate rather than tidy: each
    /// validator owns its own refusals, and a shared mutable rule set would couple three capabilities'
    /// behaviour to one edit. It is not a verbatim copy, and the three divergences are the point:
    /// </para>
    /// <para>
    /// <strong>Compound words.</strong> The siblings write <c>\bnut[\s-]?free</c>, whose leading boundary
    /// cannot match inside "peanut-free" — there is no word boundary between "pea" and "nut". A lookbehind
    /// for a letter catches the compound as well as the bare word. <c>\p{Pd}</c> in place of a literal hyphen
    /// catches the non-breaking and en-dash spellings a model will occasionally produce.
    /// </para>
    /// <para>
    /// <strong>"Wholesome" and "authentic" are not banned here.</strong> They are in the siblings, where the
    /// only field is a prompt. This document has a <see cref="AiReferenceImageAspect.Mood"/> aspect, and
    /// "wholesome" is ordinary mood language about a photograph; refusing it would cost a paid retry to
    /// prevent a word that claims nothing about the food. The health claims proper — healthy, nutritious,
    /// detox — stay.
    /// </para>
    /// <para>
    /// <strong>"Free from", "safe for" and "suitable for" need a dietary object.</strong> Bare, they refuse
    /// "a background free from clutter" and "suitable for a flat lay", which are composition readings. What
    /// makes them claims is what follows them.
    /// </para>
    /// </remarks>
    private static readonly Regex ForbiddenClaim = new(
        @"(?<![A-Za-z])(?:allergen|gluten|dairy|nut|peanut|wheat|sugar|egg|soy|lactose)"
            + @"[\s\p{Pd}]?free\b"
            + @"|(?<![A-Za-z])(?:allergy|diabetic)[\s\p{Pd}]?friendly\b"
            + @"|\bcertified\s+(?:kosher|halal|organic|vegan)\b"
            + @"|\b(?:free from|safe for|suitable for)\s+(?:gluten|dairy|nuts?|peanuts?|eggs?|soy"
            + @"|lactose|wheat|shellfish|allergens?|coeliacs?|celiacs?|vegans?|vegetarians?|children"
            + @"|babies|infants|pregnancy|diabetics?)\b"
            + @"|\blow[\s\p{Pd}]?(?:carb|fat|calorie|sodium)\b|\bhigh[\s\p{Pd}]?protein\b"
            + @"|\b(?:healthy|healthful|nutritious|detox)\b|\bimmune[\s\p{Pd}]?boosting\b"
            + @"|\bguaranteed\b|\bsafe to eat\b|\bfood[\s\p{Pd}]?safe\b"
            + @"|\b(?:cooked through|fully cooked|properly cooked|undercooked|unsafe to eat)\b"
            + @"|\bnon[\s\p{Pd}]?gmo\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Links, addresses, handles and markup.
    /// </summary>
    /// <remarks>
    /// The bare-domain alternative is this capability's own: a model reading a photograph of packaging or a
    /// watermark will transcribe "janesbakery.com", which carries no scheme and so passed the siblings'
    /// pattern. The template bans a link "anywhere", and a domain is one.
    /// </remarks>
    private static readonly Regex ForbiddenText = new(
        @"https?://|www\.|\bmailto:|[\w.%+-]+@[\w-]+\.[A-Za-z]{2,}|(?<![\w])@[\w.]{2,}"
            + @"|<[A-Za-z/!][^>]*>|\]\s*\([^)]*\)|```"
            + @"|\b[\w-]{2,}\.(?:com|net|org|io|co|uk|shop|store|blog|app|dev)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The shapes a rendering setting takes when a model writes it into prose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provider flags, settings named with a value, pixel dimensions and renderer names — the same ban
    /// IMG-002 applies, because the prompt reaches the same library.
    /// </para>
    /// <para>
    /// <strong>Food words that are also settings need a value here.</strong> The siblings ban a bare
    /// <c>seed</c>, <c>steps</c> and <c>sampler</c>. In a reading of a photograph those are a poppy seed on a
    /// bun, stone steps behind a table and an embroidered sampler on a wall — all legitimate subject and
    /// styling vocabulary, and refusing one costs a paid retry and then a failed operation. What makes them
    /// directives is a value: "seed 4412", "steps: 30", "cfg = 7". The number-first forms are kept below.
    /// </para>
    /// </remarks>
    private static readonly Regex RenderDirective = new(
        @"(?:^|\s)--[a-z]+\b|::\s*-?\d"

        // Settings that need a value, because the bare word is food or furniture.
        + @"|\b(?:seed|steps|sampler|cfg|denoise)\b\s*[:=]?\s*\d"

        // Settings whose bare name is unambiguous: nothing in a kitchen is a guidance scale.
        + @"|\b(?:guidance[\s-]?scale|negative[\s-]?prompt|aspect[\s-]?ratio|style[\s-]?preset"
        + @"|checkpoint|lora)\b"

        // Number-first forms of the same thing: "30 steps", "7 guidance".
        + @"|\b\d+\s*(?:steps|iterations)\b"

        // Pixel dimensions and output sizes.
        + @"|\b\d{3,5}\s*[x×]\s*\d{3,5}\b|\b\d+\s*dpi\b|\b[48]k\b"

        // Named renderers. "Flux", "runway" and "firefly" are kept despite being ordinary words, because a
        // prompt that names a tool is a prompt the creator has to edit when they change tools — and the
        // reading of a photograph has no reason to mention any of them.
        + @"|\b(?:midjourney|stable[\s-]?diffusion|sdxl|dall[\s-]?e|flux|imagen|firefly|ideogram"
        + @"|leonardo|runway|comfyui|automatic1111|gpt[\s-]?image|nano[\s-]?banana)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Claims about who owns, made, or authored something, refused on their own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Made by" and "produced by" are deliberately absent: "a tart made by hand" is a reading of a
    /// photograph, and banning the phrase would refuse a sound answer to catch a claim the ownership verbs
    /// here already cover. "Shot by" is absent for the same reason — "a tight shot by the window" is framing.
    /// </para>
    /// <para>
    /// The three mark symbols are banned outright rather than allowed with a visibility frame. A creator who
    /// photographed packaging may still have the mark described — the template tells the model to say "a
    /// copyright mark is visible" rather than reproduce the glyph — and that keeps "visible on the label"
    /// from becoming a phrase that smuggles the assertion back in.
    /// </para>
    /// </remarks>
    private static readonly Regex ForbiddenOwnership = new(
        @"\b(?:owned by|belongs to|the property of|property of|trademark(?:ed|s)?"
            + @"|registered trademark|copyright(?:ed|s)?|courtesy of|credited to"
            + @"|photographed by|photo by|image by|picture by|taken by|styled by|designed by"
            + @"|art[\s-]?directed by)\b|[™®©]|\(c\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Whether a person is mentioned at all. Half of the identity rule; see <see cref="Identity"/>.</summary>
    private static readonly Regex PersonMention = new(
        @"\b(?:person|people|man|men|woman|women|child|children|girl|boy|chef|baker|cook|model"
            + @"|individual|face|faces|hand|hands|he|him|his|she|her|hers|they|them|their)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The language of identification. The other half: neither alone is a claim, together they are.
    /// </summary>
    /// <remarks>
    /// <strong>Frames, not stems.</strong> A first pass matched <c>identif\w*</c> and <c>recogni[sz]\w*</c>,
    /// which refused "a recognisable braided loaf, with her hands dusted in flour" — a sound reading, caught
    /// because "recognisable" and "her" happened to share a sentence. What identifies somebody is the frame:
    /// "recognisable <em>as</em>", "identified <em>as</em>", "named", "called". The adjective alone is
    /// ordinary description.
    /// </remarks>
    private static readonly Regex PersonIdentification = new(
        @"\b(?:(?:identified|identifiable|recogni[sz]able|recogni[sz]ed|known)\s+as"
            + @"|name[ds]?\s+(?:is|are|was|were)|named|called"
            + @"|celebrity|celebrities|influencer|public figure|famous)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Two consecutive capitalised words, which is what a personal name looks like in a sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hole the frame rule above cannot close: "This appears to be Gordon Ramsay plating the dish" uses
    /// no identification vocabulary at all, and a name is the most identifying thing an answer can carry.
    /// Nothing here knows which words are names, so what is detected is their shape — and it is only applied
    /// where a person is already mentioned, because "a New York deli counter" is a place and
    /// "a Dutch oven" is a pan.
    /// </para>
    /// <para>
    /// Mid-sentence only, so an ordinary sentence's opening capital cannot pair with the word after it.
    /// </para>
    /// </remarks>
    private static readonly Regex NameShape = new(
        @"(?<=[a-z,] )\p{Lu}\p{Ll}+\s+\p{Lu}\p{Ll}+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Statements about what something is made of, which a photograph cannot establish with certainty.
    /// </summary>
    /// <remarks>
    /// Not an ingredient list — a list would be endless and would refuse "a poppy seed crust", which is a
    /// reading of what is visible. These are the <em>frames</em> that turn a visible thing into a claim about
    /// composition: "made with", "contains", "baked with". The frame is permitted; stating it as certain is
    /// not, which is the narrowest honest enforcement of IMG-004's hardest restriction.
    /// </remarks>
    private static readonly Regex CompositionClaim = new(
        @"\b(?:made\s+(?:with|from|using)|contains|baked\s+with|flavou?red\s+with|infused\s+with"
            + @"|the\s+recipe\s+(?:uses|calls)|seasoned\s+with|sweetened\s+with)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static AiOutputValidationOutcome<AiReferenceImageOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.ReferenceImageObservationsInvalid
            or AiOutputReason.ReferenceImageIdentityClaimNotPermitted
            or AiOutputReason.ImagePromptLengthOutOfRange
            or AiOutputReason.ImagePromptAvoidInvalid
            or AiOutputReason.ImagePromptClaimNotPermitted
            or AiOutputReason.ImagePromptTextNotPermitted
            or AiOutputReason.ImagePromptRenderDirectiveNotPermitted
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiReferenceImageOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
