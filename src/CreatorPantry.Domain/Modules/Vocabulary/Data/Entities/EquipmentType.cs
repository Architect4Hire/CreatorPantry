namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A kind of equipment a recipe calls for — <c>dutch-oven</c>, <c>stand-mixer</c>, <c>sheet-pan</c>,
/// <c>food-processor</c>. Global reference data with no <c>WorkspaceId</c> (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// A <em>kind</em>, never an instance. "My 5.5-quart enamelled Dutch oven" is one creator's equipment and
/// stays workspace-owned; putting it here would publish one creator's kit to every other workspace, which
/// is exactly what the platform/creator data zones exist to prevent.
/// </para>
/// <para>
/// No capacity, size, or brand either. A recipe that needs a 9-inch round pan says so in the creator's own
/// words on the recipe itself; this table only names the category so recipes can be filtered by what they
/// require.
/// </para>
/// </remarks>
public class EquipmentType : ControlledVocabulary;
