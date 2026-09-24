using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape and format validation for <see cref="RestoreRecipeVersionViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Short, because the body is: a restore submits no content, so there is no length, range or pairing rule
/// for this layer to check. What is not here is the version number — it arrives as a route segment with an
/// <c>:int:min(1)</c> constraint, so a number no version could carry never reaches an action, and whether
/// this recipe <em>has</em> the version asked for needs a lookup and belongs below.
/// </para>
/// <para>
/// Nothing here trusts the binder: the reason is treated as nullable and trimmed before it is measured.
/// </para>
/// </remarks>
public sealed class RestoreRecipeVersionViewModelValidator : AbstractValidator<RestoreRecipeVersionViewModel>
{
    public RestoreRecipeVersionViewModelValidator()
    {
        // Word for word the rule UpdateRecipeViewModelValidator states, and deliberately not shared with it:
        // the two view models have no common base, and a rule extracted to a helper would hide the fact that
        // both routes require the same token for the same reason. A token that could never have been issued is
        // a client bug, reported as one rather than as a conflict that sends a creator looking for a
        // collaborator who does not exist.
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the recipe's concurrency token with your restore.")
            .Must(RecipeConcurrencyToken.IsWellFormed)
                .WithMessage("That is not a concurrency token this API issued.")
            .OverridePropertyName(nameof(RestoreRecipeVersionViewModel.ExpectedConcurrencyToken));

        RuleFor(model => (model.Reason ?? string.Empty).Trim())
            .MaximumLength(RecipePolicy.NoteMaxLength)
            .OverridePropertyName(nameof(RestoreRecipeVersionViewModel.Reason));
    }
}
