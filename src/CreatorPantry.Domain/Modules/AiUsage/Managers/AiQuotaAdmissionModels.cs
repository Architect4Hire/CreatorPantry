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

    /// <summary>
    /// This deployment has no credit rate for the picture that was asked for (AP.2.4).
    /// </summary>
    /// <remarks>
    /// <strong>A refusal, never a free picture.</strong> Startup refuses a host that can request an unrated
    /// picture, so reaching this means a request for something no sizing profile offers. It is told apart from
    /// <see cref="Exhausted"/> because the account's allowance is not the problem and waiting for the period to
    /// reset would not help.
    /// </remarks>
    Unrated = 6,
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
    DateTimeOffset? PeriodEndsAt)
{
    /// <summary>
    /// What admitting this would have taken, whether or not it was admitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>On a refusal this is the figure USAGE-007 requires</strong> — a creator told only "your
    /// allowance is spent" cannot tell whether they are one picture over or twenty.
    /// <see cref="Reserved"/> cannot carry it, because that says what <em>was</em> held and a refusal held
    /// nothing; reporting the attempted amount there would make a refused request look like a successful hold
    /// to anything summing holds.
    /// </para>
    /// <para>
    /// Zero where nothing was computed: an unmeterable run, a suspension, or a period too contended to read.
    /// The completion path leaves it zero and refuses through <c>IAiRequestQuotaGate</c>, which gets the same
    /// figure from <see cref="AiQuotaStateServiceModel.Required"/>.
    /// </para>
    /// </remarks>
    public decimal Required { get; init; }
}

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

/// <summary>
/// Who is asking to make pictures, and for how many (AP.2.5). Every field is resolved server-side by the
/// caller before this type exists.
/// </summary>
/// <param name="AccountId">
/// The Identity account, from <c>IWorkspaceContext.AccountId</c> — never a request field, an unsigned header,
/// or anything a model supplied (USAGE-001).
/// </param>
/// <param name="GeneratedImageOperationId">The generation about to be queued.</param>
/// <param name="VariantCount">How many pictures the request asked for.</param>
/// <param name="ModelName">The image model that will be asked, which is what the rate is read against.</param>
/// <param name="Resolution">The resolution tier the request will carry, or null when the model takes none.</param>
/// <param name="Quality">The quality the request will carry, or null when the model offers none.</param>
/// <param name="ExpiresAt">
/// When the hold lapses. It has to cover every pass the operation may take rather than just the first: a
/// requeued generation is still holding this allowance, and a hold that lapsed between passes would leave the
/// next one generating against an allowance nobody had checked.
/// </param>
/// <remarks>
/// <para>
/// <strong>Admitted at the request, not at the worker</strong>, which is where this differs from
/// <see cref="AiQuotaAdmissionRequestServiceModel"/>. The image worker resolves its workspace as a service
/// identity so a picture finishes after its creator has left the workspace, which means the worker's ambient
/// account is not the creator's and cannot be charged. The request scope is the one place the account is
/// known, so the hold is taken there and settled later against its id alone.
/// </para>
/// <para>
/// <strong>There is no <c>IsMeterable</c>.</strong> Every picture costs money; a request that cannot be priced
/// is refused rather than admitted free (PRICE-001).
/// </para>
/// </remarks>
public sealed record AiImageQuotaAdmissionRequestServiceModel(
    string AccountId,
    Guid GeneratedImageOperationId,
    int VariantCount,
    string ModelName,
    string? Resolution,
    string? Quality,
    DateTimeOffset ExpiresAt);

/// <summary>
/// What one image generation's charges were, for the ledger and the settlement together (AP.2.5).
/// </summary>
/// <param name="ReservationId">The hold admission took, which is also where the account is read from.</param>
/// <param name="WorkspaceId">Where the pictures were made, as a reporting dimension only. Never an owner.</param>
/// <param name="GeneratedImageOperationId">The generation these charges belong to.</param>
/// <param name="StagedVariants">
/// How many variants the provider generated and this workspace staged, cumulative over every pass.
/// </param>
/// <param name="UnusableVariants">
/// How many variants the provider generated and billed but nothing could be staged from — bytes that did not
/// hold together, a picture too large, or one the provider flagged.
/// </param>
/// <param name="FlaggedVariants">
/// How many of <paramref name="UnusableVariants"/> the provider withheld on its own safety grounds, so the
/// ledger can say <see cref="AiUsageOutcome.SafetyBlocked"/> rather than a bare failure.
/// </param>
/// <param name="OccurredAt">When the generation finished. The instant its period is assigned from.</param>
/// <param name="ProviderName">The provider that billed for them.</param>
/// <param name="ModelName">The model it ran.</param>
/// <param name="ModelDeployment">The deployment that served it, when the provider distinguishes one.</param>
/// <remarks>
/// <para>
/// <strong>Counts, not per-variant rows, and that is the whole point.</strong> A variant the provider generated
/// but whose bytes could not be staged leaves no record of its own — there is nothing to write a
/// <c>GeneratedImage</c> row from — so the only durable account of what was billed is a count carried on the
/// operation. One model serves one operation, so provider, model and deployment are the operation's own and
/// the same for every charge in it.
/// </para>
/// <para>
/// <strong>It names no account.</strong> The Media module never learns one: this module reads it from the
/// reservation, which took it from the request scope where it was known. A ledger entry and the hold it settles
/// therefore cannot disagree about who paid.
/// </para>
/// </remarks>
public sealed record AiImageUsageServiceModel(
    Guid ReservationId,
    Guid WorkspaceId,
    Guid GeneratedImageOperationId,
    int StagedVariants,
    int UnusableVariants,
    int FlaggedVariants,
    DateTimeOffset OccurredAt,
    string ProviderName,
    string ModelName,
    string? ModelDeployment)
{
    /// <summary>Every variant the provider generated and billed for, whatever became of it.</summary>
    public int BilledVariants => StagedVariants + UnusableVariants;
}

/// <summary>Whether an image request could be admitted right now (AP.2.6).</summary>
public enum AiImageQuoteOutcome
{
    Unspecified = 0,

    /// <summary>What is left covers the whole request.</summary>
    Affordable = 1,

    /// <summary>What is left does not cover it; <see cref="AiImageQuoteServiceModel.AffordableVariants"/> says how many it would.</summary>
    Exhausted = 2,

    /// <summary>AI access is switched off, whatever is left.</summary>
    Suspended = 3,

    /// <summary>This host has no rate for the picture it would make, so it cannot be priced or admitted.</summary>
    Unpriced = 4,
}

/// <summary>
/// What an image request would cost and what it would leave, read without holding or writing anything (AP.2.6).
/// </summary>
/// <param name="Outcome">Whether the request could be admitted right now.</param>
/// <param name="Unit">What every amount here is counted in.</param>
/// <param name="Variants">How many pictures were asked about.</param>
/// <param name="PerPicture">One picture's price, rounded as a hold is; null when it cannot be priced.</param>
/// <param name="Total">The whole request's price — exactly the hold admission would take; null when unpriced.</param>
/// <param name="Allowance">The period's spendable total.</param>
/// <param name="Remaining">What is left of it now, after settled and outstanding holds.</param>
/// <param name="RemainingAfter">What would be left after this request; null unless it is affordable.</param>
/// <param name="AffordableVariants">How many of the asked-for pictures what is left would cover now, by the same test admission applies.</param>
/// <param name="LeavesNearlySpent">Whether going ahead leaves the allowance at or below its nearly-spent line (USAGE-010).</param>
/// <param name="ResetsAt">When the allowance comes back. Null for a suspension, which does not come back by itself.</param>
/// <param name="TimeZoneId">The zone <paramref name="ResetsAt"/> is a local midnight in.</param>
/// <remarks>
/// <strong>A snapshot, not a reservation</strong>, exactly as <c>PeekAsync</c> is: another tab can spend the
/// balance between this answer and the request. Admission decides; this only lets a creator see the price first.
/// Every figure is computed here so a surface renders it and never works one out (PRICE-004).
/// </remarks>
public sealed record AiImageQuoteServiceModel(
    AiImageQuoteOutcome Outcome,
    AiQuotaUnit Unit,
    int Variants,
    decimal? PerPicture,
    decimal? Total,
    decimal Allowance,
    decimal Remaining,
    decimal? RemainingAfter,
    int AffordableVariants,
    bool LeavesNearlySpent,
    DateTimeOffset? ResetsAt,
    string? TimeZoneId);
