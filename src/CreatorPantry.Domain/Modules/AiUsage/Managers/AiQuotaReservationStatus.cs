namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// What became of one admitted run's hold on an account's allowance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This says why, not where the arithmetic got to.</strong> Whether the hold has been given back, and
/// whether the charge has landed, are two separate timestamps on the row
/// (<c>AccountAiQuotaReservation.ReleasedAt</c> and <c>PostedAt</c>), because they are applied to the period in
/// a different transaction from the one that decides them and either may arrive first. A status that also tried
/// to mean "posted" would have to be written twice for one outcome.
/// </para>
/// </remarks>
public enum AiQuotaReservationStatus
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>
    /// Admitted. The estimate is counted against the period and the run may call a provider.
    /// </summary>
    Held = 1,

    /// <summary>
    /// The run finished and reported what it used. <c>SettledAmount</c> is the charge.
    /// </summary>
    /// <remarks>
    /// Reached whether or not the provider reported token counts — <c>UsageReported</c> is what distinguishes
    /// a measured charge from one settled at the estimate, exactly as it does on the usage ledger. Both are
    /// settlements; only one of them is a measurement.
    /// </remarks>
    Settled = 2,

    /// <summary>
    /// The run finished having spent nothing billable, so the whole hold goes back.
    /// </summary>
    /// <remarks>
    /// A known zero, and deliberately not the same as <see cref="Expired"/>: a worker got here and said the
    /// provider was never reached. USAGE-005's reasoning about the ledger applies to the allowance too —
    /// "nothing was spent" and "nobody came back to say" must not be the same row.
    /// </remarks>
    Released = 3,

    /// <summary>
    /// The lease lapsed with the hold still outstanding, so it was released without ever learning the cost.
    /// </summary>
    /// <remarks>
    /// An unknown, not a zero. A settlement arriving afterwards is still accepted and still posts its charge —
    /// the tokens were spent whatever this row had given up on — and <c>ExpiresAt &lt; SettledAt</c> is what
    /// records that it arrived late, which is why that case needs no column of its own.
    /// </remarks>
    Expired = 4,
}
