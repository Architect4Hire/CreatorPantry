using System.Buffers;

namespace CreatorPantry.Domain.Managers.FileInspection;

/// <summary>
/// Copies a stranger's bytes under a cap, stopping at the first byte past it rather than after the whole of it.
/// </summary>
/// <remarks>
/// The request size limit bounds what a browser can send and a provider timeout bounds how long it can talk, but
/// neither bounds what one upload or one response may hold in memory once it is being read. This does, for every
/// path that has to have the whole file in hand to inspect it (SEC-004).
/// </remarks>
public static class BoundedCopy
{
    private const int ChunkBytes = 81920;

    /// <summary>
    /// Copies from <paramref name="source"/>'s current position to its end into <paramref name="destination"/>.
    /// False when there was more than <paramref name="maxBytes"/>; the destination then holds a partial copy the
    /// caller discards.
    /// </summary>
    public static async Task<bool> TryCopyAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        // A stream that knows its length answers without a single byte being copied.
        if (source.CanSeek && source.Length - source.Position > maxBytes)
        {
            return false;
        }

        var chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);

        try
        {
            long total = 0;
            int read;

            // Each read asks for at most one byte more than may still be kept, so proving the cap was passed
            // never pulls a whole chunk past it.
            while ((read = await source.ReadAsync(
                chunk.AsMemory(0, (int)Math.Min(ChunkBytes, maxBytes - total + 1)), cancellationToken)) > 0)
            {
                total += read;

                if (total > maxBytes)
                {
                    return false;
                }

                await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
