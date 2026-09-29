using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Facade;

/// <summary>
/// Admits a run against an account's AI allowance, and settles what it spent. The only way into this module's
/// quota from anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A sink, like the ledger beside it.</strong> It is told which account to charge and never looks one
/// up: it reads no AI table, no recipe table and no membership table. That is stronger than the boundary rules
/// require, and it is what lets an unfiltered quota be reasoned about on its own.
/// </para>
/// <para>
/// <strong>A model never reaches this.</strong> Admission is deterministic domain code over stored balances;
/// ai.md puts quota enforcement among the things a model may never perform, and nothing here is reachable from
/// a Semantic Kernel plugin or expressible as a structured output.
/// </para>
/// </remarks>
public interface IAiQuotaAdmissionFacade
{
    /// <summary>
    /// Decides whether a run may call a provider, holding its estimate against the account's period when it
    /// may. One concurrency-safe transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Call it before the provider, not after.</strong> USAGE-006 refuses an over-quota account before
    /// the operation runs and before any provider call, and an admission taken afterwards would be a report
    /// rather than a control.
    /// </para>
    /// <para>
    /// <strong>Admitted means the caller owes a settlement.</strong> A hold nobody settles strands allowance
    /// until its lease lapses; the sweep is the backstop, not the plan.
    /// </para>
    /// <para>
    /// Replay-safe by the claim: the same operation and lease token find the hold they already took rather
    /// than taking a second.
    /// </para>
    /// </remarks>
    Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        AiQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken);

    /// <summary>
    /// Whether an account could run this task right now, and what it has left — without holding anything.
    /// </summary>
    /// <param name="accountId">
    /// From <c>IWorkspaceContext.AccountId</c>. Never a request field, and never anything a model supplied.
    /// </param>
    /// <param name="taskType">Which capability, so "what is left" can be weighed against what it would take.</param>
    /// <param name="isMeterable">
    /// Whether the task can cost anything at all. The caller's decision, as on
    /// <see cref="AiQuotaAdmissionRequestServiceModel.IsMeterable"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>This refuses; <see cref="AdmitAsync"/> decides.</strong> USAGE-006 wants an over-quota request
    /// turned away at the moment it is made, rather than accepted, queued, and failed twenty minutes later — so
    /// this exists to answer at the edge. It writes nothing whatsoever, including no period, because a refusal
    /// that spent something to say "you may not spend" would be its own contradiction.
    /// </para>
    /// <para>
    /// <strong>It is a snapshot and cannot be treated as a reservation.</strong> Another run can take the
    /// balance between this answer and the work starting; the authoritative check is the hold
    /// <see cref="AdmitAsync"/> takes in one concurrency-safe transaction before the provider is called.
    /// Skipping that on the strength of this would be a double-spend waiting for two tabs.
    /// </para>
    /// </remarks>
    Task<AiQuotaStateServiceModel> PeekAsync(
        string accountId, AiTaskType taskType, bool isMeterable, CancellationToken cancellationToken);

    /// <summary>
    /// Records what an admitted run actually spent, on the caller's unit of work. Stages; does not save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Stages, and the name says so</strong> — for the reason
    /// <see cref="IAiUsageRecordingFacade.StageAttemptsAsync"/> gives, and it is the same reason: this has to
    /// commit with the attempt record and the ledger entry it describes, or a settled attempt whose reservation
    /// was never settled becomes reachable. Two transactions would make that state one failure away.
    /// </para>
    /// <para>
    /// <strong>It does not touch the period.</strong> Applying the charge is a second transaction
    /// (<see cref="PostAsync"/>), because the period's row version makes every write to it contend and a
    /// concurrent settlement for the same account must not be able to roll back a generated proposal. This row
    /// carries the charge across that gap.
    /// </para>
    /// <para>
    /// <strong>Idempotent.</strong> A replayed settlement finds a hold that is no longer held and writes
    /// nothing, so a redelivery settles once (USAGE-004).
    /// </para>
    /// </remarks>
    Task StageSettlementAsync(
        Guid reservationId,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a settled hold to its period: gives the estimate back and lands the charge.
    /// </summary>
    /// <remarks>
    /// Call it after the settlement has committed. Safe to retry, safe to call twice, and safe to lose — the
    /// maintenance sweep finds anything a worker did not live long enough to finish.
    /// </remarks>
    Task PostAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Extends a hold so it lapses with the lease the run is actually holding rather than before it.
    /// </summary>
    /// <remarks>
    /// Called wherever the caller renews that lease. A hold that expired under a live worker would release an
    /// estimate the run was still spending against, and the settlement would then land against a balance that
    /// had already been handed to someone else.
    /// </remarks>
    Task RenewAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiQuotaAdmissionFacade"/>
internal sealed class AiQuotaAdmissionFacade(IAiQuotaAdmissionBusiness business) : IAiQuotaAdmissionFacade
{
    public Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        AiQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return business.AdmitAsync(request, cancellationToken);
    }

    public Task<AiQuotaStateServiceModel> PeekAsync(
        string accountId, AiTaskType taskType, bool isMeterable, CancellationToken cancellationToken) =>
        business.PeekAsync(accountId, taskType, isMeterable, cancellationToken);

    public Task StageSettlementAsync(
        Guid reservationId,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        return business.StageSettlementAsync(reservationId, attempts, cancellationToken);
    }

    public Task PostAsync(Guid reservationId, CancellationToken cancellationToken) =>
        business.PostAsync(reservationId, cancellationToken);

    public Task RenewAsync(
        Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        business.RenewAsync(reservationId, expiresAt, cancellationToken);
}
