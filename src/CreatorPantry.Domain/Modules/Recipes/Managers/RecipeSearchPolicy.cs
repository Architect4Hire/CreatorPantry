using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Limits and term handling for recipe search, shared by the criteria factory, the repository, and the
/// validation that arrives with the read seam.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The page-size bounds are deliberately not restated here.</strong> They live in
/// <see cref="ReferencePolicy"/>, which is where the published OpenAPI <c>limit</c> parameter is generated
/// from, and a second set of numbers for recipes would mean one query string parameter with two contracts and
/// no way to tell which a caller got. <see cref="RecipeSearchCriteria.Limit"/> clamps through
/// <see cref="ReferencePolicy.ClampPageSize"/> for that reason. (<c>ReferencePolicy</c> is poorly named for a
/// shared-kernel type — renaming it is a change of its own, not a thing to do in passing here.)
/// </para>
/// <para>
/// What <em>is</em> here is the term handling, because a recipe title is not a reference name.
/// <see cref="ReferencePolicy.NormalizeSearch"/> produces the dual raw/normalized form the reference
/// catalogues need, where an alias is stored pre-normalized and a display name is not. A recipe has no alias
/// table and no normalized-title column, so the normalized half would be dead weight at best and, applied to a
/// raw <c>Title</c>, a silent failure to match.
/// </para>
/// </remarks>
public static class RecipeSearchPolicy
{
    /// <summary>
    /// Below this, a term is ignored rather than applied — the same floor, for the same reason, as
    /// <see cref="ReferencePolicy.MinSearchLength"/>: one character matches most of a library, so filtering on
    /// it costs a scan to return nearly everything the first unfiltered page would have returned anyway.
    /// </summary>
    public const int MinSearchLength = ReferencePolicy.MinSearchLength;

    /// <summary>
    /// The longest term accepted, shared with the reference routes so one <c>search</c> parameter does not have
    /// two ceilings. Bounds what can become a <c>LIKE</c> pattern.
    /// </summary>
    public const int SearchMaxLength = ReferencePolicy.SearchMaxLength;

    /// <summary>
    /// The form a term is matched in, or <c>null</c> when there is nothing worth filtering on.
    /// </summary>
    /// <remarks>
    /// Lowercased in C# rather than left to the database, matching <see cref="ReferenceSearch"/>'s reasoning:
    /// SQL Server's default collation is case-insensitive and SQLite's is not, and a search should mean the
    /// same thing in a test as it does in production. The column is lowered to match, which forfeits an index
    /// seek — no loss, because a substring match was never going to seek one.
    /// </remarks>
    public static string? NormalizeSearch(string? search)
    {
        var term = search?.Trim().ToLowerInvariant();

        return string.IsNullOrEmpty(term) || term.Length < MinSearchLength ? null : term;
    }
}
