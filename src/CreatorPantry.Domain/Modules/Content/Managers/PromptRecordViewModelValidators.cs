using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Shape validation for a prompt save. Takes the channel catalogue, as the brand profile's validators do,
/// because an unknown channel key is a shape failure here rather than a lookup.
/// </summary>
public sealed class SavePromptRecordViewModelValidator : AbstractValidator<SavePromptRecordViewModel>
{
    public SavePromptRecordViewModelValidator(IContentChannelCatalog channels) =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in PromptRecordInputChecks.Save(model, channels))
            {
                context.AddFailure(field, message);
            }
        });
}
