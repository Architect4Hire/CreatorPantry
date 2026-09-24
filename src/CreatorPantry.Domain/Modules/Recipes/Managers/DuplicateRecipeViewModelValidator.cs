using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape and format validation for <see cref="DuplicateRecipeViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// The title rules are word for word the create route's, because the copy is a recipe and a recipe must keep
/// a title. What is deliberately absent is any check that the source version <em>exists</em>: that needs a
/// lookup, so it belongs below, where a refusal can say which recipe and which version it looked for.
/// </para>
/// <para>
/// Nothing here trusts the binder: the title is treated as nullable and trimmed before it is measured.
/// </para>
/// </remarks>
public sealed class DuplicateRecipeViewModelValidator : AbstractValidator<DuplicateRecipeViewModel>
{
    public DuplicateRecipeViewModelValidator()
    {
        RuleFor(model => (model.Title ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Give the copy a title.")
            .MaximumLength(RecipePolicy.TitleMaxLength)
                .WithMessage($"A title can be at most {RecipePolicy.TitleMaxLength} characters.")
            .OverridePropertyName(nameof(DuplicateRecipeViewModel.Title));

        // Only when one was sent: omitting it means "as it currently stands" and is the common case. The
        // floor matches the check constraint on RecipeVersions.VersionNumber, so a number no version could
        // carry is a field error naming the parameter rather than a lookup that finds nothing.
        RuleFor(model => model.SourceVersionNumber)
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.")
            .When(model => model.SourceVersionNumber is not null)
            .OverridePropertyName(nameof(DuplicateRecipeViewModel.SourceVersionNumber));
    }
}
