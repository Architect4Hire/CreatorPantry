using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape and format validation for <see cref="RecipeSearchViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Edge validation only, and deliberately thin: almost everything about a recipe search is either a value this
/// module must parse into a typed filter — which <see cref="RecipeSearchQueryFactory"/> does, because a
/// validator that only said "that is not a status" would leave the factory parsing it again — or a value the
/// binder has already vouched for. What is left is the three things a validator can settle on its own.
/// </para>
/// <para>
/// <strong><c>limit</c> is not validated</strong>, matching every other paged route. An out-of-range page size
/// is clamped rather than refused, so that a client cannot fail a read by asking for too much; validating it
/// would turn a clamp into a rejection.
/// </para>
/// <para>
/// The <c>cursor</c> rule keeps <see cref="CreatorPantry.Domain.Managers.Paging.ReferenceCursor.TryDecode"/>'s structural check here so
/// that a cursor which is not even base64 is answered before any filter is parsed. Whether a well-formed cursor
/// belongs to <em>this</em> query is a different question, and one a validator cannot ask: it has no idea which
/// workspace was resolved or which filters arrived. The factory owns that half.
/// </para>
/// </remarks>
public sealed class RecipeSearchViewModelValidator : AbstractValidator<RecipeSearchViewModel>
{
    public RecipeSearchViewModelValidator()
    {
        RuleFor(model => model.Search)
            .MaximumLength(RecipeSearchPolicy.SearchMaxLength)
            .WithMessage($"Search terms are limited to {RecipeSearchPolicy.SearchMaxLength} characters.")
            .OverridePropertyName("search");

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");

        // An inverted range matches nothing, which is a legal answer but almost never the intended one — and
        // "no recipes" is an answer a creator cannot debug. Refusing it names the mistake instead.
        RuleFor(model => model.UpdatedBefore)
            .Must((model, before) => before > model.UpdatedFrom)
            .WithMessage("updatedBefore must be later than updatedFrom.")
            .When(model => model.UpdatedFrom is not null && model.UpdatedBefore is not null)
            .OverridePropertyName("updatedBefore");

        RuleFor(model => model.CreatedBefore)
            .Must((model, before) => before > model.CreatedFrom)
            .WithMessage("createdBefore must be later than createdFrom.")
            .When(model => model.CreatedFrom is not null && model.CreatedBefore is not null)
            .OverridePropertyName("createdBefore");
    }
}
