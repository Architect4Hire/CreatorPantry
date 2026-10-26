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
    /// Decides whether an account may be queued a picture, holding the exact price of the whole request
    /// against its period when it may (AP.2.5). One concurrency-safe transaction. Stages; does not save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Called at the request, where <see cref="AdmitAsync"/> is called at the worker</strong>, and the
    /// difference is not a preference. The image worker resolves its workspace as a service identity so that a
    /// picture a creator paid for still finishes after they have left the workspace — which means the worker's
    /// ambient account is not the creator's and must never be charged. The request scope is the only place the
    /// account is known, so the hold is taken there and settled later against its id alone.
    /// </para>
    /// <para>
    /// <strong>It commits its own hold, and the caller owes a release if its operation then fails to
    /// write.</strong> It cannot do otherwise: the period's row version is what stops two admissions for one
    /// account both succeeding, and a concurrency check needs a transaction of its own. So the hold exists
    /// before the operation row does, and the two cannot be made atomic — what makes them consistent is
    /// <see cref="ReleaseImageAsync"/> on the failure path, with the expiry sweep behind it, which is the same
    /// compensating-write shape the Media module already uses for an object it stored and then could not
    /// name. A hold nobody releases strands that allowance until it lapses.
    /// </para>
    /// <para>
    /// <strong>Admitted means the caller owes a settlement</strong> — one, however many passes the generation
    /// takes. Replay-safe by the generation: a second admission for the same operation finds the hold it
    /// already took.
    /// </para>
    /// </remarks>
    Task<AiQuotaAdmissionServiceModel> AdmitImageAsync(
        AiImageQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken);

    /// <summary>
    /// Records what one image generation was billed for: its ledger entries and the settlement of its hold,
    /// together, on the caller's unit of work (AP.2.5). Stages; does not save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One call rather than two, because the account is only knowable here.</strong> The ledger needs
    /// the account that paid and the Media module never learns one; this reads it from the reservation, so an
    /// entry and the hold it settles cannot disagree about who paid. It is also the same reason the AI module
    /// stages its three writes through one helper: with them apart, a settled generation that posts no usage
    /// is one forgotten line away, in the failure path nobody demonstrates.
    /// </para>
    /// <para>
    /// <strong>Call it once, when the generation is finished for good.</strong> A requeued pass has not
    /// finished: its hold stands, renewed with the lease, and the counts it contributed stay on the operation
    /// until a terminal pass settles them all at once. That is what makes a resumed generation charge each
    /// variant once.
    /// </para>
    /// <para>
    /// Idempotent in both halves: a hold that is no longer held settles nothing, and a charge already in the
    /// ledger is skipped rather than posted again.
    /// </para>
    /// </remarks>
    Task StageImageUsageAsync(AiImageUsageServiceModel usage, CancellationToken cancellationToken);

    /// <summary>
    /// Whether an image generation's hold is still live, so the work it paid for may still be done (AP.2.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Asked before any picture is made, because a lapsed hold means nobody is paying.</strong> The
    /// hold is taken when the request is made and is not renewed, so a generation that waited a long time in
    /// the queue — behind its workspace's running cap, say — can be claimed after the expiry sweep has already
    /// given its allowance back. Settlement would then charge nothing, and the pictures would have been free:
    /// the one thing PRICE-001 exists to prevent.
    /// </para>
    /// <para>
    /// False for a hold that lapsed, was released, or was already settled. A generation with no hold at all is
    /// not this question — the caller knows it has none and does not ask.
    /// </para>
    /// </remarks>
    Task<bool> IsImageHoldLiveAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Gives back a hold whose generation was never queued (AP.2.5). Commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compensation for <see cref="AdmitImageAsync"/>'s hold existing before the operation row. Two
    /// requests sent with one idempotency key is the ordinary way to get here — a creator's double click, one
    /// of which loses the unique index — and the loser's hold would otherwise show the account as having spent
    /// allowance on a generation that does not exist, until it lapsed.
    /// </para>
    /// <para>
    /// Idempotent, and safe to lose: it is the same write the expiry sweep makes, so the worst case of a
    /// release that never ran is the delay this exists to avoid rather than a wrong balance. A hold with
    /// anything settled against it is left alone.
    /// </para>
    /// </remarks>
    Task ReleaseImageAsync(Guid reservationId, CancellationToken cancellationToken);

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
internal sealed class AiQuotaAdmissionFacade(
    IAiQuotaAdmissionBusiness business, IAiUsageRecordingBusiness recording) : IAiQuotaAdmissionFacade
{
    public Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        AiQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return business.AdmitAsync(request, cancellationToken);
    }

    public Task<AiQuotaAdmissionServiceModel> AdmitImageAsync(
        AiImageQuotaAdmissionRequestServiceModel request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return business.AdmitImageAsync(request, cancellationToken);
    }

    /// <remarks>
    /// The settlement first, because it is what yields the account the ledger entries are attributed to, and a
    /// hold that was already settled yields none — in which case its charges are already posted too. Both
    /// stage on the caller's unit of work, so they commit together or neither does.
    /// </remarks>
    public async Task StageImageUsageAsync(AiImageUsageServiceModel usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (await business.StageImageSettlementAsync(usage, cancellationToken) is { } accountId)
        {
            await recording.StageImageChargesAsync(accountId, usage, cancellationToken);
        }
    }

    public Task<bool> IsImageHoldLiveAsync(Guid reservationId, CancellationToken cancellationToken) =>
        business.IsImageHoldLiveAsync(reservationId, cancellationToken);

    public Task ReleaseImageAsync(Guid reservationId, CancellationToken cancellationToken) =>
        business.ReleaseImageAsync(reservationId, cancellationToken);

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
