using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What a creator is filtering their library down to. Every member is optional, and an empty instance means
/// "everything in this workspace".
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace member, and there must not be.</strong> The workspace is resolved from the
/// route and the caller's membership and reaches the query through the global EF Core query filter; a
/// <c>WorkspaceId</c> here would be a field something could set from a request body, an AI tool argument, or a
/// job payload, which is the one thing tenancy.md forbids outright.
/// </para>
/// <para>
/// The collections are AND-ed against each other and OR-ed within themselves: two cuisines and one status
/// means "in either cuisine, and in that status". An empty collection is not a filter that matches nothing —
/// it is the absence of a filter, which is what a client that sent no value for it meant.
/// </para>
/// </remarks>
/// <param name="Search">
/// A term from <see cref="RecipeSearchPolicy.NormalizeSearch"/>, matched as a substring of the title or the
/// description. Never matched against ingredient lines or instructions: full-text over a creator's prose is a
/// different capability with a different index, and doing it with <c>LIKE</c> across two more tables would be
/// slow enough to be a defect.
/// </param>
/// <param name="Statuses">Editorial states to include. Empty includes every state, archived recipes included.</param>
/// <param name="TagIds">Workspace tags; a recipe matches if it carries any one of them.</param>
/// <param name="CuisineIds">Shared cuisine vocabulary ids.</param>
/// <param name="CourseIds">Shared course vocabulary ids.</param>
/// <param name="CreatorMembershipIds">
/// Authors, by <c>WorkspaceMembership</c> id — <see cref="Data.Entities.Recipe.CreatedByMembershipId"/>, not
/// the last editor. "Whose recipe is this" is a question about who wrote it; a later editor does not become its
/// creator.
/// </param>
/// <param name="LatestVersionReadiness">
/// The readiness of the recipe's most recent version. Deliberately the latest version rather than "has ever had
/// a ready version", so that this filter and the readiness shown on the same row can never disagree.
/// </param>
/// <param name="IngredientReview">
/// Whether every ingredient line is resolved against the vocabulary. See
/// <see cref="RecipeIngredientReviewFilter"/> — emphatically not a dietary or allergen state.
/// </param>
/// <param name="UpdatedOnOrAfter">Inclusive lower bound on the last edit.</param>
/// <param name="UpdatedBefore">
/// Exclusive upper bound on the last edit. Half-open so that a caller can ask for a day, a week, or a month
/// without having to know the precision the database stores, and so adjacent ranges neither overlap nor gap.
/// </param>
/// <param name="CreatedOnOrAfter">Inclusive lower bound on creation.</param>
/// <param name="CreatedBefore">Exclusive upper bound on creation.</param>
public sealed record RecipeSearchFilters(
    string? Search = null,
    IReadOnlyList<RecipeStatus>? Statuses = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<Guid>? CuisineIds = null,
    IReadOnlyList<Guid>? CourseIds = null,
    IReadOnlyList<Guid>? CreatorMembershipIds = null,
    RecipeVersionReadiness? LatestVersionReadiness = null,
    RecipeIngredientReviewFilter? IngredientReview = null,
    DateTimeOffset? UpdatedOnOrAfter = null,
    DateTimeOffset? UpdatedBefore = null,
    DateTimeOffset? CreatedOnOrAfter = null,
    DateTimeOffset? CreatedBefore = null);

/// <summary>
/// The position a page resumes from: the last row's ordering value and its id.
/// </summary>
/// <remarks>
/// <para>
/// The typed counterpart of <see cref="ReferenceCursor"/>, which carries the same position as two strings
/// because it has to survive a query string. Converting once, here, is what keeps the repository's predicate
/// typed and therefore indexable — and it puts the one place a malformed cursor is detected above the
/// repository, where it is an ordinary bad request rather than an exception from a query.
/// </para>
/// <para>
/// <strong>Only the field matching the criteria's sort is meaningful.</strong> That is not a convention
/// somebody has to remember: <see cref="TryCreate"/> is handed the sort and fills in the field belonging to
/// it, and the repository reads that field inside the same <c>switch</c> it is already writing to choose the
/// <c>ORDER BY</c>. The two cannot come apart without the compiler noticing.
/// </para>
/// <para>
/// The tie-breaker is the recipe's id, which is where this departs from every reference cursor in the
/// platform — those use a code or normalized name because <see cref="ReferenceCursor"/> notes that C# and SQL
/// Server do not order GUIDs the same way. A recipe has no unique natural string key: titles are not unique,
/// deliberately, because two of a creator's recipes may legitimately share one. The GUID ordering mismatch is
/// harmless here for the reason that document already gives about collation — both the <c>WHERE</c> and the
/// <c>ORDER BY</c> are evaluated by the same database, so they agree with each other, which is all the keyset
/// needs. What it does mean is that a cursor is not portable between SQL Server and SQLite, exactly as a
/// reference cursor is not.
/// </para>
/// </remarks>
public sealed record RecipeSearchPosition
{
    /// <summary>
    /// Round-trip ("O"), so the encoded value carries every digit of precision the column holds. A format that
    /// truncated would produce a cursor that resumes slightly before where it claims, quietly repeating rows.
    /// </summary>
    private const string TimestampFormat = "O";

    private RecipeSearchPosition(Guid recipeId, DateTimeOffset updatedAt, string title)
    {
        RecipeId = recipeId;
        UpdatedAt = updatedAt;
        Title = title;
    }

    /// <summary>The last row's id. Always meaningful; it breaks every tie.</summary>
    public Guid RecipeId { get; }

    /// <summary>Meaningful only under <see cref="RecipeSearchSort.RecentlyUpdated"/>.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Meaningful only under <see cref="RecipeSearchSort.Title"/>.</summary>
    public string Title { get; }

    /// <summary>
    /// Converts a decoded cursor into a position for this ordering, or returns <c>false</c> when it does not
    /// describe one.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> rather than throwing, because the two halves arrived from a query string: a cursor
    /// whose tie-breaker is not a GUID, or whose sort value is not a timestamp under a timestamp ordering, is a
    /// caller's mistake and belongs in a 400. Note what is <em>not</em> checked — whether the cursor was issued
    /// for this workspace, these filters, and this sort. That is the scope fingerprint's job, and the caller
    /// must have already resolved it through <see cref="ReferenceCursor.TryResolve"/>.
    /// </remarks>
    public static bool TryCreate(RecipeSearchSort sort, ReferenceCursor cursor, out RecipeSearchPosition? position)
    {
        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var recipeId))
        {
            return false;
        }

        switch (sort)
        {
            case RecipeSearchSort.RecentlyUpdated:
                if (!TryParseTimestamp(cursor.SortValue, out var updatedAt))
                {
                    return false;
                }

                position = new RecipeSearchPosition(recipeId, updatedAt, string.Empty);
                return true;

            case RecipeSearchSort.Title:
                // No validation to do: every string is a title a row could have held, including the empty one,
                // which is why ReferenceCursor length-checks only its left two parts.
                position = new RecipeSearchPosition(recipeId, default, cursor.SortValue);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// How a timestamp is written into a cursor. Paired with <see cref="TryParseTimestamp"/> and used by
    /// <see cref="RecipeSummaryRecord.SortValue"/>, so that the value a page mints and the value the next page
    /// parses cannot drift into two different formats.
    /// </summary>
    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    internal static bool TryParseTimestamp(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);
}

/// <summary>
/// One recipe search, complete: what to filter by, how to order it, where to resume, and how many rows.
/// </summary>
/// <remarks>
/// <strong>An unclamped page size is unrepresentable.</strong> <see cref="Limit"/> is derived, not stored, so
/// there is no constructor, initializer, or <c>with</c> expression that can produce a criteria asking for ten
/// thousand rows — the restriction that page size is clamped server-side is enforced by the type rather than by
/// a line of code in one layer that a second caller could bypass. <see cref="RequestedLimit"/> keeps what was
/// asked for, because a validator's message and a cache key both want the request, not the clamp.
/// </remarks>
/// <param name="Filters">What to include. An empty instance means the whole workspace.</param>
/// <param name="Scope">
/// Identifies the ordered set these filters select, in the resolved workspace, under this ordering. Cursors
/// minted for a page of it are bound to this, so one cannot be replayed against another workspace, another
/// ordering, or another set of filters. Built by <see cref="RecipeSearchScope"/>; the repository ignores it.
/// </param>
/// <param name="Sort">The ordering, from the allow-list.</param>
/// <param name="Position">Where to resume, or <c>null</c> for the first page.</param>
/// <param name="RequestedLimit">
/// The page size asked for, or <c>null</c> for the default. Out-of-range values are clamped rather than
/// rejected, following <see cref="ReferencePolicy.ClampPageSize"/>: a client cannot fail a read by asking for
/// too much.
/// </param>
/// <param name="IncludeTotal">
/// Whether to count the whole filtered set alongside the page. Defaults to <c>true</c>, because a library
/// screen wants to say how many recipes it is showing of how many; a caller following a cursor already knows the
/// total and should turn it off rather than pay for a second statement on every page.
/// </param>
public sealed record RecipeSearchCriteria(
    RecipeSearchFilters Filters,
    string Scope,
    RecipeSearchSort Sort = RecipeSearchSort.RecentlyUpdated,
    RecipeSearchPosition? Position = null,
    int? RequestedLimit = null,
    bool IncludeTotal = true)
{
    /// <summary>
    /// The page size the repository will actually use, always within the published bounds.
    /// </summary>
    /// <remarks>
    /// Computed on every read rather than assigned once in the constructor, and that is the point: a record's
    /// copy constructor duplicates stored fields without re-running their initializers, so a stored
    /// <c>Limit</c> would survive <c>with { RequestedLimit = 10_000 }</c> unchanged the first time and be
    /// wrong the moment someone reordered the expression. Clamping on read cannot be got round.
    /// </remarks>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// One row of a recipe search: enough to render a library card and decide what to open, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Projected entirely in SQL. No aggregate is materialised, no child collection is loaded, and no media bytes
/// are touched — a search over a workspace's library must not read the ingredient lines, instruction steps, or
/// version snapshots of every recipe it lists.
/// </para>
/// <para>
/// <strong>Vocabulary references are ids, not names</strong>, following
/// <see cref="RecipeDetailServiceModel"/>'s reasoning: cuisine and course belong to another module whose
/// display names are already published by <c>/api/v1/reference/*</c>, which is the same list a library's filter
/// controls have to load anyway. Resolving them here would mean a cross-module join on every page to produce
/// strings the client already holds — and facade-to-facade is the only way modules may talk, so it would not
/// even be a join.
/// </para>
/// <para>
/// <strong>Tags are absent</strong>, and that is a decision rather than an omission. Naming them per row is
/// either a join that multiplies rows or a second batched query, and nothing yet says a library card shows
/// them. Adding an optional field later is compatible; removing one is not (api-contract.md).
/// </para>
/// </remarks>
/// <param name="Sort">
/// The ordering this row was fetched under, so that <see cref="SortValue"/> can answer what
/// <see cref="IReferenceRow"/> asks of it — "the value this row was ordered by" is not a question a row can
/// answer without knowing the ordering.
/// </param>
/// <param name="LatestVersionNumber">
/// The highest version number, or <c>null</c> for a recipe with no versions. Null is reachable in principle
/// only — creation writes version 1 in the same transaction — and is modelled honestly rather than defaulted to
/// zero, which would read as a version.
/// </param>
/// <param name="HasUnmatchedIngredients">
/// Whether any ingredient line is unresolved against the vocabulary. Present on every row, not only when
/// filtered on, because a creator cannot act on a filter whose result they cannot see.
/// </param>
public sealed record RecipeSummaryRecord(
    Guid Id,
    string Title,
    string? Description,
    RecipeStatus Status,
    Guid? CuisineId,
    Guid? CourseId,
    Guid CreatedByMembershipId,
    Guid UpdatedByMembershipId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? LatestVersionNumber,
    RecipeVersionReadiness? LatestVersionReadiness,
    bool HasUnmatchedIngredients,
    RecipeSearchSort Sort) : IReferenceRow
{
    /// <inheritdoc />
    public string SortValue => Sort switch
    {
        RecipeSearchSort.Title => Title,
        _ => RecipeSearchPosition.FormatTimestamp(UpdatedAt),
    };

    /// <inheritdoc />
    /// <remarks>
    /// The id, formatted the way <see cref="RecipeSearchPosition.TryCreate"/> parses it. Unique by definition,
    /// so it breaks every tie the sort value leaves.
    /// </remarks>
    public string TieBreaker => Id.ToString("D");
}
