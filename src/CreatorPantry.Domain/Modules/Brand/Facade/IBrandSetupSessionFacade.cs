using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>
/// The application boundary for the caller's own "Create my voice" setup session. Always scoped to the
/// resolved workspace and the authenticated caller; no method accepts a user or workspace identifier.
/// </summary>
public interface IBrandSetupSessionFacade
{
    /// <summary>The caller's session, or a success carrying null when they have none. Any member may read.</summary>
    Task<OperationResult<BrandSetupSessionServiceModel?>> GetAsync(CancellationToken cancellationToken);

    /// <summary>Upsert. Editor or above. <paramref name="ifMatch"/> is required once a session exists.</summary>
    Task<OperationResult<(BrandSetupSessionServiceModel Session, bool Created)>> SaveAsync(
        SaveBrandSetupSessionViewModel model, string? ifMatch, CancellationToken cancellationToken);

    /// <summary>Completes the session. Editor or above.</summary>
    Task<OperationResult<BrandSetupSessionServiceModel>> CompleteAsync(string? ifMatch, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the caller's own session ("start over"). Editor or above. Idempotent when none exists; a
    /// <c>brand_setup_session_conflict</c> failure if concurrent writes kept the row alive.
    /// </summary>
    Task<OperationResult<bool>> DeleteAsync(CancellationToken cancellationToken);
}

internal sealed class BrandSetupSessionFacade(
    IValidator<SaveBrandSetupSessionViewModel> validator,
    IBrandSetupSessionBusiness business,
    IWorkspaceContext workspace) : IBrandSetupSessionFacade
{
    public Task<OperationResult<BrandSetupSessionServiceModel?>> GetAsync(CancellationToken cancellationToken) =>
        business.GetAsync(cancellationToken);

    public async Task<OperationResult<(BrandSetupSessionServiceModel Session, bool Created)>> SaveAsync(
        SaveBrandSetupSessionViewModel model, string? ifMatch, CancellationToken cancellationToken)
    {
        if (Forbidden() is { } forbidden)
        {
            return OperationResult<(BrandSetupSessionServiceModel, bool)>.Failure(forbidden);
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<(BrandSetupSessionServiceModel, bool)>.Failure(OperationError.Validation(
                BrandErrorCodes.SetupSessionInvalidRequest,
                "The voice setup session could not be saved.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.SaveAsync(model, ifMatch, cancellationToken);
    }

    public async Task<OperationResult<BrandSetupSessionServiceModel>> CompleteAsync(
        string? ifMatch, CancellationToken cancellationToken) =>
        Forbidden() is { } forbidden
            ? OperationResult<BrandSetupSessionServiceModel>.Failure(forbidden)
            : await business.CompleteAsync(ifMatch, cancellationToken);

    public async Task<OperationResult<bool>> DeleteAsync(CancellationToken cancellationToken)
    {
        if (Forbidden() is { } forbidden)
        {
            return OperationResult<bool>.Failure(forbidden);
        }

        return await business.DeleteAsync(cancellationToken);
    }

    // Editor, the same bar as the brand profile it sits beside.
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Editor
            ? new OperationError(
                BrandErrorCodes.SetupSessionForbidden,
                "You do not have permission to change voice setup in this workspace.",
                new Dictionary<string, string[]>())
            : null;
}
