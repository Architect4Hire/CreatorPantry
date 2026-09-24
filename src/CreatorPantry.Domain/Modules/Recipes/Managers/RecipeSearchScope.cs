using System.Globalization;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Builds the string that identifies one ordered set of recipes, so a cursor can be bound to it.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="CreatorPantry.Domain.Managers.Paging.ReferenceQueryKey"/>, which takes a <c>ReferenceSearch</c> — the dual
/// raw/normalized form the reference catalogues need and a recipe title has no use for. Constructing one here
/// just to satisfy a signature would put a normalized form into the scope that nothing ever matches on.
/// </para>
/// <para>
/// <strong>The workspace is in the scope, and that is the point.</strong> Nothing in the shared paging kernel
/// puts it there, because reference data has no workspace to put. For workspace-owned data the ordered set a
/// cursor names depends on the resolved workspace as much as on the filters — and that workspace arrives
/// ambiently, through the query filter, not as a request field. A cursor minted in one workspace and replayed in
/// another would otherwise decode cleanly and produce a page of the wrong workspace's recipes.
/// </para>
/// <para>
/// <strong>The sort key is in it too</strong>, because a position in an ordering is meaningless in a different
/// one: a title cursor replayed against the recency ordering would resume from whatever row happened to sort
/// after that title by date, silently skipping the rest.
/// </para>
/// <para>
/// <strong>The page size is deliberately not in it.</strong> A position is a position, so asking for a different
/// number of rows after it is legitimate — the same rule
/// <see cref="CreatorPantry.Domain.Managers.Paging.ReferenceCursor"/> states. Neither is <c>includeTotal</c>: asking for a count
/// does not change which rows follow.
/// </para>
/// <para>
/// <strong>List filters are sorted before they are folded in.</strong> Without that,
/// <c>?status=Ready,Draft</c> and <c>?status=Draft,Ready</c> are the same query with two different scopes, and
/// a client that reordered its own parameters between pages would have its cursor rejected for no reason it
/// could see.
/// </para>
/// <para>
/// <strong>The free-text term goes last</strong>, following the rule <c>ReferenceQueryKey</c> establishes: it is
/// the only part whose value can contain the <c>|</c> and <c>=</c> this string is assembled from, and nothing
/// after it means nothing can be misread as belonging to another field. Every other value is an enum name, a
/// GUID, a round-trip timestamp or an integer, none of which can contain either character.
/// </para>
/// </remarks>
public static class RecipeSearchScope
{
    /// <summary>The resource this scope belongs to, matching the route's collection segment.</summary>
    public const string Resource = "recipes";

    public static string Build(Guid workspaceId, RecipeSearchSort sort, RecipeSearchFilters filters)
    {
        var parts = new List<string>
        {
            $"resource={Resource}",
            $"workspace={workspaceId:D}",
            $"sort={sort}",
            $"status={Names(filters.Statuses)}",
            $"tag={Ids(filters.TagIds)}",
            $"cuisine={Ids(filters.CuisineIds)}",
            $"course={Ids(filters.CourseIds)}",
            $"author={Ids(filters.CreatorMembershipIds)}",
            $"readiness={filters.LatestVersionReadiness}",
            $"review={filters.IngredientReview}",
            $"updatedFrom={Timestamp(filters.UpdatedOnOrAfter)}",
            $"updatedBefore={Timestamp(filters.UpdatedBefore)}",
            $"createdFrom={Timestamp(filters.CreatedOnOrAfter)}",
            $"createdBefore={Timestamp(filters.CreatedBefore)}",

            // Last, and only once.
            $"q={filters.Search}",
        };

        return string.Join('|', parts);
    }

    private static string Names<TEnum>(IReadOnlyList<TEnum>? values)
        where TEnum : struct, Enum =>
        values is null ? string.Empty : string.Join(',', values.Select(value => value.ToString()).Order(StringComparer.Ordinal));

    private static string Ids(IReadOnlyList<Guid>? values) =>
        values is null ? string.Empty : string.Join(',', values.Select(value => value.ToString("D")).Order(StringComparer.Ordinal));

    private static string Timestamp(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
}
