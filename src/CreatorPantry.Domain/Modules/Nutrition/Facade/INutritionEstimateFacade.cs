using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Nutrition.Facade;

/// <summary>
/// A nutrition estimate for one recipe version (14.3). Read-only: it writes nothing, not even to a mapping it finds
/// out of date. <c>POST .../versions/{versionNumber}/analysis/nutrition</c> serves it by number (14.6a, B-39).
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

    /// <summary>
    /// The same estimate of a version named by the number a creator cites — the form the HTTP route takes (14.6a).
    /// An unknown or another workspace's recipe is <c>recipes.recipe.not_found</c>, and a number the recipe does not
    /// have is <c>recipes.version.not_found</c> naming <c>versionNumber</c>.
    /// </summary>
    Task<OperationResult<NutritionEstimateServiceModel>> EstimateByNumberAsync(
        Guid recipeId, int versionNumber, NutritionEstimateRequestViewModel model, CancellationToken cancellationToken);
}

internal sealed class NutritionEstimateFacade(
    INutritionEstimateBusiness business,
    IValidator<NutritionEstimateRequestViewModel> validator) : INutritionEstimateFacade
{
    public async Task<OperationResult<NutritionEstimateServiceModel>> EstimateAsync(
        Guid recipeId, Guid versionId, decimal? servings, CancellationToken cancellationToken) =>
        await InvalidAsync(new NutritionEstimateRequestViewModel { Servings = servings }, cancellationToken)
            ?? await business.EstimateAsync(recipeId, versionId, servings, cancellationToken);

    public async Task<OperationResult<NutritionEstimateServiceModel>> EstimateByNumberAsync(
        Guid recipeId, int versionNumber, NutritionEstimateRequestViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        return await InvalidAsync(model, cancellationToken)
            ?? await business.EstimateByNumberAsync(recipeId, versionNumber, model.Servings, cancellationToken);
    }

    private async Task<OperationResult<NutritionEstimateServiceModel>?> InvalidAsync(
        NutritionEstimateRequestViewModel model, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? null
            : OperationResult<NutritionEstimateServiceModel>.Failure(OperationError.Validation(
                NutritionErrorCodes.EstimateInvalid,
                "That serving count could not be used.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
    }
}
