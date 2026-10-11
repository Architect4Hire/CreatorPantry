using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>The bounds on a themed day's history, shared by the read, its cache and its tests.</summary>
public static class ThemedDayHistoryPolicy
{
    /// <summary>
    /// The most contexts one read looks at: the most recently touched pieces of work carrying the theme's key.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="MaxItems"/> on purpose. Most pieces of work never reach an outcome, so the scan has
    /// to look past them to find the ones that did, and this is what stops a theme used for years from turning
    /// one read into a scan of all of it.
    /// </remarks>
    public const int MaxCandidates = 200;

    /// <summary>The most pieces of work a theme's history holds, across every page of it.</summary>
    public const int MaxItems = 50;

    /// <summary>How far back an outcome may be and still count. Older than this is not "recent".</summary>
    public const int MaxAgeDays = 365;

    public const int DefaultPageSize = 10;

    public const int MaxPageSize = 25;

    /// <summary>
    /// How long a derived history is kept. A backstop, not the mechanism: the writes that change the answer
    /// invalidate it, and this bounds how long a missed invalidation can be wrong for.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a workspace's generation stamp is kept. Must stay well past <see cref="CacheLifetime"/>: every
    /// entry written under a stamp has to have expired before the stamp itself can, or a workspace falling back
    /// to "no stamp" could meet an entry it had already invalidated.
    /// </summary>
    public static readonly TimeSpan GenerationLifetime = TimeSpan.FromDays(1);

    /// <summary>The cache category, the segment after the workspace id in every key this feature writes.</summary>
    public const string CacheCategory = "theme-history";

    /// <summary>Bumped when the cached shape changes, so an entry from a previous release is a miss.</summary>
    public const string CacheSchemaVersion = "v1";

    /// <summary>Clamped rather than rejected, so a client cannot fail a read by asking for too much.</summary>
    public static int ClampPageSize(int? limit) =>
        limit is null ? DefaultPageSize : Math.Clamp(limit.Value, 1, MaxPageSize);
}

/// <summary>What became of a piece of work. Each is something the creator did, not something generated.</summary>
public enum ThemedDayOutcomeKind
{
    /// <summary>A picture the creator saved to the library and marked as the one to keep for this work.</summary>
    KeptPicture = 1,

    /// <summary>Posts the creator accepted for one or more channels.</summary>
    AcceptedPosts = 2,

    /// <summary>A recipe this work names that the creator made by accepting an AI first draft.</summary>
    AcceptedRecipeDraft = 3,
}

/// <summary>
/// One thing a piece of work came to, with the ids a client links to. Never the thing itself: no post body,
/// no recipe text, no picture.
/// </summary>
/// <param name="Kind">Which outcome this is. It decides which of the ids below are set.</param>
/// <param name="At">When it happened. For accepted posts, the most recent acceptance among the channels.</param>
/// <param name="MediaAssetId">The library asset, for <see cref="ThemedDayOutcomeKind.KeptPicture"/>.</param>
/// <param name="MediaAssetVersionNumber">The version of that asset the work pinned, when it pinned one.</param>
/// <param name="ChannelKeys">The channels with an accepted post, for <see cref="ThemedDayOutcomeKind.AcceptedPosts"/>.</param>
/// <param name="NeedsReview">
/// For accepted posts, whether any of them is marked for review because its source changed. The acceptance
/// still happened, which is why it stays in the history — as it does when newer words are drafted or declined.
/// </param>
/// <param name="RecipeId">The recipe, for <see cref="ThemedDayOutcomeKind.AcceptedRecipeDraft"/>.</param>
public sealed record ThemedDayOutcomeServiceModel(
    ThemedDayOutcomeKind Kind,
    DateTimeOffset At,
    Guid? MediaAssetId = null,
    int? MediaAssetVersionNumber = null,
    IReadOnlyList<string>? ChannelKeys = null,
    bool? NeedsReview = null,
    Guid? RecipeId = null);

/// <summary>
/// One piece of work this workspace made for a themed day, and what it came to.
/// </summary>
/// <param name="ContextId">The creative context. What a client opens to see the work, and its posts.</param>
/// <param name="WorkingTitle">The creator's own name for the work, or null when they gave it none.</param>
/// <param name="Day">The day the work was for, which need not be the day the theme runs on now.</param>
/// <param name="IsArchived">Archiving a piece of work does not unmake what came of it, so it is still listed.</param>
/// <param name="LatestOutcomeAt">The most recent of <paramref name="Outcomes"/>, which the list is ordered by.</param>
/// <param name="Outcomes">Newest first. Never empty: work that came to nothing is not history.</param>
public sealed record ThemedDayHistoryItemServiceModel(
    Guid ContextId,
    string? WorkingTitle,
    DayOfWeek? Day,
    bool IsArchived,
    DateTimeOffset LatestOutcomeAt,
    IReadOnlyList<ThemedDayOutcomeServiceModel> Outcomes);

/// <summary>A theme's whole bounded history, which is what is derived and what is cached. Pages are slices of it.</summary>
public sealed record ThemedDayHistoryServiceModel(IReadOnlyList<ThemedDayHistoryItemServiceModel> Items);

/// <summary>The paging of a themed day's history. Carries no workspace and no theme: both come from the route.</summary>
public sealed record ThemedDayHistoryViewModel(
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and theme it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page, 1 to 25. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);

/// <summary>Where a page of the history starts: the last item of the page before it.</summary>
public sealed record ThemedDayHistoryPosition(DateTimeOffset LatestOutcomeAt, Guid ContextId)
{
    private const string TimestampFormat = "O";

    public const string Resource = "theme-history";

    public const string Ordering = "LatestOutcomeDescending";

    /// <summary>
    /// The scope a cursor carries, so one issued for another workspace, another theme or another list is
    /// refused rather than replayed.
    /// </summary>
    public static string ScopeFor(Guid workspaceId, string themeKey) =>
        $"{Resource}|{workspaceId:D}|{themeKey}|{Ordering}";

    public static string Encode(ThemedDayHistoryItemServiceModel item, string scope)
    {
        ArgumentNullException.ThrowIfNull(item);

        return ReferenceCursor.Encode(
            item.LatestOutcomeAt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            item.ContextId.ToString("D"),
            scope);
    }

    public static bool TryCreate(ReferenceCursor cursor, out ThemedDayHistoryPosition? position)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var contextId)
            || !DateTimeOffset.TryParseExact(
                cursor.SortValue,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var latestOutcomeAt))
        {
            return false;
        }

        position = new ThemedDayHistoryPosition(latestOutcomeAt, contextId);

        return true;
    }

    /// <summary>
    /// Whether <paramref name="item"/> comes after this position in the list's order: an earlier outcome, or
    /// the same instant and a smaller id.
    /// </summary>
    public bool Precedes(ThemedDayHistoryItemServiceModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.LatestOutcomeAt < LatestOutcomeAt
            || (item.LatestOutcomeAt == LatestOutcomeAt && CompareIds(item.ContextId, ContextId) < 0);
    }

    /// <summary>
    /// The tie-break both the ordering and the cursor use. By the id's text rather than <see cref="Guid.CompareTo(Guid)"/>,
    /// only so there is one comparison to keep consistent; nothing here is compared by a database.
    /// </summary>
    public static int CompareIds(Guid left, Guid right) =>
        string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
}

/// <summary>A piece of work carrying the theme's key, before anything is known about what came of it.</summary>
public sealed record ThemedDayCandidate(Guid ContextId, string? WorkingTitle, DayOfWeek? Day, bool IsArchived);

/// <summary>A picture the creator saved to the library and marked as this work's keeper.</summary>
public sealed record ThemedDayKeptPicture(
    Guid ContextId, Guid MediaAssetId, int? MediaAssetVersionNumber, DateTimeOffset AddedAt);

/// <summary>A channel of this work's post package that holds an accepted post.</summary>
public sealed record ThemedDayAcceptedChannel(
    Guid ContextId, string ChannelKey, bool NeedsReview, DateTimeOffset AcceptedAt);

/// <summary>A recipe this work names. Whether it began as an accepted draft is the AI module's to say.</summary>
public sealed record ThemedDayRecipeReference(Guid ContextId, Guid RecipeId);

/// <summary>
/// Everything this module holds about what a theme's work came to, as plain rows for Business to weigh.
/// </summary>
public sealed record ThemedDayHistorySources(
    IReadOnlyList<ThemedDayCandidate> Candidates,
    IReadOnlyList<ThemedDayKeptPicture> KeptPictures,
    IReadOnlyList<ThemedDayAcceptedChannel> AcceptedChannels,
    IReadOnlyList<ThemedDayRecipeReference> RecipeReferences);
