using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// One source a creative context names. Identifiers only.
/// </summary>
/// <remarks>
/// Deliberately no title, alt text, preview or "still available" flag. A context points and never copies, and
/// whether a source is still usable is the owning record's answer when it is read — publishing a copy here
/// would be a second account of it, free to go stale.
///
/// No <c>socialPackageId</c> yet: this seam refuses that kind until post packages exist, so the field could
/// only ever be null. It becomes a compatible addition when the kind is accepted.
/// </remarks>
public sealed record CreativeContextReferenceServiceModel(
    Guid Id,
    CreativeContextReferenceKind Kind,
    int SortOrder,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    Guid? ConceptRequestId,
    Guid? ConceptId,
    Guid? MediaAssetId,
    int? MediaAssetVersionNumber,
    Guid? GeneratedImageId,
    Guid? PromptRecordId,
    DateTimeOffset AddedAt);

/// <summary>
/// A creative context in full: the creator's words, what the work is for, and the sources it names in order.
/// </summary>
/// <remarks>
/// Carries no workspace id and no author: the workspace is the route's, and a membership id never leaves the
/// server. <see cref="ConcurrencyToken"/> is opaque — send it back as <c>expectedConcurrencyToken</c> on the
/// next edit.
/// </remarks>
public sealed record CreativeContextServiceModel(
    Guid Id,
    string? WorkingTitle,
    string? PictureBrief,
    CreativeContextBriefSource? BriefSource,
    string? WorkingBrief,
    IReadOnlyList<string> ChannelKeys,
    DayOfWeek? Day,
    string? WeeklyThemeKey,
    IReadOnlyList<CreativeContextReferenceServiceModel> References,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    string ConcurrencyToken);

/// <summary>One row of the recent list. No picture brief: the list is for finding a piece, not reading it.</summary>
public sealed record CreativeContextSummaryServiceModel(
    Guid Id,
    string? WorkingTitle,
    IReadOnlyList<string> ChannelKeys,
    DayOfWeek? Day,
    string? WeeklyThemeKey,
    int ReferenceCount,
    DateTimeOffset UpdatedAt);

/// <summary>A row of the recent list as the repository reads it.</summary>
public sealed record CreativeContextSummaryRecord(
    Guid Id,
    string? WorkingTitle,
    IReadOnlyList<string> ChannelKeys,
    DayOfWeek? Day,
    string? WeeklyThemeKey,
    int ReferenceCount,
    DateTimeOffset UpdatedAt) : IReferenceRow
{
    public string SortValue => CreativeContextListPosition.FormatTimestamp(UpdatedAt);

    public string TieBreaker => Id.ToString("D");
}

/// <summary>Where a page of the recent list starts: the last row of the page before it.</summary>
public sealed record CreativeContextListPosition
{
    private const string TimestampFormat = "O";

    private CreativeContextListPosition(Guid contextId, DateTimeOffset updatedAt)
    {
        ContextId = contextId;
        UpdatedAt = updatedAt;
    }

    public Guid ContextId { get; }

    public DateTimeOffset UpdatedAt { get; }

    public static bool TryCreate(ReferenceCursor cursor, out CreativeContextListPosition? position)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var contextId)
            || !DateTimeOffset.TryParseExact(
                cursor.SortValue,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var updatedAt))
        {
            return false;
        }

        position = new CreativeContextListPosition(contextId, updatedAt);

        return true;
    }

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
}

/// <summary>The recent list as the data layer is asked for it.</summary>
/// <param name="Scope">What the cursor is bound to: this resource, this workspace, this ordering.</param>
public sealed record CreativeContextListCriteria(
    string Scope,
    CreativeContextListPosition? Position = null,
    int? RequestedLimit = null)
{
    public const string Resource = "creative-contexts";

    public const string Ordering = "UpdatedDescending";

    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);

    /// <summary>
    /// The scope a cursor for this workspace's list carries, so one issued for another workspace — or for a
    /// different list — is refused rather than replayed.
    /// </summary>
    public static string ScopeFor(Guid workspaceId) =>
        string.Join('|', $"resource={Resource}", $"workspace={workspaceId:D}", $"sort={Ordering}");
}
