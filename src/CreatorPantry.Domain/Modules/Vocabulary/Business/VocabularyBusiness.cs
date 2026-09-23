using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Business;

internal sealed class VocabularyBusiness(IVocabularyDataLayer dataLayer) : IVocabularyBusiness
{
    public Task<bool> IsUsableAsync(
        CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
        dataLayer.IsUsableAsync(catalog, id, cancellationToken);

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

