using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

public interface IBrandWritingGuideFacade
{
    Task<OperationResult<BrandWritingGuideServiceModel>> GetAsync(
        BrandWritingGuideViewModel model, CancellationToken cancellationToken);
}

internal sealed class BrandWritingGuideFacade(
    IBrandWritingGuideBusiness business,
    IValidator<BrandWritingGuideViewModel> validator) : IBrandWritingGuideFacade
{
    public async Task<OperationResult<BrandWritingGuideServiceModel>> GetAsync(
        BrandWritingGuideViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<BrandWritingGuideServiceModel>.Failure(OperationError.Validation(
                BrandWritingGuideErrors.RequestInvalid,
                "The request is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return await business.GetAsync(model, cancellationToken);
    }
}
