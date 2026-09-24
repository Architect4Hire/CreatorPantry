using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>POST .../recipes/{recipeId}/archive</c> and <c>POST .../recipes/{recipeId}/unarchive</c>:
/// a creator shelving a recipe, or taking it back off the shelf.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One type for both commands</strong>, because both carry the same one field and neither has
/// anything of its own to say. Two identical records would be two places to forget a rule.
/// </para>
/// <para>
/// <strong>No reason, unlike an edit.</strong> An edit's reason is recorded on the version it writes, where
/// it sits beside the content it explains. These commands write no version, and their trace is an audit
/// entry — whose summary is required to stay safe to display, which creator free text is not something this
/// seam can promise. What the audit records instead is who moved the recipe, when, and between which states.
/// </para>
/// <para>
/// <strong>No target state either.</strong> The route says which way the recipe is going, so a body that
/// also said it would be a second source of truth and a way for the two to disagree.
/// </para>
/// </remarks>
public sealed record RecipeLifecycleViewModel
{
    /// <summary>
    /// The <c>concurrencyToken</c> from the recipe as the creator last saw it. Required.
    /// </summary>
    /// <remarks>
    /// The same value and the same meaning as on an edit — round-tripped from
    /// <see cref="RecipeDetailServiceModel.ConcurrencyToken"/>, opaque, stored and sent back rather than
    /// parsed. Required here even though the commands are idempotent, and for a reason idempotency does not
    /// cover: archiving a recipe someone else is actively editing should tell the archiver that the recipe
    /// moved under them, not silently shelve work they have not seen.
    /// </remarks>
    public string? ExpectedConcurrencyToken { get; init; }
}

/// <summary>
/// Shape validation for <see cref="RecipeLifecycleViewModel"/> — the token, and nothing else to check.
/// </summary>
public sealed class RecipeLifecycleViewModelValidator : AbstractValidator<RecipeLifecycleViewModel>
{
    public RecipeLifecycleViewModelValidator() =>
        // Word for word the rule the edit and restore validators state, and not shared with them for the
        // reason those two are not shared with each other: the view models have no common base, and hiding
        // the rule in a helper would obscure that all three routes require the same token for the same
        // reason. A token that could never have been issued is a client bug, reported as one rather than as
        // a conflict that sends a creator looking for a collaborator who was never there.
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the recipe's concurrency token with your request.")
            .Must(RecipeConcurrencyToken.IsWellFormed)
                .WithMessage("That is not a concurrency token this API issued.")
            .OverridePropertyName(nameof(RecipeLifecycleViewModel.ExpectedConcurrencyToken));
}
