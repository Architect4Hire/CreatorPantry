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
///
/// <c>Purpose</c> says what the row is for rather than what it points at, which is how a caller tells a
/// picture the work takes cues from apart from one the work produced (AF.4.3).
/// </remarks>
public sealed record CreativeContextReferenceServiceModel(
    Guid Id,
    CreativeContextReferenceKind Kind,
    CreativeContextReferencePurpose Purpose,
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
/// One picture a piece of work names, and what is known about what it shows (AF.6.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Enough to show the picture, and what may be said about it.</strong> The ids are the ones its own
/// render route takes; <see cref="Reading"/> is what a model saw in it, or null when nobody has looked.
/// </para>
/// <para>
/// <strong>A reading is not the creator's alt text and must not be shown as though it were.</strong> It is
/// model prose about a photograph, which no creator has reviewed — a surface showing one says so, and shows
/// each observation's confidence beside its text (<c>IMediaPictureAnalysisFacade</c>).
/// </para>
/// </remarks>
/// <param name="AltText">The creator's own words about the picture, where they wrote any.</param>
/// <param name="Reading">
/// What a model saw in it, or null when nobody has looked — and only the part a task would be grounded on:
/// the observations the reading was clear about, as many as the package's cap carries.
/// </param>
/// <param name="Grounding">
/// Which of the three a generation would actually be told about this picture.
/// </param>
/// <remarks>
/// <strong><see cref="Grounding"/> is why this read is not simply "everything known".</strong> A surface that
/// showed a reading while the package was using the creator's alt text instead would tell a creator their
/// posts know something they do not. The precedence and the cap are applied here, by the same code the
/// package uses, so what this says is what a generation gets.
/// </remarks>
public sealed record CreativeContextPictureServiceModel(
    Guid ReferenceId,
    CreativeContextReferenceKind Kind,
    CreativeContextReferencePurpose Purpose,
    Guid? MediaAssetId,
    int? MediaAssetVersionNumber,
    Guid? GeneratedImageId,
    string? AltText,
    CreativeContextPictureReadingServiceModel? Reading,
    CreativeContextPictureDescriptionSource Grounding);

/// <summary>What a model saw in one picture, with when it looked.</summary>
public sealed record CreativeContextPictureReadingServiceModel(
    IReadOnlyList<CreativeContextPictureObservation> Observations,
    DateTimeOffset ReadAt);

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
