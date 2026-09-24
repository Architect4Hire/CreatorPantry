namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The orderings a recipe search may be asked for. An allow-list, not a column name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An enum rather than a sort expression, deliberately.</strong> A client-supplied column name is an
/// injection surface on the way in and an unindexed sort on the way out; naming the orderings means every one
/// of them is a decision someone made once, with an index behind it.
/// </para>
/// <para>
/// Every member here has an index whose key matches it exactly, including the tie-breaker — see
/// <c>RecipeConfiguration</c>. That pairing is the whole point: an ordering without one is a sort of the
/// workspace's entire recipe table on every page. Adding a member therefore means adding an index, which is
/// why <c>CreatedAt</c> is <em>not</em> here yet despite being an obvious third choice.
/// </para>
/// <para>
/// Not persisted, so the numbering carries no data. Zero is the default ordering on purpose: a search that
/// forgot to say how to sort should get the same answer the library screen opens with, not an error.
/// </para>
/// </remarks>
public enum RecipeSearchSort
{
    /// <summary>
    /// Most recently edited first, tie-broken by id. The default, and what a creator returning to their
    /// library almost always wants: the thing they were last working on, at the top.
    /// </summary>
    RecentlyUpdated = 0,

    /// <summary>Alphabetical by the creator's own title, tie-broken by id.</summary>
    Title = 1,
}
