

namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>Splits the rows a reference repository fetched into the page and whether another follows.</summary>
internal static class ReferenceKeysetQuery
{
    /// <summary>
    /// Discards the probe row and reports whether it was there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every reference repository fetches <c>Limit + 1</c> rows. That extra row is how a page learns whether
    /// another one follows, without a second <c>COUNT</c> query against a set that may have changed in
    /// between — and without ever reporting "more" for a page that turns out to be the last.
    /// </para>
    /// <para>
    /// Note what this no longer does: mint the cursor. A cursor is bound to the resource and filters it was
    /// issued for, and a repository knows neither — it is handed a predicate, not a route. Business owns that,
    /// which is also the layer where a value belonging to the published contract belongs.
    /// </para>
    /// </remarks>
    public static (IReadOnlyList<T> Rows, bool HasMore) ToPage<T>(this List<T> fetched, int limit)
        where T : IReferenceRow =>
        fetched.Count <= limit
            ? (fetched, false)
            : (fetched.Take(limit).ToList(), true);
}
