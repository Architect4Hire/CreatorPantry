using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Shape and format validation for <see cref="PromptSearchViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose, matching <see cref="Recipes.Managers.RecipeSearchViewModelValidator"/>. Two of the five
/// parameters are values the binder has already vouched for, and <c>channel</c> is deliberately not checked
/// against the catalogue — see <see cref="PromptSearchPolicy.NormalizeChannel"/> for why a read must keep
/// answering for keys the catalogue has moved on from.
/// </para>
/// <para>
/// <strong><c>limit</c> is not validated</strong>, matching every other paged route: an out-of-range page size
/// is clamped rather than refused, so a client cannot fail a read by asking for too much. Validating it would
/// turn a clamp into a rejection.
/// </para>
/// <para>
/// The <c>cursor</c> rule keeps <see cref="ReferenceCursor.TryDecode"/>'s structural check here, so a cursor
/// that is not even base64 is answered before any filter is parsed. Whether a well-formed cursor belongs to
/// <em>this</em> query is a different question and one a validator cannot ask — it has no idea which workspace
/// was resolved. <see cref="PromptSearchQueryFactory"/> owns that half.
/// </para>
/// </remarks>
public sealed class PromptSearchViewModelValidator : AbstractValidator<PromptSearchViewModel>
{
    public PromptSearchViewModelValidator()
    {
        RuleFor(model => model.Search)
            .MaximumLength(PromptSearchPolicy.SearchMaxLength)
            .WithMessage($"Search terms are limited to {PromptSearchPolicy.SearchMaxLength} characters.")
            .OverridePropertyName("search");

        // Bounded so a pathological query string cannot become a pathological equality predicate. The catalogue's
        // own keys are far shorter; this is the column's width, which is the only promise the store makes.
        RuleFor(model => model.Channel)
            .MaximumLength(ContentPolicy.ChannelKeyMaxLength)
            .WithMessage($"A channel key is at most {ContentPolicy.ChannelKeyMaxLength} characters.")
            .OverridePropertyName("channel");

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");
    }
}
