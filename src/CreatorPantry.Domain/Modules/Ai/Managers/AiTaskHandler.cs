using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Everything a handler needs to run one claimed operation, and nothing it could use to reach outside its own
/// workspace. Assembled by the worker after it resolves and validates the claim's <c>WorkspaceId</c>.
/// </summary>
/// <param name="OperationId">The operation being run.</param>
/// <param name="WorkspaceId">The workspace already resolved for this scope — not a value to re-check.</param>
/// <param name="LeaseToken">Presented on every write that completes this operation.</param>
/// <param name="Scope">What the resulting proposal is allowed to touch.</param>
/// <param name="RecipeId">The recipe this is about, when it is about one.</param>
/// <param name="RecipeVersionId">The exact pinned version, when <see cref="RecipeId"/> is set.</param>
/// <param name="CorrelationId">Ties every provider attempt and audit trail back to this run.</param>
/// <param name="RenewLeaseAsync">
/// Extends this claim's lease. A handler that expects to run close to <see cref="AiPolicy.LeaseDuration"/> —
/// a slow provider call being the case that matters — calls this partway through rather than racing the lease.
/// </param>
/// <param name="Inputs">
/// The declared, capability-specific fields the request named — never a prompt, never free text outside a
/// declared field. Null or missing a key means the creator did not supply that field, not that it was cleared.
/// A task type that names no recipe reads its whole request from here; <see cref="AiOperation"/> deliberately
/// carries no column for it yet, so an operation reaching a worker through today's recipe-bound request path
/// carries none.
/// </param>
internal sealed record AiTaskExecutionContext(
    Guid OperationId,
    Guid WorkspaceId,
    Guid LeaseToken,
    AiOperationScope Scope,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    Guid CorrelationId,
    Func<CancellationToken, Task> RenewLeaseAsync,
    IReadOnlyDictionary<string, string>? Inputs = null);

/// <summary>What running one operation produced: a proposal ready to store, or why it failed — plus every
/// provider attempt made along the way, for a handler that called one.</summary>
public sealed record AiTaskHandlerOutcome
{
    public AiProposal? Proposal { get; init; }

    public AiFailureCategory? FailureCategory { get; init; }

    public string? FailureSummary { get; init; }

    /// <summary>Every provider attempt this run made. Empty for a handler that never calls the gateway.</summary>
    public required IReadOnlyList<AiAttemptRecord> Attempts { get; init; }

    public bool Succeeded => FailureCategory is null && Proposal is not null;

    public static AiTaskHandlerOutcome ForProposal(AiProposal proposal, IReadOnlyList<AiAttemptRecord> attempts) =>
        new() { Proposal = proposal, Attempts = attempts };

    public static AiTaskHandlerOutcome ForFailure(
        AiFailureCategory category, string? summary, IReadOnlyList<AiAttemptRecord> attempts) =>
        new() { FailureCategory = category, FailureSummary = summary, Attempts = attempts };
}

/// <summary>
/// Runs one <see cref="AiTaskType"/>. Registered keyed by the task's <see cref="AiTaskCatalog"/> discriminator,
/// mirroring how <c>IOutboxMessageHandler</c> is dispatched by message type.
/// </summary>
/// <remarks>
/// A handler may call <see cref="CreatorPantry.Domain.Modules.Ai.Gateways.IAiCompletionGateway"/>, or may not —
/// <see cref="DiagnosticAiTaskHandler"/> deliberately does not, per its own documented contract. Either way it
/// calls facades only, never a repository, a gateway's SDK type, or <c>DbContext</c> directly (ai.md).
/// </remarks>
internal interface IAiTaskHandler
{
    Task<AiTaskHandlerOutcome> HandleAsync(AiTaskExecutionContext context, CancellationToken cancellationToken);
}
