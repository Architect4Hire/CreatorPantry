using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Facade;

/// <summary>
/// Posts per-account AI usage. The only way into this module's ledger from anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This module is a sink.</strong> It is told who to charge and never looks it up: it reads no AI
/// table, no recipe table, and no membership table. That is stronger than the boundary rules require, and it
/// is what lets an unfiltered ledger be reasoned about on its own — nothing here can reach a workspace's data
/// even by accident.
/// </para>
/// </remarks>
public interface IAiUsageRecordingFacade
{
    /// <summary>
    /// Stages one ledger entry per attempt on the caller's unit of work, skipping attempts already posted.
    /// Returns how many were staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Stages. Does not save — and the name says so because a facade that silently did not persist
    /// would be a trap.</strong> USAGE-001 requires the ledger write to commit with the attempt record it
    /// describes: a settled attempt that posts no usage is a defect, and two transactions would make that
    /// state reachable whenever the second one failed. So this deliberately does not own a transaction
    /// boundary, which is the one place this module departs from backend.md's rule that a DataLayer owns one.
    /// It joins the caller's instead, exactly as the AI module's own <c>AddExecutionRecords</c> does, and the
    /// caller's next <c>SaveChangesAsync</c> commits both together or neither.
    /// </para>
    /// <para>
    /// <strong>Idempotent by attempt.</strong> An attempt already in the ledger is skipped rather than posted
    /// again, so a duplicate delivery posts once. The unique index on
    /// <c>(AiOperationId, AttemptNumber)</c> is the backstop for a genuine race, not the routine path — a
    /// unique violation would roll back the caller's whole transaction, taking the attempt record with it.
    /// </para>
    /// </remarks>
    Task<int> StageAttemptsAsync(
        AiUsageAttributionServiceModel attribution,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageRecordingFacade"/>
internal sealed class AiUsageRecordingFacade(IAiUsageRecordingBusiness business) : IAiUsageRecordingFacade
{
    public Task<int> StageAttemptsAsync(
        AiUsageAttributionServiceModel attribution,
        IReadOnlyCollection<AiUsageAttemptServiceModel> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(attempts);

        return business.StageAttemptsAsync(attribution, attempts, cancellationToken);
    }
}
