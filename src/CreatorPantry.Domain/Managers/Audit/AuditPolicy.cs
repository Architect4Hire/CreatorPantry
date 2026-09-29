namespace CreatorPantry.Domain.Managers.Audit;

/// <summary>
/// Field limits for <see cref="CreatorPantry.Domain.Managers.Audit.AuditLog"/> and
/// <see cref="CreatorPantry.Domain.Managers.Audit.PlatformAuditLog"/>, shared by EF configuration and callers.
/// </summary>
public static class AuditPolicy
{
    public const int ActionMaxLength = 200;
    public const int ResourceTypeMaxLength = 100;
    public const int ResourceIdMaxLength = 100;

    /// <summary>
    /// An actor or subject identifier: an Identity user id, an ops client id, or an account id. 450 to match
    /// Identity's key width, the same figure <c>AuditLog.ActorUserId</c> and <c>AiUsagePolicy</c> use.
    /// </summary>
    public const int ActorIdMaxLength = 450;

    /// <summary>The acting client's or account's display name, recorded so a revoked key is still legible.</summary>
    public const int ActorNameMaxLength = 200;

    /// <summary>
    /// A human-readable, safe-to-display summary — never a secret, token, prompt body, recipe body, or
    /// provider payload. The max length is a technical backstop, not the redaction policy itself: callers
    /// are responsible for only ever passing safe content here.
    /// </summary>
    public const int SummaryMaxLength = 1000;

    /// <summary>
    /// A pointer (an id, a version number) or a short safe-field-name diff — never the actual before/after
    /// content of a mutation.
    /// </summary>
    public const int ReferenceMaxLength = 200;
}
