using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Managers.Idempotency;

public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>Registers the idempotency seam. Requires the DbContext and <c>AddApplicationTime</c>.</summary>
    public static IServiceCollection AddIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .Validate(options => IdempotencyOptions.IsValidKey(options.FingerprintKey),
                "Idempotency:FingerprintKey must be a base64 secret of at least 32 bytes.")
            .ValidateOnStart();

        services.AddScoped<IIdempotentCommandExecutor, IdempotentCommandExecutor>();
        services.AddScoped<IIdempotencyBusiness, IdempotencyBusiness>();
        services.AddScoped<IIdempotencyDataLayer, IdempotencyDataLayer>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

        return services;
    }
}
