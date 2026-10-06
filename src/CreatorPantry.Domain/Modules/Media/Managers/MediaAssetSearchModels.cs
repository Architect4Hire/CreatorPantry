using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>How a library page is ordered. Stored nowhere; a published name a client sends.</summary>
public enum MediaAssetSearchSort
{
    /// <summary>Newest first. The default, because a library is browsed from the most recent work.</summary>
    RecentlyAdded = 0,

    /// <summary>Alphabetical by title.</summary>
    Title = 1,
}

/// <summary>
/// What a library search narrows by (DAM-002), already parsed and ready for the repository.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Tags match <em>any</em> of the ids given</strong>, which is how <c>RecipeSearchFilters</c> and the
/// brand source list both behave: a creator who passes two tags sees assets carrying either. One semi-join,
/// one meaning for the same-looking control across the product.
/// </para>
/// <para>
/// <strong>The date bounds are the asset's own created date</strong>, half-open, as every other date filter
/// in this codebase is. "When did this enter the library" is a fact about the asset and needs no join; "when
/// did I last use this" is a utilization question, which DAM-009 records and 12.9c surfaces per asset.
/// </para>
/// <para>
/// No workspace, and there never will be one — the global query filter is the scope (tenancy.md).
/// </para>
/// </remarks>
public sealed record MediaAssetSearchFilters(
    string? Search = null,
    string? ChannelKey = null,
    string? PlatformKey = null,
    DayOfWeek? Day = null,
    string? StyleKey = null,
    IReadOnlyList<Guid>? CuisineIds = null,
    IReadOnlyList<Guid>? CourseIds = null,
    IReadOnlyList<Guid>? TagIds = null,
    Guid? RecipeId = null,
    DateTimeOffset? CreatedOnOrAfter = null,
    DateTimeOffset? CreatedBefore = null);

/// <summary>
/// Where a page resumes: the last row's sort value and its id.
/// </summary>
/// <remarks>
/// The id breaks every tie, so paging is a total order rather than nearly one — two assets created in the
/// same tick, or sharing a title, would otherwise be able to repeat or vanish between pages. The same shape
/// <c>RecipeSearchPosition</c> uses, including its note that a cursor is not portable between SQL Server and
/// SQLite because the two do not order GUIDs alike. Harmless, because one database evaluates both the
/// <c>WHERE</c> and the <c>ORDER BY</c> and so agrees with itself.
/// </remarks>
public sealed record MediaAssetSearchPosition
{
    /// <summary>
    /// Round-trip ("O"), so the encoded value carries every digit the column holds. A truncating format
    /// would resume slightly before where it claims and quietly repeat rows.
    /// </summary>
    private const string TimestampFormat = "O";

    private MediaAssetSearchPosition(Guid mediaAssetId, DateTimeOffset createdAt, string title)
    {
        MediaAssetId = mediaAssetId;
        CreatedAt = createdAt;
        Title = title;
    }

    /// <summary>The last row's id. Always meaningful; it breaks every tie.</summary>
    public Guid MediaAssetId { get; }

    /// <summary>Meaningful only under <see cref="MediaAssetSearchSort.RecentlyAdded"/>.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Meaningful only under <see cref="MediaAssetSearchSort.Title"/>.</summary>
    public string Title { get; }

    /// <summary>
    /// Converts a decoded cursor into a position for this ordering, or false when it does not describe one.
    /// </summary>
    /// <remarks>
    /// False rather than throwing: the two halves arrived from a query string, so a tie-breaker that is not
    /// a GUID is a caller's mistake and belongs in a 400. It does <em>not</em> check that the cursor was
    /// issued for this workspace, these filters and this sort — that is the scope fingerprint's job, which
    /// <see cref="ReferenceCursor.TryResolve"/> must already have done.
    /// </remarks>
    public static bool TryCreate(
        MediaAssetSearchSort sort, ReferenceCursor cursor, out MediaAssetSearchPosition? position)
    {
        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var mediaAssetId))
        {
            return false;
        }

        switch (sort)
        {
            case MediaAssetSearchSort.RecentlyAdded:
                if (!DateTimeOffset.TryParseExact(
                    cursor.SortValue,
                    TimestampFormat,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var createdAt))
                {
                    return false;
                }

                position = new MediaAssetSearchPosition(mediaAssetId, createdAt, string.Empty);

                return true;

            case MediaAssetSearchSort.Title:
                position = new MediaAssetSearchPosition(
                    mediaAssetId, default, cursor.SortValue);

                return true;

            default:
                return false;
        }
    }

    /// <summary>The value a row of this ordering publishes as its cursor's sort half.</summary>
    public static string SortValueFor(MediaAssetSearchSort sort, DateTimeOffset createdAt, string title) =>
        sort is MediaAssetSearchSort.Title
            ? title
            : createdAt.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One library search, parsed and clamped.</summary>
/// <param name="Scope">
/// The fingerprint a cursor is bound to. A cursor issued for other filters, another ordering or another
/// workspace is refused rather than silently resuming in the wrong set.
/// </param>
/// <param name="RequestedLimit">
/// The page size asked for, or null for the default. Out-of-range values are clamped rather than refused:
/// a client cannot fail a read by asking for too much.
/// </param>
/// <param name="IncludeTotal">
/// Whether to count the whole filtered set beside the page. A library screen wants to say how many it is
/// showing of how many; a caller following a cursor already knows and should turn it off rather than pay
/// for a second statement per page.
/// </param>
public sealed record MediaAssetSearchCriteria(
    MediaAssetSearchFilters Filters,
    string Scope,
    MediaAssetSearchSort Sort = MediaAssetSearchSort.RecentlyAdded,
    MediaAssetSearchPosition? Position = null,
    int? RequestedLimit = null,
    bool IncludeTotal = true)
{
    /// <summary>The page size the repository will actually use, always within the published bounds.</summary>
    /// <remarks>
    /// Computed on every read rather than stored, for the reason <c>RecipeSearchCriteria.Limit</c> gives: a
    /// record's copy constructor duplicates stored fields without re-running initializers, so a stored
    /// limit would survive <c>with { RequestedLimit = 10_000 }</c> and be wrong. Clamping on read cannot be
    /// got round.
    /// </remarks>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// One row of a library search, as the repository projects it.
/// </summary>
/// <remarks>
/// Projected entirely in SQL. No aggregate is materialised, no child collection is loaded, and
/// <strong>no bytes are touched</strong> — a search over a library must not read the objects of every asset
/// it lists, and nothing here is or becomes an address (media.md, and this prompt's restriction).
/// </remarks>
/// <param name="Sort">
/// The ordering this row was fetched under, so that <see cref="SortValue"/> can answer what
/// <see cref="IReferenceRow"/> asks of it. The same shape <c>RecipeSummaryRecord</c> uses, and for the
/// reason it gives: "the value this row was ordered by" is not a question a row can answer without knowing
/// the ordering, and computing it here rather than in the projection is also what keeps the projection
/// translatable — a format string in a <c>Select</c> is not SQL.
/// </param>
public sealed record MediaAssetSearchRecord(
    Guid Id,
    string Title,
    string? Description,
    MediaAssetKind Kind,
    string? AltText,
    string? ChannelKey,
    string? PlatformKey,
    DayOfWeek? Day,
    string? StyleKey,
    Guid? CuisineId,
    Guid? CourseId,
    int CurrentVersionNumber,
    string? MediaType,
    int Width,
    int Height,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    MediaAssetSearchSort Sort) : IReferenceRow
{
    /// <inheritdoc />
    public string SortValue => MediaAssetSearchPosition.SortValueFor(Sort, CreatedAt, Title);

    /// <inheritdoc />
    /// <remarks>
    /// The id, formatted the way <see cref="MediaAssetSearchPosition.TryCreate"/> parses it. Unique by
    /// definition, so it breaks every tie the sort value leaves.
    /// </remarks>
    public string TieBreaker => Id.ToString("D");
}

/// <summary>One asset as a library page reports it: enough to render a card and decide what to open.</summary>
/// <remarks>
/// <para>
/// The current version's media facts come with the row because a grid needs dimensions to lay out, and the
/// alternative is a client fetching every asset to find out. They are read through
/// <c>IX_MediaAssetVersions_Workspace_Asset_VersionNumber</c>, which exists for exactly this seek.
/// </para>
/// <para>
/// <strong>No object key and no URL.</strong> Bytes are read by id, which is 12.9f and 12.9g.
/// </para>
/// </remarks>
public sealed record MediaAssetSummaryServiceModel(
    Guid Id,
    string Title,
    string? Description,
    MediaAssetKind Kind,
    string? AltText,
    string? ChannelKey,
    string? PlatformKey,
    DayOfWeek? Day,
    string? StyleKey,
    Guid? CuisineId,
    Guid? CourseId,
    int CurrentVersionNumber,
    string? MediaType,
    int Width,
    int Height,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One page of a library search, as the route publishes it.</summary>
/// <remarks>
/// <see cref="NextCursor"/> is <c>null</c> on the last page, so a client loops until it is null rather than
/// comparing counts against a page size it may not have chosen.
/// </remarks>
/// <param name="TotalCount">
/// The size of the whole filtered set, or null when the caller did not ask for it.
///
/// Named to match <c>RecipeSearchPageServiceModel</c> and <c>PromptSearchPageServiceModel</c> rather than the
/// <c>Total</c> the layers below call it: three sibling paged routes publishing two names for one fact is the
/// drift api-contract.md is about, and the published name is the one that cannot be changed later.
///
/// Counted before the cursor is applied, so it does not shrink as a client pages through. It is a count of
/// rows, never a page number — the ordering is a keyset and there is no page N to jump to.
/// </param>
public sealed record MediaAssetSearchPageServiceModel(
    IReadOnlyList<MediaAssetSummaryServiceModel> Items, string? NextCursor, int? TotalCount);

/// <summary>The fixed limits of a library search.</summary>
public static class MediaAssetSearchPolicy
{
    /// <summary>The longest free-text term a search will take.</summary>
    /// <remarks>
    /// A bound on a substring match, which has no index to help it: a term longer than this is not a search
    /// a creator meant to run. Matches the recipe library's.
    /// </remarks>
    public const int SearchMaxLength = 200;

    /// <summary>Trims a term and folds it for the case-insensitive comparison the repository makes.</summary>
    /// <remarks>
    /// Lowered here rather than in the predicate so the scope fingerprint and the query agree about what
    /// was searched for — two cursors differing only in the case of the term would otherwise bind to
    /// different scopes while returning the same rows.
    /// </remarks>
    public static string? NormalizeSearch(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
}
