using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Publishing;

public static class PublishingServiceCollectionExtensions
{
    /// <summary>
    /// The publishing profile seam (PUB-001).
    /// </summary>
    /// <remarks>
    /// The gateway is a <c>TryAdd</c> of the one that says "not configured": a host that has a provider
    /// registers its adapter as <see cref="IPublishingProfileGateway"/> and that registration wins, whichever
    /// side of this call it is made on. A host that has none starts cleanly and answers the same stable way.
    /// </remarks>
    public static IServiceCollection AddPublishingModule(this IServiceCollection services)
    {
        services.TryAddScoped<IPublishingProfileGateway, UnconfiguredPublishingProfileGateway>();

        services.AddScoped<IPublishingProfileRepository, PublishingProfileRepository>();
        services.AddScoped<IPublishingProfileDataLayer, PublishingProfileDataLayer>();
        services.AddScoped<IPublishingProfileBusiness, PublishingProfileBusiness>();
        services.AddScoped<IPublishingProfileFacade, PublishingProfileFacade>();

        return services;
    }
}
