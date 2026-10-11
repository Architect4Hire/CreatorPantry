using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

public sealed class ReplaceWeeklyThemesViewModelValidator : AbstractValidator<ReplaceWeeklyThemesViewModel>
{
    public ReplaceWeeklyThemesViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in WeeklyThemeInputChecks.Themes(model.Themes))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class ThemedDayHistoryViewModelValidator : AbstractValidator<ThemedDayHistoryViewModel>
{
    public ThemedDayHistoryViewModelValidator() =>
        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");
}
