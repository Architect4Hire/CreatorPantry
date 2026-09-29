using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

/// <summary>
/// One admitted run's hold on an account's allowance, from admission through to the charge landing on the
/// period. Platform-scoped, counts only, and never filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the row <see cref="AccountAiQuotaPeriod.Reserved"/> said it could not be correct
/// without.</strong> That column is a balance; releasing an abandoned hold and settling exactly once on a
/// replay both need something with its own identity to move the number against, and this is it. The two
/// counters on the period are, exactly:
/// </para>
/// <list type="bullet">
/// <item><description><c>Reserved</c> = the sum of <see cref="ReservedAmount"/> where <see cref="ReleasedAt"/> is null.</description></item>
/// <item><description><c>Consumed</c> = the sum of <see cref="SettledAmount"/> where <see cref="PostedAt"/> is not null.</description></item>
/// </list>
/// <para>
/// Those are invariants a reconciliation pass can assert rather than descriptions of what the code happens to
/// do, and they are why the two timestamps exist separately from <see cref="Status"/>.
/// </para>
/// <para>
/// <strong>Deciding a settlement and applying it are deliberately different transactions.</strong> The decision
/// commits with the attempt record and the ledger entry it describes, so a settled attempt whose reservation
/// was never settled is unreachable. Applying it touches <see cref="AccountAiQuotaPeriod"/>, whose
/// <c>RowVersion</c> makes every write to it contend — and folding that into the attempt's transaction would
/// mean a concurrent settlement for the same account could roll back a generated proposal. Losing creator work
/// to protect a counter is the wrong trade, so this row carries the charge across the gap and doubles as the
/// durable job record backend.md asks for when a side effect must follow a committed transaction. Nothing is
/// lost if the process dies in between: the maintenance sweep finishes it.
/// </para>
/// <para>
/// <strong>Counts and outcome only.</strong> Like the usage ledger beside it, this table has no free-text
/// column and no creator content of any kind, which is what lets it live outside the workspace filter. A
/// content-bearing column here would make the missing filter indefensible.
/// </para>
/// <para>
/// No <c>RowVersion</c> of its own, unlike the period. Every write that moves one of this row's counters moves
/// the period's in the same transaction, so the period's token already serializes them; the loser re-reads and
/// finds the timestamp set. The one write that does not touch the period — settlement — happens under the
/// operation's lease, which admits one writer by construction.
/// </para>
/// </remarks>
public class AccountAiQuotaReservation
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Identity account whose allowance this holds.
    /// </summary>
    /// <remarks>
    /// Resolved server-side from the operation's own requester and handed to this module, never looked up here
    /// and never taken from a request field, a header, or anything a model supplied (USAGE-001, ai.md).
    /// </remarks>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The period this spends from, fixed at admission.</summary>
    /// <remarks>
    /// A real foreign key, unlike every other reference in this module. A reservation without its period is not
    /// a degraded record but an uninterpretable one — the amounts are denominated in that period's frozen unit
    /// and counted in its totals — and periods are never deleted, so nothing is being kept alive by it.
    /// Restrict rather than cascade: a period with outstanding holds is not a period anything may remove.
    /// </remarks>
    public Guid PeriodId { get; set; }

    /// <inheritdoc cref="PeriodId"/>
    public AccountAiQuotaPeriod? Period { get; set; }

    /// <summary>The operation this was admitted for. An id, not a foreign key.</summary>
    /// <remarks>
    /// The reasoning <c>AccountAiUsageEntry</c> records: an operation is workspace-owned creator property and
    /// deleting a workspace does not unspend an account's allowance. Validated by the caller, not enforced here.
    /// </remarks>
    public Guid AiOperationId { get; set; }

    /// <summary>
    /// The claim this was admitted under.
    /// </summary>
    /// <remarks>
    /// Part of the identity rather than a detail, because an operation may legitimately be admitted more than
    /// once: a lease that lapses returns the operation to the queue, and the next worker to claim it is making
    /// a fresh request for allowance that the abandoned hold must not pay for. One claim, one reservation, held
    /// by a unique index on the pair.
    /// </remarks>
    public Guid LeaseToken { get; set; }

    /// <summary>Which capability was admitted, so estimates can be read back against outcomes.</summary>
    /// <remarks>
    /// The AI module's own enum rather than a copy, on the same footing as
    /// <see cref="AccountAiUsageEntry.TaskType"/>: a second vocabulary would drift the first time a capability
    /// was added, and this one has to line up with the ledger it will be compared against.
    /// </remarks>
    public AiTaskType TaskType { get; set; }

    /// <inheritdoc cref="AccountAiQuotaPeriod.Unit"/>
    /// <remarks>Copied from the period at admission, so the amounts below are readable without joining to it.</remarks>
    public AiQuotaUnit Unit { get; set; }

    /// <summary>
    /// What admission held against the period: the estimate for a whole run, in <see cref="Unit"/>.
    /// </summary>
    /// <remarks>
    /// A run may make more than one provider call — a schema correction is the ordinary case — so this is
    /// <see cref="PerAttemptEstimate"/> times the number of calls a run is permitted. Admitting for one call
    /// while permitting several is admitting past the allowance by construction.
    /// </remarks>
    public decimal ReservedAmount { get; set; }

    /// <summary>
    /// What one provider call of this task was estimated at, in <see cref="Unit"/>.
    /// </summary>
    /// <remarks>
    /// Recorded rather than re-derived because it is what a billable attempt is charged when the provider
    /// reports no token counts. Re-reading it from configuration at settlement would let a price change between
    /// admission and settlement charge a run at a rate it was never admitted under.
    /// </remarks>
    public decimal PerAttemptEstimate { get; set; }

    /// <summary>
    /// The final charge, in <see cref="Unit"/>. Null while the run has not finished.
    /// </summary>
    /// <remarks>
    /// The sum over the run's billable attempts of what each one is charged: its converted token counts when
    /// the provider reported them, and <see cref="PerAttemptEstimate"/> when it did not. Zero when nothing
    /// billable happened.
    /// </remarks>
    public decimal? SettledAmount { get; set; }

    /// <summary>
    /// Whether every billable attempt in this run reported its token counts.
    /// </summary>
    /// <remarks>
    /// False means part of <see cref="SettledAmount"/> is estimate rather than measurement. Not redundant with
    /// a zero charge and not inferable from one: a run can be charged the estimate and a run can be charged a
    /// measured amount that happens to be equal. USAGE-005 keeps "we do not know" visible on the ledger for the
    /// same reason it has to be visible here.
    /// </remarks>
    public bool UsageReported { get; set; }

    public AiQuotaReservationStatus Status { get; set; }

    public DateTimeOffset HeldAt { get; set; }

    /// <summary>
    /// When the hold lapses if nothing has settled it, tracking the operation's own lease.
    /// </summary>
    /// <remarks>
    /// Copied from the caller at admission and extended whenever the caller extends the lease, so the two have
    /// one lifetime. A hold that outlived its worker would strand allowance an account could otherwise spend;
    /// one that expired under a live worker would release an estimate the run was still spending against.
    /// </remarks>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the run's outcome was decided. Null while <see cref="Status"/> is Held.</summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>
    /// When <see cref="ReservedAmount"/> was given back to the period. Null while it is still held.
    /// </summary>
    /// <remarks>
    /// Written by whichever of settlement-posting or lease expiry reaches the period first, and exactly once —
    /// which is what makes both of them safe to run, retry and race.
    /// </remarks>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>
    /// When <see cref="SettledAmount"/> was added to the period. Null until it has been.
    /// </summary>
    public DateTimeOffset? PostedAt { get; set; }
}
