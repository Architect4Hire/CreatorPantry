using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>
/// Reads of the content board (13.5). Every query is scoped by the workspace query filter, so a card another
/// workspace owns is not found here rather than found and refused.
/// </summary>
public interface IContentCardRepository
{
    /// <summary>
    /// One page of cards in board order, read one past <see cref="ContentCardListCriteria.Limit"/> so the
    /// caller can tell whether another page follows.
    /// </summary>
    Task<(IReadOnlyList<ContentCardSummaryRecord> Rows, bool HasMore)> ListAsync(
        ContentCardListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>How many cards each column holds under the filters, ignoring where the page starts.</summary>
    Task<IReadOnlyList<ContentCardColumnCountRecord>> CountByColumnAsync(
        ContentCardFilters filters, CancellationToken cancellationToken);

    /// <summary>One card in full, or null when the resolved workspace has no such card.</summary>
    Task<ContentCardDetailRecord?> GetDetailAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>The highest rank in a column, or null for a column with no cards.</summary>
    Task<long?> HighestRankAsync(ContentCardColumn column, CancellationToken cancellationToken);

    /// <summary>
    /// Which of this module's own records the resolved workspace holds and a card may link. A null id is not
    /// asked about and answers true; another workspace's record answers false, exactly as one that does not
    /// exist. An archived piece of work is not linkable, and neither are posts written for a different piece
    /// of work than the one named beside them.
    /// </summary>
    /// <param name="socialPackageContextId">
    /// The piece of work the posts must belong to, or null when the card names none and any of the workspace's
    /// packages will do.
    /// </param>
    Task<ContentCardOwnLineage> FindOwnLineageAsync(
        Guid? creativeContextId,
        Guid? promptRecordId,
        Guid? socialPackageId,
        Guid? socialPackageContextId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The id of the card the Content Pipeline made for a piece of work, or null when the resolved workspace
    /// holds no such card — because the work has none, or because the work is another workspace's.
    /// </summary>
    Task<Guid?> FindPipelineCardIdAsync(Guid creativeContextId, CancellationToken cancellationToken);

    /// <summary>
    /// What a piece of work has to put on a card, or null when the resolved workspace has no such live work:
    /// unknown, archived and another workspace's are one answer.
    /// </summary>
    Task<PipelineCardSourceRecord?> FindPipelineSourceAsync(Guid creativeContextId, CancellationToken cancellationToken);

    /// <summary>The newest saved prompt that names a library asset as the picture it made, or null.</summary>
    Task<Guid?> FindPromptForAssetAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>One card, tracked, for an edit; or null when the resolved workspace has no such card.</summary>
    Task<ContentCard?> FindForUpdateAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>
    /// Every card in a column as its id and rank, in the board's own order — rank, then id, as the store
    /// breaks the tie. Read without tracking: this is for deciding a place, not for writing one.
    /// </summary>
    Task<IReadOnlyList<ContentCardSlot>> ListColumnSlotsAsync(ContentCardColumn column, CancellationToken cancellationToken);

    /// <summary>Every card in a column except one, tracked, for a re-spacing that will write their ranks.</summary>
    Task<IReadOnlyList<ContentCard>> ListColumnForUpdateAsync(
        ContentCardColumn column, Guid exceptCardId, CancellationToken cancellationToken);

    /// <summary>The highest sequence in a card's movement history, or null for a card with none.</summary>
    Task<int?> LastMoveSequenceAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>
    /// One editorial revision with where its proposal stands, or null when the resolved workspace has no such
    /// revision.
    /// </summary>
    Task<EditorialPinRecord?> FindEditorialPinAsync(Guid revisionId, CancellationToken cancellationToken);

    /// <summary>
    /// Post revisions with where each one's channel stands. A revision the resolved workspace does not have is
    /// simply absent from the answer.
    /// </summary>
    Task<IReadOnlyList<SocialPinRecord>> FindSocialPinsAsync(
        IReadOnlyCollection<Guid> revisionIds, CancellationToken cancellationToken);

    /// <summary>Stages a recipe package with its picture and channel pins. Nothing is written until the unit of work saves.</summary>
    void Add(RecipeContentPackageLink package);

    /// <summary>How many cards other than one point at a picture.</summary>
    Task<int> CountOtherCardsLinkingAssetAsync(Guid mediaAssetId, Guid exceptCardId, CancellationToken cancellationToken);

    /// <summary>Whether a card has a recipe package.</summary>
    Task<bool> HasPackageAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>
    /// Stages a card's deletion. Its movement history and its recipe package are not staged with it: they are
    /// write-once, and leave by the database's own cascade when the card does.
    /// </summary>
    void Remove(ContentCard card);

    /// <summary>Stages one row of movement history. Nothing is written until the unit of work saves.</summary>
    void Add(CardMoveHistory move);

    /// <summary>Stages a new card and its creation row. Nothing is written until the unit of work saves.</summary>
    void Add(ContentCard card, CardMoveHistory created);
}

internal sealed class ContentCardRepository(CreatorPantryDbContext context) : IContentCardRepository
{
    public async Task<(IReadOnlyList<ContentCardSummaryRecord> Rows, bool HasMore)> ListAsync(
        ContentCardListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var fetched = await Project(
                Positioned(Filtered(criteria.Filters), criteria.Position)
                    .OrderBy(card => card.Column)
                    .ThenBy(card => card.SortRank)
                    .ThenBy(card => card.Id)
                    .Take(criteria.Limit + 1))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    public async Task<IReadOnlyList<ContentCardColumnCountRecord>> CountByColumnAsync(
        ContentCardFilters filters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var counts = await Filtered(filters)
            .GroupBy(card => card.Column)
            .Select(group => new { Column = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return [.. counts.Select(row => new ContentCardColumnCountRecord(row.Column, row.Count))];
    }

    public async Task<ContentCardDetailRecord?> GetDetailAsync(Guid cardId, CancellationToken cancellationToken)
    {
        var cards = context.ContentCards.AsNoTracking().Where(card => card.Id == cardId);

        var summary = await Project(cards).SingleOrDefaultAsync(cancellationToken);
        if (summary is null)
        {
            return null;
        }

        // The description, and the names of the two linked records this module owns. Recipes and pictures
        // belong to other modules and are asked for through their facades, above this layer.
        var own = await cards
            .Select(card => new
            {
                card.Description,
                ContextTitle = context.CreativeContexts
                    .Where(item => item.Id == card.CreativeContextId)
                    .Select(item => item.WorkingTitle)
                    .FirstOrDefault(),
                PromptLabel = context.PromptRecords
                    .Where(item => item.Id == card.PromptRecordId)
                    .Select(item => item.Label)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        // The card was there a moment ago and is not now: deleted between two reads. That is "no such card",
        // not a failure.
        if (own is null)
        {
            return null;
        }

        // The latest moves, one past the cap so the caller can say there are more.
        var latest = await context.CardMoveHistory.AsNoTracking()
            .Where(move => move.ContentCardId == cardId)
            .OrderByDescending(move => move.Sequence)
            .Take(ContentCardPolicy.DetailHistoryMaxRows + 1)
            .Select(move => new CardMoveRecord(move.Sequence, move.FromColumn, move.ToColumn, move.Note, move.OccurredAt))
            .ToListAsync(cancellationToken);

        var truncated = latest.Count > ContentCardPolicy.DetailHistoryMaxRows;
        var moves = latest.Take(ContentCardPolicy.DetailHistoryMaxRows).OrderBy(move => move.Sequence).ToList();

        var link = await context.RecipeContentPackageLinks.AsNoTracking()
            .Include(item => item.Assets)
            .Include(item => item.Channels)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.ContentCardId == cardId, cancellationToken);

        return new ContentCardDetailRecord(
            summary, own.Description, own.ContextTitle, own.PromptLabel, moves, truncated, link is null ? null : ToRecord(link));
    }

    public Task<long?> HighestRankAsync(ContentCardColumn column, CancellationToken cancellationToken) =>
        context.ContentCards.AsNoTracking()
            .Where(card => card.Column == column)
            .MaxAsync(card => (long?)card.SortRank, cancellationToken);

    public Task<ContentCard?> FindForUpdateAsync(Guid cardId, CancellationToken cancellationToken) =>
        context.ContentCards.FirstOrDefaultAsync(card => card.Id == cardId, cancellationToken);

    public Task<Guid?> FindPipelineCardIdAsync(Guid creativeContextId, CancellationToken cancellationToken) =>
        context.ContentCards.AsNoTracking()
            .Where(card => card.CreativeContextId == creativeContextId && card.Origin == ContentCardOrigin.ContentPipeline)
            .Select(card => (Guid?)card.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PipelineCardSourceRecord?> FindPipelineSourceAsync(
        Guid creativeContextId, CancellationToken cancellationToken)
    {
        var work = await context.CreativeContexts.AsNoTracking()
            .Where(item => item.Id == creativeContextId && item.ArchivedAt == null)
            .Select(item => new { item.WorkingTitle })
            .FirstOrDefaultAsync(cancellationToken);

        if (work is null)
        {
            return null;
        }

        // The recipe the work is about, and the library assets its kept pictures became. Everything else a
        // work names — a cue picture, a reused prompt, a kept picture not yet saved — is not a card's to link.
        var references = await context.CreativeContextReferences.AsNoTracking()
            .Where(reference => reference.CreativeContextId == creativeContextId
                && (reference.Kind == CreativeContextReferenceKind.Recipe
                    || (reference.Kind == CreativeContextReferenceKind.DamAsset
                        && reference.Purpose == CreativeContextReferencePurpose.Keeper)))
            .OrderBy(reference => reference.SortOrder)
            .Select(reference => new
            {
                reference.Kind,
                reference.RecipeId,
                reference.RecipeVersionId,
                reference.MediaAssetId,
                reference.MediaAssetVersionNumber,
            })
            .ToListAsync(cancellationToken);

        var recipe = references.FirstOrDefault(reference => reference.Kind is CreativeContextReferenceKind.Recipe);

        // Accepted now, not accepted once: a post that has since been sent back for review is not what the
        // card should hold up as the work's posts.
        var socialPackageId = await context.SocialPackages.AsNoTracking()
            .Where(package => package.CreativeContextId == creativeContextId
                && package.Channels.Any(channel => channel.Status == ContentProposalStatus.Accepted))
            .Select(package => (Guid?)package.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new PipelineCardSourceRecord(
            work.WorkingTitle,
            recipe?.RecipeId,
            recipe?.RecipeVersionId,
            [.. references
                .Where(reference => reference.Kind is CreativeContextReferenceKind.DamAsset && reference.MediaAssetId is not null)
                .Select(reference => new PipelineKeeperRecord(reference.MediaAssetId!.Value, reference.MediaAssetVersionNumber))],
            socialPackageId);
    }

    public Task<Guid?> FindPromptForAssetAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        context.PromptRecords.AsNoTracking()
            .Where(prompt => prompt.DamAssetId == mediaAssetId)
            .OrderByDescending(prompt => prompt.CreatedAt)
            .ThenByDescending(prompt => prompt.Id)
            .Select(prompt => (Guid?)prompt.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<ContentCardOwnLineage> FindOwnLineageAsync(
        Guid? creativeContextId,
        Guid? promptRecordId,
        Guid? socialPackageId,
        Guid? socialPackageContextId,
        CancellationToken cancellationToken) =>
        new(
            creativeContextId is not { } contextId
                || await context.CreativeContexts.AsNoTracking()
                    .AnyAsync(item => item.Id == contextId && item.ArchivedAt == null, cancellationToken),
            promptRecordId is not { } promptId
                || await context.PromptRecords.AsNoTracking().AnyAsync(item => item.Id == promptId, cancellationToken),
            // Posts belong to one piece of work. When the card names a piece of work too, the posts have to be
            // that work's own, or the card would point at two different things as though they were one.
            socialPackageId is not { } packageId
                || await context.SocialPackages.AsNoTracking()
                    .AnyAsync(
                        item => item.Id == packageId
                            && (socialPackageContextId == null || item.CreativeContextId == socialPackageContextId),
                        cancellationToken));

    public async Task<IReadOnlyList<ContentCardSlot>> ListColumnSlotsAsync(
        ContentCardColumn column, CancellationToken cancellationToken) =>
        await context.ContentCards.AsNoTracking()
            .Where(card => card.Column == column)
            .OrderBy(card => card.SortRank)
            .ThenBy(card => card.Id)
            .Select(card => new ContentCardSlot(card.Id, card.SortRank))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContentCard>> ListColumnForUpdateAsync(
        ContentCardColumn column, Guid exceptCardId, CancellationToken cancellationToken) =>
        await context.ContentCards
            .Where(card => card.Column == column && card.Id != exceptCardId)
            .ToListAsync(cancellationToken);

    public Task<int?> LastMoveSequenceAsync(Guid cardId, CancellationToken cancellationToken) =>
        context.CardMoveHistory.AsNoTracking()
            .Where(move => move.ContentCardId == cardId)
            .MaxAsync(move => (int?)move.Sequence, cancellationToken);

    public Task<EditorialPinRecord?> FindEditorialPinAsync(Guid revisionId, CancellationToken cancellationToken) =>
        (from revision in context.ContentRevisions.AsNoTracking()
         join proposal in context.ContentProposals.AsNoTracking() on revision.ContentProposalId equals proposal.Id
         where revision.Id == revisionId
         select new EditorialPinRecord(
             revision.Id,
             proposal.Id,
             proposal.RecipeId,
             proposal.Kind,
             proposal.Status,
             proposal.AcceptedRevisionId,
             revision.RecipeVersionId))
        .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SocialPinRecord>> FindSocialPinsAsync(
        IReadOnlyCollection<Guid> revisionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revisionIds);

        if (revisionIds.Count == 0)
        {
            return [];
        }

        return await (
                from revision in context.SocialRevisions.AsNoTracking()
                join channel in context.SocialPackageChannels.AsNoTracking() on revision.SocialPackageChannelId equals channel.Id
                join package in context.SocialPackages.AsNoTracking() on channel.SocialPackageId equals package.Id
                where revisionIds.Contains(revision.Id)
                select new SocialPinRecord(
                    revision.Id,
                    channel.Id,
                    channel.ChannelKey,
                    package.Id,
                    package.CreativeContextId,
                    channel.Status,
                    channel.AcceptedRevisionId,
                    revision.RecipeId,
                    revision.RecipeVersionId))
            .ToListAsync(cancellationToken);
    }

    public void Add(RecipeContentPackageLink package) => context.RecipeContentPackageLinks.Add(package);

    public Task<int> CountOtherCardsLinkingAssetAsync(
        Guid mediaAssetId, Guid exceptCardId, CancellationToken cancellationToken) =>
        context.ContentCards.AsNoTracking()
            .CountAsync(card => card.MediaAssetId == mediaAssetId && card.Id != exceptCardId, cancellationToken);

    public Task<bool> HasPackageAsync(Guid cardId, CancellationToken cancellationToken) =>
        context.RecipeContentPackageLinks.AsNoTracking().AnyAsync(link => link.ContentCardId == cardId, cancellationToken);

    public void Remove(ContentCard card) => context.ContentCards.Remove(card);

    public void Add(CardMoveHistory move) => context.CardMoveHistory.Add(move);

    public void Add(ContentCard card, CardMoveHistory created)
    {
        context.ContentCards.Add(card);
        context.CardMoveHistory.Add(created);
    }

    private IQueryable<ContentCard> Filtered(ContentCardFilters filters)
    {
        var cards = context.ContentCards.AsNoTracking();

        if (filters.ChannelKey is { } channelKey)
        {
            cards = cards.Where(card => card.ChannelKey == channelKey);
        }

        if (filters.Day is { } day)
        {
            cards = cards.Where(card => card.PlannedDayOfWeek == day);
        }

        if (filters.Date is { } date)
        {
            cards = cards.Where(card => card.PlannedDate == date);
        }

        if (filters.Column is { } column)
        {
            cards = cards.Where(card => card.Column == column);
        }

        return cards;
    }

    /// <summary>
    /// Strictly after the position, in the board's own order. All three parts, so two cards sharing a rank are
    /// neither repeated nor skipped across a page boundary.
    /// </summary>
    private static IQueryable<ContentCard> Positioned(IQueryable<ContentCard> cards, ContentCardPosition? position) =>
        position is null
            ? cards
            : cards.Where(card =>
                card.Column > position.Column
                || (card.Column == position.Column
                    && (card.SortRank > position.SortRank
                        || (card.SortRank == position.SortRank && card.Id.CompareTo(position.CardId) > 0))));

    private IQueryable<ContentCardSummaryRecord> Project(IQueryable<ContentCard> cards) =>
        cards.Select(card => new ContentCardSummaryRecord(
            card.Id,
            card.Title,
            card.Description == null ? null : card.Description.Substring(0, ContentCardPolicy.DescriptionPreviewMaxLength),
            card.Description == null ? 0 : card.Description.Length,
            card.Column,
            card.SortRank,
            card.ChannelKey,
            card.PlannedDate,
            card.PlannedDayOfWeek,
            card.CuisineCode,
            card.DishTypeCode,
            card.MethodCode,
            card.CreativeContextId,
            card.RecipeId,
            card.RecipeVersionId,
            card.PromptRecordId,
            card.MediaAssetId,
            card.MediaAssetVersionNumber,
            card.SocialPackageId,
            context.CardMoveHistory.Where(move => move.ContentCardId == card.Id).Max(move => (int?)move.Sequence),
            context.RecipeContentPackageLinks.Any(link => link.ContentCardId == card.Id),
            card.CreatedAt,
            card.UpdatedAt,
            card.RowVersion));

    private static ContentCardPackageRecord ToRecord(RecipeContentPackageLink link) => new(
        link.Id,
        link.RecipeId,
        link.RecipeVersionId,
        link.EditorialProposalId,
        link.EditorialRevisionId,
        link.SocialPackageId,
        link.WindowStartsAt,
        link.WindowEndsAt,
        link.WindowTimeZoneId,
        link.WindowLocalStartsAt,
        link.WindowLocalEndsAt,
        link.NeedsReviewAllowedAt,
        link.NeedsReviewAllowedReasons,
        link.CreatedAt,
        [.. link.Assets.OrderBy(asset => asset.SortOrder)
            .Select(asset => new ContentCardPackageAssetRecord(asset.MediaAssetId, asset.MediaAssetVersionNumber))],
        [.. link.Channels.OrderBy(channel => channel.SortOrder)
            .Select(channel => new ContentCardPackageChannelRecord(
                channel.ChannelKey, channel.SocialPackageChannelId, channel.SocialRevisionId))]);
}
