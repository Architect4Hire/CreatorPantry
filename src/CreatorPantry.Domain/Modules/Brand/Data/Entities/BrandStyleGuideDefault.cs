using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// The guide version a workspace writes with unless a task says otherwise. At most one per workspace — the
/// workspace is the key — and the only stored activation decision.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its own row, not a column on <see cref="BrandProfile"/>.</strong> The profile holds facts and no
/// style; activating a guide is not an edit to those facts, and should neither need a profile to exist nor
/// invalidate one that is open in an editor.
/// </para>
/// <para>
/// Mutable: activation repoints it, and <see cref="RowVersion"/> is the "expected current default" check. No
/// row means the workspace has no default, never a fallback to some other guide.
/// </para>
/// </remarks>
public class BrandStyleGuideDefault : IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    /// <summary>An approved version: the foreign key targets <see cref="BrandStyleGuideApproval"/>.</summary>
    public Guid BrandStyleGuideVersionId { get; set; }

    /// <summary>The activator's own note, or null.</summary>
    public string? Reason { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership.</summary>
    public Guid ActivatedByMembershipId { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
