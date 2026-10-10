using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Data;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Requests renditions for pictures that have none and nothing on the way: those stored before the job
/// existed, and any whose own request was lost or failed (B-28, AF.5.5).
/// </summary>
public interface IMediaRenditionBackfillWorker
{
    /// <summary>Requests renditions for one bounded batch. Returns how many pictures were requested.</summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaRenditionBackfillWorker"/>
/// <remarks>
/// <para>
/// <strong>It makes nothing itself.</strong> Each picture it finds becomes the same outbox request a new
/// picture is given, so backfilled work goes through the same handler, the same workspace validation and
/// the same retries. This holds identifiers and never resolves a workspace, because it never reads one.
/// </para>
/// <para>
/// <strong>Bounded, and resumed without a cursor.</strong> A pass takes at most
/// <see cref="MediaPolicy.RenditionBackfillBatchSize"/> pictures. A picture that has been dealt with has
/// rows and is no longer found, so the next pass starts where this one's work ended; every outcome is a
/// row, including giving up, so nothing stays at the head of the queue for ever.
/// </para>
/// <para>
/// <strong>Not the newest pictures.</strong> One stored within
/// <see cref="MediaPolicy.RenditionBackfillMinimumAge"/> still has its own request in flight, and asking
/// again would only be a second delivery of the same work. Asking twice is harmless — the job does nothing
/// for a purpose that has a row — but there is no reason to.
/// </para>
/// </remarks>
internal sealed class MediaRenditionBackfillWorker(
    MediaRenditionClaimRepository work, IOutboxWriter outbox, IClock clock) : IMediaRenditionBackfillWorker
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var due = await work.FindUnrenderedAsync(
            clock.UtcNow - MediaPolicy.RenditionBackfillMinimumAge,
            MediaPolicy.RenditionBackfillBatchSize,
            cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        foreach (var request in due)
        {
            outbox.Enqueue(MediaRenditionRequestedEvent.MessageType, request.Serialize(), request.CorrelationId);
        }

        await work.SaveChangesAsync(cancellationToken);

        return due.Count;
    }
}
