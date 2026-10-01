using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Brand;

public static class BrandServiceCollectionExtensions
{
    /// <summary>
    /// The brand module's composition root. Requires <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/>
    /// , tenancy, application time, audit and idempotency to be registered.
    /// </summary>
    public static IServiceCollection AddBrandModule(this IServiceCollection services)
    {
        services.AddContentChannelCatalog();
        services.AddScoped<IBrandProfileRepository, BrandProfileRepository>();
        services.AddScoped<IBrandProfileDataLayer, BrandProfileDataLayer>();
        services.AddScoped<IBrandProfileBusiness, BrandProfileBusiness>();
        services.AddScoped<IBrandProfileFacade, BrandProfileFacade>();
        services.AddScoped<IValidator<CreateBrandProfileViewModel>, CreateBrandProfileViewModelValidator>();
        services.AddScoped<IValidator<UpdateBrandProfileViewModel>, UpdateBrandProfileViewModelValidator>();

        // The store that refuses everything unless the host registered a real one, so this module resolves in
        // a host with no storage and fails loudly only if an object is actually reached for.
        services.AddPrivateObjectStorage();
        services.AddScoped<IBrandSourceObjectGateway, BrandSourceObjectGateway>();

        return services;
    }
}
