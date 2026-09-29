namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// The invariants of the admission and settlement seam that are the platform's decision rather than a
/// deployment's.
/// </summary>
/// <remarks>
/// Deliberately small, and deliberately separate from <see cref="AiQuotaOptions"/>: what is here cannot be
/// configured wrong, and what is there has no single right answer for every deployment.
/// </remarks>
public static class AiQuotaPolicy
{
    /// <summary>
    /// How many times a contended write to a period is retried before the caller is told the period is busy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A period carries a row version because two workers reserving for one account must not both read the
    /// same remaining balance and both be admitted (USAGE-004); the loser gets a conflict, and this is how
    /// many times it re-reads and tries again. The single-statement atomic decrement that would avoid the
    /// retry entirely is not available — <c>BulkOperationBoundaryTests</c> bans <c>ExecuteUpdate</c> in domain
    /// code, because it bypasses every save interceptor.
    /// </para>
    /// <para>
    /// Three, because the conflict window is one round trip and an account with enough concurrent runs to lose
    /// three in a row is better served by a requeue with backoff than by a fourth immediate attempt.
    /// </para>
    /// </remarks>
    public const int ContendedWriteAttempts = 3;

    /// <summary>The most holds one maintenance sweep releases or posts in a single pass.</summary>
    /// <remarks>
    /// Bounded so a backlog is worked through over several passes rather than in one transaction long enough
    /// to hold contended rows against live admissions. The same reasoning as the AI queue's claim batch.
    /// </remarks>
    public const int MaintenanceBatchSize = 100;
}
