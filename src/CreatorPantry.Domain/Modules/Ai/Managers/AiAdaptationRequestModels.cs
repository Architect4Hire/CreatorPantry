using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The one adaptation goal a AIREC-005 operation may declare. Exactly one per operation — see
/// <see cref="RequestRecipeAdaptationViewModel"/>.
/// </summary>
public enum AiAdaptationGoal
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>Adapt toward a diet or restriction the creator names in <see cref="RequestRecipeAdaptationViewModel.GoalDetail"/>.</summary>
    Dietary = 1,

    /// <summary>Adapt toward equipment the creator has, or does not have, named the same way.</summary>
    Equipment = 2,

    /// <summary>
    /// Adapt the batch size. The only goal whose target is a number rather than free text — see
    /// <see cref="RequestRecipeAdaptationViewModel.TargetMultiplier"/> and
    /// <see cref="RequestRecipeAdaptationViewModel.TargetYieldQuantity"/> — and the only one whose ingredient
    /// quantities are computed deterministically rather than proposed by the model (ai.md).
    /// </summary>
    Yield = 3,

    /// <summary>Adapt toward the cook's skill level or the technique they have access to.</summary>
    SkillLevel = 4,
}

/// <summary>
/// AIREC-005's request: which pinned version to adapt, the one goal to adapt it toward, and what that goal
/// means for this recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its own contract rather than the generic proposal request's</strong>, for the reason every other
/// recipe-bound capability in this module took one: a goal and its detail have nowhere to travel through
/// <c>RequestAiProposalViewModel</c>, which carries a task, a scope and a source version and stores no task
/// inputs.
/// </para>
/// <para>
/// <strong>There is no scope field.</strong> A complete cross-field proposal cannot fit inside
/// <see cref="AiOperationScope.Ingredients"/>, <see cref="AiOperationScope.Instructions"/> or
/// <see cref="AiOperationScope.Metadata"/> alone — it needs all of them — so the operation always runs in
/// <see cref="AiOperationScope.WholeRecipe"/>, fixed server-side, the same way AIREC-004 fixes
/// <see cref="AiOperationScope.Advisory"/> rather than accepting a scope from the client.
/// </para>
/// </remarks>
public sealed class RequestRecipeAdaptationViewModel
{
    /// <summary>
    /// The exact version to adapt, which must be the recipe's current version.
    /// </summary>
    /// <remarks>Required, for the reason every other pinned request in this module gives; see recipes.md.</remarks>
    public Guid SourceVersionId { get; set; }

    /// <summary>The one goal this operation adapts toward. Required.</summary>
    public AiAdaptationGoal Goal { get; set; }

    /// <summary>
    /// What the goal means for this recipe, in the creator's own words: "gluten-free", "no stand mixer", "a
    /// nervous first-timer". Required for every goal but <see cref="AiAdaptationGoal.Yield"/>, where the target
    /// numbers carry the instruction and a detail is only ever additional context.
    /// </summary>
    /// <remarks>
    /// Untrusted prompt content throughout, exactly like AIREC-003's goal and AIREC-004's reason: it travels in
    /// a PREFERENCES segment at creator-data trust, never in the task's own instructions.
    /// </remarks>
    public string? GoalDetail { get; set; }

    /// <summary>
    /// The factor to scale by. Meaningful only when <see cref="Goal"/> is <see cref="AiAdaptationGoal.Yield"/>,
    /// and exactly one of this or <see cref="TargetYieldQuantity"/> is named then.
    /// </summary>
    public decimal? TargetMultiplier { get; set; }

    /// <summary>The yield to scale toward, as the alternative to <see cref="TargetMultiplier"/>.</summary>
    public decimal? TargetYieldQuantity { get; set; }
}

public sealed class RequestRecipeAdaptationViewModelValidator : AbstractValidator<RequestRecipeAdaptationViewModel>
{
    public RequestRecipeAdaptationViewModelValidator()
    {
        RuleFor(model => model.SourceVersionId)
            .NotEmpty().WithMessage("Name the recipe version to adapt.");

        RuleFor(model => model.Goal)
            .NotEqual(AiAdaptationGoal.Unspecified).WithMessage("Say which goal this adaptation is for.")
            .IsInEnum().WithMessage("That is not a goal this adaptation can be declared for.");

        RuleFor(model => model.GoalDetail).MaximumLength(AiPolicy.AdaptationGoalDetailMaxLength);

        // Every goal but yield names its target in words — there is nothing else to adapt toward. Yield names
        // its target as a number instead; a detail there is optional context, never the instruction itself.
        RuleFor(model => model.GoalDetail)
            .Must(detail => !string.IsNullOrWhiteSpace(detail))
            .WithMessage("Say what the goal means for this recipe.")
            .When(model => model.Goal is AiAdaptationGoal.Dietary
                or AiAdaptationGoal.Equipment
                or AiAdaptationGoal.SkillLevel);

        RuleFor(model => model)
            .Must(model => (model.TargetMultiplier is not null) ^ (model.TargetYieldQuantity is not null))
            .WithMessage("Name exactly one of a multiplier or a target yield.")
            .When(model => model.Goal is AiAdaptationGoal.Yield)
            .OverridePropertyName(nameof(RequestRecipeAdaptationViewModel.TargetMultiplier));

        RuleFor(model => model.TargetMultiplier)
            .GreaterThan(0m).WithMessage("The multiplier must be greater than zero.")
            .When(model => model.TargetMultiplier is not null);

        RuleFor(model => model.TargetYieldQuantity)
            .GreaterThan(0m).WithMessage("The target yield must be greater than zero.")
            .When(model => model.TargetYieldQuantity is not null);

        RuleFor(model => model.TargetMultiplier)
            .Must(value => value is null)
            .WithMessage("A multiplier only applies to a yield goal.")
            .When(model => model.Goal is not AiAdaptationGoal.Yield);

        RuleFor(model => model.TargetYieldQuantity)
            .Must(value => value is null)
            .WithMessage("A target yield only applies to a yield goal.")
            .When(model => model.Goal is not AiAdaptationGoal.Yield);
    }
}

/// <summary>Stable error codes this request seam introduces. Renaming one is a breaking API change.</summary>
public static class AiAdaptationRequestErrors
{
    /// <summary>The request failed shape validation.</summary>
    public const string RequestInvalid = "ai.recipeAdaptation.invalid_request";

    /// <summary>AIREC-005 is a real task, switched off for this deployment.</summary>
    public const string TaskNotEnabled = "ai.recipeAdaptation.not_enabled";

    /// <summary>Unknown recipe, or one in another workspace: deliberately indistinguishable (tenancy.md).</summary>
    public const string RecipeNotFound = "ai.recipeAdaptation.recipe.not_found";

    /// <summary>The named version is not the recipe's current version, so a diff from it would start stale.</summary>
    public const string SourceVersionInvalid = "ai.recipeAdaptationSource.invalid_request";

    /// <summary>
    /// A yield-goal request's multiplier or target yield could not be resolved to a scaling factor — a
    /// non-positive value, or a target yield against a recipe whose own yield is not a structured number.
    /// </summary>
    public const string YieldTargetInvalid = "ai.recipeAdaptationYieldTarget.invalid_request";

    /// <summary>No such adaptation request in this workspace, or one belonging to another task.</summary>
    public const string RequestNotFound = "ai.recipeAdaptationRequest.not_found";
}

/// <summary>The keys AIREC-005's request writes into <c>AiOperation.TaskInputsJson</c>.</summary>
/// <remarks>
/// The same agreement <see cref="AiRevisionInputs"/> and <see cref="AiSubstitutionInputs"/> record, for the
/// same reason: the request seam writes this and the handler reads it back in another process, with no type
/// between them.
/// </remarks>
public static class AiAdaptationInputs
{
    /// <summary>The declared goal, as <see cref="AiAdaptationGoal"/>'s member name.</summary>
    public const string Goal = "goal";

    /// <summary>What the goal means for this recipe, in the creator's own words.</summary>
    public const string GoalDetail = "goalDetail";

    /// <summary>The yield goal's multiplier, when it named one.</summary>
    public const string TargetMultiplier = "targetMultiplier";

    /// <summary>The yield goal's target yield, when it named one.</summary>
    public const string TargetYieldQuantity = "targetYieldQuantity";
}
