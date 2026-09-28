using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.IngredientSubstitution"/> (AIREC-004): ranked alternatives for one ingredient
/// line in a pinned version, with what each does to the dish and what must be checked before trusting it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reads a recipe and proposes no change to it</strong>, which no other handler in this module does.
/// It needs the pinned snapshot because the answer depends on what the ingredient is <em>doing</em> in that
/// method — the same butter binds in one recipe and browns in another — but nothing it returns addresses a
/// change to anything, so <see cref="AiDiffCalculator"/> is never called and there is no before value for one
/// to compute. The operation runs in <see cref="AiOperationScope.Advisory"/>, which permits no target at all.
/// </para>
/// <para>
/// <strong>The selected line is named by position — never by its text, and not by its id either.</strong> Its
/// text is the creator's untrusted content; an import can carry an injection as easily as a headnote can, and
/// rendering it into the task's own instructions would put untrusted bytes in the system message, which is
/// precisely what <see cref="PromptEnvelopeBuilder"/> exists to prevent. The id would be safe on that count
/// and is left out anyway, because nothing is gained by handing one over.
/// </para>
/// <para>
/// <strong>That is not the same as the model receiving no identifier, and it should not be read as one.</strong>
/// The SOURCE segment is the serialized snapshot, and a snapshot carries the id of every group, line and step
/// — it has to, because that is what lets a diff match a moved row to itself. What actually holds here is
/// narrower and does not depend on keeping ids away from the model:
/// <see cref="AiSubstitutionOutputDocument"/> has no field for a target id anywhere in it, so an answer has
/// nowhere to address a row and nowhere to repeat an id back.
/// </para>
/// <para>
/// <strong>The creator's reason is the most sensitive free text this module carries</strong>, because it is
/// where somebody types "my reader is allergic to peanuts". It travels as
/// <see cref="PromptSegmentKind.Preferences"/> at <see cref="PromptSegmentTrust.CreatorData"/>, exactly like
/// AIREC-001's brief and AIREC-003's goal. A reason that names a person's allergy is context for a culinary
/// answer; it is never permission to declare anything safe for them, and
/// <see cref="AiSubstitutionOutputValidator"/> enforces the shape of that limit regardless of what the model
/// makes of the instruction.
/// </para>
/// </remarks>
internal sealed class IngredientSubstitutionAiTaskHandler(
    IAiCompletionGateway gateway,
    IRecipeFacade recipes,
    IPromptTemplateStore templates,
    IClock clock) : IAiTaskHandler
{
    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.RecipeId is not { } recipeId || context.RecipeVersionId is not { } versionId)
        {
            // Unreachable through the request seam, which requires both. Refused rather than assumed: advice
            // about an ingredient with no recipe around it is advice about a word.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "A substitution names a recipe and the exact version the ingredient was selected in.",
                []);
        }

        if (ReadIngredientId(context.Inputs) is not { } ingredientId)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation, "A substitution names the ingredient it is about.", []);
        }

        var snapshot = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);

        if (!snapshot.Succeeded)
        {
            // Gone, or in another workspace and therefore invisible. Its own category: retrying will not
            // bring it back.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "The recipe version this substitution was asked against is no longer available.",
                []);
        }

        var source = snapshot.Value!.Document;

        // Re-checked here although the request seam checked it too. The request wrote this id into the
        // operation and a worker reads it back in another process, so this is the last point at which an id
        // that does not name a line in the pinned version can be caught before the model is asked to reason
        // about one that is not there.
        if (Locate(source, ingredientId) is not { } position)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "That ingredient is not part of the version this substitution was asked against.",
                []);
        }

        var template = templates.Get(AiTaskCatalog.IngredientSubstitution);

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(Bounds(position)))
            .WithOutputSchema(AiSubstitutionOutputSchema.Json)
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(source))
            .WithPreferences(context.WorkspaceId, RenderReason(context.Inputs))
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiSubstitutionOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiSubstitutionOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!);

        // The attempt that actually produced this document — the last one, whether or not it was a correction
        // — is what provenance should name, not the gateway's own configured defaults.
        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: versionId,
            currentVersionId: versionId,
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
    /// The template's inputs: which line, and how many alternatives the server will take.
    /// </summary>
    /// <remarks>
    /// The count is derived from <see cref="AiPolicy.MaxSubstitutionCount"/> rather than written into the
    /// body, for the reason AIREC-003 derives its allow-lists: the instructions cannot then describe a bound
    /// the validator does not apply.
    /// </remarks>
    private static Dictionary<string, string> Bounds(string selectedIngredient) =>
        new(StringComparer.Ordinal)
        {
            ["selectedIngredient"] = selectedIngredient,
            ["maxAlternatives"] = AiPolicy.MaxSubstitutionCount.ToString(),
        };

    private static Guid? ReadIngredientId(IReadOnlyDictionary<string, string>? supplied) =>
        supplied is not null
            && supplied.TryGetValue(AiSubstitutionInputs.IngredientId, out var value)
            && Guid.TryParse(value, out var parsed)
            && parsed != Guid.Empty
                ? parsed
                : null;

    /// <summary>
    /// Where the selected line sits in the source, as a position the instructions can name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A position rather than the id, and rather than the line's text.</strong> The text is the
    /// creator's untrusted content and must not reach the system message at all. The id would work and is
    /// left out anyway, since a position says which line just as exactly and hands over nothing. Note that
    /// the SOURCE segment carries row ids regardless — see this type's own remarks for what does and does not
    /// follow from that.
    /// </para>
    /// <para>
    /// Counted from one, and in the order the source serializes — which is the order the model reads it in,
    /// so the two cannot disagree.
    /// </para>
    /// </remarks>
    private static string? Locate(RecipeSnapshotDocument source, Guid ingredientId)
    {
        for (var group = 0; group < source.IngredientGroups.Count; group++)
        {
            var lines = source.IngredientGroups[group].Ingredients;

            for (var line = 0; line < lines.Count; line++)
            {
                if (lines[line].Id == ingredientId)
                {
                    return $"line {line + 1} of ingredient group {group + 1}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Why the creator is asking, defaulting to no reason at all when they gave none.
    /// </summary>
    /// <remarks>
    /// "I just want to know what else would work" is a legitimate ask, so an absent reason reads as a complete
    /// sentence rather than a blank the model has to interpret — and, more to the point, rather than a gap it
    /// might fill by guessing at a dietary motive nobody stated.
    /// </remarks>
    private static string RenderReason(IReadOnlyDictionary<string, string>? supplied)
    {
        var reason = supplied is not null
            && supplied.TryGetValue(AiSubstitutionInputs.Reason, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : "not given — no reason was stated, so do not assume one";

        return $"""
            Why the creator is asking: {reason}
            """;
    }

    /// <summary>
    /// Each alternative becomes one server-minted id: an Add row carrying its name, then a Set row per other
    /// field it supplied. A warning about one alternative is re-addressed to that alternative's Add row by
    /// index, so <see cref="AiProposalAssembler"/> resolves it exactly as it resolves a recipe-diff warning.
    /// </summary>
    /// <remarks>
    /// The Add row's <c>ProposedPosition</c> carries the rank, zero-based. It is the only ordering the stored
    /// rows keep, and AIREC-004 asks for ranked alternatives — a reader that lost the rank would be reading a
    /// set of options the model had deliberately put in an order.
    /// </remarks>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiSubstitutionOutputDocument document)
    {
        var changes = new List<AiResolvedChange>();
        var addRowIndexBySubstitution = new int[document.Substitutions.Count];

        for (var index = 0; index < document.Substitutions.Count; index++)
        {
            var substitution = document.Substitutions[index];
            var targetId = Guid.NewGuid();

            addRowIndexBySubstitution[index] = changes.Count;
            changes.Add(new AiResolvedChange(
                AiChangeKind.Add,
                AiChangeTargetKind.IngredientSubstitution,
                targetId,
                FieldName: null,
                BeforeValue: null,
                substitution.Alternative,
                ProposedPosition: substitution.Rank - 1,
                changes.Count));

            AddSet(changes, targetId, AiSubstitutionFields.FunctionalRole, substitution.FunctionalRole);
            AddSet(changes, targetId, AiSubstitutionFields.QuantityGuidance, substitution.QuantityGuidance);
            AddSet(changes, targetId, AiSubstitutionFields.TechniqueImpact, substitution.TechniqueImpact);
            AddSet(changes, targetId, AiSubstitutionFields.FlavorImpact, substitution.FlavorImpact);
            AddSet(changes, targetId, AiSubstitutionFields.TextureImpact, substitution.TextureImpact);
            AddSet(changes, targetId, AiSubstitutionFields.DietaryEffects, Describe(substitution.DietaryEffects));
            AddSet(
                changes, targetId, AiSubstitutionFields.AllergenEffects, Describe(substitution.AllergenEffects));
            AddSet(changes, targetId, AiSubstitutionFields.Confidence, substitution.Confidence.ToString());
            AddSet(changes, targetId, AiSubstitutionFields.EvidenceBasis, substitution.EvidenceBasis.ToString());
            AddSet(changes, targetId, AiSubstitutionFields.EvidenceNote, substitution.EvidenceNote);
            AddSet(changes, targetId, AiSubstitutionFields.TestRecommendation, substitution.TestRecommendation);
        }

        var warnings = document.Warnings
            .Select(warning => new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = warning.Message,
                ChangeIndex = warning.SubstitutionIndex is { } substitutionIndex
                    ? addRowIndexBySubstitution[substitutionIndex]
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
            AiChangeKind.Set,
            AiChangeTargetKind.IngredientSubstitution,
            targetId,
            field,
            BeforeValue: null,
            value,
            ProposedPosition: null,
            changes.Count));
    }

    /// <summary>
    /// The allergen consequences as a creator would read them: "introduces tree nuts; may introduce soy".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Words rather than enum names. This is the field where being understood matters most, and
    /// <c>"MayIntroduce soy"</c> is a machine token — one every reader would otherwise have to translate
    /// separately, which is how one of them eventually shows a creator the raw thing.
    /// </para>
    /// <para>
    /// The direction survives the flattening, which is the part that matters. Every phrase this can produce
    /// names an effect that runs one way, because <see cref="AiAllergenEffectKind"/> has no member that runs
    /// the other.
    /// </para>
    /// </remarks>
    private static string? Describe(IReadOnlyList<AiAllergenEffect> effects) =>
        effects.Count == 0
            ? null
            : string.Join("; ", effects.Select(effect => $"{Phrase(effect.Effect)} {effect.Allergen}"));

    /// <inheritdoc cref="Describe(IReadOnlyList{AiAllergenEffect})"/>
    private static string? Describe(IReadOnlyList<AiDietaryEffect> effects) =>
        effects.Count == 0
            ? null
            : string.Join("; ", effects.Select(effect => $"{Phrase(effect.Effect)} {effect.Diet}"));

    private static string Phrase(AiAllergenEffectKind effect) => effect switch
    {
        AiAllergenEffectKind.Introduces => "introduces",
        AiAllergenEffectKind.MayIntroduce => "may introduce",
        _ => "unknown whether it introduces",
    };

    private static string Phrase(AiDietaryEffectKind effect) => effect switch
    {
        AiDietaryEffectKind.Conflicts => "conflicts with",
        AiDietaryEffectKind.MayConflict => "may conflict with",
        _ => "unknown whether it conflicts with",
    };
}

/// <summary>The keys AIREC-004's request writes into <c>AiOperation.TaskInputsJson</c>.</summary>
/// <remarks>
/// The same agreement <see cref="AiRevisionInputs"/> records, for the same reason: the request seam writes
/// this and the handler reads it back in another process, with no type between them.
/// </remarks>
public static class AiSubstitutionInputs
{
    /// <summary>
    /// The ingredient line the creator selected, as its id in the pinned version.
    /// </summary>
    /// <remarks>
    /// An id rather than the line's text, because the text is the creator's untrusted content and the id is
    /// not. It is checked against the pinned snapshot at request time and again in the handler; neither check
    /// is redundant, because the two run in different processes.
    /// </remarks>
    public const string IngredientId = "ingredientId";

    /// <summary>Why the creator is asking, in their own words. Optional.</summary>
    public const string Reason = "reason";
}
