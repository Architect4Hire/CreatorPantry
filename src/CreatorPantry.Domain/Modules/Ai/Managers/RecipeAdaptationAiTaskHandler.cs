using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.RecipeAdaptation"/> (AIREC-005): adapts one pinned version of an existing recipe
/// toward exactly one declared goal, returning a complete cross-field proposal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shaped like <see cref="RecipeRevisionAiTaskHandler"/>, not like
/// <see cref="IngredientSubstitutionAiTaskHandler"/>.</strong> The answer is an ordinary
/// <see cref="AiOutputDocument"/>, diffed against the pinned snapshot by <see cref="AiDiffCalculator"/> exactly
/// as a revision's is. The operation always runs in <see cref="AiOperationScope.WholeRecipe"/> — a complete
/// cross-field proposal cannot fit a narrower scope — so unlike a revision this handler renders no scope choice
/// into the template; only the declared goal varies.
/// </para>
/// <para>
/// <strong>A yield goal computes before it asks.</strong>
/// <see cref="IRecipeFacade.ScaleAsync(Guid, int, decimal?, decimal?, CancellationToken)"/> — the same
/// deterministic scaling ING-003 already exposes, through the primitive-only overload a cross-module caller
/// uses — runs first, and its result travels two places: into a
/// REFERENCES segment so the model has the exact figures to copy, and into
/// <see cref="AiAdaptationOutputValidator"/> so an answer that computed its own instead is rejected rather than
/// trusted. Neither the prompt's wording nor the model's cooperation is what stops the arithmetic drifting —
/// the validator is.
/// </para>
/// <para>
/// <strong>The goal detail is untrusted text and travels as one</strong>, exactly as AIREC-003's goal and
/// AIREC-004's reason do: in a <see cref="PromptSegmentKind.Preferences"/> segment at
/// <see cref="PromptSegmentTrust.CreatorData"/>, never rendered into the task's own instructions. The
/// per-goal instructions themselves are fixed server text chosen from a closed set by the declared
/// <see cref="AiAdaptationGoal"/>, so they render safely into the TASK segment the same way a revision's scope
/// bound does.
/// </para>
/// </remarks>
internal sealed class RecipeAdaptationAiTaskHandler(
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
            // Unreachable through the request seam, which requires both. Refused rather than assumed: an
            // adaptation with no pinned source has nothing to compute a diff or a scaling figure against.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "An adaptation names a recipe and the exact version it was asked against.",
                []);
        }

        if (ReadGoal(context.Inputs) is not { } goal)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation, "An adaptation names the one goal it is working toward.", []);
        }

        var snapshot = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);

        if (!snapshot.Succeeded)
        {
            // The recipe or the version is gone, or belongs to another workspace and is therefore invisible.
            // Its own category: retrying will not bring it back.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "The recipe version this adaptation was asked against is no longer available.",
                []);
        }

        RecipeScalingPreview? deterministicScaling = null;

        if (goal is AiAdaptationGoal.Yield)
        {
            var scaled = await ResolveDeterministicScalingAsync(
                recipeId, snapshot.Value!.VersionNumber, context.Inputs, cancellationToken);

            if (scaled.Failure is not null)
            {
                return scaled.Failure;
            }

            deterministicScaling = scaled.Preview;
        }

        var template = templates.Get(AiTaskCatalog.RecipeAdaptation);
        var source = snapshot.Value!.Document;

        var envelopeBuilder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(Bounds(goal)))
            .WithOutputSchema(AiOutputSchema.Json)
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(source))
            .WithPreferences(context.WorkspaceId, RenderGoalDetail(goal, context.Inputs));

        if (deterministicScaling is not null)
        {
            envelopeBuilder = envelopeBuilder.AddReference(
                context.WorkspaceId, JsonSerializer.Serialize(Summarize(deterministicScaling)));
        }

        var envelope = envelopeBuilder.Build();

        AiOutputValidatorDelegate<AiOutputDocument> validate = (payload, expectedSchemaVersion, scope) =>
            AiAdaptationOutputValidator.Validate(
                payload, expectedSchemaVersion, scope, goal, deterministicScaling);

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                validate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        // Every before value comes from here, computed against the pinned snapshot. A model that wanted to
        // assert one has nowhere to put it, and this would ignore it if it did.
        var diff = AiDiffCalculator.Calculate(source, outcome.Document!);

        if (!diff.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid, diff.Failure!.Message, outcome.Attempts);
        }

        var document = deterministicScaling is null
            ? outcome.Document!
            : outcome.Document! with
            {
                Warnings = [.. outcome.Document!.Warnings, .. ScalingWarnings(deterministicScaling)],
            };

        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: versionId,
            currentVersionId: versionId,
            document,
            diff.Changes,
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
    /// Computes the deterministic scaling a yield goal needs, through the same facade operation ING-003 already
    /// exposes.
    /// </summary>
    /// <remarks>
    /// Re-resolved here although the request seam checked the target's shape too: the request wrote this into
    /// the operation and a worker reads it back in another process, and the recipe's own yield may only be
    /// known to be structured — or not — once the current version is actually read.
    /// </remarks>
    private async Task<(RecipeScalingPreview? Preview, AiTaskHandlerOutcome? Failure)> ResolveDeterministicScalingAsync(
        Guid recipeId,
        int sourceVersionNumber,
        IReadOnlyDictionary<string, string>? inputs,
        CancellationToken cancellationToken)
    {
        var (multiplier, targetYieldQuantity) = ReadYieldTarget(inputs);

        if (multiplier is null && targetYieldQuantity is null)
        {
            return (null, AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "A yield adaptation names a multiplier or a target yield to scale toward.",
                []));
        }

        var scaled = await recipes.ScaleAsync(
            recipeId, sourceVersionNumber, multiplier, targetYieldQuantity, cancellationToken);

        if (!scaled.Succeeded)
        {
            return (null, AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                scaled.Error!.Message,
                []));
        }

        return (scaled.Value!.Preview, null);
    }

    /// <summary>
    /// The per-goal instructions, rendered from a closed set the server chooses — never from creator text —
    /// alongside the bound every AIREC-005 operation shares: the whole recipe, because a cross-field proposal
    /// cannot fit a narrower one.
    /// </summary>
    private static Dictionary<string, string> Bounds(AiAdaptationGoal goal)
    {
        var targets = AiPolicy.AllowedTargets(AiOperationScope.WholeRecipe)
            .Where(target => AiChangeApplicability.For(target).Count > 0)
            .OrderBy(target => target.ToString(), StringComparer.Ordinal)
            .ToList();

        var fields = targets
            .SelectMany(target => AiPolicy.AllowedFields(AiOperationScope.WholeRecipe, target)
                .Select(field => $"{target}.{field}"))
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToList();

        var kinds = targets
            .SelectMany(target => AiChangeApplicability.For(target).Select(kind => $"{kind} on {target}"))
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToList();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["goal"] = Describe(goal),
            ["goalInstructions"] = Instructions(goal),
            ["allowedFields"] = string.Join(", ", fields),
            ["allowedChanges"] = string.Join(", ", kinds),
        };
    }

    private static string Describe(AiAdaptationGoal goal) => goal switch
    {
        AiAdaptationGoal.Dietary => "dietary",
        AiAdaptationGoal.Equipment => "equipment",
        AiAdaptationGoal.Yield => "yield",
        AiAdaptationGoal.SkillLevel => "skill level",
        _ => "none",
    };

    /// <summary>
    /// Fixed English per goal, chosen by server code from a closed set of four. Safe to render into the task's
    /// own instructions because none of it is creator or model text.
    /// </summary>
    private static string Instructions(AiAdaptationGoal goal) => goal switch
    {
        AiAdaptationGoal.Dietary => """
            Adapt the recipe to satisfy the diet or restriction named in the PREFERENCES segment. State every
            allergen and dietary consequence of a change as a safety caution beside it — never as a guarantee
            that the result suits anyone in particular; you cannot see the brands used or the kitchen it is
            made in.
            """,

        AiAdaptationGoal.Equipment => """
            Adapt the method to work with the equipment named in the PREFERENCES segment — either equipment the
            creator has, or equipment they say they do not. Where a substitution changes timing, temperature or
            texture, say so explicitly rather than leaving the recipe silently inconsistent.
            """,

        AiAdaptationGoal.Yield => """
            Adapt the recipe to the batch size already computed in the REFERENCES segment below. Use those
            ingredient quantities exactly as given — copy the figures, do not recompute them — and address
            everything a different batch size changes that a multiplier does not: pan size, timing, and whether
            leavening or technique still behaves the same at this scale.
            """,

        AiAdaptationGoal.SkillLevel => """
            Adapt the method for the skill level or technique access named in the PREFERENCES segment, without
            changing what the finished dish is. Prefer substituting or explaining a technique over removing a
            step that is doing real work in the recipe.
            """,

        _ => "",
    };

    private static AiAdaptationGoal? ReadGoal(IReadOnlyDictionary<string, string>? supplied) =>
        supplied is not null
            && supplied.TryGetValue(AiAdaptationInputs.Goal, out var value)
            && Enum.TryParse<AiAdaptationGoal>(value, ignoreCase: false, out var goal)
            && goal is not AiAdaptationGoal.Unspecified
                ? goal
                : null;

    private static (decimal? Multiplier, decimal? TargetYieldQuantity) ReadYieldTarget(
        IReadOnlyDictionary<string, string>? supplied)
    {
        if (supplied is null)
        {
            return (null, null);
        }

        var multiplier = supplied.TryGetValue(AiAdaptationInputs.TargetMultiplier, out var multiplierText)
            && decimal.TryParse(multiplierText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (decimal?)null;

        var targetYield = supplied.TryGetValue(AiAdaptationInputs.TargetYieldQuantity, out var targetYieldText)
            && decimal.TryParse(targetYieldText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedTarget)
                ? parsedTarget
                : (decimal?)null;

        return (multiplier, targetYield);
    }

    /// <summary>
    /// The goal detail, defaulting to no detail at all when the goal is yield and the creator gave none — the
    /// numbers already say what the goal is.
    /// </summary>
    private static string RenderGoalDetail(AiAdaptationGoal goal, IReadOnlyDictionary<string, string>? supplied)
    {
        var detail = supplied is not null
            && supplied.TryGetValue(AiAdaptationInputs.GoalDetail, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : goal is AiAdaptationGoal.Yield
                    ? "not given — the target batch size in the REFERENCES segment is the whole instruction"
                    : "not given";

        return $"""
            What the goal means for this recipe: {detail}
            """;
    }

    /// <summary>
    /// The deterministic scaling result, reshaped into the minimal figures a model needs to copy rather than
    /// the internal fraction representation <see cref="RecipeScalingPreview"/> carries.
    /// </summary>
    /// <remarks>
    /// Formatted at exactly <see cref="AiAdaptationOutputValidator.QuantityComparisonScale"/>, so the string the
    /// model is shown and the string the validator compares an answer against can never round differently.
    /// </remarks>
    private static DeterministicScalingSummary Summarize(RecipeScalingPreview preview) => new(
        Factor: preview.Factor.ToDecimal(AiAdaptationOutputValidator.QuantityComparisonScale).ToString(CultureInfo.InvariantCulture),
        ScaledYieldQuantity: Format(preview.ScaledYieldQuantity),
        Lines: [.. preview.Lines.Select(line => new DeterministicScalingLine(
            line.Id,
            line.DisplayText,
            line.WasScaled,
            Format(line.ScaledQuantity),
            Format(line.ScaledQuantityUpper)))]);

    private static string? Format(Quantity? value) =>
        value?.ToDecimal(AiAdaptationOutputValidator.QuantityComparisonScale, MidpointRounding.ToEven)
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Review flags from the deterministic calculation itself — never from the model — so a line the creator
    /// marked fixed, or a factor extreme enough to need a second look, reaches the proposal the same way any
    /// other server-computed fact does.
    /// </summary>
    private static IEnumerable<AiOutputWarning> ScalingWarnings(RecipeScalingPreview preview)
    {
        foreach (var code in preview.RecipeWarnings.Distinct())
        {
            yield return new AiOutputWarning
            {
                Kind = AiWarningKind.CulinaryCaution,
                Message = Truncate(Describe(code)),
                ChangeIndex = null,
            };
        }

        foreach (var line in preview.Lines)
        {
            foreach (var code in line.Warnings.Distinct())
            {
                yield return new AiOutputWarning
                {
                    Kind = AiWarningKind.NonScalableLanguage,
                    Message = Truncate($"{Describe(code)} ({line.DisplayText})"),
                    ChangeIndex = null,
                };
            }
        }
    }

    private static string Describe(RecipeScalingWarningCode code) => code switch
    {
        RecipeScalingWarningCode.FixedQuantityNotScaled => "This line is fixed and was not scaled.",
        RecipeScalingWarningCode.ReviewRequiredQualitative => "This line has no quantity to scale.",
        RecipeScalingWarningCode.ReviewRequiredDiscreteCount => "This line is a discrete count and was not scaled.",
        RecipeScalingWarningCode.ReviewRequiredRange => "This line carries a range and needs review after scaling.",
        RecipeScalingWarningCode.ReviewRequiredOther => "This line is flagged for review and was not scaled.",
        RecipeScalingWarningCode.NoQuantityToScale => "This line has no quantity recorded to scale.",
        RecipeScalingWarningCode.ScaledToNegligibleDisplay => "The scaled quantity rounds to a negligible display value.",
        RecipeScalingWarningCode.RecipeYieldNotStructured => "The recipe's yield is not a structured number, so a scaled yield cannot be shown.",
        RecipeScalingWarningCode.ExtremeScaleFactor => "This is an extreme scale factor; leavening, pan size and cook time may not behave linearly.",
        _ => "This line needs review after scaling.",
    };

    private static string Truncate(string value) =>
        value.Length <= AiPolicy.MessageMaxLength ? value : value[..AiPolicy.MessageMaxLength];

    private sealed record DeterministicScalingLine(
        Guid Id, string DisplayText, bool WasScaled, string? Quantity, string? QuantityUpper);

    private sealed record DeterministicScalingSummary(
        string Factor, string? ScaledYieldQuantity, IReadOnlyList<DeterministicScalingLine> Lines);
}
