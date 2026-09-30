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
