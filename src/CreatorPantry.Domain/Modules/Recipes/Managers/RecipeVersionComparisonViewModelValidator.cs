using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape validation for <see cref="RecipeVersionComparisonViewModel"/>: both parameters are required, and a
/// version number is at least 1.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Whether the numbers name versions that exist is not a question a validator can ask.</strong> It has
/// no idea which workspace was resolved or which recipe the route named, and a lookup from here would be a
/// query outside the seam. Business settles it, against the recipe the route actually resolved, and answers
/// <see cref="RecipeErrorCodes.VersionNotFound"/>.
/// </para>
/// <para>
/// <strong><c>from</c> equal to <c>to</c> is accepted.</strong> It is a wasteful request and a truthful one:
/// the answer is a comparison with no changes in it. Refusing it would make a client that preselects the same
/// version on both sides handle an error where rendering "no changes" is correct.
/// </para>
/// <para>
/// <strong><c>from</c> greater than <c>to</c> is accepted too.</strong> Reading a newer version as the
/// left-hand side is what a creator weighing a revert is doing, and the response names which version is which,
/// so nothing is ambiguous about the answer.
/// </para>
/// <para>
/// The 1 floor matches the check constraint on <c>RecipeVersions.VersionNumber</c>, so a number no version
/// could ever carry is refused at the edge rather than looked up and missed. The property names are overridden
/// to the query parameters the caller actually sent.
/// </para>
/// </remarks>
public sealed class RecipeVersionComparisonViewModelValidator : AbstractValidator<RecipeVersionComparisonViewModel>
{
    public RecipeVersionComparisonViewModelValidator()
    {
        RuleFor(model => model.From)
            .NotNull()
            .WithMessage("Name the version to compare from.")
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version numbers start at 1.")
            .OverridePropertyName("from");

        RuleFor(model => model.To)
            .NotNull()
            .WithMessage("Name the version to compare to.")
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version numbers start at 1.")
            .OverridePropertyName("to");
    }
}
