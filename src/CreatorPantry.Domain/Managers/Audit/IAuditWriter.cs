namespace CreatorPantry.Domain.Managers.Audit;

/// <summary>
/// One safe-to-log audit entry. <see cref="Summary"/>, <see cref="BeforeReference"/>, and
/// <see cref="AfterReference"/> must never carry secrets, tokens, prompt bodies, recipe bodies, or provider
/// payloads — see <see cref="AuditLog"/>'s remarks.
/// </summary>
public sealed record AuditEntry(
    string? ActorUserId,
    string Action,
    string ResourceType,
    string ResourceId,
    Guid CorrelationId,
    string Summary,
    string? BeforeReference = null,
    string? AfterReference = null);

/// <summary>
/// Injectable into any DataLayer that also writes domain entities on the same <c>CreatorPantryDbContext</c>.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Stages an <see cref="AuditLog"/> row on the ambient DbContext without saving, so it commits in the
    /// same <c>SaveChangesAsync</c> as the domain write the caller is already about to save — the audit
    /// entry and the action it records commit together, or neither does. <c>WorkspaceId</c> is stamped
    /// automatically by <c>WorkspaceOwnershipInterceptor</c>, the same as any other workspace-owned insert.
    /// </summary>
    void Record(AuditEntry entry);
}
