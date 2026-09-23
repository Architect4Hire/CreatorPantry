namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>Turns a repository's rows into a page, minting the cursor that resumes it.</summary>
/// <remarks>
/// The cursor is minted at the Business layer rather than in a repository because it is bound to the resource
/// and filters it was issued for, and a repository is handed a predicate rather than a route — it has no way
/// to know either. This helper is shared so the three reference modules cannot drift on how a page ends.
/// </remarks>
public static class PageBuilder
{
    public static CursorPageServiceModel<TModel> Build<TRecord, TModel>(
        IReadOnlyList<TRecord> rows,
        bool hasMore,
        string scope,
        Func<TRecord, TModel> map)
        where TRecord : IReferenceRow
    {
        // No more rows means no cursor, so a client loops until nextCursor is null rather than comparing
        // counts against a page size it may not have chosen.
        var nextCursor = hasMore && rows.Count > 0
            ? ReferenceCursor.Encode(rows[^1].SortValue, rows[^1].TieBreaker, scope)
            : null;

        return new CursorPageServiceModel<TModel>([.. rows.Select(map)], nextCursor);
    }
}
