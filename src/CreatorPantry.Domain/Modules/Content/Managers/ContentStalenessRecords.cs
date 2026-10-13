using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>A recipe's newest version, as far as staleness needs to know it.</summary>
public sealed record LatestRecipeVersionRecord(Guid Id, int VersionNumber);

/// <summary>An accepted proposal and the recipe version its accepted revision is pinned to.</summary>
public sealed record AcceptedPinRecord(
    Guid ProposalId, Guid AcceptedRevisionId, Guid PinnedVersionId, int PinnedVersionNumber);

/// <summary>What the data layer needs to mark one proposal stale and write the history row.</summary>
public sealed record ContentStalenessChange(
    Guid ProposalId, ContentStaleReasons Reasons, DateTimeOffset At);

/// <summary>
/// One long-form draft with an accepted revision, and the version of one recipe that revision is pinned to
/// (LF.6.1). A draft pinned to the recipe more than once appears once per pin.
/// </summary>
/// <param name="StaleReasons">What is already recorded against the draft; none while it is Accepted.</param>
public sealed record LongFormRecipePinRecord(Guid DraftId, ContentStaleReasons StaleReasons, int PinnedVersionNumber);

/// <summary>One long-form draft with an accepted revision, and the style guide version it was written under.</summary>
/// <param name="BrandStyleGuideVersionId">Null when it was written with no guide in force.</param>
public sealed record LongFormVoicePinRecord(Guid DraftId, ContentStaleReasons StaleReasons, Guid? BrandStyleGuideVersionId);

/// <summary>What the data layer needs to record one moved pin against one long-form draft.</summary>
/// <param name="Reason">The one pin that moved. Added to whatever is already recorded.</param>
public sealed record LongFormStalenessChange(Guid DraftId, ContentStaleReasons Reason, DateTimeOffset At);

/// <summary>A recipe's latest version and the accepted proposals derived from it.</summary>
public sealed record ContentStalenessCandidates(
    LatestRecipeVersionRecord? Latest, IReadOnlyList<AcceptedPinRecord> Accepted);

/// <summary>A proposal's currency inputs, read together so they describe one moment.</summary>
public sealed record ContentCurrencyFacts(
    Guid ProposalId,
    ContentProposalStatus Status,
    Guid? AcceptedRevisionId,
    AcceptedPinRecord? Pin,
    LatestRecipeVersionRecord? Latest);
