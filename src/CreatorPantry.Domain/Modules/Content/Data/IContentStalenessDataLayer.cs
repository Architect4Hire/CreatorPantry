using CreatorPantry.Domain.Managers.Audit;
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

    /// <inheritdoc cref="ISocialPackageRepository.FindAcceptedPinsAsync"/>
    Task<IReadOnlyList<AcceptedSocialPinRecord>> FindAcceptedSocialPinsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks one accepted post channel NeedsReview and writes its audit entry in one save. Returns false
    /// without writing when the channel is no longer Accepted, which is what makes a replayed delivery a no-op.
    /// </summary>
    /// <remarks>
    /// The same bargain as <see cref="MarkNeedsReviewAsync"/>: a concurrent change to the slot fails the save
    /// on its row version and throws, and the outbox retry is the recovery. The revision is not touched — it
    /// could not be — so the accepted words and their pin are exactly what was accepted.
    /// </remarks>
    Task<bool> MarkSocialNeedsReviewAsync(SocialStalenessChange change, CancellationToken cancellationToken);
}

internal sealed class ContentStalenessDataLayer(
    CreatorPantryDbContext context,
    IContentStalenessRepository repository,
    ISocialPackageRepository socialPackages,
    IAuditWriter auditWriter) : IContentStalenessDataLayer
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

    public Task<IReadOnlyList<AcceptedSocialPinRecord>> FindAcceptedSocialPinsAsync(
        Guid recipeId, CancellationToken cancellationToken) =>
        socialPackages.FindAcceptedPinsAsync(recipeId, cancellationToken);

    public async Task<bool> MarkSocialNeedsReviewAsync(SocialStalenessChange change, CancellationToken cancellationToken)
    {
        var channel = await socialPackages.GetChannelForUpdateAsync(change.ChannelId, cancellationToken);

        if (channel is not { Status: ContentProposalStatus.Accepted })
        {
            return false;
        }

        channel.Status = ContentProposalStatus.NeedsReview;
        channel.StaleSince = change.At;
        channel.StaleReasons = change.Reasons;
        channel.UpdatedAt = change.At;

        // A system move, so no actor. Statuses and the channel key only: never a word of the post.
        auditWriter.Record(new AuditEntry(
            ActorUserId: null,
            ContentAuditActions.SocialChannelMarkedStale,
            ContentAuditActions.SocialChannelResourceType,
            channel.Id.ToString("D"),
            Guid.NewGuid(),
            $"Marked the accepted {channel.ChannelKey} post as needing review after its recipe changed.",
            BeforeReference: nameof(ContentProposalStatus.Accepted),
            AfterReference: nameof(ContentProposalStatus.NeedsReview)));

        await context.SaveChangesAsync(cancellationToken);

        return true;
    }
}
