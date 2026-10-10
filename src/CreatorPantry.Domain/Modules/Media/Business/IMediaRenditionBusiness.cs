using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <param name="Ready">Renditions stored and recorded by this call.</param>
/// <param name="NotCompressed">Purposes this call recorded as having no rendition.</param>
public sealed record MediaRenditionProduceSummary(int Ready, int NotCompressed)
{
    public static MediaRenditionProduceSummary Nothing { get; } = new(0, 0);
}

/// <summary>
/// Makes the renditions a stored picture does not have yet (B-28, AF.5.5).
/// </summary>
public interface IMediaRenditionBusiness
{
    /// <summary>
    /// Decodes the picture once and, for each purpose with no row, records either a stored rendition or the
    /// reason there is none.
    /// </summary>
    /// <param name="finalAttempt">
    /// Whether the caller will not ask again. A failure that would otherwise be retried is then recorded as
    /// <see cref="MediaRenditionReason.RetriesExhausted"/> instead of thrown.
    /// </param>
    Task<MediaRenditionProduceSummary> ProduceAsync(
        MediaRenditionSource source, bool finalAttempt, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaRenditionBusiness"/>
/// <remarks>
/// <para>
/// <strong>Every outcome is a row, and a row is final.</strong> A rendition that was stored, a source the
/// codec cannot read, a rendition that came out no smaller: each is recorded against its purpose, and a
/// purpose with a row is never attempted again. That is what makes a second delivery of the same request do
/// nothing, and what stops an unreadable picture being decoded on every pass for ever.
/// </para>
/// <para>
/// <strong>Only a failure that might not happen next time is thrown.</strong> Storage that cannot be
/// reached is the caller's to retry. A file that is not a PNG will not become one, so it is settled here.
/// </para>
/// <para>
/// <strong>The original is read and never written.</strong> Nothing here has a way to change or remove the
/// picture a rendition is made from.
/// </para>
/// </remarks>
internal sealed class MediaRenditionBusiness(
    IMediaRenditionDataLayer renditions, ILogger<MediaRenditionBusiness> logger) : IMediaRenditionBusiness
{
    public async Task<MediaRenditionProduceSummary> ProduceAsync(
        MediaRenditionSource source, bool finalAttempt, CancellationToken cancellationToken)
    {
        var target = await renditions.FindTargetAsync(source, cancellationToken);

        // Declined, expired, kept, deleted, unknown or already done: none of it is an error. The request
        // was true when it was written and the picture has moved on since.
        if (target is null || target.Missing.Count == 0)
        {
            return MediaRenditionProduceSummary.Nothing;
        }

        try
        {
            return await ProduceAsync(target, cancellationToken);
        }
        catch (Exception exception) when (finalAttempt && exception is not OperationCanceledException)
        {
            // By exception type only: a message could carry a key, and a key names a creator's picture.
            logger.LogError(
                "Renditions could not be made after the last attempt ({ExceptionType}); the picture is served as stored.",
                exception.GetType().Name);

            return await GiveUpAsync(source, cancellationToken);
        }
    }

    private async Task<MediaRenditionProduceSummary> ProduceAsync(
        MediaRenditionTarget target, CancellationToken cancellationToken)
    {
        // Known from the row, so a source too big to decode is never read into memory to find that out.
        if (target.SizeBytes > MediaPolicy.ImageMaxBytes)
        {
            return await SettleAsync(target, MediaRenditionReason.TooLarge, cancellationToken);
        }

        var decoded = PngDecoder.Decode(
            await renditions.ReadSourceAsync(target, cancellationToken), cancellationToken);

        MediaRenditionReason? unreadable = decoded.Outcome switch
        {
            PngDecodeOutcome.Decoded => null,
            PngDecodeOutcome.NotPng => MediaRenditionReason.UnreadableFormat,
            PngDecodeOutcome.Unsupported => MediaRenditionReason.UnsupportedVariant,
            PngDecodeOutcome.TooLarge => MediaRenditionReason.TooLarge,
            _ => MediaRenditionReason.Corrupt,
        };

        if (unreadable is { } reason)
        {
            return await SettleAsync(target, reason, cancellationToken);
        }

        var pixels = decoded.Pixels!;

        // A JPEG has no alpha, and choosing a colour to flatten a creator's picture onto is not this job's
        // decision to make. Served as stored (B-28, as amended by AF.5.5).
        if (pixels.HasTransparency)
        {
            return await SettleAsync(target, MediaRenditionReason.HasTransparency, cancellationToken);
        }

        int ready = 0, notCompressed = 0;

        foreach (var purpose in target.Missing)
        {
            var (maxEdge, quality) = MediaPolicy.RenditionSpecification(purpose);
            var fitted = AreaAverageDownscaler.Downscale(pixels, maxEdge, maxEdge, cancellationToken);
            var encoded = JpegEncoder.Encode(fitted, quality, JpegChromaSubsampling.Quarter, cancellationToken);

            if (encoded.Outcome is not JpegEncodeOutcome.Encoded)
            {
                await renditions.RecordNotCompressedAsync(target, purpose, MediaRenditionReason.TooLarge, cancellationToken);
                notCompressed++;
            }
            else if (encoded.Bytes!.Length >= target.SizeBytes)
            {
                // Discarded before it is stored: a "smaller" copy that is not smaller is only a worse one.
                await renditions.RecordNotCompressedAsync(target, purpose, MediaRenditionReason.NotSmaller, cancellationToken);
                notCompressed++;
            }
            else
            {
                await renditions.StoreAsync(target, purpose, encoded.Bytes, fitted.Width, fitted.Height, cancellationToken);
                ready++;
            }
        }

        return new MediaRenditionProduceSummary(ready, notCompressed);
    }

    private async Task<MediaRenditionProduceSummary> SettleAsync(
        MediaRenditionTarget target, MediaRenditionReason reason, CancellationToken cancellationToken)
    {
        foreach (var purpose in target.Missing)
        {
            await renditions.RecordNotCompressedAsync(target, purpose, reason, cancellationToken);
        }

        return new MediaRenditionProduceSummary(0, target.Missing.Count);
    }

    /// <summary>
    /// Records that the remaining purposes will not be made, and removes anything stored for them.
    /// </summary>
    /// <remarks>
    /// Read again rather than reusing what the failed attempt knew, because that attempt may have recorded
    /// one purpose before it failed on the next. If this fails too it throws, the request is marked failed,
    /// and the backfill finds the picture later: nothing is left claiming an outcome it did not reach.
    /// </remarks>
    private async Task<MediaRenditionProduceSummary> GiveUpAsync(
        MediaRenditionSource source, CancellationToken cancellationToken)
    {
        if (await renditions.FindTargetAsync(source, cancellationToken) is not { } target)
        {
            return MediaRenditionProduceSummary.Nothing;
        }

        foreach (var purpose in target.Missing)
        {
            await renditions.RecordGaveUpAsync(target, purpose, cancellationToken);
        }

        return new MediaRenditionProduceSummary(0, target.Missing.Count);
    }
}
