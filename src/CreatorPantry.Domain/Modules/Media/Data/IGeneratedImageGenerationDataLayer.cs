using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

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
/// <param name="ReservationId">
/// The hold the request took against its account's allowance (AP.2.5). Null only for a generation queued
/// before allowance was held for pictures at all, which settles nothing and charges nothing.
/// </param>
/// <param name="BilledVariants">
/// How many variants earlier passes were already billed for, so this pass's charges continue the count rather
/// than restarting it.
/// </param>
/// <param name="UnusableVariants">How many of those nothing could be staged from.</param>
/// <param name="FlaggedVariants">How many of those the provider withheld on safety grounds.</param>
public sealed record GeneratedImageWork(
    GeneratedImageWriteOutcome Outcome,
    string PromptText = "",
    string? AvoidText = null,
    int VariantCount = 0,
    int Attempts = 0,
    IReadOnlySet<int>? StagedVariants = null,
    Guid? ReservationId = null,
    int BilledVariants = 0,
    int UnusableVariants = 0,
    int FlaggedVariants = 0);

/// <summary>What the provider billed this host for on one variant (AP.2.5).</summary>
/// <remarks>
/// Only reached for a variant the provider actually generated. A call that failed before generation — unreachable,
/// throttled, or refused outright — is not billed and never counted, and its share of the hold is released.
/// </remarks>
public enum GeneratedImageBilling
{
    /// <summary>Bytes arrived and this workspace staged them.</summary>
    Staged = 1,

    /// <summary>
    /// Bytes arrived and nothing could be staged from them: not an image, not whole, or too large.
    /// </summary>
    /// <remarks>Charged all the same. The provider made the picture, and the inspection that refused it is ours.</remarks>
    Unusable = 2,

    /// <summary>The provider made the picture and withheld it on its own safety grounds.</summary>
    Flagged = 3,
}

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
    /// Counts one variant the provider generated and bills for (AP.2.5), under the lease.
    /// </summary>
    Task<GeneratedImageWriteOutcome> RecordBilledVariantAsync(
        Guid operationId, Guid leaseToken, GeneratedImageBilling billing, CancellationToken cancellationToken);

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
        string? qualitySent,
        string? sizeSent,
        CancellationToken cancellationToken);

    /// <summary>Settles the operation as succeeded, partially succeeded, or failed.</summary>
    /// <remarks>
    /// <strong>Also reckons the generation's allowance (AP.2.5)</strong>, because this is the one place a
    /// generation ends: its ledger entries and the settlement of its hold are staged onto the same unit of
    /// work as the terminal status, so the two commit together. A requeue does not come through here, which
    /// is what keeps the hold standing for the pass that follows.
    /// </remarks>
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
    /// <param name="admission">
    /// Whose allowance to hold the pictures against, and what they will cost (AP.2.5). Null only where no
    /// image provider is configured at all, in which case nothing can be generated and nothing is held.
    /// </param>
    Task<GeneratedImageRequestResult> RequestAsync(
        GeneratedImageOperation operation,
        GeneratedImageAdmissionRequest? admission,
        CancellationToken cancellationToken);

    /// <summary>Whether this idempotency key has already bought an operation in this workspace.</summary>
    Task<bool> IsReplayAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IGeneratedImageGenerationRepository.CountForLimitsAsync"/>
    Task<(int InFlight, int VariantsToday)> CountForLimitsAsync(DateTimeOffset dayStart, CancellationToken cancellationToken);

    /// <inheritdoc cref="IGeneratedImageGenerationRepository.VariantsRequestedThroughAsync"/>
    Task<int?> VariantsRequestedThroughAsync(Guid operationId, CancellationToken cancellationToken);
}

/// <param name="Abandoned">True when no attempts remained and the operation was settled instead.</param>
public sealed record GeneratedImageRequeueResult(GeneratedImageWriteOutcome Outcome, bool Abandoned);

/// <param name="Replayed">True when the key had already bought this operation and nothing new was written.</param>
/// <param name="Operation">The operation, or null when the account's allowance would not cover it.</param>
/// <param name="Replayed">Whether this key had already bought the operation returned.</param>
/// <param name="Refusal">
/// Why no operation was created, when the account could not pay for it (AP.2.5). Null whenever
/// <paramref name="Operation"/> is not.
/// </param>
public sealed record GeneratedImageRequestResult(
    GeneratedImageOperation? Operation,
    bool Replayed,
    AiQuotaAdmissionServiceModel? Refusal = null);

/// <summary>
/// Whose allowance a generation is held against, and what its pictures will cost (AP.2.5).
/// </summary>
/// <param name="AccountId">
/// From <c>IWorkspaceContext.AccountId</c> in the request scope — never a request field, an unsigned header, or
/// anything a model supplied (USAGE-001). The one place it is knowable: the worker that runs the generation
/// resolves a service identity instead, so that a picture finishes after its creator has left the workspace.
/// </param>
/// <param name="ModelName">The image model the deployment will ask.</param>
/// <param name="Resolution">The tier it will send, or null when the model takes none.</param>
/// <param name="Quality">The quality it will send, or null when the model offers none.</param>
public sealed record GeneratedImageAdmissionRequest(
    string AccountId, string ModelName, string? Resolution, string? Quality);

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
    IOutboxWriter outbox,
    IAiQuotaAdmissionFacade quota,
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
            staged.ToHashSet(),
            operation.AiQuotaReservationId,
            operation.BilledVariantCount,
            operation.UnusableVariantCount,
            operation.FlaggedVariantCount);
    }

    /// <remarks>
    /// <para>
    /// <strong>Its own write, committed before the next provider call.</strong> A variant the provider
    /// generated is a variant the provider bills for, whether or not anything could be staged from it, so the
    /// count has to survive a crash between two variants — otherwise a resumed pass would re-run the whole
    /// request and the account would be charged for the first pass's pictures a second time while this count
    /// said it owed nothing.
    /// </para>
    /// <para>
    /// A staged variant's row and this count are separate writes, so a crash between them can leave a staged
    /// picture uncounted. That is the safe direction: the account is undercharged by one picture rather than
    /// charged for one it cannot see.
    /// </para>
    /// </remarks>
    public async Task<GeneratedImageWriteOutcome> RecordBilledVariantAsync(
        Guid operationId, Guid leaseToken, GeneratedImageBilling billing, CancellationToken cancellationToken)
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

        operation.BilledVariantCount++;

        if (billing is not GeneratedImageBilling.Staged)
        {
            operation.UnusableVariantCount++;
        }

        if (billing is GeneratedImageBilling.Flagged)
        {
            operation.FlaggedVariantCount++;
        }

        await operations.SaveChangesAsync(cancellationToken);

        return GeneratedImageWriteOutcome.Applied;
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
        string? qualitySent,
        string? sizeSent,
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
            GenerationQuality = qualitySent,
            GenerationSize = sizeSent,
            RetentionExpiresAt = now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = now,
            StatusChangedAt = now,
        };

        operations.Add(image);

        // Staged with the row, so the image and the request for its renditions commit together or not at
        // all (B-28, AF.5.5). Generation does not wait on them: this is a row in the outbox, and the
        // renditions are made by whoever delivers it. If the save below is refused, the request can still
        // go out with this scope's next save; it then names an image another attempt staged, or none, and
        // the job does nothing for a picture that already has its rows or does not exist.
        outbox.Enqueue(
            MediaRenditionRequestedEvent.MessageType,
            MediaRenditionRequestedEvent.For(
                operation.WorkspaceId, MediaRenditionSource.ForGeneratedImage(image.Id)).Serialize(),
            image.Id);

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

    /// <summary>
    /// What a charge-less settlement records as its provider, where nothing was ever reached.
    /// </summary>
    /// <remarks>
    /// The ledger requires a provider name on every row and such a settlement writes no rows at all — it
    /// releases the hold and stops — so this is never stored. It exists because the column is non-nullable and
    /// an empty string would fail its own constraint if the counts ever disagreed with the stamp.
    /// </remarks>
    private const string UnreachedProvider = "unreached";

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

        // The generation is over, so this is where its allowance is reckoned (AP.2.5): the account's ledger
        // entries and the settlement of its hold, staged onto this same unit of work so the terminal status
        // and the charge for reaching it commit together. A requeue does not come through here, which is what
        // keeps the hold standing for the pass that follows.
        // The provider is read off the row rather than passed in: RecordProviderAsync stamped it before the
        // first variant ran, so it is there for every path that can have billed anything — including the
        // requeue that runs out of attempts, which has no model of its own to hand over. Nothing stamped
        // means no provider was ever reached, so there is nothing to charge and the hold is released whole.
        if (operation.AiQuotaReservationId is { } reservation)
        {
            await quota.StageImageUsageAsync(
                new AiImageUsageServiceModel(
                    reservation,
                    operation.WorkspaceId,
                    operation.Id,
                    operation.BilledVariantCount - operation.UnusableVariantCount,
                    operation.UnusableVariantCount,
                    operation.FlaggedVariantCount,
                    now,
                    operation.ProviderName ?? UnreachedProvider,
                    operation.ModelName ?? UnreachedProvider,
                    operation.ModelDeployment),
                cancellationToken);
        }

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

    public async Task<bool> IsReplayAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        await operations.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken) is not null;

    public Task<(int InFlight, int VariantsToday)> CountForLimitsAsync(
        DateTimeOffset dayStart, CancellationToken cancellationToken) =>
        operations.CountForLimitsAsync(dayStart, cancellationToken);

    public Task<int?> VariantsRequestedThroughAsync(Guid operationId, CancellationToken cancellationToken) =>
        operations.VariantsRequestedThroughAsync(operationId, cancellationToken);

    public async Task<GeneratedImageRequestResult> RequestAsync(
        GeneratedImageOperation operation,
        GeneratedImageAdmissionRequest? admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Read first, because the common replay is a creator's second click rather than a race, and a read
        // that finds the first operation costs nothing and buys nothing. A replay is never admitted again:
        // its answer is the operation it already bought, and the hold that bought it.
        var existing = await operations.FindByIdempotencyKeyAsync(operation.IdempotencyKey, cancellationToken);

        if (existing is not null)
        {
            return new GeneratedImageRequestResult(existing, true);
        }

        // The allowance, before the row that would spend it (PRICE-001). The hold commits on its own — the
        // period's row version needs its own transaction — so from here until the operation is saved there is
        // a hold with no operation, and every path out of that window releases it. Admission is also the
        // first write of this request on purpose: a contended retry inside it clears the change tracker, so
        // anything staged before it would be silently dropped.
        if (admission is not null)
        {
            var held = await quota.AdmitImageAsync(
                new AiImageQuotaAdmissionRequestServiceModel(
                    admission.AccountId,
                    operation.Id,
                    operation.VariantCount,
                    admission.ModelName,
                    admission.Resolution,
                    admission.Quality,
                    clock.UtcNow + MediaPolicy.GenerationHoldDuration),
                cancellationToken);

            if (held.Outcome is not AiQuotaAdmissionOutcome.Admitted)
            {
                return new GeneratedImageRequestResult(null, false, held);
            }

            operation.AiQuotaReservationId = held.ReservationId;
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
            //
            // The loser's hold goes back now. It bought nothing — the winner's operation has its own hold —
            // and leaving it would show this account as having spent allowance on a generation that does not
            // exist, for as long as MediaPolicy.GenerationHoldDuration.
            await ReleaseHoldAsync(operation, CancellationToken.None);

            var winner = await operations.FindByIdempotencyKeyAsync(operation.IdempotencyKey, cancellationToken);

            return winner is null
                ? throw new InvalidOperationException(
                    "The generation request was refused by the database but no operation holds its idempotency key.")
                : new GeneratedImageRequestResult(winner, true);
        }
        catch
        {
            // Anything else between the hold and the row: the request is failing, so nothing will ever spend
            // this hold. Released on the way out rather than left for the sweep.
            await ReleaseHoldAsync(operation, CancellationToken.None);

            throw;
        }
    }

    /// <summary>
    /// Gives back the hold of an operation that was never queued, if it took one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not cancellable, and swallowing its own failures: it runs while a request is already failing, and a
    /// release that threw would replace the real reason with its own. The expiry sweep makes the same write,
    /// so the cost of losing this is the delay rather than a wrong balance.
    /// </para>
    /// <para>
    /// Logged without the prompt, the key or the account — the prompt is private creator content (ai.md), and
    /// this module never learns an account id at all.
    /// </para>
    /// </remarks>
    private async Task ReleaseHoldAsync(GeneratedImageOperation operation, CancellationToken cancellationToken)
    {
        if (operation.AiQuotaReservationId is not { } reservation)
        {
            return;
        }

        // The insert that just failed is still pending on this context, and releasing the hold is a save. Left
        // attached, that save re-sends the doomed insert, fails on the same constraint, and the hold it was
        // meant to give back stays held for hours. The staging path detaches for the same reason.
        operations.Forget(operation);

        try
        {
            await quota.ReleaseImageAsync(reservation, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Generation {OperationId} was not queued and its allowance hold was not released "
                    + "({ExceptionType}); the maintenance sweep will release it.",
                operation.Id,
                exception.GetType().Name);
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
