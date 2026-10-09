using System.Globalization;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.RecipeFirstDraft"/> (AIREC-002): renders the creator's structured brief into the
/// <c>recipe.first-draft</c> template, asks the model for one complete structured recipe draft, and stores it
/// as a proposal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never diffs a recipe, because there is none</strong> — the same reason
/// <see cref="RecipeConceptsAiTaskHandler"/> never calls <see cref="AiDiffCalculator"/>. A first-draft
/// operation names no recipe (<see cref="AiTaskExecutionContext.RecipeId"/> is null), so there is no
/// <c>RecipeSnapshotDocument</c> to resolve a change against.
/// </para>
/// <para>
/// <strong>Group membership is recovered from document order, not from a parent-id field.</strong>
/// <see cref="AiDiffCalculator"/>'s own convention for an <c>Add</c> is that <c>TargetId</c> names the
/// <em>existing</em> parent a new row goes into — workable there because the model is shown the pinned
/// recipe's real ids. Here nothing exists yet, on either end of a group/line relationship, so
/// <c>TargetId</c> is used the way <see cref="RecipeConceptsAiTaskHandler"/> already uses it: as the row's
/// own self-identity, minted here and never the model's, so the row's own <see cref="AiChangeKind.Set"/> rows
/// have something stable to address. A group's membership is therefore positional: every <c>Ingredient</c> or
/// <c>InstructionStep</c> <c>Add</c> row belongs to the most recently emitted <c>IngredientGroup</c> or
/// <c>InstructionGroup</c> <c>Add</c> row that precedes it in <c>SortOrder</c>. Reconstructing this ordering
/// into an actual recipe is 9.4b's "special transaction," not this handler's job — this handler only has to
/// emit it consistently, which <see cref="Translate"/> does in one single forward pass.
/// </para>
/// <para>
/// <strong>Warnings are whole-draft only.</strong> Unlike <see cref="RecipeConceptsAiTaskHandler"/>, which can
/// address a warning to one concept by index, a first draft has five different nested list shapes a warning
/// could plausibly target, and wiring an index scheme across all of them is materially more translator
/// complexity than SCOPE's plain "warnings" category asks for. Every warning — including one built from an
/// unresolved question — carries <c>ChangeIndex: null</c>. A later prompt can add per-item targeting if a
/// review UI comes to need it.
/// </para>
/// <para>
/// <strong>A whole-group <c>"isOptional"</c> <see cref="AiChangeKind.Set"/> row has nowhere to land yet.</strong>
/// This is emitted faithfully for <see cref="AiChangeTargetKind.IngredientGroup"/> and
/// <see cref="AiChangeTargetKind.InstructionGroup"/> whenever the model marks a whole group optional — but
/// <c>RecipeIngredientGroup</c> and <c>RecipeInstructionGroup</c> (the Recipes module's own entities) have no
/// <c>IsOptional</c> column at all today; only a single line or equipment item does. The row validates and
/// stores correctly — nothing here is lost or inverted — it simply has no destination column for a future
/// acceptance step ("9.4b") to write it into until the Recipes module gains one. Known and accepted rather
/// than worked around: adding that column is a Recipes-module domain change (its own migration, its own
/// skill), not this capability's to make.
/// </para>
/// </remarks>
internal sealed class RecipeFirstDraftAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    IClock clock) : IAiTaskHandler
{
    private static readonly Dictionary<string, string> NoTemplateInputs = [];

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        var template = templates.Get(AiTaskCatalog.RecipeFirstDraft);

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(NoTemplateInputs))
            .WithOutputSchema(AiRecipeDraftOutputSchema.Json)
            .WithPreferences(context.WorkspaceId, RenderBrief(context.Inputs))
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiRecipeDraftOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiRecipeDraftOutputValidator.AsDelegate),
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
    /// The brief as plain text: AIREC-001's own eleven declared fields, defaulting an unsupplied or blank one
    /// to "not specified" the same way <see cref="RecipeConceptsAiTaskHandler.RenderBrief"/> does, plus one
    /// more line for a selected concept's summary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler does not itself define AIREC-002's request contract — that is 9.4's own prompt ("from a
    /// selected concept or declared brief fields"). Reusing AIREC-001's already-established field vocabulary
    /// here, rather than inventing a new one, is what "declared brief fields" in that SCOPE line refers back
    /// to; <c>selectedConcept</c> is the one addition for the other case that SCOPE names.
    /// </para>
    /// <para>
    /// Every key is named from <see cref="AiFirstDraftInputs"/>, which is also what the request seam writes.
    /// The two sides are in different processes with no shared type between them, so a literal here would let
    /// a rename on one side produce drafts silently missing a field rather than a build failure.
    /// </para>
    /// </remarks>
    private static string RenderBrief(IReadOnlyDictionary<string, string>? supplied)
    {
        string Value(string name) =>
            supplied is not null
                && supplied.TryGetValue(name, out var value)
                && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : "not specified";

        return $"""
            Selected concept: {Value(AiFirstDraftInputs.SelectedConcept)}

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
    /// One forward pass over the validated document. See the type-level remark for the group-membership
    /// convention this relies on.
    /// </summary>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiRecipeDraftOutputDocument document)
    {
        var changes = new List<AiResolvedChange>();

        AddSet(changes, null, "title", document.Title, AiChangeTargetKind.Recipe);
        AddSet(changes, null, "description", document.Description, AiChangeTargetKind.Recipe);
        AddSet(changes, null, "notes", document.Notes, AiChangeTargetKind.Recipe);

        if (document.Timing is { } timing)
        {
            AddSet(changes, null, "prepTimeMinutes", Format(timing.PrepTimeMinutes), AiChangeTargetKind.Recipe);
            AddSet(changes, null, "cookTimeMinutes", Format(timing.CookTimeMinutes), AiChangeTargetKind.Recipe);
            AddSet(changes, null, "restTimeMinutes", Format(timing.RestTimeMinutes), AiChangeTargetKind.Recipe);
            AddSet(changes, null, "totalTimeMinutes", Format(timing.TotalTimeMinutes), AiChangeTargetKind.Recipe);
        }

        if (document.Yield is { } yield)
        {
            AddSet(changes, null, "yieldText", yield.YieldText, AiChangeTargetKind.Recipe);
            AddSet(changes, null, "yieldQuantity", Format(yield.YieldQuantity), AiChangeTargetKind.Recipe);
            AddSet(changes, null, "yieldUnitText", yield.YieldUnitText, AiChangeTargetKind.Recipe);
            AddSet(changes, null, "servingCount", Format(yield.ServingCount), AiChangeTargetKind.Recipe);
            AddSet(changes, null, "servingSize", Format(yield.ServingSize), AiChangeTargetKind.Recipe);
        }

        for (var groupIndex = 0; groupIndex < document.IngredientGroups.Count; groupIndex++)
        {
            var group = document.IngredientGroups[groupIndex];
            var groupId = Guid.NewGuid();

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.IngredientGroup, groupId,
                FieldName: null, BeforeValue: null, group.Title, ProposedPosition: groupIndex, changes.Count));

            if (group.IsOptional)
            {
                AddSet(changes, groupId, "isOptional", "true", AiChangeTargetKind.IngredientGroup);
            }

            for (var lineIndex = 0; lineIndex < group.Ingredients.Count; lineIndex++)
            {
                var line = group.Ingredients[lineIndex];
                var lineId = Guid.NewGuid();

                changes.Add(new AiResolvedChange(
                    AiChangeKind.Add, AiChangeTargetKind.Ingredient, lineId,
                    FieldName: null, BeforeValue: null, line.DisplayText, ProposedPosition: lineIndex, changes.Count));

                AddSet(changes, lineId, "ingredientNameText", line.IngredientNameText, AiChangeTargetKind.Ingredient);
                AddSet(changes, lineId, "unitText", line.UnitText, AiChangeTargetKind.Ingredient);
                AddSet(changes, lineId, "quantity", Format(line.Quantity), AiChangeTargetKind.Ingredient);
                AddSet(changes, lineId, "quantityUpper", Format(line.QuantityUpper), AiChangeTargetKind.Ingredient);
                AddSet(changes, lineId, "preparationNote", line.PreparationNote, AiChangeTargetKind.Ingredient);

                if (line.IsOptional)
                {
                    AddSet(changes, lineId, "isOptional", "true", AiChangeTargetKind.Ingredient);
                }
            }
        }

        for (var groupIndex = 0; groupIndex < document.Instructions.Count; groupIndex++)
        {
            var group = document.Instructions[groupIndex];
            var groupId = Guid.NewGuid();

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.InstructionGroup, groupId,
                FieldName: null, BeforeValue: null, group.Title, ProposedPosition: groupIndex, changes.Count));

            if (group.IsOptional)
            {
                AddSet(changes, groupId, "isOptional", "true", AiChangeTargetKind.InstructionGroup);
            }

            for (var stepIndex = 0; stepIndex < group.Steps.Count; stepIndex++)
            {
                var step = group.Steps[stepIndex];
                var stepId = Guid.NewGuid();

                changes.Add(new AiResolvedChange(
                    AiChangeKind.Add, AiChangeTargetKind.InstructionStep, stepId,
                    FieldName: null, BeforeValue: null, step.Text, ProposedPosition: stepIndex, changes.Count));

                AddSet(changes, stepId, "note", step.Note, AiChangeTargetKind.InstructionStep);
                AddSet(changes, stepId, "durationMinutes", Format(step.DurationMinutes), AiChangeTargetKind.InstructionStep);
            }
        }

        for (var itemIndex = 0; itemIndex < document.Equipment.Count; itemIndex++)
        {
            var item = document.Equipment[itemIndex];
            var itemId = Guid.NewGuid();

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.Equipment, itemId,
                FieldName: null, BeforeValue: null, item.DisplayText, ProposedPosition: itemIndex, changes.Count));

            AddSet(changes, itemId, "note", item.Note, AiChangeTargetKind.Equipment);

            if (item.IsOptional)
            {
                AddSet(changes, itemId, "isOptional", "true", AiChangeTargetKind.Equipment);
            }
        }

        var warnings = new List<AiOutputWarning>();

        foreach (var question in document.UnresolvedQuestions)
        {
            warnings.Add(new AiOutputWarning
            {
                Kind = AiWarningKind.UnresolvedQuestion,
                Message = question,
                ChangeIndex = null,
            });
        }

        foreach (var warning in document.Warnings)
        {
            warnings.Add(new AiOutputWarning { Kind = warning.Kind, Message = warning.Message, ChangeIndex = null });
        }

        return (changes, warnings);
    }

    private static void AddSet(
        List<AiResolvedChange> changes, Guid? targetId, string field, string? value, AiChangeTargetKind targetKind)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        changes.Add(new AiResolvedChange(
            AiChangeKind.Set, targetKind, targetId,
            field, BeforeValue: null, value, ProposedPosition: null, changes.Count));
    }

    private static string? Format(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
}
