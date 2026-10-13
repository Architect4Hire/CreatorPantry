using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/content-cards")]
public sealed class ContentCardsController(IContentCardFacade cards) : ControllerBase
{
    /// <summary>Reads the content board of the workspace named by the route, a page at a time.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">The filters, the cursor and the page size. Carries no workspace.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. **The board has one order, so there is no `sort` parameter**:
    /// column — `ToDo`, `InProcess`, `ReadyForReview`, `Approved` — then each card's place in its column. Two
    /// cards that share a place are ordered by id, which is what keeps a page boundary from repeating one and
    /// skipping the other. The place itself is not published; the order of `items` is the order.
    ///
    /// `channel`, `day`, `date` and `column` each narrow the board and combine with AND. `day` is a weekday by
    /// name and matches the weekday of a card's planned date; `date` is `yyyy-MM-dd` and matches it exactly; a
    /// card with no planned date matches neither. A `channel` naming no channel at all is an empty page rather
    /// than an error — a read keeps answering for keys the catalogue has retired, because a card that stored
    /// one is still on the board. A filter that cannot be read answers `400 content.card.list.invalid`.
    ///
    /// **Paging is by keyset cursor.** Pass a page's `nextCursor` back as `cursor`; it is null on the last
    /// page. A cursor is bound to the workspace and the filters it was issued for, and one replayed against
    /// another answers `400 content.card.cursor.invalid` rather than a plausible page from the wrong place.
    /// `limit` outside 1–100 is clamped, not refused.
    ///
    /// `columns` lists every column the request could match — all four, or the one named by `column` — with
    /// how many cards it holds under the same filters, across every page; an empty column is listed with zero.
    ///
    /// **Each row is a summary.** `descriptionPreview` is cut server-side and `descriptionLength` is the full
    /// length; `lineage` carries ids only, and their names come from the detail route. `moveCount` counts the
    /// times the card changed column — putting it in a different place in the same column is not a move.
    /// `concurrencyToken` is what an edit or a move quotes. **`publishing` is always null today**: no
    /// publication record exists yet, and a card says nothing about delivery on its own. No row carries a
    /// picture, an address for one, or any provider's data. The response is `no-store`: it is
    /// workspace-private creator content.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<ContentCardPageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        string workspaceSlug,
        [FromQuery] ContentCardListViewModel query,
        CancellationToken cancellationToken)
    {
        // Set before the result is examined, so a refusal carries it too.
        Response.Headers.CacheControl = "no-store";

        var result = await cards.ListAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Reads one card of the workspace named by the route, in full.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cardId">
    /// The card to read. Constrained to a Guid, so a malformed id never reaches this action: routing answers
    /// 404 with the edge's generic <c>not_found</c> code rather than this module's.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. `card` is the list's row for this card; beside it are the
    /// whole `description`, the records it points at, its movement history and its recipe package.
    ///
    /// **`lineage` names what the card points at.** Each entry has a `kind`, an `id`, the pinned version where
    /// there is one, and a `label` where the record has a name this route can read — a recipe's title, a piece
    /// of work's working title, a saved prompt's label. `available` is false when the record can no longer be
    /// read: the card still says what it pointed at rather than failing to open. A picture is named by id and
    /// version only — no bytes, no address, no title.
    ///
    /// **`history` is oldest first**, by `sequence`; the first entry is the card's creation and has no
    /// `fromColumn`. At most the latest 100 moves are returned and `historyTruncated` says when there were
    /// more; `card.moveCount` is the true count either way. No entry says who moved the card: membership ids
    /// never leave the server.
    ///
    /// `package` is null for a card with no recipe package. When present it is pins only — the exact recipe
    /// version, editorial revision, posts and picture versions it was put together from — and its `window` is
    /// what the creator asked for, which schedules nothing.
    ///
    /// An unknown id answers `404 content.card.not_found`. **Another workspace's card answers exactly that —
    /// the same code, the same message, the same body** — so a card id cannot be used to ask what a neighbour
    /// owns. That is not two branches written to match: the workspace query filter means the server never sees
    /// the other row at all. The response is `no-store`.
    /// </remarks>
    [HttpGet("{cardId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid cardId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await cards.GetDetailAsync(cardId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Puts a new card on the content board of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="model">The card. Carries no workspace, author, place in the column or weekday.</param>
    /// <param name="idempotencyKey">Optional. Send one to make a retry safe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above; a Viewer is refused with `403 content.card.forbidden`. Only `title` is required.
    /// `column` defaults to `ToDo`. **A new card goes to the bottom of its column**, and its creation is its
    /// first history entry. `plannedDate` is `yyyy-MM-dd`; the card's `plannedDay` is that date's own weekday
    /// and cannot be sent.
    ///
    /// `channelKey` must be a channel the catalogue offers now, and `cuisineCode`, `dishTypeCode` and
    /// `methodCode` must be codes the cooking vocabulary holds; one that is not, like any field that cannot be
    /// read, answers `400 content.card.invalid` naming it.
    ///
    /// **Every linked record must be this workspace's own.** `creativeContextId`, `recipeId` and
    /// `recipeVersionId`, `promptRecordId`, `mediaAssetId` and `mediaAssetVersionNumber`, and `socialPackageId`
    /// are each checked through the record's owner before anything is written. One that cannot be linked
    /// answers `422 content.card.lineage.unprocessable` naming the field — **the same answer whether the
    /// record never existed, has been archived or removed, or belongs to another workspace**, so a card cannot
    /// be used to ask what a neighbour owns. A version sent without its recipe or picture is a `400`.
    ///
    /// **Nothing is half-written.** The card, its first history entry and its audit entry are one write; on
    /// any refusal there is no card. `409 content.card.conflict` means the write did not happen and is safe to
    /// send again.
    ///
    /// With an `Idempotency-Key`, a repeat of the same request returns the first answer with
    /// `Idempotent-Replayed: true` and creates nothing more; the same key with a different body is refused.
    /// Without a key, two identical requests are two cards.
    ///
    /// Answers `201` with the card in full — the detail route's shape — and its `Location`. The response is
    /// `no-store`.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        CreateContentCardViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await cards.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        // The location is the only thing this layer contributes, because only it knows the route shape.
        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/content-cards/{created.Card.Id}", created));
    }

    /// <summary>Edits a card's fields. Only what the patch mentions changes.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="cardId">The card to edit.</param>
    /// <param name="model">The fields to change, and the token of the read they were composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. **A field left out is untouched; a field sent with a value is set; a field sent
    /// as `null` is cleared.** `title` may be changed but not cleared. `plannedDay` is not a field of the request
    /// and is ignored if sent: it is the planned date's own weekday, re-derived when the date changes and
    /// cleared when the date is.
    ///
    /// **A patch does not move a card.** Sending `column` answers `400` naming it; a card changes column
    /// through the move route, which is what records the move.
    ///
    /// `expectedConcurrencyToken` is required. When the card has changed since that read the answer is
    /// `409 content.card.stale.conflict` and **nothing is written** — keep what the creator typed, read the
    /// card again, and send again. A token that could never have been issued is a `400`. A patch that changes
    /// nothing writes nothing and returns the same token.
    ///
    /// Only a *newly chosen* `channelKey` or cooking code is checked against what is offered now: one the card
    /// already holds stays valid after it is retired, so an unrelated edit never fails because of it.
    ///
    /// **Nothing is unlinked unless its field is sent as `null`.** `creativeContextId`, `recipeId`,
    /// `promptRecordId`, `mediaAssetId` and `socialPackageId` can each be set, replaced or cleared, and no
    /// patch unlinks one it did not mention. **A version pin is never dropped as a side effect**: clearing or
    /// replacing a recipe or picture the card pins a version of answers `400` unless `recipeVersionId` or
    /// `mediaAssetVersionNumber` is sent in the same patch — a version of the new record, or `null` to drop
    /// the pin. A newly linked record is checked exactly as on create, and one that cannot be linked answers
    /// `422 content.card.lineage.unprocessable` — the same answer whether it never existed, can no longer be
    /// used, or belongs to another workspace. A record the card already points at is not re-checked.
    ///
    /// An unknown card and another workspace's card both answer `404 content.card.not_found`, identically.
    /// Answers `200` with the card in full and its new `concurrencyToken`. The response is `no-store`.
    /// </remarks>
    [HttpPatch("{cardId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Update(
        string workspaceSlug,
        Guid cardId,
        UpdateContentCardViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await cards.UpdateAsync(userId, cardId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Moves a card to a column and a place in it.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="cardId">The card to move.</param>
    /// <param name="model">Where it should be, and the token of the read the move was composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. `column` is required and names where the card should be — another column, or
    /// the one it is already in to give it a new place there. `beforeCardId` names the card it goes
    /// immediately before, which must be in `column`; left out, the card goes to the bottom. A place is named
    /// by a neighbour rather than a number, so it means the same whatever filters a board is showing.
    ///
    /// **Only a change of column is a move.** It adds one entry to the card's `history` — the column it left,
    /// the column it entered, the `note` and when — and raises `moveCount`. **A new place in the same column
    /// adds none**, so the history never shows a transition that did not happen; a `note` sent with one
    /// answers `400` naming it, because there is no move to keep it on.
    ///
    /// `expectedConcurrencyToken` is required. `409 content.card.stale.conflict` means the card — or a card
    /// that had to be re-spaced to make room — changed first, and **nothing was written**: read the board
    /// again and send the move again. A token that could never have been issued is a `400`. A card that is
    /// already exactly there is left alone and keeps its token.
    ///
    /// **The move is one write.** The card's new place, any re-spacing of the column around it, the history
    /// entry and the audit entry happen together or not at all.
    ///
    /// A `beforeCardId` that is not in the target column answers `422 content.card.move.unprocessable` —
    /// **the same answer whether that card does not exist, is in another column, is the card being moved, or
    /// belongs to another workspace**. An unknown card and another workspace's card both answer
    /// `404 content.card.not_found`, identically.
    ///
    /// Answers `200` with the card in full and its new `concurrencyToken`. Making room can change the
    /// `concurrencyToken` of other cards in the column, though never what they say. The response is `no-store`.
    /// </remarks>
    [HttpPost("{cardId:guid}/move")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Move(
        string workspaceSlug,
        Guid cardId,
        MoveContentCardViewModel model,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await cards.MoveAsync(userId, cardId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Deletes a card from the content board.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="cardId">The card to delete.</param>
    /// <param name="model">The confirmation, the card's token, and what to do about its linked picture.</param>
    /// <param name="idempotencyKey">Required. Makes the deletion one transaction and lets a retry learn what happened.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Requires the **Editor** role, where adding, editing and moving a card require Contributor: a deletion
    /// takes finished planning away from every collaborator rather than contributing to it.
    ///
    /// **What is deleted with the card:** its movement history, and its recipe package — the record of which
    /// versions were put together, not the things themselves. **What is never deleted with it:** the recipe,
    /// the piece of work, the saved prompt and the posts it pointed at. They are exactly as they were.
    ///
    /// **A picture is never removed from the library unless you say so.** `linkedAsset` left out, or `Keep`,
    /// leaves the card's linked picture untouched. `Remove` removes it too, and requires
    /// `linkedAssetConcurrencyToken` — the picture's own `concurrencyToken`, so you have read what you are
    /// removing. That removal is the library's own: the picture gains a tombstone, **no bytes are deleted**,
    /// and anything else pointing at it keeps pointing. Only the card's own linked picture is ever in question
    /// — never the pictures pinned in its recipe package. `Remove` on a card with no linked picture answers
    /// `400`.
    ///
    /// `confirmed` must be `true`, and `expectedConcurrencyToken` is required: `409
    /// content.card.stale.conflict` means the card changed since you read it and **nothing was deleted**.
    ///
    /// **`Idempotency-Key` is required**, and the deletion is one transaction: if the picture cannot be
    /// removed as asked — its token is out of date, say — the answer is `409
    /// content.card.linked_asset.conflict` and **the card is not deleted either**. Repeating the request with
    /// the same key returns the first answer with `Idempotent-Replayed: true` and deletes nothing more; the
    /// same key with a different body or a different card is refused. Without the key a second request could
    /// only ever answer "not found", about a card you have just deleted.
    ///
    /// The response says what was deleted and carries none of the creator's words: the `column` the card was
    /// in, its `moveCount`, whether a package went with it, and `linkedAsset` — `outcome` is `None`, `Kept` or
    /// `Removed`, `otherCardCount` is how many other cards still point at the picture, and for a removed
    /// picture `affected` counts what else now points at it. Counts only: nothing here names a recipe.
    ///
    /// An unknown card and another workspace's card both answer `404 content.card.not_found`, identically.
    /// The response is `no-store`.
    /// </remarks>
    [HttpDelete("{cardId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<ContentCardDeletionServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Delete(
        string workspaceSlug,
        Guid cardId,
        DeleteContentCardViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await cards.DeleteAsync(userId, cardId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, Ok);
    }

    /// <summary>Puts a recipe package on the board as a card.</summary>
    /// <param name="workspaceSlug">Bound only so the route is well formed; see the list route.</param>
    /// <param name="model">The recipe, the exact versions to pin, the channels and the window.</param>
    /// <param name="idempotencyKey">Required. Makes the package one transaction and a retry safe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. Creates one card for one recipe, pinned to **the exact versions you name**:
    /// `recipeVersionId` (required), `editorialRevisionId`, a `socialRevisionId` per channel, and a
    /// `versionNumber` per picture. The server checks each one; it never goes and finds something newer than
    /// what you were looking at. **Nothing is copied** — the package is ids and version pins, and the records
    /// stay where they are.
    ///
    /// **A pin that cannot be made refuses the whole request** with `422
    /// content.card.package.unprocessable`, naming the field, and nothing is created:
    ///
    /// - a record that cannot be found or can no longer be used — **the same answer whether it never existed,
    ///   was removed, or belongs to another workspace**;
    /// - a version that is not the approved one: a recipe version that is neither approved nor marked ready,
    ///   an editorial revision that is not the accepted one for this recipe, a post that is not the accepted
    ///   one for that channel of that piece of work.
    ///
    /// `creativeContextId` is required when any channel pins a post, because a post belongs to a piece of
    /// work. A channel may be named with no post pinned.
    ///
    /// **A source that was approved but has since moved does not refuse the request — it blocks the package.**
    /// The card is created, and `package.needsReview` lists why: the recipe has a newer version, the editorial
    /// or a post has been marked as needing review, or one of them was written against a different version of
    /// the recipe. `package.blocked` is then true. These are worked out from the pinned records each time the
    /// card is read, so a package that was clear when you made it becomes blocked if a source moves later.
    ///
    /// **`allowNeedsReview: true` lets such a package stand.** It takes the Editor role (`403` otherwise) and
    /// is recorded with who allowed it and when; `package.needsReview` still lists the reasons, and `blocked`
    /// is false. **An allowance covers the reasons that were there when it was given, and no others**: if a
    /// different source moves later, the package is blocked again. It records nothing when there is nothing
    /// to allow.
    ///
    /// `window` is the local start and end you mean, with no offset. It is converted with the workspace's time
    /// zone from brand settings and stored both ways; a workspace with no time zone answers `422` naming
    /// `window`, and so does a local time that the clocks skip in that zone. A window is an intention: it
    /// schedules nothing.
    ///
    /// `title` defaults to the recipe's title and `column` to `ToDo`. The card goes to the bottom of its
    /// column and its creation is its first history entry.
    ///
    /// **`Idempotency-Key` is required.** The card, its history entry, the package and every pin are one
    /// write: all of it or none. Repeating the request with the same key returns the first card with
    /// `Idempotent-Replayed: true`; the same key with a different body is refused.
    ///
    /// Answers `201` with the card in full and its `Location`. The response is `no-store`.
    /// </remarks>
    [HttpPost("recipe-packages")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<ContentCardDetailServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> CreateRecipePackage(
        string workspaceSlug,
        CreateRecipePackageViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await cards.CreateRecipePackageAsync(userId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/content-cards/{created.Card.Id}", created));
    }
}
