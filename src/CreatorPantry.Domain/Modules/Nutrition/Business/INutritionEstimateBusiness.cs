using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Data;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Business;

/// <summary>One recipe version's lines and yield, as an estimate reads them.</summary>
public sealed record NutritionRecipeVersion(
    int VersionNumber,
    IReadOnlyList<NutritionEstimateLineInput> Lines,
    decimal? ServingCount,
    decimal? YieldQuantity,
    decimal? ServingSize);

/// <summary>
/// Reads one explicitly named recipe version for an estimate, through the Recipes module's facade.
/// </summary>
/// <remarks>
/// An estimate is of a version, never of the recipe as it is being edited: a version is immutable, so the same
/// version, mappings and source versions always give the same estimate.
/// </remarks>
public interface INutritionRecipeVersionReader
{
    /// <summary>The version, or null when this workspace has no such recipe or version.</summary>
    Task<NutritionRecipeVersion?> ReadAsync(Guid recipeId, Guid versionId, CancellationToken cancellationToken);
}

internal sealed class NutritionRecipeVersionReader(IRecipeFacade recipes) : INutritionRecipeVersionReader
{
    public async Task<NutritionRecipeVersion?> ReadAsync(Guid recipeId, Guid versionId, CancellationToken cancellationToken) =>
        FromResult(await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken));

    /// <summary>
    /// The version, or null for any refusal — another workspace's recipe included, which the Recipes module answers
    /// as not found exactly as it answers one that does not exist.
    /// </summary>
    internal static NutritionRecipeVersion? FromResult(OperationResult<RecipeSnapshotServiceModel> snapshot) =>
        snapshot is { Succeeded: true, Value: { } version } ? FromSnapshot(version) : null;

    /// <summary>The snapshot's lines in the order the creator sees them — group by group — and its yield.</summary>
    internal static NutritionRecipeVersion FromSnapshot(RecipeSnapshotServiceModel snapshot) => new(
        snapshot.VersionNumber,
        [.. snapshot.Document.IngredientGroups
            .OrderBy(group => group.SortOrder)
            .SelectMany(group => group.Ingredients.OrderBy(line => line.SortOrder))
            .Select(line => new NutritionEstimateLineInput(
                line.Id, line.DisplayText, line.Quantity, line.QuantityUpper, line.MeasurementUnitId, line.IngredientId, line.IsOptional))],
        snapshot.Document.Recipe.ServingCount,
        snapshot.Document.Recipe.YieldQuantity,
        snapshot.Document.Recipe.ServingSize);
}

/// <summary>Gathers what one estimate needs and computes it (14.3, B-35).</summary>
public interface INutritionEstimateBusiness
{
    /// <param name="servings">A serving count that overrides the version's own; null to use the version's.</param>
    Task<OperationResult<NutritionEstimateServiceModel>> EstimateAsync(
        Guid recipeId, Guid versionId, decimal? servings, CancellationToken cancellationToken);
}

internal sealed class NutritionEstimateBusiness(
    INutritionRecipeVersionReader versions,
    INutritionMappingDataLayer mappings,
    IIngredientFacade ingredients,
    IMeasurementFacade measurement) : INutritionEstimateBusiness
{
    public async Task<OperationResult<NutritionEstimateServiceModel>> EstimateAsync(
        Guid recipeId, Guid versionId, decimal? servings, CancellationToken cancellationToken)
    {
        if (await versions.ReadAsync(recipeId, versionId, cancellationToken) is not { } version)
        {
            return Failure(NutritionErrorCodes.EstimateRecipeNotFound, "That recipe version was not found.");
        }

        var ingredientIds = version.Lines.Where(line => line.IngredientId is not null).Select(line => line.IngredientId!.Value).Distinct().ToList();

        // This workspace's own mappings, through the filtered data layer: another workspace's decisions never count.
        var mapped = (await mappings.ListAsync(ingredientIds, cancellationToken))
            .Select(mapping => new NutritionEstimateMappingInput(
                mapping.Id, mapping.IngredientId, mapping.Status, mapping.SourceCode, mapping.ExternalRecordId, mapping.SourceVersion,
                mapping.PortionMeasurementUnitId, mapping.PortionAmount, mapping.PortionGrams, mapping.PortionOrigin))
            .ToDictionary(mapping => mapping.IngredientId);

        var unitIds = version.Lines.Select(line => line.MeasurementUnitId)
            .Concat(mapped.Values.Select(mapping => mapping.PortionMeasurementUnitId))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var units = (await measurement.FindUnitsByIdsAsync(unitIds, cancellationToken))
            .ToDictionary(unit => unit.Id, unit => new NutritionEstimateUnitInput(unit.Id, unit.Dimension, unit.BaseUnitFactor));

        // Each confirmed record as the source reports it now. A record the source withdrew is simply absent, and the
        // calculator says so per line; a source that cannot be asked fails the estimate rather than shrink it.
        var references = new Dictionary<(string, string), NutritionEstimateReferenceInput>();
        foreach (var mapping in mapped.Values.Where(mapping => mapping.Status is NutritionMappingStatus.Confirmed))
        {
            var key = (mapping.SourceCode!, mapping.ExternalRecordId!);
            if (references.ContainsKey(key))
            {
                continue;
            }

            var reference = await ingredients.GetNutritionReferenceAsync(key.Item1, key.Item2, cancellationToken);
            switch (reference.Status)
            {
                case NutritionReferenceStatus.Found:
                    references[key] = new NutritionEstimateReferenceInput(
                        reference.SourceCode!,
                        reference.ExternalRecordId!,
                        reference.SourceVersion!,
                        reference.Citation!,
                        reference.LicenseCode!,
                        reference.LicenseUrl!,
                        [.. (reference.Nutrients ?? []).Select(nutrient => new NutritionEstimateNutrientInput(nutrient.Code, nutrient.AmountPer100Grams, nutrient.Unit))]);
                    break;
                case NutritionReferenceStatus.NotFound or NutritionReferenceStatus.NoApprovedSource:
                    break;
                default:
                    return Failure(
                        NutritionErrorCodes.SourceUnavailable,
                        "The nutrition source could not be asked just then, so no estimate was made.");
            }
        }

        var outcome = NutritionEstimateCalculator.Calculate(new NutritionEstimateInput(
            recipeId,
            versionId,
            version.VersionNumber,
            version.Lines,
            units,
            mapped,
            references,
            new NutritionEstimateServingsInput(servings, version.ServingCount, version.YieldQuantity, version.ServingSize)));

        return outcome.Estimate is { } estimate
            ? OperationResult<NutritionEstimateServiceModel>.Success(estimate)
            : Failure(NutritionErrorCodes.EstimateOutOfRange, "The quantities in this recipe are too large to estimate.");
    }

    private static OperationResult<NutritionEstimateServiceModel> Failure(string code, string message) =>
        OperationResult<NutritionEstimateServiceModel>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));
}
