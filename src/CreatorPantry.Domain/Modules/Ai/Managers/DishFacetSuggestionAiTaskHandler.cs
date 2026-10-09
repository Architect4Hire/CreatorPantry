using System.Globalization;
using System.Text;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.DishFacetSuggestion"/>: reads the dish name a creator typed and proposes which
/// cuisine, course and cooking technique it names, each chosen from the platform vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The catalogue is read here, shown to the model, and enforced against the same read.</strong> That
/// one decision is what keeps this a constrained command: the candidate lists are built from
/// <see cref="IVocabularyFacade"/>, rendered into the REFERENCES segment, and every code that comes back is
/// looked up in the dictionary built from that same list. A code the model invented, remembered from another
/// product, or produced by lower-casing a display name is discarded — so the worst a confused answer can do
/// is suggest nothing. Validating against a list fetched separately, or a list hard-coded here, would both
/// reintroduce the gap this closes.
/// </para>
/// <para>
/// <strong>Never diffs a recipe, because there is none.</strong> Like
/// <see cref="RecipeConceptsAiTaskHandler"/>, this names no recipe
/// (<see cref="AiTaskExecutionContext.RecipeId"/> is null), so there is no <c>RecipeSnapshotDocument</c> to
/// resolve against and <see cref="AiDiffCalculator"/> is never called. The reading becomes one
/// <see cref="AiChangeKind.Add"/> row carrying the name that was read, then
/// <see cref="AiChangeKind.Set"/> rows per facet, all under one id this handler mints — never the model,
/// which has no before value to assert and no existing row to name.
/// </para>
/// <para>
/// <strong>The name travels as <see cref="PromptSegmentKind.UntrustedText"/> and the vocabulary as
/// <see cref="PromptSegmentKind.References"/>.</strong> The name is the one thing here a creator typed or
/// pasted, so it is the one thing that could carry an injection, and <c>ai.md</c> asks that it be fenced as
/// untrusted rather than folded into the task's own instructions. The vocabulary is platform reference data
/// with no <c>WorkspaceId</c> at all — the same rows for every creator — so it is a reference, not creator
/// data, and no workspace's content is mixed into it.
/// </para>
/// <para>
/// <strong>A declined facet is stored, not dropped.</strong> "The name does not say how this is cooked" is an
/// answer the creator benefits from reading beside an empty control, and a handler that kept only the codes
/// would turn a considered refusal into an indistinguishable silence.
/// </para>
/// </remarks>
internal sealed class DishFacetSuggestionAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    IVocabularyFacade vocabulary,
    IClock clock) : IAiTaskHandler
{
    private static readonly Dictionary<string, string> NoTemplateInputs = [];

    /// <summary>
    /// What a creator is told when a suggestion named a code the vocabulary does not have.
    /// </summary>
    /// <remarks>
    /// It says the facet was left to them rather than naming the value that was refused. The refused string is
    /// model output about their dish, and repeating it would put a cuisine the catalogue has never heard of in
    /// front of them as though it were a near miss.
    /// </remarks>
    private const string OffCatalogueWarning =
        "The {0} reading named a value that is not in the cooking vocabulary, so that facet was left for you "
            + "to choose.";

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var dishName = Read(context.Inputs, AiDishFacetInputs.DishName);

        // The request seam requires a name, so this is the backstop for an operation that reached the worker
        // some other way. Refused rather than asked with an empty name, which would spend a provider call to
        // read nothing.
        if (dishName is null)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid,
                "A dish-facet reading needs a dish name, and this operation carries none.",
                []);
        }

        var candidates = await ReadCandidatesAsync(cancellationToken);

        // Every catalogue empty is a deployment with no vocabulary, not a question for a model: there would be
        // nothing for it to choose from and every answer would be discarded.
        if (candidates.Count == 0)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid,
                "The cooking vocabulary is empty, so there is nothing a dish name could be read as.",
                []);
        }

        var template = templates.Get(AiTaskCatalog.DishFacetSuggestion);

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(NoTemplateInputs))
            .WithOutputSchema(AiDishFacetsOutputSchema.Json)

            // The workspace on this reference is a required stamp, not a leak check that caught anything: the
            // candidate lists are platform vocabulary with no WorkspaceId at all, cached under a global key,
            // so there is no workspace they could have come from. The builder admits no segment without one
            // (it throws on an empty id), so this is how global reference data is passed. The segment that
            // genuinely needs checking is the untrusted one below, and it is checked independently.
            .AddReference(context.WorkspaceId, RenderCandidates(candidates))
            .WithUntrustedText(context.WorkspaceId, dishName)
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiDishFacetsOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiDishFacetsOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!, dishName, candidates);

        // The attempt that actually produced this document -- the last one, whether or not it was a
        // correction -- is what provenance should name, not the gateway's own configured defaults.
        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
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
            : AiTaskHandlerOutcome.ForFailure(
                assembly.Failure!.Category, assembly.Failure.Message, outcome.Attempts);
    }

    /// <summary>
    /// The active vocabulary a name may be read as, by facet, each entry keyed by its own code.
    /// </summary>
    /// <remarks>
    /// Active only. A retired cuisine is still shown wherever a record already carries it, but it may not be
    /// a new suggestion — proposing one would hand a creator a pin the seed generator then refuses as
    /// retired.
    /// </remarks>
    private async Task<Dictionary<AiDishFacet, Dictionary<string, string>>> ReadCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var cuisines = await vocabulary.ListActiveCuisinesAsync(cancellationToken);
        var courses = await vocabulary.ListActiveCoursesAsync(cancellationToken);
        var techniques = await vocabulary.ListActiveTechniquesAsync(cancellationToken);

        var candidates = new Dictionary<AiDishFacet, Dictionary<string, string>>();

        Add(AiDishFacet.Cuisine, cuisines.Select(entry => (entry.Code, entry.DisplayName)));
        Add(AiDishFacet.DishType, courses.Select(entry => (entry.Code, entry.DisplayName)));
        Add(AiDishFacet.Method, techniques.Select(entry => (entry.Code, entry.DisplayName)));

        return candidates;

        void Add(AiDishFacet facet, IEnumerable<(string Code, string DisplayName)> entries)
        {
            // Ordinal, because a code is an identifier: two codes differing only in case are two codes, and
            // matching them case-insensitively would let a model's "THAI" pass for the catalogue's "thai".
            var byCode = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (code, displayName) in entries)
            {
                byCode[code] = displayName;
            }

            if (byCode.Count > 0)
            {
                candidates[facet] = byCode;
            }
        }
    }

    /// <summary>
    /// The candidate lists as plain text: one heading per facet, one `code — display name` line per entry.
    /// </summary>
    /// <remarks>
    /// Both halves on every line on purpose. The code is what must come back and the display name is what
    /// makes the code legible — a list of bare codes would have a model guessing what <c>sous-vide</c> covers,
    /// and a list of bare names would have it inventing the code.
    /// </remarks>
    private static string RenderCandidates(Dictionary<AiDishFacet, Dictionary<string, string>> candidates)
    {
        var rendered = new StringBuilder();

        foreach (var facet in Facets)
        {
            if (!candidates.TryGetValue(facet, out var entries))
            {
                continue;
            }

            if (rendered.Length > 0)
            {
                rendered.AppendLine();
            }

            rendered.AppendLine(CultureInfo.InvariantCulture, $"{facet} candidates:");

            foreach (var (code, displayName) in entries)
            {
                rendered.AppendLine(CultureInfo.InvariantCulture, $"- {code} — {displayName}");
            }
        }

        return rendered.ToString();
    }

    /// <summary>The facets in a fixed order, so a rendered list and a stored reading do not depend on a hash.</summary>
    private static readonly AiDishFacet[] Facets =
        [AiDishFacet.Cuisine, AiDishFacet.DishType, AiDishFacet.Method];

    /// <summary>
    /// The reading becomes one server-minted id: an Add row carrying the name that was read, then a Set row
    /// per facet field the answer supplied.
    /// </summary>
    /// <remarks>
    /// <strong>This is where an off-catalogue code stops.</strong> A suggestion naming a code that is not in
    /// the list the model was shown is recorded as a decline, with a warning, rather than being stored or
    /// dropped silently: the creator sees that the facet was not answered and why, and no pin is ever made
    /// from a code the vocabulary does not have.
    /// </remarks>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiDishFacetsOutputDocument document,
        string dishName,
        Dictionary<AiDishFacet, Dictionary<string, string>> candidates)
    {
        var changes = new List<AiResolvedChange>();
        var warnings = new List<AiOutputWarning>();
        var targetId = Guid.NewGuid();

        changes.Add(new AiResolvedChange(
            AiChangeKind.Add,
            AiChangeTargetKind.DishFacetSuggestion,
            targetId,
            FieldName: null,
            BeforeValue: null,
            dishName,
            ProposedPosition: 0,
            changes.Count));

        foreach (var suggestion in document.Suggestions)
        {
            var code = string.IsNullOrWhiteSpace(suggestion.Code) ? null : suggestion.Code;
            var known = code is not null
                && candidates.TryGetValue(suggestion.Facet, out var entries)
                && entries.ContainsKey(code);

            if (code is not null && !known)
            {
                // Named, unusable, and said so. The rationale is kept because it is still the model's account
                // of its reading, and a creator comparing it with the warning can see what went wrong.
                warnings.Add(new AiOutputWarning
                {
                    Kind = AiWarningKind.Limitation,
                    Message = string.Format(
                        CultureInfo.InvariantCulture, OffCatalogueWarning, suggestion.Facet),
                    ChangeIndex = 0,
                });
            }

            AddSet(changes, targetId, AiDishFacetFields.Code(suggestion.Facet), known ? code : null);
            AddSet(
                changes,
                targetId,
                AiDishFacetFields.Confidence(suggestion.Facet),
                known ? suggestion.Confidence.ToString() : null);
            AddSet(changes, targetId, AiDishFacetFields.Rationale(suggestion.Facet), suggestion.Rationale);
        }

        warnings.AddRange(document.Warnings.Select(warning => new AiOutputWarning
        {
            Kind = warning.Kind,
            Message = warning.Message,
            ChangeIndex = null,
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
            AiChangeKind.Set,
            AiChangeTargetKind.DishFacetSuggestion,
            targetId,
            field,
            BeforeValue: null,
            value,
            ProposedPosition: null,
            changes.Count));
    }

    private static string? Read(IReadOnlyDictionary<string, string>? inputs, string name) =>
        inputs is not null && inputs.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
