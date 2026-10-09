using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary a dish-name reading crosses to become an <see cref="AiDishFacetsOutputDocument"/>, mirroring
/// <see cref="AiOutputValidator"/>'s five stages and its two guarantees — the payload appears on neither the
/// success nor the failure path, and nothing is repaired — over a document with its own shape and rules.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stage 5 is where this capability's honesty is kept.</strong> The earlier stages check that an
/// answer is well formed; <see cref="Domain"/> checks that it is a <em>reading</em> rather than a guess
/// wearing a reading's clothes. Three rules carry that: a facet is answered at most once, a code arrives with
/// a confidence and a code's absence arrives with a reason instead, and every suggestion says which words led
/// to it.
/// </para>
/// <para>
/// <strong>What this validator deliberately cannot check is whether a code exists.</strong> The candidate
/// codes are catalogue rows read per request, and a static validator has no access to them — so
/// <see cref="DishFacetSuggestionAiTaskHandler"/> checks them against the same read it showed the model and
/// drops what does not match. Putting that here instead would mean either passing the catalogue through the
/// gateway's validator delegate or hard-coding a vocabulary that the database owns.
/// </para>
/// <para>
/// A reading addresses no change to a recipe, so there is no <see cref="AiOperationScope"/> question to ask
/// here the way <see cref="AiOutputValidator"/> asks one of a diff's targets. <see cref="AsDelegate"/> still
/// accepts a scope, to match <see cref="AiOutputValidatorDelegate{TDocument}"/>, and ignores it.
/// </para>
/// </remarks>
public static class AiDishFacetsOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // A provider answering an explicit null for a non-nullable collection — "suggestions": null — would
        // otherwise crash this validator rather than being refused: System.Text.Json does not enforce a
        // non-nullable annotation unless asked, and a collection initialiser does not survive an explicit
        // null. With this, the null is a classified, correctable rejection like any other bad shape.
        RespectNullableAnnotations = true,
    };

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiDishFacetsOutputDocument> Validate(
        string? payload, string expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Hygiene(document!)
            ?? Domain(document!)
            ?? AiOutputValidationOutcome<AiDishFacetsOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls.</summary>
    public static AiOutputValidatorDelegate<AiDishFacetsOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? SchemaVersion(
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

    /// <remarks>
    /// This is the stage that refuses an invented field — an <c>ingredients</c> list, a <c>calories</c>
    /// figure, an <c>allergens</c> array — because the document has no member for any of them and unmapped
    /// members are disallowed. The claim is rejected rather than dropped, so a model that tried to tell a
    /// creator what is in their dish fails loudly instead of having that part quietly deleted.
    /// </remarks>
    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? Shape(
        string payload, out AiDishFacetsOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiDishFacetsOutputDocument>(payload, OutputJson);
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

    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? Hygiene(
        AiDishFacetsOutputDocument document)
    {
        if (document.Suggestions.Count > AiPolicy.MaxDishFacetSuggestions)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxDishFacetSuggestions} facet suggestions.",
                correctable: true);
        }

        if (document.Warnings.Count > AiPolicy.MaxDishFacetWarnings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxDishFacetWarnings} warnings.",
                correctable: true);
        }

        foreach (var suggestion in document.Suggestions)
        {
            var failure =
                CheckString(suggestion.Code, AiPolicy.FieldNameMaxLength, "a facet code")
                ?? CheckString(suggestion.Rationale, AiPolicy.DishFacetRationaleMaxLength, "a facet rationale");

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

    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? CheckString(
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

    /// <summary>Stage 5: the rules the type system cannot state, and which this capability turns on.</summary>
    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? Domain(
        AiDishFacetsOutputDocument document) =>
        FacetsDeclared(document)
            ?? DistinctFacets(document)
            ?? AnsweredOrDeclined(document)
            ?? Rationales(document)
            ?? WarningKinds(document)
            ?? Claims(document);

    /// <summary>
    /// Every suggestion names a facet, and names a real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Unspecified</c> is what a model gets by omitting the property, so without that half a suggestion
    /// which never said which facet it was about would read as being about the first one in the list.
    /// </para>
    /// <para>
    /// <strong>The <see cref="Enum.IsDefined{TEnum}(TEnum)"/> half closes a real hole, not a theoretical
    /// one.</strong> <see cref="JsonStringEnumConverter"/> accepts integers as well as names by default, so
    /// <c>"facet": 7</c> deserializes to a value no member of the enum has — passing a check that only looks
    /// for <c>Unspecified</c>. Downstream that becomes a stored field named <c>facet.7.rationale</c> and a
    /// warning reading "The 7 reading…", which is a row nothing can read back and a sentence no creator can
    /// make sense of. Checked here rather than by disabling integers on the converter, because this states
    /// the rule where the other domain rules are and does not depend on a serializer setting to hold.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? FacetsDeclared(
        AiDishFacetsOutputDocument document) =>
        document.Suggestions.Any(suggestion =>
            suggestion.Facet is AiDishFacet.Unspecified || !Enum.IsDefined(suggestion.Facet))
            ? Reject(
                AiOutputReason.KindNotDeclared,
                "Every suggestion says which facet it is about, and names one this schema defines.",
                correctable: true)
            : null;

    /// <remarks>
    /// The stored rows are keyed by facet, so a second reading of the cuisine would overwrite the first.
    /// Refused rather than deduplicated: which of two contradictory readings a creator is shown is not this
    /// validator's choice to make silently.
    /// </remarks>
    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? DistinctFacets(
        AiDishFacetsOutputDocument document) =>
        document.Suggestions.Select(suggestion => suggestion.Facet).Distinct().Count()
            != document.Suggestions.Count
            ? Reject(
                AiOutputReason.DishFacetDuplicate,
                "Each facet is read at most once.",
                correctable: true)
            : null;

    /// <summary>
    /// A suggestion either names a code and says how sure it is, or names none — and the two must not be
    /// mixed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A code with no confidence is the failure this capability most needs to refuse: it is a guess that
    /// arrives looking exactly like a reading, and the surface would pre-fill a creator's control from it.
    /// </para>
    /// <para>
    /// A confidence with no code is refused too, and it is the subtler half. "I am fairly sure, about
    /// nothing" is not an answer, and tolerating it would mean the band and the code could disagree about
    /// whether a reading exists at all.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? AnsweredOrDeclined(
        AiDishFacetsOutputDocument document)
    {
        foreach (var suggestion in document.Suggestions)
        {
            var hasCode = !string.IsNullOrWhiteSpace(suggestion.Code);

            if (hasCode && suggestion.Confidence is AiDishFacetConfidence.Unspecified)
            {
                return Reject(
                    AiOutputReason.DishFacetConfidenceMisplaced,
                    "A suggestion that names a code says how sure it is.",
                    correctable: true);
            }

            if (!hasCode && suggestion.Confidence is not AiDishFacetConfidence.Unspecified)
            {
                return Reject(
                    AiOutputReason.DishFacetConfidenceMisplaced,
                    "A suggestion that names no code states no confidence: there is no reading to be "
                        + "confident about.",
                    correctable: true);
            }

            // An out-of-range integer, for FacetsDeclared's reason: a confidence of 9 is neither band, and
            // the handler would store the string "9" where the surface expects a name it can act on.
            if (hasCode && !Enum.IsDefined(suggestion.Confidence))
            {
                return Reject(
                    AiOutputReason.DishFacetConfidenceMisplaced,
                    "A suggestion's confidence names a band this schema defines, or none at all.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <remarks>
    /// Required whether or not a code was named, which is the point: a declined facet whose reason is blank
    /// tells the creator nothing, and a named code with no rationale is a selection they cannot check against
    /// the name they typed.
    /// </remarks>
    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? Rationales(
        AiDishFacetsOutputDocument document) =>
        document.Suggestions.Any(suggestion => string.IsNullOrWhiteSpace(suggestion.Rationale))
            ? Reject(
                AiOutputReason.DishFacetUnexplained,
                "Every suggestion says which words of the name led to it, or why none did.",
                correctable: true)
            : null;

    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? WarningKinds(
        AiDishFacetsOutputDocument document) =>
        document.Warnings.Any(warning =>
            warning.Kind is AiWarningKind.Unspecified || !Enum.IsDefined(warning.Kind))
            ? Reject(
                AiOutputReason.KindNotDeclared,
                "Every warning declares a kind this schema defines.",
                correctable: true)
            : null;

    /// <summary>
    /// Neither free-text field makes a claim about the food.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document has no <em>field</em> for a diet, an allergen or a safety verdict, and strict shape
    /// validation refuses an invented one — but it has two free-text fields, and that is not the same thing.
    /// A rationale is rendered beside the control it explains, so "fattoush, so naturally gluten free" would
    /// arrive as a finding about the creator's dish; a warning message reaches them just as directly.
    /// </para>
    /// <para>
    /// Against the same term list <see cref="AiImagePromptOutputValidator"/> and
    /// <see cref="AiPhotographyConceptOutputValidator"/> use, and kept in parity with them by
    /// <c>AiImageTextRuleParityTests</c>'s convention. <strong>Its limits are real:</strong> a denylist
    /// catches the phrasings that recur, not every possible one — "vegan" and "vegetarian" are absent from it
    /// deliberately, since those are descriptive tags elsewhere in the product. The template's instruction and
    /// the evaluation set carry the rest.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? Claims(
        AiDishFacetsOutputDocument document)
    {
        foreach (var suggestion in document.Suggestions)
        {
            var failure = Claim(suggestion.Rationale, "a facet rationale");

            if (failure is not null)
            {
                return failure;
            }
        }

        foreach (var warning in document.Warnings)
        {
            var failure = Claim(warning.Message, "a warning");

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument>? Claim(string? value, string what)
    {
        if (value is null)
        {
            return null;
        }

        var match = ForbiddenClaim.Match(value);

        return match.Success
            ? Reject(
                AiOutputReason.DishFacetClaimNotPermitted,
                $"The response made a claim about the food in {what} (\"{Sanitize(match.Value)}\"). Reading a "
                    + "dish name says what the dish is called, never what it contains or who it suits.",
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

    private static AiOutputValidationOutcome<AiDishFacetsOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        // The four domain rules are what this capability turns on; everything else here is a malformed answer.
        // The distinction is what the worker records and what a reviewer reads off a failed operation.
        var category = reasonCode is AiOutputReason.DishFacetDuplicate
            or AiOutputReason.DishFacetConfidenceMisplaced
            or AiOutputReason.DishFacetUnexplained
            or AiOutputReason.DishFacetClaimNotPermitted
            or AiOutputReason.KindNotDeclared
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiDishFacetsOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
