using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

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
/// The request, status and disposition endpoints (AF.6.4) and the capability that produces a generated body
/// (AF.6.3) both arrive through here: the landing step writes generated revisions with
/// <see cref="RecordGeneratedAsync"/>, and the routes a creator uses call <see cref="EditAsync"/> and
/// <see cref="DispositionAsync"/>.
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

    /// <inheritdoc cref="ISocialPackageBusiness.EditAsync"/>
    /// <remarks>Contributor and above. Validates the sent shape before the domain sees it.</remarks>
    Task<OperationResult<SocialPackageServiceModel>> EditAsync(
        Guid contextId, string channelKey, EditChannelPostViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Applies one decision to one channel: accept, reject, or reaffirm.
    /// </summary>
    /// <remarks>
    /// <see cref="ChannelPostDecision.Regenerate"/> is deliberately not one of them. Regenerating spends the
    /// account's allowance and produces an operation to poll, so it belongs to the AI module's request seam;
    /// a caller sending it here is refused rather than quietly given one of the other three.
    /// </remarks>
    Task<OperationResult<SocialPackageServiceModel>> DispositionAsync(
        string userId,
        Guid contextId,
        string channelKey,
        ChannelPostDispositionViewModel model,
        CancellationToken cancellationToken);

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
internal sealed class SocialPackageFacade(
    ISocialPackageBusiness business,
    IValidator<EditChannelPostViewModel> editValidator,
    IValidator<ChannelPostDispositionViewModel> dispositionValidator,
    IWorkspaceContext workspace) : ISocialPackageFacade
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

    public async Task<OperationResult<SocialPackageServiceModel>> EditAsync(
        Guid contextId, string channelKey, EditChannelPostViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Unnamed(channelKey) is { } unnamed)
        {
            return Failure(unnamed);
        }

        var validation = await editValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure(OperationError.Validation(
                ContentErrorCodes.SocialInvalid,
                "That post could not be saved as described.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        // Business holds the role bar for a draft, as it does for every other move: the rule lives in
        // ContentProposalTransitions and a second copy here could disagree with it.
        return await business.EditAsync(
            contextId, channelKey, model.Body!, model.ExpectedLatestRevisionId, cancellationToken);
    }

    public async Task<OperationResult<SocialPackageServiceModel>> DispositionAsync(
        string userId,
        Guid contextId,
        string channelKey,
        ChannelPostDispositionViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Unnamed(channelKey) is { } unnamed)
        {
            return Failure(unnamed);
        }

        var validation = await dispositionValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure(OperationError.Validation(
                ContentErrorCodes.SocialInvalid,
                "That decision could not be applied as described.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return model.Decision switch
        {
            ChannelPostDecision.Accept => await business.AcceptAsync(
                userId, contextId, channelKey, model.RevisionId!.Value, cancellationToken),
            ChannelPostDecision.Reject => await business.RejectAsync(
                userId, contextId, channelKey, model.RevisionId!.Value, cancellationToken),
            ChannelPostDecision.Reaffirm => await business.ReaffirmAsync(
                userId, contextId, channelKey, cancellationToken),

            // Regenerate reaches the AI module's request seam, never this one. The route decides that before
            // calling here; this is the backstop for a worker or plugin that did not.
            _ => Failure(OperationError.Validation(
                ContentErrorCodes.SocialInvalid,
                "That decision could not be applied as described.",
                [("decision", "Regenerating a post is a new request rather than a decision about this one.")])),
        };
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
