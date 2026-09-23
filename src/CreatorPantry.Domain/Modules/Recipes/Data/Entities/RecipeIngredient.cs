using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One line of a recipe's ingredient list. Interior to the <see cref="Recipe"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisplayText"/> is the recipe. Everything else on this entity is a reading of it: a parsed
/// quantity, a recognised unit, a matched vocabulary ingredient. Each of those is nullable and additive, and
/// none of them may ever be written back over the creator's wording — that is the rule recipes.md exists to
/// state, and this is the entity where it is most easily broken.
/// </para>
/// <para>
/// The structure is what makes scaling and conversion possible at all, and it is also what makes them
/// refusable: a line with no <see cref="Quantity"/>, or one marked
/// <see cref="IngredientScaling.ReviewRequired"/>, is surfaced to the creator rather than multiplied.
/// </para>
/// </remarks>
public class RecipeIngredient : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Denormalized from the owning group, and part of the composite foreign key to it rather than a loose
    /// column: <c>(WorkspaceId, RecipeId, RecipeIngredientGroupId)</c> points at the group's alternate key,
    /// so this cannot drift from the group's recipe or the group's workspace.
    /// </summary>
    public Guid RecipeId { get; set; }

    public Guid RecipeIngredientGroupId { get; set; }

    /// <summary>Position within the group, unique there. See <see cref="RecipeIngredientGroup.SortOrder"/>.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// The line exactly as the creator typed it: "2 cups (240 g) all-purpose flour, sifted". Required, and
    /// canonical. Nothing below is permitted to rewrite it, and a derivative that quotes an ingredient quotes
    /// this.
    /// </summary>
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>
    /// The ingredient-name span of <see cref="DisplayText"/> as parsed — "all-purpose flour" out of the line
    /// above — so an interface can emphasise the name without re-parsing on every render. A reading of the
    /// text, not a correction to it.
    /// </summary>
    public string? IngredientNameText { get; set; }

    /// <summary>
    /// The parsed quantity, or the low end of a range. Null when the line has no number ("a pinch of salt")
    /// or when nothing has parsed it yet.
    /// </summary>
    public decimal? Quantity { get; set; }

    /// <summary>
    /// The high end of a range: "2 to 3 tablespoons" stores 2 here and 3 there. Null for a single quantity.
    /// Ranges are common enough in recipe writing that collapsing them to a midpoint would put an invented
    /// number into every scaled result.
    /// </summary>
    public decimal? QuantityUpper { get; set; }

    /// <summary>The recognised unit, or null when none was written or none was matched. Additive.</summary>
    public Guid? MeasurementUnitId { get; set; }

    /// <summary>
    /// Mirrors the referenced unit's dimension so the composite foreign key can point at
    /// <c>MeasurementUnits (Id, Dimension)</c>, and <c>CK_RecipeIngredients_Unit_Dimension</c> can rule out
    /// <see cref="MeasurementDimension.Temperature"/>. An ingredient measured in degrees is a parse error,
    /// and this makes it one the database refuses.
    /// </summary>
    public MeasurementDimension? MeasurementUnitDimension { get; set; }

    /// <summary>
    /// The matched vocabulary ingredient. Null whenever <see cref="MatchStatus"/> is anything but
    /// <see cref="IngredientMatchStatus.Matched"/>. Additive: matching enriches the line, it never replaces
    /// <see cref="DisplayText"/>.
    /// </summary>
    public Guid? IngredientId { get; set; }

    /// <summary>
    /// Whether matching has been attempted and what came of it, so an unmatched line can be shown as
    /// unresolved rather than as blank.
    /// </summary>
    public IngredientMatchStatus MatchStatus { get; set; } = IngredientMatchStatus.NotAttempted;

    /// <summary>"finely chopped", "at room temperature" — the preparation the line asks for.</summary>
    public string? PreparationNote { get; set; }

    /// <summary>Whether the recipe works without this line.</summary>
    public bool IsOptional { get; set; }

    /// <summary>
    /// How this line behaves when the recipe is scaled. Recorded rather than inferred at scale time, so the
    /// judgment that "1 package" cannot be doubled is made once and stays inspectable.
    /// </summary>
    public IngredientScaling ScalingBehavior { get; set; } = IngredientScaling.Proportional;
}
