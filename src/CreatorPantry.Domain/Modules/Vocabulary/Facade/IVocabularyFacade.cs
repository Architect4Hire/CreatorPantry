using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Facade;

public interface IVocabularyFacade
{
    /// <summary>
    /// Whether an id names an entry in one of this module.s catalogues that is still offered for new input.
    /// </summary>
    /// <remarks>
    /// Exists so another module can validate a reference it is about to store without reading these tables
    /// itself. A retired entry answers false: existing recipes keep resolving it, but nothing new may point
    /// at it. An unknown id also answers false, and the two are not distinguished — a caller only needs to
    /// know whether it may use the id.
    /// </remarks>
    Task<bool> IsUsableAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The display name of one entry, for a caller rendering a reference it already stores.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="IsUsableAsync"/>, a retired entry still answers: an existing recipe keeps resolving
    /// what it was saved with. An unknown id answers <c>null</c>, which a caller must treat as unresolved
    /// rather than guess a name for.
    /// </remarks>
    Task<string?> GetDisplayNameAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken);

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
