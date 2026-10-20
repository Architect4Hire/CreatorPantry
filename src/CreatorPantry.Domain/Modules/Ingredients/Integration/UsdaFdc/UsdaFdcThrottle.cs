using CreatorPantry.Domain.Managers.Time;

namespace CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;

/// <summary>
/// Remembers, for the whole process, that FoodData Central has throttled this key and until when.
/// </summary>
/// <remarks>
/// <para>
/// A singleton because the block is the key's, not a request's: FDC blocks the key for an hour once its address
/// is over the hourly limit, and every request sent in that hour is refused and counted. Holding off for all
/// callers at once is what stops one busy page from extending the block for everyone.
/// </para>
/// <para>
/// <strong>In memory, and so this process's alone.</strong> A restart forgets a block, and a second replica or
/// the Worker would not see one. The API is the only host that reads evidence today; a shared store comes with
/// the first second reader.
/// </para>
/// </remarks>
public sealed class UsdaFdcThrottle(IClock clock)
{
    private readonly Lock _gate = new();
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    /// <summary>How long is left of a block, or null when there is none.</summary>
    public TimeSpan? Remaining()
    {
        lock (_gate)
        {
            var left = _blockedUntil - clock.UtcNow;
            return left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>Starts or extends a block. Never shortens one already longer.</summary>
    public void Block(TimeSpan duration)
    {
        lock (_gate)
        {
            var until = clock.UtcNow + duration;
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }
    }
}
