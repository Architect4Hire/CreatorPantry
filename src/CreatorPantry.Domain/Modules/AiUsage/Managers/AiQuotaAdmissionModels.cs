using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Who is asking for allowance, and for what. Every field is resolved server-side by the caller before this
/// type exists.
/// </summary>
/// <param name="AccountId">
/// The Identity account, from <c>IWorkspaceContext.AccountId</c> — never a request field, an unsigned header,
/// or anything a model supplied (USAGE-001).
/// </param>
/// <param name="AiOperationId">The operation about to run.</param>
/// <param name="LeaseToken">
/// The claim it is running under. One claim gets one reservation, so a replayed admission finds the hold it
/// already took rather than taking a second.
/// </param>
/// <param name="TaskType">Which capability, which is what the estimate is read against.</param>
/// <param name="IsMeterable">
/// Whether this run is capable of costing anything at all.
/// </param>
/// <param name="ExpiresAt">
/// When the caller's lease lapses. The hold is released with it, so this must track the lease the run is
/// actually holding rather than a duration this module invents.
/// </param>
/// <remarks>
/// <paramref name="IsMeterable"/> is the caller's decision and not this module's, exactly as
/// <see cref="AiUsageAttemptServiceModel.IsBillable"/> is: the AI module knows that a diagnostic task never
/// reaches a provider, and duplicating that knowledge here would give the platform two answers to one question.
/// A run declared unmeterable is admitted without a period being read at all — it cannot spend allowance, so
/// queueing it behind one would be a refusal nothing earned.
/// </remarks>
public sealed record AiQuotaAdmissionRequestServiceModel(
    string AccountId,
    Guid AiOperationId,
    Guid LeaseToken,
    AiTaskType TaskType,
    bool IsMeterable,
    DateTimeOffset ExpiresAt);

/// <summary>Whether a run may call a provider, and why not when it may not.</summary>
public enum AiQuotaAdmissionOutcome
{
    /// <summary>Allowance was held. The run may proceed and must settle.</summary>
    Admitted = 1,

    /// <summary>
    /// The run cannot cost anything, so nothing was held and nothing needs settling.
    /// </summary>
    NotMetered = 2,

    /// <summary>The period's allowance would not cover the estimate. Nothing was written.</summary>
    Exhausted = 3,

    /// <summary>AI access is switched off for this account regardless of any remaining allowance.</summary>
    /// <remarks>
    /// Distinct from <see cref="Exhausted"/> because USAGE-007 requires a refusal to say what is exhausted and
    /// when it resets, and a suspension has no reset time to offer.
    /// </remarks>
    Suspended = 4,

    /// <summary>
    /// Competing writers kept the period moving under this admission until its retries ran out.
    /// </summary>
    /// <remarks>
    /// Not a refusal and not a failure: nothing is known about whether the account had the allowance. The
    /// caller leaves its lease to expire so the operation is requeued with backoff, rather than failing a run
    /// that a moment later would have been admitted.
    /// </remarks>
    Busy = 5,
}

/// <summary>What an admission decided, in the only terms a caller needs.</summary>
/// <param name="Outcome">Whether the run may proceed.</param>
/// <param name="ReservationId">
/// The hold to settle against, on <see cref="AiQuotaAdmissionOutcome.Admitted"/> and never otherwise.
/// </param>
/// <param name="Reserved">What was held, in <paramref name="Unit"/>. Zero when nothing was.</param>
/// <param name="Remaining">
/// What is left of the period after this hold: allowance plus carry-over, less everything settled and
/// everything outstanding. Zero when no period was read.
/// </param>
/// <param name="Unit">What the two amounts are denominated in.</param>
/// <param name="PeriodEndsAt">
/// When the period resets, which is what a refusal has to be able to tell a creator (USAGE-007). Null when no
/// period was read, and on <see cref="AiQuotaAdmissionOutcome.Suspended"/>, where there is nothing to wait for.
/// </param>
public sealed record AiQuotaAdmissionServiceModel(
    AiQuotaAdmissionOutcome Outcome,
    Guid? ReservationId,
    decimal Reserved,
    decimal Remaining,
    AiQuotaUnit Unit,
    DateTimeOffset? PeriodEndsAt);

/// <summary>Whether an account may spend right now, without anything being held to find out.</summary>
public enum AiQuotaStateOutcome
{
    /// <summary>The period covers what this task would take.</summary>
    Available = 1,

    /// <summary>What is left would not cover it.</summary>
    Exhausted = 2,

    /// <summary>AI access is switched off, whatever is left.</summary>
    Suspended = 3,
}

/// <summary>
/// What an account's allowance looks like at this instant, read without writing anything.
/// </summary>
/// <param name="Outcome">Whether a run of the asked-about task could be admitted right now.</param>
/// <param name="Unit">What the three amounts below are denominated in.</param>
/// <param name="Allowance">The period's spendable total: its allowance plus anything carried into it.</param>
/// <param name="Remaining">What is left of that after everything settled and everything outstanding.</param>
/// <param name="Required">
/// What admitting the asked-about task would hold. Zero for a task that cannot spend anything.
/// </param>
/// <param name="ResetsAt">
/// When the period ends and the allowance comes back. Null on <see cref="AiQuotaStateOutcome.Suspended"/>,
/// where there is nothing to wait for (USAGE-007).
/// </param>
/// <remarks>
/// <para>
/// <strong>Every figure belongs to the account that asked.</strong> Nothing here is another account's usage and
/// nothing names a workspace, which is what lets this be handed straight to a refused caller.
/// </para>
/// <para>
/// <strong>A snapshot, not a promise.</strong> Another run of the same account can spend the balance between
/// this read and the admission that acts on it. The authoritative decision is
/// <see cref="Facade.IAiQuotaAdmissionFacade.AdmitAsync"/>, which takes a hold in one concurrency-safe
/// transaction; this exists so a creator is refused at the moment they ask rather than after waiting in a queue
/// for it (USAGE-006).
/// </para>
/// </remarks>
public sealed record AiQuotaStateServiceModel(
    AiQuotaStateOutcome Outcome,
    AiQuotaUnit Unit,
    decimal Allowance,
    decimal Remaining,
    decimal Required,
    DateTimeOffset? ResetsAt);

/// <summary>What one maintenance sweep over outstanding holds did.</summary>
/// <param name="Posted">Settlements applied to a period that a worker did not live long enough to apply.</param>
/// <param name="Expired">Holds released because their lease lapsed with nothing settled against them.</param>
/// <param name="Finalized">Ended periods whose totals can no longer move.</param>
public sealed record AiQuotaMaintenanceSummary(int Posted, int Expired, int Finalized);
