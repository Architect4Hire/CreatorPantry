using System.Globalization;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The two checks AIREC-005 adds on top of <see cref="AiOutputValidator"/>, over the same
/// <see cref="AiOutputDocument"/> shape a recipe revision uses.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A wrapper, not a parallel five-stage validator.</strong> AIREC-004 wrote its own envelope, schema,
/// shape and hygiene stages because it validates a document <see cref="AiOutputValidator"/> has never heard of.
/// AIREC-005 reuses the same <see cref="AiOutputDocument"/> a revision produces, so every one of those stages —
/// and the scope check <see cref="AiOutputValidator"/> already runs against
/// <see cref="AiOperationScope.WholeRecipe"/> — applies unchanged. What this adds are the two rules that are
/// specific to a declared goal: deterministic yield math, and an honest limitation in place of a fabricated
/// success.
/// </para>
/// <para>
/// <strong>Deterministic yield math is enforced here, not merely described in the prompt.</strong> The handler
/// computes every scaled ingredient quantity through <c>RecipeScalingCalculator</c> before the model is called,
/// and hands the result to the model as authoritative context. This stage checks the model actually used it: a
/// <see cref="AiChangeKind.Set"/> on an ingredient's <c>quantity</c> or <c>quantityUpper</c> must equal the
/// deterministic figure for that line, or the whole answer is rejected — never silently corrected, matching
/// <see cref="AiOutputValidator"/>'s own "nothing is repaired" guarantee.
/// </para>
/// </remarks>
public static class AiAdaptationOutputValidator
{
    /// <summary>
    /// The decimal places an ingredient quantity is compared at.
    /// </summary>
    /// <remarks>
    /// Matches the column's own precision, per <see cref="RecipeScalingPolicy.MaxFactor"/>'s remarks. Public so
    /// <see cref="RecipeAdaptationAiTaskHandler"/> can format the same deterministic figures it hands to the
    /// model at the exact precision this stage will hold them to — the two must never round differently.
    /// </remarks>
    public const int QuantityComparisonScale = 12;

    /// <summary>
    /// Validates one model answer: <see cref="AiOutputValidator.Validate"/>'s stages, then this capability's
    /// own two.
    /// </summary>
    /// <param name="payload">The provider's raw response text.</param>
    /// <param name="expectedSchemaVersion">The template's declared output schema version.</param>
    /// <param name="scope">The operation's scope — always <see cref="AiOperationScope.WholeRecipe"/> for this task.</param>
    /// <param name="goal">The one goal this operation declared.</param>
    /// <param name="deterministicScaling">
    /// The scaling already computed for a <see cref="AiAdaptationGoal.Yield"/> goal, or <see langword="null"/>
    /// for every other goal.
    /// </param>
    public static AiOutputValidationOutcome<AiOutputDocument> Validate(
        string? payload,
        string expectedSchemaVersion,
        AiOperationScope scope,
        AiAdaptationGoal goal,
        RecipeScalingPreview? deterministicScaling)
    {
        var baseResult = AiOutputValidator.Validate(payload, expectedSchemaVersion, scope);

        if (!baseResult.Succeeded)
        {
            return AiOutputValidationOutcome<AiOutputDocument>.Failed(baseResult.Failure!);
        }

        var document = baseResult.Document!;

        // The base validator this sits on refuses a safety assurance in any proposed value or warning
        // (AiOutputValidator.NoSafetyAssurance), so the blocker's prose half is already closed by the time
        // these goal-specific rules run.
        return TooManyWarnings(document)
            ?? YieldMathIsDeterministic(document, goal, deterministicScaling)
            ?? RecipeYieldIsDeterministic(document, goal, deterministicScaling)
            ?? CautionedDietaryAdaptation(document, goal)
            ?? AnswerIsExplained(document)
            ?? AiOutputValidationOutcome<AiOutputDocument>.Success(document);
    }

    /// <remarks>
    /// The reason <c>MaxRecipeDraftWarnings</c> and <c>MaxSubstitutionWarnings</c> exist: without a cap, a
    /// genuinely important <see cref="AiWarningKind.Limitation"/> or <see cref="AiWarningKind.SafetyCaution"/>
    /// could be diluted among an unbounded number of low-value assumptions.
    /// </remarks>
    private static AiOutputValidationOutcome<AiOutputDocument>? TooManyWarnings(AiOutputDocument document) =>
        document.Warnings.Count > AiPolicy.MaxAdaptationWarnings
            ? Reject(
                AiOutputReason.ValueTooLong,
                $"The response carries more than {AiPolicy.MaxAdaptationWarnings} warnings.",
                correctable: true)
            : null;

    private static AiOutputValidationOutcome<AiOutputDocument>? YieldMathIsDeterministic(
        AiOutputDocument document, AiAdaptationGoal goal, RecipeScalingPreview? deterministicScaling)
    {
        if (goal is not AiAdaptationGoal.Yield || deterministicScaling is null)
        {
            return null;
        }

        foreach (var change in document.Changes)
        {
            if (change.ChangeKind is not AiChangeKind.Set
                || change.TargetKind is not AiChangeTargetKind.Ingredient
                || change.FieldName is not ("quantity" or "quantityUpper"))
            {
                continue;
            }

            var line = deterministicScaling.Lines.FirstOrDefault(candidate => candidate.Id == change.TargetId);

            // Not a line the deterministic preview knows about. AiDiffCalculator refuses an id absent from the
            // pinned snapshot on its own account; this stage is only responsible for the arithmetic.
            if (line is null)
            {
                continue;
            }

            var expected = change.FieldName == "quantity" ? line.ScaledQuantity : line.ScaledQuantityUpper;

            if (!MatchesDeterministicValue(change.AfterValue, expected))
            {
                return Reject(
                    AiOutputReason.YieldMathNotDeterministic,
                    "A yield-goal answer set an ingredient quantity that does not match the deterministic "
                        + "scaling already computed for it. Use the figures given rather than computing new "
                        + "ones.",
                    correctable: false);
            }
        }

        return null;
    }

    /// <summary>
    /// A yield-goal answer may not set the recipe's own yield or servings to a figure of its own (14.7, H-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="YieldMathIsDeterministic"/> checked ingredient quantities and nothing else, so the three
    /// fields the goal is actually *about* — <c>yieldQuantity</c>, <c>servingCount</c> and <c>servingSize</c> —
    /// were the model's to choose. That matters more than a line quantity: a servings figure becomes the
    /// divisor for every per-serving nutrition estimate, so a model-chosen number would silently set what a
    /// creator publishes as "per serving".
    /// </para>
    /// <para>
    /// <c>yieldQuantity</c> must be the deterministic scaled yield where there is one. <c>servingCount</c> and
    /// <c>servingSize</c> are refused outright under this goal: the deterministic preview computes no scaled
    /// servings, so there is no figure to check against, and ai.md puts yield arithmetic in domain code. A
    /// creator who wants the servings changed edits them, or scales the recipe and keeps the result.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiOutputDocument>? RecipeYieldIsDeterministic(
        AiOutputDocument document, AiAdaptationGoal goal, RecipeScalingPreview? deterministicScaling)
    {
        if (goal is not AiAdaptationGoal.Yield || deterministicScaling is null)
        {
            return null;
        }

        foreach (var change in document.Changes)
        {
            if (change.ChangeKind is not AiChangeKind.Set || change.TargetKind is not AiChangeTargetKind.Recipe)
            {
                continue;
            }

            if (change.FieldName is "servingCount" or "servingSize")
            {
                return Reject(
                    AiOutputReason.YieldMathNotDeterministic,
                    $"A yield-goal answer set '{change.FieldName}', which is arithmetic this product does "
                        + "deterministically rather than a value to propose.",
                    correctable: false);
            }

            if (change.FieldName is not "yieldQuantity")
            {
                continue;
            }

            if (!MatchesDeterministicValue(change.AfterValue, deterministicScaling.ScaledYieldQuantity))
            {
                return Reject(
                    AiOutputReason.YieldMathNotDeterministic,
                    "A yield-goal answer set a recipe yield that does not match the deterministic scaling "
                        + "already computed for it. Use the figure given rather than computing a new one.",
                    correctable: false);
            }
        }

        return null;
    }

    /// <summary>
    /// A dietary adaptation that changes what is in the recipe arrives with a safety caution, or it does not
    /// arrive (14.7, H-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule <see cref="AiSubstitutionOutputValidator"/> has enforced all along, finally enforced here. The
    /// adaptation prompt declares <c>safetyClass: DietaryOrAllergen</c> and asks for a caution beside any
    /// change touching preservation, temperature, allergens or dietary suitability; no validator checked, so
    /// the whole rule rested on the model's goodwill. A dietary goal is precisely the one that swaps an
    /// ingredient for another a reader may react to.
    /// </para>
    /// <para>
    /// Required once for the answer rather than per change: unlike a substitution, an adaptation's changes are
    /// one coherent edit to one recipe, and a caution on each of eleven rows is noise that buries itself. What
    /// is refused is an answer that changes ingredients or method under a dietary goal and carries no caution
    /// at all.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiOutputDocument>? CautionedDietaryAdaptation(
        AiOutputDocument document, AiAdaptationGoal goal)
    {
        if (goal is not AiAdaptationGoal.Dietary)
        {
            return null;
        }

        var changesContent = document.Changes.Any(change =>
            change.TargetKind is AiChangeTargetKind.Ingredient
                or AiChangeTargetKind.InstructionStep
                or AiChangeTargetKind.Recipe);

        if (!changesContent)
        {
            return null;
        }

        return document.Warnings.Any(warning => warning.Kind is AiWarningKind.SafetyCaution)
            ? null
            : Reject(
                AiOutputReason.AllergenEffectUncautioned,
                "A dietary adaptation changed the recipe with no safety caution beside it. A dietary change "
                    + "is an allergen question for whoever is eating, and it may not arrive without one.",
                correctable: true);
    }

    /// <summary>
    /// Whether a proposed decimal string is the deterministic figure, at the precision the recipe stores.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> expected means the deterministic calculation found nothing to scale on this line
    /// — no quantity at all, or a line the creator marked fixed or review-required. A model proposing a number
    /// there has invented one, exactly as much as a model disagreeing with a real figure has.
    /// </remarks>
    private static bool MatchesDeterministicValue(string? afterValue, Quantity? expected)
    {
        if (expected is null)
        {
            return false;
        }

        if (afterValue is null
            || !decimal.TryParse(afterValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var proposed))
        {
            return false;
        }

        var canonical = expected.Value.ToDecimal(QuantityComparisonScale, MidpointRounding.ToEven);

        return Math.Round(proposed, QuantityComparisonScale, MidpointRounding.ToEven) == canonical;
    }

    /// <remarks>
    /// Proposing nothing toward the declared goal is allowed and is sometimes the only honest answer — the
    /// goal was impossible, or unsafe to pursue as stated. Proposing nothing in silence is not: a creator
    /// cannot tell it from a call that went wrong, and <see cref="AiWarningKind.Limitation"/> exists precisely
    /// so they do not have to.
    /// </remarks>
    private static AiOutputValidationOutcome<AiOutputDocument>? AnswerIsExplained(AiOutputDocument document)
    {
        if (document.Changes.Count > 0)
        {
            return null;
        }

        var explained = document.Warnings.Any(warning =>
            warning.Kind is AiWarningKind.Limitation && warning.ChangeIndex is null);

        return explained
            ? null
            : Reject(
                AiOutputReason.AdaptationAnswerUnexplained,
                "The response proposed no change toward the declared goal and did not record a limitation "
                    + "explaining why.",
                correctable: true);
    }

    private static AiOutputValidationOutcome<AiOutputDocument> Reject(
        string reasonCode, string message, bool correctable) =>
        AiOutputValidationOutcome<AiOutputDocument>.Failed(
            new AiOutputFailure(AiFailureCategory.DomainInvalid, reasonCode, Truncate(message), correctable));

    private static string Truncate(string value) =>
        value.Length <= AiPolicy.DiagnosticMaxLength ? value : value[..AiPolicy.DiagnosticMaxLength];
}
