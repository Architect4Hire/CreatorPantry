using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IContentCardBusiness
{
    /// <summary>One page of the board, with every column's count under the same filters.</summary>
    Task<ContentCardPageServiceModel> ListAsync(ContentCardListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// One card in full, or a failure carrying <see cref="ContentErrorCodes.CardNotFound"/> — for a card that
    /// does not exist and for one another workspace owns alike.
    /// </summary>
    Task<OperationResult<ContentCardDetailServiceModel>> GetDetailAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a new card at the bottom of its column, with its creation recorded as its first move.
    /// </summary>
    /// <remarks>
    /// Fails with <see cref="ContentErrorCodes.CardInvalid"/> for a body that cannot be read or names a channel
    /// or cooking code that is not offered, <see cref="ContentErrorCodes.CardLineageUnprocessable"/> for a
    /// linked record this workspace cannot link, and <see cref="ContentErrorCodes.CardNotSaved"/> when the
    /// write did not happen. On any failure nothing is written.
    /// </remarks>
    /// <param name="actorUserId">Who is creating it, for the audit entry. The membership comes from the resolved context.</param>
    Task<OperationResult<ContentCardDetailServiceModel>> CreateAsync(
        string actorUserId, CreateContentCardViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a merge patch to a card: only the fields it mentions change.
    /// </summary>
    /// <remarks>
    /// Fails with <see cref="ContentErrorCodes.CardNotFound"/> for a card this workspace does not have,
    /// <see cref="ContentErrorCodes.CardStale"/> when the card is no longer in the state the patch was composed
    /// against, <see cref="ContentErrorCodes.CardInvalid"/> for a patch that cannot be read or would drop a
    /// version pin without saying so, and <see cref="ContentErrorCodes.CardLineageUnprocessable"/> for a newly
    /// linked record that cannot be linked. On any failure nothing is written. A patch that changes nothing
    /// writes nothing and leaves the token valid.
    /// </remarks>
    Task<OperationResult<ContentCardDetailServiceModel>> UpdateAsync(
        string actorUserId, Guid cardId, UpdateContentCardViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a card in a column at a place: a move when the column changes, a new place in the same column when
    /// it does not.
    /// </summary>
    /// <remarks>
    /// Only a change of column is recorded in the card's movement history. Fails with
    /// <see cref="ContentErrorCodes.CardNotFound"/>, <see cref="ContentErrorCodes.CardStale"/> when the card or
    /// a card that had to be re-spaced around it changed first, <see cref="ContentErrorCodes.CardInvalid"/> for
    /// a request that cannot be read or sends a note with no move to keep it on, and
    /// <see cref="ContentErrorCodes.CardMoveUnprocessable"/> when the card to go before is not in the target
    /// column. On any failure nothing is written. A card already exactly there is left alone and its token
    /// stays valid.
    /// </remarks>
    Task<OperationResult<ContentCardDetailServiceModel>> MoveAsync(
        string actorUserId, Guid cardId, MoveContentCardViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a card, and — only when the request says so in so many words — removes its linked picture from
    /// the library.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The card's movement history and recipe package go with it; nothing it pointed at does. Fails with
    /// <see cref="ContentErrorCodes.CardNotFound"/>, <see cref="ContentErrorCodes.CardStale"/>,
    /// <see cref="ContentErrorCodes.CardInvalid"/> for a request that cannot be read or asks to remove a
    /// picture the card does not have, and <see cref="ContentErrorCodes.CardLinkedAssetConflict"/> when the
    /// picture could not be removed as asked.
    /// </para>
    /// <para>
    /// <strong>Must run inside the caller's transaction.</strong> The card's deletion and the picture's
    /// removal are two modules' writes, and it is the facade's keyed, transactional execution that makes a
    /// failure of the second undo the first. This method returns a failure for the transaction to roll back
    /// on; it does not compensate.
    /// </para>
    /// </remarks>
    Task<OperationResult<ContentCardDeletionServiceModel>> DeleteAsync(
        string actorUserId, Guid cardId, DeleteContentCardViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a card on the board for one recipe, pinned to the exact versions the creator approved (13.7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every pin is asked of its owner inside the resolved workspace. One that cannot be found, can no longer
    /// be used, or is not the approved version fails the request with
    /// <see cref="ContentErrorCodes.CardPackageUnprocessable"/> and nothing is created. One that was approved
    /// but whose source has since moved does not fail it: the package is created <em>blocked</em>, with the
    /// reasons — unless the request allows it to stand, which takes the Editor role and is recorded.
    /// </para>
    /// <para>
    /// Nothing is copied. The package is ids and version pins, written with the card, its creation row and its
    /// audit entry in one save.
    /// </para>
    /// </remarks>
    Task<OperationResult<ContentCardDetailServiceModel>> CreateRecipePackageAsync(
        string actorUserId, CreateRecipePackageViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// The card the Content Pipeline made for a piece of work, or a failure carrying
    /// <see cref="ContentErrorCodes.CardNotFound"/> — for a work with no such card, a work that does not exist
    /// and one another workspace owns alike.
    /// </summary>
    Task<OperationResult<ContentCardDetailServiceModel>> GetPipelineCardAsync(
        Guid creativeContextId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a Content Pipeline run on the board: one card linking the piece of work, its recipe, its posts,
    /// its first saved picture and the prompt kept with that picture (13.7a, PIPE-UI-006).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Safe to send again, at any time.</strong> A work that already has its card answers with that
    /// card and writes nothing. Nothing here saves a picture or a prompt — the library's keep route did, once
    /// each — so a repeat cannot duplicate one either.
    /// </para>
    /// <para>
    /// <strong>Everything is read here, from the work.</strong> The request names a piece of work and nothing
    /// else; what the card links is what that work holds now, so a client cannot compose a card that points at
    /// two different things as though they were one run.
    /// </para>
    /// <para>
    /// Fails with <see cref="ContentErrorCodes.CreativeContextNotFound"/> for a work this workspace does not
    /// have live, <see cref="ContentErrorCodes.CardPipelineUnprocessable"/> for a work with no picture saved
    /// to the library, and <see cref="ContentErrorCodes.CardNotSaved"/> when the write did not happen. On any
    /// failure nothing is written.
    /// </para>
    /// </remarks>
    Task<OperationResult<ContentPipelineCardOutcome>> CommitPipelineAsync(
        string actorUserId, Guid creativeContextId, CancellationToken cancellationToken);
}

internal sealed class ContentCardBusiness(
    IContentCardDataLayer dataLayer,
    IRecipeFacade recipes,
    IMediaAssetLookupFacade mediaAssets,
    IMediaAssetFacade mediaLibrary,
    IBrandProfileFacade brandProfiles,
    ITimeZoneConverter zones,
    IContentChannelFacade channels,
    IVocabularyFacade vocabulary,
    IWorkspaceContext workspace,
    IClock clock) : IContentCardBusiness
{
    private const string CannotCreate = "That card could not be created as described.";
    private const string CannotUpdate = "That card could not be edited as described.";
    private const string CannotMove = "That card could not be moved as described.";
    private const string CannotDelete = "That card could not be deleted as described.";
    private const string CannotPackage = "That recipe package could not be put together as described.";
    private const string NotFoundHere = "That record could not be found in this workspace, or can no longer be used.";
    private const string NotApproved = "That is not the approved version.";

    public async Task<OperationResult<ContentCardDetailServiceModel>> CreateAsync(
        string actorUserId, CreateContentCardViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for any caller that reaches Business without the facade's validator — a worker, a
        // plugin. The rules live in one place so the two cannot disagree.
        var shape = ContentCardInputChecks.Create(model).ToList();
        if (shape.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotCreate, shape));
        }

        var channelKey = ContentCardInputChecks.Normalize(model.ChannelKey);
        var cuisineCode = ContentCardInputChecks.Normalize(model.CuisineCode);
        var dishTypeCode = ContentCardInputChecks.Normalize(model.DishTypeCode);
        var methodCode = ContentCardInputChecks.Normalize(model.MethodCode);

        var vocabularyFailures = await VocabularyFailuresAsync(channelKey, cuisineCode, dishTypeCode, methodCode, cancellationToken);
        if (vocabularyFailures.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotCreate, vocabularyFailures));
        }

        var unlinkable = await UnlinkableAsync(Linking(model), cancellationToken);
        if (unlinkable.Count > 0)
        {
            return Failure(LineageRefused(unlinkable));
        }

        var now = clock.UtcNow;
        var column = model.Column ?? ContentCardColumn.ToDo;

        // WorkspaceId is left unset: stamping it is the ownership interceptor's job, from the resolved context,
        // and there is no field a client could send one in. The place in the column is the data layer's to
        // decide, inside the write.
        var card = new ContentCard
        {
            Id = Guid.NewGuid(),
            Title = ContentCardInputChecks.Normalize(model.Title)!,
            Description = ContentCardInputChecks.Normalize(model.Description),
            Column = column,
            ChannelKey = channelKey,
            PlannedDate = model.PlannedDate,

            // The date's own weekday, computed here so the two can never disagree. A calendar date has one
            // weekday whatever the zone, so no conversion is involved.
            PlannedDayOfWeek = model.PlannedDate?.DayOfWeek,
            CuisineCode = cuisineCode,
            DishTypeCode = dishTypeCode,
            MethodCode = methodCode,
            CreativeContextId = model.CreativeContextId,
            RecipeId = model.RecipeId,
            RecipeVersionId = model.RecipeVersionId,
            PromptRecordId = model.PromptRecordId,
            MediaAssetId = model.MediaAssetId,
            MediaAssetVersionNumber = model.MediaAssetVersionNumber,
            SocialPackageId = model.SocialPackageId,

            // From the resolved membership, never from input.
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // The card's first move: it entered its column from nowhere. Every card has exactly one.
        var created = new CardMoveHistory
        {
            Id = Guid.NewGuid(),
            ContentCardId = card.Id,
            Sequence = 1,
            FromColumn = null,
            ToColumn = column,
            ActorMembershipId = workspace.MembershipId,
            OccurredAt = now,
        };

        var saved = await dataLayer.CreateAsync(
            card,
            created,

            // State only: the column and the id. Never the title or the description, which are the creator's.
            new AuditEntry(
                actorUserId,
                ContentAuditActions.ContentCardCreated,
                ContentAuditActions.ContentCardResourceType,
                card.Id.ToString("D"),
                CorrelationId(),
                $"Put a card on the board in {column}."),
            cancellationToken);

        if (!saved)
        {
            // The checks above passed a moment ago, so this is a linked record that stopped being linkable in
            // between, or a transient fault. Asked again so the answer names what changed when something did.
            var gone = await UnlinkableAsync(Linking(model), cancellationToken);

            return Failure(gone.Count > 0
                ? LineageRefused(gone)
                : new OperationError(
                    ContentErrorCodes.CardNotSaved,
                    "The card could not be saved just then. Try again.",
                    new Dictionary<string, string[]>()));
        }

        // Read back rather than assembled from what was staged: the place, the token and the names of what it
        // points at are all facts about the stored row.
        return await GetDetailAsync(card.Id, cancellationToken);
    }

    /// <summary>
    /// The channel and the three cooking codes, each against what is offered now. A retired channel and a code
    /// the vocabulary does not hold are refused: a new card cannot start out naming something nobody can pick.
    /// </summary>
    private async Task<List<(string, string)>> VocabularyFailuresAsync(
        string? channelKey, string? cuisineCode, string? dishTypeCode, string? methodCode, CancellationToken cancellationToken)
    {
        var failures = new List<(string, string)>();

        if (channelKey is not null
            && !channels.List().Any(channel => channel.IsActive && string.Equals(channel.Key, channelKey, StringComparison.Ordinal)))
        {
            failures.Add(("channelKey", "That is not a channel you can choose."));
        }

        if (cuisineCode is not null
            && !(await vocabulary.ListActiveCuisinesAsync(cancellationToken))
                .Any(entry => string.Equals(entry.Code, cuisineCode, StringComparison.Ordinal)))
        {
            failures.Add(("cuisineCode", "That is not a cuisine in the cooking vocabulary."));
        }

        if (dishTypeCode is not null
            && !(await vocabulary.ListActiveCoursesAsync(cancellationToken))
                .Any(entry => string.Equals(entry.Code, dishTypeCode, StringComparison.Ordinal)))
        {
            failures.Add(("dishTypeCode", "That is not a dish type in the cooking vocabulary."));
        }

        if (methodCode is not null
            && !(await vocabulary.ListActiveTechniquesAsync(cancellationToken))
                .Any(entry => string.Equals(entry.Code, methodCode, StringComparison.Ordinal)))
        {
            failures.Add(("methodCode", "That is not a method in the cooking vocabulary."));
        }

        return failures;
    }

    public async Task<OperationResult<ContentCardDetailServiceModel>> UpdateAsync(
        string actorUserId, Guid cardId, UpdateContentCardViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a caller that reaches Business without the facade's validator.
        var shape = ContentCardInputChecks.Update(model).ToList();
        if (shape.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotUpdate, shape));
        }

        var card = await dataLayer.FindForUpdateAsync(cardId, cancellationToken);

        // One refusal for "no such card" and for "that card is another workspace's": the query filter means
        // the other row was never seen.
        if (card is null)
        {
            return Failure(NotFound());
        }

        // Before anything else is judged, so every later answer is about the card the caller actually read.
        if (!ContentCardConcurrencyToken.Matches(model.ExpectedConcurrencyToken, card.RowVersion))
        {
            return Failure(Stale());
        }

        var next = Requested(card, model);

        var pins = PinFailures(card, model, next);
        if (pins.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotUpdate, pins));
        }

        // Only what the patch newly chooses is checked against what is offered now. A channel or a code the
        // card already holds stays valid after it is retired, so an unrelated edit never fails because of it.
        var vocabularyFailures = await VocabularyFailuresAsync(
            next.ChannelKey == card.ChannelKey ? null : next.ChannelKey,
            next.CuisineCode == card.CuisineCode ? null : next.CuisineCode,
            next.DishTypeCode == card.DishTypeCode ? null : next.DishTypeCode,
            next.MethodCode == card.MethodCode ? null : next.MethodCode,
            cancellationToken);
        if (vocabularyFailures.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotUpdate, vocabularyFailures));
        }

        var linking = NewlyLinking(card, next);
        var unlinkable = await UnlinkableAsync(linking, cancellationToken);
        if (unlinkable.Count > 0)
        {
            return Failure(LineageRefused(unlinkable));
        }

        var changed = Apply(card, next);

        // The pipeline's card is the card for one piece of work. Pointed at another, or at none, it is no
        // longer that work's card — so it stops holding the place, and the work can be given one again.
        if (card.Origin is ContentCardOrigin.ContentPipeline && changed.Contains("creativeContextId"))
        {
            card.Origin = ContentCardOrigin.Board;
        }

        // An edit that changes nothing writes nothing: no audit entry, and the token the caller holds stays
        // valid.
        if (changed.Count == 0)
        {
            return await GetDetailAsync(card.Id, cancellationToken);
        }

        card.UpdatedAt = clock.UtcNow;
        card.UpdatedByMembershipId = workspace.MembershipId;

        var write = await dataLayer.SaveAsync(
            card,

            // Which fields, never what they now say: the words are the creator's.
            new AuditEntry(
                actorUserId,
                ContentAuditActions.ContentCardUpdated,
                ContentAuditActions.ContentCardResourceType,
                card.Id.ToString("D"),
                CorrelationId(),
                $"Edited a card: {string.Join(", ", changed)}."),
            cancellationToken);

        switch (write)
        {
            case ContentCardWrite.Saved:
                return await GetDetailAsync(card.Id, cancellationToken);

            case ContentCardWrite.Stale:
                return Failure(Stale());

            default:
                // The checks above passed a moment ago, so this is a newly linked record that stopped being
                // linkable in between, or a transient fault. Asked again so the answer names what changed.
                var gone = await UnlinkableAsync(linking, cancellationToken);

                return Failure(gone.Count > 0
                    ? LineageRefused(gone)
                    : new OperationError(
                        ContentErrorCodes.CardNotSaved,
                        "The card could not be saved just then. Try again.",
                        new Dictionary<string, string[]>()));
        }
    }

    public async Task<OperationResult<ContentCardDetailServiceModel>> MoveAsync(
        string actorUserId, Guid cardId, MoveContentCardViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a caller that reaches Business without the facade's validator.
        var shape = ContentCardInputChecks.Move(model).ToList();
        if (shape.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotMove, shape));
        }

        var card = await dataLayer.FindForUpdateAsync(cardId, cancellationToken);
        if (card is null)
        {
            return Failure(NotFound());
        }

        if (!ContentCardConcurrencyToken.Matches(model.ExpectedConcurrencyToken, card.RowVersion))
        {
            return Failure(Stale());
        }

        var from = card.Column;
        var to = model.Column!.Value;
        var changesColumn = to != from;
        var note = ContentCardInputChecks.Normalize(model.Note);

        // A note is kept on the move it explains, and a new place in the same column is not a move: there
        // would be nowhere to keep it. Refused rather than dropped, because the words are the creator's.
        if (note is not null && !changesColumn)
        {
            return Failure(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotMove,
                [("note", "A note is kept with a move to another column. This card is staying in its column.")]));
        }

        // The target column in the store's own order, which is the only order that breaks a tie between two
        // cards sharing a rank the way every reader of the board will see it broken.
        var column = await dataLayer.ListColumnSlotsAsync(to, cancellationToken);
        var placement = ContentCardPlacer.Place(column, card.Id, model.BeforeCardId);

        switch (placement.Kind)
        {
            // One answer for a card that does not exist, one in another column, the card itself and one that
            // belongs to another workspace: the workspace filter means the last was never in the list at all.
            case ContentCardPlacementKind.TargetNotInColumn:
                return Failure(OperationError.Validation(
                    ContentErrorCodes.CardMoveUnprocessable,
                    "That card cannot be put there.",
                    [("beforeCardId", "That card is not in the target column of this board.")]));

            // Already exactly there: nothing is written, and the token the caller holds stays valid.
            case ContentCardPlacementKind.Unchanged:
                return await GetDetailAsync(card.Id, cancellationToken);

            case ContentCardPlacementKind.Rebalance:
                // No rank is left between the two neighbours, so the column is re-spaced in its current order.
                // Each of these cards is written with its own row version: if one was edited since it was
                // read, the whole save is refused and nothing moves.
                var others = await dataLayer.ListColumnForUpdateAsync(to, card.Id, cancellationToken);

                // These were read after the plan was made, and carry row versions as of now. If the column is
                // not exactly as it was planned around — a card arrived, a card left, a card was given a new
                // place — writing the plan would overwrite somebody's move without any conflict being raised.
                // The move is asked again instead, and that is decided before any rank is touched, so a
                // refusal leaves nothing changed in this unit of work.
                if (!ContentCardPlacer.StillAsPlanned(
                        column, card.Id, [.. others.Select(other => new ContentCardSlot(other.Id, other.SortRank))]))
                {
                    return Failure(Stale());
                }

                foreach (var other in others)
                {
                    other.SortRank = placement.Respaced[other.Id];
                }

                break;
        }

        var now = clock.UtcNow;

        card.Column = to;
        card.SortRank = placement.SortRank;
        card.UpdatedAt = now;
        card.UpdatedByMembershipId = workspace.MembershipId;

        CardMoveHistory? entered = null;
        AuditEntry? audit = null;

        // Only a change of column is a move. A new place in the same column records nothing here, so the
        // history never claims a transition that did not happen.
        if (changesColumn)
        {
            // The creation row is sequence one. A card with no history at all — none is written without one
            // by this seam, but a row can predate it — still starts its moves at two.
            var last = await dataLayer.LastMoveSequenceAsync(card.Id, cancellationToken) ?? 1;

            entered = new CardMoveHistory
            {
                Id = Guid.NewGuid(),
                ContentCardId = card.Id,
                Sequence = last + 1,
                FromColumn = from,
                ToColumn = to,
                Note = note,

                // From the resolved membership, never from input.
                ActorMembershipId = workspace.MembershipId,
                OccurredAt = now,
            };

            // The two columns and the id. Never the note, which is the creator's own words.
            audit = new AuditEntry(
                actorUserId,
                ContentAuditActions.ContentCardMoved,
                ContentAuditActions.ContentCardResourceType,
                card.Id.ToString("D"),
                CorrelationId(),
                $"Moved a card from {from} to {to}.",
                BeforeReference: from.ToString(),
                AfterReference: to.ToString());
        }

        // Anything but a clean save means another writer got there first: the card itself, a neighbour that
        // was being re-spaced, or a second move of this card taking the same place in its history. Nothing was
        // written, and the answer is the same — read the board again.
        return await dataLayer.SaveMoveAsync(card, entered, audit, cancellationToken) is ContentCardWrite.Saved
            ? await GetDetailAsync(card.Id, cancellationToken)
            : Failure(Stale());
    }

    public async Task<OperationResult<ContentCardDeletionServiceModel>> DeleteAsync(
        string actorUserId, Guid cardId, DeleteContentCardViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a caller that reaches Business without the facade's validator.
        var shape = ContentCardInputChecks.Delete(model).ToList();
        if (shape.Count > 0)
        {
            return Refused(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotDelete, shape));
        }

        var card = await dataLayer.FindForUpdateAsync(cardId, cancellationToken);
        if (card is null)
        {
            return Refused(NotFound());
        }

        // Checked before anything is decided about the picture: a caller quoting a stale token has not seen
        // what the card points at now.
        if (!ContentCardConcurrencyToken.Matches(model.ExpectedConcurrencyToken, card.RowVersion))
        {
            return Refused(Stale());
        }

        var removePicture = model.LinkedAsset is ContentCardLinkedAssetAction.Remove;
        var assetId = card.MediaAssetId;

        // Asked to remove a picture the card does not have. Refused rather than treated as nothing to do: the
        // creator believes something is being removed, and should be told it is not.
        if (removePicture && assetId is null)
        {
            return Refused(OperationError.Validation(
                ContentErrorCodes.CardInvalid,
                CannotDelete,
                [("linkedAsset", "This card has no linked picture to remove.")]));
        }

        // PUBLISHING POLICY (decided with 13.6c, enforced when publication records exist — Phase 13.9 on):
        // a card with a publication that is queued or publishing must be refused here, because deleting it
        // would take the plan out from under a delivery in flight. Delivered and failed publications do not
        // block a deletion. There is no publication record to ask about yet, so there is nothing to check.

        // Read before the card goes, because afterwards there is nothing to count them on.
        var moveCount = Math.Max(0, (await dataLayer.LastMoveSequenceAsync(card.Id, cancellationToken) ?? 1) - 1);
        var hadPackage = await dataLayer.HasPackageAsync(card.Id, cancellationToken);
        var otherCards = assetId is { } linked
            ? await dataLayer.CountOtherCardsLinkingAssetAsync(linked, card.Id, cancellationToken)
            : 0;
        var column = card.Column;
        var now = clock.UtcNow;

        var write = await dataLayer.DeleteAsync(
            card,

            // What remains of the card once its history has gone with it: where it was, how often it had moved,
            // whether a package went with it, and what was done about its picture. Ids and states only — never
            // its title, its description or a note. The picture's removal is in the same transaction as this
            // entry, so an entry that says "removed" is only ever committed when it was.
            new AuditEntry(
                actorUserId,
                ContentAuditActions.ContentCardDeleted,
                ContentAuditActions.ContentCardResourceType,
                cardId.ToString("D"),
                CorrelationId(),
                $"Deleted a card from {column} that had moved {moveCount} time(s)"
                    + (hadPackage ? ", with its recipe package" : string.Empty)
                    + (assetId is not { } picture
                        ? ". It had no linked picture."
                        : removePicture
                            ? $". Its linked picture {picture:D} was removed from the library with it."
                            : $". Its linked picture {picture:D} was kept."),
                BeforeReference: column.ToString(),
                AfterReference: assetId is null ? "picture:none" : removePicture ? "picture:removed" : "picture:kept"),
            cancellationToken);

        if (write is not ContentCardWrite.Saved)
        {
            // Stale for a card that changed or went first; anything else is the store refusing, and safe to retry.
            return Refused(write is ContentCardWrite.Stale
                ? Stale()
                : new OperationError(
                    ContentErrorCodes.CardNotSaved,
                    "The card could not be deleted just then. Try again.",
                    new Dictionary<string, string[]>()));
        }

        // Kept unless the request said otherwise, in so many words. This is the only line that can take
        // anything out of the library, and it is reached only on an explicit Remove.
        if (!removePicture)
        {
            return OperationResult<ContentCardDeletionServiceModel>.Success(new ContentCardDeletionServiceModel(
                cardId,
                column,
                moveCount,
                hadPackage,
                now,
                new ContentCardLinkedAssetServiceModel(
                    assetId is null ? ContentCardLinkedAssetOutcome.None : ContentCardLinkedAssetOutcome.Kept,
                    assetId,
                    AlreadyRemoved: false,
                    otherCards,
                    Affected: null)));
        }

        // Through the library's own facade, so its rules are the rules: its role bar, its token, its
        // tombstone that deletes no bytes, and its report of what else points at the picture.
        var removal = await mediaLibrary.SoftDeleteConfirmedAsync(
            actorUserId, assetId!.Value, model.LinkedAssetConcurrencyToken, cancellationToken);

        // The picture could not be removed as asked. Returned as a failure so the transaction this runs in
        // undoes the card's deletion too: the creator asked for both, and gets both or neither.
        if (!removal.Succeeded)
        {
            return Refused(new OperationError(
                ContentErrorCodes.CardLinkedAssetConflict,
                "The linked picture could not be removed as asked, so nothing was deleted. Read the picture again and decide again.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<ContentCardDeletionServiceModel>.Success(new ContentCardDeletionServiceModel(
            cardId,
            column,
            moveCount,
            hadPackage,
            now,
            new ContentCardLinkedAssetServiceModel(
                ContentCardLinkedAssetOutcome.Removed,
                assetId,
                removal.Value!.AlreadyDeleted,
                otherCards,

                // Counted, not named: the library's report carries recipe titles, and this answer is stored
                // against the idempotency key.
                new ContentCardAffectedContentServiceModel(
                    removal.Value.Affected.RecipeCount,
                    removal.Value.Affected.BrandProfileCount,
                    removal.Value.Affected.TestAttachmentCount,
                    removal.Value.Affected.Any))));

        static OperationResult<ContentCardDeletionServiceModel> Refused(OperationError error) =>
            OperationResult<ContentCardDeletionServiceModel>.Failure(error);
    }

    /// <summary>The card as the patch asks for it: each field as submitted, or as the card has it.</summary>
    private static CardFields Requested(ContentCard card, UpdateContentCardViewModel model) => new(
        model.Title.IsSubmitted ? ContentCardInputChecks.Normalize(model.Title.Value)! : card.Title,
        model.Description.IsSubmitted ? ContentCardInputChecks.Normalize(model.Description.Value) : card.Description,
        model.ChannelKey.IsSubmitted ? ContentCardInputChecks.Normalize(model.ChannelKey.Value) : card.ChannelKey,
        model.PlannedDate.Or(card.PlannedDate),
        model.CuisineCode.IsSubmitted ? ContentCardInputChecks.Normalize(model.CuisineCode.Value) : card.CuisineCode,
        model.DishTypeCode.IsSubmitted ? ContentCardInputChecks.Normalize(model.DishTypeCode.Value) : card.DishTypeCode,
        model.MethodCode.IsSubmitted ? ContentCardInputChecks.Normalize(model.MethodCode.Value) : card.MethodCode,
        model.CreativeContextId.Or(card.CreativeContextId),
        model.RecipeId.Or(card.RecipeId),
        model.RecipeVersionId.Or(card.RecipeVersionId),
        model.PromptRecordId.Or(card.PromptRecordId),
        model.MediaAssetId.Or(card.MediaAssetId),
        model.MediaAssetVersionNumber.Or(card.MediaAssetVersionNumber),
        model.SocialPackageId.Or(card.SocialPackageId));

    /// <summary>
    /// The version-pin rules. A pin is a pin on one record, so it cannot outlive that record on the card — and
    /// it is never dropped as a side effect of changing something else.
    /// </summary>
    /// <remarks>
    /// When a patch clears or replaces a recipe or a picture that the card pins a version of, the pin has to be
    /// answered for in the same patch: a version of the new record, or <c>null</c> to drop it. A patch that
    /// leaves the pin's field out is refused, because the only things it could do with the pin are keep one
    /// that now points at the wrong record, or remove one nobody asked to remove.
    /// </remarks>
    private static List<(string, string)> PinFailures(ContentCard card, UpdateContentCardViewModel model, CardFields next)
    {
        var failures = new List<(string, string)>();

        if (next.RecipeId != card.RecipeId && card.RecipeVersionId is not null && !model.RecipeVersionId.IsSubmitted)
        {
            failures.Add((
                "recipeVersionId",
                "This card is pinned to a version of its current recipe. Send recipeVersionId as well: a version of the new recipe, or null to drop the pin."));
        }
        else if (next.RecipeVersionId is not null && next.RecipeId is null)
        {
            failures.Add(("recipeVersionId", "Name the recipe this is a version of."));
        }

        if (next.MediaAssetId != card.MediaAssetId
            && card.MediaAssetVersionNumber is not null
            && !model.MediaAssetVersionNumber.IsSubmitted)
        {
            failures.Add((
                "mediaAssetVersionNumber",
                "This card is pinned to a version of its current picture. Send mediaAssetVersionNumber as well: a version of the new picture, or null to drop the pin."));
        }
        else if (next.MediaAssetVersionNumber is not null && next.MediaAssetId is null)
        {
            failures.Add(("mediaAssetVersionNumber", "Name the picture this is a version of."));
        }

        return failures;
    }

    /// <summary>
    /// What the patch links that the card did not already link, which is all that needs asking about. A record
    /// the card already points at is not re-checked: it may have been archived since, and an edit to the title
    /// must not fail because of it.
    /// </summary>
    private static LineageToCheck NewlyLinking(ContentCard card, CardFields next)
    {
        var recipeChanged = next.RecipeId != card.RecipeId;
        var versionChanged = recipeChanged || next.RecipeVersionId != card.RecipeVersionId;

        // A picture is asked about when it is newly linked, or newly pinned at a version. Dropping a pin links
        // nothing new, so it is not asked about: a pin on a picture that has since been removed must still be
        // droppable, as a recipe's is.
        var assetChanged = next.MediaAssetId != card.MediaAssetId
            || (next.MediaAssetVersionNumber is not null && next.MediaAssetVersionNumber != card.MediaAssetVersionNumber);
        var contextChanged = next.CreativeContextId != card.CreativeContextId;

        // The posts and the piece of work have to agree, so a change to either asks about the pair.
        var packageChanged = next.SocialPackageId != card.SocialPackageId || (contextChanged && next.CreativeContextId is not null);

        return new LineageToCheck(
            contextChanged ? next.CreativeContextId : null,
            recipeChanged ? next.RecipeId : null,
            versionChanged ? next.RecipeId : null,
            versionChanged ? next.RecipeVersionId : null,
            next.PromptRecordId != card.PromptRecordId ? next.PromptRecordId : null,
            assetChanged ? next.MediaAssetId : null,
            assetChanged ? next.MediaAssetVersionNumber : null,
            packageChanged ? next.SocialPackageId : null,
            packageChanged ? next.CreativeContextId : null);
    }

    /// <summary>Writes the requested fields onto the card and names the ones that changed.</summary>
    private static List<string> Apply(ContentCard card, CardFields next)
    {
        var changed = new List<string>();

        void Set<T>(string name, T current, T requested, Action<T> assign)
        {
            if (!EqualityComparer<T>.Default.Equals(current, requested))
            {
                assign(requested);
                changed.Add(name);
            }
        }

        Set("title", card.Title, next.Title, value => card.Title = value);
        Set("description", card.Description, next.Description, value => card.Description = value);
        Set("channelKey", card.ChannelKey, next.ChannelKey, value => card.ChannelKey = value);
        Set("plannedDate", card.PlannedDate, next.PlannedDate, value =>
        {
            card.PlannedDate = value;

            // The date's own weekday, set with it and cleared with it, so the two can never disagree.
            card.PlannedDayOfWeek = value?.DayOfWeek;
        });
        Set("cuisineCode", card.CuisineCode, next.CuisineCode, value => card.CuisineCode = value);
        Set("dishTypeCode", card.DishTypeCode, next.DishTypeCode, value => card.DishTypeCode = value);
        Set("methodCode", card.MethodCode, next.MethodCode, value => card.MethodCode = value);
        Set("creativeContextId", card.CreativeContextId, next.CreativeContextId, value => card.CreativeContextId = value);
        Set("recipeId", card.RecipeId, next.RecipeId, value => card.RecipeId = value);
        Set("recipeVersionId", card.RecipeVersionId, next.RecipeVersionId, value => card.RecipeVersionId = value);
        Set("promptRecordId", card.PromptRecordId, next.PromptRecordId, value => card.PromptRecordId = value);
        Set("mediaAssetId", card.MediaAssetId, next.MediaAssetId, value => card.MediaAssetId = value);
        Set(
            "mediaAssetVersionNumber",
            card.MediaAssetVersionNumber,
            next.MediaAssetVersionNumber,
            value => card.MediaAssetVersionNumber = value);
        Set("socialPackageId", card.SocialPackageId, next.SocialPackageId, value => card.SocialPackageId = value);

        return changed;
    }

    /// <summary>Everything a create links: all of it is new, so all of it is asked about.</summary>
    private static LineageToCheck Linking(CreateContentCardViewModel model) => new(
        model.CreativeContextId,
        model.RecipeId,
        model.RecipeId,
        model.RecipeVersionId,
        model.PromptRecordId,
        model.MediaAssetId,
        model.MediaAssetVersionNumber,
        model.SocialPackageId,
        model.CreativeContextId);

    /// <summary>
    /// The lineage fields naming a record this workspace cannot link, each asked of the record's owner inside
    /// the resolved workspace. Empty when every named record can be linked.
    /// </summary>
    /// <remarks>
    /// A record that does not exist and one another workspace owns are the same answer from every owner asked
    /// here, so they are the same refusal — a card cannot be used to learn that a neighbour's record exists
    /// (tenancy.md). The composite keys would refuse a foreign id too; this is the answer in words.
    /// </remarks>
    private async Task<List<string>> UnlinkableAsync(LineageToCheck lineage, CancellationToken cancellationToken)
    {
        var refused = new List<string>();

        var own = await dataLayer.FindOwnLineageAsync(
            lineage.CreativeContextId,
            lineage.PromptRecordId,
            lineage.SocialPackageId,
            lineage.SocialPackageContextId,
            cancellationToken);

        if (!own.CreativeContext)
        {
            refused.Add("creativeContextId");
        }

        var recipeUsable = true;
        if (lineage.RecipeId is { } recipeId)
        {
            var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);
            recipeUsable = recipe.Succeeded && recipe.Value!.Status is not RecipeStatus.Archived;

            if (!recipeUsable)
            {
                refused.Add("recipeId");
            }
        }

        // Asked only of a recipe that can be linked: a version of one that cannot is not a second fault.
        if (recipeUsable
            && lineage.RecipeVersionId is { } versionId
            && lineage.RecipeOfVersionId is { } versionOf
            && !(await recipes.GetSnapshotAsync(versionOf, versionId, cancellationToken)).Succeeded)
        {
            refused.Add("recipeVersionId");
        }

        if (!own.PromptRecord)
        {
            refused.Add("promptRecordId");
        }

        // A removed picture and a version the picture does not have both answer "not linkable".
        if (lineage.MediaAssetId is { } assetId
            && await mediaAssets.ResolveLinkTargetAsync(assetId, lineage.MediaAssetVersionNumber, cancellationToken)
                is not MediaAssetLinkTarget.Linkable)
        {
            refused.Add(lineage.MediaAssetVersionNumber is null ? "mediaAssetId" : "mediaAssetVersionNumber");
        }

        if (!own.SocialPackage)
        {
            refused.Add("socialPackageId");
        }

        return refused;
    }

    /// <summary>
    /// The records to ask about before a write. A null means "not being linked by this write", and is not
    /// asked about.
    /// </summary>
    /// <param name="RecipeId">The recipe to check is usable, when it is newly linked.</param>
    /// <param name="RecipeOfVersionId">The recipe <paramref name="RecipeVersionId"/> must be a version of.</param>
    /// <param name="SocialPackageContextId">The piece of work the posts must belong to, when the card names one.</param>
    private sealed record LineageToCheck(
        Guid? CreativeContextId,
        Guid? RecipeId,
        Guid? RecipeOfVersionId,
        Guid? RecipeVersionId,
        Guid? PromptRecordId,
        Guid? MediaAssetId,
        int? MediaAssetVersionNumber,
        Guid? SocialPackageId,
        Guid? SocialPackageContextId);

    /// <summary>A card's editable fields, as one value.</summary>
    private sealed record CardFields(
        string Title,
        string? Description,
        string? ChannelKey,
        DateOnly? PlannedDate,
        string? CuisineCode,
        string? DishTypeCode,
        string? MethodCode,
        Guid? CreativeContextId,
        Guid? RecipeId,
        Guid? RecipeVersionId,
        Guid? PromptRecordId,
        Guid? MediaAssetId,
        int? MediaAssetVersionNumber,
        Guid? SocialPackageId);

    private static OperationError NotFound() => new(
        ContentErrorCodes.CardNotFound, "That card could not be found.", new Dictionary<string, string[]>());

    private static OperationError Stale() => new(
        ContentErrorCodes.CardStale,
        "This card has changed since you opened it. Reload it and try again.",
        new Dictionary<string, string[]>());

    private static OperationError LineageRefused(IReadOnlyList<string> fields) => OperationError.Validation(
        ContentErrorCodes.CardLineageUnprocessable,
        "That card points at something that cannot be linked.",
        fields.Select(field => (field, "That record could not be found in this workspace, or can no longer be used.")));

    private static OperationResult<ContentCardDetailServiceModel> Failure(OperationError error) =>
        OperationResult<ContentCardDetailServiceModel>.Failure(error);

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    public async Task<OperationResult<ContentCardDetailServiceModel>> GetPipelineCardAsync(
        Guid creativeContextId, CancellationToken cancellationToken) =>
        await dataLayer.FindPipelineCardIdAsync(creativeContextId, cancellationToken) is { } cardId
            ? await GetDetailAsync(cardId, cancellationToken)
            : Failure(NotFound());

    public async Task<OperationResult<ContentPipelineCardOutcome>> CommitPipelineAsync(
        string actorUserId, Guid creativeContextId, CancellationToken cancellationToken)
    {
        // Asked first, and before anything about the work is judged: a commit whose answer was lost must be
        // told about the card it made, whatever has happened to the work's pictures since.
        if (await ExistingPipelineCardAsync(creativeContextId, cancellationToken) is { } already)
        {
            return already;
        }

        var source = await dataLayer.FindPipelineSourceAsync(creativeContextId, cancellationToken);

        // Unknown, archived and another workspace's: the query filter and the live-work rule give one answer.
        if (source is null)
        {
            return PipelineFailure(new OperationError(
                ContentErrorCodes.CreativeContextNotFound,
                "That creative context could not be found.",
                new Dictionary<string, string[]>()));
        }

        // The first kept picture the library still holds. One that has since been removed is passed over
        // rather than refused: the run still has the others.
        PipelineKeeperRecord? keeper = null;
        foreach (var candidate in source.Keepers)
        {
            if (await mediaAssets.ResolveLinkTargetAsync(
                    candidate.MediaAssetId, candidate.MediaAssetVersionNumber, cancellationToken)
                is MediaAssetLinkTarget.Linkable)
            {
                keeper = candidate;
                break;
            }
        }

        if (keeper is null)
        {
            return PipelineFailure(OperationError.Validation(
                ContentErrorCodes.CardPipelineUnprocessable,
                "This run cannot be put on the board yet.",
                [("keepers", "Save at least one of the pictures you kept to your library first.")]));
        }

        // The recipe is linked when it can be, and left off when it cannot: an archived recipe is not a reason
        // to keep finished pictures and posts off the board.
        Guid? recipeId = null;
        Guid? recipeVersionId = null;
        string? recipeTitle = null;
        if (source.RecipeId is { } namedRecipe)
        {
            var recipe = await recipes.GetDetailAsync(namedRecipe, cancellationToken);
            if (recipe.Succeeded && recipe.Value!.Status is not RecipeStatus.Archived)
            {
                recipeId = namedRecipe;
                recipeTitle = recipe.Value.Title;

                if (source.RecipeVersionId is { } pinned
                    && (await recipes.GetSnapshotAsync(namedRecipe, pinned, cancellationToken)).Succeeded)
                {
                    recipeVersionId = pinned;
                }
            }
        }

        var now = clock.UtcNow;

        var card = new ContentCard
        {
            Id = Guid.NewGuid(),
            Title = PipelineCardTitle(source.WorkingTitle, recipeTitle),
            Column = ContentCardColumn.ToDo,
            Origin = ContentCardOrigin.ContentPipeline,
            CreativeContextId = creativeContextId,
            RecipeId = recipeId,
            RecipeVersionId = recipeVersionId,

            // The prompt the library kept with that picture, when it kept one. Read, never written here.
            PromptRecordId = await dataLayer.FindPromptForAssetAsync(keeper.MediaAssetId, cancellationToken),
            MediaAssetId = keeper.MediaAssetId,
            MediaAssetVersionNumber = keeper.MediaAssetVersionNumber,
            SocialPackageId = source.SocialPackageId,
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var saved = await dataLayer.CreateAsync(
            card,
            new CardMoveHistory
            {
                Id = Guid.NewGuid(),
                ContentCardId = card.Id,
                Sequence = 1,
                FromColumn = null,
                ToColumn = card.Column,
                ActorMembershipId = workspace.MembershipId,
                OccurredAt = now,
            },

            // State only, as for any card: never the title.
            new AuditEntry(
                actorUserId,
                ContentAuditActions.ContentCardCreated,
                ContentAuditActions.ContentCardResourceType,
                card.Id.ToString("D"),
                CorrelationId(),
                $"Put a Content Pipeline run on the board in {card.Column}."),
            cancellationToken);

        if (saved)
        {
            var detail = await GetDetailAsync(card.Id, cancellationToken);

            return detail.Succeeded
                ? OperationResult<ContentPipelineCardOutcome>.Success(new ContentPipelineCardOutcome(detail.Value!, Created: true))
                : PipelineFailure(detail.Error!);
        }

        // Nothing was written. Either another commit for this work landed first — the store lets one through
        // — and its card is the answer, or the write simply failed and is safe to send again.
        return await ExistingPipelineCardAsync(creativeContextId, cancellationToken)
            ?? PipelineFailure(new OperationError(
                ContentErrorCodes.CardNotSaved,
                "The card could not be saved just then. Try again.",
                new Dictionary<string, string[]>()));
    }

    /// <summary>The work's pipeline card as a commit's answer, or null when it has none.</summary>
    private async Task<OperationResult<ContentPipelineCardOutcome>?> ExistingPipelineCardAsync(
        Guid creativeContextId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindPipelineCardIdAsync(creativeContextId, cancellationToken) is not { } cardId)
        {
            return null;
        }

        var detail = await GetDetailAsync(cardId, cancellationToken);

        // A card deleted between the two reads is a card the work no longer has.
        return detail.Succeeded
            ? OperationResult<ContentPipelineCardOutcome>.Success(new ContentPipelineCardOutcome(detail.Value!, Created: false))
            : null;
    }

    /// <summary>
    /// The card's title: the creator's own name for the work, else the recipe's, else a plain label. Their
    /// words, cut to fit and never composed from a prompt or a post.
    /// </summary>
    private static string PipelineCardTitle(string? workingTitle, string? recipeTitle)
    {
        var title = ContentCardInputChecks.Normalize(workingTitle)
            ?? ContentCardInputChecks.Normalize(recipeTitle)
            ?? "Content Pipeline run";

        return title.Length <= ContentCardPolicy.TitleMaxLength ? title : title[..ContentCardPolicy.TitleMaxLength].TrimEnd();
    }

    private static OperationResult<ContentPipelineCardOutcome> PipelineFailure(OperationError error) =>
        OperationResult<ContentPipelineCardOutcome>.Failure(error);

    public async Task<ContentCardPageServiceModel> ListAsync(
        ContentCardListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (rows, hasMore, counts) = await dataLayer.ListAsync(criteria, cancellationToken);

        // The cursor is minted here, not in the repository: it is bound to the workspace and filters it was
        // issued for, and a repository is handed a predicate rather than a route.
        var page = PageBuilder.Build(rows, hasMore, criteria.Scope, ToSummary);

        return new ContentCardPageServiceModel(page.Items, page.NextCursor, ColumnCounts(criteria.Filters, counts));
    }

    public async Task<OperationResult<ContentCardDetailServiceModel>> GetDetailAsync(
        Guid cardId, CancellationToken cancellationToken)
    {
        var record = await dataLayer.GetDetailAsync(cardId, cancellationToken);

        // One refusal for "no such card" and for "that card is another workspace's", because by the time the
        // answer is null those were never two conditions here. Nothing names a field: the id came from the route.
        if (record is null)
        {
            return Failure(NotFound());
        }

        return OperationResult<ContentCardDetailServiceModel>.Success(new ContentCardDetailServiceModel(
            ToSummary(record.Summary),
            record.Description,
            await LineageAsync(record, cancellationToken),
            [.. record.Moves.Select(move => new CardMoveServiceModel(
                move.Sequence, move.FromColumn, move.ToColumn, move.Note, move.OccurredAt))],
            record.MovesTruncated,
            record.Package is null ? null : await ToPackageAsync(record.Package, cancellationToken)));
    }

    /// <summary>
    /// Every column the request could match, in board order, each with its count — zero where the filters
    /// leave a column empty, so a board can head all four without inferring the missing ones.
    /// </summary>
    private static IReadOnlyList<ContentCardColumnCountServiceModel> ColumnCounts(
        ContentCardFilters filters, IReadOnlyList<ContentCardColumnCountRecord> counts)
    {
        var columns = filters.Column is { } only ? [only] : Enum.GetValues<ContentCardColumn>();

        return [.. columns
            .OrderBy(column => (int)column)
            .Select(column => new ContentCardColumnCountServiceModel(
                column, counts.FirstOrDefault(row => row.Column == column)?.Count ?? 0))];
    }

    /// <summary>
    /// The records the card points at, in a fixed order, each with the name this route can read for it.
    /// </summary>
    /// <remarks>
    /// A recipe and a picture belong to other modules and are asked for through their facades, inside the
    /// resolved workspace. Either may have been archived or removed since the card was linked; the card then
    /// still says what it pointed at and marks it unavailable, rather than failing to open or pretending the
    /// record is there. The work, the prompt and the posts are this module's and are held by restricted keys,
    /// so they are there for as long as the card names them.
    /// </remarks>
    private async Task<IReadOnlyList<ContentCardLineageServiceModel>> LineageAsync(
        ContentCardDetailRecord record, CancellationToken cancellationToken)
    {
        var card = record.Summary;
        var lineage = new List<ContentCardLineageServiceModel>();

        if (card.CreativeContextId is { } contextId)
        {
            lineage.Add(new(ContentCardLineageKind.CreativeContext, contextId, null, null, record.CreativeContextTitle, Available: true));
        }

        if (card.RecipeId is { } recipeId)
        {
            var title = (await recipes.ListTitlesAsync([recipeId], cancellationToken))
                .FirstOrDefault(candidate => candidate.Id == recipeId)?.Title;

            lineage.Add(new(ContentCardLineageKind.Recipe, recipeId, card.RecipeVersionId, null, title, Available: title is not null));
        }

        if (card.PromptRecordId is { } promptId)
        {
            lineage.Add(new(ContentCardLineageKind.PromptRecord, promptId, null, null, record.PromptLabel, Available: true));
        }

        if (card.MediaAssetId is { } assetId)
        {
            // Asked only whether it can still be read, at the pinned version when there is one. The picture
            // itself — its bytes, its address — is the media routes' to serve, never this one's.
            var described = await mediaAssets.DescribeAsync(assetId, card.MediaAssetVersionNumber, cancellationToken);

            lineage.Add(new(
                ContentCardLineageKind.MediaAsset, assetId, null, card.MediaAssetVersionNumber, null, Available: described is not null));
        }

        if (card.SocialPackageId is { } packageId)
        {
            lineage.Add(new(ContentCardLineageKind.SocialPackage, packageId, null, null, null, Available: true));
        }

        return lineage;
    }

    private static ContentCardSummaryServiceModel ToSummary(ContentCardSummaryRecord card) => new(
        card.Id,
        card.Title,
        card.DescriptionPreview,
        card.DescriptionLength,
        card.Column,
        card.ChannelKey,
        card.PlannedDate,
        card.PlannedDayOfWeek,
        card.CuisineCode,
        card.DishTypeCode,
        card.MethodCode,
        new ContentCardLineageIdsServiceModel(
            card.CreativeContextId,
            card.RecipeId,
            card.RecipeVersionId,
            card.PromptRecordId,
            card.MediaAssetId,
            card.MediaAssetVersionNumber,
            card.SocialPackageId),

        // The creation row is sequence one, so the moves since are everything after it. A card with no
        // history at all — none is written by this prompt's seam — has moved zero times, not minus one.
        MoveCount: Math.Max(0, (card.LastMoveSequence ?? 1) - 1),
        card.HasPackage,

        // Null until publication records exist. A card says where the creator's work stands and nothing about
        // delivery, so there is no honest value to put here yet.
        Publishing: null,
        card.CreatedAt,
        card.UpdatedAt,
        ContentCardConcurrencyToken.From(card.RowVersion));

    public async Task<OperationResult<ContentCardDetailServiceModel>> CreateRecipePackageAsync(
        string actorUserId, CreateRecipePackageViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for a caller that reaches Business without the facade's validator.
        var shape = RecipePackageInputChecks.Create(model).ToList();
        if (shape.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotPackage, shape));
        }

        var allow = model.AllowNeedsReview is true;

        // Letting a package stand although a source has moved is the one decision here that takes more than
        // the role that may plan work: it is the same bar as deleting a card.
        if (allow && workspace.Role < WorkspaceRole.Editor)
        {
            return Failure(new OperationError(
                ContentErrorCodes.CardForbidden,
                "Only an Editor or an Owner can allow a package to stand when a source needs review.",
                new Dictionary<string, string[]>()));
        }

        var recipeId = model.RecipeId!.Value;
        var versionId = model.RecipeVersionId!.Value;
        var channelInputs = (model.Channels ?? [])
            .Select(channel => (Key: ContentCardInputChecks.Normalize(channel!.ChannelKey)!, channel.SocialRevisionId))
            .ToList();
        var assetInputs = (model.Assets ?? []).Select(asset => (Id: asset!.MediaAssetId!.Value, Version: asset.VersionNumber!.Value)).ToList();

        // A channel is somewhere the creator can publish now; a retired one cannot be newly chosen.
        var catalogue = channels.List();
        var unknownChannels = channelInputs
            .Select((channel, index) => (channel.Key, index))
            .Where(entry => !catalogue.Any(known => known.IsActive && string.Equals(known.Key, entry.Key, StringComparison.Ordinal)))
            .Select(entry => ($"channels[{entry.index}].channelKey", "That is not a channel you can choose."))
            .ToList();
        if (unknownChannels.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardInvalid, CannotPackage, unknownChannels));
        }

        var refused = new List<(string, string)>();

        // ---- The recipe and the exact version of it ----
        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);
        var recipeUsable = recipe.Succeeded && recipe.Value!.Status is not RecipeStatus.Archived;
        var currentVersion = recipeUsable ? recipe.Value!.CurrentVersion : null;

        if (!recipeUsable)
        {
            refused.Add(("recipeId", NotFoundHere));
        }
        else if (!(await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken)).Succeeded)
        {
            refused.Add(("recipeVersionId", NotFoundHere));
        }

        // A recipe that cannot say which version is current cannot have any version checked against it, so
        // nothing about this pin can be verified. Refused rather than waved through unblocked.
        else if (currentVersion is null)
        {
            refused.Add(("recipeVersionId", NotApproved));
        }

        // Approved means what it means for an export: the recipe is approved, or this version was marked
        // ready. Only the current version can be asked; an older one is a source that has moved, which is a
        // reason to block the package rather than to refuse it.
        else if (currentVersion.Id == versionId
                 && recipe.Value!.Status is not RecipeStatus.Approved
                 && currentVersion.Readiness is not RecipeVersionReadiness.Ready)
        {
            refused.Add(("recipeVersionId", NotApproved));
        }

        // ---- The piece of work, when one is named ----
        if (model.CreativeContextId is { } contextId
            && !(await dataLayer.FindOwnLineageAsync(contextId, null, null, null, cancellationToken)).CreativeContext)
        {
            refused.Add(("creativeContextId", NotFoundHere));
        }

        // ---- The editorial revision: this recipe's, and the approved one ----
        EditorialPinRecord? editorial = null;
        if (model.EditorialRevisionId is { } editorialRevisionId)
        {
            editorial = await dataLayer.FindEditorialPinAsync(editorialRevisionId, cancellationToken);

            // Another recipe's editorial is as absent from this package as one that does not exist.
            if (editorial is null || editorial.RecipeId != recipeId || editorial.Kind != ContentPackageKind.Editorial)
            {
                refused.Add(("editorialRevisionId", NotFoundHere));
                editorial = null;
            }
            else if (editorial.AcceptedRevisionId != editorialRevisionId)
            {
                refused.Add(("editorialRevisionId", NotApproved));
            }
        }

        // ---- Each pinned post: that channel's, of that piece of work, and the approved one ----
        var socialPins = await dataLayer.FindSocialPinsAsync(
            [.. channelInputs.Where(channel => channel.SocialRevisionId is not null).Select(channel => channel.SocialRevisionId!.Value)],
            cancellationToken);
        var pinByChannel = new Dictionary<string, SocialPinRecord>(StringComparer.Ordinal);

        for (var index = 0; index < channelInputs.Count; index++)
        {
            if (channelInputs[index].SocialRevisionId is not { } revisionId)
            {
                continue;
            }

            var pin = socialPins.FirstOrDefault(candidate => candidate.RevisionId == revisionId);

            // A post written about a different recipe is not this package's post, whatever piece of work it
            // sits in. One written about no recipe in particular is allowed: a creator may write a post by
            // hand for work that names none.
            if (pin is null
                || pin.CreativeContextId != model.CreativeContextId
                || !string.Equals(pin.ChannelKey, channelInputs[index].Key, StringComparison.Ordinal)
                || (pin.RevisionRecipeId is { } writtenAbout && writtenAbout != recipeId))
            {
                refused.Add(($"channels[{index}].socialRevisionId", NotFoundHere));
            }
            else if (pin.AcceptedRevisionId != revisionId)
            {
                refused.Add(($"channels[{index}].socialRevisionId", NotApproved));
            }
            else
            {
                pinByChannel[channelInputs[index].Key] = pin;
            }
        }

        // ---- Each picture, at the exact version ----
        for (var index = 0; index < assetInputs.Count; index++)
        {
            if (await mediaAssets.ResolveLinkTargetAsync(assetInputs[index].Id, assetInputs[index].Version, cancellationToken)
                is not MediaAssetLinkTarget.Linkable)
            {
                refused.Add(($"assets[{index}].mediaAssetId", NotFoundHere));
            }
        }

        // ---- The window, in the workspace's own zone ----
        (DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Zone, DateTime LocalStart, DateTime LocalEnd)? window = null;
        if (model.Window is { LocalStartsAt: { } localStart, LocalEndsAt: { } localEnd })
        {
            var profile = await brandProfiles.GetAsync(cancellationToken);
            var zone = profile.Succeeded ? profile.Value!.TimeZoneId : null;

            // The window is stored as instants and as the local times they came from, which takes a zone. With
            // none set there is no honest conversion, and guessing UTC would move the creator's window.
            if (zone is null || !zones.IsValidZone(zone))
            {
                refused.Add(("window", "Set your workspace's time zone in brand settings before giving a package a window."));
            }
            else
            {
                var starts = zones.ToUtc(DateOnly.FromDateTime(localStart), TimeOnly.FromDateTime(localStart), zone);
                var ends = zones.ToUtc(DateOnly.FromDateTime(localEnd), TimeOnly.FromDateTime(localEnd), zone);

                // A local time the clocks skip never happens in that zone. Storing it as "the local time the
                // creator meant" beside an instant an hour later would be recording two different answers.
                if (starts.Resolution is LocalTimeResolution.ShiftedForward || ends.Resolution is LocalTimeResolution.ShiftedForward)
                {
                    refused.Add(("window", $"That time does not exist in {zone}: the clocks go forward over it. Choose a time an hour either side."));
                }

                // Local order does not guarantee order in time across a clock change, and the instants are
                // what a schedule would run on.
                else if (ends.Utc < starts.Utc)
                {
                    refused.Add(("window", "A window cannot end before it starts."));
                }
                else
                {
                    window = (starts.Utc, ends.Utc, zone, localStart, localEnd);
                }
            }
        }

        if (refused.Count > 0)
        {
            return Failure(OperationError.Validation(ContentErrorCodes.CardPackageUnprocessable, CannotPackage, refused));
        }

        // ---- Everything pinned exists and was approved. Does it all still stand? ----
        var reasons = RecipePackageReview.Reasons(
            versionId,
            recipeAvailable: true,
            currentVersion?.Id,
            editorialPinned: editorial is not null,
            editorial,
            [.. channelInputs
                .Where(channel => channel.SocialRevisionId is not null)
                .Select(channel => (channel.Key, channel.SocialRevisionId!.Value, (SocialPinRecord?)pinByChannel[channel.Key]))]);

        var now = clock.UtcNow;
        var column = model.Column ?? ContentCardColumn.ToDo;
        var socialPackageId = pinByChannel.Values.Select(pin => (Guid?)pin.SocialPackageId).FirstOrDefault();
        var title = ContentCardInputChecks.Normalize(model.Title) ?? recipe.Value!.Title;

        var card = new ContentCard
        {
            Id = Guid.NewGuid(),

            // The recipe's own title when none was given. A copy of a name, to label a plan: the recipe stays
            // the one place its title is true.
            Title = title.Length > ContentCardPolicy.TitleMaxLength ? title[..ContentCardPolicy.TitleMaxLength] : title,
            Column = column,
            RecipeId = recipeId,
            RecipeVersionId = versionId,
            CreativeContextId = model.CreativeContextId,
            SocialPackageId = socialPackageId,
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var created = new CardMoveHistory
        {
            Id = Guid.NewGuid(),
            ContentCardId = card.Id,
            Sequence = 1,
            FromColumn = null,
            ToColumn = column,
            ActorMembershipId = workspace.MembershipId,
            OccurredAt = now,
        };

        // Pins, and nothing but pins: every field names a record the workspace already owns, at one version.
        var package = new RecipeContentPackageLink
        {
            Id = Guid.NewGuid(),
            ContentCardId = card.Id,
            RecipeId = recipeId,
            RecipeVersionId = versionId,
            EditorialProposalId = editorial?.ProposalId,
            EditorialRevisionId = editorial?.RevisionId,
            SocialPackageId = socialPackageId,
            WindowStartsAt = window?.StartsAt,
            WindowEndsAt = window?.EndsAt,
            WindowTimeZoneId = window?.Zone,
            WindowLocalStartsAt = window?.LocalStart,
            WindowLocalEndsAt = window?.LocalEnd,

            // Recorded only when there is something to allow: an allowance of nothing is not a decision. And
            // recorded with exactly which reasons were allowed, so a reason that arises later still blocks.
            NeedsReviewAllowedAt = allow && reasons.Count > 0 ? now : null,
            NeedsReviewAllowedByMembershipId = allow && reasons.Count > 0 ? workspace.MembershipId : null,
            NeedsReviewAllowedReasons = allow && reasons.Count > 0 ? RecipePackageReview.Encode(reasons) : null,
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
        };

        package.Assets.AddRange(assetInputs.Select((asset, index) => new RecipeContentPackageLinkAsset
        {
            Id = Guid.NewGuid(),
            RecipeContentPackageLinkId = package.Id,
            MediaAssetId = asset.Id,
            MediaAssetVersionNumber = asset.Version,
            SortOrder = index,
        }));
        package.Channels.AddRange(channelInputs.Select((channel, index) => new RecipeContentPackageLinkChannel
        {
            Id = Guid.NewGuid(),
            RecipeContentPackageLinkId = package.Id,
            ChannelKey = channel.Key,
            SocialPackageChannelId = pinByChannel.TryGetValue(channel.Key, out var pin) ? pin.ChannelId : null,
            SocialRevisionId = pinByChannel.ContainsKey(channel.Key) ? channel.SocialRevisionId : null,
            SortOrder = index,
        }));

        var standing = reasons.Count == 0 ? "clear" : allow ? "allowed" : "blocked";

        var saved = await dataLayer.CreateAsync(
            card,
            created,

            // Counts and states. No title, no text, and nothing of any pinned record but that it was pinned.
            new AuditEntry(
                actorUserId,
                ContentAuditActions.ContentCardPackaged,
                ContentAuditActions.ContentCardResourceType,
                card.Id.ToString("D"),
                CorrelationId(),
                $"Put a recipe package on the board in {column}: {assetInputs.Count} picture(s), {channelInputs.Count} channel(s)"
                    + (editorial is null ? ", no editorial" : ", with editorial")
                    + (reasons.Count == 0
                        ? "."
                        : allow
                            ? $". {reasons.Count} source(s) needed review and it was allowed to stand."
                            : $". Blocked: {reasons.Count} source(s) need review."),
                AfterReference: $"package:{standing}"),
            cancellationToken,
            package);

        return saved
            ? await GetDetailAsync(card.Id, cancellationToken)
            : Failure(new OperationError(
                ContentErrorCodes.CardNotSaved,
                "The package could not be saved just then. Try again.",
                new Dictionary<string, string[]>()));
    }

    /// <summary>
    /// A stored package with whether its sources still stand, asked of those sources as they are now.
    /// </summary>
    /// <remarks>
    /// Blocked is never read from the package: a package cannot know that a recipe gained a version after it
    /// was put together. It is worked out here on every read, from the recipe through the recipes facade and
    /// from the editorial and post revisions this module owns. The allowance is the one stored fact, and it
    /// covers only the reasons that were there to look at when it was given.
    /// </remarks>
    private async Task<ContentCardPackageServiceModel> ToPackageAsync(
        ContentCardPackageRecord package, CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetDetailAsync(package.RecipeId, cancellationToken);
        var available = recipe.Succeeded && recipe.Value!.Status is not RecipeStatus.Archived;

        var editorial = package.EditorialRevisionId is { } editorialRevisionId
            ? await dataLayer.FindEditorialPinAsync(editorialRevisionId, cancellationToken)
            : null;

        var pinned = package.Channels.Where(channel => channel.SocialRevisionId is not null).ToList();
        var pins = await dataLayer.FindSocialPinsAsync(
            [.. pinned.Select(channel => channel.SocialRevisionId!.Value)], cancellationToken);

        var reasons = RecipePackageReview.Reasons(
            package.RecipeVersionId,
            available,
            available ? recipe.Value!.CurrentVersion?.Id : null,
            editorialPinned: package.EditorialRevisionId is not null,
            editorial,
            [.. pinned.Select(channel => (
                channel.ChannelKey,
                channel.SocialRevisionId!.Value,
                pins.FirstOrDefault(pin => pin.RevisionId == channel.SocialRevisionId)))]);

        return new ContentCardPackageServiceModel(
            package.RecipeId,
            package.RecipeVersionId,
            package.EditorialProposalId,
            package.EditorialRevisionId,
            package.SocialPackageId,
            [.. package.Assets.Select(asset => new ContentCardPackageAssetServiceModel(asset.MediaAssetId, asset.MediaAssetVersionNumber))],
            [.. package.Channels.Select(channel => new ContentCardPackageChannelServiceModel(
                channel.ChannelKey, channel.SocialPackageChannelId, channel.SocialRevisionId))],
            package.WindowStartsAt is { } startsAt
                ? new ContentCardPackageWindowServiceModel(
                    startsAt,
                    package.WindowEndsAt!.Value,
                    package.WindowTimeZoneId!,
                    package.WindowLocalStartsAt!.Value,
                    package.WindowLocalEndsAt!.Value)
                : null,
            reasons,
            package.NeedsReviewAllowedAt,
            Blocked: RecipePackageReview.IsBlocked(reasons, package.NeedsReviewAllowedReasons),
            package.CreatedAt);
    }
}
