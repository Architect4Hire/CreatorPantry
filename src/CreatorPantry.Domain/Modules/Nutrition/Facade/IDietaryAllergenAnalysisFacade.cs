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
        Guid recipeId, Guid versionId, DietaryAllergenAnalysisViewModel model, CancellationToken cancellationToken);
}

internal sealed class DietaryAllergenAnalysisFacade(
    IDietaryAllergenAnalysisBusiness business,
    IValidator<DietaryAllergenAnalysisViewModel> validator) : IDietaryAllergenAnalysisFacade
{
    public async Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeAsync(
        Guid recipeId, Guid versionId, DietaryAllergenAnalysisViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<DietaryAllergenAnalysisServiceModel>.Failure(OperationError.Validation(
                NutritionErrorCodes.AnalysisInvalid,
                "That analysis could not be read as asked.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return await business.AnalyzeAsync(recipeId, versionId, model, cancellationToken);
    }
}
