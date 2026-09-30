using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

public sealed class CreateBrandProfileViewModelValidator : AbstractValidator<CreateBrandProfileViewModel>
{
    public CreateBrandProfileViewModelValidator(IContentChannelCatalog channels)
    {
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in new[]
            {
                BrandProfileInputChecks.Name(model.BrandName),
                BrandProfileInputChecks.Text(nameof(model.ShortDescription), model.ShortDescription, BrandPolicy.ShortDescriptionMaxLength),
                BrandProfileInputChecks.Text(nameof(model.DefaultAudience), model.DefaultAudience, BrandPolicy.DefaultAudienceMaxLength),
                BrandProfileInputChecks.Locale(model.Locale),
                BrandProfileInputChecks.TimeZone(model.TimeZoneId),
                BrandProfileInputChecks.Channels(model.ChannelDefaults, channels),
                BrandProfileInputChecks.Links(model.Links),
                BrandProfileInputChecks.Assets(model.Assets),
            }.SelectMany(failures => failures))
            {
                context.AddFailure(field, message);
            }
        });
    }
}

public sealed class UpdateBrandProfileViewModelValidator : AbstractValidator<UpdateBrandProfileViewModel>
{
    private readonly IContentChannelCatalog _channels;

    public UpdateBrandProfileViewModelValidator(IContentChannelCatalog channels)
    {
        _channels = channels;

        // Required, and checked first: an edit with nothing to check against cannot be applied safely. A token
        // that could never have been issued is a client bug, not a collaborator's edit.
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the brand profile's concurrency token with your edit.")
            .Must(BrandConcurrencyToken.IsWellFormed).WithMessage("That is not a concurrency token this API issued.")
            .OverridePropertyName(nameof(UpdateBrandProfileViewModel.ExpectedConcurrencyToken));

        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in Failures(model, _channels))
            {
                context.AddFailure(field, message);
            }
        });
    }

    private static IEnumerable<(string, string)> Failures(UpdateBrandProfileViewModel model, IContentChannelCatalog catalog)
    {
        foreach (var failure in BrandProfileInputChecks.Text(
            nameof(model.Reason), model.Reason, BrandPolicy.ReasonMaxLength))
        {
            yield return failure;
        }

        // The one field that may be changed but never cleared.
        if (model.BrandName.IsSubmitted)
        {
            foreach (var failure in BrandProfileInputChecks.Name(model.BrandName.Value))
            {
                yield return failure;
            }
        }

        if (model.ShortDescription.TryGetSubmitted(out var description))
        {
            foreach (var failure in BrandProfileInputChecks.Text(nameof(model.ShortDescription), description, BrandPolicy.ShortDescriptionMaxLength))
            {
                yield return failure;
            }
        }

        if (model.DefaultAudience.TryGetSubmitted(out var audience))
        {
            foreach (var failure in BrandProfileInputChecks.Text(nameof(model.DefaultAudience), audience, BrandPolicy.DefaultAudienceMaxLength))
            {
                yield return failure;
            }
        }

        if (model.Locale.TryGetSubmitted(out var locale))
        {
            foreach (var failure in BrandProfileInputChecks.Locale(locale))
            {
                yield return failure;
            }
        }

        if (model.TimeZoneId.TryGetSubmitted(out var zone))
        {
            foreach (var failure in BrandProfileInputChecks.TimeZone(zone))
            {
                yield return failure;
            }
        }

        if (model.ChannelDefaults.TryGetSubmitted(out var channels))
        {
            foreach (var failure in BrandProfileInputChecks.Channels(channels, catalog))
            {
                yield return failure;
            }
        }

        if (model.Links.TryGetSubmitted(out var links))
        {
            foreach (var failure in BrandProfileInputChecks.Links(links))
            {
                yield return failure;
            }
        }

        if (model.Assets.TryGetSubmitted(out var assets))
        {
            foreach (var failure in BrandProfileInputChecks.Assets(assets))
            {
                yield return failure;
            }
        }
    }
}
