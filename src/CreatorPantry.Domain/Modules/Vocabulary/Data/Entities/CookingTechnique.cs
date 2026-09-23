using CreatorPantry.Domain.Modules.Vocabulary.Managers;
namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A cooking method a recipe can be described and filtered by — <c>bake</c>, <c>braise</c>,
/// <c>stir-fry</c>, <c>no-cook</c>, <c>water-bath-canning</c>. Global reference data with no
/// <c>WorkspaceId</c> (tenancy.md). This is the vocabulary behind what the requirements call a recipe's
/// "method".
/// </summary>
public class CookingTechnique : ControlledVocabulary
{
    /// <summary>
    /// Whether guidance about this technique must carry an explicit caution — canning, fermenting, curing,
    /// smoking, sous vide, and the other techniques where getting it wrong is a food-safety outcome rather
    /// than a disappointing dinner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read this flag in one direction only. <c>true</c> means a caution is required. <c>false</c> means no
    /// caution has been attached to this entry — it is <em>not</em> a statement that the technique is safe,
    /// and no caller may render it as one. The asymmetry is deliberate and matches the rule the trait model
    /// is built on: missing data means unknown, never safe (recipes.md).
    /// </para>
    /// <para>
    /// It exists so the requirement in recipes.md — that preservation, canning, fermentation, and
    /// internal-temperature claims carry a vetted reference or an explicit caution — has one structured hook
    /// that generation and review paths can read. The alternative is a list of technique codes copied into
    /// prompt text, where it drifts silently and is invisible to review.
    /// </para>
    /// <para>
    /// What it is not: a severity, a score, or a certification. Nothing here records that a technique is
    /// approved, tested, or safe when performed correctly. If a technique ever needs vetted procedural
    /// references, those belong in their own cited, dated table beside
    /// <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.IngredientDensityReference"/> and the trait model, not in a column here.
    /// </para>
    /// </remarks>
    public bool RequiresSafetyCaution { get; set; }
}
