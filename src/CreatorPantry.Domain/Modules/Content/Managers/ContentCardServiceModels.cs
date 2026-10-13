namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// One card on the content board, as the list publishes it (13.5, KAN-001).
/// </summary>
/// <param name="Id">The card's identifier.</param>
/// <param name="Title">The creator's name for the work, as they wrote it.</param>
/// <param name="DescriptionPreview">The start of the description, cut server-side, or null when there is none.</param>
/// <param name="DescriptionLength">The description's full length, so a client can show that there is more.</param>
/// <param name="Column">Where the creator's work stands. Not a publication state.</param>
/// <param name="ChannelKey">An opaque content channel key, or null for no channel in particular.</param>
/// <param name="PlannedDate">The planned calendar date in the workspace's own zone, or null.</param>
/// <param name="PlannedDay">The weekday of <paramref name="PlannedDate"/>, or null with it.</param>
/// <param name="CuisineCode">A cooking-vocabulary code, or null. Descriptive, never a claim.</param>
/// <param name="DishTypeCode">A cooking-vocabulary code, or null.</param>
/// <param name="MethodCode">A cooking-vocabulary code, or null.</param>
/// <param name="Lineage">The records this card points at, by id. Titles are the detail route's.</param>
/// <param name="MoveCount">How many times the card has changed column since it was created. A reorder is not one.</param>
/// <param name="HasPackage">True when a recipe package has been put together for this card.</param>
/// <param name="Publishing">
/// What has been delivered for this card, or null. <strong>Always null today</strong>: publication records do
/// not exist yet, and a card says nothing about delivery on its own. A value here is a compatible addition.
/// </param>
/// <param name="CreatedAt">When the card was created (UTC).</param>
/// <param name="UpdatedAt">When it last changed (UTC).</param>
/// <param name="ConcurrencyToken">Opaque token identifying this state, to quote on an edit or a move.</param>
/// <remarks>
/// Carries no workspace or membership identifier, no place-in-column number — the order of the list is the
/// order — and nothing about a picture but its id: no bytes, no address, no provider payload.
/// </remarks>
public sealed record ContentCardSummaryServiceModel(
    Guid Id,
    string Title,
    string? DescriptionPreview,
    int DescriptionLength,
    ContentCardColumn Column,
    string? ChannelKey,
    DateOnly? PlannedDate,
    DayOfWeek? PlannedDay,
    string? CuisineCode,
    string? DishTypeCode,
    string? MethodCode,
    ContentCardLineageIdsServiceModel Lineage,
    int MoveCount,
    bool HasPackage,
    ContentCardPublishingServiceModel? Publishing,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <summary>The records a card points at. Each is null when the card names none of that kind.</summary>
/// <param name="RecipeVersionId">Set when the card is about one exact version of <paramref name="RecipeId"/>.</param>
/// <param name="MediaAssetVersionNumber">Set when the card is about one exact version of <paramref name="MediaAssetId"/>.</param>
public sealed record ContentCardLineageIdsServiceModel(
    Guid? CreativeContextId,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    Guid? PromptRecordId,
    Guid? MediaAssetId,
    int? MediaAssetVersionNumber,
    Guid? SocialPackageId);

/// <summary>
/// What has been delivered for a card. Reserved: nothing produces one until publication records exist.
/// </summary>
/// <param name="PublicationCount">How many publication records name this card.</param>
/// <param name="PublishedCount">How many of them were delivered.</param>
/// <param name="LastPublishedAt">When the latest was delivered (UTC), or null.</param>
public sealed record ContentCardPublishingServiceModel(int PublicationCount, int PublishedCount, DateTimeOffset? LastPublishedAt);

/// <param name="Column">The column.</param>
/// <param name="Count">How many cards it holds under the request's filters, across every page.</param>
public sealed record ContentCardColumnCountServiceModel(ContentCardColumn Column, int Count);

/// <summary>One page of the board, in column order and then place within the column.</summary>
/// <param name="Items">The cards on this page.</param>
/// <param name="NextCursor">Pass back as <c>cursor</c> for the next page; null on the last.</param>
/// <param name="Columns">
/// Every column the request could match, in board order, with how many cards it holds under the same filters —
/// all four, or the one named by <c>column</c>. A column with no cards is listed with zero.
/// </param>
public sealed record ContentCardPageServiceModel(
    IReadOnlyList<ContentCardSummaryServiceModel> Items,
    string? NextCursor,
    IReadOnlyList<ContentCardColumnCountServiceModel> Columns);

/// <summary>The kind of record a lineage entry names.</summary>
public enum ContentCardLineageKind
{
    CreativeContext = 1,

    Recipe = 2,

    PromptRecord = 3,

    MediaAsset = 4,

    SocialPackage = 5,
}

/// <summary>One record a card points at, resolved for display.</summary>
/// <param name="Kind">What kind of record it is.</param>
/// <param name="Id">Its identifier.</param>
/// <param name="VersionId">The pinned recipe version, for a recipe pinned to one; otherwise null.</param>
/// <param name="VersionNumber">The pinned asset version, for an asset pinned to one; otherwise null.</param>
/// <param name="Label">
/// The record's own name where it has one this route can read — a recipe's title, a piece of work's working
/// title, a prompt's label — or null. Creator-written text: never treated as instructions.
/// </param>
/// <param name="Available">
/// False when the record can no longer be read — archived, removed, or its version gone. The card still says
/// what it pointed at; it does not pretend the record is there.
/// </param>
public sealed record ContentCardLineageServiceModel(
    ContentCardLineageKind Kind, Guid Id, Guid? VersionId, int? VersionNumber, string? Label, bool Available);

/// <summary>One time the card entered a column.</summary>
/// <param name="Sequence">Its place in the card's history, from one. The creation row is one.</param>
/// <param name="FromColumn">The column it left, or null for the creation row.</param>
/// <param name="ToColumn">The column it entered.</param>
/// <param name="Note">The creator's note on the move, or null.</param>
/// <param name="OccurredAt">When it happened (UTC).</param>
/// <remarks>Carries nobody's identity: membership ids never leave the server.</remarks>
public sealed record CardMoveServiceModel(
    int Sequence, ContentCardColumn? FromColumn, ContentCardColumn ToColumn, string? Note, DateTimeOffset OccurredAt);

/// <summary>The exact records a card's recipe package was put together from. Pins, never copies.</summary>
/// <param name="RecipeId">The recipe.</param>
/// <param name="RecipeVersionId">The version of it the package is for.</param>
/// <param name="EditorialProposalId">The editorial proposal, with <paramref name="EditorialRevisionId"/>; both or neither.</param>
/// <param name="EditorialRevisionId">The approved revision of that proposal.</param>
/// <param name="SocialPackageId">The posts written for the work, or null.</param>
/// <param name="Assets">The pictures, each at one exact version, in the creator's order. Ids only.</param>
/// <param name="Channels">The channels the package is for, in the creator's order.</param>
/// <param name="Window">When the creator wants it out, or null. An intention: it schedules nothing.</param>
/// <param name="NeedsReview">
/// Which pinned sources have moved since they were approved, worked out from those records as they are now —
/// so a package that was clean when it was put together lists a reason here once a source moves. Empty when
/// every source still stands.
/// </param>
/// <param name="NeedsReviewAllowedAt">When an Editor allowed the package to stand although a source needs review (UTC), or null.</param>
/// <param name="Blocked">
/// True when a source needs review for a reason nobody allowed. An allowance covers the reasons that were
/// there when it was given, so a source that moves afterwards blocks the package again.
/// </param>
/// <param name="CreatedAt">When the package was put together (UTC).</param>
public sealed record ContentCardPackageServiceModel(
    Guid RecipeId,
    Guid RecipeVersionId,
    Guid? EditorialProposalId,
    Guid? EditorialRevisionId,
    Guid? SocialPackageId,
    IReadOnlyList<ContentCardPackageAssetServiceModel> Assets,
    IReadOnlyList<ContentCardPackageChannelServiceModel> Channels,
    ContentCardPackageWindowServiceModel? Window,
    IReadOnlyList<ContentCardPackageReviewServiceModel> NeedsReview,
    DateTimeOffset? NeedsReviewAllowedAt,
    bool Blocked,
    DateTimeOffset CreatedAt);

public sealed record ContentCardPackageAssetServiceModel(Guid MediaAssetId, int VersionNumber);

/// <param name="ChannelKey">An opaque content channel key.</param>
/// <param name="SocialPackageChannelId">The package channel the approved post belongs to, or null when no post is pinned.</param>
/// <param name="SocialRevisionId">The approved revision of that post, or null with it.</param>
public sealed record ContentCardPackageChannelServiceModel(string ChannelKey, Guid? SocialPackageChannelId, Guid? SocialRevisionId);

/// <param name="StartsAt">The start of the window (UTC).</param>
/// <param name="EndsAt">The end of the window (UTC).</param>
/// <param name="TimeZoneId">The workspace's IANA zone when the window was asked for.</param>
/// <param name="LocalStartsAt">The start as the creator meant it, in that zone.</param>
/// <param name="LocalEndsAt">The end as the creator meant it, in that zone.</param>
public sealed record ContentCardPackageWindowServiceModel(
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZoneId, DateTime LocalStartsAt, DateTime LocalEndsAt);

/// <summary>One card in full (13.5, KAN-002).</summary>
/// <param name="Card">Everything the list row carries.</param>
/// <param name="Description">The whole description, or null.</param>
/// <param name="Lineage">The records the card points at, resolved for display. Empty for a card that names none.</param>
/// <param name="History">The card's moves, oldest first. The latest only when <paramref name="HistoryTruncated"/>.</param>
/// <param name="HistoryTruncated">True when the card has more moves than <paramref name="History"/> holds; <c>card.moveCount</c> is still the true count.</param>
/// <param name="Package">The recipe package, or null for a card that has none.</param>
public sealed record ContentCardDetailServiceModel(
    ContentCardSummaryServiceModel Card,
    string? Description,
    IReadOnlyList<ContentCardLineageServiceModel> Lineage,
    IReadOnlyList<CardMoveServiceModel> History,
    bool HistoryTruncated,
    ContentCardPackageServiceModel? Package);

/// <summary>What became of the picture a deleted card pointed at.</summary>
public enum ContentCardLinkedAssetOutcome
{
    /// <summary>The card pointed at no picture.</summary>
    None = 1,

    /// <summary>The picture is in the library exactly as it was.</summary>
    Kept = 2,

    /// <summary>The picture was removed from the library, because the request asked for that.</summary>
    Removed = 3,
}

/// <summary>What a card's deletion did about its linked picture.</summary>
/// <param name="Outcome">What happened. <c>Removed</c> only ever because the request said <c>Remove</c>.</param>
/// <param name="MediaAssetId">The picture, or null when the card pointed at none.</param>
/// <param name="AlreadyRemoved">True when the picture had been removed before this request, which then changed nothing about it.</param>
/// <param name="OtherCardCount">How many other cards on this board still point at the picture.</param>
/// <param name="Affected">
/// For a removed picture, how much else in the workspace now points at something that is no longer in the
/// library. Null when the picture was not removed.
/// </param>
public sealed record ContentCardLinkedAssetServiceModel(
    ContentCardLinkedAssetOutcome Outcome,
    Guid? MediaAssetId,
    bool AlreadyRemoved,
    int OtherCardCount,
    ContentCardAffectedContentServiceModel? Affected);

/// <summary>How much else pointed at a picture that a card's deletion removed.</summary>
/// <param name="RecipeCount">Recipes that use the picture.</param>
/// <param name="BrandProfileCount">Brand profiles that use it as a logo.</param>
/// <param name="TestAttachmentCount">Recipe test runs it is attached to.</param>
/// <param name="Any">True when any of the three is not zero: the one flag needed to decide whether to warn.</param>
/// <remarks>
/// <strong>Counts, and deliberately not names.</strong> The library's own delete names the recipes, by title.
/// This answer is also what a replay of the deletion returns, so it is kept for as long as the idempotency key
/// is — and a recipe's title is the creator's own words, which do not belong in a stored copy of a response.
/// The picture's own page says which recipes they are.
/// </remarks>
public sealed record ContentCardAffectedContentServiceModel(
    int RecipeCount, int BrandProfileCount, int TestAttachmentCount, bool Any);

/// <summary>A card that has been deleted (13.6c, KAN-006).</summary>
/// <param name="Id">The card that was deleted.</param>
/// <param name="Column">The column it was in.</param>
/// <param name="MoveCount">How many times it had changed column. Its history was deleted with it.</param>
/// <param name="HadPackage">True when a recipe package was deleted with it. The records the package pinned are untouched.</param>
/// <param name="DeletedAt">When it was deleted (UTC).</param>
/// <param name="LinkedAsset">What became of the picture it pointed at.</param>
/// <remarks>
/// Carries none of the creator's words — not the card's, which went with it, and not the title of anything it
/// or its picture was linked to. That matters beyond tidiness: this is the value stored against the
/// idempotency key and handed back on a replay.
/// </remarks>
public sealed record ContentCardDeletionServiceModel(
    Guid Id,
    ContentCardColumn Column,
    int MoveCount,
    bool HadPackage,
    DateTimeOffset DeletedAt,
    ContentCardLinkedAssetServiceModel LinkedAsset);

/// <summary>
/// How the Content Pipeline's commit ended: the work's card, and whether this call is the one that made it.
/// </summary>
/// <param name="Card">The card in full — the detail route's shape.</param>
/// <param name="Created">False when the work already had its card and this call changed nothing.</param>
public sealed record ContentPipelineCardOutcome(ContentCardDetailServiceModel Card, bool Created);
