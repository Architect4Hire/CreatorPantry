using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Validates a model's editorial package against <see cref="AiEditorialPackageOutputDocument"/>. Rejects and
/// never repairs: a truncated or mis-shaped answer fails, it is not patched into something the model never said.
/// </summary>
/// <remarks>
/// <para>
/// Context-free on purpose. Which sections were requested and which ingredient lines exist are facts about one
/// request, checked by the handler afterwards (<see cref="AiOutputReason.EditorialSectionNotRequested"/>,
/// <see cref="AiOutputReason.EditorialLineNotInSource"/>). Whether prose makes a claim the recipe does not
/// support is not a rejection at all: <see cref="AiEditorialClaimScanner"/> reports those as warnings.
/// </para>
/// <para>
/// Plain text only. Markup and links are refused here because channel formatting belongs in channel
/// profiles and adapters (content.md), not in generated prose, and because a link is an unverified claim
/// about somewhere else.
/// </para>
/// </remarks>
public static class AiEditorialPackageOutputValidator
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

    // Links in every shape a consumer might auto-link, plus the markup the prompt forbids: schemes, bare domains,
    // addresses, handles and hashtags, HTML, markdown links and code fences.
    private static readonly Regex MarkupOrLink = new(
        @"\b(?:https?|ftp|data|mailto|javascript):|www\.|\b[a-z0-9-]{2,}\.(?:com|net|org|io|co|uk|app|dev|me|ly|shop|blog|info|xyz)\b|\S+@\S+\.\S+|(?<!\w)[@#][a-z]\w|<\s*/?\s*[a-z!]|\]\(|`{3}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] ServerFindingPrefixes = ["[editorial.", "[model]"];

    /// <param name="context">
    /// What this request made available — the sections asked for and the ingredient lines of the pinned version.
    /// When given, an answer naming anything outside it is refused <em>here</em>, so the gateway's one corrective
    /// re-ask can apply, instead of sinking the whole package after the call.
    /// </param>
    public static AiOutputValidationOutcome<AiEditorialPackageOutputDocument> Validate(
        string? payload, string expectedSchemaVersion, AiEditorialRequestContext? context = null)
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
            ?? Domain(document!)
            ?? (context is null ? null : Request(document!, context));

        return failure ?? AiOutputValidationOutcome<AiEditorialPackageOutputDocument>.Success(document!);
    }

    public static AiOutputValidatorDelegate<AiEditorialPackageOutputDocument> AsDelegate { get; } =
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion);

    public static AiOutputValidatorDelegate<AiEditorialPackageOutputDocument> AsDelegateFor(AiEditorialRequestContext context) =>
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion, context);

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? Request(
        AiEditorialPackageOutputDocument document, AiEditorialRequestContext context)
    {
        if (AiEditorialSectionCatalog.Present(document.Sections).Any(section => !context.Requested.Contains(section)))
        {
            return Reject(
                AiOutputReason.EditorialSectionNotRequested,
                "The answer wrote a section the request did not ask for; write only the sections named.",
                correctable: true);
        }

        return document.Sections.Substitutions.Any(item => !context.LineIds.Contains(item.LineId))
            ? Reject(
                AiOutputReason.EditorialLineNotInSource,
                "A substitution names an ingredient line that is not one of the recipe's lines; use only the ids given.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? SchemaVersion(
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

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? Shape(
        string payload, out AiEditorialPackageOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiEditorialPackageOutputDocument>(payload, OutputJson);
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
    /// JSON <c>null</c> deserializes into a non-nullable member without complaint, and would otherwise surface as a
    /// <see cref="NullReferenceException"/> in the checks below — an unclassified failure after the model has
    /// already been paid for. A null where the contract has a value is a shape failure like any other.
    /// </summary>
    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? Nulls(
        AiEditorialPackageOutputDocument document)
    {
        var sections = document.Sections;

        var missing = sections is null
            || document.Warnings is null
            || sections.Tips is null || sections.Substitutions is null || sections.Faq is null
            || sections.Tips.Any(item => item is null)
            || sections.Substitutions.Any(item => item is null)
            || sections.Faq.Any(item => item is null)
            || document.Warnings.Any(item => item is null);

        return missing
            ? Reject(AiOutputReason.ShapeInvalid, "The response has a null where the schema requires a value.", correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? Hygiene(
        AiEditorialPackageOutputDocument document)
    {
        var sections = document.Sections;

        if (sections.Tips.Count > AiPolicy.MaxEditorialTips
            || sections.Substitutions.Count > AiPolicy.MaxEditorialSubstitutions
            || sections.Faq.Count > AiPolicy.MaxEditorialFaqItems
            || document.Warnings.Count > AiPolicy.MaxEditorialWarnings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxEditorialTips} tips, "
                    + $"{AiPolicy.MaxEditorialSubstitutions} substitutions, {AiPolicy.MaxEditorialFaqItems} FAQ "
                    + $"items or {AiPolicy.MaxEditorialWarnings} warnings.",
                correctable: true);
        }

        foreach (var (text, limit, what) in Texts(document))
        {
            var failure = CheckString(text, limit, what);

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static IEnumerable<(string Text, int Limit, string What)> Texts(AiEditorialPackageOutputDocument document)
    {
        var sections = document.Sections;

        if (sections.Headnote is not null) yield return (sections.Headnote.Text, AiPolicy.EditorialTextMaxLength, "a headnote");
        if (sections.Introduction is not null) yield return (sections.Introduction.Text, AiPolicy.EditorialTextMaxLength, "an introduction");
        if (sections.StorageReheating is not null) yield return (sections.StorageReheating.Text, AiPolicy.EditorialTextMaxLength, "a storage note");
        if (sections.Cta is not null) yield return (sections.Cta.Text, AiPolicy.EditorialTextMaxLength, "a call to action");

        foreach (var tip in sections.Tips) yield return (tip.Text, AiPolicy.EditorialItemMaxLength, "a tip");

        foreach (var substitution in sections.Substitutions)
        {
            yield return (substitution.Suggestion, AiPolicy.EditorialItemMaxLength, "a substitution suggestion");
            yield return (substitution.CulinaryNote, AiPolicy.EditorialItemMaxLength, "a substitution note");
        }

        foreach (var item in sections.Faq)
        {
            yield return (item.Question, AiPolicy.EditorialItemMaxLength, "a FAQ question");
            yield return (item.Answer, AiPolicy.EditorialItemMaxLength, "a FAQ answer");
        }

        foreach (var warning in document.Warnings)
        {
            yield return (warning.Message, AiPolicy.MessageMaxLength - AiPolicy.ModelWarningLabel.Length, "a warning message");
        }
    }

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? CheckString(
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

        // Format characters (zero-width joiners and the like) are invisible, so they can only be there to get
        // something past a reader or a phrase list.
        if (value.Any(character =>
            (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
            || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format))
        {
            return Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with a control or invisible character.",
                correctable: true);
        }

        return MarkupOrLink.IsMatch(value) || MarkupOrLink.IsMatch(AiEditorialProse.Canonicalize(value))
            ? Reject(
                AiOutputReason.IllegalCharacters,
                $"The response contains {what} with markup or a link; write plain text.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument>? Domain(
        AiEditorialPackageOutputDocument document)
    {
        var sections = document.Sections;

        if (Texts(document).Any(entry => string.IsNullOrWhiteSpace(entry.Text))
            || sections.Substitutions.Any(item => item.LineId == Guid.Empty))
        {
            return Reject(
                AiOutputReason.EditorialFieldMissing,
                "A section, tip, substitution, FAQ item or warning is empty, or a substitution names no ingredient line.",
                correctable: true);
        }

        if (document.Warnings.Any(warning => warning.Kind is AiWarningKind.Unspecified))
        {
            return Reject(AiOutputReason.KindNotDeclared, "Every warning declares its kind.", correctable: true);
        }

        // A model warning dressed as a server finding would read, beside the real ones, as the server vouching
        // for something. Server findings are told apart by this prefix, so the model may not write it.
        if (document.Warnings.Any(warning => ServerFindingPrefixes.Any(prefix => warning.Message.Contains(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            return Reject(
                AiOutputReason.DomainInvalid,
                "A warning imitates a server finding; write the caution in your own words.",
                correctable: true);
        }

        var duplicated =
            Duplicates(sections.Tips.Select(tip => tip.Text))
            || Duplicates(sections.Faq.Select(item => item.Question))
            || sections.Substitutions.Select(item => item.LineId).Distinct().Count() != sections.Substitutions.Count;

        return duplicated
            ? Reject(
                AiOutputReason.EditorialDuplicateItem,
                "A tip, FAQ question or ingredient line appears twice in one section.",
                correctable: true)
            : null;
    }

    private static bool Duplicates(IEnumerable<string> values)
    {
        var list = values.Select(value => value.Trim()).ToList();

        return list.Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count;
    }

    private static AiOutputValidationOutcome<AiEditorialPackageOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.EditorialFieldMissing
            or AiOutputReason.EditorialDuplicateItem
            or AiOutputReason.KindNotDeclared
            or AiOutputReason.DomainInvalid
            or AiOutputReason.EditorialSectionNotRequested
            or AiOutputReason.EditorialLineNotInSource
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiEditorialPackageOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
