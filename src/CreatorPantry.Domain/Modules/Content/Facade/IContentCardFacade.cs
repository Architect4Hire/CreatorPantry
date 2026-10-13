using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The content board's application boundary: the list and one card in full (13.5), create (13.6), edit
/// (13.6a), move (13.6b) and delete (13.6c).
/// </summary>
public interface IContentCardFacade
{
    /// <summary>
    /// One page of the resolved workspace's board. Fails with <see cref="ContentErrorCodes.CardListInvalid"/>
    /// for a filter that cannot be read, or <see cref="ContentErrorCodes.CardCursorInvalid"/> for a cursor
    /// issued for another workspace or other filters.
    /// </summary>
    /// <remarks>
    /// Any member may read, a Viewer included, so there is no role check: the route's policy and the resolved
    /// workspace context already are the authorization. Not cached — the board is workspace-private and changes
    /// with every drag.
    /// </remarks>
    Task<OperationResult<ContentCardPageServiceModel>> ListAsync(
        ContentCardListViewModel model, CancellationToken cancellationToken);

    /// <summary>One card in full, or <see cref="ContentErrorCodes.CardNotFound"/>.</summary>
    Task<OperationResult<ContentCardDetailServiceModel>> GetDetailAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a new card on the resolved workspace's board. Contributor and above; a lower role is refused with
    /// <see cref="ContentErrorCodes.CardForbidden"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent under <paramref name="idempotencyKey"/>: a replay returns the first answer and writes
    /// nothing, and the key's record commits with the card or not at all. The key is optional; without one,
    /// two identical requests are two cards.
    /// </remarks>
    Task<IdempotentOutcome<ContentCardDetailServiceModel>> CreateAsync(
        string userId, CreateContentCardViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a merge patch to one card. Contributor and above. Not idempotent by key: the concurrency token
    /// is what makes a repeat safe — a second send finds the card changed and is told so.
    /// </summary>
    Task<OperationResult<ContentCardDetailServiceModel>> UpdateAsync(
        string userId, Guid cardId, UpdateContentCardViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Moves one card to a column and a place in it. Contributor and above. Guarded by the concurrency token,
    /// as an edit is.
    /// </summary>
    Task<OperationResult<ContentCardDetailServiceModel>> MoveAsync(
        string userId, Guid cardId, MoveContentCardViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one card. <strong>Editor</strong> and above, where creating, editing and moving a card take
    /// Contributor: a deletion takes finished planning, and its history, away from every collaborator.
    /// </summary>
    /// <remarks>
    /// <strong>The idempotency key is required.</strong> It is what runs the deletion in one transaction — so
    /// that a card and a picture removed together are removed together or not at all — and what lets a client
    /// whose response was lost ask again and be told what happened, rather than a bare "not found" about a
    /// card it has just deleted.
    /// </remarks>
    Task<IdempotentOutcome<ContentCardDeletionServiceModel>> DeleteAsync(
        string userId, Guid cardId, DeleteContentCardViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a recipe package on the board as a card (13.7). Contributor and above; allowing a package to stand
    /// when a source needs review takes Editor.
    /// </summary>
    /// <remarks>
    /// <strong>The idempotency key is required.</strong> A package is several rows and a card; the key is what
    /// writes them in one transaction, and what stops a retry after a lost response putting a second card on
    /// the board for the same package.
    /// </remarks>
    Task<IdempotentOutcome<ContentCardDetailServiceModel>> CreateRecipePackageAsync(
        string userId, CreateRecipePackageViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardBusiness.GetPipelineCardAsync"/>
    Task<OperationResult<ContentCardDetailServiceModel>> GetPipelineCardAsync(
        Guid creativeContextId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a Content Pipeline run on the board as its one card (13.7a). Contributor and above.
    /// </summary>
    /// <remarks>
    /// <strong>No idempotency key, deliberately.</strong> A key is remembered for a day; a run can be resumed
    /// after a week. What makes this safe to repeat is the store's own rule that a piece of work has one
    /// pipeline card, which does not expire and holds for two requests at once.
    /// </remarks>
    Task<OperationResult<ContentPipelineCardOutcome>> CommitPipelineAsync(
        string userId, Guid creativeContextId, CancellationToken cancellationToken);
}

internal sealed class ContentCardFacade(
    IValidator<ContentCardListViewModel> listValidator,
    IValidator<CreateContentCardViewModel> createValidator,
    IValidator<UpdateContentCardViewModel> updateValidator,
    IValidator<MoveContentCardViewModel> moveValidator,
    IValidator<DeleteContentCardViewModel> deleteValidator,
    IValidator<CreateRecipePackageViewModel> packageValidator,
    IContentCardBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IContentCardFacade
{
    private const string CannotList = "That board cannot be read as described.";
    private const string CreateOperation = "content.card.create";
    private const string CannotUpdate = "That card could not be edited as described.";
    private const string CannotMove = "That card could not be moved as described.";
    private const string CannotDelete = "That card could not be deleted as described.";
    private const string DeleteOperation = "content.card.delete";
    private const string PackageOperation = "content.card.recipe_package.create";

    public async Task<IdempotentOutcome<ContentCardDetailServiceModel>> CreateAsync(
        string userId, CreateContentCardViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Refused(forbidden);
        }

        var validation = await createValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                "That card could not be created as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The submitted fields are the fingerprint: the same key with a different body is a different request.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, CreateOperation, idempotencyKey, model, KeyRequired: false),
            token => business.CreateAsync(userId, model, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<ContentCardDetailServiceModel>> CreateRecipePackageAsync(
        string userId, CreateRecipePackageViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Refused(forbidden);
        }

        var validation = await packageValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                "That recipe package could not be put together as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, PackageOperation, idempotencyKey, model, KeyRequired: true),
            token => business.CreateRecipePackageAsync(userId, model, token),
            cancellationToken);
    }

    public async Task<OperationResult<ContentCardDetailServiceModel>> UpdateAsync(
        string userId, Guid cardId, UpdateContentCardViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return OperationResult<ContentCardDetailServiceModel>.Failure(forbidden);
        }

        // Shape, so 400 rather than 409: a token that could never have been issued is not a stale one, and
        // telling a client to re-read would send it round a loop that cannot end differently.
        if (!ContentCardConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            return OperationResult<ContentCardDetailServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotUpdate,
                [("expectedConcurrencyToken", "Send the card's concurrencyToken, exactly as it was returned, with your edit.")]));
        }

        var validation = await updateValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<ContentCardDetailServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotUpdate,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.UpdateAsync(userId, cardId, model, cancellationToken);
    }

    public async Task<OperationResult<ContentCardDetailServiceModel>> MoveAsync(
        string userId, Guid cardId, MoveContentCardViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return OperationResult<ContentCardDetailServiceModel>.Failure(forbidden);
        }

        // Shape, so 400 rather than 409, for the reason an edit gives.
        if (!ContentCardConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            return OperationResult<ContentCardDetailServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotMove,
                [("expectedConcurrencyToken", "Send the card's concurrencyToken, exactly as it was returned, with your move.")]));
        }

        var validation = await moveValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<ContentCardDetailServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotMove,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.MoveAsync(userId, cardId, model, cancellationToken);
    }

    public async Task<IdempotentOutcome<ContentCardDeletionServiceModel>> DeleteAsync(
        string userId, Guid cardId, DeleteContentCardViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Editor, not Contributor. The route policy has already refused a lower role; this is the backstop for
        // a worker or plugin reaching the facade directly.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return NotDeleted(new OperationError(
                ContentErrorCodes.CardForbidden,
                "You do not have permission to delete cards from the content board in this workspace.",
                new Dictionary<string, string[]>()));
        }

        if (!ContentCardConcurrencyToken.IsWellFormed(model.ExpectedConcurrencyToken))
        {
            return NotDeleted(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotDelete,
                [("expectedConcurrencyToken", "Send the card's concurrencyToken, exactly as it was returned, with your deletion.")]));
        }

        var validation = await deleteValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return NotDeleted(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotDelete,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The card is part of the fingerprint as well as the body: the same key against a different card is a
        // different request, not a replay.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, DeleteOperation, idempotencyKey, new { cardId, model }, KeyRequired: true),
            token => business.DeleteAsync(userId, cardId, model, token),
            cancellationToken);

        static IdempotentOutcome<ContentCardDeletionServiceModel> NotDeleted(OperationError error) =>
            new(OperationResult<ContentCardDeletionServiceModel>.Failure(error), Replayed: false);
    }

    public Task<OperationResult<ContentCardDetailServiceModel>> GetPipelineCardAsync(
        Guid creativeContextId, CancellationToken cancellationToken) =>
        business.GetPipelineCardAsync(creativeContextId, cancellationToken);

    public async Task<OperationResult<ContentPipelineCardOutcome>> CommitPipelineAsync(
        string userId, Guid creativeContextId, CancellationToken cancellationToken) =>
        Forbidden() is { } forbidden
            ? OperationResult<ContentPipelineCardOutcome>.Failure(forbidden)
            : await business.CommitPipelineAsync(userId, creativeContextId, cancellationToken);

    // Contributor: planning work on the board is contributing creator content, the same bar as saving a
    // prompt. The route policy has already refused a lower role; this is the backstop for a worker or plugin
    // reaching the facade directly, which is why the facade rather than the controller owns the bar.
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Contributor
            ? new OperationError(
                ContentErrorCodes.CardForbidden,
                "You do not have permission to change the content board in this workspace.",
                new Dictionary<string, string[]>())
            : null;

    private static IdempotentOutcome<ContentCardDetailServiceModel> Refused(OperationError error) =>
        new(OperationResult<ContentCardDetailServiceModel>.Failure(error), Replayed: false);

    public async Task<OperationResult<ContentCardPageServiceModel>> ListAsync(
        ContentCardListViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await listValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            // A bad cursor gets its own code, because it means something different to a client than a bad
            // filter does: stop paging and start over, rather than fix the input.
            var code = validation.Errors.Any(failure => failure.PropertyName.Equals("cursor", StringComparison.Ordinal))
                ? ContentErrorCodes.CardCursorInvalid
                : ContentErrorCodes.CardListInvalid;

            return OperationResult<ContentCardPageServiceModel>.Failure(OperationError.Validation(
                code,
                CannotList,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        if (!ContentCardListQueryFactory.TryCreate(model, workspace.WorkspaceId, out var criteria, out var error))
        {
            return OperationResult<ContentCardPageServiceModel>.Failure(error!);
        }

        return OperationResult<ContentCardPageServiceModel>.Success(
            await business.ListAsync(criteria!, cancellationToken));
    }

    public Task<OperationResult<ContentCardDetailServiceModel>> GetDetailAsync(
        Guid cardId, CancellationToken cancellationToken) =>
        business.GetDetailAsync(cardId, cancellationToken);
}
