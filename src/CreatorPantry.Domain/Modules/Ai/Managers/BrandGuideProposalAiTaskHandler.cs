using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// 11A.17: proposes brand guidance across the dimensions a creator asked for, from their own guide answers and
/// the source document versions they selected, as a proposal they read, edit and decide on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A proposal, never a guide.</strong> The scope is fixed server-side at
/// <see cref="AiOperationScope.NotApplicable"/> — this task names no recipe at all — and
/// <see cref="AiChangeTargetKind.BrandGuideSection"/> is absent from <see cref="AiChangeApplicability"/> and
/// answers <c>null</c> in <see cref="AiChangeTargetPolicy"/>, so no stored row has a path to any write. The
/// creator writes their own guide version from what they agree with.
/// </para>
/// <para>
/// <strong>The guide answers are pinned.</strong> The request records which guide version the creator's answers
/// were read from, and this refuses to run against a different one — the reason the SEO package refuses a
/// changed rule set: a proposal explained by answers that have since been rewritten is unexplainable.
/// </para>
/// <para>
/// <strong>Grounding is read, not trusted.</strong> Passages come from the brand module's facade under the
/// resolved workspace's query filter, so another workspace's passage is never a candidate; the validator refuses
/// a citation naming anything this run did not read; and the quotation measure refuses an answer that reproduces
/// a long run of one.
/// </para>
/// <para>
/// <strong>Thin evidence is the server's to state; a contradiction is not.</strong> An answer looks better
/// without either, so <see cref="AiBrandGuideClaimScanner"/> adds the sparse-evidence, unavailable-source and
/// unsupported-guidance findings whatever the model returned, appended after its own warnings so nothing it says
/// can displace them. What the server <em>cannot</em> do is notice a disagreement the model did not report:
/// nothing here reads the passages for meaning. What it enforces instead is that a conflict the model <em>does</em>
/// report cites two distinct passages, so the creator can go and read both sides — which makes a reported
/// contradiction checkable, not an omitted one impossible.
/// </para>
/// </remarks>
internal sealed class BrandGuideProposalAiTaskHandler(
    IAiCompletionGateway gateway,
    IBrandStyleGuideFacade guides,
    IBrandSourcePassageFacade passages,
    IPromptTemplateStore templates,
    IClock clock) : IAiTaskHandler
{
    private static readonly JsonSerializerOptions ContentJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A brand guide is not a recipe, and an operation that named one would contradict this task's whole
        // subject for as long as the row exists. Asserted rather than tolerated, as the concept handler does.
        if (context.RecipeId is not null || context.RecipeVersionId is not null)
        {
            return Failure(AiFailureCategory.Validation, "A brand guide proposal names no recipe.");
        }

        if (Read(context.Inputs) is not { } request)
        {
            return Failure(
                AiFailureCategory.Validation,
                "A brand guide proposal names the guide, the guide version its answers were read from, and at least one dimension.");
        }

        var guide = await guides.GetAsync(request.GuideId, cancellationToken);

        if (!guide.Succeeded)
        {
            return Failure(AiFailureCategory.Validation, "The brand style guide this proposal was asked about is no longer available.");
        }

        var working = guide.Value!.WorkingVersion;

        if (working.VersionNumber != request.GuideVersionNumber)
        {
            // Both numbers are stated: they are version numbers of the caller's own guide, not secrets, and a
            // creator told only "it moved" cannot tell whether they are one edit or ten behind.
            return Failure(
                AiFailureCategory.DomainInvalid,
                $"The guide has been edited since this proposal was asked for (asked against version "
                    + $"{request.GuideVersionNumber}, the guide is now on {working.VersionNumber}); ask again.");
        }

        var supplied = await passages.ListPassagesAsync(request.Sources, cancellationToken);
        var template = templates.Get(AiTaskCatalog.BrandGuideProposal);

        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)))
            .WithOutputSchema(AiBrandGuideOutputSchema.Json)
            .WithPreferences(context.WorkspaceId, RenderPreferences(request, supplied))

            // The creator's own answers: where the guidance is being written from, and creator data rather than
            // instruction — a guide section can carry an injection as easily as an imported document can.
            .WithSource(context.WorkspaceId, RenderGuideAnswers(working));

        if (supplied.Passages.Count > 0)
        {
            // Retrieval is where leaks originate, so this segment carries the workspace id too. What actually
            // keeps it honest is the facade reading under the resolved workspace's query filter; the argument is
            // the builder's own check, which cannot catch a passage that came from elsewhere because every
            // segment here is given the same id.
            builder.AddReference(context.WorkspaceId, RenderPassages(supplied.Passages));
        }

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiBrandGuideOutputDocument>(
                builder.Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiBrandGuideOutputValidator.AsDelegate(new AiBrandGuideRequestContext(
                    request.Dimensions,
                    request.ChannelKeys,
                    supplied.Passages.ToDictionary(passage => passage.PassageId, passage => passage.Text)))),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var document = outcome.Document!;

        // After the call: the creator may have edited the guide while the model ran, which would leave a
        // proposal explained by answers that are no longer the working version's.
        var current = await guides.GetAsync(request.GuideId, cancellationToken);

        if (!current.Succeeded || current.Value!.WorkingVersion.VersionNumber != request.GuideVersionNumber)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid,
                "The guide was edited while this proposal was being written; ask again.",
                outcome.Attempts);
        }

        var findings = AiBrandGuideClaimScanner.Scan(
            document,
            AiBrandGuideClaimScanner.Unsupported(document),
            supplied.Passages.Count,
            supplied.Unavailable.Count);

        var (changes, warnings) = Translate(document, findings);
        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,

            // No pinned or current recipe version: this proposal is not about a recipe, so there is nothing for
            // staleness to be measured against on that axis. The guide's own version is checked above instead.
            pinnedVersionId: null,
            currentVersionId: null,
            new AiOutputDocument { SchemaVersion = template.OutputSchemaVersion, Warnings = warnings },
            changes,
            new AiProposalProvenance(
                template.OutputSchemaVersion,
                template.Id,
                template.Version.ToString(),
                template.BodyChecksum,
                attempt.ProviderName,
                attempt.ModelName,
                attempt.ModelDeployment),
            clock.UtcNow);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, outcome.Attempts)
            : AiTaskHandlerOutcome.ForFailure(assembly.Failure!.Category, assembly.Failure.Message, outcome.Attempts);
    }

    /// <summary>What the request asked for, as the handler reads it back out of the operation's declared fields.</summary>
    private sealed record Request(
        Guid GuideId,
        int GuideVersionNumber,
        IReadOnlySet<AiBrandGuideDimension> Dimensions,
        IReadOnlySet<string> ChannelKeys,
        IReadOnlyList<BrandSourcePassageSelector> Sources);

    private static Request? Read(IReadOnlyDictionary<string, string>? inputs)
    {
        if (inputs is null
            || !inputs.TryGetValue(AiBrandGuideProposalInputs.GuideId, out var rawGuide)
            || !Guid.TryParse(rawGuide, out var guideId)
            || guideId == Guid.Empty
            || !inputs.TryGetValue(AiBrandGuideProposalInputs.GuideVersionNumber, out var rawVersion)
            || !int.TryParse(rawVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber)
            || versionNumber <= 0)
        {
            return null;
        }

        var dimensions = AiBrandGuideDimensionCatalog.ParseList(
            inputs.GetValueOrDefault(AiBrandGuideProposalInputs.Dimensions));

        if (dimensions is null || dimensions.Count == 0)
        {
            return null;
        }

        var channelKeys = (inputs.GetValueOrDefault(AiBrandGuideProposalInputs.ChannelKeys) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        // Channel guidance with no channel named has nothing to be guidance about, so the request is malformed
        // rather than merely unproductive.
        if (dimensions.Contains(AiBrandGuideDimension.Channel) && channelKeys.Count == 0)
        {
            return null;
        }

        return ReadSources(inputs.GetValueOrDefault(AiBrandGuideProposalInputs.SourceVersions)) is { } sources
            ? new Request(guideId, versionNumber, dimensions, channelKeys, sources)
            : null;
    }

    /// <summary>
    /// The selected versions, as <c>{documentId:N}:{versionNumber}</c> pairs. An empty list is legitimate: a
    /// creator may ask for guidance from their answers alone, and the sparse-evidence finding then says so.
    /// </summary>
    private static IReadOnlyList<BrandSourcePassageSelector>? ReadSources(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var selectors = new List<BrandSourcePassageSelector>();

        foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(':', StringSplitOptions.TrimEntries);

            if (parts.Length != 2
                || !Guid.TryParseExact(parts[0], "N", out var documentId)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber)
                || versionNumber <= 0)
            {
                // A malformed pair is a malformed request, never a pair to skip: silently dropping one would
                // ground the answer on less than the creator selected without saying so.
                return null;
            }

            selectors.Add(new BrandSourcePassageSelector(documentId, versionNumber));
        }

        return selectors;
    }

    /// <summary>The dimensions to write, the channels offered, and the limits each answer must meet.</summary>
    private static string RenderPreferences(Request request, BrandSourcePassageSetServiceModel supplied) =>
        JsonSerializer.Serialize(
            new
            {
                dimensions = request.Dimensions.Select(AiBrandGuideDimensionCatalog.ToWire).OrderBy(name => name).ToArray(),
                channelKeys = request.ChannelKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),

                // How many passages the REFERENCE segment carries, so "none" is stated rather than inferred from
                // the segment's absence.
                passages = supplied.Passages.Count,
                limits = new
                {
                    guidanceCharacters = AiPolicy.BrandGuideBodyMaxLength,
                    ruleCharacters = AiPolicy.BrandGuideRuleMaxLength,
                    summaryCharacters = AiPolicy.BrandGuideSummaryMaxLength,
                    maxRules = AiPolicy.BrandGuideMaxRules,
                    maxConflicts = AiPolicy.BrandGuideMaxFindings,
                    maxUncertainties = AiPolicy.BrandGuideMaxFindings,
                    maxCitationsPerItem = AiPolicy.BrandGuideMaxCitationsPerItem,
                    maxQuotedWordRun = AiPolicy.BrandGuideMaxQuotedWordRun,
                },
            },
            ContentJson);

    /// <summary>
    /// The creator's own answers, as the guide's working version holds them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Section keys and bodies only. No guide name, no purpose, no actor, no timestamps: none of them is
    /// evidence about how the creator writes, and a prompt carries what the task needs and nothing more.
    /// </para>
    /// <para>
    /// <strong><see cref="BrandStyleGuideSectionKey.UserNotes"/> is excluded</strong> (audit 11A.24a, S2). It is
    /// the creator's own scratch area on the guide — notes to themselves <em>about</em> the guide, not an answer
    /// about how they write — so sending it would turn a private reminder into evidence, and into something a
    /// proposal could cite back at them. <see cref="BrandContextSelection"/> excludes it from every generation
    /// task for the same reason; this path read the whole section list and so did not.
    /// </para>
    /// </remarks>
    private static string RenderGuideAnswers(BrandStyleGuideVersionDetailServiceModel version) =>
        JsonSerializer.Serialize(
            new
            {
                answers = version.Sections
                    .Where(section => section.SectionKey is not BrandStyleGuideSectionKey.UserNotes)
                    .Select(section => new
                    {
                        key = section.SectionKey.ToString(),
                        channel = section.ChannelKey,
                        text = section.Body,
                    })
                    .ToArray(),
                rules = version.Rules
                    .Select(rule => new { kind = rule.Kind.ToString(), text = rule.Text })
                    .ToArray(),
            },
            ContentJson);

    /// <summary>The passages, each with the id a citation must name.</summary>
    private static string RenderPassages(IReadOnlyList<BrandSourcePassageServiceModel> passages) =>
        JsonSerializer.Serialize(
            passages.Select(passage => new
            {
                passageId = passage.PassageId,

                // The document and ordinal travel so a creator reading a citation can find the passage, and so
                // the model can tell two documents apart when they disagree.
                documentId = passage.DocumentId,
                versionNumber = passage.VersionNumber,
                ordinal = passage.Ordinal,
                text = passage.Text,
            }).ToArray(),
            ContentJson);

    /// <summary>
    /// Turns the validated answer into the rows a creator reads, and the warnings beside them.
    /// </summary>
    /// <remarks>
    /// One <see cref="AiChangeKind.Add"/> row carries each item's own text, with its dimension, evidence basis,
    /// channel and citations as <see cref="AiChangeKind.Set"/> rows against the same target id — the shape
    /// <see cref="SeoPackageAiTaskHandler"/> uses for a content section. The target kind has no path to any
    /// write, so these rows are read and nothing else.
    /// </remarks>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiBrandGuideOutputDocument document, IReadOnlyList<AiBrandGuideFinding> findings)
    {
        var changes = new List<AiResolvedChange>();
        var rowByDimension = new Dictionary<AiBrandGuideDimension, int>();
        var position = 0;

        Guid Item(string itemKind, string text, AiBrandGuideDimension? dimension, params (string Field, string? Value)[] extras)
        {
            var targetId = Guid.NewGuid();

            if (dimension is { } named && itemKind == AiBrandGuideItemKinds.Section)
            {
                rowByDimension[named] = changes.Count;
            }

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.BrandGuideSection, targetId, FieldName: null, BeforeValue: null,
                text, ProposedPosition: position++, changes.Count));

            AddSet(changes, targetId, AiBrandGuideFields.ItemKind, itemKind);

            if (dimension is { } value)
            {
                AddSet(changes, targetId, AiBrandGuideFields.Dimension, AiBrandGuideDimensionCatalog.ToWire(value));
            }

            foreach (var (field, extra) in extras)
            {
                AddSet(changes, targetId, field, extra);
            }

            return targetId;
        }

        static string? Cited(IReadOnlyList<AiBrandGuideCitation> citations) =>
            citations.Count == 0 ? null : string.Join(',', citations.Select(citation => citation.PassageId.ToString("N")));

        foreach (var section in document.Sections)
        {
            Item(
                AiBrandGuideItemKinds.Section,
                section.Body,
                section.Dimension,
                (AiBrandGuideFields.ChannelKey, section.ChannelKey),
                (AiBrandGuideFields.Evidence, section.Evidence.ToString()),
                (AiBrandGuideFields.Citations, Cited(section.Citations)));
        }

        foreach (var rule in document.Rules)
        {
            Item(
                AiBrandGuideItemKinds.Rule,
                rule.Text,
                null,
                (AiBrandGuideFields.RuleKind, rule.Kind.ToString()),
                (AiBrandGuideFields.Evidence, rule.Evidence.ToString()),
                (AiBrandGuideFields.Citations, Cited(rule.Citations)));
        }

        foreach (var conflict in document.Conflicts)
        {
            Item(
                AiBrandGuideItemKinds.Conflict,
                conflict.Summary,
                conflict.Dimension,
                (AiBrandGuideFields.Citations, Cited(conflict.Citations)));
        }

        foreach (var uncertainty in document.Uncertainties)
        {
            Item(AiBrandGuideItemKinds.Uncertainty, uncertainty.Summary, uncertainty.Dimension);
        }

        int? RowFor(AiBrandGuideDimension? dimension) =>
            dimension is { } named && rowByDimension.TryGetValue(named, out var row) ? row : null;

        var warnings = document.Warnings
            .Select(warning => new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = AiPolicy.ModelWarningLabel + warning.Message,
                ChangeIndex = RowFor(warning.Dimension),
            })
            .ToList();

        // The server's findings after the model's, so they cannot be displaced by its warning limit and cannot
        // be left out by anything it says.
        warnings.AddRange(findings.Select(finding => new AiOutputWarning
        {
            Kind = finding.Kind,
            Message = $"[{finding.Code}] {finding.Message}",
            ChangeIndex = RowFor(finding.Dimension),
        }));

        return (changes, warnings);
    }

    private static void AddSet(List<AiResolvedChange> changes, Guid targetId, string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        changes.Add(new AiResolvedChange(
            AiChangeKind.Set, AiChangeTargetKind.BrandGuideSection, targetId, field, BeforeValue: null, value,
            ProposedPosition: null, changes.Count));
    }

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string message) =>
        AiTaskHandlerOutcome.ForFailure(category, message, []);
}

/// <summary>The wire names for <see cref="AiBrandGuideDimension"/>, and parsing back from them.</summary>
/// <remarks>
/// A catalogue rather than <c>Enum.Parse</c>, matching <c>AiSeoSectionCatalog</c>: the wire name is part of the
/// request contract, so renaming the enum member must not silently change what a client may send.
/// </remarks>
public static class AiBrandGuideDimensionCatalog
{
    private static readonly Dictionary<string, AiBrandGuideDimension> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["voice"] = AiBrandGuideDimension.Voice,
        ["tone"] = AiBrandGuideDimension.Tone,
        ["tenor"] = AiBrandGuideDimension.Tenor,
        ["style"] = AiBrandGuideDimension.Style,
        ["language"] = AiBrandGuideDimension.Language,
        ["channel"] = AiBrandGuideDimension.Channel,
        ["blog"] = AiBrandGuideDimension.Blog,
        ["social"] = AiBrandGuideDimension.Social,
        ["visual"] = AiBrandGuideDimension.Visual,
    };

    /// <summary>Every dimension a request may ask for.</summary>
    public static IReadOnlyCollection<string> Names => ByName.Keys;

    public static AiBrandGuideDimension? Parse(string? name) =>
        name is not null && ByName.TryGetValue(name, out var dimension) ? dimension : null;

    public static string ToWire(AiBrandGuideDimension dimension) =>
        ByName.First(pair => pair.Value == dimension).Key;

    /// <summary>
    /// A comma-separated list, or null when any entry is not a dimension or the same one appears twice.
    /// </summary>
    public static IReadOnlySet<AiBrandGuideDimension>? ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .ToList();

        if (parsed.Count == 0 || parsed.Any(dimension => dimension is null))
        {
            return null;
        }

        var dimensions = parsed.Select(dimension => dimension!.Value).ToHashSet();

        return dimensions.Count == parsed.Count ? dimensions : null;
    }
}
