using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// An <see cref="IRecipeBusiness"/> whose every member refuses, for a test about one of them.
/// </summary>
/// <remarks>
/// The same arrangement and the same argument as <see cref="StubRecipeDataLayerBase"/>: the interface carries
/// every recipe operation there is, a test about the transition seam has no business saying anything about the
/// rest, and refusing rather than returning a default is what keeps a test from passing down a path nobody
/// meant it to reach.
/// </remarks>
internal abstract class StubRecipeBusinessBase : IRecipeBusiness
{
    public virtual Task<IReadOnlyList<RecipeLinkCandidateServiceModel>> ListTitlesAsync(
        IReadOnlyList<Guid> recipeIds, CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CanonicalCreateRecipe input,
        MeasurementDimension? yieldUnitDimension,
        IReadOnlyDictionary<Guid, MeasurementDimension> ingredientUnitDimensions,
        RecipeVersionOrigin origin,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(
        Guid recipeId, CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeSnapshotServiceModel>> GetSnapshotAsync(
        Guid recipeId, Guid versionId, CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
        Guid recipeId,
        CanonicalRecipePatch patch,
        MeasurementDimension? submittedYieldUnitDimension,
        IReadOnlyDictionary<Guid, MeasurementDimension> ingredientUnitDimensions,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeDetailServiceModel>> ApplyProposedChangesAsync(
        Guid recipeId,
        Guid expectedVersionId,
        Guid aiProposalId,
        IReadOnlyList<ProposedRecipeChange> changes,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<RecipeSearchPageServiceModel> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeVersionHistoryPageResult>> GetVersionHistoryAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid recipeId,
        int fromVersionNumber,
        int toVersionNumber,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeScalingResultServiceModel>> ScaleAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeScalingRequest request,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeUnitConversionResultServiceModel>> ConvertUnitsAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeUnitConversionRequest request,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeTemperatureConversionResultServiceModel>> ConvertTemperatureAsync(
        Guid recipeId,
        int sourceVersionNumber,
        ConvertTemperatureViewModel model,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeYieldReconciliationResultServiceModel>> RecalculateYieldAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeYieldReconciliationRequest request,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeQuantityDisplayResultServiceModel>> NormalizeDisplayAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeQuantityDisplayRequest request,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeDetailServiceModel>> RestoreVersionAsync(
        Guid recipeId,
        int versionNumber,
        CanonicalRestoreRecipeVersion request,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<CreatedRecipeServiceModel>> DuplicateAsync(
        Guid recipeId,
        CanonicalDuplicateRecipe request,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
        Guid recipeId,
        RecipeStatus target,
        string? reason,
        RecipeReadinessServiceModel? readiness,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken) => throw Unused();

    private static NotSupportedException Unused() =>
        new("This test's business layer was asked for something it does not stand in for.");
}
