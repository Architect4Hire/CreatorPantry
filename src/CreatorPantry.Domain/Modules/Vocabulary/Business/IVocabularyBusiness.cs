using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Business;

/// <inheritdoc cref="CreatorPantry.Domain.Modules.Vocabulary.Facade.IVocabularyFacade"/>
public interface IVocabularyBusiness
{
    Task<bool> IsUsableAsync(CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListFoodCategoriesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCuisinesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCoursesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<CookingTechniqueServiceModel>> ListTechniquesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListEquipmentTypesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListDietaryProfilesAsync(ReferenceQuery query, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListAllergensAsync(ReferenceQuery query, CancellationToken cancellationToken);
}
