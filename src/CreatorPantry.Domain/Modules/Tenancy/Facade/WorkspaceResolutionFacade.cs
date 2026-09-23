using CreatorPantry.Domain.Modules.Tenancy.Business;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Tenancy.Facade;

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
