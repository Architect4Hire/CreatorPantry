using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Business;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Vocabulary;

public static class VocabularyServiceCollectionExtensions
{
    /// <inheritdoc cref="Measurement.MeasurementServiceCollectionExtensions.AddMeasurementModule"/>
    public static IServiceCollection AddVocabularyModule(this IServiceCollection services)
    {
        services.AddPaging();
        services.AddScoped<IValidator<ReferenceQueryViewModel>, ReferenceQueryViewModelValidator>();
        services.AddScoped<IVocabularyFacade, VocabularyFacade>();
        services.AddScoped<IVocabularyBusiness, VocabularyBusiness>();
        services.AddScoped<IVocabularyDataLayer, VocabularyDataLayer>();
        services.AddScoped<IControlledVocabularyRepository, ControlledVocabularyRepository>();
        services.AddScoped<IReferenceCatalogRepository, ReferenceCatalogRepository>();

        return services;
    }
}
