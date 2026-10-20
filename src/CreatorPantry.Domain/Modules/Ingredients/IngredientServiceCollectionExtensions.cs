using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Business;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Ingredients;

public static class IngredientServiceCollectionExtensions
{
    /// <inheritdoc cref="Measurement.MeasurementServiceCollectionExtensions.AddMeasurementModule"/>
    public static IServiceCollection AddIngredientModule(this IServiceCollection services)
    {
        services.AddPaging();
        services.AddScoped<IValidator<IngredientQueryViewModel>, IngredientQueryViewModelValidator>();
        services.AddScoped<IIngredientFacade, IngredientFacade>();
        services.AddScoped<IIngredientBusiness, IngredientBusiness>();
        services.AddScoped<IIngredientDataLayer, IngredientDataLayer>();
        services.AddScoped<IIngredientRepository, IngredientRepository>();

        // Evidence sources (14.1). "Not configured" unless a host adds a provider (AddUsdaFdcEvidence): a TryAdd,
        // so a provider registered before this stops it being added, and one registered after is resolved instead.
        services.TryAddScoped<IReferenceEvidenceGateway, UnconfiguredReferenceEvidenceGateway>();
        services.AddScoped<IReferenceEvidenceDataLayer, ReferenceEvidenceDataLayer>();

        return services;
    }
}
