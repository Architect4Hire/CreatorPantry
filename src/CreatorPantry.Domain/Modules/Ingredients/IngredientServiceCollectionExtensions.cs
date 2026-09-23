using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Business;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
