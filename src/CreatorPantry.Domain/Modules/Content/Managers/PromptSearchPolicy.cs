using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Term handling for the prompt library's search, shared by the validator, the query factory and the repository.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The page-size bounds are deliberately not restated here.</strong> They live in
/// <see cref="ReferencePolicy"/>, which is where the published <c>limit</c> parameter is generated from, and a
/// second set of numbers for prompts would mean one query parameter with two contracts and no way to tell which
/// a caller got. <see cref="PromptSearchCriteria.Limit"/> clamps through
/// <see cref="ReferencePolicy.ClampPageSize"/> for that reason.
/// </para>
/// <para>
/// What is here is the term handling, and it is <see cref="Recipes.Managers.RecipeSearchPolicy"/>'s rather than
/// <see cref="ReferencePolicy.NormalizeSearch"/>'s. That one produces the dual raw/normalized form the
/// reference catalogues need, where an alias is stored pre-normalized; a prompt has no alias table and no
/// normalized column, so the normalized half would be dead weight at best and, applied to raw prompt text, a
/// silent failure to match.
/// </para>
/// </remarks>
public static class PromptSearchPolicy
{
    /// <summary>
    /// Below this, a term is ignored rather than applied — the same floor, for the same reason, as
    /// <see cref="ReferencePolicy.MinSearchLength"/>: one character matches most of a library, so filtering on
    /// it costs a scan to return nearly everything the first unfiltered page would have returned anyway.
    /// </summary>
    public const int MinSearchLength = ReferencePolicy.MinSearchLength;

    /// <summary>
    /// The longest term accepted, shared with the other searchable routes so one <c>search</c> parameter does
    /// not have two ceilings. Bounds what can become a <c>LIKE</c> pattern.
    /// </summary>
    public const int SearchMaxLength = ReferencePolicy.SearchMaxLength;

    /// <summary>
    /// The form a term is matched in, or <c>null</c> when there is nothing worth filtering on.
    /// </summary>
    /// <remarks>
    /// Lowercased in C# rather than left to the database: SQL Server's default collation is case-insensitive
    /// and SQLite's is not, and a search should mean the same thing in a test as in production. The columns are
    /// lowered to match, which forfeits an index seek — no loss, because a substring match was never going to
    /// seek one.
    /// </remarks>
    public static string? NormalizeSearch(string? search)
    {
        var term = search?.Trim().ToLowerInvariant();

        return string.IsNullOrEmpty(term) || term.Length < MinSearchLength ? null : term;
    }

    /// <summary>
    /// The channel key as it is matched, or <c>null</c> when none was given. Trimmed only.
    /// </summary>
    /// <remarks>
    /// <strong>Not validated against the catalogue, unlike a save.</strong> A filter naming a channel that does
    /// not exist, or one that has been retired since a prompt stored its key, is an empty page rather than a
    /// refusal — a read must keep answering for keys the catalogue has moved on from, which is the whole reason
    /// a retired channel stays readable (12.1). Matched case-sensitively, because the stored keys are the
    /// catalogue's own lowercase ones.
    /// </remarks>
    public static string? NormalizeChannel(string? channelKey)
    {
        var trimmed = channelKey?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
