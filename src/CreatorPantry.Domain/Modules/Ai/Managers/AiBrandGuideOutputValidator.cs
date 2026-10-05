using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What this request made available to the model, so an answer naming anything else can be refused.
/// </summary>
/// <param name="Dimensions">The dimensions the request asked for. An answer may write fewer, never others.</param>
/// <param name="ChannelKeys">
/// The channel keys offered for <see cref="AiBrandGuideDimension.Channel"/>. Empty means no channel guidance was
/// asked for, so any channel section at all is refused.
/// </param>
/// <param name="Passages">
/// The exact <c>BrandSourceChunk</c> rows the handler read for this request, keyed by id. A citation naming
/// anything else is refused, which is what makes a citation verifiable rather than merely well formed, and the
/// text is what the quotation check measures an answer against.
/// </param>
public sealed record AiBrandGuideRequestContext(
    IReadOnlySet<AiBrandGuideDimension> Dimensions,
    IReadOnlySet<string> ChannelKeys,
    IReadOnlyDictionary<Guid, string> Passages);

/// <summary>
/// Validates a model's brand-guide proposal against <see cref="AiBrandGuideOutputDocument"/> and the request it
/// answers. Rejects and never repairs: an uncited claim fails, it is not relabelled as an assumption.
/// </summary>
/// <remarks>
/// <para>
/// Three of 11A.17's restrictions are enforced here rather than hoped for from the prompt: a citation must name
/// a passage this request actually offered, a conflict must cite both sides, and a dimension must have been
/// asked for. The two that cannot be fully enforced — copying a long passage, and naming or profiling a person
/// — are measured by <see cref="AiBrandGuideClaimScanner"/>, which this validator calls for the first and the
/// handler consults for the second.
/// </para>
/// <para>
/// <strong>Why the quotation check lives here and not after the call.</strong> It is the one domain rule a
/// corrective re-ask can plausibly fix — the model is told to describe rather than reproduce — and a failure
/// raised at this point gets that single re-ask instead of sinking the whole proposal. Everything about which
/// passages exist is a fact about one request, which is why the context is supplied rather than looked up.
/// </para>
/// </remarks>
public static class AiBrandGuideOutputValidator
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

    /// <summary>The same shape the editorial and SEO validators refuse, and for the same reasons.</summary>
    private static readonly System.Text.RegularExpressions.Regex MarkupOrLink = new(
        @"\b(?:https?|ftp|data|mailto|javascript):|www\.|\[\.\]|\(dot\)|\bdot (?:com|net|org)\b|\b[a-z0-9-]{2,}\.(?:com|net|org|io|co|uk|app|dev|me|ly|shop|blog|info|xyz|de|ca|fr|es|it|nl|au|us|tv|ai)\b|\S+@\S+\.\S+|(?<!\w)[@#][a-z]\w|<\s*/?\s*[a-z!]|\]\(|`{3}",
        System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <param name="context">
    /// What this request offered. When given, an answer naming a dimension, channel or passage outside it is
    /// refused here, where the gateway's one corrective re-ask still applies.
    /// </param>
    public static AiOutputValidationOutcome<AiBrandGuideOutputDocument> Validate(
        string? payload, string expectedSchemaVersion, AiBrandGuideRequestContext? context = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);

        var failure = Envelope(payload) ?? SchemaVersion(payload!, expectedSchemaVersion);

        if (failure is not null)
        {
            return failure;
        }

        failure = Shape(payload!, out var document)
            ?? Nulls(document!)
            ?? Declared(document!)
            ?? Lengths(document!)
            ?? PlainText(document!)
            ?? Evidence(document!)
            ?? (context is null ? null : Request(document!, context));

        return failure ?? AiOutputValidationOutcome<AiBrandGuideOutputDocument>.Success(document!);
    }

    public static AiOutputValidatorDelegate<AiBrandGuideOutputDocument> AsDelegate(
        AiBrandGuideRequestContext? context = null) =>
        (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion, context);

    /// <summary>Every enum a row carries has to be declared: a zero means the model did not say.</summary>
    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Declared(AiBrandGuideOutputDocument document)
    {
        if (document.Sections.Any(section => section.Dimension is AiBrandGuideDimension.Unspecified)
            || document.Conflicts.Any(conflict => conflict.Dimension is AiBrandGuideDimension.Unspecified)
            || document.Uncertainties.Any(item => item.Dimension is AiBrandGuideDimension.Unspecified)
            || document.Warnings.Any(warning => warning.Dimension is AiBrandGuideDimension.Unspecified))
        {
            return Reject(
                AiOutputReason.KindNotDeclared,
                "Every item that names a dimension must name one of the dimensions, not an unspecified value.",
                correctable: true);
        }

        if (document.Rules.Any(rule => rule.Kind is AiBrandGuideRuleKind.Unspecified))
        {
            return Reject(AiOutputReason.KindNotDeclared, "Every rule says whether it is a do or a don't.", correctable: true);
        }

        if (document.Sections.Any(section => section.Evidence is AiBrandGuideEvidence.Unspecified)
            || document.Rules.Any(rule => rule.Evidence is AiBrandGuideEvidence.Unspecified))
        {
            return Reject(
                AiOutputReason.KindNotDeclared,
                "Every section and rule says what it rests on.",
                correctable: true);
        }

        return document.Warnings.Any(warning => warning.Kind is AiWarningKind.Unspecified)
            ? Reject(AiOutputReason.KindNotDeclared, "Every warning declares its kind.", correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Lengths(AiBrandGuideOutputDocument document)
    {
        if (document.Rules.Count > AiPolicy.BrandGuideMaxRules)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"At most {AiPolicy.BrandGuideMaxRules} rules; {document.Rules.Count} were given.",
                correctable: true);
        }

        if (document.Conflicts.Count > AiPolicy.BrandGuideMaxFindings
            || document.Uncertainties.Count > AiPolicy.BrandGuideMaxFindings)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"At most {AiPolicy.BrandGuideMaxFindings} conflicts and {AiPolicy.BrandGuideMaxFindings} uncertainties.",
                correctable: true);
        }

        foreach (var section in document.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Body) || section.Body.Length > AiPolicy.BrandGuideBodyMaxLength)
            {
                return Reject(
                    AiOutputReason.ValueTooLong,
                    $"Each dimension's guidance must say something and be at most {AiPolicy.BrandGuideBodyMaxLength} characters.",
                    correctable: true);
            }
        }

        foreach (var rule in document.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Text) || rule.Text.Length > AiPolicy.BrandGuideRuleMaxLength)
            {
                return Reject(
                    AiOutputReason.ValueTooLong,
                    $"Each rule must say something and be at most {AiPolicy.BrandGuideRuleMaxLength} characters.",
                    correctable: true);
            }
        }

        var summaries = document.Conflicts.Select(conflict => conflict.Summary)
            .Concat(document.Uncertainties.Select(item => item.Summary));

        if (summaries.Any(summary => string.IsNullOrWhiteSpace(summary) || summary.Length > AiPolicy.BrandGuideSummaryMaxLength))
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"Each conflict and uncertainty must say something and be at most {AiPolicy.BrandGuideSummaryMaxLength} characters.",
                correctable: true);
        }

        return document.Warnings.Any(warning => string.IsNullOrWhiteSpace(warning.Message)
            || warning.Message.Length > AiPolicy.MessageMaxLength)
            ? Reject(AiOutputReason.ValueTooLong, "Each warning must say something and fit the message limit.", correctable: true)
            : null;
    }

    /// <summary>
    /// Plain text, as the prompt asks for: no markup, link, address, handle or hashtag anywhere the model wrote.
    /// </summary>
    /// <remarks>
    /// Enforced rather than trusted to the prompt, for the reason the editorial and SEO packages enforce the same
    /// rule: a link is an unverified claim about somewhere else, and these strings are rendered to a creator.
    /// Every field goes through it, warnings included, through the scanner's own list so the two cannot drift.
    /// </remarks>
    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? PlainText(
        AiBrandGuideOutputDocument document) =>
        AiBrandGuideClaimScanner.Written(document).Any(item => MarkupOrLink.IsMatch(item.Text))
            ? Reject(
                AiOutputReason.IllegalCharacters,
                "Write plain text: no markup, links, addresses, handles or hashtags.",
                correctable: true)
            : null;

    /// <summary>
    /// What each claim rests on, against what it cites. The heart of "every claim is cited or it is not stored".
    /// </summary>
    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Evidence(AiBrandGuideOutputDocument document)
    {
        foreach (var section in document.Sections)
        {
            if (Cited(section.Evidence, section.Citations) is { } failure)
            {
                return failure;
            }
        }

        foreach (var rule in document.Rules)
        {
            if (Cited(rule.Evidence, rule.Citations) is { } failure)
            {
                return failure;
            }
        }

        foreach (var conflict in document.Conflicts)
        {
            // Two sides, and two distinct ones: the same passage twice does not disagree with itself.
            if (conflict.Citations.Select(citation => citation.PassageId).Distinct().Count() < 2)
            {
                return Reject(
                    AiOutputReason.BrandGuideConflictUnsupported,
                    "A conflict names at least two different passages, so both sides of it can be read.",
                    correctable: true);
            }

            if (conflict.Citations.Count > AiPolicy.BrandGuideMaxCitationsPerItem)
            {
                return Reject(
                    AiOutputReason.ValueTooLong,
                    $"A conflict cites at most {AiPolicy.BrandGuideMaxCitationsPerItem} passages.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Cited(
        AiBrandGuideEvidence evidence, IReadOnlyList<AiBrandGuideCitation> citations)
    {
        if (citations.Count > AiPolicy.BrandGuideMaxCitationsPerItem)
        {
            return Reject(
                AiOutputReason.ValueTooLong,
                $"An item cites at most {AiPolicy.BrandGuideMaxCitationsPerItem} passages.",
                correctable: true);
        }

        // Claiming the sources without naming one is the failure this whole contract exists to prevent.
        if (evidence is AiBrandGuideEvidence.Sources or AiBrandGuideEvidence.Both && citations.Count == 0)
        {
            return Reject(
                AiOutputReason.BrandGuideCitationInvalid,
                "An item resting on the sources must cite at least one passage, or say it rests on the questionnaire or on nothing.",
                correctable: true);
        }

        // And the converse: citing passages while claiming not to have used them misreports where it came from.
        return evidence is AiBrandGuideEvidence.Questionnaire or AiBrandGuideEvidence.None && citations.Count > 0
            ? Reject(
                AiOutputReason.BrandGuideCitationInvalid,
                "An item that cites a passage rests on the sources; say so rather than citing one and denying it.",
                correctable: true)
            : null;
    }

    /// <summary>
    /// The answer against the request: only dimensions asked for, only channels offered, only passages read, and
    /// no reproduced run of a passage. Facts about one request, so they need the context.
    /// </summary>
    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Request(
        AiBrandGuideOutputDocument document, AiBrandGuideRequestContext context)
    {
        foreach (var section in document.Sections)
        {
            if (!context.Dimensions.Contains(section.Dimension))
            {
                return Reject(
                    AiOutputReason.BrandGuideDimensionInvalid,
                    "The answer wrote guidance for a dimension the request did not ask for.",
                    correctable: true);
            }

            if (section.Dimension is AiBrandGuideDimension.Channel)
            {
                if (string.IsNullOrWhiteSpace(section.ChannelKey) || !context.ChannelKeys.Contains(section.ChannelKey))
                {
                    return Reject(
                        AiOutputReason.BrandGuideChannelInvalid,
                        "Channel guidance names one of the channel keys the request offered.",
                        correctable: true);
                }
            }
            else if (section.ChannelKey is not null)
            {
                return Reject(
                    AiOutputReason.BrandGuideChannelInvalid,
                    "Only channel guidance carries a channel key.",
                    correctable: true);
            }
        }

        // One section per dimension, and per channel within the channel dimension: two answers for one question
        // leave a creator deciding which the model meant.
        var keyed = document.Sections
            .Select(section => (section.Dimension, Channel: section.ChannelKey ?? string.Empty))
            .ToList();

        if (keyed.Distinct().Count() != keyed.Count)
        {
            return Reject(
                AiOutputReason.BrandGuideDimensionInvalid,
                "The answer wrote the same dimension twice.",
                correctable: true);
        }

        var cited = document.Sections.SelectMany(section => section.Citations)
            .Concat(document.Rules.SelectMany(rule => rule.Citations))
            .Concat(document.Conflicts.SelectMany(conflict => conflict.Citations));

        if (cited.Any(citation => !context.Passages.ContainsKey(citation.PassageId)))
        {
            // Never echoes the id: an id this request did not offer is not this workspace's to confirm.
            return Reject(
                AiOutputReason.BrandGuideCitationInvalid,
                "A citation names a passage that was not among those supplied for this request.",
                correctable: true);
        }

        // Last, because it is the most expensive check and the one most likely to be fixed by the single
        // corrective re-ask: every other failure above is cheaper to find first.
        //
        // Every field the model wrote, warnings included: a warning is prose a creator reads like any other, so
        // leaving it out would give a reproduced passage one field to live in where nothing looked. The scanner
        // owns the list so this and the finding scan cannot drift apart.
        var written = AiBrandGuideClaimScanner.Written(document).Select(item => item.Text);

        return AiBrandGuideClaimScanner.LongestQuotedRun(written, context.Passages.Values)
            > AiPolicy.BrandGuideMaxQuotedWordRun
            ? Reject(
                AiOutputReason.BrandGuidePassageCopied,
                $"The answer reproduces more than {AiPolicy.BrandGuideMaxQuotedWordRun} consecutive words of a source "
                    + "passage. Describe how the creator writes rather than repeating what they wrote.",
                correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Envelope(string? payload)
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

    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? SchemaVersion(string payload, string expected)
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

    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Shape(
        string payload, out AiBrandGuideOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiBrandGuideOutputDocument>(payload, OutputJson);
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

    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument>? Nulls(AiBrandGuideOutputDocument document)
    {
        var missing = document.Sections is null
            || document.Rules is null
            || document.Conflicts is null
            || document.Uncertainties is null
            || document.Warnings is null
            || document.Sections.Any(item => item is null)
            || document.Rules.Any(item => item is null)
            || document.Conflicts.Any(item => item is null)
            || document.Uncertainties.Any(item => item is null)
            || document.Warnings.Any(item => item is null)
            || document.Sections.Any(section => section.Citations is null || section.Citations.Any(item => item is null))
            || document.Rules.Any(rule => rule.Citations is null || rule.Citations.Any(item => item is null))
            || document.Conflicts.Any(conflict => conflict.Citations is null || conflict.Citations.Any(item => item is null));

        return missing
            ? Reject(AiOutputReason.ShapeInvalid, "A list in the response contains a null entry.", correctable: true)
            : null;
    }

    private static AiOutputValidationOutcome<AiBrandGuideOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.BrandGuideCitationInvalid
            or AiOutputReason.BrandGuideConflictUnsupported
            or AiOutputReason.BrandGuideChannelInvalid
            or AiOutputReason.BrandGuideDimensionInvalid
            or AiOutputReason.BrandGuidePassageCopied
            or AiOutputReason.KindNotDeclared
            or AiOutputReason.ValueTooLong
            or AiOutputReason.DomainInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiBrandGuideOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
