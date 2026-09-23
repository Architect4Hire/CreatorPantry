using CreatorPantry.Domain.Managers.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Managers.Reference;

public static class ReferenceSeedServiceCollectionExtensions
{
    /// <summary>
    /// Registers the global reference catalogue seeder. Run it only where the schema exists (the migration
    /// service, after migrations).
    /// </summary>
    /// <param name="includeDevelopmentSampleData">
    /// Whether to include the sample ingredient set and its illustrative densities and traits. Pass
    /// <c>false</c> in Production: those rows cite a development seed source and are not reference material.
    /// The unit and vocabulary catalogue is seeded either way.
    /// </param>
    public static IServiceCollection AddReferenceSeeding(
        this IServiceCollection services,
        bool includeDevelopmentSampleData)
    {
        services.AddSingleton(new ReferenceSeedOptions(includeDevelopmentSampleData));
        services.AddScoped<IDataSeeder, ReferenceDataSeeder>();

        return services;
    }
}
