namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What the account-usage reconciliation may repair, and how far back it looks for what it may only report.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Before"/> is what separates repairing from reporting, and it is an instant rather than a
/// switch on purpose.</strong> A flag would have let the backfill quietly repair drift in current traffic —
/// masking a defect in the recording path, which is the one thing the repeatable check exists to surface. A
/// cutoff makes that unreachable: nothing at or after it is ever posted by this seam, and a value that is not
/// in the past is refused rather than clamped — so the separation cannot be configured away by accident, which
/// a clamp would have allowed silently.
/// </para>
/// <para>
/// Nothing here changes a quota period. Backfilled usage is history, not spend.
/// </para>
/// </remarks>
public sealed class AiUsageReconciliationOptions
{
    public const string SectionName = "Ai:Usage:Reconciliation";

    /// <summary>
    /// Attempts completed before this instant may be posted to the ledger. Null means no backfill runs.
    /// </summary>
    /// <remarks>
    /// Set to the deployment instant at which per-attempt recording began, and unset once the backfill has
    /// drained — leaving it set is harmless but keeps a scan alive for rows that no longer exist. A value at
    /// or after "now" is refused: see this type's remarks.
    /// </remarks>
    public DateTimeOffset? Before { get; set; }

    /// <summary>
    /// How far back the repeatable check looks for attempts nobody posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A trailing window rather than all history, so the check stays a range seek on a table that grows with
    /// every provider attempt forever. A defect in the recording path shows up within one pass; anything older
    /// than this belongs to <see cref="Before"/>'s range by definition.
    /// </para>
    /// <para>
    /// No grace period is subtracted, and none is needed: the live path commits the ledger entry in the same
    /// transaction as the execution record, so there is no instant at which one exists without the other.
    /// Anything this reports is a real gap rather than a race.
    /// </para>
    /// </remarks>
    public TimeSpan DriftWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How many attempts one pass reads.</summary>
    /// <remarks>
    /// Bounded so a backfill is drained over many passes rather than one long transaction holding rows against
    /// live traffic. Resumability costs nothing here: the query is its own bookmark.
    /// </remarks>
    public int BatchSize { get; set; } = 200;

    /// <summary>How often a pass runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);
}
