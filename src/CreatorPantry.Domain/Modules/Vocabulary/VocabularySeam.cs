using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary;

/// <summary>
/// Application boundary for the controlled vocabularies a recipe is described with: cuisines, courses,
/// techniques, equipment types, food categories, dietary profiles, and allergens.
/// </summary>
/// <remarks>
/// Global and read-only. No workspace, no user, no membership — the platform data zone, readable before any
/// workspace is resolved (tenancy.md).
/// </remarks>
public interface IVocabularyFacade
{
    Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListFoodCategoriesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCuisinesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCoursesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<CookingTechniqueServiceModel>>> ListTechniquesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListEquipmentTypesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>> ListDietaryProfilesAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>> ListAllergensAsync(
        ReferenceQueryViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVocabularyFacade"/>
public interface IVocabularyBusiness
{
    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListFoodCategoriesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCuisinesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCoursesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<CookingTechniqueServiceModel>> ListTechniquesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListEquipmentTypesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListDietaryProfilesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListAllergensAsync(ReferenceQuery query, CancellationToken cancellationToken);
}

/// <inheritdoc cref="Measurement.IMeasurementDataLayer"/>
public interface IVocabularyDataLayer
{
    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListFoodCategoriesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCuisinesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCoursesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<CookingTechniqueRecord> Rows, bool HasMore)> ListTechniquesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListEquipmentTypesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListDietaryProfilesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListAllergensAsync(ReferenceQuery query, CancellationToken cancellationToken);
}

internal sealed class VocabularyFacade(
    IValidator<ReferenceQueryViewModel> validator,
    IVocabularyBusiness business,
    CachedPageReader reader) : IVocabularyFacade
{
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

internal sealed class VocabularyBusiness(IVocabularyDataLayer dataLayer) : IVocabularyBusiness
{
    public async Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListFoodCategoriesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Entries(await dataLayer.ListFoodCategoriesAsync(query, cancellationToken), query.Scope);

    public async Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCuisinesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Entries(await dataLayer.ListCuisinesAsync(query, cancellationToken), query.Scope);

    public async Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCoursesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Entries(await dataLayer.ListCoursesAsync(query, cancellationToken), query.Scope);

    public async Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListEquipmentTypesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Entries(await dataLayer.ListEquipmentTypesAsync(query, cancellationToken), query.Scope);

    public async Task<CursorPageServiceModel<CookingTechniqueServiceModel>> ListTechniquesAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await dataLayer.ListTechniquesAsync(query, cancellationToken);

        return PageBuilder.Build(rows, hasMore, query.Scope, row => new CookingTechniqueServiceModel(
            row.Id, row.Code, row.DisplayName, row.RequiresSafetyCaution));
    }

    public async Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListDietaryProfilesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Described(await dataLayer.ListDietaryProfilesAsync(query, cancellationToken), query.Scope);

    public async Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListAllergensAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Described(await dataLayer.ListAllergensAsync(query, cancellationToken), query.Scope);

    private static CursorPageServiceModel<ReferenceEntryServiceModel> Entries(
        (IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore) page, string scope) =>
        PageBuilder.Build(page.Rows, page.HasMore, scope,
            row => new ReferenceEntryServiceModel(row.Id, row.Code, row.DisplayName));

    private static CursorPageServiceModel<DescribedReferenceEntryServiceModel> Described(
        (IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore) page, string scope) =>
        PageBuilder.Build(page.Rows, page.HasMore, scope,
            row => new DescribedReferenceEntryServiceModel(row.Id, row.Code, row.DisplayName, row.Description));
}

internal sealed class VocabularyDataLayer(
    IControlledVocabularyRepository vocabularies,
    IReferenceCatalogRepository catalog) : IVocabularyDataLayer
{
    public Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListFoodCategoriesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        catalog.ListFoodCategoriesAsync(query, cancellationToken);

    public Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCuisinesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        vocabularies.ListCuisinesAsync(query, cancellationToken);

    public Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCoursesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        vocabularies.ListCoursesAsync(query, cancellationToken);

    public Task<(IReadOnlyList<CookingTechniqueRecord> Rows, bool HasMore)> ListTechniquesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        vocabularies.ListTechniquesAsync(query, cancellationToken);

    public Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListEquipmentTypesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        vocabularies.ListEquipmentTypesAsync(query, cancellationToken);

    public Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListDietaryProfilesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        catalog.ListDietaryProfilesAsync(query, cancellationToken);

    public Task<(IReadOnlyList<DescribedReferenceEntryRecord> Rows, bool HasMore)> ListAllergensAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        catalog.ListAllergensAsync(query, cancellationToken);
}
