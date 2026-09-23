using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// EF Core access for the recipe aggregate. Persistence only — no domain decisions, no caching, no HTTP
/// shapes, and never <c>IgnoreQueryFilters</c>: workspace scope comes from the global query filter, so a
/// recipe belonging to another workspace is simply not there.
/// </summary>
public interface IRecipeRepository
{
    /// <summary>
    /// Stages a new aggregate and its children for insertion.
    /// </summary>
    /// <remarks>
    /// Synchronous and returning nothing, because it performs no I/O: it adds to the change tracker, and the
    /// DataLayer that owns the transaction decides when to save. Naming it <c>AddAsync</c> would advertise a
    /// round trip that does not happen. <c>WorkspaceId</c> is not set here — the ownership interceptor stamps
    /// every workspace-owned entity from the resolved context, and feature code assigning it is a defect.
    /// </remarks>
    void Add(Recipe recipe);

    /// <summary>
    /// Reads one recipe with every child and the metadata of its most recent version.
    /// </summary>
    /// <returns>
    /// <c>null</c> when no such recipe is visible. A recipe that does not exist and one belonging to another
    /// workspace are deliberately indistinguishable here, which is what lets the read seam answer 404 to both
    /// without disclosing that the recipe exists (tenancy.md).
    /// </returns>
    /// <remarks>
    /// Reads across several statements rather than one — see the implementation for why — so it observes
    /// whatever isolation the caller's transaction provides. A caller that needs the aggregate and its
    /// version to be mutually consistent must establish that boundary itself; this method does not open one.
    /// </remarks>
    Task<CompleteRecipe?> GetCompleteAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the same aggregate as <see cref="GetCompleteAsync"/>, <strong>tracked</strong>, so a caller can
    /// change it and save.
    /// </summary>
    /// <returns>
    /// <c>null</c> when no such recipe is visible — unknown and belonging-to-another-workspace remaining
    /// indistinguishable, exactly as on the read path.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="GetCompleteAsync"/> rather than a <c>tracked</c> parameter on it, because
    /// tracking an aggregate is not a tuning knob: it is the caller announcing an intention to write, and
    /// every read that does not mean to write should stay unable to. The two share one query internally.
    /// </para>
    /// <para>
    /// The returned <see cref="CompleteRecipe.CurrentVersion"/> is the highest-numbered version and is
    /// <em>not</em> tracked — versions are immutable, so nothing may edit one — but it is what tells a
    /// writer which number comes next and which version the new one descends from.
    /// </para>
    /// <para>
    /// This opens no transaction, so the state it returns can be stale by the time a caller saves. That is
    /// not a gap: <c>Recipe.RowVersion</c> travels with the aggregate, and the save quotes it, so an edit
    /// composed against state that has since moved is refused rather than applied.
    /// </para>
    /// </remarks>
    Task<CompleteRecipe?> GetForUpdateAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads just the recipe's concurrency token, or <c>null</c> when no such recipe is visible.
    /// </summary>
    /// <remarks>
    /// Exists for one question, asked only after a write has already failed: is this recipe still in the
    /// state the failed edit was composed against? A whole-aggregate read would answer it too, at the cost
    /// of eight queries to compare eight bytes.
    /// </remarks>
    Task<byte[]?> FindRowVersionAsync(Guid recipeId, CancellationToken cancellationToken);
}
