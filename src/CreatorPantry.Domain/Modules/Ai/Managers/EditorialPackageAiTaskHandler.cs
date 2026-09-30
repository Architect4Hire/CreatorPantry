using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// RCPUB-001: writes the editorial package for one approved recipe version — headnote, introduction, tips,
/// substitutions, storage/reheating, FAQ and call to action — as a proposal the creator reviews, edits and
/// decides on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Advisory.</strong> It reads a recipe and proposes no change to it: the operation's scope is fixed
/// server-side at <see cref="AiOperationScope.Advisory"/> and <see cref="AiChangeTargetKind.ContentSection"/>
/// has no path to a recipe edit. A derivative becomes content only when a creator accepts it as a
/// <c>ContentRevision</c>, which is a different seam.
/// </para>
/// <para>
/// <strong>Exact versions.</strong> The recipe version is checked to still be current and still approved
/// both before the model is called — so a stale request spends nothing — and again by the assembler after,
/// because the recipe can move while the model runs. The brand facts were pinned by value at request time and
/// travel in the operation's inputs; nothing here re-reads a brand profile that may have changed since.
/// </para>
/// <para>
/// <strong>Nothing the model says decides what is supported.</strong> Its answer is validated, checked against
/// the request and the pinned recipe, and then scanned in code for claims the recipe does not make
/// (<see cref="AiEditorialClaimScanner"/>). Those findings are warnings the model cannot remove.
/// </para>
/// </remarks>
internal sealed class EditorialPackageAiTaskHandler(
    IAiCompletionGateway gateway,
    IRecipeFacade recipes,
    IPromptTemplateStore templates,
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
            return Failure(AiFailureCategory.Validation, "An editorial package names a recipe and the exact version it was asked against.");
        }

        if (ReadSections(context.Inputs) is not { Count: > 0 } requested)
        {
            return Failure(AiFailureCategory.Validation, "An editorial package names at least one section to write.");
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
        var template = templates.Get(AiTaskCatalog.EditorialPackage);

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)))
            .WithOutputSchema(AiEditorialPackageOutputSchema.Json)
            .WithPreferences(context.WorkspaceId, RenderPreferences(requested, context.Inputs))
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(source))
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiEditorialPackageOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiEditorialPackageOutputValidator.AsDelegateFor(new AiEditorialRequestContext(
                    requested,
                    source.IngredientGroups.SelectMany(group => group.Ingredients).Select(line => line.Id).ToHashSet()))),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var document = outcome.Document!;

        if (CheckAgainstRequestAndSource(document, requested, source) is { } mismatch)
        {
            return AiTaskHandlerOutcome.ForFailure(AiFailureCategory.DomainInvalid, mismatch, outcome.Attempts);
        }

        // Fetched again after the call: the recipe may have moved while the model ran, and the assembler is
        // what refuses a proposal whose source is no longer the current one.
        var current = await recipes.GetDetailAsync(recipeId, cancellationToken);

        // Still approved, as well as still the same version: an edit after approval reopens the recipe, and a
        // package written about words the creator has since withdrawn must not be stored as current.
        if (current.Succeeded && current.Value!.Status is not RecipeStatus.Approved)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid, "The recipe is no longer approved.", outcome.Attempts);
        }

        var findings = AiEditorialClaimScanner.Scan(document.Sections, AiEditorialSourceFacts.From(source));
        var (changes, warnings) = Translate(document, findings);

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

        return detail.Value.Status is RecipeStatus.Approved
            ? null
            : "The recipe is no longer approved.";
    }

    private static string? CheckAgainstRequestAndSource(
        AiEditorialPackageOutputDocument document, IReadOnlySet<AiEditorialSection> requested, RecipeSnapshotDocument source)
    {
        if (AiEditorialSectionCatalog.Present(document.Sections).Any(section => !requested.Contains(section)))
        {
            return $"{AiOutputReason.EditorialSectionNotRequested}: the answer wrote a section the request did not ask for.";
        }

        var lineIds = source.IngredientGroups.SelectMany(group => group.Ingredients).Select(line => line.Id).ToHashSet();

        return document.Sections.Substitutions.Any(item => !lineIds.Contains(item.LineId))
            ? $"{AiOutputReason.EditorialLineNotInSource}: a substitution names an ingredient line that is not part of the version this package was asked against."
            : null;
    }

    private static IReadOnlySet<AiEditorialSection>? ReadSections(IReadOnlyDictionary<string, string>? inputs)
    {
        if (inputs is null || !inputs.TryGetValue(AiEditorialPackageInputs.Sections, out var value))
        {
            return null;
        }

        var sections = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AiEditorialSectionCatalog.Parse)
            .ToList();

        return sections.Any(section => section is null)
            ? null
            : sections.Select(section => section!.Value).ToHashSet();
    }

    /// <summary>The requested sections and the brand facts pinned at request time, as creator data.</summary>
    private static string RenderPreferences(IReadOnlySet<AiEditorialSection> requested, IReadOnlyDictionary<string, string>? inputs)
    {
        var preferences = new Dictionary<string, object?>
        {
            ["sections"] = AiEditorialSectionCatalog.All.Where(requested.Contains).Select(section => ToWire(section)).ToArray(),
        };

        foreach (var key in new[] { AiEditorialPackageInputs.BrandName, AiEditorialPackageInputs.Audience, AiEditorialPackageInputs.Locale })
        {
            if (inputs is not null && inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                preferences[key] = value;
            }
        }

        // Relaxed escaping so a brand name reads as written ("Sam's Kitchen", not "Sam\u0027s Kitchen") in a prompt
        // the model reads and a creator may one day inspect. It is a data segment, fenced; nothing executes it.
        return JsonSerializer.Serialize(preferences, PreferencesJson);
    }

    internal static string ToWire(AiEditorialSection section) =>
        char.ToLowerInvariant(section.ToString()[0]) + section.ToString()[1..];

    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiEditorialPackageOutputDocument document, IReadOnlyList<AiEditorialFinding> findings)
    {
        var changes = new List<AiResolvedChange>();
        var addRow = new Dictionary<(AiEditorialSection, int?), int>();
        var sections = document.Sections;

        void Item(AiEditorialSection section, int? index, string text, params (string Field, string Value)[] extras)
        {
            var targetId = Guid.NewGuid();

            addRow[(section, index)] = changes.Count;
            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.ContentSection, targetId, FieldName: null, BeforeValue: null,
                text, ProposedPosition: index ?? 0, changes.Count));

            AddSet(changes, targetId, AiEditorialFields.Section, ToWire(section));

            foreach (var (field, value) in extras)
            {
                AddSet(changes, targetId, field, value);
            }
        }

        if (sections.Headnote is not null) Item(AiEditorialSection.Headnote, null, sections.Headnote.Text);
        if (sections.Introduction is not null) Item(AiEditorialSection.Introduction, null, sections.Introduction.Text);

        for (var i = 0; i < sections.Tips.Count; i++) Item(AiEditorialSection.Tips, i, sections.Tips[i].Text);

        for (var i = 0; i < sections.Substitutions.Count; i++)
        {
            var item = sections.Substitutions[i];
            Item(AiEditorialSection.Substitutions, i, item.Suggestion,
                (AiEditorialFields.LineId, item.LineId.ToString()), (AiEditorialFields.CulinaryNote, item.CulinaryNote));
        }

        if (sections.StorageReheating is not null) Item(AiEditorialSection.StorageReheating, null, sections.StorageReheating.Text);

        for (var i = 0; i < sections.Faq.Count; i++)
        {
            Item(AiEditorialSection.Faq, i, sections.Faq[i].Answer, (AiEditorialFields.Question, sections.Faq[i].Question));
        }

        if (sections.Cta is not null) Item(AiEditorialSection.Cta, null, sections.Cta.Text);

        int? RowFor(AiEditorialSection section, int? index) =>
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

        // The server's own findings, after the model's, so they cannot be displaced by the model's warning
        // limit and cannot be left out by anything it says.
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

/// <summary>The field names on the <see cref="AiChangeTargetKind.ContentSection"/> rows a package is flattened into.</summary>
public static class AiEditorialFields
{
    public const string Section = "section";

    public const string Question = "question";

    public const string LineId = "lineId";

    public const string CulinaryNote = "culinaryNote";
}

/// <summary>The keys the request writes into <c>AiOperation.TaskInputsJson</c> and the handler reads back.</summary>
/// <remarks>
/// The brand facts are pinned by value because the brand profile is edited in place and an operation must
/// describe the facts it was actually given. <see cref="BrandProfileRevision"/> records which revision they were.
/// </remarks>
public static class AiEditorialPackageInputs
{
    /// <summary>The requested sections, comma-separated by name.</summary>
    public const string Sections = "sections";

    public const string BrandProfileRevision = "brandProfileRevision";

    public const string BrandName = "brandName";

    public const string Audience = "audience";

    public const string Locale = "locale";
}
