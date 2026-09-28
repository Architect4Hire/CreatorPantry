using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.RecipeReview"/> (AIREC-006): field-linked findings about one pinned version of an
/// existing recipe, each with a severity, an evidence basis, and whether it needs checking against a reference
/// this system does not have.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shaped like <see cref="IngredientSubstitutionAiTaskHandler"/>, not like a diff-producing
/// handler.</strong> It reads the pinned snapshot because a finding is about what the recipe actually says, but
/// nothing it returns addresses a change to anything, so <see cref="AiDiffCalculator"/> is never called and the
/// operation runs in <see cref="AiOperationScope.Advisory"/>, which permits no target at all.
/// </para>
/// <para>
/// <strong>Unlike a substitution, this reviews the whole recipe rather than one selected line</strong>, so the
/// template renders no per-request bound — there is no selected ingredient or declared goal to name, only the
/// SOURCE segment itself. The whole snapshot is what a finding's field reference is checked against.
/// </para>
/// <para>
/// <strong>A finding's field reference is validated twice, for two different reasons.</strong>
/// <see cref="AiRecipeReviewOutputValidator"/> checks the shape of the pairing — an id where a field kind needs
/// one, none where it does not — with no snapshot in hand to check anything further. This handler checks the
/// other half afterward: that an id naming an ingredient line, a step, or an equipment item actually names one
/// in the pinned version, the same check <see cref="AiDiffCalculator"/> makes for an ordinary diff's target row
/// via <see cref="AiOutputReason.TargetNotInSource"/>.
/// </para>
/// </remarks>
internal sealed class RecipeReviewAiTaskHandler(
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
            // Unreachable through the request seam, which requires both. Refused rather than assumed: a review
            // with no pinned source has nothing to review.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "A review names a recipe and the exact version it was asked against.",
                []);
        }

        var snapshot = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);

        if (!snapshot.Succeeded)
        {
            // Gone, or in another workspace and therefore invisible. Its own category: retrying will not bring
            // it back.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "The recipe version this review was asked against is no longer available.",
                []);
        }

        var source = snapshot.Value!.Document;
        var template = templates.Get(AiTaskCatalog.RecipeReview);

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)))
            .WithOutputSchema(AiRecipeReviewOutputSchema.Json)
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(source))
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiRecipeReviewOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiRecipeReviewOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        if (InvalidFieldRef(source, outcome.Document!) is { } fieldRefMessage)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid, fieldRefMessage, outcome.Attempts);
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
    /// Whether any finding names an ingredient line, step, or equipment item the pinned version does not
    /// contain, and the message to fail with if one does.
    /// </summary>
    /// <remarks>
    /// The validator already checked that an id is present exactly where a field kind requires one; this is the
    /// half only a snapshot can check — that the id actually names something in it. A model naming a line from
    /// a different recipe, a stale id, or one it invented all fail here rather than being stored as a link to
    /// nothing.
    /// </remarks>
    private static string? InvalidFieldRef(RecipeSnapshotDocument source, AiRecipeReviewOutputDocument document)
    {
        var ingredientIds = source.IngredientGroups
            .SelectMany(group => group.Ingredients)
            .Select(ingredient => ingredient.Id)
            .ToHashSet();

        var stepIds = source.InstructionGroups
            .SelectMany(group => group.Steps)
            .Select(step => step.Id)
            .ToHashSet();

        var equipmentIds = source.Equipment.Select(equipment => equipment.Id).ToHashSet();

        foreach (var finding in document.Findings)
        {
            var known = finding.FieldRef switch
            {
                { FieldKind: AiRecipeReviewFieldKind.IngredientLine, EntityId: { } id } =>
                    ingredientIds.Contains(id),
                { FieldKind: AiRecipeReviewFieldKind.Step, EntityId: { } id } => stepIds.Contains(id),
                { FieldKind: AiRecipeReviewFieldKind.Equipment, EntityId: { } id } => equipmentIds.Contains(id),
                _ => true,
            };

            if (!known)
            {
                return "A finding points at an ingredient line, step, or equipment item that is not part of "
                    + "the version this review was asked against.";
            }
        }

        return null;
    }

    /// <summary>
    /// Each finding becomes one server-minted id: an Add row carrying its summary, then a Set row per other
    /// field it supplied. A warning about one finding is re-addressed to that finding's Add row by index, so
    /// <see cref="AiProposalAssembler"/> resolves it exactly as it resolves a substitution's warning.
    /// </summary>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiRecipeReviewOutputDocument document)
    {
        var changes = new List<AiResolvedChange>();
        var addRowIndexByFinding = new int[document.Findings.Count];

        for (var index = 0; index < document.Findings.Count; index++)
        {
            var finding = document.Findings[index];
            var targetId = Guid.NewGuid();

            addRowIndexByFinding[index] = changes.Count;
            changes.Add(new AiResolvedChange(
                AiChangeKind.Add,
                AiChangeTargetKind.RecipeReviewFinding,
                targetId,
                FieldName: null,
                BeforeValue: null,
                finding.Summary,
                ProposedPosition: index,
                changes.Count));

            AddSet(changes, targetId, AiRecipeReviewFields.Category, finding.Category.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.Severity, finding.Severity.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.FieldKind, finding.FieldRef.FieldKind.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.FieldEntityId, finding.FieldRef.EntityId?.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.Allergen, finding.Allergen);
            AddSet(changes, targetId, AiRecipeReviewFields.AllergenEffect, finding.AllergenEffect?.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.Diet, finding.Diet);
            AddSet(changes, targetId, AiRecipeReviewFields.DietaryEffect, finding.DietaryEffect?.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.EvidenceBasis, finding.EvidenceBasis.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.EvidenceNote, finding.EvidenceNote);
            AddSet(changes, targetId, AiRecipeReviewFields.Confidence, finding.Confidence.ToString());
            AddSet(
                changes,
                targetId,
                AiRecipeReviewFields.RequiresReferenceCheck,
                finding.RequiresReferenceCheck.ToString());
            AddSet(changes, targetId, AiRecipeReviewFields.UnknownFactors, Describe(finding.UnknownFactors));
        }

        var warnings = document.Warnings
            .Select(warning => new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = warning.Message,
                ChangeIndex = warning.FindingIndex is { } findingIndex
                    ? addRowIndexByFinding[findingIndex]
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
            AiChangeTargetKind.RecipeReviewFinding,
            targetId,
            field,
            BeforeValue: null,
            value,
            ProposedPosition: null,
            changes.Count));
    }

    private static string? Describe(IReadOnlyList<string> factors) =>
        factors.Count == 0 ? null : string.Join("; ", factors);
}
