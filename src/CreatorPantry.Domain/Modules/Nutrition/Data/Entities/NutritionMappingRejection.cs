using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Nutrition.Data.Entities;

/// <summary>
/// A reference a creator turned down for one mapping, so no automatic proposal offers it again. Workspace-owned.
/// </summary>
public class NutritionMappingRejection : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Set from the mapping, which pins both through a composite key.</summary>
    public Guid WorkspaceId { get; set; }

    public Guid NutritionMappingId { get; set; }

    public string SourceCode { get; set; } = string.Empty;

    public string ExternalRecordId { get; set; } = string.Empty;

    public DateTimeOffset RejectedAt { get; set; }

    public Guid RejectedByMembershipId { get; set; }
}
