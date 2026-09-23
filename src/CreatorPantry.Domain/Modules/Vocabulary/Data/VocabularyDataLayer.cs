using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data;

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
