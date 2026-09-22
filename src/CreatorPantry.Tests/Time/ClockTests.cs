using CreatorPantry.Domain.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Time;

public class ClockTests
{
    [Fact]
    public void Clock_returns_the_fixed_instant_in_utc()
    {
        var fixedInstant = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var clock = CreateClock(new FakeTimeProvider(fixedInstant));

        Assert.Equal(fixedInstant, clock.UtcNow);
        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }

    [Fact]
    public void Clock_follows_the_time_provider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var clock = CreateClock(time);

        time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero), clock.UtcNow);
    }

    [Fact]
    public void Registration_resolves_clock_and_converter_as_singletons()
    {
        using var provider = new ServiceCollection().AddApplicationTime().BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IClock>(), provider.GetRequiredService<IClock>());
        Assert.Same(provider.GetRequiredService<ITimeZoneConverter>(), provider.GetRequiredService<ITimeZoneConverter>());
    }

    private static IClock CreateClock(TimeProvider time) =>
        new ServiceCollection()
            .AddSingleton(time)
            .AddApplicationTime()
            .BuildServiceProvider()
            .GetRequiredService<IClock>();
}
