using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary IMG-001's answer crosses to become an <see cref="AiPhotographyConceptOutputDocument"/>,
/// mirroring <see cref="AiConceptOutputValidator"/>'s stages and its two guarantees — the payload appears on
/// neither the success nor the failure path, and nothing is repaired — over this capability's own shape and
/// its own domain rules.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is specific to this validator is what a photography concept may not say.</strong> Three rules
/// carry IMG-001's restriction into code: no digit outside an allow-listed crop ratio, no phrasing from a
/// short list of dietary, health and safety claims, and no link, email address, handle, HTML tag, Markdown
/// link or code fence. The fourth — that no concept may carry a finished image prompt — needs no rule here at
/// all, because <see cref="AiPhotographyConceptOutputDocument"/> has no field for one and strict
/// deserialization refuses an unknown member.
/// </para>
/// <para>
/// <strong>What the prompt asks for and this code does not enforce</strong>, stated here so the gap is a
/// decision rather than a surprise: a number written as a word ("forty minutes") or as a vulgar fraction
/// passes the digit rule; a brand name, a trademark or a person's name has no rule at all; a doneness or
/// texture asserted as fact ("the crumb is fully set") reads as styling to this code; an ingredient the
/// pinned recipe does not list is not checked against the snapshot; and Markdown emphasis and headings are
/// not refused. Each is a thing the prompt forbids and a model is expected to honour, and none of them is
/// claimed as enforced. The evaluation set is where the prompt's own compliance is examined, and a green
/// validator is not evidence about it.
/// </para>
/// <para>
/// A photography concept names no recipe target, so there is no <see cref="AiOperationScope"/> question to ask
/// of it. <see cref="AsDelegate"/> still accepts one, to match
/// <see cref="AiOutputValidatorDelegate{TDocument}"/>, and ignores it.
/// </para>
/// </remarks>
public static class AiPhotographyConceptOutputValidator
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
    public static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Hygiene(document!)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiPhotographyConceptOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Shape(
        string payload, out AiPhotographyConceptOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiPhotographyConceptOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            // An unknown member is how a composed "prompt" field, or any other shape the model invented,
            // arrives here — refused rather than dropped, so the answer is never silently narrowed.
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

    /// <summary>Lengths and control characters, before any rule about meaning.</summary>
    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Hygiene(
        AiPhotographyConceptOutputDocument document)
    {
        foreach (var concept in document.Concepts)
        {
            var failure = CheckString(concept.Label, AiPolicy.PhotographyShortFieldMaxLength, "a concept label")
                ?? CheckString(concept.Mood, AiPolicy.PhotographyShortFieldMaxLength, "a mood")
                ?? CheckString(concept.Palette, AiPolicy.PhotographyShortFieldMaxLength, "a palette")
                ?? CheckString(concept.Rationale, AiPolicy.PhotographyFieldMaxLength, "a rationale")
                ?? CheckString(concept.ChannelFit, AiPolicy.PhotographyFieldMaxLength, "a channel-fit note");

            if (failure is not null)
            {
                return failure;
            }

            foreach (var shot in concept.Shots)
            {
                failure = CheckString(shot.Framing, AiPolicy.PhotographyFieldMaxLength, "a framing note")
                    ?? CheckString(shot.Lighting, AiPolicy.PhotographyFieldMaxLength, "a lighting note")
                    ?? CheckString(shot.Surface, AiPolicy.PhotographyFieldMaxLength, "a surface note")
                    ?? CheckString(shot.Styling, AiPolicy.PhotographyFieldMaxLength, "a styling note")
                    ?? CheckProps(shot.Props);

                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        foreach (var warning in document.Warnings)
        {
            // A warning is shown to the creator beside the concepts, so it is as much a place for a claim or a
            // link to land as a styling note is — and a likelier one, because it is where a model explains
            // itself. Claims and links are refused here too.
            //
            // Numerals are not: a warning's whole job may be to say that the recipe gives no baking time, or
            // that a 4:5 crop was assumed, and a figure quoted from the creator's own source is not an
            // invented recipe fact the way one in a shot description would be.
            var failure = CheckString(warning.Message, AiPolicy.MessageMaxLength, "a warning message")
                ?? Claims(warning.Message, "a warning message")
                ?? Text(warning.Message, "a warning message");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? CheckProps(
        IReadOnlyList<string> props)
    {
        if (props.Count > AiPolicy.MaxPhotographyPropCount)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"One shot lists more than {AiPolicy.MaxPhotographyPropCount} props.",
                correctable: true);
        }

        foreach (var prop in props)
        {
            var failure = CheckString(prop, AiPolicy.PhotographyPropMaxLength, "a prop");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? CheckString(
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

            // Format characters as well as control ones. A zero-width space is neither printable nor a control
            // character, so without this a model could write "gluten​free" and walk past every rule
            // below — the regexes would see two harmless fragments while the creator read the claim. Nothing
            // in a photography brief needs one.
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

    /// <summary>The rules the type system cannot state.</summary>
    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Domain(
        AiPhotographyConceptOutputDocument document)
    {
        if (document.Concepts.Count < AiPolicy.MinPhotographyConceptCount
            || document.Concepts.Count > AiPolicy.MaxPhotographyConceptCount)
        {
            return Reject(
                AiOutputReason.PhotographyConceptCountOutOfRange,
                $"The response proposed {document.Concepts.Count} concept(s); between "
                    + $"{AiPolicy.MinPhotographyConceptCount} and {AiPolicy.MaxPhotographyConceptCount} "
                    + "are required.",
                correctable: true);
        }

        foreach (var concept in document.Concepts)
        {
            var failure = Required(concept) ?? Roles(concept) ?? Prose(concept);

            if (failure is not null)
            {
                return failure;
            }
        }

        var distinctLabels = document.Concepts
            .Select(concept => concept.Label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (distinctLabels != document.Concepts.Count)
        {
            return Reject(
                AiOutputReason.PhotographyDuplicateLabel,
                "Two proposed concepts share a label, so they cannot be told apart.",
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

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Required(
        AiPhotographyConcept concept)
    {
        if (string.IsNullOrWhiteSpace(concept.Label)
            || string.IsNullOrWhiteSpace(concept.Mood)
            || string.IsNullOrWhiteSpace(concept.Palette)
            || string.IsNullOrWhiteSpace(concept.Rationale))
        {
            return Reject(
                AiOutputReason.PhotographyFieldMissing,
                "Every concept needs a label, a mood, a palette and a rationale.",
                correctable: true);
        }

        var blankShot = concept.Shots.Any(shot =>
            string.IsNullOrWhiteSpace(shot.Framing)
            || string.IsNullOrWhiteSpace(shot.Lighting)
            || string.IsNullOrWhiteSpace(shot.Surface)
            || string.IsNullOrWhiteSpace(shot.Styling));

        return blankShot
            ? Reject(
                AiOutputReason.PhotographyFieldMissing,
                "Every shot needs framing, lighting, a surface and styling.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Roles(
        AiPhotographyConcept concept)
    {
        if (concept.Shots.Count < AiPolicy.MinPhotographyShotCount
            || concept.Shots.Count > AiPolicy.MaxPhotographyShotCount)
        {
            return Reject(
                AiOutputReason.PhotographyShotCountOutOfRange,
                $"A concept planned {concept.Shots.Count} shot(s); between "
                    + $"{AiPolicy.MinPhotographyShotCount} and {AiPolicy.MaxPhotographyShotCount} are required.",
                correctable: true);
        }

        var heroes = concept.Shots.Count(shot => shot.Kind is AiPhotographyShotKind.Hero);

        if (heroes != 1)
        {
            return Reject(
                AiOutputReason.PhotographyShotRolesInvalid,
                heroes == 0
                    ? "A concept plans no hero shot, so there is no frame to lead with."
                    : "A concept plans more than one hero shot.",
                correctable: true);
        }

        var distinctKinds = concept.Shots.Select(shot => shot.Kind).Distinct().Count();

        return distinctKinds != concept.Shots.Count
            ? Reject(
                AiOutputReason.PhotographyShotRolesInvalid,
                "Two shots in one concept claim the same role, so they are not a shot list.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// What a concept may not say, applied to every field a creator reads.
    /// </summary>
    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Prose(
        AiPhotographyConcept concept)
    {
        foreach (var (value, what) in Fields(concept))
        {
            var failure = Numerals(value, what) ?? Claims(value, what) ?? Text(value, what);

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    /// <summary>Every piece of prose in one concept, with the name to report it by.</summary>
    private static IEnumerable<(string? Value, string What)> Fields(AiPhotographyConcept concept)
    {
        yield return (concept.Label, "a concept label");
        yield return (concept.Mood, "a mood");
        yield return (concept.Palette, "a palette");
        yield return (concept.Rationale, "a rationale");
        yield return (concept.ChannelFit, "a channel-fit note");

        foreach (var shot in concept.Shots)
        {
            yield return (shot.Framing, "a framing note");
            yield return (shot.Lighting, "a lighting note");
            yield return (shot.Surface, "a surface note");
            yield return (shot.Styling, "a styling note");

            foreach (var prop in shot.Props)
            {
                yield return (prop, "a prop");
            }
        }
    }

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Numerals(
        string? value, string what)
    {
        if (value is null)
        {
            return null;
        }

        // Crop ratios are the one numeric thing a concept legitimately states, so they are removed before the
        // digits are counted rather than parsed around.
        var withoutRatios = CropRatio.Replace(value, string.Empty);

        return withoutRatios.Any(char.IsDigit)
            ? Reject(
                AiOutputReason.PhotographyNumeralNotPermitted,
                $"The response wrote a numeral in {what}. A concept describes the picture in words; a "
                    + "quantity, time, temperature or yield would be invented, and camera specifications "
                    + "belong to the shot list rather than the look. Only a crop ratio may be a numeral.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Claims(
        string? value, string what)
    {
        if (value is null)
        {
            return null;
        }

        var match = ForbiddenClaim.Match(value);

        return match.Success
            ? Reject(
                AiOutputReason.PhotographyClaimNotPermitted,
                $"The response made a claim about the food in {what} (\"{Sanitize(match.Value)}\"). A "
                    + "photography concept describes how a dish looks, never what it is.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>? Text(string? value, string what)
    {
        if (value is null)
        {
            return null;
        }

        return ForbiddenText.IsMatch(value)
            ? Reject(
                AiOutputReason.PhotographyTextNotPermitted,
                $"The response put a link, an account handle, markup or a code fence in {what}.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// The aspect ratios a concept may state, and only these.
    /// </summary>
    /// <remarks>
    /// An allow-list rather than <c>\d+:\d+</c>, which would have exempted a clock time ("shoot at 10:30") and
    /// an arbitrary pair ("350:2") from the numeral ban — exactly the hole the ban exists to close. These are
    /// the ratios a photograph is actually framed to.
    /// </remarks>
    private static readonly Regex CropRatio = new(
        @"\b(?:1\s*:\s*1|4\s*:\s*5|5\s*:\s*4|3\s*:\s*4|4\s*:\s*3|2\s*:\s*3|3\s*:\s*2|9\s*:\s*16|16\s*:\s*9)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Claims about what the food <em>is</em>, which a shoot plan has no basis for (ai.md).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberately short list of the words that turn a description into an assurance. It is not a safety
    /// mechanism on its own — the prompt is what asks for none of them — but it is what stops one reaching a
    /// stored concept a creator then photographs and captions.
    /// </para>
    /// <para>
    /// <strong><c>organic</c> and <c>traditional</c> are deliberately absent</strong>, although the prompt bans
    /// both as claims. In a photography brief they are ordinary visual vocabulary — "organic, imperfect
    /// arrangement", "a traditional farmhouse table" — so refusing them here would reject sound concepts far
    /// more often than it caught a claim. They stay prompt-only, which is the honest division: the prompt asks
    /// for a claim-free brief, and this list refuses the phrasings that are claims in any context.
    /// </para>
    /// <para>
    /// <strong>The bare dietary labels went the same way, and that is a correction.</strong> An earlier pass
    /// added <c>vegan</c>, <c>vegetarian</c>, <c>kosher</c>, <c>halal</c>, <c>keto</c>, <c>paleo</c> and
    /// <c>plant-based</c> to this list. Each of them names a dish at least as often as it makes a claim:
    /// <strong>"kosher salt" is a prop a food photographer reaches for constantly</strong>, and a recipe
    /// called "Vegan Brownies" is a recipe, not an assurance. Refusing those would have failed sound answers
    /// for a word in the dish's own name, and the corrective re-ask could not have fixed it. What is matched
    /// instead is the claim frame — <c>certified kosher</c>, <c>suitable for</c>, <c>safe for</c>,
    /// <c>free from</c>, <c>diabetic-friendly</c> — and the <c>-free</c> compounds, which assert the absence
    /// of an allergen and are the one thing ai.md names outright.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A link, an email address, an account handle, an HTML tag, a Markdown link or a code fence.
    /// </summary>
    /// <remarks>
    /// The Markdown case is the link syntax only, not emphasis or headings: a bracketed link is the shape an
    /// exfiltration attempt takes, where <c>**bold**</c> in a styling note is a formatting slip the prompt
    /// already asks against and which is not worth refusing a whole answer over.
    /// </remarks>
    private static readonly Regex ForbiddenText = new(
        @"https?://|www\.|\bmailto:|[\w.%+-]+@[\w-]+\.[A-Za-z]{2,}|(?<![\w])@[\w.]{2,}"
            + @"|<[A-Za-z/!][^>]*>|\]\s*\([^)]*\)|```",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static AiOutputValidationOutcome<AiPhotographyConceptOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.PhotographyConceptCountOutOfRange
            or AiOutputReason.PhotographyShotCountOutOfRange
            or AiOutputReason.PhotographyFieldMissing
            or AiOutputReason.PhotographyShotRolesInvalid
            or AiOutputReason.PhotographyDuplicateLabel
            or AiOutputReason.PhotographyNumeralNotPermitted
            or AiOutputReason.PhotographyClaimNotPermitted
            or AiOutputReason.PhotographyTextNotPermitted
            or AiOutputReason.WarningIndexInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiPhotographyConceptOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
