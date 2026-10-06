using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Shape checks on a library search: the things a creator can get wrong that are not worth a query.
/// </summary>
/// <remarks>
/// Deliberately thin. Page size is clamped rather than refused, and an unmatched filter is an empty page
/// rather than an error — both following the recipe library. What is refused is a term longer than any
/// search anybody meant, a cursor that is not one this server issued, and an inverted date range.
/// </remarks>
public sealed class MediaAssetSearchViewModelValidator : AbstractValidator<MediaAssetSearchViewModel>
{
    private static readonly string KeyTooLong =
        $"Keys are limited to {MediaPolicy.VocabularyKeyMaxLength} characters.";

    public MediaAssetSearchViewModelValidator()
    {
        RuleFor(model => model.Search)
            .MaximumLength(MediaAssetSearchPolicy.SearchMaxLength)
            .WithMessage($"Search terms are limited to {MediaAssetSearchPolicy.SearchMaxLength} characters.")
            .OverridePropertyName("search");

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");

        // A key longer than the column can hold matches nothing by construction, so refusing it costs a
        // client nothing it could have wanted. It also keeps the cursor scope assembled from bounded parts,
        // which is what MediaAssetSearchScope's note about the free-text term going last relies on.
        RuleFor(model => model.Channel)
            .MaximumLength(MediaPolicy.VocabularyKeyMaxLength)
            .WithMessage(KeyTooLong)
            .OverridePropertyName("channel");

        RuleFor(model => model.Platform)
            .MaximumLength(MediaPolicy.VocabularyKeyMaxLength)
            .WithMessage(KeyTooLong)
            .OverridePropertyName("platform");

        RuleFor(model => model.Style)
            .MaximumLength(MediaPolicy.VocabularyKeyMaxLength)
            .WithMessage(KeyTooLong)
            .OverridePropertyName("style");

        // An inverted range matches nothing, which is a legal answer but almost never the intended one — and
        // "no assets" is an answer a creator cannot debug. Refusing it names the mistake instead.
        RuleFor(model => model.CreatedBefore)
            .Must((model, before) => before > model.CreatedFrom)
            .WithMessage("createdBefore must be later than createdFrom.")
            .When(model => model.CreatedFrom is not null && model.CreatedBefore is not null)
            .OverridePropertyName("createdBefore");
    }
}
