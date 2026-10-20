using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Data;
using CreatorPantry.Domain.Modules.Nutrition.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Nutrition;

public static class NutritionServiceCollectionExtensions
{
    /// <summary>
    /// The nutrition module's composition root (14.2). Requires
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/>, tenancy, application time
    /// and audit to be registered.
    /// </summary>
    /// <remarks>
    /// A mapping names a platform ingredient and a portion unit, and reads evidence, through the
    /// <strong>Ingredients</strong> and <strong>Measurement</strong> facades, and an estimate reads a recipe version
    /// through <strong>Recipes</strong>, so all three belong beside this call in any host that serves it. A host
    /// missing one fails at resolution rather than quietly at runtime.
    /// </remarks>
    public static IServiceCollection AddNutritionModule(this IServiceCollection services)
    {
        services.AddScoped<INutritionMappingRepository, NutritionMappingRepository>();
        services.AddScoped<INutritionMappingDataLayer, NutritionMappingDataLayer>();
        services.AddScoped<INutritionMappingBusiness, NutritionMappingBusiness>();
        services.AddScoped<INutritionMappingFacade, NutritionMappingFacade>();
        services.AddScoped<IValidator<ConfirmNutritionMappingViewModel>, ConfirmNutritionMappingViewModelValidator>();
        services.AddScoped<IValidator<RemapNutritionMappingViewModel>, RemapNutritionMappingViewModelValidator>();
        services.AddScoped<IValidator<NutritionMappingDecisionViewModel>, NutritionMappingDecisionViewModelValidator>();

        // Estimates (14.3): read a recipe version through the Recipes facade, so a host serving them registers
        // that module too.
        services.AddScoped<INutritionRecipeVersionReader, NutritionRecipeVersionReader>();
        services.AddScoped<INutritionEstimateBusiness, NutritionEstimateBusiness>();
        services.AddScoped<INutritionEstimateFacade, NutritionEstimateFacade>();

        // Dietary and allergen analysis (14.4): the same version reader, the Ingredients module's approved traits,
        // and the Vocabulary module's allergens and profiles.
        services.AddScoped<IDietaryAllergenAnalysisBusiness, DietaryAllergenAnalysisBusiness>();
        services.AddScoped<IDietaryAllergenAnalysisFacade, DietaryAllergenAnalysisFacade>();
        services.AddScoped<IValidator<DietaryAllergenAnalysisViewModel>, DietaryAllergenAnalysisViewModelValidator>();

        return services;
    }
}
