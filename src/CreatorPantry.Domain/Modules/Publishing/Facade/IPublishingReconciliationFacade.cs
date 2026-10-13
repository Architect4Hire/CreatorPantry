using CreatorPantry.Domain.Modules.Publishing.Business;

namespace CreatorPantry.Domain.Modules.Publishing.Facade;

/// <summary>
/// The boundary the reconciliation worker crosses, once per profile it has claimed records for (13.10c).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No role gate, and deliberately so.</strong> There is no member here: the worker resolved the
/// workspace as the service before calling this, and what it passes are ids it read from queue state. The
/// authorization that matters already happened — the workspace was resolved and validated, and every read
/// below this runs under its own query filter.
/// </para>
/// <para>
/// It takes a profile and the records waiting on it rather than one record at a time, because one listing of
/// that channel answers all of them and the provider's quota is small.
/// </para>
/// </remarks>
public interface IPublishingReconciliationFacade
{
    /// <inheritdoc cref="IPublishingReconciliationBusiness.ReconcileProfileAsync"/>
    Task<PublishingReconcileSummary> ReconcileProfileAsync(
        Guid publishingProfileId, IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublishingReconciliationFacade"/>
internal sealed class PublishingReconciliationFacade(IPublishingReconciliationBusiness business)
    : IPublishingReconciliationFacade
{
    public Task<PublishingReconcileSummary> ReconcileProfileAsync(
        Guid publishingProfileId, IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken) =>
        business.ReconcileProfileAsync(publishingProfileId, recordIds, cancellationToken);
}
