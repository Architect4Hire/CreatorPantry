using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The body of <c>POST .../content-cards/recipe-packages</c> (13.7, RCPUB-006): a card for one recipe, put
/// together from the exact versions the creator approved.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every version is named by the client and verified by the server.</strong> The creator packages what
/// they were looking at; the server checks that each is the approved one and does not go and find something
/// newer. Nothing is copied into the package — it holds ids and version pins, so it can always be traced back
/// to what it was made from.
/// </para>
/// <para>
/// There is no workspace, author or place in the column here, and no time zone: the window is sent as the
/// local times the creator means, and converted with the workspace's own zone.
/// </para>
/// </remarks>
public sealed record CreateRecipePackageViewModel
{
    public Guid? RecipeId { get; init; }

    /// <summary>The version of <see cref="RecipeId"/> the package is for. Required: a package is never for "the recipe as it stands".</summary>
    public Guid? RecipeVersionId { get; init; }

    /// <summary>The piece of work the posts were written for. Required when any channel pins a post.</summary>
    public Guid? CreativeContextId { get; init; }

    /// <summary>The approved revision of this recipe's editorial package, when the package includes one.</summary>
    public Guid? EditorialRevisionId { get; init; }

    /// <summary>Where the work is going, in the creator's order. May be empty.</summary>
    public IReadOnlyList<RecipePackageChannelInput?>? Channels { get; init; }

    /// <summary>The pictures, each at one exact version, in the creator's order. May be empty.</summary>
    public IReadOnlyList<RecipePackageAssetInput?>? Assets { get; init; }

    /// <summary>When the creator wants it out. An intention: it schedules nothing.</summary>
    public RecipePackageWindowInput? Window { get; init; }

    /// <summary>The card's title. Left out, the card takes the recipe's own title.</summary>
    public string? Title { get; init; }

    /// <summary>The column to start in. Left out, <see cref="ContentCardColumn.ToDo"/>.</summary>
    public ContentCardColumn? Column { get; init; }

    /// <summary>
    /// True to let the package stand although a source it pins needs review. An Editor's decision, recorded
    /// with who made it and when. Left out or false, such a package is created blocked.
    /// </summary>
    public bool? AllowNeedsReview { get; init; }
}

/// <param name="ChannelKey">A content channel key the catalogue offers.</param>
/// <param name="SocialRevisionId">The approved revision of this channel's post, or null to name the channel with no post pinned.</param>
public sealed record RecipePackageChannelInput(string? ChannelKey, Guid? SocialRevisionId);

/// <param name="MediaAssetId">The picture.</param>
/// <param name="VersionNumber">The exact version of it. Required: a package is never for "the picture as it stands".</param>
public sealed record RecipePackageAssetInput(Guid? MediaAssetId, int? VersionNumber);

/// <param name="LocalStartsAt">The start as the creator means it, in the workspace's own zone, with no offset.</param>
/// <param name="LocalEndsAt">The end, likewise.</param>
public sealed record RecipePackageWindowInput(DateTime? LocalStartsAt, DateTime? LocalEndsAt);

/// <summary>Why a recipe package is blocked: which pinned source has moved since it was approved.</summary>
public enum ContentCardPackageReviewReason
{
    /// <summary>The recipe has a newer version than the one the package pins.</summary>
    RecipeVersionSuperseded = 1,

    /// <summary>The recipe itself can no longer be read — archived or removed.</summary>
    RecipeUnavailable = 2,

    /// <summary>The editorial package has been marked as needing review, or a different revision is now the approved one.</summary>
    EditorialNeedsReview = 3,

    /// <summary>The editorial revision was written against a different version of the recipe than the one pinned.</summary>
    EditorialForAnotherRecipeVersion = 4,

    /// <summary>A channel's post has been marked as needing review, or a different revision is now the approved one.</summary>
    PostNeedsReview = 5,

    /// <summary>A channel's post was written against a different version of the recipe than the one pinned.</summary>
    PostForAnotherRecipeVersion = 6,
}

/// <summary>One reason a package is blocked.</summary>
/// <param name="Reason">What moved.</param>
/// <param name="ChannelKey">The channel, for a reason about one channel's post; otherwise null.</param>
public sealed record ContentCardPackageReviewServiceModel(ContentCardPackageReviewReason Reason, string? ChannelKey);

/// <summary>An editorial revision as a package may pin it: which proposal it belongs to and where that proposal stands.</summary>
public sealed record EditorialPinRecord(
    Guid RevisionId,
    Guid ProposalId,
    Guid RecipeId,
    ContentPackageKind Kind,
    ContentProposalStatus Status,
    Guid? AcceptedRevisionId,
    Guid RevisionRecipeVersionId);

/// <summary>A post revision as a package may pin it: which channel of which piece of work, and where that channel stands.</summary>
public sealed record SocialPinRecord(
    Guid RevisionId,
    Guid ChannelId,
    string ChannelKey,
    Guid SocialPackageId,
    Guid CreativeContextId,
    ContentProposalStatus Status,
    Guid? AcceptedRevisionId,
    Guid? RevisionRecipeId,
    Guid? RevisionRecipeVersionId);

/// <summary>
/// Whether a package's pinned sources still stand, worked out from the pinned records as they are now.
/// Deterministic and free of I/O, so the same states always give the same reasons in the same order.
/// </summary>
public static class RecipePackageReview
{
    private const char ReasonSeparator = ';';
    private const char ChannelSeparator = ':';

    /// <summary>
    /// The reasons as one stored string: each as its name, with its channel where it has one, in a fixed order.
    /// What an allowance records, so it can later be told which reasons somebody actually looked at.
    /// </summary>
    public static string Encode(IEnumerable<ContentCardPackageReviewServiceModel> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        return string.Join(ReasonSeparator, reasons.Select(Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether a package is blocked: a source needs review for a reason nobody allowed.
    /// </summary>
    /// <param name="reasons">Why the package needs review now.</param>
    /// <param name="allowedReasons">The reasons an Editor allowed, as <see cref="Encode"/> wrote them, or null when nobody has.</param>
    public static bool IsBlocked(IReadOnlyList<ContentCardPackageReviewServiceModel> reasons, string? allowedReasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        if (reasons.Count == 0)
        {
            return false;
        }

        var allowed = new HashSet<string>(
            (allowedReasons ?? string.Empty).Split(ReasonSeparator, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        // An allowance is of what was looked at: one reason outside it blocks the package again.
        return reasons.Any(reason => !allowed.Contains(Key(reason)));
    }

    private static string Key(ContentCardPackageReviewServiceModel reason) =>
        reason.ChannelKey is null ? reason.Reason.ToString() : $"{reason.Reason}{ChannelSeparator}{reason.ChannelKey}";

    /// <param name="pinnedRecipeVersionId">The recipe version the package is for.</param>
    /// <param name="recipeAvailable">False when the recipe can no longer be read.</param>
    /// <param name="currentRecipeVersionId">The recipe's current version, or null when that is not known.</param>
    /// <param name="editorial">The pinned editorial revision's state, or null when none is pinned or it has gone.</param>
    /// <param name="editorialPinned">True when the package pins an editorial revision at all.</param>
    /// <param name="posts">Each channel that pins a post, with that post's state — null when it has gone.</param>
    public static IReadOnlyList<ContentCardPackageReviewServiceModel> Reasons(
        Guid pinnedRecipeVersionId,
        bool recipeAvailable,
        Guid? currentRecipeVersionId,
        bool editorialPinned,
        EditorialPinRecord? editorial,
        IReadOnlyList<(string ChannelKey, Guid RevisionId, SocialPinRecord? Pin)> posts)
    {
        ArgumentNullException.ThrowIfNull(posts);

        var reasons = new List<ContentCardPackageReviewServiceModel>();

        if (!recipeAvailable)
        {
            reasons.Add(new(ContentCardPackageReviewReason.RecipeUnavailable, null));
        }
        else if (currentRecipeVersionId is { } current && current != pinnedRecipeVersionId)
        {
            reasons.Add(new(ContentCardPackageReviewReason.RecipeVersionSuperseded, null));
        }

        if (editorialPinned)
        {
            // A revision that is no longer the approved one — or cannot be found — is one that needs review,
            // not one that can be assumed to stand.
            if (editorial is null
                || editorial.Status != ContentProposalStatus.Accepted
                || editorial.AcceptedRevisionId != editorial.RevisionId)
            {
                reasons.Add(new(ContentCardPackageReviewReason.EditorialNeedsReview, null));
            }

            if (editorial is not null && editorial.RevisionRecipeVersionId != pinnedRecipeVersionId)
            {
                reasons.Add(new(ContentCardPackageReviewReason.EditorialForAnotherRecipeVersion, null));
            }
        }

        foreach (var (channelKey, revisionId, pin) in posts)
        {
            if (pin is null || pin.Status != ContentProposalStatus.Accepted || pin.AcceptedRevisionId != revisionId)
            {
                reasons.Add(new(ContentCardPackageReviewReason.PostNeedsReview, channelKey));
            }

            // A post written for no recipe in particular is not written for the wrong one.
            if (pin?.RevisionRecipeVersionId is { } writtenFor && writtenFor != pinnedRecipeVersionId)
            {
                reasons.Add(new(ContentCardPackageReviewReason.PostForAnotherRecipeVersion, channelKey));
            }
        }

        return reasons;
    }
}

/// <summary>The shape rules for a recipe package, as <c>(field, message)</c>.</summary>
public static class RecipePackageInputChecks
{
    public static IEnumerable<(string Field, string Message)> Create(CreateRecipePackageViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.RecipeId is not { } recipeId || recipeId == Guid.Empty)
        {
            yield return ("recipeId", "Name the recipe the package is for.");
        }

        if (model.RecipeVersionId is not { } versionId || versionId == Guid.Empty)
        {
            yield return ("recipeVersionId", "Name the exact version of the recipe the package is for.");
        }

        if (model.CreativeContextId == Guid.Empty)
        {
            yield return ("creativeContextId", "That is not an identifier.");
        }

        if (model.EditorialRevisionId == Guid.Empty)
        {
            yield return ("editorialRevisionId", "That is not an identifier.");
        }

        if (ContentCardInputChecks.Normalize(model.Title) is { Length: > ContentCardPolicy.TitleMaxLength })
        {
            yield return ("title", $"A title can be at most {ContentCardPolicy.TitleMaxLength} characters.");
        }

        if (model.Column is { } column && !Enum.IsDefined(column))
        {
            yield return ("column", "Name a column: ToDo, InProcess, ReadyForReview or Approved.");
        }

        foreach (var failure in Channels(model))
        {
            yield return failure;
        }

        foreach (var failure in Assets(model.Assets))
        {
            yield return failure;
        }

        foreach (var failure in Window(model.Window))
        {
            yield return failure;
        }
    }

    private static IEnumerable<(string, string)> Channels(CreateRecipePackageViewModel model)
    {
        var channels = model.Channels ?? [];

        if (channels.Count > ContentCardPolicy.MaxPackageChannels)
        {
            yield return ("channels", $"A package can name at most {ContentCardPolicy.MaxPackageChannels} channels.");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < channels.Count; index++)
        {
            var key = ContentCardInputChecks.Normalize(channels[index]?.ChannelKey);

            if (key is null || key.Length > ContentPolicy.ChannelKeyMaxLength)
            {
                yield return ($"channels[{index}].channelKey", "Name a content channel.");
            }
            else if (!seen.Add(key))
            {
                yield return ($"channels[{index}].channelKey", "Each channel can be named once.");
            }

            if (channels[index]?.SocialRevisionId is { } revisionId)
            {
                if (revisionId == Guid.Empty)
                {
                    yield return ($"channels[{index}].socialRevisionId", "That is not an identifier.");
                }

                // A post belongs to a piece of work, so a post cannot be pinned without saying which.
                else if (model.CreativeContextId is null)
                {
                    yield return ("creativeContextId", "Name the piece of work these posts were written for.");
                }
            }
        }
    }

    private static IEnumerable<(string, string)> Assets(IReadOnlyList<RecipePackageAssetInput?>? assets)
    {
        if (assets is null)
        {
            yield break;
        }

        if (assets.Count > ContentCardPolicy.MaxPackageAssets)
        {
            yield return ("assets", $"A package can pin at most {ContentCardPolicy.MaxPackageAssets} pictures.");
            yield break;
        }

        var seen = new HashSet<Guid>();

        for (var index = 0; index < assets.Count; index++)
        {
            if (assets[index]?.MediaAssetId is not { } id || id == Guid.Empty)
            {
                yield return ($"assets[{index}].mediaAssetId", "Name the picture.");
            }
            else if (!seen.Add(id))
            {
                yield return ($"assets[{index}].mediaAssetId", "Each picture can be pinned once.");
            }

            if (assets[index]?.VersionNumber is not { } version)
            {
                yield return ($"assets[{index}].versionNumber", "Name the exact version of the picture.");
            }
            else if (version < 1)
            {
                yield return ($"assets[{index}].versionNumber", "Version numbers start at 1.");
            }
        }
    }

    private static IEnumerable<(string, string)> Window(RecipePackageWindowInput? window)
    {
        if (window is null)
        {
            yield break;
        }

        if (window.LocalStartsAt is not { } start || window.LocalEndsAt is not { } end)
        {
            yield return ("window", "A window has a start and an end.");
            yield break;
        }

        // The local time the creator means, with no offset: the zone is the workspace's, and converting is the
        // server's. A time that arrives already carrying an offset has been converted by somebody else.
        if (start.Kind != DateTimeKind.Unspecified || end.Kind != DateTimeKind.Unspecified)
        {
            yield return ("window", "Send local times with no offset, such as 2026-10-16T09:00:00.");
        }
        else if (end < start)
        {
            yield return ("window", "A window cannot end before it starts.");
        }
    }
}

public sealed class CreateRecipePackageViewModelValidator : AbstractValidator<CreateRecipePackageViewModel>
{
    public CreateRecipePackageViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in RecipePackageInputChecks.Create(model))
            {
                context.AddFailure(field, message);
            }
        });
}
