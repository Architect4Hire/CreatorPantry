using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data;

/// <inheritdoc cref="CreatorPantry.Domain.Modules.Measurement.Data.IMeasurementDataLayer"/>
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

