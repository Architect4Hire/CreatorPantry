using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.AiUsage;

public static class AiUsageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-account AI usage recording and quota seams (facade through repository). Requires
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/> and
    /// <c>AddApplicationTime</c> to be registered.
    /// </summary>
    /// <param name="configuration">
    /// The host's configuration, read for <see cref="AiQuotaOptions.SectionName"/>. Optional so a test can
    /// register the module without one and get the documented defaults — a host that omits it gets the
    /// platform default allowance and estimates, which is a deployment decision rather than a broken one.
    /// </param>
    /// <remarks>
    /// Scoped, like every other seam here, and for one extra reason worth stating: the recording and
    /// settlement paths stage onto the caller's unit of work, so they must share the caller's
    /// <c>DbContext</c> instance. A singleton or transient registration would still compile and would stage
    /// onto a context nobody saves.
    /// </remarks>
    public static IServiceCollection AddAiUsageModule(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddScoped<IAiUsageRecordingFacade, AiUsageRecordingFacade>();
        services.AddScoped<IAiUsageRecordingBusiness, AiUsageRecordingBusiness>();
        services.AddScoped<IAiUsageDataLayer, AiUsageDataLayer>();
        services.AddScoped<IAiUsageRepository, AiUsageRepository>();

        services.AddScoped<IAiQuotaAdmissionFacade, AiQuotaAdmissionFacade>();
        services.AddScoped<IAiQuotaMaintenanceFacade, AiQuotaMaintenanceFacade>();
        services.AddScoped<IAiQuotaAdmissionBusiness, AiQuotaAdmissionBusiness>();
        services.AddScoped<IAiQuotaPeriodResolver, AiQuotaPeriodResolver>();
        services.AddScoped<IAiQuotaDataLayer, AiQuotaDataLayer>();
        services.AddScoped<IAiQuotaRepository, AiQuotaRepository>();

        // The platform's image spend breaker (AP.2.7). Its trip writes a platform audit row, so the writer comes
        // with it: TryAdd, because the hosts register it again through AddAudit and a test that registers only
        // this module still has to be able to refuse a picture.
        services.AddScoped<IPlatformImageSpendDataLayer, PlatformImageSpendDataLayer>();
        services.AddScoped<IPlatformImageSpendRepository, PlatformImageSpendRepository>();
        services.TryAddScoped<IPlatformAuditWriter, PlatformAuditWriter>();

        // The trip's alert is a log line, so admission takes a logger it cannot do without. AddLogging adds only
        // what is missing, so a host's own logging is untouched and a module-only test still resolves one.
        services.AddLogging();

        // The account's own read of its usage (USAGE-008). Separate from the seams above because it carries a
        // prerequisite they do not: it asks the tenancy module which workspaces the caller still belongs to,
        // so a host registering this must also call AddTenancy. Registered here rather than in its own method
        // because every host that serves /api/v1/me already has tenancy for the memberships route beside it.
        services.AddScoped<IAiUsageReadFacade, AiUsageReadFacade>();
        services.AddScoped<IAiUsageReadBusiness, AiUsageReadBusiness>();
        services.AddScoped<IAiUsageReadDataLayer, AiUsageReadDataLayer>();
        services.AddScoped<IAiUsageReadRepository, AiUsageReadRepository>();

        var quota = services.AddOptions<AiQuotaOptions>()
            .Validate(
                options => options.NearlySpentFraction is > 0m and < 1m,
                "Ai:Quota:NearlySpentFraction must be above nought and below one.")
            .Validate(
                options => options.PlatformImageSpendPerDay is null
                    or (> 0m and <= AiQuotaOptions.PlatformImageSpendPerDayMax),
                $"Ai:Quota:PlatformImageSpendPerDay must be above nought and at most "
                    + $"{AiQuotaOptions.PlatformImageSpendPerDayMax:0} US dollars when it is set; leave it unset to "
                    + "switch the image spend breaker off.")

            // At startup, not at the first picture: a ceiling of nought would otherwise start cleanly and then
            // refuse every AI request the moment admission read its options.
            .ValidateOnStart();

        // The list prices the breaker counts in, read from the AI module's own section (AiImageListPriceOptions).
        var listPrices = services.AddOptions<AiImageListPriceOptions>();

        if (configuration is not null)
        {
            quota.Bind(configuration.GetSection(AiQuotaOptions.SectionName));
            listPrices.Bind(configuration.GetSection(AiImageListPriceOptions.SectionName));
        }

        return services;
    }

    /// <summary>
    /// Registers the platform administration seam (USAGE-009), reached only through the <c>/api/v1/ops/*</c>
    /// routes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AddAiUsageModule"/> because its prerequisites and its callers are different.
    /// It needs the Auth module's <c>IPlatformAccountFacade</c> — an administrator naming an account must get
    /// a 404 for one nobody has — and <c>IPlatformAuditWriter</c> from <c>AddAudit</c>, because every change it
    /// makes stages an audit row in the same transaction.
    /// </para>
    /// <para>
    /// The worker registers <see cref="AddAiUsageModule"/> and deliberately not this: it settles and sweeps
    /// allowances, it does not administer them, and there is no ops route in that host for a key to reach.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAiUsageAdministration(this IServiceCollection services)
    {
        services.AddScoped<IValidator<SetAccountAiQuotaViewModel>, SetAccountAiQuotaViewModelValidator>();
        services.AddScoped<IValidator<AccountAiQuotaReasonViewModel>, AccountAiQuotaReasonViewModelValidator>();
        services.AddScoped<IValidator<PlatformImageSpendLiftViewModel>, PlatformImageSpendLiftViewModelValidator>();
        services.AddScoped<IAiUsageAdministrationFacade, AiUsageAdministrationFacade>();
        services.AddScoped<IAiUsageAdministrationBusiness, AiUsageAdministrationBusiness>();
        services.AddScoped<IAiUsageAdministrationDataLayer, AiUsageAdministrationDataLayer>();
        services.AddScoped<IAiUsageAdministrationRepository, AiUsageAdministrationRepository>();

        return services;
    }
}
