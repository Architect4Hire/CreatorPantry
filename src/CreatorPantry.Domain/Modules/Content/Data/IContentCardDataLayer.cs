using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>How a card edit ended.</summary>
public enum ContentCardWrite
{
    Saved = 0,

    /// <summary>The card changed, or went, after it was read. Nothing was written.</summary>
    Stale = 1,

    /// <summary>The store refused the write — a linked record that stopped being linkable, or a fault. Nothing was written.</summary>
    Refused = 2,
}

/// <summary>The content board's complete persistence operations: the reads (13.5), the create (13.6), the edit (13.6a), the move (13.6b) and the delete (13.6c).</summary>
public interface IContentCardDataLayer
{
    /// <summary>One page of the board with the per-column counts for the same filters.</summary>
    Task<(IReadOnlyList<ContentCardSummaryRecord> Rows, bool HasMore, IReadOnlyList<ContentCardColumnCountRecord> Counts)> ListAsync(
        ContentCardListCriteria criteria, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.GetDetailAsync"/>
    Task<ContentCardDetailRecord?> GetDetailAsync(Guid cardId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.FindOwnLineageAsync"/>
    Task<ContentCardOwnLineage> FindOwnLineageAsync(
        Guid? creativeContextId,
        Guid? promptRecordId,
        Guid? socialPackageId,
        Guid? socialPackageContextId,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.FindForUpdateAsync"/>
    Task<ContentCard?> FindForUpdateAsync(Guid cardId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.FindPipelineCardIdAsync"/>
    Task<Guid?> FindPipelineCardIdAsync(Guid creativeContextId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.FindPipelineSourceAsync"/>
    Task<PipelineCardSourceRecord?> FindPipelineSourceAsync(Guid creativeContextId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.FindPromptForAssetAsync"/>
    Task<Guid?> FindPromptForAssetAsync(Guid mediaAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an edited card and its audit entry in one unit of work: both, or neither.
    /// </summary>
    /// <remarks>
    /// The card is the one <see cref="FindForUpdateAsync"/> returned, so its row version is in the write's
    /// <c>WHERE</c>: an edit composed against a state the card no longer has updates nothing and answers
    /// <see cref="ContentCardWrite.Stale"/>. On anything but <see cref="ContentCardWrite.Saved"/> nothing is
    /// written and nothing is left staged.
    /// </remarks>
    Task<ContentCardWrite> SaveAsync(ContentCard card, AuditEntry audit, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.ListColumnSlotsAsync"/>
    Task<IReadOnlyList<ContentCardSlot>> ListColumnSlotsAsync(ContentCardColumn column, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.ListColumnForUpdateAsync"/>
    Task<IReadOnlyList<ContentCard>> ListColumnForUpdateAsync(
        ContentCardColumn column, Guid exceptCardId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.LastMoveSequenceAsync"/>
    Task<int?> LastMoveSequenceAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a placed card — and, with it, whatever the placement needed — in one unit of work: all of it, or
    /// none of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "All of it" is the moved card, every card the placement re-spaced, the history row when the card changed
    /// column, and the audit entry. Each card is written with its own row version in the <c>WHERE</c>, so a
    /// card another writer changed since it was read — the moving card or a re-spaced neighbour — updates
    /// nothing and the whole save is refused. The history row's sequence is unique per card, which is a second
    /// guard against two moves of one card both being recorded.
    /// </para>
    /// <para>
    /// On anything but <see cref="ContentCardWrite.Saved"/> nothing is written and nothing is left staged.
    /// </para>
    /// </remarks>
    /// <param name="card">The moved card, as <see cref="FindForUpdateAsync"/> returned it and since placed.</param>
    /// <param name="entered">The history row, for a card that changed column; null for a new place in the same one.</param>
    /// <param name="audit">The audit entry, when the write is one that is audited.</param>
    Task<ContentCardWrite> SaveMoveAsync(
        ContentCard card, CardMoveHistory? entered, AuditEntry? audit, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.CountOtherCardsLinkingAssetAsync"/>
    Task<int> CountOtherCardsLinkingAssetAsync(Guid mediaAssetId, Guid exceptCardId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.HasPackageAsync"/>
    Task<bool> HasPackageAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a card and writes its audit entry in one unit of work: both, or neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What goes with it.</strong> The card's movement history and its recipe package — with that
    /// package's picture and channel pins — are removed by the database's cascade. They are write-once and are
    /// deliberately never loaded here: a tracked copy would be refused by the interceptor that keeps them
    /// immutable, and the cascade is the one way they are meant to leave.
    /// </para>
    /// <para>
    /// <strong>What does not.</strong> Nothing the card pointed at: not a recipe, a piece of work, a saved
    /// prompt, the posts, or a picture. Those keys are restricted from the other side — a card never held
    /// them up — and deleting the card simply stops pointing.
    /// </para>
    /// <para>
    /// The card is the one <see cref="FindForUpdateAsync"/> returned, so its row version is in the delete's
    /// <c>WHERE</c>. On anything but <see cref="ContentCardWrite.Saved"/> nothing is deleted and nothing is
    /// left staged.
    /// </para>
    /// </remarks>
    Task<ContentCardWrite> DeleteAsync(ContentCard card, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a new card at the bottom of its column with its creation row and its audit entry, in one unit of
    /// work: all three, or nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The place is decided here, inside the operation that writes it</strong>: one gap below the
    /// column's lowest card, read immediately before the save. Two creates racing into one column may read the
    /// same lowest card and take the same rank; both are written, and the board's order — column, rank, id —
    /// still reads them one way every time. That is what the gapped rank is for, so there is no retry.
    /// </para>
    /// <para>
    /// The unit of work is the request's own <c>DbContext</c>, so an idempotency record staged by the caller
    /// commits with these rows or not at all. False means nothing was written, and nothing is left staged.
    /// </para>
    /// <para>
    /// <strong>A second pipeline card for one piece of work is refused here, by the store.</strong> Two
    /// commits that both found no card both arrive at this save; the unique index lets one through and the
    /// other answers false, having written nothing. The caller reads the card that won.
    /// </para>
    /// </remarks>
    /// <param name="package">
    /// A recipe package to write with the card, with its picture and channel pins, or null for a card that has
    /// none. In the same save as everything else: a card is never left without the package it was created for.
    /// </param>
    Task<bool> CreateAsync(
        ContentCard card,
        CardMoveHistory created,
        AuditEntry audit,
        CancellationToken cancellationToken,
        RecipeContentPackageLink? package = null);

    /// <inheritdoc cref="IContentCardRepository.FindEditorialPinAsync"/>
    Task<EditorialPinRecord?> FindEditorialPinAsync(Guid revisionId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentCardRepository.FindSocialPinsAsync"/>
    Task<IReadOnlyList<SocialPinRecord>> FindSocialPinsAsync(
        IReadOnlyCollection<Guid> revisionIds, CancellationToken cancellationToken);
}

internal sealed class ContentCardDataLayer(
    IContentCardRepository repository,
    IAuditWriter auditWriter,
    CreatorPantryDbContext db) : IContentCardDataLayer
{
    public Task<ContentCardOwnLineage> FindOwnLineageAsync(
        Guid? creativeContextId,
        Guid? promptRecordId,
        Guid? socialPackageId,
        Guid? socialPackageContextId,
        CancellationToken cancellationToken) =>
        repository.FindOwnLineageAsync(
            creativeContextId, promptRecordId, socialPackageId, socialPackageContextId, cancellationToken);

    public Task<ContentCard?> FindForUpdateAsync(Guid cardId, CancellationToken cancellationToken) =>
        repository.FindForUpdateAsync(cardId, cancellationToken);

    public Task<Guid?> FindPipelineCardIdAsync(Guid creativeContextId, CancellationToken cancellationToken) =>
        repository.FindPipelineCardIdAsync(creativeContextId, cancellationToken);

    public Task<PipelineCardSourceRecord?> FindPipelineSourceAsync(
        Guid creativeContextId, CancellationToken cancellationToken) =>
        repository.FindPipelineSourceAsync(creativeContextId, cancellationToken);

    public Task<Guid?> FindPromptForAssetAsync(Guid mediaAssetId, CancellationToken cancellationToken) =>
        repository.FindPromptForAssetAsync(mediaAssetId, cancellationToken);

    public Task<IReadOnlyList<ContentCardSlot>> ListColumnSlotsAsync(
        ContentCardColumn column, CancellationToken cancellationToken) =>
        repository.ListColumnSlotsAsync(column, cancellationToken);

    public Task<IReadOnlyList<ContentCard>> ListColumnForUpdateAsync(
        ContentCardColumn column, Guid exceptCardId, CancellationToken cancellationToken) =>
        repository.ListColumnForUpdateAsync(column, exceptCardId, cancellationToken);

    public Task<int?> LastMoveSequenceAsync(Guid cardId, CancellationToken cancellationToken) =>
        repository.LastMoveSequenceAsync(cardId, cancellationToken);

    public Task<ContentCardWrite> SaveMoveAsync(
        ContentCard card, CardMoveHistory? entered, AuditEntry? audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (entered is not null)
        {
            repository.Add(entered);
        }

        if (audit is not null)
        {
            auditWriter.Record(audit);
        }

        return CommitAsync(cancellationToken);
    }

    public Task<int> CountOtherCardsLinkingAssetAsync(
        Guid mediaAssetId, Guid exceptCardId, CancellationToken cancellationToken) =>
        repository.CountOtherCardsLinkingAssetAsync(mediaAssetId, exceptCardId, cancellationToken);

    public Task<bool> HasPackageAsync(Guid cardId, CancellationToken cancellationToken) =>
        repository.HasPackageAsync(cardId, cancellationToken);

    public Task<ContentCardWrite> DeleteAsync(ContentCard card, AuditEntry audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(audit);

        repository.Remove(card);
        auditWriter.Record(audit);

        return CommitAsync(cancellationToken);
    }

    public Task<ContentCardWrite> SaveAsync(ContentCard card, AuditEntry audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(audit);

        auditWriter.Record(audit);

        return CommitAsync(cancellationToken);
    }

    /// <summary>One save of everything staged in this unit of work, with what it means when it does not happen.</summary>
    private async Task<ContentCardWrite> CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return ContentCardWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else's write landed first, or the card is gone. The audit entry was in the same save,
            // so it did not happen either.
            db.ChangeTracker.Clear();

            return ContentCardWrite.Stale;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            return ContentCardWrite.Refused;
        }
    }

    public Task<EditorialPinRecord?> FindEditorialPinAsync(Guid revisionId, CancellationToken cancellationToken) =>
        repository.FindEditorialPinAsync(revisionId, cancellationToken);

    public Task<IReadOnlyList<SocialPinRecord>> FindSocialPinsAsync(
        IReadOnlyCollection<Guid> revisionIds, CancellationToken cancellationToken) =>
        repository.FindSocialPinsAsync(revisionIds, cancellationToken);

    public async Task<bool> CreateAsync(
        ContentCard card,
        CardMoveHistory created,
        AuditEntry audit,
        CancellationToken cancellationToken,
        RecipeContentPackageLink? package = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(audit);

        var highest = await repository.HighestRankAsync(card.Column, cancellationToken);
        card.SortRank = (highest ?? 0) + ContentCardPolicy.RankGap;

        repository.Add(card, created);

        if (package is not null)
        {
            repository.Add(package);
        }

        auditWriter.Record(audit);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Nothing committed: the card, its creation row and the audit entry are one save. Cleared rather
            // than detached one by one, so none of them can ride along on this scope's next save.
            db.ChangeTracker.Clear();

            return false;
        }

        return true;
    }

    public async Task<(IReadOnlyList<ContentCardSummaryRecord> Rows, bool HasMore, IReadOnlyList<ContentCardColumnCountRecord> Counts)> ListAsync(
        ContentCardListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (rows, hasMore) = await repository.ListAsync(criteria, cancellationToken);

        // Counted on every page rather than only the first: a board that changed between two pages would
        // otherwise head its columns with numbers from before the change.
        var counts = await repository.CountByColumnAsync(criteria.Filters, cancellationToken);

        return (rows, hasMore, counts);
    }

    public Task<ContentCardDetailRecord?> GetDetailAsync(Guid cardId, CancellationToken cancellationToken) =>
        repository.GetDetailAsync(cardId, cancellationToken);
}
