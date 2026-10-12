using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// What the library calls to put a picture it is keeping on the piece of work it was kept for.
/// </summary>
/// <remarks>
/// <para>
/// A facade of its own, over nothing but the creative context's own storage, for the reason
/// <see cref="IThemedDayHistoryInvalidationFacade"/> is one: the library must be able to take it without
/// resolving everything <see cref="ICreativeContextFacade"/> needs to check a creator's choice of source —
/// recipes, concepts, prompts and the library itself, which would close a cycle.
/// </para>
/// <para>
/// <strong>It does not retire the themed-day history</strong>, unlike every write on
/// <see cref="ICreativeContextFacade"/>: <see cref="RecordAsync"/> runs inside the caller's transaction, and a
/// history derived before that commits would be cached without the picture. The caller retires it once its
/// own transaction has committed.
/// </para>
/// </remarks>
public interface ICreativeContextKeptPictureFacade
{
    /// <inheritdoc cref="ICreativeContextKeptPictureBusiness.CheckAsync"/>
    Task<OperationError?> CheckAsync(Guid contextId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ICreativeContextKeptPictureBusiness.RecordAsync"/>
    Task<OperationError?> RecordAsync(
        Guid contextId, Guid mediaAssetId, int mediaAssetVersionNumber, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextKeptPictureFacade"/>
internal sealed class CreativeContextKeptPictureFacade(
    ICreativeContextKeptPictureBusiness business, IWorkspaceContext workspace) : ICreativeContextKeptPictureFacade
{
    public async Task<OperationError?> CheckAsync(Guid contextId, CancellationToken cancellationToken) =>
        Forbidden() ?? await business.CheckAsync(contextId, cancellationToken);

    public async Task<OperationError?> RecordAsync(
        Guid contextId, Guid mediaAssetId, int mediaAssetVersionNumber, CancellationToken cancellationToken) =>
        Forbidden() ?? await business.RecordAsync(contextId, mediaAssetId, mediaAssetVersionNumber, cancellationToken);

    // Contributor, the bar every other change to a piece of work is held to. The keep route has already
    // refused a lower role; this is the backstop for a caller that is not that route.
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Contributor
            ? new OperationError(
                ContentErrorCodes.CreativeContextForbidden,
                "You do not have permission to change creative contexts in this workspace.",
                new Dictionary<string, string[]>())
            : null;
}
