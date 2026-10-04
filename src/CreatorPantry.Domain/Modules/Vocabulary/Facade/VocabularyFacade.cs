using CreatorPantry.Domain.Modules.Vocabulary.Business;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Facade;

internal sealed class VocabularyFacade(
    IValidator<ReferenceQueryViewModel> validator,
    IVocabularyBusiness business,
    CachedPageReader reader,
    IApplicationCache cache) : IVocabularyFacade
{
    public Task<bool> IsUsableAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
        business.IsUsableAsync(catalog, id, cancellationToken);

    public Task<string?> GetDisplayNameAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
        business.GetDisplayNameAsync(catalog, id, cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListFoodCategoriesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
        ReadAsync(model, "food-categories", business.ListFoodCategoriesAsync, cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCuisinesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
        ReadAsync(model, "cuisines", business.ListCuisinesAsync, cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCoursesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
        ReadAsync(model, "courses", business.ListCoursesAsync, cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<CookingTechniqueServiceModel>>> ListTechniquesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
        ReadAsync(model, "techniques", business.ListTechniquesAsync, cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListEquipmentTypesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
        ReadAsync(model, "equipment-types", business.ListEquipmentTypesAsync, cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>> ListDietaryProfilesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
        ReadAsync(model, "dietary-profiles", business.ListDietaryProfilesAsync, cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>> ListAllergensAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
        ReadAsync(model, "allergens", business.ListAllergensAsync, cancellationToken);

    public Task<IReadOnlyList<ReferenceEntryServiceModel>> ListActiveCuisinesAsync(CancellationToken cancellationToken) =>
        ReadAllAsync("cuisines", business.ListActiveCuisinesAsync, cancellationToken);

    public Task<IReadOnlyList<ReferenceEntryServiceModel>> ListActiveCoursesAsync(CancellationToken cancellationToken) =>
        ReadAllAsync("courses", business.ListActiveCoursesAsync, cancellationToken);

    public Task<IReadOnlyList<CookingTechniqueServiceModel>> ListActiveTechniquesAsync(CancellationToken cancellationToken) =>
        ReadAllAsync("techniques", business.ListActiveTechniquesAsync, cancellationToken);

    /// <summary>
    /// One whole catalogue, cached on a global key.
    /// </summary>
    /// <remarks>
    /// Its own key space — <c>all</c> rather than a query segment — so a full read and a page of the same resource
    /// can never be served to each other. <see cref="CacheKeys.Global"/> as every reference read uses, which has
    /// no overload accepting a workspace, so this structurally cannot be cached under one tenant's key.
    /// A list is cached as an array: <see cref="IApplicationCache"/> serializes values, and an interface-typed
    /// list would not round-trip.
    /// </remarks>
    private async Task<IReadOnlyList<TModel>> ReadAllAsync<TModel>(
        string resource,
        Func<CancellationToken, Task<IReadOnlyList<TModel>>> read,
        CancellationToken cancellationToken)
    {
        var key = CacheKeys.Global(
            ReferencePolicy.CacheCategory, $"{resource}:{ReferencePolicy.CacheSchemaVersion}:all");

        if (await cache.GetAsync<TModel[]>(key, cancellationToken) is { } cached)
        {
            return cached;
        }

        var all = await read(cancellationToken);
        await cache.SetAsync(key, all.ToArray(), ReferencePolicy.CacheLifetime, cancellationToken);

        return all;
    }

    /// <remarks>
    /// The resource literals above are the cache-key segments the seam used before the module split, kept
    /// character for character so no cached key changes meaning (4A.5).
    /// </remarks>
    private Task<OperationResult<CursorPageServiceModel<TModel>>> ReadAsync<TModel>(
        ReferenceQueryViewModel model,
        string resource,
        Func<ReferenceQuery, CancellationToken, Task<CursorPageServiceModel<TModel>>> read,
        CancellationToken cancellationToken) =>
        reader.ReadAsync(
            validator, model, resource, VocabularyQueryFactory.TryCreate,
            query => query.CacheKeySegment, read, cancellationToken);
}

