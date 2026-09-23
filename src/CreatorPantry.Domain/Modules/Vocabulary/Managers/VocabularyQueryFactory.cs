using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Managers;

/// <summary>Shape rules for this module's list query. See the validator remarks in Measurement for the shared reasoning.</summary>
public sealed class ReferenceQueryViewModelValidator : AbstractValidator<ReferenceQueryViewModel>
{
    public ReferenceQueryViewModelValidator()
    {
        RuleFor(model => model.Search)
            .MaximumLength(ReferencePolicy.SearchMaxLength)
            .WithMessage($"Search terms are limited to {ReferencePolicy.SearchMaxLength} characters.")
            .OverridePropertyName(nameof(ReferenceQueryViewModel.Search));

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName(nameof(ReferenceQueryViewModel.Cursor));
    }
}

/// <summary>Turns a validated vocabulary view model into the effective domain query.</summary>
/// <remarks>
/// The <c>resource</c> argument is what distinguishes seven otherwise identical queries, and it is what the
/// cursor is bound to — which is why a cuisine cursor cannot resume a list of allergens.
/// </remarks>
public static class VocabularyQueryFactory
{
    public static bool TryCreate(ReferenceQueryViewModel model, string resource, out ReferenceQuery query)
    {
        query = default!;

        var search = ReferencePolicy.NormalizeSearch(model.Search);
        var scope = ReferenceQueryKey.Scope(resource, search);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            return false;
        }

        query = new ReferenceQuery(search, cursor, ReferencePolicy.ClampPageSize(model.Limit), scope);

        return true;
    }
}
