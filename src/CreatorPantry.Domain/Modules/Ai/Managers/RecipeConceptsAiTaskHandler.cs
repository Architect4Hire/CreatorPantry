using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.RecipeConcepts"/> (AIREC-001): renders the creator's structured brief into the
/// <c>recipe.concepts</c> template, asks the model for two to five distinct concepts, and stores them as a
/// proposal — the first handler in this module that actually calls <see cref="IAiCompletionGateway"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never diffs a recipe, because there is none.</strong> A concept-generation operation names no
/// recipe (<see cref="AiTaskExecutionContext.RecipeId"/> is null for it), so unlike every other capability this
/// module ships, there is no <c>RecipeSnapshotDocument</c> to resolve a change against and
/// <see cref="AiDiffCalculator"/> is never called. Each validated <see cref="AiRecipeConcept"/> is translated
/// directly into an <see cref="AiChangeKind.Add"/> row (the title) followed by <see cref="AiChangeKind.Set"/>
/// rows (its other fields), all sharing one id this handler mints — never the model, which has no before value
/// to assert and no existing row to name.
/// </para>
/// <para>
/// <strong>Reads its brief from <see cref="AiTaskExecutionContext.Inputs"/>, not from a recipe.</strong> A
/// field the creator did not supply renders as "not specified" rather than an empty placeholder, so the
/// brief always reads as a complete sentence. Today's recipe-bound request path
/// (<c>IAiProposalFacade.RequestAsync</c>) supplies no inputs at all; a dedicated, allow-listed request
/// contract for this task is separate work (AIREC-001's own endpoint).
/// </para>
/// <para>
/// <strong>The brief travels as <see cref="PromptSegmentKind.Preferences"/>, not folded into the task's own
/// instructions.</strong> Every field is creator-supplied data — several genuinely free text
/// (<c>exclusions</c>, <c>dietaryGoals</c>, <c>creatorStyle</c>) — and <c>ai.md</c> asks that user instructions
/// be treated as untrusted prompt content. <see cref="PromptSegmentKind.Preferences"/> exists exactly for "the
/// workspace's and creator's stated preferences", so the brief is rendered on its own and carried there at
/// <see cref="PromptSegmentTrust.CreatorData"/> trust; the template's own body is instructions only and has no
/// placeholder for it.
/// </para>
/// </remarks>
internal sealed class RecipeConceptsAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    IClock clock) : IAiTaskHandler
{
    private static readonly Dictionary<string, string> NoTemplateInputs = [];

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        var template = templates.Get(AiTaskCatalog.RecipeConcepts);

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(NoTemplateInputs))
            .WithOutputSchema(AiConceptOutputSchema.Json)
            .WithPreferences(context.WorkspaceId, RenderBrief(context.Inputs))
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiConceptOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiConceptOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!);

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
    /// The brief as plain text, one labeled line per declared field, defaulting an unsupplied or blank one to
    /// "not specified" so every line reads as a complete sentence.
    /// </summary>
    private static string RenderBrief(IReadOnlyDictionary<string, string>? supplied)
    {
        string Value(string name) =>
            supplied is not null
                && supplied.TryGetValue(name, out var value)
                && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : "not specified";

        return $"""
            Brief:
            - Dish name: {Value(AiBriefInputs.DishName)}
            - Audience: {Value(AiBriefInputs.Audience)}
            - Course: {Value(AiBriefInputs.Course)}
            - Cuisine: {Value(AiBriefInputs.Cuisine)}
            - Dietary goals: {Value(AiBriefInputs.DietaryGoals)}
            - Available ingredients: {Value(AiBriefInputs.AvailableIngredients)}
            - Exclusions: {Value(AiBriefInputs.Exclusions)}
            - Equipment: {Value(AiBriefInputs.Equipment)}
            - Skill level: {Value(AiBriefInputs.Skill)}
            - Season: {Value(AiBriefInputs.Season)}
            - Time budget: {Value(AiBriefInputs.TimeBudget)}
            - Creator style: {Value(AiBriefInputs.CreatorStyle)}
            """;
    }

    /// <summary>
    /// Each concept becomes one server-minted id: an Add row carrying its title, then a Set row per other
    /// field it supplied. A concept's own warning is re-addressed to that concept's Add row by index, so
    /// <see cref="AiProposalAssembler"/> can resolve it exactly the way it resolves a recipe-diff warning.
    /// </summary>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiConceptOutputDocument document)
    {
        var changes = new List<AiResolvedChange>();
        var addRowIndexByConcept = new int[document.Concepts.Count];

        for (var conceptIndex = 0; conceptIndex < document.Concepts.Count; conceptIndex++)
        {
            var concept = document.Concepts[conceptIndex];
            var targetId = Guid.NewGuid();

            addRowIndexByConcept[conceptIndex] = changes.Count;
            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.RecipeConcept, targetId,
                FieldName: null, BeforeValue: null, concept.Title, ProposedPosition: conceptIndex, changes.Count));

            AddSet(changes, targetId, AiConceptFields.Summary, concept.Summary);
            AddSet(changes, targetId, AiConceptFields.DistinctnessRationale, concept.DistinctnessRationale);
            AddSet(changes, targetId, AiConceptFields.Assumptions, Join(concept.Assumptions));
            AddSet(changes, targetId, AiConceptFields.SuggestedIngredients, Join(concept.SuggestedIngredients));
            AddSet(changes, targetId, AiConceptFields.DietaryNotes, Join(concept.DietaryNotes));
            AddSet(changes, targetId, AiConceptFields.TimeBudgetNote, concept.TimeBudgetNote);
            AddSet(changes, targetId, AiConceptFields.SkillLevelFit, concept.SkillLevelFit);
        }

        var warnings = document.Warnings
            .Select(warning => new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = warning.Message,
                ChangeIndex = warning.ConceptIndex is { } conceptIndex
                    ? addRowIndexByConcept[conceptIndex]
                    : null,
            })
            .ToList();

        return (changes, warnings);
    }

    private static void AddSet(List<AiResolvedChange> changes, Guid targetId, string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        changes.Add(new AiResolvedChange(
            AiChangeKind.Set, AiChangeTargetKind.RecipeConcept, targetId,
            field, BeforeValue: null, value, ProposedPosition: null, changes.Count));
    }

    private static string? Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join("; ", values);
}
