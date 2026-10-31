using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Ai.Data;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Ai.Business;

/// <summary>
/// Refuses an AI request whose account cannot pay for it, before anything is queued (USAGE-006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One implementation, seven callers, and the compiler cannot make the eighth remember.</strong> Every
/// request seam calls this first, and a behavioural test per route is what proves it — there is no shared base
/// class to put it in, and a capability that forgot would queue work an account has no allowance for and only
/// find out at the worker, twenty minutes later, as a failed operation.
/// </para>
/// <para>
/// <strong>Deliberately not in <c>AiOperationDataLayer.RequestAsync</c></strong>, which genuinely is the single
/// funnel every route converges on. Shaping an HTTP refusal is a product decision, and backend.md puts those in
/// Business; a DataLayer that decided which creators get a 429 would be the wrong layer holding the rule, and
/// the next such rule would land there too.
/// </para>
/// <para>
/// <strong>This refuses. It does not admit.</strong> Nothing is held here — the hold is taken by the worker, in
/// one concurrency-safe transaction, immediately before the provider call. Two requests made at once can both
/// pass this and only one be admitted, which is correct: refusing at the edge is a courtesy to the creator, and
/// the balance is defended where it is actually spent.
/// </para>
/// </remarks>
internal interface IAiRequestQuotaGate
{
    /// <summary>
    /// The reply this request should be answered with, or <c>null</c> when the account can pay for it.
    /// </summary>
    /// <remarks>
    /// Returns the caller's own reply type rather than a bare error, so the eight call sites are one line each
    /// and "a refusal never replays" is stated once here instead of eight times in their words.
    /// </remarks>
    /// <param name="idempotencyKey">
    /// The request's key. One that has already bought an operation in this workspace is a replay, and a replay is
    /// never refused by the in-progress cap (15.3): its answer is the operation it bought.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>?> RefuseIfUnaffordableAsync(
        AiTaskType taskType, CancellationToken cancellationToken, string? idempotencyKey = null);

    /// <summary>
    /// The refusal a request for several runs of one task should be answered with, or <c>null</c> when the
    /// account can pay for all of them.
    /// </summary>
    /// <param name="runs">How many runs the request would queue.</param>
    /// <param name="requestedExtension">The name the refusal gives <paramref name="runs"/>.</param>
    /// <param name="coverableExtension">The name it gives the number the allowance could have covered.</param>
    /// <remarks>
    /// <para>
    /// All or none: a request that cannot be covered whole is refused whole, and told how much of it could
    /// have been. Nothing is held and nothing is written to find that out.
    /// </para>
    /// <para>
    /// A snapshot, exactly as the single-run form is. Each run still takes its own hold when it starts, and
    /// that hold — not this — is what stops an account spending what it does not have.
    /// </para>
    /// </remarks>
    Task<OperationError?> RefuseIfRunsUnaffordableAsync(
        AiTaskType taskType,
        int runs,
        string requestedExtension,
        string coverableExtension,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiRequestQuotaGate"/>
internal sealed class AiRequestQuotaGate(
    IAiQuotaAdmissionFacade quota,
    IAiOperationDataLayer operations,
    IOptions<LimitsOptions> limits,
    IWorkspaceContext workspace) : IAiRequestQuotaGate
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>?> RefuseIfUnaffordableAsync(
        AiTaskType taskType, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        if (!await operations.IsReplayAsync(idempotencyKey, cancellationToken)
            && await RefuseIfTooMuchQueuedAsync(runs: 1, cancellationToken) is { } queued)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(queued), Replayed: false);
        }

        // The account, from the resolved context -- never a request field, never an unsigned header, and
        // never anything a model supplied (USAGE-001, ai.md). A workspace role grants no exemption and cannot:
        // there is nothing here to read a role from, because an allowance belongs to the person rather than to
        // the workspace they happen to be working in.
        var state = await quota.PeekAsync(
            workspace.AccountId,
            taskType,
            taskType is not AiTaskType.Diagnostic,
            cancellationToken);

        var error = state.Outcome switch
        {
            AiQuotaStateOutcome.Suspended => new OperationError(
                AiProposalErrors.QuotaSuspended,
                "AI assistance is switched off for your account.",
                new Dictionary<string, string[]>(),

                // No balance and no reset. There is nothing to wait for, and a remaining figure here would
                // read as an invitation to try again (USAGE-007).
                new Dictionary<string, object?> { ["unit"] = state.Unit.ToString() }),

            AiQuotaStateOutcome.TrialEnded => TrialEnded(state),

            AiQuotaStateOutcome.Exhausted => new OperationError(
                AiProposalErrors.QuotaExhausted,
                "Your AI allowance for this period and your pack credits don't cover this. Your allowance comes back when the period resets.",
                new Dictionary<string, string[]>(),

                // What is exhausted, what remains, what this would have taken, and when it resets — the four
                // things USAGE-007 requires a refused caller to be told — with the allowance and the pack balance
                // as separate figures, since only one of them resets (AP.5.2). Every one of them is this
                // account's own figure, and none of them names a workspace.
                new Dictionary<string, object?>
                {
                    ["unit"] = state.Unit.ToString(),
                    ["allowance"] = state.Allowance,
                    ["remaining"] = state.Remaining,
                    ["packRemaining"] = state.PackRemaining,
                    ["required"] = state.Required,

                    // Read back as Retry-After by the HTTP layer, which is why it stays a DateTimeOffset here
                    // rather than being formatted into a string Business would have chosen the shape of.
                    ["resetsAt"] = state.ResetsAt,
                }),

            _ => null,
        };

        // A refusal never replays: nothing was created, so there is nothing to return again — and a replayed
        // flag on a 429 would tell a client it had already got this answer once, which it had not.
        return error is null
            ? null
            : new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(error), Replayed: false);
    }

    public async Task<OperationError?> RefuseIfRunsUnaffordableAsync(
        AiTaskType taskType,
        int runs,
        string requestedExtension,
        string coverableExtension,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);

        if (await RefuseIfTooMuchQueuedAsync(runs, cancellationToken) is { } queued)
        {
            return queued;
        }

        var state = await quota.PeekAsync(
            workspace.AccountId,
            taskType,
            taskType is not AiTaskType.Diagnostic,
            cancellationToken);

        if (state.Outcome is AiQuotaStateOutcome.Suspended)
        {
            return new OperationError(
                AiProposalErrors.QuotaSuspended,
                "AI assistance is switched off for your account.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["unit"] = state.Unit.ToString() });
        }

        if (state.Outcome is AiQuotaStateOutcome.TrialEnded)
        {
            return TrialEnded(state);
        }

        // A task that costs nothing, or an account nothing is metered for, can run as often as it likes.
        if (state.Required <= 0 && state.Outcome is not AiQuotaStateOutcome.Exhausted)
        {
            return null;
        }

        // Whole runs only, and never fewer than none: what is left of the period and then of the pack balance, in
        // units of one run. Each run's hold spends the allowance first and grants after (AP.5.2), so together they
        // cover as many whole runs as their sum does.
        var coverable = state.Outcome is AiQuotaStateOutcome.Exhausted || state.Required <= 0
            ? 0
            : (int)Math.Min(runs, Math.Max(0, Math.Floor((state.Remaining + state.PackRemaining) / state.Required)));

        return coverable >= runs
            ? null
            : new OperationError(
                AiProposalErrors.QuotaExhausted,
                "Your AI allowance for this period and your pack credits don't cover all of this. Nothing was started.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?>
                {
                    ["unit"] = state.Unit.ToString(),
                    ["allowance"] = state.Allowance,
                    ["remaining"] = state.Remaining,
                    ["packRemaining"] = state.PackRemaining,
                    ["required"] = state.Required * runs,
                    ["resetsAt"] = state.ResetsAt,
                    [requestedExtension] = runs,
                    [coverableExtension] = coverable,
                });
    }

    /// <summary>
    /// The in-progress caps (15.3): what this member, and this workspace, already have queued or running, plus what
    /// this request would add. Asked before the allowance, which is the more expensive question, and before anything
    /// is written.
    /// </summary>
    /// <remarks>
    /// Counted inside the resolved workspace, so the figures are this workspace's and this member's own. Two requests
    /// racing for the last place can both be admitted: that is the price of not locking every AI request, and the
    /// worker's per-workspace share is what keeps an overshoot of one from crowding anyone else out.
    /// </remarks>
    private async Task<OperationError?> RefuseIfTooMuchQueuedAsync(int runs, CancellationToken cancellationToken)
    {
        var options = limits.Value;

        if (!options.Enabled)
        {
            return null;
        }

        var (inWorkspace, byMember) = await operations.CountInFlightAsync(workspace.MembershipId, cancellationToken);

        if (byMember + runs > options.AiInFlightPerMember)
        {
            LimitsTelemetry.Refused(LimitedOperation.AiRequest, "concurrency", LimitScope.User);
            return LimitRefusal.Concurrency(LimitedOperation.AiRequest, LimitScope.User, options.AiInFlightPerMember);
        }

        if (inWorkspace + runs > options.AiInFlightPerWorkspace)
        {
            LimitsTelemetry.Refused(LimitedOperation.AiRequest, "concurrency", LimitScope.Workspace);
            return LimitRefusal.Concurrency(
                LimitedOperation.AiRequest, LimitScope.Workspace, options.AiInFlightPerWorkspace);
        }

        LimitsTelemetry.Admitted(LimitedOperation.AiRequest, "concurrency", LimitScope.Workspace);

        return null;
    }

    /// <summary>
    /// The refusal for an account whose trial has ended while it is still on the trial (AP.7.1): no balance and no
    /// reset, as for a suspension, because choosing a plan is the remedy and waiting is not.
    /// </summary>
    private static OperationError TrialEnded(AiQuotaStateServiceModel state) => new(
        AiProposalErrors.QuotaTrialEnded,
        "Your free trial has ended. Choose a plan to keep using AI assistance.",
        new Dictionary<string, string[]>(),
        new Dictionary<string, object?>
        {
            ["unit"] = state.Unit.ToString(),
            ["trialEndedAt"] = state.TrialEndedAt,
        });
}
