using System.Globalization;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Builds the string that identifies one ordered set of library assets, so a cursor can be bound to it.
/// </summary>
/// <remarks>
/// <para>
/// Every filter and the ordering go in; the page size and <c>includeTotal</c> do not, following
/// <c>RecipeSearchScope</c> and the rule <see cref="CreatorPantry.Domain.Managers.Paging.ReferenceCursor"/> states: changing how
/// many rows come after a position is legitimate, and asking for a count does not change which rows follow.
/// </para>
/// <para>
/// <strong>List filters are sorted before they are folded in.</strong> Without that, <c>?tag=a,b</c> and
/// <c>?tag=b,a</c> are the same query with two different scopes, and a client that reordered its own
/// parameters between pages would have its cursor refused for no reason it could see.
/// </para>
/// <para>
/// <strong>The free-text term goes last.</strong> It is the only part whose value can contain the <c>|</c>
/// and <c>=</c> this string is assembled from, and nothing after it means nothing can be misread as
/// belonging to another field. Every other value is an enum name, a GUID, a round-trip timestamp or an
/// opaque key — and the keys are bounded to 64 characters of workspace vocabulary.
/// </para>
/// </remarks>
public static class MediaAssetSearchScope
{
    /// <summary>The resource this scope belongs to, matching the route's collection segment.</summary>
    public const string Resource = "dam-assets";

    public static string Build(Guid workspaceId, MediaAssetSearchSort sort, MediaAssetSearchFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var parts = new List<string>
        {
            $"resource={Resource}",
            $"workspace={workspaceId:D}",
            $"sort={sort}",
            $"channel={filters.ChannelKey}",
            $"platform={filters.PlatformKey}",
            $"day={filters.Day}",
            $"style={filters.StyleKey}",
            $"cuisine={Ids(filters.CuisineIds)}",
            $"course={Ids(filters.CourseIds)}",
            $"tag={Ids(filters.TagIds)}",
            $"recipe={filters.RecipeId:D}",
            $"createdFrom={Timestamp(filters.CreatedOnOrAfter)}",
            $"createdBefore={Timestamp(filters.CreatedBefore)}",

            // Last, and only once.
            $"q={filters.Search}",
        };

        return string.Join('|', parts);
    }

    private static string Ids(IReadOnlyList<Guid>? values) =>
        values is null
            ? string.Empty
            : string.Join(',', values.Select(value => value.ToString("D")).Order(StringComparer.Ordinal));

    private static string Timestamp(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
}
