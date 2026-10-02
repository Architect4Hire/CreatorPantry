using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

public interface IBrandVisualGuideFacade
{
    Task<OperationResult<BrandVisualGuideServiceModel>> GetAsync(
        BrandVisualGuideViewModel model, CancellationToken cancellationToken);
}

internal sealed class BrandVisualGuideFacade(
    IBrandVisualGuideBusiness business,
    IValidator<BrandVisualGuideViewModel> validator) : IBrandVisualGuideFacade
{
    public async Task<OperationResult<BrandVisualGuideServiceModel>> GetAsync(
        BrandVisualGuideViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<BrandVisualGuideServiceModel>.Failure(OperationError.Validation(
                BrandVisualGuideErrors.RequestInvalid,
                "The request is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return await business.GetAsync(model, cancellationToken);
    }
}
