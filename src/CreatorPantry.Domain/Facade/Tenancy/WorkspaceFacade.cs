using CreatorPantry.Domain.Business.Tenancy;
using CreatorPantry.Domain.Models.Results;
using CreatorPantry.Domain.Models.ServiceModels.Tenancy;
using CreatorPantry.Domain.Models.ViewModels.Tenancy;
using CreatorPantry.Domain.Tenancy;
using FluentValidation;

namespace CreatorPantry.Domain.Facade.Tenancy;

internal sealed class WorkspaceFacade(
    IValidator<CreateWorkspaceViewModel> createValidator,
    IValidator<UpdateWorkspaceViewModel> updateValidator,
    IWorkspaceBusiness business) : IWorkspaceFacade
{
    public Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(
        string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return business.GetMyMembershipsAsync(userId, cancellationToken);
    }

    public async Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
        string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var validation = await createValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkspaceServiceModel>.Failure(OperationError.Validation(
                TenancyErrorCodes.WorkspaceInvalidRequest, "The request is invalid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return await business.CreateAsync(userId, model, cancellationToken);
    }

    public Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken) =>
        business.GetCurrentAsync(cancellationToken);

    public async Task<OperationResult<WorkspaceServiceModel>> RenameCurrentAsync(
        UpdateWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        var validation = await updateValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<WorkspaceServiceModel>.Failure(OperationError.Validation(
                TenancyErrorCodes.WorkspaceInvalidRequest, "The request is invalid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        var renamed = await business.RenameCurrentAsync(model, cancellationToken);
        return OperationResult<WorkspaceServiceModel>.Success(renamed);
    }
}
