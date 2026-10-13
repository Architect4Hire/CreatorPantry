using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

public interface IContentStalenessRepository
{
    /// <summary>The recipe's highest-numbered version, or null when it has none or is not visible to this workspace.</summary>
    Task<LatestRecipeVersionRecord?> GetLatestRecipeVersionAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>Every Accepted proposal of the recipe, with the version its accepted revision is pinned to. No-tracking.</summary>
    Task<IReadOnlyList<AcceptedPinRecord>> FindAcceptedPinsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>One proposal's accepted pin, or null when it has none. No-tracking.</summary>
    Task<(ContentProposalStatus Status, Guid? AcceptedRevisionId, AcceptedPinRecord? Pin)?> FindProposalPinAsync(
        Guid proposalId, CancellationToken cancellationToken);

    /// <summary>The proposal tracked, for a change the layer below will save; null when not visible.</summary>
    Task<ContentProposal?> GetForUpdateAsync(Guid proposalId, CancellationToken cancellationToken);

    /// <summary>The recipe of a proposal, for reading its latest version. Null when not visible.</summary>
    Task<Guid?> FindRecipeIdAsync(Guid proposalId, CancellationToken cancellationToken);

    /// <summary>Stages a history row. Nothing is saved.</summary>
    void Add(ContentProposalTransition transition);

    /// <summary>
    /// Every long-form draft whose accepted revision is pinned to the recipe, with the version it is pinned
    /// to (LF.6.1). Accepted drafts and ones already marked for review; a draft never accepted has no accepted
    /// revision and is not here. No-tracking.
    /// </summary>
    Task<IReadOnlyList<LongFormRecipePinRecord>> FindLongFormRecipePinsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Every long-form draft with an accepted revision, with the style guide version that revision was
    /// written under. Accepted drafts and ones already marked for review. No-tracking.
    /// </summary>
    Task<IReadOnlyList<LongFormVoicePinRecord>> FindLongFormVoicePinsAsync(CancellationToken cancellationToken);

    /// <summary>The draft tracked, for a change the layer below will save; null when not visible.</summary>
    Task<LongFormDraft?> GetLongFormDraftForUpdateAsync(Guid draftId, CancellationToken cancellationToken);
}

internal sealed class ContentStalenessRepository(CreatorPantryDbContext context) : IContentStalenessRepository
{
    // No WorkspaceId predicate anywhere below: proposals, revisions and recipe versions are all
    // workspace-owned, so the global query filter scopes every read, and another workspace's rows are
    // indistinguishable from none.
    public Task<LatestRecipeVersionRecord?> GetLatestRecipeVersionAsync(Guid recipeId, CancellationToken cancellationToken) =>
        context.RecipeVersions
            .AsNoTracking()
            .Where(version => version.RecipeId == recipeId)
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new LatestRecipeVersionRecord(version.Id, version.VersionNumber))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AcceptedPinRecord>> FindAcceptedPinsAsync(
        Guid recipeId, CancellationToken cancellationToken) =>
        await (
            from proposal in context.ContentProposals.AsNoTracking()
            where proposal.RecipeId == recipeId && proposal.Status == ContentProposalStatus.Accepted
            join revision in context.ContentRevisions.AsNoTracking() on proposal.AcceptedRevisionId equals revision.Id
            join version in context.RecipeVersions.AsNoTracking() on revision.RecipeVersionId equals version.Id
            select new AcceptedPinRecord(proposal.Id, revision.Id, version.Id, version.VersionNumber))
            .ToListAsync(cancellationToken);

    public async Task<(ContentProposalStatus Status, Guid? AcceptedRevisionId, AcceptedPinRecord? Pin)?> FindProposalPinAsync(
        Guid proposalId, CancellationToken cancellationToken)
    {
        var proposal = await context.ContentProposals
            .AsNoTracking()
            .Where(candidate => candidate.Id == proposalId)
            .Select(candidate => new { candidate.Status, candidate.AcceptedRevisionId })
            .SingleOrDefaultAsync(cancellationToken);

        if (proposal is null)
        {
            return null;
        }

        var pin = proposal.AcceptedRevisionId is { } revisionId
            ? await (
                from revision in context.ContentRevisions.AsNoTracking()
                where revision.Id == revisionId
                join version in context.RecipeVersions.AsNoTracking() on revision.RecipeVersionId equals version.Id
                select new AcceptedPinRecord(proposalId, revision.Id, version.Id, version.VersionNumber))
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        return (proposal.Status, proposal.AcceptedRevisionId, pin);
    }

    public Task<ContentProposal?> GetForUpdateAsync(Guid proposalId, CancellationToken cancellationToken) =>
        context.ContentProposals.SingleOrDefaultAsync(proposal => proposal.Id == proposalId, cancellationToken);

    public async Task<Guid?> FindRecipeIdAsync(Guid proposalId, CancellationToken cancellationToken) =>
        await context.ContentProposals
            .AsNoTracking()
            .Where(proposal => proposal.Id == proposalId)
            .Select(proposal => (Guid?)proposal.RecipeId)
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(ContentProposalTransition transition) => context.ContentProposalTransitions.Add(transition);

    // The accepted revision, by the draft's own pointer: never the latest, and never a revision of a draft
    // whose creator has not accepted one. Status is the second guard — a draft reopened for writing keeps its
    // pointer and is not an article of record while it is open.
    public async Task<IReadOnlyList<LongFormRecipePinRecord>> FindLongFormRecipePinsAsync(
        Guid recipeId, CancellationToken cancellationToken) =>
        await (
            from draft in context.LongFormDrafts.AsNoTracking()
            where draft.Status == LongFormDraftStatus.Accepted || draft.Status == LongFormDraftStatus.NeedsReview
            join pin in context.LongFormRevisionRecipePins.AsNoTracking() on draft.AcceptedRevisionId equals pin.LongFormRevisionId
            where pin.RecipeId == recipeId
            join version in context.RecipeVersions.AsNoTracking() on pin.RecipeVersionId equals version.Id
            select new LongFormRecipePinRecord(draft.Id, draft.StaleReasons, version.VersionNumber))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LongFormVoicePinRecord>> FindLongFormVoicePinsAsync(CancellationToken cancellationToken) =>
        await (
            from draft in context.LongFormDrafts.AsNoTracking()
            where draft.Status == LongFormDraftStatus.Accepted || draft.Status == LongFormDraftStatus.NeedsReview
            join revision in context.LongFormRevisions.AsNoTracking() on draft.AcceptedRevisionId equals revision.Id
            select new LongFormVoicePinRecord(draft.Id, draft.StaleReasons, revision.BrandStyleGuideVersionId))
            .ToListAsync(cancellationToken);

    public Task<LongFormDraft?> GetLongFormDraftForUpdateAsync(Guid draftId, CancellationToken cancellationToken) =>
        context.LongFormDrafts.SingleOrDefaultAsync(draft => draft.Id == draftId, cancellationToken);
}
