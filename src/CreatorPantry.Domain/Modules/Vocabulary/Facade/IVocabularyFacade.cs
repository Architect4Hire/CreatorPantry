using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Facade;

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
