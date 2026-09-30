using CreatorPantry.Domain.Modules.Tenancy.Business;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Tenancy.Facade;

internal sealed class WorkspaceResolutionFacade(
    IValidator<ResolveWorkspaceViewModel> validator,
    IWorkspaceBusiness business,
    IWorkspaceContextResolver contextResolver) : IWorkspaceResolutionFacade
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

    public async Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForOperationAsync(
        Guid workspaceId, Guid membershipId, CancellationToken cancellationToken)
    {
        var result = await business.ResolveForOperationAsync(workspaceId, membershipId, cancellationToken);

        // Unlike ResolveAsync, this populates the ambient context itself rather than leaving that to a
        // caller-side IWorkspaceContextResolver.Resolve call: this method's one caller, the AI worker, lives
        // in another module and Tenancy's own IWorkspaceContextResolver is not a facade type, so it may not
        // cross to reach it (backend.md, ModuleBoundaryTests). Folding the resolve step in here is what lets
        // the worker's own module never need to know that type exists.
        if (result.Succeeded)
        {
            var resolved = result.Value!;
            contextResolver.Resolve(
                resolved.WorkspaceId,
                resolved.WorkspaceSlug,
                resolved.MembershipId,
                resolved.Role,
                resolved.AccountId);
        }

        return result;
    }

    public async Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForServiceAsync(
        Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await business.ResolveForServiceAsync(workspaceId, cancellationToken);

        if (result.Succeeded)
        {
            var resolved = result.Value!;
            contextResolver.Resolve(
                resolved.WorkspaceId, resolved.WorkspaceSlug, resolved.MembershipId, resolved.Role, resolved.AccountId);
        }

        return result;
    }
}
