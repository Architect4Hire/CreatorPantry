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
        Guid recipeId, Guid versionId, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken);

    /// <summary>The same analysis of a version named by number (14.6), refusing as the Recipes module does.</summary>
    Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeByNumberAsync(
        Guid recipeId, int versionNumber, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken);
}

internal sealed class DietaryAllergenAnalysisBusiness(
    INutritionRecipeVersionReader versions,
    IIngredientFacade ingredients,
    IVocabularyFacade vocabulary,
    IClock clock) : IDietaryAllergenAnalysisBusiness
{
    public async Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeAsync(
        Guid recipeId, Guid versionId, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var allergens = await SubjectsAsync(VocabularyCatalog.Allergen, model.AllergenIds, cancellationToken);
        var profiles = await SubjectsAsync(VocabularyCatalog.DietaryProfile, model.DietaryProfileIds, cancellationToken);
        if (allergens is null || profiles is null)
        {
            return SubjectUnprocessable(allergens is null);
        }

        if (await versions.ReadAsync(recipeId, versionId, cancellationToken) is not { } version)
        {
            return OperationResult<DietaryAllergenAnalysisServiceModel>.Failure(
                new OperationError(NutritionErrorCodes.EstimateRecipeNotFound, "That recipe version was not found.", new Dictionary<string, string[]>()));
        }

        return await AnalyzeAsync(recipeId, versionId, version, allergens, profiles, cancellationToken);
    }

    public async Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeByNumberAsync(
        Guid recipeId, int versionNumber, DietaryAllergenSelectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The subjects first, as by id: a request naming something the catalogue does not offer is refused before
        // any recipe is read.
        var allergens = await SubjectsAsync(VocabularyCatalog.Allergen, model.AllergenIds, cancellationToken);
        var profiles = await SubjectsAsync(VocabularyCatalog.DietaryProfile, model.DietaryProfileIds, cancellationToken);
        if (allergens is null || profiles is null)
        {
            return SubjectUnprocessable(allergens is null);
        }

        var read = await versions.ReadByNumberAsync(recipeId, versionNumber, cancellationToken);
        if (!read.Succeeded)
        {
            return OperationResult<DietaryAllergenAnalysisServiceModel>.Failure(read.Error!);
        }

        return await AnalyzeAsync(recipeId, read.Value!.VersionId, read.Value.Version, allergens, profiles, cancellationToken);
    }

    private async Task<OperationResult<DietaryAllergenAnalysisServiceModel>> AnalyzeAsync(
        Guid recipeId,
        Guid versionId,
        NutritionRecipeVersion version,
        List<AnalysisSubject> allergens,
        List<AnalysisSubject> profiles,
        CancellationToken cancellationToken)
    {
        // The records as of today, UTC: a trait dated later is not yet in effect. Recorded on the result.
        var evaluatedOn = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var ingredientIds = version.Lines.Where(line => line.IngredientId is not null).Select(line => line.IngredientId!.Value).Distinct().ToList();

        var evidence = await ingredients.FindApprovedTraitsAsync(
            ingredientIds, [.. allergens.Select(subject => subject.Id)], [.. profiles.Select(subject => subject.Id)], evaluatedOn, cancellationToken);

        return OperationResult<DietaryAllergenAnalysisServiceModel>.Success(DietaryAllergenAnalyzer.Analyze(
            recipeId, versionId, version.VersionNumber, version.Lines, allergens, profiles, evidence, evaluatedOn));
    }

    private static OperationResult<DietaryAllergenAnalysisServiceModel> SubjectUnprocessable(bool allergens) =>
        OperationResult<DietaryAllergenAnalysisServiceModel>.Failure(OperationError.Validation(
            NutritionErrorCodes.AnalysisSubjectUnprocessable,
            "That analysis could not be run as asked.",
            [(allergens ? "allergenIds" : "dietaryProfileIds", "Choose allergens and dietary profiles the catalogue offers.")]));

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
