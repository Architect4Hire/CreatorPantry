using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>The board's filters, typed. Each is optional and they combine with AND.</summary>
public sealed record ContentCardFilters(
    string? ChannelKey = null,
    DayOfWeek? Day = null,
    DateOnly? Date = null,
    ContentCardColumn? Column = null);

/// <summary>
/// Where one page of the board ended: the last card's column, rank and id — the whole of the board's order, so
/// the next page starts exactly after it even when two cards share a rank.
/// </summary>
public sealed record ContentCardPosition
{
    private const char Separator = ':';

    private ContentCardPosition(ContentCardColumn column, long sortRank, Guid cardId)
    {
        Column = column;
        SortRank = sortRank;
        CardId = cardId;
    }

    public ContentCardColumn Column { get; }

    public long SortRank { get; }

    public Guid CardId { get; }

    /// <summary>False for a cursor whose parts are not a column, a rank and an id.</summary>
    public static bool TryCreate(ReferenceCursor cursor, out ContentCardPosition? position)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        position = null;

        var parts = cursor.SortValue.Split(Separator);

        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var column)
            || !Enum.IsDefined((ContentCardColumn)column)
            || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rank)
            || !Guid.TryParseExact(cursor.TieBreaker, "D", out var cardId))
        {
            return false;
        }

        position = new ContentCardPosition((ContentCardColumn)column, rank, cardId);

        return true;
    }

    internal static string FormatSortValue(ContentCardColumn column, long sortRank) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)column}{Separator}{sortRank}");
}

/// <summary>One read of the board: what to match, where to start, and how many rows.</summary>
/// <param name="Scope">What a cursor issued for this read is bound to: the workspace and every filter.</param>
public sealed record ContentCardListCriteria(
    ContentCardFilters Filters,
    string Scope,
    ContentCardPosition? Position = null,
    int? RequestedLimit = null)
{
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// Turns a bound <see cref="ContentCardListViewModel"/> into the typed criteria the rest of the seam works in.
/// Called after the validator, so every filter here is known to parse.
/// </summary>
public static class ContentCardListQueryFactory
{
    public static bool TryCreate(
        ContentCardListViewModel model,
        Guid workspaceId,
        out ContentCardListCriteria? criteria,
        out OperationError? error)
    {
        ArgumentNullException.ThrowIfNull(model);

        criteria = null;
        error = null;

        var filters = new ContentCardFilters(
            ChannelKey: ContentCardListInputs.Normalize(model.Channel),
            Day: ContentCardListInputs.Normalize(model.Day) is { } day && ContentCardListInputs.TryParseDay(day, out var parsedDay)
                ? parsedDay
                : null,
            Date: ContentCardListInputs.Normalize(model.Date) is { } date && ContentCardListInputs.TryParseDate(date, out var parsedDate)
                ? parsedDate
                : null,
            Column: ContentCardListInputs.Normalize(model.Column) is { } column
                && ContentCardListInputs.TryParseColumn(column, out var parsedColumn)
                ? parsedColumn
                : null);

        var scope = ScopeFor(workspaceId, filters);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            error = CursorRefused();
            return false;
        }

        ContentCardPosition? position = null;
        if (cursor is not null && !ContentCardPosition.TryCreate(cursor, out position))
        {
            error = CursorRefused();
            return false;
        }

        criteria = new ContentCardListCriteria(filters, scope, position, model.Limit);

        return true;
    }

    /// <summary>
    /// The workspace and every filter, so a cursor cannot be replayed against another workspace's board or a
    /// differently filtered one — where it would return a plausible page from the wrong place in the wrong set.
    /// </summary>
    private static string ScopeFor(Guid workspaceId, ContentCardFilters filters) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"content-cards|{workspaceId:D}|channel={filters.ChannelKey}|day={(int?)filters.Day}"
                + $"|date={filters.Date:yyyy-MM-dd}|column={(int?)filters.Column}");

    private static OperationError CursorRefused() =>
        OperationError.Validation(
            ContentErrorCodes.CardCursorInvalid,
            "This cursor cannot be used with this board and these filters. Start again without one.",
            [("cursor", "Start the list again without a cursor.")]);
}

/// <summary>Whether each of this module's own lineage records can be linked. True for one that was not asked about.</summary>
public sealed record ContentCardOwnLineage(bool CreativeContext, bool PromptRecord, bool SocialPackage);

/// <summary>One card as the board's list reads it. Never the description in full, and never a membership id.</summary>
public sealed record ContentCardSummaryRecord(
    Guid Id,
    string Title,
    string? DescriptionPreview,
    int DescriptionLength,
    ContentCardColumn Column,
    long SortRank,
    string? ChannelKey,
    DateOnly? PlannedDate,
    DayOfWeek? PlannedDayOfWeek,
    string? CuisineCode,
    string? DishTypeCode,
    string? MethodCode,
    Guid? CreativeContextId,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    Guid? PromptRecordId,
    Guid? MediaAssetId,
    int? MediaAssetVersionNumber,
    Guid? SocialPackageId,
    int? LastMoveSequence,
    bool HasPackage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    byte[] RowVersion) : IReferenceRow
{
    public string SortValue => ContentCardPosition.FormatSortValue(Column, SortRank);

    public string TieBreaker => Id.ToString("D");
}

/// <summary>How many cards one column holds under a set of filters.</summary>
public sealed record ContentCardColumnCountRecord(ContentCardColumn Column, int Count);

/// <summary>One row of a card's movement history, as stored. The actor is deliberately not read.</summary>
public sealed record CardMoveRecord(
    int Sequence, ContentCardColumn? FromColumn, ContentCardColumn ToColumn, string? Note, DateTimeOffset OccurredAt);

/// <summary>A card's recipe package, as stored: ids and version pins only.</summary>
public sealed record ContentCardPackageRecord(
    Guid Id,
    Guid RecipeId,
    Guid RecipeVersionId,
    Guid? EditorialProposalId,
    Guid? EditorialRevisionId,
    Guid? SocialPackageId,
    DateTimeOffset? WindowStartsAt,
    DateTimeOffset? WindowEndsAt,
    string? WindowTimeZoneId,
    DateTime? WindowLocalStartsAt,
    DateTime? WindowLocalEndsAt,
    DateTimeOffset? NeedsReviewAllowedAt,
    string? NeedsReviewAllowedReasons,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ContentCardPackageAssetRecord> Assets,
    IReadOnlyList<ContentCardPackageChannelRecord> Channels);

public sealed record ContentCardPackageAssetRecord(Guid MediaAssetId, int MediaAssetVersionNumber);

public sealed record ContentCardPackageChannelRecord(string ChannelKey, Guid? SocialPackageChannelId, Guid? SocialRevisionId);

/// <summary>
/// One card in full, as the detail route reads it.
/// </summary>
/// <param name="Summary">Everything the list row carries.</param>
/// <param name="Description">The whole description.</param>
/// <param name="CreativeContextTitle">The linked work's own title. This module's record, so read with the card.</param>
/// <param name="PromptLabel">The linked prompt's label. This module's record too.</param>
/// <param name="Moves">The latest moves, oldest first.</param>
/// <param name="MovesTruncated">True when the card has more moves than <paramref name="Moves"/> holds.</param>
/// <param name="Package">The recipe package, or null for a card that has none.</param>
public sealed record ContentCardDetailRecord(
    ContentCardSummaryRecord Summary,
    string? Description,
    string? CreativeContextTitle,
    string? PromptLabel,
    IReadOnlyList<CardMoveRecord> Moves,
    bool MovesTruncated,
    ContentCardPackageRecord? Package);

/// <summary>
/// What the Content Pipeline's commit reads about one piece of work before it makes that work's card (13.7a).
/// Ids and the work's own title only: nothing here is a copy of a source.
/// </summary>
/// <param name="WorkingTitle">The creator's name for the work, or null when they gave none.</param>
/// <param name="RecipeId">The recipe the work is about, or null.</param>
/// <param name="RecipeVersionId">The version of it the work pinned, or null.</param>
/// <param name="Keepers">The library assets the work's kept pictures became, in the order they were kept.</param>
/// <param name="SocialPackageId">The work's posts, when at least one channel's post stands accepted; otherwise null.</param>
public sealed record PipelineCardSourceRecord(
    string? WorkingTitle,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    IReadOnlyList<PipelineKeeperRecord> Keepers,
    Guid? SocialPackageId);

/// <summary>One kept picture, as the library asset it was saved as.</summary>
public sealed record PipelineKeeperRecord(Guid MediaAssetId, int? MediaAssetVersionNumber);
