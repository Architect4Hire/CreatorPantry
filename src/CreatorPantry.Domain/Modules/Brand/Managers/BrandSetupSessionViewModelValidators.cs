using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

public sealed class SaveBrandSetupSessionViewModelValidator : AbstractValidator<SaveBrandSetupSessionViewModel>
{
    public const int DraftMaxBytes = BrandSetupSessionDraft.MaxBytes;

    public SaveBrandSetupSessionViewModelValidator()
    {
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in BrandSetupSteps.Check(
                model.CurrentStep, model.FurthestStep, model.CompletedSteps, model.SkippedSteps))
            {
                context.AddFailure(field, message);
            }

            if (BrandSetupSessionDraft.Problem(model.DraftJson) is { } problem)
            {
                context.AddFailure(nameof(model.DraftJson), problem);
            }
        });
    }
}
