using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IContentStalenessBusiness
{
    /// <summary>
    /// Marks NeedsReview every Accepted proposal of the recipe, and every Accepted post channel pinned to it,
    /// whose accepted content was written against an older version than the recipe's latest, and returns how
    /// many it marked. Idempotent and order-independent.
    /// </summary>
    Task<int> ApplyRecipeChangeAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks NeedsReview every accepted long-form draft whose accepted revision was written under a brand
    /// style guide version other than <paramref name="activeVersionId"/>, and returns how many it marked
    /// (LF.6.1). Idempotent and order-independent.
    /// </summary>
    /// <param name="activeVersionId">
    /// The workspace's active style guide version as it stands now, or null when it has none. Read by the
    /// caller from the brand module, never taken from an event.
    /// </param>
    Task<int> ApplyVoiceChangeAsync(Guid? activeVersionId, CancellationToken cancellationToken);

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

        // Posts follow the same rule from the same event (AF.6.1), each channel on its own: one channel of a
        // package may be pinned to an older version than its neighbour, and only the stale one is marked.
        var socialPins = await dataLayer.FindAcceptedSocialPinsAsync(recipeId, cancellationToken);

        foreach (var pin in socialPins.Where(pin => ContentCurrency.IsStale(pin.PinnedVersionNumber, latest.VersionNumber)))
        {
            var applied = await dataLayer.MarkSocialNeedsReviewAsync(
                new SocialStalenessChange(pin.ChannelId, ContentStaleReasons.RecipeChanged, clock.UtcNow),
                cancellationToken);

            if (applied)
            {
                marked++;
            }
        }

        // Long-form articles follow the same rule from the same event (LF.6.1), the document as the unit: a
        // piece is stale when any recipe it was written from has moved on, and it is marked once however many
        // of its pins name this recipe. One already marked for another reason gains this one.
        var longForm = await dataLayer.FindLongFormRecipePinsAsync(recipeId, cancellationToken);

        foreach (var draft in longForm
            .Where(pin => ContentCurrency.IsStale(pin.PinnedVersionNumber, latest.VersionNumber))
            .Where(pin => !pin.StaleReasons.HasFlag(ContentStaleReasons.RecipeChanged))
            .Select(pin => pin.DraftId)
            .Distinct())
        {
            var applied = await dataLayer.MarkLongFormNeedsReviewAsync(
                new LongFormStalenessChange(draft, ContentStaleReasons.RecipeChanged, clock.UtcNow),
                cancellationToken);

            if (applied)
            {
                marked++;
            }
        }

        return marked;
    }

    public async Task<int> ApplyVoiceChangeAsync(Guid? activeVersionId, CancellationToken cancellationToken)
    {
        // Against the active version as it stands now. Null is a value on both sides, as ContentSourcePins
        // has it: a piece written with no guide is not current against one activated since.
        var pins = await dataLayer.FindLongFormVoicePinsAsync(cancellationToken);
        var marked = 0;

        foreach (var pin in pins.Where(pin =>
            pin.BrandStyleGuideVersionId != activeVersionId && !pin.StaleReasons.HasFlag(ContentStaleReasons.VoiceChanged)))
        {
            // One save per draft, as for a recipe change: one broken draft must not roll back the ones
            // already decided, and the retry that follows a throw skips everything already marked.
            var applied = await dataLayer.MarkLongFormNeedsReviewAsync(
                new LongFormStalenessChange(pin.DraftId, ContentStaleReasons.VoiceChanged, clock.UtcNow),
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
