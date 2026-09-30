using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IContentStalenessBusiness
{
    /// <summary>
    /// Marks NeedsReview every Accepted proposal of the recipe whose accepted content was written against an
    /// older version than the recipe's latest, and returns how many it marked. Idempotent and order-independent.
    /// </summary>
    Task<int> ApplyRecipeChangeAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>The proposal's accepted content and whether it is current now; null when not visible.</summary>
    Task<ContentCurrencyServiceModel?> GetCurrencyAsync(Guid proposalId, CancellationToken cancellationToken);
}

internal sealed class ContentStalenessBusiness(IContentStalenessDataLayer dataLayer, IClock clock) : IContentStalenessBusiness
{
    public async Task<int> ApplyRecipeChangeAsync(Guid recipeId, CancellationToken cancellationToken)
    {
        // Against the recipe's latest version as it stands now, not the version an event named. Delivery is
        // at-least-once and unordered, so an old event replayed after a newer change, or after a creator
        // reaffirmed their content, must not flip anything — and a newer event must not be missed because an
        // older one was handled first.
        var candidates = await dataLayer.FindCandidatesAsync(recipeId, cancellationToken);

        if (candidates.Latest is not { } latest)
        {
            return 0;
        }

        // System-only by rule: there is no role a creator could use to declare their own content stale.
        if (ContentProposalTransitions.Find(ContentProposalStatus.Accepted, ContentProposalStatus.NeedsReview) is not { MinimumRole: null })
        {
            throw new InvalidOperationException("The staleness move is no longer a system-only transition.");
        }

        var marked = 0;

        foreach (var pin in candidates.Accepted.Where(pin => ContentCurrency.IsStale(pin.PinnedVersionNumber, latest.VersionNumber)))
        {
            // One save per proposal: one broken proposal must not roll back the ones already decided, and the
            // retry that follows a throw re-reads and skips everything already marked.
            var applied = await dataLayer.MarkNeedsReviewAsync(
                new ContentStalenessChange(pin.ProposalId, ContentStaleReasons.RecipeChanged, clock.UtcNow),
                cancellationToken);

            if (applied)
            {
                marked++;
            }
        }

        return marked;
    }

    public async Task<ContentCurrencyServiceModel?> GetCurrencyAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        var facts = await dataLayer.FindCurrencyFactsAsync(proposalId, cancellationToken);

        if (facts is null)
        {
            return null;
        }

        // Nothing accepted means nothing to call current or stale.
        bool? isCurrent = facts.Pin is { } pin && facts.Latest is { } latest
            ? facts.Status == ContentProposalStatus.Accepted && !ContentCurrency.IsStale(pin.PinnedVersionNumber, latest.VersionNumber)
            : null;

        return new ContentCurrencyServiceModel(
            facts.ProposalId,
            facts.Status,
            facts.AcceptedRevisionId,
            facts.Pin?.PinnedVersionId,
            facts.Latest?.Id,
            isCurrent);
    }
}
