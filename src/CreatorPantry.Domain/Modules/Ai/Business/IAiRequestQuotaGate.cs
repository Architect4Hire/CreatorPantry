using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

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
    Task<IdempotentOutcome<AiProposalStatusServiceModel>?> RefuseIfUnaffordableAsync(
        AiTaskType taskType, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiRequestQuotaGate"/>
internal sealed class AiRequestQuotaGate(
    IAiQuotaAdmissionFacade quota, IWorkspaceContext workspace) : IAiRequestQuotaGate
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>?> RefuseIfUnaffordableAsync(
        AiTaskType taskType, CancellationToken cancellationToken)
    {
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

            AiQuotaStateOutcome.Exhausted => new OperationError(
                AiProposalErrors.QuotaExhausted,
                "Your AI allowance for this period is spent. It comes back when the period resets.",
                new Dictionary<string, string[]>(),

                // What is exhausted, what remains, what this would have taken, and when it resets — the four
                // things USAGE-007 requires a refused caller to be told. Every one of them is this account's
                // own figure, and none of them names a workspace.
                new Dictionary<string, object?>
                {
                    ["unit"] = state.Unit.ToString(),
                    ["allowance"] = state.Allowance,
                    ["remaining"] = state.Remaining,
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
}
