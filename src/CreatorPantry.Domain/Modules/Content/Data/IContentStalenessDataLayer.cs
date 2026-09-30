using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data;

public interface IContentStalenessDataLayer
{
    Task<ContentStalenessCandidates> FindCandidatesAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks one accepted proposal NeedsReview and appends the history row in one save. Returns false without
    /// writing when the proposal is no longer Accepted — already marked, or moved on — which is what makes a
    /// replayed delivery a no-op.
    /// </summary>
    /// <remarks>
    /// A concurrent change to the proposal fails the save on its row version and <em>throws</em>. That is
    /// deliberate: the caller is an outbox handler, the retry is the recovery, and swallowing it here would
    /// leave the proposal Accepted with nothing scheduled to look again.
    /// </remarks>
    Task<bool> MarkNeedsReviewAsync(ContentStalenessChange change, CancellationToken cancellationToken);

    Task<ContentCurrencyFacts?> FindCurrencyFactsAsync(Guid proposalId, CancellationToken cancellationToken);
}

internal sealed class ContentStalenessDataLayer(
    CreatorPantryDbContext context,
    IContentStalenessRepository repository) : IContentStalenessDataLayer
{
    public async Task<ContentStalenessCandidates> FindCandidatesAsync(Guid recipeId, CancellationToken cancellationToken)
    {
        var latest = await repository.GetLatestRecipeVersionAsync(recipeId, cancellationToken);
        var accepted = await repository.FindAcceptedPinsAsync(recipeId, cancellationToken);

        return new ContentStalenessCandidates(latest, accepted);
    }

    public async Task<bool> MarkNeedsReviewAsync(ContentStalenessChange change, CancellationToken cancellationToken)
    {
        var proposal = await repository.GetForUpdateAsync(change.ProposalId, cancellationToken);

        if (proposal is not { Status: ContentProposalStatus.Accepted })
        {
            return false;
        }

        proposal.Status = ContentProposalStatus.NeedsReview;
        proposal.StaleSince = change.At;
        proposal.StaleReasons = change.Reasons;
        proposal.UpdatedAt = change.At;

        repository.Add(new ContentProposalTransition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = proposal.WorkspaceId,
            ContentProposalId = proposal.Id,
            FromStatus = ContentProposalStatus.Accepted,
            ToStatus = ContentProposalStatus.NeedsReview,

            // The revision that was found stale, so the history says which accepted words were affected.
            ContentRevisionId = proposal.AcceptedRevisionId,
            ActorMembershipId = null,
            StaleReasons = change.Reasons,
            OccurredAt = change.At,
            MachineVersion = ContentProposalTransitions.Version,
        });

        // One save, one transaction: the proposal's UPDATE carries its row version in the WHERE clause, so
        // nothing commits unless the proposal is still where this was decided.
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<ContentCurrencyFacts?> FindCurrencyFactsAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        var proposal = await repository.FindProposalPinAsync(proposalId, cancellationToken);

        if (proposal is null)
        {
            return null;
        }

        var recipeId = await repository.FindRecipeIdAsync(proposalId, cancellationToken);
        var latest = recipeId is { } id ? await repository.GetLatestRecipeVersionAsync(id, cancellationToken) : null;

        return new ContentCurrencyFacts(proposalId, proposal.Value.Status, proposal.Value.AcceptedRevisionId, proposal.Value.Pin, latest);
    }
}
