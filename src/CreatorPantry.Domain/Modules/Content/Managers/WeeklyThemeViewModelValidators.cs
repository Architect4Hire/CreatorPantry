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
