using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Facade;

/// <summary>
/// A nutrition estimate for one recipe version (14.3). Read-only: it writes nothing, not even to a mapping it finds
/// out of date. No route serves it yet (14.6a).
/// </summary>
/// <remarks>
/// Any member may read an estimate, as any member may read the recipe. The workspace is the resolved context's, so
/// only this workspace's recipe and this workspace's mappings are ever read.
/// </remarks>
public interface INutritionEstimateFacade
{
    /// <param name="servings">
    /// A serving count to divide by instead of the version's own, greater than zero and at most
    /// <see cref="NutritionEstimatePolicy.MaxRequestedServings"/>; null to use the version's.
    /// </param>
    Task<OperationResult<NutritionEstimateServiceModel>> EstimateAsync(
        Guid recipeId, Guid versionId, decimal? servings, CancellationToken cancellationToken);
}

internal sealed class NutritionEstimateFacade(INutritionEstimateBusiness business) : INutritionEstimateFacade
{
    public Task<OperationResult<NutritionEstimateServiceModel>> EstimateAsync(
        Guid recipeId, Guid versionId, decimal? servings, CancellationToken cancellationToken)
    {
        if (servings is { } requested && (requested <= 0m || requested > NutritionEstimatePolicy.MaxRequestedServings || requested.Scale > 4))
        {
            return Task.FromResult(OperationResult<NutritionEstimateServiceModel>.Failure(OperationError.Validation(
                NutritionErrorCodes.EstimateInvalid,
                "That serving count could not be used.",
                [("servings", $"Give a number of servings above zero and no more than {NutritionEstimatePolicy.MaxRequestedServings}, to at most four decimal places.")])));
        }

        return business.EstimateAsync(recipeId, versionId, servings, cancellationToken);
    }
}
