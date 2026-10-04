using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

public sealed class ContentSeedQueryViewModelValidator : AbstractValidator<ContentSeedQueryViewModel>
{
    public ContentSeedQueryViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in ContentSeedInputChecks.Query(model))
            {
                context.AddFailure(field, message);
            }
        });
}
