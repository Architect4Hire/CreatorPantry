namespace CreatorPantry.Domain.Managers.Audit;

/// <summary>
/// One safe-to-log platform audit entry. <see cref="Reason"/>, <see cref="BeforeReference"/>, and
/// <see cref="AfterReference"/> must never carry secrets, API keys, prompt bodies, recipe bodies, or
/// provider payloads — see <see cref="CreatorPantry.Domain.Managers.Audit.PlatformAuditLog"/>'s remarks.
/// </summary>
/// <param name="Reason">
/// Why the operator made this change, in their own words. Required, because USAGE-009 asks every quota
/// change, suspension and restoration to name one.
/// </param>
public sealed record PlatformAuditEntry(
    PlatformAuditActorType ActorType,
    string ActorId,
    string ActorName,
    string Action,
    string SubjectType,
    string SubjectId,
    Guid CorrelationId,
    string Reason,
    string? BeforeReference = null,
    string? AfterReference = null);

/// <summary>
/// Who is making a platform change. Resolved from the authenticated credential, never from the request.
/// </summary>
/// <remarks>
/// Beside <see cref="PlatformAuditEntry"/> in the shared kernel rather than in whichever module first needed
/// one: the ops authentication handler produces it for every ops route, so a second ops controller in another
/// module must not have to depend on the first module to name its own actor.
/// </remarks>
public sealed record PlatformActor(PlatformAuditActorType Type, string Id, string Name);

/// <summary>
/// The platform-level counterpart to <see cref="IAuditWriter"/>, for actions taken outside any workspace.
/// Injectable into any DataLayer that also writes domain entities on the same <c>CreatorPantryDbContext</c>.
/// </summary>
public interface IPlatformAuditWriter
{
    /// <summary>
    /// Stages a <see cref="CreatorPantry.Domain.Managers.Audit.PlatformAuditLog"/> row on the ambient
    /// DbContext without saving, so it commits in the same <c>SaveChangesAsync</c> as the change the caller
    /// is already about to save — the audit entry and the action it records commit together, or neither does.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="IAuditWriter"/>, nothing is stamped by an ownership interceptor here: there is no
    /// workspace to stamp, which is the whole reason this writer exists.
    /// </remarks>
    void Record(PlatformAuditEntry entry);
}
