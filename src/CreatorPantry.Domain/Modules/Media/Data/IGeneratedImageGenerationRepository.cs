using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>
/// EF Core access to the image-generation queue, within the resolved workspace.
/// </summary>
/// <remarks>
/// Every read here is filtered: the worker has already resolved and validated the workspace through
/// <c>IWorkspaceResolutionFacade</c>, so an operation the claim found but that belongs to someone else
/// simply is not in the set these read over. That is what turns a tampered or stale claim into "missing"
/// rather than into a cross-workspace read (tenancy.md).
/// </remarks>
public interface IGeneratedImageGenerationRepository
{
    /// <summary>The operation, tracked, or null when this workspace holds none with that id.</summary>
    Task<GeneratedImageOperation?> FindAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>The variant indexes this operation has already staged. The replay guard's input.</summary>
    Task<IReadOnlyList<int>> StagedVariantsAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>How many images this operation has staged.</summary>
    Task<int> StagedCountAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>The operation already recorded under this idempotency key, or null.</summary>
    Task<GeneratedImageOperation?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// This workspace's image requests queued or running, and the variants it has asked for since
    /// <paramref name="dayStart"/> (15.3).
    /// </summary>
    /// <remarks>
    /// Counted from the operations themselves, inside the workspace filter: exact whatever a worker did since, a
    /// replay adds nothing because it is the same row, and no other workspace's requests are ever in the sum.
    /// </remarks>
    Task<(int InFlight, int VariantsToday)> CountForLimitsAsync(DateTimeOffset dayStart, CancellationToken cancellationToken);

    /// <summary>
    /// The variants this workspace had asked for on <paramref name="operationId"/>'s day, up to and including it.
    /// Null when the operation is not this workspace's.
    /// </summary>
    Task<int?> VariantsRequestedThroughAsync(Guid operationId, CancellationToken cancellationToken);

    void Add(GeneratedImageOperation operation);

    void Add(GeneratedImage image);

    /// <summary>
    /// Forgets an image whose insert failed, so a later save does not attempt it again.
    /// </summary>
    /// <remarks>
    /// A failed <c>SaveChanges</c> leaves the entity <c>Added</c>. Without this, the next write on this
    /// scope — settling the operation, which is exactly what follows a failed stage — would replay the
    /// insert that just failed and throw on the way out, turning a recorded failure into an unhandled one.
    /// </remarks>
    void Forget(GeneratedImage image);

    /// <summary>
    /// Forgets an operation whose insert failed, so a later save on this context does not attempt it again.
    /// </summary>
    /// <remarks>
    /// The request path needs it for the same reason the staging path does, and with money on it: after a
    /// failed insert the operation is still pending, so the very next save — the one releasing the allowance
    /// hold that operation would have spent — would re-send the doomed insert, fail again, and leave the hold
    /// standing. Detaching it is what lets the compensation through.
    /// </remarks>
    void Forget(GeneratedImageOperation operation);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageGenerationRepository"/>
internal sealed class GeneratedImageGenerationRepository(CreatorPantryDbContext context)
    : IGeneratedImageGenerationRepository
{
    public Task<GeneratedImageOperation?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
        context.GeneratedImageOperations.FirstOrDefaultAsync(
            operation => operation.Id == operationId, cancellationToken);

    public async Task<IReadOnlyList<int>> StagedVariantsAsync(Guid operationId, CancellationToken cancellationToken) =>
        await context.GeneratedImages
            .AsNoTracking()
            .Where(image => image.GeneratedImageOperationId == operationId)
            .Select(image => image.VariantIndex)
            .ToListAsync(cancellationToken);

    public Task<int> StagedCountAsync(Guid operationId, CancellationToken cancellationToken) =>
        context.GeneratedImages.CountAsync(
            image => image.GeneratedImageOperationId == operationId, cancellationToken);

    public Task<GeneratedImageOperation?> FindByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken cancellationToken) =>
        context.GeneratedImageOperations.FirstOrDefaultAsync(
            operation => operation.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<(int InFlight, int VariantsToday)> CountForLimitsAsync(
        DateTimeOffset dayStart, CancellationToken cancellationToken)
    {
        var inFlight = await context.GeneratedImageOperations.CountAsync(
            operation => operation.Status == GeneratedImageOperationStatus.Requested
                || operation.Status == GeneratedImageOperationStatus.Running,
            cancellationToken);

        var variants = await context.GeneratedImageOperations
            .Where(operation => operation.RequestedAt >= dayStart)
            .SumAsync(operation => (int?)operation.VariantCount, cancellationToken) ?? 0;

        return (inFlight, variants);
    }

    public async Task<int?> VariantsRequestedThroughAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var requestedAt = await context.GeneratedImageOperations
            .Where(operation => operation.Id == operationId)
            .Select(operation => (DateTimeOffset?)operation.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (requestedAt is not { } instant)
        {
            return null;
        }

        var dayStart = LimitDay.StartOf(instant);

        // Up to and including this one, in request order. Two requests in the same instant are both counted, which
        // can only ever refuse sooner, never let more through.
        return await context.GeneratedImageOperations
            .Where(operation => operation.RequestedAt >= dayStart && operation.RequestedAt <= instant)
            .SumAsync(operation => (int?)operation.VariantCount, cancellationToken) ?? 0;
    }

    public void Add(GeneratedImageOperation operation) => context.GeneratedImageOperations.Add(operation);

    public void Add(GeneratedImage image) => context.GeneratedImages.Add(image);

    public void Forget(GeneratedImage image) => context.Entry(image).State = EntityState.Detached;

    public void Forget(GeneratedImageOperation operation) =>
        context.Entry(operation).State = EntityState.Detached;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
