using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Nutrition.Facade;

/// <summary>
/// A dietary and allergen analysis of one recipe version against selected allergens and profiles (14.4). Read-only,
/// for any member; no route serves it yet (14.6).
/// </summary>
/// <remarks>
/// Its findings are the only facts. Anything that explains them later — a model included — takes them as input
/// and cannot change one (ai.md).
/// </remarks>
public interface IDietaryAllergenAnalysisFacade
{
    Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeAsync(
        Guid recipeId, Guid versionId, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// The same analysis of a version named by the number a creator cites — the form the HTTP route takes (14.6).
    /// An unknown or another workspace's recipe is <c>recipes.recipe.not_found</c>, and a number the recipe does not
    /// have is <c>recipes.version.not_found</c> naming <c>versionNumber</c>.
    /// </summary>
    Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeByNumberAsync(
        Guid recipeId, int versionNumber, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken);
}

internal sealed class DietaryAllergenAnalysisFacade(
    IDietaryAllergenAnalysisBusiness business,
    IValidator<DietaryAllergenSelectionViewModel> validator) : IDietaryAllergenAnalysisFacade
{
    public async Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeAsync(
        Guid recipeId, Guid versionId, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await InvalidAsync(model, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return await business.AnalyzeAsync(recipeId, versionId, model, cancellationToken);
    }

    public async Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeByNumberAsync(
        Guid recipeId, int versionNumber, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await InvalidAsync(model, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return await business.AnalyzeByNumberAsync(recipeId, versionNumber, model, cancellationToken);
    }

    private async Task<OperationResult<DietaryAllergenAnalysisServiceModel>?> InvalidAsync(
        DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? null
            : OperationResult<DietaryAllergenAnalysisServiceModel>.Failure(OperationError.Validation(
                NutritionErrorCodes.AnalysisInvalid,
                "That analysis could not be read as asked.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
    }
}
