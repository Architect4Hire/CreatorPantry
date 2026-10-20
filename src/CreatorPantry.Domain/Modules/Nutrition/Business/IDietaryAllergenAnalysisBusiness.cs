using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;

namespace CreatorPantry.Domain.Modules.Nutrition.Business;

/// <summary>Gathers what one dietary and allergen analysis needs and runs the decision table (14.4, B-36).</summary>
public interface IDietaryAllergenAnalysisBusiness
{
    Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeAsync(
        Guid recipeId, Guid versionId, DietaryAllergenAnalysisViewModel model, CancellationToken cancellationToken);
}

internal sealed class DietaryAllergenAnalysisBusiness(
    INutritionRecipeVersionReader versions,
    IIngredientFacade ingredients,
    IVocabularyFacade vocabulary,
    IClock clock) : IDietaryAllergenAnalysisBusiness
{
    public async Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeAsync(
        Guid recipeId, Guid versionId, DietaryAllergenAnalysisViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var allergens = await SubjectsAsync(VocabularyCatalog.Allergen, model.AllergenIds, cancellationToken);
        var profiles = await SubjectsAsync(VocabularyCatalog.DietaryProfile, model.DietaryProfileIds, cancellationToken);
        if (allergens is null || profiles is null)
        {
            return OperationResult<DietaryAllergenAnalysisServiceModel>.Failure(OperationError.Validation(
                NutritionErrorCodes.AnalysisSubjectUnprocessable,
                "That analysis could not be run as asked.",
                [(allergens is null ? "allergenIds" : "dietaryProfileIds", "Choose allergens and dietary profiles the catalogue offers.")]));
        }

        if (await versions.ReadAsync(recipeId, versionId, cancellationToken) is not { } version)
        {
            return OperationResult<DietaryAllergenAnalysisServiceModel>.Failure(
                new OperationError(NutritionErrorCodes.EstimateRecipeNotFound, "That recipe version was not found.", new Dictionary<string, string[]>()));
        }

        // The records as of today, UTC: a trait dated later is not yet in effect. Recorded on the result.
        var evaluatedOn = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var ingredientIds = version.Lines.Where(line => line.IngredientId is not null).Select(line => line.IngredientId!.Value).Distinct().ToList();

        var evidence = await ingredients.FindApprovedTraitsAsync(
            ingredientIds, [.. allergens.Select(subject => subject.Id)], [.. profiles.Select(subject => subject.Id)], evaluatedOn, cancellationToken);

        return OperationResult<DietaryAllergenAnalysisServiceModel>.Success(DietaryAllergenAnalyzer.Analyze(
            recipeId, versionId, version.VersionNumber, version.Lines, allergens, profiles, evidence, evaluatedOn));
    }

    /// <summary>Each id as a usable catalogue entry with its name, or null when any one is not.</summary>
    private async Task<List<AnalysisSubject>?> SubjectsAsync(
        VocabularyCatalog catalog, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var subjects = new List<AnalysisSubject>();
        foreach (var id in ids)
        {
            if (!await vocabulary.IsUsableAsync(catalog, id, cancellationToken)
                || await vocabulary.GetDisplayNameAsync(catalog, id, cancellationToken) is not { } name)
            {
                return null;
            }

            subjects.Add(new AnalysisSubject(id, name));
        }

        return subjects;
    }
}
