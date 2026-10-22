using CreatorPantry.Domain.Managers.Limits;
using StackExchange.Redis;

namespace CreatorPantry.ApiService.RateLimiting;

public static class RateLimitingRegistration
{
    /// <summary>
    /// Binds <c>Limits</c> and registers the rate-limit store: Redis when the cache connection is configured, so
    /// every API instance shares one bucket per caller, with this instance's own buckets as the fallback; this
    /// instance's buckets alone when there is no Redis at all.
    /// </summary>
    public static IHostApplicationBuilder AddOperationLimits(this IHostApplicationBuilder builder)
    {
        builder.Services.AddLimits(builder.Configuration);
        builder.Services.AddOptions<LimitsOptions>()
            .Validate(
                options => options.Rates.Values.All(rule =>
                    (rule.User is null || rule.User.IsValid) && (rule.Workspace is null || rule.Workspace.IsValid)),
                "Every configured rate needs a burst, a refill and a period of at least one.")
            .ValidateOnStart();

        builder.Services.AddSingleton<InMemoryRateLimitStore>();
        builder.Services.AddSingleton<IRateLimitStore>(services =>
            services.GetService<IConnectionMultiplexer>() is { } redis
                ? new RedisRateLimitStore(
                    redis,
                    services.GetRequiredService<InMemoryRateLimitStore>(),
                    services.GetRequiredService<ILogger<RedisRateLimitStore>>())
                : services.GetRequiredService<InMemoryRateLimitStore>());

        return builder;
    }
}
