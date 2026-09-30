using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// RCPUB-002: writes search title, meta description, key phrases, alt-text suggestions and internal-link ideas
/// for one approved recipe version, and derives its slug, as a proposal the creator reviews, edits and decides on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Advisory and recommendation-only.</strong> The scope is fixed server-side at
/// <see cref="AiOperationScope.Advisory"/>; <see cref="AiChangeTargetKind.ContentSection"/> has no path to a
/// recipe edit. No field in the schema can hold a metric, and <see cref="AiSeoClaimScanner"/> reports any the
/// model writes into prose.
/// </para>
/// <para>
/// <strong>The rules are the request's.</strong> The rule-set version is pinned when the request is made and the
/// handler refuses to run against a different one, so a configuration change cannot re-judge queued work.
/// Lengths and formats are enforced by the validator, not trusted to the prompt.
/// </para>
/// <para>
/// <strong>Identifiers are code's.</strong> The slug is derived from the proposed title by
/// <see cref="SeoSlug"/>; the model is not asked for one. Internal links may name only the candidate recipes
/// this handler offers, read through the recipe facade under the resolved workspace's filter, and alt text only
/// the asset links of the pinned version.
/// </para>
/// </remarks>
internal sealed class SeoPackageAiTaskHandler(
    IAiCompletionGateway gateway,
    IRecipeFacade recipes,
    IPromptTemplateStore templates,
    SeoRules rules,
    IClock clock) : IAiTaskHandler
{
    private static readonly JsonSerializerOptions PreferencesJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.RecipeId is not { } recipeId || context.RecipeVersionId is not { } versionId)
        {
            return Failure(AiFailureCategory.Validation, "An SEO package names a recipe and the exact version it was asked against.");
        }

        if (ReadSections(context.Inputs) is not { } requested
            || !requested.Overlaps(AiSeoSectionCatalog.ModelWritten))
        {
            return Failure(AiFailureCategory.Validation, "An SEO package names at least one section the model writes.");
        }

        string? pinnedRules = null;

        if (context.Inputs is null
            || !context.Inputs.TryGetValue(AiSeoPackageInputs.RuleSet, out pinnedRules)
            || !string.Equals(pinnedRules, rules.Version, StringComparison.Ordinal))
        {
            // Both versions are stated: they are configuration fingerprints, not secrets, and a mismatch between
            // this host and the one that took the request is otherwise invisible.
            return Failure(
                AiFailureCategory.DomainInvalid,
                $"The SEO rules changed after this package was asked for (asked under '{pinnedRules ?? "none"}', this host has '{rules.Version}'); ask again.");
        }

        var snapshot = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);

        if (!snapshot.Succeeded)
        {
            return Failure(AiFailureCategory.Validation, "The recipe version this package was asked against is no longer available.");
        }

        if (await StaleReasonAsync(recipeId, versionId, cancellationToken) is { } staleReason)
        {
            return Failure(AiFailureCategory.DomainInvalid, staleReason);
        }

        var source = snapshot.Value!.Document;
        var candidates = requested.Contains(AiSeoSection.InternalLinks)
            ? await CandidatesAsync(recipeId, cancellationToken)
            : [];
        var template = templates.Get(AiTaskCatalog.SeoPackage);

        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)))
            .WithOutputSchema(AiSeoPackageOutputSchema.Json)
            .WithPreferences(context.WorkspaceId, RenderPreferences(requested, context.Inputs, candidates.Count))
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(source));

        if (candidates.Count > 0)
        {
            // Isolation here rests on the recipe facade, which reads only the resolved workspace's recipes under
            // its query filter, and on the worker having resolved and validated the workspace before this ran.
            // The workspace argument is the builder's own check, not a second one: every segment of this
            // envelope is given the same id, so it cannot catch a candidate that came from elsewhere.
            builder.AddReference(context.WorkspaceId, JsonSerializer.Serialize(candidates.Select(candidate => new { id = candidate.Id, title = candidate.Title })));
        }

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiSeoPackageOutputDocument>(
                builder.Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiSeoPackageOutputValidator.AsDelegate(
                    rules,
                    new AiSeoRequestContext(
                        requested,
                        source.AssetLinks.ToDictionary(link => link.Id, link => link.Caption),
                        candidates.Select(candidate => candidate.Id).ToHashSet(),
                        recipeId))),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var document = outcome.Document!;

        if (CheckAgainstRequestAndSource(document, requested, source, candidates, recipeId) is { } mismatch)
        {
            return AiTaskHandlerOutcome.ForFailure(AiFailureCategory.DomainInvalid, mismatch, outcome.Attempts);
        }

        // After the call: the recipe may have moved, or been reopened, while the model ran.
        var current = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (current.Succeeded && current.Value!.Status is not RecipeStatus.Approved)
        {
            return AiTaskHandlerOutcome.ForFailure(AiFailureCategory.DomainInvalid, "The recipe is no longer approved.", outcome.Attempts);
        }

        // A candidate may have been reopened or archived while the model ran; a link idea to it is stale too.
        if (document.Sections.InternalLinks.Count > 0)
        {
            var fresh = (await CandidatesAsync(recipeId, cancellationToken)).Select(candidate => candidate.Id).ToHashSet();

            if (document.Sections.InternalLinks.Any(link => !fresh.Contains(link.RecipeId)))
            {
                return AiTaskHandlerOutcome.ForFailure(
                    AiFailureCategory.DomainInvalid,
                    $"{AiOutputReason.SeoItemNotInSource}: a recipe this package links to is no longer approved.",
                    outcome.Attempts);
            }
        }

        var captions = source.AssetLinks.ToDictionary(link => link.Id, link => link.Caption);
        var findings = new List<AiSeoFinding>(AiSeoClaimScanner.Scan(document.Sections, AiEditorialSourceFacts.From(source), captions, source.Recipe.Title));

        var slug = requested.Contains(AiSeoSection.Slug) ? DeriveSlug(document, source, recipeId) : null;

        var (changes, warnings) = Translate(document, findings, slug);
        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: versionId,
            currentVersionId: current.Succeeded ? current.Value!.CurrentVersion?.Id : null,
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

    /// <summary>The slug, from the proposed title when one was written and otherwise the recipe's own.</summary>
    private (string Value, string Basis)? DeriveSlug(AiSeoPackageOutputDocument document, RecipeSnapshotDocument source, Guid recipeId)
    {
        var fromTitle = SeoSlug.FromTitle(document.Sections.SeoTitle?.Text, rules.SlugMaxLength);

        if (fromTitle.Length > 0)
        {
            return (SeoSlug.Offer(fromTitle, recipeId, rules.SlugMaxLength), "seoTitle");
        }

        var fromRecipe = SeoSlug.FromTitle(source.Recipe.Title, rules.SlugMaxLength);

        // Never a constant: two recipes with nothing usable in their titles must not be offered one slug, and a
        // reserved path segment is never offered as it stands.
        return fromRecipe.Length > 0
            ? (SeoSlug.Offer(fromRecipe, recipeId, rules.SlugMaxLength), "recipeTitle")
            : (SeoSlug.Offer(string.Empty, recipeId, rules.SlugMaxLength), "fallback");
    }

    private async Task<string?> StaleReasonAsync(Guid recipeId, Guid versionId, CancellationToken cancellationToken)
    {
        var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!detail.Succeeded)
        {
            return "The recipe this package was asked against is no longer available.";
        }

        if (detail.Value!.CurrentVersion?.Id != versionId)
        {
            return "The recipe has a newer version than the one this package was asked against.";
        }

        return detail.Value.Status is RecipeStatus.Approved ? null : "The recipe is no longer approved.";
    }

    /// <summary>
    /// The workspace's other approved recipes, id and title only, most recently updated first (the recipe search's
    /// default order), at most <see cref="AiPolicy.MaxSeoLinkCandidates"/>. Read through the recipe facade, so another
    /// workspace's recipes are invisible here; the model may link to these and to nothing else. A workspace with
    /// more approved recipes than the cap offers its most recently touched ones: recency is a stable, explainable
    /// order, not a claim that they are the most relevant links.
    /// </summary>
    private Task<IReadOnlyList<RecipeLinkCandidateServiceModel>> CandidatesAsync(Guid recipeId, CancellationToken cancellationToken) =>
        recipes.ListApprovedLinkCandidatesAsync(recipeId, AiPolicy.MaxSeoLinkCandidates, cancellationToken);

    private static string? CheckAgainstRequestAndSource(
        AiSeoPackageOutputDocument document,
        IReadOnlySet<AiSeoSection> requested,
        RecipeSnapshotDocument source,
        IReadOnlyList<RecipeLinkCandidateServiceModel> candidates,
        Guid recipeId)
    {
        if (AiSeoSectionCatalog.Present(document.Sections).Any(section => !requested.Contains(section)))
        {
            return $"{AiOutputReason.EditorialSectionNotRequested}: the answer wrote a section the request did not ask for.";
        }

        var links = source.AssetLinks.ToDictionary(link => link.Id);

        foreach (var alt in document.Sections.AltText)
        {
            if (!links.TryGetValue(alt.AssetLinkId, out var link))
            {
                return $"{AiOutputReason.SeoItemNotInSource}: an alt text names an image that is not part of the version this package was asked against.";
            }

            // "From the caption" is a claim about where it came from; with no caption it cannot be true.
            if (alt.Basis is AiSeoAltTextBasis.Caption && string.IsNullOrWhiteSpace(link.Caption))
            {
                return $"{AiOutputReason.SeoAltTextInvalid}: an alt text claims to rest on a caption the image does not have.";
            }
        }

        var candidateIds = candidates.Select(candidate => candidate.Id).ToHashSet();

        return document.Sections.InternalLinks.Any(link => link.RecipeId == recipeId || !candidateIds.Contains(link.RecipeId))
            ? $"{AiOutputReason.SeoItemNotInSource}: a link idea points at this recipe or at one that was not offered."
            : null;
    }

    private static IReadOnlySet<AiSeoSection>? ReadSections(IReadOnlyDictionary<string, string>? inputs)
    {
        if (inputs is null || !inputs.TryGetValue(AiSeoPackageInputs.Sections, out var value))
        {
            return null;
        }

        var sections = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AiSeoSectionCatalog.Parse)
            .ToList();

        return sections.Count == 0 || sections.Any(section => section is null)
            ? null
            : sections.Select(section => section!.Value).ToHashSet();
    }

    /// <summary>The sections the model writes, the configured limits, and the brand facts pinned at request time, as creator data.</summary>
    private string RenderPreferences(IReadOnlySet<AiSeoSection> requested, IReadOnlyDictionary<string, string>? inputs, int linkCandidates)
    {
        var preferences = new Dictionary<string, object?>
        {
            ["sections"] = AiSeoSectionCatalog.ModelWritten.Where(requested.Contains).Select(AiSeoSectionCatalog.ToWire).ToArray(),

            // How many recipes the REFERENCE segment offers, so "none" is stated rather than inferred from absence.
            ["linkCandidates"] = linkCandidates,
            ["limits"] = new
            {
                titleCharacters = new[] { rules.TitleMinLength, rules.TitleMaxLength },
                metaDescriptionCharacters = new[] { rules.MetaDescriptionMinLength, rules.MetaDescriptionMaxLength },
                keyPhrases = new[] { rules.KeyPhraseMinCount, rules.KeyPhraseMaxCount },
                keyPhraseMaxWords = rules.KeyPhraseMaxWords,
                altTextMaxCharacters = rules.AltTextMaxLength,
                anchorMaxCharacters = rules.AnchorTextMaxLength,
                maxInternalLinks = rules.MaxInternalLinks,
            },
        };

        foreach (var key in new[] { AiSeoPackageInputs.BrandName, AiSeoPackageInputs.Audience, AiSeoPackageInputs.Locale })
        {
            if (inputs is not null && inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                preferences[key] = value;
            }
        }

        return JsonSerializer.Serialize(preferences, PreferencesJson);
    }

    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiSeoPackageOutputDocument document,
        List<AiSeoFinding> findings,
        (string Value, string Basis)? slug)
    {
        var changes = new List<AiResolvedChange>();
        var addRow = new Dictionary<(AiSeoSection, int?), int>();
        var sections = document.Sections;

        void Item(AiSeoSection section, int? index, string text, params (string Field, string Value)[] extras)
        {
            var targetId = Guid.NewGuid();

            addRow[(section, index)] = changes.Count;
            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.ContentSection, targetId, FieldName: null, BeforeValue: null,
                text, ProposedPosition: index ?? 0, changes.Count));

            AddSet(changes, targetId, AiEditorialFields.Section, AiSeoSectionCatalog.ToWire(section));

            foreach (var (field, value) in extras)
            {
                AddSet(changes, targetId, field, value);
            }
        }

        if (sections.SeoTitle is not null) Item(AiSeoSection.SeoTitle, null, sections.SeoTitle.Text);
        if (sections.MetaDescription is not null) Item(AiSeoSection.MetaDescription, null, sections.MetaDescription.Text);

        for (var i = 0; i < sections.KeyPhrases.Count; i++) Item(AiSeoSection.KeyPhrases, i, sections.KeyPhrases[i].Phrase);

        if (slug is { } derived)
        {
            Item(AiSeoSection.Slug, null, derived.Value, (AiSeoFields.SlugBasis, derived.Basis));
            findings.Add(new AiSeoFinding(
                AiSeoSection.Slug, null, AiWarningKind.Limitation, AiSeoClaimScanner.SlugUniquenessNotChecked,
                "This slug was derived from the title by rule. Whether it is unique among your pages has not been checked."));
        }

        for (var i = 0; i < sections.AltText.Count; i++)
        {
            var alt = sections.AltText[i];
            Item(AiSeoSection.AltText, i, alt.Text,
                (AiSeoFields.AssetLinkId, alt.AssetLinkId.ToString()), (AiSeoFields.Basis, alt.Basis.ToString()));
        }

        for (var i = 0; i < sections.InternalLinks.Count; i++)
        {
            var link = sections.InternalLinks[i];
            Item(AiSeoSection.InternalLinks, i, link.AnchorText,
                (AiSeoFields.RecipeId, link.RecipeId.ToString()), (AiSeoFields.Reason, link.Reason));
        }

        int? RowFor(AiSeoSection section, int? index) =>
            addRow.TryGetValue((section, index), out var row) ? row
            : index is null && addRow.Where(pair => pair.Key.Item1 == section).Select(pair => (int?)pair.Value).FirstOrDefault() is { } first ? first
            : null;

        var warnings = document.Warnings
            .Select(warning => new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = AiPolicy.ModelWarningLabel + warning.Message,
                ChangeIndex = warning.Section is { } section ? RowFor(section, null) : null,
            })
            .ToList();

        // The server's findings, after the model's, so they cannot be displaced by its warning limit and cannot
        // be left out by anything it says.
        warnings.AddRange(findings.Select(finding => new AiOutputWarning
        {
            Kind = finding.Kind,
            Message = $"[{finding.Code}] {finding.Message}",
            ChangeIndex = RowFor(finding.Section, finding.ItemIndex),
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
            AiChangeKind.Set, AiChangeTargetKind.ContentSection, targetId, field, BeforeValue: null, value,
            ProposedPosition: null, changes.Count));
    }

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string message) =>
        AiTaskHandlerOutcome.ForFailure(category, message, []);
}

/// <summary>The field names specific to the SEO package's rows, beside <see cref="AiEditorialFields.Section"/>.</summary>
public static class AiSeoFields
{
    public const string SlugBasis = "slugBasis";

    public const string AssetLinkId = "assetLinkId";

    public const string Basis = "basis";

    public const string RecipeId = "recipeId";

    public const string Reason = "reason";
}

/// <summary>The keys the request writes into <c>AiOperation.TaskInputsJson</c> and the handler reads back.</summary>
public static class AiSeoPackageInputs
{
    /// <summary>The requested sections, comma-separated by name.</summary>
    public const string Sections = "sections";

    /// <summary>The <c>SeoRules.Version</c> in force when the request was made. The handler refuses any other.</summary>
    public const string RuleSet = "seoRuleSet";

    public const string BrandProfileRevision = "brandProfileRevision";

    public const string BrandName = "brandName";

    public const string Audience = "audience";

    public const string Locale = "locale";
}
