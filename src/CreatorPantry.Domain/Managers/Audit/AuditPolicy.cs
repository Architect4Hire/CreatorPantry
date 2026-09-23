namespace CreatorPantry.Domain.Managers.Audit;

/// <summary>Field limits for <see cref="Data.AuditLog"/>, shared by EF configuration and callers.</summary>
public static class AuditPolicy
{
    public const int ActionMaxLength = 200;
    public const int ResourceTypeMaxLength = 100;
    public const int ResourceIdMaxLength = 100;

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
