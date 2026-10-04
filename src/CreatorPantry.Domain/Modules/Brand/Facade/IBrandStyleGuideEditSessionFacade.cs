using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>
/// The application boundary for the caller's own unsaved edit of one brand style guide: what the editor
/// autosaves into, so closing a tab does not cost a creator their words.
/// </summary>
/// <remarks>
/// <para>
/// Always scoped to the resolved workspace, the guide from the route and the authenticated caller. No method
/// accepts a user or workspace identifier, and there is no way to read another creator's draft — not a
/// filtered one, not an administrative one.
/// </para>
/// <para>
/// <strong>Nothing here is canonical.</strong> A draft says what somebody is in the middle of typing;
/// <c>BrandStyleGuideVersion</c> says what the guide is. No generation is grounded on a draft, no draft can be
/// approved or activated, and keeping one changes nothing about the guide.
/// </para>
/// <para>
/// <strong>No idempotency keys.</strong> Every operation here is naturally idempotent — a save replaces the
/// one row the caller owns, and a delete of nothing is a success — so a retry after a dropped response needs
/// no record to recognise itself by. <c>If-Match</c> is what stops two tabs overwriting each other, which is
/// a different question from replay.
/// </para>
/// </remarks>
public interface IBrandStyleGuideEditSessionFacade
{
    /// <summary>
    /// The caller's draft of one guide, or a success carrying null when they have none. Editor or above.
    /// </summary>
    /// <param name="guideId">The guide, from the route. Never from the query string or the body.</param>
    Task<OperationResult<BrandStyleGuideEditSessionServiceModel?>> GetAsync(
        Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// Upsert of the caller's draft. Editor or above. <paramref name="ifMatch"/> is required once one exists.
    /// </summary>
    Task<OperationResult<(BrandStyleGuideEditSessionServiceModel Session, bool Created)>> SaveAsync(
        Guid guideId,
        SaveBrandStyleGuideEditSessionViewModel model,
        string? ifMatch,
        CancellationToken cancellationToken);

    /// <summary>Discards the caller's own draft of one guide. Editor or above.</summary>
    Task<OperationResult<bool>> DeleteAsync(Guid guideId, CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideEditSessionFacade(
    IValidator<SaveBrandStyleGuideEditSessionViewModel> validator,
    IBrandStyleGuideEditSessionBusiness business,
    IWorkspaceContext workspace) : IBrandStyleGuideEditSessionFacade
{
    public async Task<OperationResult<BrandStyleGuideEditSessionServiceModel?>> GetAsync(
        Guid guideId, CancellationToken cancellationToken) =>
        Forbidden() is { } forbidden
            ? OperationResult<BrandStyleGuideEditSessionServiceModel?>.Failure(forbidden)
            : await business.GetAsync(guideId, cancellationToken);

    public async Task<OperationResult<(BrandStyleGuideEditSessionServiceModel Session, bool Created)>> SaveAsync(
        Guid guideId,
        SaveBrandStyleGuideEditSessionViewModel model,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        if (Forbidden() is { } forbidden)
        {
            return OperationResult<(BrandStyleGuideEditSessionServiceModel, bool)>.Failure(forbidden);
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<(BrandStyleGuideEditSessionServiceModel, bool)>.Failure(
                OperationError.Validation(
                    BrandErrorCodes.GuideInvalidRequest,
                    "That edit could not be kept.",
                    validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.SaveAsync(guideId, model, ifMatch, cancellationToken);
    }

    public async Task<OperationResult<bool>> DeleteAsync(Guid guideId, CancellationToken cancellationToken) =>
        Forbidden() is { } forbidden
            ? OperationResult<bool>.Failure(forbidden)
            : await business.DeleteAsync(guideId, cancellationToken);

    /// <summary>
    /// Editor, including on the read — deliberately above the Viewer bar the guide's own reads carry.
    /// </summary>
    /// <remarks>
    /// Two reasons. A draft is only useful to somebody who could save it as a version, and that is the Editor
    /// bar; and letting a Contributor autosave would bank work they can never commit, which is a crueller
    /// failure than not offering the editor at all. The read carries the same gate as the write because the
    /// thing being read is one creator's own unsaved work, not the guide.
    /// </remarks>
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Editor
            ? new OperationError(
                BrandErrorCodes.GuideForbidden,
                "You do not have permission to edit brand style guides in this workspace.",
                new Dictionary<string, string[]>())
            : null;
}
