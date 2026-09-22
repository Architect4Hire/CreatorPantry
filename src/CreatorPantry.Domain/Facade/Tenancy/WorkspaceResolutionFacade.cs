using CreatorPantry.Domain.Business.Tenancy;
using CreatorPantry.Domain.Models.Results;
using CreatorPantry.Domain.Models.ServiceModels.Tenancy;
using CreatorPantry.Domain.Models.ViewModels.Tenancy;
using CreatorPantry.Domain.Tenancy;
using FluentValidation;

namespace CreatorPantry.Domain.Facade.Tenancy;

internal sealed class WorkspaceResolutionFacade(
    IValidator<ResolveWorkspaceViewModel> validator, IWorkspaceBusiness business) : IWorkspaceResolutionFacade
{
    public async Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
        string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<ResolvedWorkspaceServiceModel>.Failure(OperationError.Validation(
                TenancyErrorCodes.WorkspaceResolutionInvalidRequest, "The request is invalid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return await business.ResolveAsync(userId, model, cancellationToken);
    }
}
