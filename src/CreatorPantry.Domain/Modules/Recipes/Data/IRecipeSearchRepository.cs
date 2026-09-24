using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// The purpose-built paged query behind a creator's recipe library. Persistence only — no domain decisions, no
/// caching, and never <c>IgnoreQueryFilters</c>: workspace scope comes from the global query filter, so another
/// workspace's recipes are simply not there to be filtered, sorted, counted, or paged past.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IRecipeRepository"/> on purpose.</strong> That interface is EF Core access
/// for the recipe <em>aggregate</em> — every method on it returns a whole recipe and its children, because that
/// is what its callers are about to read or write. Nothing here ever materialises an aggregate: it projects
/// summaries in SQL. Putting a search on that interface would make "returns the aggregate" stop being true of
/// it, and backend.md prefers a purpose-built query to a widened one when the purpose is what communicates
/// intent.
/// </para>
/// <para>
/// <strong>No cursor is minted here.</strong> A cursor is bound to the route and filters it was issued for, and
/// a repository is handed a predicate rather than a route — so this reports only whether another page follows,
/// exactly as every reference repository does, and Business turns that into a cursor with
/// <c>PageBuilder</c>.
/// </para>
/// </remarks>
public interface IRecipeSearchRepository
{
    /// <summary>
    /// Reads one page of recipe summaries matching the criteria, in the requested order.
    /// </summary>
    /// <returns>
    /// The page's rows, and whether another page follows. An empty page with no more rows is the honest answer
    /// for a workspace with no recipes and for filters nothing matches alike — neither is an error, and a search
    /// that matched nothing must not be distinguishable from a library that is empty.
    /// </returns>
    /// <remarks>
    /// Fetches one row beyond the limit and discards it, which is how "does another page follow" is answered
    /// without a second <c>COUNT</c> against a set that may have changed in between, and without ever promising
    /// a further page that turns out not to exist.
    /// </remarks>
    Task<(IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore)> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts every recipe matching the criteria's filters, across all pages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second method rather than a field on the page, so a caller that does not need a total does not pay for
    /// one. Both methods are built from the same private filter, which is what stops the counted set and the
    /// paged set from drifting apart — a total that counted a different set than it paged would be worse than no
    /// total at all.
    /// </para>
    /// <para>
    /// <see cref="RecipeSearchCriteria.Position"/> and <see cref="RecipeSearchCriteria.Limit"/> are deliberately
    /// ignored. Applying the cursor would count what remains after the current page, which reads like a total
    /// and is not one.
    /// </para>
    /// <para>
    /// Being a separate statement, this can disagree with the rows of a page read beside it by however many
    /// recipes were written in between. That is the accepted cost of not paying for a windowed count on every
    /// page whether or not anyone asked for one; a creator seeing "25 of 41" briefly read "of 40" is not a
    /// defect worth that price.
    /// </para>
    /// </remarks>
    Task<int> CountAsync(RecipeSearchCriteria criteria, CancellationToken cancellationToken);
}
