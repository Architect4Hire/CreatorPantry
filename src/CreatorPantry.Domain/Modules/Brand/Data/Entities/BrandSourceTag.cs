using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One tag in a creator's vocabulary for their brand source material — "launch", "holiday", "long-form".
/// Workspace-owned, and an aggregate root that outlives any one document carrying it.
/// </summary>
/// <remarks>
/// Deliberately separate from the recipe module's <c>WorkspaceTag</c>. That vocabulary describes dishes;
/// this one describes writing and imagery, and sharing a table would put "weeknight" in a picker for style
/// samples. Retired with <see cref="IsActive"/> rather than deleted, as there.
/// </remarks>
public class BrandSourceTag : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The creator's own capitalisation and spacing. What gets displayed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The natural key within a workspace, from <see cref="CreatorPantry.Domain.Managers.Reference.NameNormalization.NormalizeName"/>.
    /// </summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Whether the tag is offered for new input. A retired tag stays on the documents carrying it.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
