using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>How one write to a long-form draft ended.</summary>
public enum LongFormDraftWrite
{
    Saved = 0,

    /// <summary>The draft's row version had moved: somebody else wrote first.</summary>
    Stale = 1,

    /// <summary>A key or a constraint refused the rows: a draft that already exists, a revision number taken, a pin the keys would not accept.</summary>
    Refused = 2,
}

/// <summary>Composes the long-form draft aggregate's reads and its one write (LF.2.2).</summary>
public interface ILongFormDraftDataLayer
{
    /// <inheritdoc cref="ILongFormDraftRepository.ContextExistsAsync"/>
    Task<bool> ContextExistsAsync(Guid contextId, CancellationToken cancellationToken);

    Task<LongFormDraft?> FindAsync(Guid draftId, CancellationToken cancellationToken);

    Task<LongFormDraft?> FindForUpdateAsync(Guid draftId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormDraftRepository.FindLatestRevisionAsync"/>
    Task<LongFormRevision?> FindLatestRevisionAsync(Guid draftId, CancellationToken cancellationToken);

    Task<LongFormRevision?> FindRevisionAsync(Guid draftId, int revisionNumber, CancellationToken cancellationToken);

    Task<bool> HasRevisionForProposalAsync(Guid draftId, Guid aiProposalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LongFormRevisionRecipePin>> ListPinsAsync(Guid revisionId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<LongFormDraftSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        LongFormDraftListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a draft's change, with the revision, pins and audit entry that go with it, in one transaction.
    /// </summary>
    /// <param name="draft">A draft from <see cref="FindForUpdateAsync"/>, or a new one when <paramref name="isNew"/>.</param>
    Task<LongFormDraftWrite> SaveAsync(
        LongFormDraft draft,
        bool isNew,
        LongFormRevision? revision,
        IReadOnlyList<LongFormRevisionRecipePin> pins,
        AuditEntry? audit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILongFormDraftDataLayer"/>
internal sealed class LongFormDraftDataLayer(
    ILongFormDraftRepository drafts,
    IAuditWriter auditWriter,
    CreatorPantryDbContext db) : ILongFormDraftDataLayer
{
    public Task<bool> ContextExistsAsync(Guid contextId, CancellationToken cancellationToken) =>
        drafts.ContextExistsAsync(contextId, cancellationToken);

    public Task<LongFormDraft?> FindAsync(Guid draftId, CancellationToken cancellationToken) =>
        drafts.FindAsync(draftId, cancellationToken);

    public Task<LongFormDraft?> FindForUpdateAsync(Guid draftId, CancellationToken cancellationToken) =>
        drafts.FindForUpdateAsync(draftId, cancellationToken);

    public Task<LongFormRevision?> FindLatestRevisionAsync(Guid draftId, CancellationToken cancellationToken) =>
        drafts.FindLatestRevisionAsync(draftId, cancellationToken);

    public Task<LongFormRevision?> FindRevisionAsync(Guid draftId, int revisionNumber, CancellationToken cancellationToken) =>
        drafts.FindRevisionAsync(draftId, revisionNumber, cancellationToken);

    public Task<bool> HasRevisionForProposalAsync(Guid draftId, Guid aiProposalId, CancellationToken cancellationToken) =>
        drafts.HasRevisionForProposalAsync(draftId, aiProposalId, cancellationToken);

    public Task<IReadOnlyList<LongFormRevisionRecipePin>> ListPinsAsync(Guid revisionId, CancellationToken cancellationToken) =>
        drafts.ListPinsAsync(revisionId, cancellationToken);

    public Task<(IReadOnlyList<LongFormDraftSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        LongFormDraftListCriteria criteria, CancellationToken cancellationToken) =>
        drafts.ListRecentAsync(criteria, cancellationToken);

    public async Task<LongFormDraftWrite> SaveAsync(
        LongFormDraft draft,
        bool isNew,
        LongFormRevision? revision,
        IReadOnlyList<LongFormRevisionRecipePin> pins,
        AuditEntry? audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(pins);

        if (isNew)
        {
            drafts.Add(draft);
        }

        if (revision is not null)
        {
            drafts.Add(revision);
            drafts.AddRange(pins);
        }

        if (audit is not null)
        {
            auditWriter.Record(audit);
        }

        try
        {
            // One SaveChanges, and so one transaction: the draft's status, the revision it moved for, that
            // revision's pins and the audit entry are written together or not at all.
            await db.SaveChangesAsync(cancellationToken);

            return LongFormDraftWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Cleared rather than detached one by one: everything above is staged, and any of it left behind
            // would ride along on this scope's next save.
            db.ChangeTracker.Clear();

            return LongFormDraftWrite.Stale;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            return LongFormDraftWrite.Refused;
        }
    }
}
