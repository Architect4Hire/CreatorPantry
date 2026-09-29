using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Managers.Audit;

/// <summary>Who performed a platform-level action.</summary>
public enum PlatformAuditActorType
{
    Unspecified = 0,

    /// <summary>A machine client authenticated with an ops API key (baseline B-14).</summary>
    OpsClient = 1,

    /// <summary>A signed-in platform administrator.</summary>
    User = 2,
}

/// <summary>
/// An immutable record of one action taken <strong>outside any workspace</strong> — an operator changing an
/// account's AI allowance, suspending its access, restoring it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not <see cref="AuditLog"/>.</strong> That entity is <c>IWorkspaceOwned</c>: its
/// <c>WorkspaceId</c> is required, carries a <em>cascading</em> foreign key to <c>Workspace</c>, leads both
/// of its indexes, and is stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved request scope.
/// An ops route resolves no workspace, so there is nothing to stamp; and a sentinel id would leave the
/// operator's audit trail one workspace deletion away from being erased. The two trails answer different
/// questions and are kept apart.
/// </para>
/// <para>
/// <strong>No <c>WorkspaceId</c>, and therefore no global query filter.</strong> This is the same argument
/// <c>AccountAiUsageEntry</c> already makes and that its model-shape test pins: the row carries actor
/// identifiers, an action code, a reason, and compact state pointers — never a recipe title, a prompt body,
/// a proposal, or any other creator content, and no column may ever be added that does. That is what makes
/// reading it without a workspace filter safe rather than careful.
/// </para>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: a correction is a new row, never an
/// edit to this one.
/// </para>
/// </remarks>
public class PlatformAuditLog : IImmutableRecord
{
    public Guid Id { get; set; }

    /// <summary>Whether an ops client or a platform administrator acted.</summary>
    public PlatformAuditActorType ActorType { get; set; }

    /// <summary>
    /// The ops client id or the acting user id. Deliberately not a foreign key to either table: an audit row
    /// must outlive the credential or the account that acted.
    /// </summary>
    public string ActorId { get; set; } = string.Empty;

    /// <summary>The client or account name at the time of the action, so a revoked key is still legible.</summary>
    public string ActorName { get; set; } = string.Empty;

    /// <summary>A stable code, e.g. <c>account.ai_quota.set</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>What was acted on, e.g. <c>Account</c>.</summary>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary>The subject's identifier, e.g. the Identity account id.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// Why the operator made the change, in their own words. <strong>Required</strong>: USAGE-009 asks every
    /// quota change, suspension and restoration to name one, and a nullable column would let a caller skip it.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>State before the change, as a compact pointer. See <see cref="AfterReference"/>.</summary>
    public string? BeforeReference { get; set; }

    /// <summary>
    /// State after the change, as a compact pointer such as
    /// <c>allowance=1000;unit=Credits;period=Monthly;anchor=1;zone=Etc/UTC;carry=None;suspended=false</c>,
    /// or the literal <c>default</c> when no account quota is in force. Field names and values, never prose
    /// and never content.
    /// </summary>
    public string? AfterReference { get; set; }

    /// <summary>Ties this entry to the request that produced it.</summary>
    public Guid CorrelationId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
