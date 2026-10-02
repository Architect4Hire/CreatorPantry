using CreatorPantry.Domain.Modules.Brand.Business;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>
/// The seam the Worker's embedding driver calls once it has claimed an operation and resolved its workspace.
/// </summary>
/// <remarks>Not exposed over HTTP: nothing a creator does requests an embedding directly (no per-request embedding).</remarks>
public interface IBrandSourceEmbeddingFacade
{
    Task<BrandSourceEmbeddingRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>Deletes one retired or abandoned chunk set of the resolved workspace.</summary>
    Task<bool> RetireSetAsync(Guid setId, CancellationToken cancellationToken);

    /// <summary>Queues the embedding of one artifact of the resolved workspace that has no current set.</summary>
    Task<bool> EnqueueAsync(Guid extractionId, CancellationToken cancellationToken);
}

internal sealed class BrandSourceEmbeddingFacade(IBrandSourceEmbeddingBusiness business) : IBrandSourceEmbeddingFacade
{
    public Task<BrandSourceEmbeddingRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken) =>
        business.ExecuteAsync(operationId, leaseToken, cancellationToken);

    public Task<bool> RetireSetAsync(Guid setId, CancellationToken cancellationToken) =>
        business.RetireSetAsync(setId, cancellationToken);

    public Task<bool> EnqueueAsync(Guid extractionId, CancellationToken cancellationToken) =>
        business.EnqueueAsync(extractionId, cancellationToken);
}
