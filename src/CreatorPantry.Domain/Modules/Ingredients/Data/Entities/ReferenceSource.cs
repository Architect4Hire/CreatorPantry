using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Entities;

/// <summary>
/// Where a shared reference fact came from. Global reference data: no <c>WorkspaceId</c>, not
/// <see cref="Tenancy.IWorkspaceOwned"/>, readable before a workspace is resolved (tenancy.md).
/// </summary>
/// <remarks>
/// Introduced for density references, but deliberately general: dietary and allergen evidence, and later
/// provider-supplied nutrition, need the same citation and the same <see cref="Kind"/> distinction. One
/// provenance table keeps a single citation per source instead of copying its text onto every fact.
/// </remarks>
public class ReferenceSource
{
    public Guid Id { get; set; }

    /// <summary>Stable lowercase machine key, e.g. <c>usda-fdc</c>. Permanent; seed data resolves by it.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// How much authority this source carries. Recorded rather than inferred, so a value's standing travels
    /// with it — a conversion can tell a creator which figures are vetted and which are estimates.
    /// </summary>
    public ReferenceSourceKind Kind { get; set; }

    public string? Url { get; set; }

    /// <summary>
    /// Human-readable attribution, shown wherever a derived value is presented. Required: a reference fact
    /// that cannot say where it came from is not usable (recipes.md).
    /// </summary>
    public string Citation { get; set; } = string.Empty;

    /// <summary>A retired source stays readable so the facts citing it keep their provenance.</summary>
    public bool IsActive { get; set; } = true;
}
