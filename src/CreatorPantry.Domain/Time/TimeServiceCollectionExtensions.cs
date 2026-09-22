using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Time;

public static class TimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IClock"/> and <see cref="ITimeZoneConverter"/>. A <see cref="TimeProvider"/>
    /// registered earlier (for example a fake in tests) is preserved.
    /// </summary>
    public static IServiceCollection AddApplicationTime(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ITimeZoneConverter, NodaTimeZoneConverter>();

        return services;
    }
}
