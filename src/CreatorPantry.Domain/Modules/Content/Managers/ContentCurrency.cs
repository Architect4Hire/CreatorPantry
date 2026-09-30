using CreatorPantry.Domain.Modules.Content.Data.Entities;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Whether accepted content still describes the recipe as it stands, decided from what it is pinned to rather
/// than from <see cref="ContentProposal.Status"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The hard guarantee behind an eventually-consistent flag.</strong> Staleness is marked by a durable
/// job after the recipe commits, so for a moment — or for as long as a poisoned delivery waits — a proposal can
/// read <c>Accepted</c> while its recipe has moved on. Anything that trusts accepted content (acceptance,
/// confirming a publication) must ask this, not read <c>Status</c> alone; then a status that lags can never be
/// mistaken for currency.
/// </para>
/// <para>
/// Versions are compared by number, which is sequential per recipe and never reused. A restore writes a newer
/// number, so restoring old content still makes derivatives of the previous state stale.
/// </para>
/// </remarks>
public static class ContentCurrency
{
    /// <summary>True when the recipe has a version newer than the one the content was written against.</summary>
    public static bool IsStale(int pinnedVersionNumber, int latestVersionNumber) =>
        pinnedVersionNumber < latestVersionNumber;
}

/// <summary>
/// One proposal's accepted content and whether it is current right now.
/// </summary>
/// <param name="IsCurrent">
/// Null when nothing has been accepted. False for a <c>NeedsReview</c> proposal, and also for an
/// <c>Accepted</c> one whose recipe has moved on but whose flag has not been set yet.
/// </param>
public sealed record ContentCurrencyServiceModel(
    Guid ProposalId,
    ContentProposalStatus Status,
    Guid? AcceptedRevisionId,
    Guid? PinnedRecipeVersionId,
    Guid? LatestRecipeVersionId,
    bool? IsCurrent);
