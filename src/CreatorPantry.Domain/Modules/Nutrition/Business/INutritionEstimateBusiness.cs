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

    /// <summary>
    /// The version a creator names by number, with the id it resolved to (14.6), or the Recipes module's own refusal —
    /// <c>recipes.recipe.not_found</c> for an unknown or another workspace's recipe, <c>recipes.version.not_found</c>
    /// naming <c>versionNumber</c> — passed through unchanged, so a route answers as every other recipe route does.
    /// </summary>
    Task<OperationResult<NumberedNutritionRecipeVersion>> ReadByNumberAsync(Guid recipeId, int versionNumber, CancellationToken cancellationToken);
}

/// <summary>A recipe version read by its number, and the permanent id that number named.</summary>
public sealed record NumberedNutritionRecipeVersion(Guid VersionId, NutritionRecipeVersion Version);

internal sealed class NutritionRecipeVersionReader(IRecipeFacade recipes) : INutritionRecipeVersionReader
{
    public async Task<NutritionRecipeVersion?> ReadAsync(Guid recipeId, Guid versionId, CancellationToken cancellationToken) =>
        FromResult(await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken));

    public async Task<OperationResult<NumberedNutritionRecipeVersion>> ReadByNumberAsync(
        Guid recipeId, int versionNumber, CancellationToken cancellationToken)
    {
        var snapshot = await recipes.GetSnapshotByNumberAsync(recipeId, versionNumber, cancellationToken);

        return snapshot is { Succeeded: true, Value: { } version }
            ? OperationResult<NumberedNutritionRecipeVersion>.Success(new(version.VersionId, FromSnapshot(version)))
            : OperationResult<NumberedNutritionRecipeVersion>.Failure(snapshot.Error!);
    }

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

    /// <summary>The same estimate of a version named by number (14.6a), refusing as the Recipes module does.</summary>
    Task<OperationResult<NutritionEstimateServiceModel>> EstimateByNumberAsync(
        Guid recipeId, int versionNumber, decimal? servings, CancellationToken cancellationToken);
}

internal sealed class NutritionEstimateBusiness(
    INutritionRecipeVersionReader versions,
    INutritionMappingDataLayer mappings,
    IIngredientFacade ingredients,
    IMeasurementFacade measurement) : INutritionEstimateBusiness
{
    /// <summary>
    /// What a citation's licence reads as when the source row records none (14.7).
    /// </summary>
    /// <remarks>
    /// Unreachable through an approved source, which always carries one. Said rather than null-forgiven
    /// because the estimate's model declares the licence non-null and a screen links it: a missing licence
    /// should read as missing, not as a broken link or the word "null".
    /// </remarks>
    private const string UnknownLicence = "No licence recorded";

    public async Task<OperationResult<NutritionEstimateServiceModel>> EstimateAsync(
        Guid recipeId, Guid versionId, decimal? servings, CancellationToken cancellationToken)
    {
        if (await versions.ReadAsync(recipeId, versionId, cancellationToken) is not { } version)
        {
            return Failure(NutritionErrorCodes.EstimateRecipeNotFound, "That recipe version was not found.");
        }

        return await EstimateAsync(recipeId, versionId, version, servings, cancellationToken);
    }

    public async Task<OperationResult<NutritionEstimateServiceModel>> EstimateByNumberAsync(
        Guid recipeId, int versionNumber, decimal? servings, CancellationToken cancellationToken)
    {
        var read = await versions.ReadByNumberAsync(recipeId, versionNumber, cancellationToken);

        return read.Succeeded
            ? await EstimateAsync(recipeId, read.Value!.VersionId, read.Value.Version, servings, cancellationToken)
            : OperationResult<NutritionEstimateServiceModel>.Failure(read.Error!);
    }

    private async Task<OperationResult<NutritionEstimateServiceModel>> EstimateAsync(
        Guid recipeId, Guid versionId, NutritionRecipeVersion version, decimal? servings, CancellationToken cancellationToken)
    {
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

                        // Not null-forgiven (14.7). `ReferenceSource` allows a null licence, and an approved
                        // source in `EvidenceSourceCatalog` always sets one — but the estimate's own model
                        // declares both non-null and the UI binds the URL straight into an href, so a row
                        // created without one would have reached a creator as the string "null" or a broken
                        // link. There is no licence to invent here, so the gap is said instead.
                        reference.LicenseCode ?? UnknownLicence,
                        reference.LicenseUrl ?? string.Empty,
                        [.. (reference.Nutrients ?? []).Select(nutrient => new NutritionEstimateNutrientInput(nutrient.Code, nutrient.AmountPer100Grams, nutrient.Unit))]);
                    break;
                case NutritionReferenceStatus.NotFound or NutritionReferenceStatus.NoApprovedSource:
                    break;

                // Permanent until configured, not transient: said so, so nobody waits for it to clear.
                case NutritionReferenceStatus.NotConfigured:
                    return Failure(
                        NutritionErrorCodes.EstimateSourceUnavailable,
                        "The nutrition source is not available on this installation, so no estimate was made.");
                default:
                    return Failure(
                        NutritionErrorCodes.EstimateSourceUnavailable,
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
            : Failure(NutritionErrorCodes.EstimateQuantitiesUnprocessable, "The quantities in this recipe are too large to estimate.");
    }

    private static OperationResult<NutritionEstimateServiceModel> Failure(string code, string message) =>
        OperationResult<NutritionEstimateServiceModel>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));
}
