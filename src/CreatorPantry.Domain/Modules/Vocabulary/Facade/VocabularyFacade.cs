using CreatorPantry.Domain.Modules.Vocabulary.Business;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Facade;

internal sealed class VocabularyFacade(
    IValidator<ReferenceQueryViewModel> validator,
    IVocabularyBusiness business,
    CachedPageReader reader) : IVocabularyFacade
{
    public Task<bool> IsUsableAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
        business.IsUsableAsync(catalog, id, cancellationToken);

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

