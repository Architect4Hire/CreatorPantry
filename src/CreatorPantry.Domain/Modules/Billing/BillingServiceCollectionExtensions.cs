using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Billing;

public static class BillingServiceCollectionExtensions
{
    /// <summary>
    /// The published plan catalogue (LP.6, the catalogue half of AP.4.1). Configuration only: the module owns no
    /// table, so it has no Data area and the API alone registers it — nothing in the Worker reads a price.
    /// </summary>
    /// <remarks>
    /// The catalogue is validated at startup rather than at the request. A price of nought on a paid plan, two
    /// plans both flagged as recommended or a duplicate key are all the sort of mistake that would otherwise be
    /// found by a visitor reading the page, and a host that would publish a wrong price should not start.
    /// </remarks>
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BillingOptions>()
            .Bind(configuration.GetSection(BillingOptions.SectionName))
            .Validate(
                billing => !billing.PublishPlans || billing.Plans.Count > 0,
                "Billing:PublishPlans is on but Billing:Plans is empty; a published catalogue needs at least one plan.")
            .Validate(
                billing => billing.Plans.All(plan => !string.IsNullOrWhiteSpace(plan.Key) && !string.IsNullOrWhiteSpace(plan.Name)),
                "Every plan under Billing:Plans needs a key and a name.")
            .Validate(
                billing => billing.Plans.Select(plan => plan.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == billing.Plans.Count,
                "Two plans under Billing:Plans share a key; the key is what a sign-up link carries.")
            .Validate(
                billing => billing.Plans.Count(plan => plan.Recommended) <= 1,
                "More than one plan under Billing:Plans is flagged Recommended; the page recommends one.")
            .Validate(
                billing => billing.Plans.All(plan => plan.MonthlyPrice >= 0 && plan.AnnualMonthlyPrice >= 0),
                "A plan under Billing:Plans has a negative price.")
            .Validate(
                // A yearly price above the monthly one would have the page offer a saving that is a loss.
                billing => billing.Plans.All(plan => plan.AnnualMonthlyPrice <= plan.MonthlyPrice),
                "A plan under Billing:Plans costs more per month paid yearly than paid monthly.")
            .Validate(
                billing => billing.Plans.All(plan => plan.Credits >= 0 && plan.DailyImageCap >= 0),
                "A plan under Billing:Plans has a negative credit allowance or daily picture cap.")
            .Validate(
                billing => billing.Plans.All(plan => plan.TrialDays is null or > 0),
                "A plan under Billing:Plans has a trial of nought days; leave TrialDays unset on a paid plan.")
            .Validate(
                billing => billing.Packs.All(pack => !string.IsNullOrWhiteSpace(pack.Key) && pack.Credits > 0 && pack.Price >= 0),
                "Every pack under Billing:Packs needs a key, credits above nought and a price that is not negative.")
            .Validate(
                billing => billing.Packs.Select(pack => pack.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == billing.Packs.Count,
                "Two packs under Billing:Packs share a key.")
            .Validate(
                billing => billing.Currency.Length == 3 && billing.Currency.All(char.IsAsciiLetterUpper),
                "Billing:Currency must be a three-letter upper-case ISO 4217 code.")
            .Validate(
                // The key travels in `/sign-up?plan=`, so it has to survive a URL without escaping.
                billing => billing.Plans.All(plan => plan.Key.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')),
                "A plan key under Billing:Plans is not url-safe; use lower-case letters, digits and hyphens.")
            .Validate(
                billing => !billing.PublishPlans || billing.Plans.All(plan => !string.IsNullOrWhiteSpace(plan.ExampleMix)),
                "A published plan under Billing:Plans has no ExampleMix; every card says what its credits buy.")
            .Validate(
                // A rate of nought would have the estimator price that kind of work at nothing, and the menu
                // tell a visitor it is free.
                billing => !billing.PublishPlans || billing.Rates.All().All(rate => rate > 0),
                "A rate under Billing:Rates is nought; a published menu states what each kind of action costs.")
            .ValidateOnStart();

        services.AddScoped<IPlanCatalogueBusiness, PlanCatalogueBusiness>();
        services.AddScoped<IPlanCatalogueFacade, PlanCatalogueFacade>();

        return services;
    }
}
