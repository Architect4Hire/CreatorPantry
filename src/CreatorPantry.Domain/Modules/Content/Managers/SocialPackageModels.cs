using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Data.Entities;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a channel writing profile said about one body. Supplied by whoever measured it; never computed here.
/// </summary>
/// <param name="CharacterCount">The body's length as the profile counts it, which need not be string length.</param>
/// <param name="CharacterLimit">The limit applied, or null when the profile sets none.</param>
public sealed record SocialLimitResult(
    int CharacterCount, int? CharacterLimit, SocialLimitStatus Status, string ChannelProfileVersion)
{
    /// <summary>
    /// What a revision stores of a channel-profile measurement (AF.6.2): the count, the limit, the verdict
    /// and the profile version that gave it. The findings beyond length are the reviewer's to see when the
    /// body is measured and are not stored: they are re-derivable from the body and that version.
    /// </summary>
    public static SocialLimitResult From(ChannelWritingResult measured)
    {
        ArgumentNullException.ThrowIfNull(measured);

        return new SocialLimitResult(
            measured.Count,
            measured.Limit,
            measured.IsOverLimit ? SocialLimitStatus.Over : SocialLimitStatus.Within,
            measured.ProfileVersion);
    }
}

/// <summary>
/// One generated body to store for one channel, with everything that says where it came from.
/// </summary>
/// <remarks>
/// No workspace field, and none could arrive in one: ownership is the resolved context's. The recipe pin is
/// the version the caller actually grounded on — read from the assembled context package, not re-resolved
/// here — and is refused unless the context names that recipe.
/// </remarks>
public sealed record SocialGeneratedRevisionInput(
    Guid CreativeContextId,
    string ChannelKey,
    string Body,
    SocialLimitResult? Limit,
    Guid AiProposalId,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string PromptTemplateBodyChecksum,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    Guid? BrandProfileRevisionId,
    Guid? BrandStyleGuideVersionId,
    string? CreativeContextVersion,
    string? ContextPackageChecksum);

/// <summary>
/// The creator's own wording for one channel.
/// </summary>
/// <param name="ExpectedLatestRevisionId">
/// The revision this edit was composed against, or null for the first words on a channel. An edit of anything
/// but the latest revision is refused rather than silently stacked on words its author never saw.
/// </param>
public sealed record SocialEditedRevisionInput(
    Guid CreativeContextId,
    string ChannelKey,
    string Body,
    SocialLimitResult? Limit,
    Guid? ExpectedLatestRevisionId);

/// <summary>One revision as a caller sees it. Drops the workspace and the membership that wrote it.</summary>
public sealed record SocialRevisionServiceModel(
    Guid Id,
    int RevisionNumber,
    Guid? ParentRevisionId,
    ContentRevisionSource Source,
    string Body,
    int? CharacterCount,
    int? CharacterLimit,
    SocialLimitStatus LimitStatus,
    string? ChannelProfileVersion,
    Guid? AiProposalId,
    string? PromptTemplateId,
    string? PromptTemplateVersion,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    Guid? BrandProfileRevisionId,
    Guid? BrandStyleGuideVersionId,
    DateTimeOffset CreatedAt);

/// <summary>
/// One channel's output and where it stands.
/// </summary>
/// <param name="Latest">The newest revision: what a decision would be about.</param>
/// <param name="Accepted">The revision the creator last accepted, kept through every later move; or null.</param>
/// <param name="IsCurrent">
/// Null when nothing has been accepted. False for a <c>NeedsReview</c> channel, and also for an
/// <c>Accepted</c> one whose recipe has moved on but whose flag has not been set yet — decided from the pin,
/// never from the status alone (<see cref="ContentCurrency"/>).
/// </param>
public sealed record SocialChannelServiceModel(
    string ChannelKey,
    ContentProposalStatus Status,
    SocialRevisionServiceModel Latest,
    SocialRevisionServiceModel? Accepted,
    bool? IsCurrent,
    DateTimeOffset? StaleSince,
    ContentStaleReasons StaleReasons,
    DateTimeOffset UpdatedAt);

/// <summary>The posts written for one creative context, one entry per channel the creator picked.</summary>
public sealed record SocialPackageServiceModel(
    Guid Id,
    Guid CreativeContextId,
    IReadOnlyList<SocialChannelServiceModel> Channels,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>What the write seam needs to know about the context a post is for.</summary>
/// <param name="RecipeIds">The recipes the context names, in the creator's order. The first is the one posts pin.</param>
public sealed record SocialContextFacts(IReadOnlyList<Guid> RecipeIds);

/// <summary>
/// A package as the data layer reads it: the slots, each one's latest and accepted revisions, and the version
/// numbers that decide currency — read together so they describe one moment.
/// </summary>
/// <param name="PinnedVersionNumbers">By recipe version id, for every accepted revision that has a pin.</param>
/// <param name="LatestVersionNumbers">By recipe id, for the same revisions.</param>
public sealed record SocialPackageSnapshot(
    SocialPackage Package,
    IReadOnlyList<SocialRevision> Heads,
    IReadOnlyDictionary<Guid, int> PinnedVersionNumbers,
    IReadOnlyDictionary<Guid, int> LatestVersionNumbers);

/// <summary>An accepted channel and the recipe version its accepted revision is pinned to.</summary>
public sealed record AcceptedSocialPinRecord(
    Guid ChannelId, Guid AcceptedRevisionId, Guid PinnedVersionId, int PinnedVersionNumber);

/// <summary>What the data layer needs to mark one channel stale.</summary>
public sealed record SocialStalenessChange(Guid ChannelId, ContentStaleReasons Reasons, DateTimeOffset At);
