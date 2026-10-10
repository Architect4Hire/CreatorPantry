using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The application boundary for post packages (AF.6.1): the posts written for one creative context, one
/// channel at a time.
/// </summary>
/// <remarks>
/// <para>
/// The workspace is never an argument. It reaches every operation through the resolved
/// <see cref="IWorkspaceContext"/>, so a controller, a worker and a plugin all get the same isolation, and no
/// input here has a field one could arrive in.
/// </para>
/// <para>
/// No route calls this yet — the request, status and disposition endpoints are AF.6.4, and the capability that
/// produces a generated body is AF.6.3. Both arrive through here.
/// </para>
/// </remarks>
public interface ISocialPackageFacade
{
    /// <inheritdoc cref="ISocialPackageBusiness.GetAsync"/>
    /// <remarks>Any member.</remarks>
    Task<OperationResult<SocialPackageServiceModel?>> GetAsync(Guid contextId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageBusiness.RecordGeneratedAsync"/>
    /// <remarks>Contributor and above.</remarks>
    Task<OperationResult<SocialPackageServiceModel>> RecordGeneratedAsync(
        SocialGeneratedRevisionInput input, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageBusiness.RecordEditAsync"/>
    /// <remarks>Contributor and above.</remarks>
    Task<OperationResult<SocialPackageServiceModel>> RecordEditAsync(
        SocialEditedRevisionInput input, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageBusiness.AcceptAsync"/>
    /// <remarks>Editor and above. Accepting an already accepted revision changes nothing.</remarks>
    Task<OperationResult<SocialPackageServiceModel>> AcceptAsync(
        string userId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageBusiness.RejectAsync"/>
    /// <remarks>Editor and above.</remarks>
    Task<OperationResult<SocialPackageServiceModel>> RejectAsync(
        string userId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageBusiness.ReaffirmAsync"/>
    /// <remarks>Editor and above.</remarks>
    Task<OperationResult<SocialPackageServiceModel>> ReaffirmAsync(
        string userId, Guid contextId, string channelKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISocialPackageFacade"/>
internal sealed class SocialPackageFacade(ISocialPackageBusiness business, IWorkspaceContext workspace) : ISocialPackageFacade
{
    public Task<OperationResult<SocialPackageServiceModel?>> GetAsync(Guid contextId, CancellationToken cancellationToken) =>
        business.GetAsync(contextId, cancellationToken);

    public Task<OperationResult<SocialPackageServiceModel>> RecordGeneratedAsync(
        SocialGeneratedRevisionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return Forbidden() is { } forbidden
            ? Task.FromResult(Failure(forbidden))
            : business.RecordGeneratedAsync(input, cancellationToken);
    }

    public Task<OperationResult<SocialPackageServiceModel>> RecordEditAsync(
        SocialEditedRevisionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return Forbidden() is { } forbidden
            ? Task.FromResult(Failure(forbidden))
            : business.RecordEditAsync(input, cancellationToken);
    }

    public Task<OperationResult<SocialPackageServiceModel>> AcceptAsync(
        string userId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken) =>
        Unnamed(channelKey) is { } unnamed
            ? Task.FromResult(Failure(unnamed))
            : business.AcceptAsync(userId, contextId, channelKey, revisionId, cancellationToken);

    public Task<OperationResult<SocialPackageServiceModel>> RejectAsync(
        string userId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken) =>
        Unnamed(channelKey) is { } unnamed
            ? Task.FromResult(Failure(unnamed))
            : business.RejectAsync(userId, contextId, channelKey, revisionId, cancellationToken);

    public Task<OperationResult<SocialPackageServiceModel>> ReaffirmAsync(
        string userId, Guid contextId, string channelKey, CancellationToken cancellationToken) =>
        Unnamed(channelKey) is { } unnamed
            ? Task.FromResult(Failure(unnamed))
            : business.ReaffirmAsync(userId, contextId, channelKey, cancellationToken);

    // Contributor: drafting a post is contributing creator content, the same bar as shaping the context it is
    // for. The backstop for a worker or plugin reaching the facade directly; the bar for a decision depends on
    // the move, so Business holds that one against the transition rules.
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Contributor
            ? new OperationError(
                ContentErrorCodes.SocialForbidden,
                "You do not have permission to write posts in this workspace.",
                new Dictionary<string, string[]>())
            : null;

    private static OperationError? Unnamed(string? channelKey) =>
        string.IsNullOrWhiteSpace(channelKey)
            ? OperationError.Validation(
                ContentErrorCodes.SocialInvalid,
                "That post could not be found as described.",
                [("channelKey", "Name the channel this post is for.")])
            : null;

    private static OperationResult<SocialPackageServiceModel> Failure(OperationError error) =>
        OperationResult<SocialPackageServiceModel>.Failure(error);
}
