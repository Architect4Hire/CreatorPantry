using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Publishing;

public static class PublishingServiceCollectionExtensions
{
    /// <summary>
    /// The publishing profile seam (PUB-001).
    /// </summary>
    /// <remarks>
    /// The gateway is a <c>TryAdd</c> of the one that says "not configured": a host that has a provider
    /// registers its adapter as <see cref="IPublishingProfileGateway"/> and that registration wins, whichever
    /// side of this call it is made on. A host that has none starts cleanly and answers the same stable way.
    /// </remarks>
    public static IServiceCollection AddPublishingModule(this IServiceCollection services)
    {
        services.TryAddScoped<IPublishingProfileGateway, UnconfiguredPublishingProfileGateway>();

        // The same TryAdd, for the same reason: a host with no provider still starts, and a confirmation is
        // refused with a stable answer instead of failing to resolve a gateway.
        services.TryAddScoped<IPublishingPostGateway, UnconfiguredPublishingPostGateway>();

        services.AddScoped<IPublishingProfileRepository, PublishingProfileRepository>();
        services.AddScoped<IPublishingProfileDataLayer, PublishingProfileDataLayer>();
        services.AddScoped<IPublishingProfileBusiness, PublishingProfileBusiness>();
        services.AddScoped<IPublishingProfileFacade, PublishingProfileFacade>();

        // The dashboard's read of the ledger (13.11).
        services.AddScoped<IPublishingDashboardRepository, PublishingDashboardRepository>();
        services.AddScoped<IPublishingDashboardDataLayer, PublishingDashboardDataLayer>();
        services.AddScoped<IPublishingDashboardBusiness, PublishingDashboardBusiness>();
        services.AddScoped<IPublishingDashboardFacade, PublishingDashboardFacade>();

        // The ledger seam (PUB-002).
        services.AddScoped<IPublishingRecordRepository, PublishingRecordRepository>();
        services.AddScoped<IPublishingRecordDataLayer, PublishingRecordDataLayer>();
        services.AddScoped<IPublishingRecordBusiness, PublishingRecordBusiness>();
        services.AddScoped<IPublishingRecordFacade, PublishingRecordFacade>();
        services.AddScoped<IValidator<SchedulePublishingViewModel>, SchedulePublishingViewModelValidator>();
        services.AddScoped<IValidator<CancelPublishingViewModel>, CancelPublishingViewModelValidator>();

        // Reconciliation (13.10c): the claim repository is the carve-out, the business is the matrix.
        services.AddScoped<PublishingReconciliationClaimRepository>();
        services.AddScoped<IPublishingReconciliationBusiness, PublishingReconciliationBusiness>();
        services.AddScoped<IPublishingReconciliationFacade, PublishingReconciliationFacade>();
        services.AddScoped<IPublishingReconciliationWorker, PublishingReconciliationWorker>();

        return services;
    }
}
