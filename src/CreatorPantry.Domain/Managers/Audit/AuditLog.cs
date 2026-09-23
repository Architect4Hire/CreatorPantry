using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Managers.Audit;

/// <summary>
/// An immutable record of one sensitive action within a workspace. Written once and never updated or
/// deleted by feature code (enforced by <see cref="AuditLogImmutabilityInterceptor"/>) — a correction is a
/// new row, not an edit to this one.
/// </summary>
/// <remarks>
/// <see cref="Summary"/>, <see cref="BeforeReference"/>, and <see cref="AfterReference"/> must never carry
/// secrets, tokens, prompt bodies, recipe bodies, or provider payloads (SEC-007, DATA-RQ-001):
/// before/after are pointers (an id, a version number) or short safe-field-name diffs, not the actual
/// content that changed.
/// </remarks>
public class AuditLog : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The acting user, or null for a system/background-initiated entry.</summary>
    public string? ActorUserId { get; set; }

    /// <summary>A stable code, e.g. <c>workspace.renamed</c>.</summary>
    public string Action { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    /// <summary>Ties this entry to the request/operation that produced it.</summary>
    public Guid CorrelationId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? BeforeReference { get; set; }

    public string? AfterReference { get; set; }
}
