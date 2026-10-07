using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>Whether a lease-checked write took effect.</summary>
public enum GeneratedImageWriteOutcome
{
    Applied = 1,

    /// <summary>The operation is not readable in the resolved workspace, or is not there at all.</summary>
    Missing = 2,

    /// <summary>Somebody else holds the lease, or the operation has already been settled. Nothing was written.</summary>
    LeaseLost = 3,
}

/// <summary>What the worker needs to run one claimed operation, and nothing more.</summary>
/// <param name="StagedVariants">The variants already staged. A replay skips every one of them.</param>
public sealed record GeneratedImageWork(
    GeneratedImageWriteOutcome Outcome,
    string PromptText = "",
    string? AvoidText = null,
    int VariantCount = 0,
    int Attempts = 0,
    IReadOnlySet<int>? StagedVariants = null);

/// <summary>How staging one variant's bytes ended.</summary>
public enum GeneratedImageStageOutcome
{
    /// <summary>The object is in staging storage and the row that owns it is committed.</summary>
    Staged = 1,

    /// <summary>A row for this variant already existed. Nothing was written and nothing was removed.</summary>
    AlreadyStaged = 2,

    /// <summary>The scanner refused the bytes. Nothing was stored.</summary>
    Rejected = 3,

    /// <summary>The bytes ran past <see cref="MediaPolicy.ImageMaxBytes"/>. Nothing was stored.</summary>
    TooLarge = 4,

    /// <summary>Storage could not be reached, or the row could not be written. Compensated.</summary>
    Unavailable = 5,

    /// <summary>The lease was lost before the row could be written. Compensated.</summary>
    LeaseLost = 6,
}

/// <summary>Composes the image-generation queue's persistence operations and owns its transaction boundaries.</summary>
public interface IGeneratedImageGenerationDataLayer
{
    /// <inheritdoc cref="IGeneratedImageProviderGateway.Describe"/>
    GeneratedImageModel DescribeModel();

    /// <inheritdoc cref="IGeneratedImageProviderGateway.GenerateAsync"/>
    Task<GeneratedImageResult> GenerateAsync(string prompt, string? avoid, CancellationToken cancellationToken);

    /// <summary>Reads what a claimed operation asks for, if this worker still holds its lease.</summary>
    Task<GeneratedImageWork> OpenAsync(Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// One operation of the resolved workspace with its images, or null when it has none with that id.
    /// </summary>
    /// <remarks>
    /// No lease and no claim: this is a creator reading their own operation rather than a worker taking work, so
    /// it reads through the ordinary workspace filter and holds nothing.
    /// </remarks>
    Task<GeneratedImageOperationDetailServiceModel?> FindDetailAsync(
        Guid operationId, CancellationToken cancellationToken);

    /// <summary>Records which provider and model this attempt is running against, before it runs.</summary>
    /// <remarks>
    /// Before rather than after, so an operation that dies mid-generation still says what it was talking to.
    /// </remarks>
    Task<GeneratedImageWriteOutcome> RecordProviderAsync(
        Guid operationId, Guid leaseToken, GeneratedImageModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Scans one variant's bytes, writes the object, and commits the row that owns it — compensating the
    /// object if the row cannot be written.
    /// </summary>
    Task<GeneratedImageStageOutcome> StageVariantAsync(
        Guid operationId,
        Guid leaseToken,
        int variantIndex,
        ReadOnlyMemory<byte> content,
        string mediaType,
        int width,
        int height,
        CancellationToken cancellationToken);

    /// <summary>Settles the operation as succeeded, partially succeeded, or failed.</summary>
    Task<GeneratedImageWriteOutcome> SettleAsync(
        Guid operationId,
        Guid leaseToken,
        GeneratedImageOperationStatus status,
        string? failureCategory,
        string? failureSummary,
        CancellationToken cancellationToken);

    /// <summary>Hands the operation back for another attempt, or settles it when none remain.</summary>
    Task<GeneratedImageRequeueResult> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        string failureCategory,
        string failureSummary,
        double jitter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases the lease without charging the attempt, for a worker that is stopping rather than failing.
    /// </summary>
    Task<GeneratedImageWriteOutcome> ReleaseAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <inheritdoc cref="IGeneratedImageGenerationRepository.StagedCountAsync"/>
    Task<int> StagedCountAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Records a new request, or returns the one this idempotency key already bought.</summary>
    Task<GeneratedImageRequestResult> RequestAsync(
        GeneratedImageOperation operation, CancellationToken cancellationToken);
}

/// <param name="Abandoned">True when no attempts remained and the operation was settled instead.</param>
public sealed record GeneratedImageRequeueResult(GeneratedImageWriteOutcome Outcome, bool Abandoned);

/// <param name="Replayed">True when the key had already bought this operation and nothing new was written.</param>
public sealed record GeneratedImageRequestResult(GeneratedImageOperation Operation, bool Replayed);

/// <inheritdoc cref="IGeneratedImageGenerationDataLayer"/>
/// <remarks>
/// <para>
/// <strong>No transaction is open across a provider call.</strong> The provider call and the object write
/// happen between database round trips, never inside one: a generation can take minutes, and a SQL
/// transaction held open for it would hold locks on the queue for as long as the slowest model in the
/// batch. Each variant's row is its own single committed write, which is also what makes a crash resumable
/// at the variant rather than at the request.
/// </para>
/// <para>
/// <strong>Every write is lease-checked.</strong> A worker whose lease lapsed while it was inside a
/// provider call must not go on writing: the sweep has already handed the operation to somebody else, and
/// two workers settling one operation is how a creator ends up told their request failed while its images
/// were staged.
/// </para>
/// </remarks>
internal sealed class GeneratedImageGenerationDataLayer(
    IGeneratedImageGenerationRepository operations,
    IGeneratedImageOperationDetailRepository details,
    IGeneratedImageProviderGateway provider,
    IGeneratedImageObjectGateway objects,
    IMalwareScanGateway scanner,
    IClock clock,
    ILogger<GeneratedImageGenerationDataLayer> logger) : IGeneratedImageGenerationDataLayer
{
    public GeneratedImageModel DescribeModel() => provider.Describe();

    public Task<GeneratedImageOperationDetailServiceModel?> FindDetailAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        details.FindAsync(operationId, cancellationToken);

    public Task<GeneratedImageResult> GenerateAsync(
        string prompt, string? avoid, CancellationToken cancellationToken) =>
        provider.GenerateAsync(prompt, avoid, cancellationToken);

    public async Task<GeneratedImageWork> OpenAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var operation = await operations.FindAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return new GeneratedImageWork(GeneratedImageWriteOutcome.Missing);
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return new GeneratedImageWork(GeneratedImageWriteOutcome.LeaseLost);
        }

        var staged = await operations.StagedVariantsAsync(operationId, cancellationToken);

        return new GeneratedImageWork(
            GeneratedImageWriteOutcome.Applied,
            operation.PromptText,
            operation.AvoidText,
            operation.VariantCount,
            operation.Attempts,
            staged.ToHashSet());
    }

    public async Task<GeneratedImageWriteOutcome> RecordProviderAsync(
        Guid operationId, Guid leaseToken, GeneratedImageModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var operation = await operations.FindAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return GeneratedImageWriteOutcome.Missing;
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return GeneratedImageWriteOutcome.LeaseLost;
        }

        operation.ProviderName = model.ProviderName;
        operation.ModelName = model.ModelName;
        operation.ModelDeployment = model.ModelDeployment;

        await operations.SaveChangesAsync(cancellationToken);

        return GeneratedImageWriteOutcome.Applied;
    }

    public async Task<GeneratedImageStageOutcome> StageVariantAsync(
        Guid operationId,
        Guid leaseToken,
        int variantIndex,
        ReadOnlyMemory<byte> content,
        string mediaType,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var operation = await operations.FindAsync(operationId, cancellationToken);

        if (operation is null || !HoldsLease(operation, leaseToken))
        {
            return GeneratedImageStageOutcome.LeaseLost;
        }

        if (await ScanAsync(content, cancellationToken) is not MalwareScanVerdict.Clean)
        {
            return GeneratedImageStageOutcome.Rejected;
        }

        var write = await PutAsync(operationId, variantIndex, content, mediaType, cancellationToken);

        switch (write.Outcome)
        {
            case GeneratedImageObjectWriteOutcome.TooLarge:
                return GeneratedImageStageOutcome.TooLarge;
            case GeneratedImageObjectWriteOutcome.Unavailable:
                return GeneratedImageStageOutcome.Unavailable;
            case GeneratedImageObjectWriteOutcome.AlreadyExists:
                // Written above and still refused: something is holding the key this worker cannot clear.
                return GeneratedImageStageOutcome.Unavailable;
        }

        var stored = write.Object!;
        var now = clock.UtcNow;

        var image = new GeneratedImage
        {
            Id = Guid.NewGuid(),

            // Stamped by WorkspaceOwnershipInterceptor from the resolved context; set here only so the
            // in-memory graph is coherent before it is saved.
            WorkspaceId = operation.WorkspaceId,
            GeneratedImageOperationId = operationId,
            VariantIndex = variantIndex,
            Status = GeneratedImageStatus.Staged,
            ObjectKey = stored.ObjectKey,
            MediaType = stored.MediaType,
            Width = width,
            Height = height,
            SizeBytes = stored.SizeBytes,
            ContentChecksum = stored.ContentChecksum,
            ProviderName = operation.ProviderName ?? string.Empty,
            ModelName = operation.ModelName ?? string.Empty,
            ModelDeployment = operation.ModelDeployment,
            RetentionExpiresAt = now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = now,
            StatusChangedAt = now,
        };

        operations.Add(image);

        try
        {
            await operations.SaveChangesAsync(cancellationToken);

            return GeneratedImageStageOutcome.Staged;
        }
        catch (DbUpdateException exception)
        {
            // Before anything else: a failed save leaves the row Added, and settling the operation is
            // the very next write this scope makes.
            operations.Forget(image);

            // The compensating half. The object is written and the row that would have owned it is not, so
            // without this the bytes are an orphan: nothing references them, nothing reads them, and the
            // retention sweep looks at rows rather than at the container. A unique-index violation is the
            // expected case — another worker staged this variant while this one was at the provider — and
            // there the object is already owned by that worker's row under the same key, so it must be
            // left exactly where it is.
            var duplicate = await operations.StagedVariantsAsync(operationId, CancellationToken.None);

            if (duplicate.Contains(variantIndex))
            {
                logger.LogInformation(
                    "Variant {VariantIndex} of operation {OperationId} was staged by another attempt; this one stood down.",
                    variantIndex,
                    operationId);

                return GeneratedImageStageOutcome.AlreadyStaged;
            }

            logger.LogError(
                "Staging variant {VariantIndex} of operation {OperationId} failed to commit ({ExceptionType}); "
                    + "removing the object it had written.",
                variantIndex,
                operationId,
                exception.GetType().Name);

            await CompensateAsync(stored.ObjectKey);

            return GeneratedImageStageOutcome.Unavailable;
        }
    }

    public async Task<GeneratedImageWriteOutcome> SettleAsync(
        Guid operationId,
        Guid leaseToken,
        GeneratedImageOperationStatus status,
        string? failureCategory,
        string? failureSummary,
        CancellationToken cancellationToken)
    {
        var operation = await operations.FindAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return GeneratedImageWriteOutcome.Missing;
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return GeneratedImageWriteOutcome.LeaseLost;
        }

        var now = clock.UtcNow;

        operation.Status = status;
        operation.FailureCategory = failureCategory;
        operation.FailureSummary = Truncate(failureSummary);
        operation.StatusChangedAt = now;
        operation.CompletedAt = now;
        operation.LeasedBy = null;
        operation.LeaseExpiresAt = null;

        await operations.SaveChangesAsync(cancellationToken);

        return GeneratedImageWriteOutcome.Applied;
    }

    public async Task<GeneratedImageRequeueResult> RequeueAsync(
        Guid operationId,
        Guid leaseToken,
        string failureCategory,
        string failureSummary,
        double jitter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCategory);

        var operation = await operations.FindAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return new GeneratedImageRequeueResult(GeneratedImageWriteOutcome.Missing, false);
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return new GeneratedImageRequeueResult(GeneratedImageWriteOutcome.LeaseLost, false);
        }

        if (operation.Attempts >= MediaPolicy.GenerationMaxAttempts)
        {
            // Out of attempts. Whatever was staged is kept and said so, because a creator who got two of
            // four images has two real images — telling them the request failed would be a lie about work
            // that is sitting in storage waiting for them.
            var staged = await operations.StagedCountAsync(operationId, cancellationToken);

            return new GeneratedImageRequeueResult(
                await SettleAsync(
                    operationId,
                    leaseToken,
                    staged > 0
                        ? GeneratedImageOperationStatus.PartiallySucceeded
                        : GeneratedImageOperationStatus.Failed,
                    failureCategory,
                    failureSummary,
                    cancellationToken),
                true);
        }

        var now = clock.UtcNow;

        operation.Status = GeneratedImageOperationStatus.Requested;
        operation.FailureCategory = failureCategory;
        operation.FailureSummary = Truncate(failureSummary);
        operation.StatusChangedAt = now;
        operation.AvailableAt = now + MediaPolicy.GenerationBackoffFor(operation.Attempts, jitter);
        operation.LeasedBy = null;
        operation.LeaseExpiresAt = null;

        await operations.SaveChangesAsync(cancellationToken);

        return new GeneratedImageRequeueResult(GeneratedImageWriteOutcome.Applied, false);
    }

    public async Task<GeneratedImageWriteOutcome> ReleaseAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var operation = await operations.FindAsync(operationId, cancellationToken);

        if (operation is null)
        {
            return GeneratedImageWriteOutcome.Missing;
        }

        if (!HoldsLease(operation, leaseToken))
        {
            return GeneratedImageWriteOutcome.LeaseLost;
        }

        // Uncharged and immediately claimable: the host is stopping, which is not the request's fault, and
        // the attempt it already spent bought whatever variants are staged. The next worker resumes at the
        // first variant with no row.
        operation.Status = GeneratedImageOperationStatus.Requested;
        operation.StatusChangedAt = clock.UtcNow;
        operation.AvailableAt = clock.UtcNow;
        operation.Attempts = Math.Max(0, operation.Attempts - 1);
        operation.LeasedBy = null;
        operation.LeaseExpiresAt = null;

        await operations.SaveChangesAsync(cancellationToken);

        return GeneratedImageWriteOutcome.Applied;
    }

    public Task<int> StagedCountAsync(Guid operationId, CancellationToken cancellationToken) =>
        operations.StagedCountAsync(operationId, cancellationToken);

    public async Task<GeneratedImageRequestResult> RequestAsync(
        GeneratedImageOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Read first, because the common replay is a creator's second click rather than a race, and a read
        // that finds the first operation costs nothing and buys nothing.
        var existing = await operations.FindByIdempotencyKeyAsync(operation.IdempotencyKey, cancellationToken);

        if (existing is not null)
        {
            return new GeneratedImageRequestResult(existing, true);
        }

        operations.Add(operation);

        try
        {
            await operations.SaveChangesAsync(cancellationToken);

            return new GeneratedImageRequestResult(operation, false);
        }
        catch (DbUpdateException)
        {
            // The race the read above cannot close: two requests with one key arriving together. The unique
            // index is what actually enforces this, and losing to it means the other request's operation is
            // the answer — which is the whole point of the key. A second generation is the most expensive
            // mistake this product can make, so the index rather than the read is the authority.
            var winner = await operations.FindByIdempotencyKeyAsync(operation.IdempotencyKey, cancellationToken);

            return winner is null
                ? throw new InvalidOperationException(
                    "The generation request was refused by the database but no operation holds its idempotency key.")
                : new GeneratedImageRequestResult(winner, true);
        }
    }

    /// <summary>
    /// Scans the provider's bytes before anything stores them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yes, even bytes from an authenticated model deployment over TLS. media.md asks for a scanning policy
    /// on media, not on media from sources we distrust, and "the provider would never" is the assumption
    /// that makes a compromised or confused deployment a path straight into a creator's library. The
    /// structural check that ran before this proves the file is a well-formed image header; it proves
    /// nothing about what follows it.
    /// </para>
    /// <para>
    /// An unavailable scanner refuses, as it does everywhere else: no verdict is not permission.
    /// </para>
    /// </remarks>
    private async Task<MalwareScanVerdict> ScanAsync(
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);

        var verdict = await scanner.ScanAsync(stream, cancellationToken);

        if (verdict is not MalwareScanVerdict.Clean)
        {
            logger.LogWarning("A generated image was refused by the scanner: {Verdict}.", verdict);
        }

        return verdict;
    }

    /// <summary>
    /// Writes the object, clearing an orphan left by an attempt that died between the write and the row.
    /// </summary>
    /// <remarks>
    /// The key is deterministic, so a crashed attempt's object sits at exactly the key this one wants. The
    /// caller has already established that no row owns this variant, and the unique index on
    /// <c>ObjectKey</c> means no other row can own this key either — so there is nothing the delete could
    /// be taking away from anyone. Clearing it is the only way forward: the store is create-only, and a
    /// variant whose key is permanently occupied could never be staged again.
    /// </remarks>
    private async Task<GeneratedImageObjectWrite> PutAsync(
        Guid operationId, int variantIndex, ReadOnlyMemory<byte> content, string mediaType, CancellationToken cancellationToken)
    {
        var write = await objects.PutVariantAsync(operationId, variantIndex, content, mediaType, cancellationToken);

        if (write.Outcome is not GeneratedImageObjectWriteOutcome.AlreadyExists)
        {
            return write;
        }

        logger.LogInformation(
            "Variant {VariantIndex} of operation {OperationId} found an orphaned staging object from an "
                + "earlier attempt; removing it and writing again.",
            variantIndex,
            operationId);

        await objects.DeleteVariantAsync(operationId, variantIndex, cancellationToken);

        return await objects.PutVariantAsync(operationId, variantIndex, content, mediaType, cancellationToken);
    }

    /// <summary>
    /// Removes an object whose owning row could not be committed.
    /// </summary>
    /// <remarks>
    /// <c>CancellationToken.None</c>, because compensation runs on the way out of a failure and a token
    /// that has already fired would leave the orphan it exists to clear. A delete that itself fails is
    /// logged and swallowed: the write that needed compensating has already failed, and throwing here
    /// would replace a recorded outcome with an unhandled exception. 12.8's reconciliation is the backstop
    /// for the orphan this leaves behind.
    /// </remarks>
    private async Task CompensateAsync(string objectKey)
    {
        try
        {
            await objects.DeleteAsync(objectKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Could not remove the staging object of a row that failed to commit ({ExceptionType}); "
                    + "it is an orphan until the retention sweep reconciles it.",
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Whether this worker still holds the operation, and the operation is still running.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A matching token on a settled operation is a worker that woke up after the
    /// sweep already finished its work for it, and writing on the strength of the token alone would undo
    /// a terminal state.
    /// </remarks>
    private static bool HoldsLease(GeneratedImageOperation operation, Guid leaseToken) =>
        operation.Status is GeneratedImageOperationStatus.Running && operation.LeasedBy == leaseToken;

    private static string? Truncate(string? summary) =>
        summary is null || summary.Length <= MediaPolicy.FailureSummaryMaxLength
            ? summary
            : summary[..MediaPolicy.FailureSummaryMaxLength];
}
