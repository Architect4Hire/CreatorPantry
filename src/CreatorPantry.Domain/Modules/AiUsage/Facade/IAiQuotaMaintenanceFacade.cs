using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Facade;

/// <summary>
/// Finishes what a worker did not: applies settlements nobody applied, releases holds whose lease lapsed, and
/// closes periods whose totals can no longer move.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IAiQuotaAdmissionFacade"/> because the caller is different.</strong> That
/// one is reached by the code running an operation, inside a resolved workspace scope; this one is driven by a
/// timer in the worker host, before any workspace exists and on behalf of nobody in particular.
/// </para>
/// <para>
/// <strong>It reads across every account, and needs no carve-out to.</strong> Nothing in this module is
/// workspace-owned, so there is no query filter here to ignore — unlike the AI queue's own claim, which is a
/// documented <c>BulkOperationBoundaryTests</c> exemption precisely because its table is filtered.
/// </para>
/// </remarks>
public interface IAiQuotaMaintenanceFacade
{
    /// <summary>
    /// Runs one pass. Every step is idempotent, bounded, and safe to race against a live worker.
    /// </summary>
    Task<AiQuotaMaintenanceSummary> SweepAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiQuotaMaintenanceFacade"/>
internal sealed class AiQuotaMaintenanceFacade(IAiQuotaAdmissionBusiness business) : IAiQuotaMaintenanceFacade
{
    public Task<AiQuotaMaintenanceSummary> SweepAsync(CancellationToken cancellationToken) =>
        business.SweepAsync(cancellationToken);
}
