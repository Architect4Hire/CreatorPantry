using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>EF access for the long-form draft aggregate (LF.2.2).</summary>
public interface ILongFormDraftRepository
{
    /// <summary>Whether this workspace has that creative context. Another workspace's is simply not there.</summary>
    Task<bool> ContextExistsAsync(Guid contextId, CancellationToken cancellationToken);

    Task<LongFormDraft?> FindAsync(Guid draftId, CancellationToken cancellationToken);

    Task<LongFormDraft?> FindForUpdateAsync(Guid draftId, CancellationToken cancellationToken);

    /// <summary>The revision with the highest number, which is the draft's current state; null when it has none.</summary>
    Task<LongFormRevision?> FindLatestRevisionAsync(Guid draftId, CancellationToken cancellationToken);

    /// <summary>One revision of a draft by its number, or null when the draft has none with it.</summary>
    Task<LongFormRevision?> FindRevisionAsync(Guid draftId, int revisionNumber, CancellationToken cancellationToken);

    Task<bool> HasRevisionForProposalAsync(Guid draftId, Guid aiProposalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LongFormRevisionRecipePin>> ListPinsAsync(Guid revisionId, CancellationToken cancellationToken);

    /// <summary>
    /// The recipe references this workspace's creative context holds now, in the context's own order.
    /// Identifiers only; another workspace's context has none.
    /// </summary>
    Task<IReadOnlyList<LongFormContextRecipeReference>> ListRecipeReferencesAsync(
        Guid contextId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<LongFormDraftSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        LongFormDraftListCriteria criteria, CancellationToken cancellationToken);

    void Add(LongFormDraft draft);

    void Add(LongFormRevision revision);

    void AddRange(IEnumerable<LongFormRevisionRecipePin> pins);
}

/// <inheritdoc cref="ILongFormDraftRepository"/>
internal sealed class LongFormDraftRepository(CreatorPantryDbContext context) : ILongFormDraftRepository
{
    // No WorkspaceId predicate anywhere below: contexts, drafts, revisions and pins are all workspace-owned, so
    // the global query filter scopes every read, and another workspace's rows are indistinguishable from none.
    public Task<bool> ContextExistsAsync(Guid contextId, CancellationToken cancellationToken) =>
        context.CreativeContexts.AsNoTracking().AnyAsync(candidate => candidate.Id == contextId, cancellationToken);

    public Task<LongFormDraft?> FindAsync(Guid draftId, CancellationToken cancellationToken) =>
        context.LongFormDrafts.AsNoTracking().FirstOrDefaultAsync(draft => draft.Id == draftId, cancellationToken);

    public Task<LongFormDraft?> FindForUpdateAsync(Guid draftId, CancellationToken cancellationToken) =>
        context.LongFormDrafts.FirstOrDefaultAsync(draft => draft.Id == draftId, cancellationToken);

    public Task<LongFormRevision?> FindLatestRevisionAsync(Guid draftId, CancellationToken cancellationToken) =>
        context.LongFormRevisions
            .AsNoTracking()
            .Where(revision => revision.LongFormDraftId == draftId)
            .OrderByDescending(revision => revision.RevisionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<LongFormRevision?> FindRevisionAsync(Guid draftId, int revisionNumber, CancellationToken cancellationToken) =>
        context.LongFormRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                revision => revision.LongFormDraftId == draftId && revision.RevisionNumber == revisionNumber,
                cancellationToken);

    public Task<bool> HasRevisionForProposalAsync(Guid draftId, Guid aiProposalId, CancellationToken cancellationToken) =>
        context.LongFormRevisions
            .AsNoTracking()
            .AnyAsync(
                revision => revision.LongFormDraftId == draftId && revision.AiProposalId == aiProposalId,
                cancellationToken);

    public async Task<IReadOnlyList<LongFormRevisionRecipePin>> ListPinsAsync(
        Guid revisionId, CancellationToken cancellationToken) =>
        await context.LongFormRevisionRecipePins
            .AsNoTracking()
            .Where(pin => pin.LongFormRevisionId == revisionId)
            .OrderBy(pin => pin.SortOrder)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LongFormContextRecipeReference>> ListRecipeReferencesAsync(
        Guid contextId, CancellationToken cancellationToken) =>
        await context.CreativeContextReferences
            .AsNoTracking()
            .Where(reference => reference.CreativeContextId == contextId
                && reference.Kind == CreativeContextReferenceKind.Recipe
                && reference.RecipeId != null)
            .OrderBy(reference => reference.SortOrder)
            .Select(reference => new LongFormContextRecipeReference(reference.Id, reference.RecipeId!.Value))
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<LongFormDraftSummaryRecord> Rows, bool HasMore)> ListRecentAsync(
        LongFormDraftListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var drafts = context.LongFormDrafts.AsNoTracking();

        if (criteria.Kind is { } kind)
        {
            drafts = drafts.Where(draft => draft.Kind == kind);
        }

        if (criteria.CreativeContextId is { } contextId)
        {
            drafts = drafts.Where(draft => draft.CreativeContextId == contextId);
        }

        if (criteria.Position is { } position)
        {
            drafts = drafts.Where(draft =>
                draft.UpdatedAt < position.UpdatedAt
                || (draft.UpdatedAt == position.UpdatedAt && draft.Id.CompareTo(position.ContextId) < 0));
        }

        var fetched = await drafts
            .OrderByDescending(draft => draft.UpdatedAt)
            .ThenByDescending(draft => draft.Id)

            // One row past the limit: how the page learns whether another follows without a second query.
            .Take(criteria.Limit + 1)
            .Select(draft => new
            {
                draft.Id,
                draft.CreativeContextId,
                draft.Kind,
                draft.Status,

                // Identifiers and a number only. No word of any plan is read to build a list.
                LatestRevisionNumber = context.LongFormRevisions
                    .Where(revision => revision.LongFormDraftId == draft.Id)
                    .Max(revision => (int?)revision.RevisionNumber),
                draft.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return (
            [
                .. fetched.Take(criteria.Limit).Select(row => new LongFormDraftSummaryRecord(
                    row.Id, row.CreativeContextId, row.Kind, row.Status, row.LatestRevisionNumber, row.UpdatedAt)),
            ],
            fetched.Count > criteria.Limit);
    }

    public void Add(LongFormDraft draft) => context.LongFormDrafts.Add(draft);

    public void Add(LongFormRevision revision) => context.LongFormRevisions.Add(revision);

    public void AddRange(IEnumerable<LongFormRevisionRecipePin> pins) => context.LongFormRevisionRecipePins.AddRange(pins);
}
