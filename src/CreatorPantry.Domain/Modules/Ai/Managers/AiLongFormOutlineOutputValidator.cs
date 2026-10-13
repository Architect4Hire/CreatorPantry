using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What one outline request is held to, beyond its schema.</summary>
/// <param name="RecipeCount">
/// How many recipes the task was shown. A newsletter plan has exactly this many items, one for each.
/// </param>
/// <param name="WantedRoles">The roles the creator asked for. Each must be in the plan.</param>
/// <param name="Plans">The content module's shape rules, asked rather than restated.</param>
public sealed record AiLongFormOutlineExpectation(
    LongFormKind Kind,
    int RecipeCount,
    IReadOnlyCollection<LongFormSectionRole> WantedRoles,
    ILongFormPlanFacade Plans);

/// <summary>
/// The boundary LF.2.1's answer crosses to become an <see cref="AiLongFormOutlineOutputDocument"/>, mirroring
/// <see cref="AiChannelPostsOutputValidator"/>'s stages and its two guarantees — the payload appears on neither
/// the success nor the failure path, and nothing is repaired.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it refuses is what makes a plan unusable as a whole</strong>: a role outside the vocabulary or
/// outside this kind, a once-only role used twice, a heading or an intent that is not one plain line, a
/// newsletter whose items do not match its sources one to one, a section the creator asked for left out, and a
/// warning that is not a warning. A bad section is never dropped to rescue the rest, and a role is never
/// swapped for its nearest neighbour — both would be repairing the answer.
/// </para>
/// <para>
/// <strong>The role rules are not written here.</strong> Which roles a kind allows and how often is the
/// content module's (<see cref="ILongFormPlanFacade"/>), so the plan a model proposes and the plan a creator
/// edits are held to one definition.
/// </para>
/// <para>
/// <strong>What it deliberately does not refuse.</strong> A safety, allergen, dietary or health claim, an
/// unsupported figure and a search metric in a heading or an intent are found afterwards by the claim scanners
/// and reported beside the section — those words name dishes as often as they assert anything, and refusing
/// here would cost the creator a whole plan over one phrase.
/// </para>
/// <para>
/// <strong>What the prompt asks for and no code enforces</strong>: that the plan is about the piece it was
/// given, that an intent describes a section rather than writing its first line, and that an instruction
/// planted in a brief, a recipe line or an accepted paragraph is ignored. Each is a question about the model's
/// behaviour, examined by review of real output.
/// </para>
/// </remarks>
public static class AiLongFormOutlineOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
    };

    private const int ZeroWidthJoiner = 0x200D;

    private const int EmojiVariationSelector = 0xFE0F;

    private const int KeycapMark = 0x20E3;

    /// <summary>The most UTF-16 units one counted character of a heading or an intent may take.</summary>
    private const int MaxUnitsPerCharacter = 10;

    /// <summary>
    /// What marks a finding as the server's, or a warning as the model's. A model warning that carries one
    /// would read, beside the real ones, as the server vouching for it.
    /// </summary>
    private static readonly string[] ServerFindingPrefixes =
        ["[editorial.", "[seo.", "[posts.", "[long_form.", "[brand.", "[brand_context.", "[model]"];

    /// <summary>
    /// What a heading or an intent may not carry: HTML, a code fence or span, link or emphasis markup, a
    /// leading block mark, and a web or email address — with or without a scheme in front of it.
    /// </summary>
    /// <remarks>
    /// A plan's text is shown as typed and edited in a plain field. Markup in it is either an attempt at a body
    /// or noise a creator would have to delete, and no address was supplied to plan around.
    /// </remarks>
    private static readonly Regex Markup = new(
        @"<[A-Za-z/!][^>]*>|`|\]\(|\*\*|__|^\s*(?:#{1,6}|[-*+>]|\d+[.)])\s|https?://|\bwww\.|\S@\S+\.\S|\b[a-z0-9-]+\.(?:com|net|org|io|co|uk|blog|kitchen)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    public static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument> Validate(
        string? payload, string expectedSchemaVersion, AiLongFormOutlineExpectation expectation)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);
        ArgumentNullException.ThrowIfNull(expectation);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Domain(document!, expectation)
            ?? AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls, bound to one request.</summary>
    public static AiOutputValidatorDelegate<AiLongFormOutlineOutputDocument> For(AiLongFormOutlineExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);

        return (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion, expectation);
    }

    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Shape(
        string payload, out AiLongFormOutlineOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiLongFormOutlineOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            // An unknown member is how a body, a word count or a draft paragraph arrives here — refused rather
            // than dropped, so a plan never carries prose the creator did not approve a plan for.
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
    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Domain(
        AiLongFormOutlineOutputDocument document, AiLongFormOutlineExpectation expectation)
    {
        if (document.Sections.Count is 0 or > AiPolicy.MaxLongFormOutlineSections)
        {
            return Reject(
                AiOutputReason.LongFormOutlineSectionsInvalid,
                $"A plan has between 1 and {AiPolicy.MaxLongFormOutlineSections} sections.",
                correctable: true);
        }

        var rules = expectation.Plans.Describe(expectation.Kind);
        var planned = new List<LongFormPlanSectionServiceModel>(document.Sections.Count);

        for (var index = 0; index < document.Sections.Count; index++)
        {
            var section = document.Sections[index];
            var number = index + 1;

            // The name is the model's own text, so it is not echoed: a failure message is stored, and is read
            // back to the model in the corrective turn.
            if (!LongFormOutlineInputs.TryRole<LongFormSectionRole>(section.Role, out var role))
            {
                return Reject(
                    AiOutputReason.LongFormOutlineRoleUnknown,
                    $"Section {number} names a role that is not one of the roles listed.",
                    correctable: true);
            }

            if (Line(section.Heading, rules.HeadingMaxCharacters, $"the heading of section {number}", "heading",
                    AiOutputReason.LongFormOutlineHeadingInvalid) is { } heading)
            {
                return heading;
            }

            if (Line(section.Intent, rules.IntentMaxCharacters, $"the intent of section {number}", "intent",
                    AiOutputReason.LongFormOutlineIntentInvalid) is { } intent)
            {
                return intent;
            }

            planned.Add(new LongFormPlanSectionServiceModel(role, section.Heading, section.Intent));
        }

        if (Roles(expectation, planned) is { } roleFailure)
        {
            return roleFailure;
        }

        if (Items(expectation, document, planned) is { } itemFailure)
        {
            return itemFailure;
        }

        // Ordered, so the same answer is always refused for the same role.
        if (expectation.WantedRoles.Except(planned.Select(section => section.Role)).Order().FirstOrDefault() is var missing
            && missing != default)
        {
            return Reject(
                AiOutputReason.LongFormOutlineWantedSectionMissing,
                $"The plan has no {missing} section, which the creator asked for.",
                correctable: true);
        }

        return Warnings(document);
    }

    /// <summary>Asks the content module whether the plan's roles hold together, and says which rule broke.</summary>
    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Roles(
        AiLongFormOutlineExpectation expectation, IReadOnlyList<LongFormPlanSectionServiceModel> planned)
    {
        // The first finding, in the order the rules report them: one refusal names one thing to put right.
        if (expectation.Plans.Check(expectation.Kind, planned).FirstOrDefault() is not { } finding)
        {
            return null;
        }

        var where = finding.SectionIndex is { } index ? $"Section {index + 1}" : "The plan";

        return finding.Code switch
        {
            LongFormFindingCodes.RoleNotAllowed => Reject(
                AiOutputReason.LongFormOutlineRoleNotAllowed,
                $"{where} uses a role this kind of piece does not have.",
                correctable: true),
            LongFormFindingCodes.RoleRepeated => Reject(
                AiOutputReason.LongFormOutlineRoleRepeated,
                $"{where} repeats a role more often than it may appear.",
                correctable: true),
            LongFormFindingCodes.UnknownRole => Reject(
                AiOutputReason.LongFormOutlineRoleUnknown,
                $"{where} names a role that is not one of the roles listed.",
                correctable: true),
            LongFormFindingCodes.HeadingBlank or LongFormFindingCodes.HeadingTooLong => Reject(
                AiOutputReason.LongFormOutlineHeadingInvalid,
                $"{where} has a heading that is blank or too long.",
                correctable: true),
            LongFormFindingCodes.IntentBlank or LongFormFindingCodes.IntentTooLong
                or LongFormFindingCodes.IntentNotOneLine => Reject(
                    AiOutputReason.LongFormOutlineIntentInvalid,
                    $"{where} has an intent that is not one short line.",
                    correctable: true),
            _ => Reject(
                AiOutputReason.LongFormOutlineSectionsInvalid,
                $"{where} does not meet the rules for a plan.",
                correctable: true),
        };
    }

    /// <summary>
    /// A newsletter's items cover its sources one to one; nothing else names a source at all.
    /// </summary>
    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Items(
        AiLongFormOutlineExpectation expectation,
        AiLongFormOutlineOutputDocument document,
        IReadOnlyList<LongFormPlanSectionServiceModel> planned)
    {
        var covered = new HashSet<int>();

        for (var index = 0; index < planned.Count; index++)
        {
            var source = document.Sections[index].Source;

            if (planned[index].Role is not LongFormSectionRole.Item)
            {
                if (source is not null)
                {
                    return Reject(
                        AiOutputReason.LongFormOutlineItemsInvalid,
                        $"Section {index + 1} is not an item and may not name a source.",
                        correctable: true);
                }

                continue;
            }

            if (source is not { } position || position < 1 || position > expectation.RecipeCount || !covered.Add(position))
            {
                return Reject(
                    AiOutputReason.LongFormOutlineItemsInvalid,
                    $"Section {index + 1} is an item and must name one recipe by its position, 1 to "
                        + $"{Number(expectation.RecipeCount)}, that no other item names.",
                    correctable: true);
            }
        }

        // Exactly one per source and no more: an item for a recipe nobody referenced would be an invented
        // source, and a referenced recipe with no item is one the creator chose and the issue leaves out.
        return expectation.Kind is LongFormKind.NewsletterIssue && covered.Count != expectation.RecipeCount
            ? Reject(
                AiOutputReason.LongFormOutlineItemsInvalid,
                $"The plan has {Number(covered.Count)} item sections for {Number(expectation.RecipeCount)} "
                    + "recipes. There is exactly one item for each recipe.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Warnings(
        AiLongFormOutlineOutputDocument document)
    {
        if (document.Warnings.Count > AiPolicy.MaxLongFormOutlineWarnings)
        {
            return Reject(
                AiOutputReason.LongFormOutlineWarningInvalid,
                $"The response carries more than {AiPolicy.MaxLongFormOutlineWarnings} warnings.",
                correctable: true);
        }

        foreach (var warning in document.Warnings)
        {
            // Capped at the column's length less the label the handler stores a model warning under.
            if (warning.Kind is AiWarningKind.Unspecified
                || !Enum.IsDefined(warning.Kind)
                || string.IsNullOrWhiteSpace(warning.Message)
                || warning.Message.Length > AiPolicy.MessageMaxLength - AiPolicy.ModelWarningLabel.Length
                || warning.SectionIndex is < 0
                || warning.SectionIndex >= document.Sections.Count)
            {
                return Reject(
                    AiOutputReason.LongFormOutlineWarningInvalid,
                    "A warning declares its kind, is a sentence that fits the space a warning has, and names a "
                        + "section of the plan or none.",
                    correctable: true);
            }

            if (ServerFindingPrefixes.Any(prefix => warning.Message.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return Reject(
                    AiOutputReason.LongFormOutlineWarningInvalid,
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

    /// <summary>One plain line of text, within its cap.</summary>
    /// <remarks>
    /// Counted the way the content module counts it — one per grapheme cluster — so a heading this passes is
    /// one that module accepts.
    /// </remarks>
    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Line(
        string value, int maxCharacters, string what, string noun, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Reject(reasonCode, $"The response has {what} blank.", correctable: true);
        }

        if (value.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            return Reject(reasonCode, $"The response has {what} on more than one line. A {noun} is one line.", correctable: true);
        }

        // A ceiling in UTF-16 units first, because a cap counted in grapheme clusters alone is no cap at all: one
        // letter under thousands of combining marks is a single cluster. Ten units a cluster is room for any
        // real emoji sequence, keeps the value inside the row it is stored in, and bounds what the pattern
        // below is run over.
        if (value.Length > maxCharacters * MaxUnitsPerCharacter)
        {
            return Reject(reasonCode, $"The response has {what} far too long to be a {noun}.", correctable: true);
        }

        var length = new StringInfo(value).LengthInTextElements;

        if (length > maxCharacters)
        {
            return Reject(
                reasonCode,
                $"The response has {what} at {Number(length)} characters; a {noun} is at most {Number(maxCharacters)}.",
                correctable: true);
        }

        if (Markup.IsMatch(value))
        {
            return Reject(
                reasonCode,
                $"The response has {what} carrying markup or a web address. A {noun} is plain text.",
                correctable: true);
        }

        return Characters(value, what);
    }

    /// <summary>Refuses a control character and any invisible one that is not holding an emoji together.</summary>
    /// <remarks>See <see cref="AiChannelPostsOutputValidator"/>, whose rule this is, for why it is a refusal.</remarks>
    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>? Characters(string value, string what)
    {
        var runes = value.EnumerateRunes().ToArray();

        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            var category = Rune.GetUnicodeCategory(rune);

            if (category is UnicodeCategory.Control && rune.Value is not ('\t' or '\n' or '\r'))
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with a control character.",
                    correctable: true);
            }

            if (IsInvisible(rune, category) && !HoldsAnEmojiTogether(runes, index))
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with an invisible formatting character.",
                    correctable: true);
            }
        }

        return null;
    }

    private static bool IsInvisible(Rune rune, UnicodeCategory category) =>
        category is UnicodeCategory.Format
        || rune.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF)
        || rune.Value is 0x034F or 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800;

    private static bool HoldsAnEmojiTogether(Rune[] runes, int index)
    {
        if (index == 0 || runes[index].Value is not (ZeroWidthJoiner or EmojiVariationSelector))
        {
            return false;
        }

        var before = runes[index - 1];

        if (Rune.GetUnicodeCategory(before) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol
            || (runes[index].Value is ZeroWidthJoiner && before.Value is EmojiVariationSelector))
        {
            return true;
        }

        return runes[index].Value is EmojiVariationSelector
            && before.Value is (>= '0' and <= '9') or '#' or '*'
            && index + 1 < runes.Length
            && runes[index + 1].Value is KeycapMark;
    }

    private static AiOutputValidationOutcome<AiLongFormOutlineOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.LongFormOutlineSectionsInvalid
            or AiOutputReason.LongFormOutlineRoleUnknown
            or AiOutputReason.LongFormOutlineRoleNotAllowed
            or AiOutputReason.LongFormOutlineRoleRepeated
            or AiOutputReason.LongFormOutlineHeadingInvalid
            or AiOutputReason.LongFormOutlineIntentInvalid
            or AiOutputReason.LongFormOutlineItemsInvalid
            or AiOutputReason.LongFormOutlineWantedSectionMissing
            or AiOutputReason.LongFormOutlineWarningInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiLongFormOutlineOutputDocument>.Failed(
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
