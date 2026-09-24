using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape validation for <see cref="RecipeVersionHistoryViewModel"/>: one rule, because there is one thing a
/// validator can settle here.
/// </summary>
/// <remarks>
/// <para>
/// The <c>cursor</c> rule keeps <see cref="ReferenceCursor.TryDecode"/>'s structural check at the edge, so a
/// cursor that is not even base64 is refused before any workspace or recipe is resolved. Whether a well-formed
/// cursor belongs to <em>this</em> recipe's history is a different question and not one a validator can ask: it
/// has no idea which workspace was resolved or which recipe the route named.
/// <see cref="RecipeVersionHistoryQueryFactory"/> owns that half, exactly as on the search route.
/// </para>
/// <para>
/// <strong><c>limit</c> is not validated</strong>, matching every other paged route. An out-of-range page size
/// is clamped rather than refused, so a client cannot fail a read by asking for too much; validating it would
/// turn a clamp into a rejection.
/// </para>
/// </remarks>
public sealed class RecipeVersionHistoryViewModelValidator : AbstractValidator<RecipeVersionHistoryViewModel>
{
    public RecipeVersionHistoryViewModelValidator()
    {
        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");
    }
}
