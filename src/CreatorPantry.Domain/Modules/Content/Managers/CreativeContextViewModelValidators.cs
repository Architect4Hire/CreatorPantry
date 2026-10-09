using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

public sealed class CreateCreativeContextViewModelValidator : AbstractValidator<CreateCreativeContextViewModel>
{
    public CreateCreativeContextViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in CreativeContextInputChecks.Create(model))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class PatchCreativeContextViewModelValidator : AbstractValidator<PatchCreativeContextViewModel>
{
    public PatchCreativeContextViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in CreativeContextInputChecks.Patch(model))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class AddCreativeContextReferenceViewModelValidator
    : AbstractValidator<AddCreativeContextReferenceViewModel>
{
    public AddCreativeContextReferenceViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in CreativeContextInputChecks.Add(model))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class CreativeContextListViewModelValidator : AbstractValidator<CreativeContextListViewModel>
{
    public CreativeContextListViewModelValidator() =>
        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");
}
