using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;

namespace CreatorPantry.Domain.Modules.Nutrition.Business;

/// <summary>Reads the parts of one recipe version the food-safety checks need (14.5).</summary>
public interface IFoodSafetyRecipeVersionReader
{
    /// <summary>The version, or null when this workspace has no such recipe or version.</summary>
    Task<FoodSafetyRecipeInput?> ReadAsync(Guid recipeId, Guid versionId, CancellationToken cancellationToken);
}

internal sealed class FoodSafetyRecipeVersionReader(IRecipeFacade recipes) : IFoodSafetyRecipeVersionReader
{
    public async Task<FoodSafetyRecipeInput?> ReadAsync(Guid recipeId, Guid versionId, CancellationToken cancellationToken) =>
        FromResult(await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken));

    /// <summary>
    /// The version, or null for any refusal — another workspace's recipe included, which the Recipes module answers
    /// as not found exactly as it answers one that does not exist.
    /// </summary>
    internal static FoodSafetyRecipeInput? FromResult(OperationResult<RecipeSnapshotServiceModel> snapshot) =>
        snapshot is { Succeeded: true, Value: { } version } ? FromSnapshot(version) : null;

    /// <summary>Lines, steps and equipment in the order the creator sees them, group by group.</summary>
    internal static FoodSafetyRecipeInput FromSnapshot(RecipeSnapshotServiceModel snapshot)
    {
        var document = snapshot.Document;

        return new FoodSafetyRecipeInput(
            snapshot.VersionNumber,
            document.Recipe.Title,
            document.Recipe.Description,
            document.Recipe.Headnote,
            document.Recipe.Notes,
            document.Recipe.StorageNotes,
            document.Recipe.PrimaryTechniqueId,
            [.. document.IngredientGroups
                .OrderBy(group => group.SortOrder)
                .SelectMany(group => group.Ingredients.OrderBy(line => line.SortOrder))
                .Select(line => new FoodSafetyLineInput(line.Id, line.DisplayText, line.PreparationNote, line.IngredientId))],
            [.. document.InstructionGroups
                .OrderBy(group => group.SortOrder)
                .SelectMany(group => group.Steps.OrderBy(step => step.SortOrder))
                .Select(step => new FoodSafetyStepInput(step.Id, step.Text, step.Note, step.TechniqueId))],
            [.. document.Equipment
                .OrderBy(item => item.SortOrder)
                .Select(item => new FoodSafetyEquipmentInput(item.Id, item.DisplayText, item.Note))]);
    }
}

/// <summary>Gathers what the food-safety checks need for one version and runs them (14.5, B-37).</summary>
public interface IFoodSafetyWarningBusiness
{
    Task<OperationResult<FoodSafetyWarningsServiceModel>> DetectAsync(
        Guid recipeId, Guid versionId, CancellationToken cancellationToken);
}

internal sealed class FoodSafetyWarningBusiness(
    IFoodSafetyRecipeVersionReader versions,
    IIngredientFacade ingredients,
    IVocabularyFacade vocabulary) : IFoodSafetyWarningBusiness
{
    public async Task<OperationResult<FoodSafetyWarningsServiceModel>> DetectAsync(
        Guid recipeId, Guid versionId, CancellationToken cancellationToken)
    {
        if (await versions.ReadAsync(recipeId, versionId, cancellationToken) is not { } version)
        {
            return OperationResult<FoodSafetyWarningsServiceModel>.Failure(
                new OperationError(NutritionErrorCodes.EstimateRecipeNotFound, "That recipe version was not found.", new Dictionary<string, string[]>()));
        }

        var techniques = (await vocabulary.ListActiveTechniquesAsync(cancellationToken)).ToDictionary(technique => technique.Id);

        var ingredientIds = version.Lines.Where(line => line.IngredientId is not null).Select(line => line.IngredientId!.Value).Distinct().ToList();
        var categories = (await ingredients.ListCategoriesAsync(ingredientIds, cancellationToken)).ToDictionary(ingredient => ingredient.IngredientId);

        return OperationResult<FoodSafetyWarningsServiceModel>.Success(FoodSafetyWarningDetector.Detect(
            recipeId, versionId, version, techniques, categories, Evidence(ingredients.DescribeEvidenceCoverage(ReferenceEvidenceKind.FoodSafety))));
    }

    /// <summary>
    /// What every warning is checked against. Nothing is approved for food safety (B-33), so on every host today
    /// this is "no approved source" and every warning says verification is required.
    /// </summary>
    /// <remarks>
    /// An approved source that this host could reach would still not be cited: nothing reads a food-safety
    /// reference yet, so there is no citation to give. That is reported as unavailable — never as cited, and never
    /// as nothing to check — until a reviewed decision approves a source and a reader for it.
    /// </remarks>
    internal static FoodSafetyEvidenceInput Evidence(ReferenceEvidenceCoverageServiceModel coverage) =>
        FoodSafetyEvidenceInput.Uncited(coverage.Availability switch
        {
            ReferenceEvidenceAvailability.NoApprovedSource => FoodSafetyEvidenceStatus.NoApprovedSource,
            ReferenceEvidenceAvailability.NotConfigured => FoodSafetyEvidenceStatus.NotConfigured,
            _ => FoodSafetyEvidenceStatus.Unavailable,
        });
}
