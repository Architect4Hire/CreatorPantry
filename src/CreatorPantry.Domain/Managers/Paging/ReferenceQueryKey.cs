namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// Builds the two derived strings that identify a reference query: the cache key segment naming one exact
/// page, and the scope a cursor is bound to.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, both learned the hard way.
/// </para>
/// <para>
/// <strong>Both carry the raw search term, not the normalized one.</strong>
/// <see cref="ReferenceSearch"/> exists because the two forms are not interchangeable, and the repositories
/// filter on both — raw against display names and codes, normalized against alias keys. Keying on the
/// normalized form alone collapses terms that return different rows: <c>gluten-free</c> and
/// <c>gluten free</c> both normalize to <c>gluten free</c>, but only the first matches the display name
/// "Gluten-Free". They would share one entry and serve each other's answer until it expired. The normalized
/// form is a pure function of the raw one, so the raw form alone identifies the query completely.
/// </para>
/// <para>
/// <strong>Caller-supplied text is made unambiguous, not assumed to be.</strong> The filters and the page
/// size have constrained alphabets — a clamped integer, a lowercase code, an enum name — and none can contain
/// the <c>|</c> separator. Two fields can: the search term, which goes last so nothing can follow it to be
/// misread; and the cursor's position, which is length-prefixed because it cannot go last and its halves come
/// out of a payload that only splits on <c>\u001F</c>.
/// </para>
/// <para>
/// The cursor is caller-supplied in the fullest sense: <see cref="ReferenceCursor.Fingerprint"/> is unkeyed,
/// so anyone can mint a structurally valid cursor carrying any tie-breaker they like. An earlier version of
/// this file asserted that every non-search field had a safe alphabet and used the bare tie-breaker, which was
/// wrong twice over — it let a forged cursor collide with a legitimate query's key, and it dropped the sort
/// value that the repositories' <c>WHERE</c> clauses actually use.
/// </para>
/// </remarks>
public static class ReferenceQueryKey
{
    /// <summary>Names one page: the set to read, plus the position and size that pick a page out of it.</summary>
    public static string Build(
        ReferenceSearch? search,
        ReferenceCursor? cursor,
        int limit,
        params (string Name, string? Value)[] filters)
    {
        var parts = new List<string> { $"limit={limit}", $"cursor={Position(cursor)}" };

        parts.AddRange(filters.Select(filter => $"{filter.Name}={filter.Value}"));

        // Last, and only once: see the remarks.
        parts.Add($"q={search?.Raw}");

        return string.Join('|', parts);
    }

    /// <summary>
    /// Names the ordered set a cursor is a position within: the resource, and every filter that decides which
    /// rows are in it.
    /// </summary>
    /// <remarks>
    /// Excludes the page size, which changes how many rows follow a position without changing the position or
    /// the set it is in — so a client may legitimately change <c>limit</c> mid-page. Excludes the cursor
    /// itself, which would otherwise give every page a different scope from the one before it and defeat the
    /// check entirely.
    /// </remarks>
    public static string Scope(
        string resource,
        ReferenceSearch? search,
        params (string Name, string? Value)[] filters)
    {
        var parts = new List<string> { $"resource={resource}" };

        parts.AddRange(filters.Select(filter => $"{filter.Name}={filter.Value}"));
        parts.Add($"q={search?.Raw}");

        return string.Join('|', parts);
    }

    /// <summary>
    /// The position half of a cursor, length-prefixed so it cannot be confused with anything around it.
    /// </summary>
    /// <remarks>
    /// Carries <em>both</em> halves, because both are used by the repositories' keyset predicates — a key
    /// naming only the tie-breaker would stand for two different <c>WHERE</c> clauses whenever a row's sort
    /// value changed, which a deploy that renames a display name does routinely. Length-prefixed because the
    /// halves are caller-controlled and may contain <c>|</c> or <c>=</c>: with an explicit length, a segment
    /// can only be read one way, so no forged cursor can make its key collide with another query's.
    /// </remarks>
    private static string Position(ReferenceCursor? cursor) =>
        cursor is null
            ? string.Empty
            : $"{cursor.SortValue.Length}:{cursor.SortValue}:{cursor.TieBreaker.Length}:{cursor.TieBreaker}";
}
