using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>POST .../recipes/{recipeId}/readiness-transitions</c>: one move through the editorial
/// machine (TESTRUN-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No readiness evaluation field, and that is the point.</strong> The approval is gated on a fresh
/// evaluation with no blockers, and the server makes that evaluation itself when the target needs one. A body
/// that carried a verdict would be a client asserting its own recipe was ready, which is the one thing a gate
/// must not accept.
/// </para>
/// <para>
/// <strong>No <c>fromStatus</c> either.</strong> The recipe knows where it is, and a body that also said would
/// be a second source of truth and a way for the two to disagree. A caller who believes the recipe is somewhere
/// else finds out through <see cref="ExpectedConcurrencyToken"/>.
/// </para>
/// </remarks>
public sealed record RecipeReadinessTransitionViewModel
{
    /// <summary>The state to move the recipe to. Required.</summary>
    /// <remarks>
    /// Which targets are reachable depends on where the recipe currently is, which this type cannot know; see
    /// <see cref="RecipeTransitionTargetViewModel"/>. An unreachable one is refused by Business with the
    /// states the recipe could have gone to instead.
    /// </remarks>
    [Description("The editorial state to move the recipe to. Which targets are legal depends on the state the recipe is in now.")]
    public RecipeTransitionTargetViewModel? TargetStatus { get; init; }

    /// <summary>
    /// Why, in the caller's own words. Required when reopening a recipe and optional on every other move.
    /// </summary>
    /// <remarks>
    /// The requirement is data-dependent — whether this move is a reopen depends on where the recipe is now —
    /// so the validator only bounds the length and Business refuses a reopen without one. Stored on the
    /// transition as creator text, and on an approval it travels onto the version the approval writes.
    /// </remarks>
    [Description("Why the recipe is being moved. Required when reopening a recipe; optional otherwise.")]
    public string? Reason { get; init; }

    /// <summary>
    /// The <c>concurrencyToken</c> from the recipe as the caller last saw it. Required.
    /// </summary>
    /// <remarks>
    /// The same value and the same meaning as on an edit — round-tripped from
    /// <see cref="RecipeDetailServiceModel.ConcurrencyToken"/>, or from the readiness evaluation, which
    /// publishes the same token for exactly this. Required even though the command is idempotent, and for a
    /// reason idempotency does not cover: approving or shelving a recipe somebody else is actively editing
    /// should tell the caller that it moved under them, not act on work they have not seen.
    /// </remarks>
    [Description("The recipe's concurrencyToken as the caller last saw it. A token the recipe has moved past is refused as a conflict.")]
    public string? ExpectedConcurrencyToken { get; init; }
}

/// <summary>
/// Shape validation for <see cref="RecipeReadinessTransitionViewModel"/>.
/// </summary>
/// <remarks>
/// Shape only, and the line is worth stating because most of what makes a transition legal is not shape: which
/// move exists, which role it needs, whether it needs a reason and whether readiness is clear are all facts
/// about the recipe, and backend.md keeps those in Business. What is left here is that the target is a value
/// the enum has, the token could have been issued, and the reason fits the column.
/// </remarks>
public sealed class RecipeReadinessTransitionViewModelValidator : AbstractValidator<RecipeReadinessTransitionViewModel>
{
    public RecipeReadinessTransitionViewModelValidator()
    {
        RuleFor(model => model.TargetStatus)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Say which state to move the recipe to.")

            // Enum.IsDefined rather than a cast, for the reason CreateRecipeViewModelValidator records: a cast
            // accepts 99 happily, and an undefined target would reach the machine as a state nothing can move
            // to and be refused as an invalid jump — a confusing answer to a malformed request.
            .Must(target => Enum.IsDefined(target!.Value))
                .WithMessage("That is not a state a recipe can be moved to.");

        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the recipe's concurrency token with your request.")
            .Must(RecipeConcurrencyToken.IsWellFormed)
                .WithMessage("That is not a concurrency token this API issued.");

        // Bounded, never required here: a reopen needs one and nothing else does, and which this is depends on
        // the recipe. Business refuses a reopen with no reason, naming the field.
        RuleFor(model => model.Reason)
            .MaximumLength(RecipePolicy.TransitionReasonMaxLength)
            .When(model => model.Reason is not null);
    }
}
