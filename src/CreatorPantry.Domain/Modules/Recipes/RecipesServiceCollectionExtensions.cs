using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Recipes;

public static class RecipesServiceCollectionExtensions
{
    /// <summary>
    /// The recipe module's composition root. Repositories, the data layer and business are registered here;
    /// the facade joins them when its seam is built.
    /// </summary>
    public static IServiceCollection AddRecipesModule(this IServiceCollection services)
    {
        services.AddScoped<IRecipeRepository, RecipeRepository>();
        services.AddScoped<IRecipeSearchRepository, RecipeSearchRepository>();
        services.AddScoped<IRecipeVersionRepository, RecipeVersionRepository>();
        services.AddScoped<IWorkspaceTagRepository, WorkspaceTagRepository>();
        services.AddScoped<IRecipeDataLayer, RecipeDataLayer>();
        services.AddScoped<IRecipeBusiness, RecipeBusiness>();
        services.AddScoped<IRecipeFacade, RecipeFacade>();
        services.AddScoped<IIngredientParsingFacade, IngredientParsingFacade>();
        services.AddScoped<IValidator<ParseIngredientLinesViewModel>, ParseIngredientLinesViewModelValidator>();
        services.AddScoped<IValidator<CreateRecipeViewModel>, CreateRecipeViewModelValidator>();
        services.AddScoped<IValidator<UpdateRecipeViewModel>, UpdateRecipeViewModelValidator>();
        services.AddScoped<IValidator<RecipeSearchViewModel>, RecipeSearchViewModelValidator>();
        services.AddScoped<IValidator<RecipeVersionHistoryViewModel>, RecipeVersionHistoryViewModelValidator>();
        services.AddScoped<IValidator<RecipeVersionComparisonViewModel>, RecipeVersionComparisonViewModelValidator>();
        services.AddScoped<IValidator<ScaleRecipeViewModel>, ScaleRecipeViewModelValidator>();
        services.AddScoped<IValidator<ConvertUnitsViewModel>, ConvertUnitsViewModelValidator>();
        services.AddScoped<IValidator<ConvertTemperatureViewModel>, ConvertTemperatureViewModelValidator>();
        services.AddScoped<IValidator<RecalculateYieldViewModel>, RecalculateYieldViewModelValidator>();
        services.AddScoped<IValidator<NormalizeDisplayViewModel>, NormalizeDisplayViewModelValidator>();
        services.AddScoped<IValidator<RestoreRecipeVersionViewModel>, RestoreRecipeVersionViewModelValidator>();
        services.AddScoped<IValidator<DuplicateRecipeViewModel>, DuplicateRecipeViewModelValidator>();
        services.AddScoped<IValidator<RecipeLifecycleViewModel>, RecipeLifecycleViewModelValidator>();

        return services;
    }
}
