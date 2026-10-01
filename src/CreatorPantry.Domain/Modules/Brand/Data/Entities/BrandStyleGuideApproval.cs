using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// The creator's approval of one <see cref="BrandStyleGuideVersion"/>. Write-once, and at most one per
/// version: the version is the key. Its existence is what "approved" means.
/// </summary>
/// <remarks>
/// Approval is never withdrawn. Moving off an approved version means approving and activating another, which
/// keeps every generation that cited this one traceable to a version that was approved when it was used.
/// <see cref="BrandStyleGuideDefault"/> points here rather than at the version, so only an approved version
/// can be made the workspace default.
/// </remarks>
public class BrandStyleGuideApproval : IWorkspaceOwned, IImmutableRecord
{
    public Guid WorkspaceId { get; set; }

    public Guid BrandStyleGuideVersionId { get; set; }

    /// <summary>The approver's own note, or null.</summary>
    public string? Reason { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership.</summary>
    public Guid ApprovedByMembershipId { get; set; }

    public DateTimeOffset ApprovedAt { get; set; }
}
