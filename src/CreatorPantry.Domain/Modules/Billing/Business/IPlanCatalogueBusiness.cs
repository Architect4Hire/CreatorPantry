using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>Reads the configured catalogue and works out the figures that are derived rather than typed.</summary>
public interface IPlanCatalogueBusiness
{
    PlanCatalogueServiceModel Published();
}

/// <remarks>
/// <see cref="IOptions{TOptions}"/> rather than a monitor on purpose: the value is the one
/// <c>ValidateOnStart</c> checked, and it cannot be replaced by a later configuration reload that no validator
/// has seen. A catalogue is not something to hot-swap under a running host; publishing a different price is a
/// deployment.
/// </remarks>
internal sealed class PlanCatalogueBusiness(IOptions<BillingOptions> options) : IPlanCatalogueBusiness
{
    public PlanCatalogueServiceModel Published()
    {
        var billing = options.Value;

        // Unpublished is not an error and not an empty page: it is a catalogue with nothing published in it,
        // which the landing page renders as no pricing section at all. The alternative — a 404 — would make an
        // expected state look like a fault in every log on every page load.
        if (!billing.PublishPlans)
        {
            return PlanCatalogueServiceModel.Unpublished(billing.Currency);
        }

        return new PlanCatalogueServiceModel(
            billing.Currency,
            [.. billing.Plans.OrderBy(plan => plan.MonthlyPrice).Select(Describe)],
            [.. billing.Packs.OrderBy(pack => pack.Credits).Select(pack =>
                new CreditPackServiceModel(pack.Key, pack.Credits, pack.Price))],
            new CreditRatesServiceModel(
                billing.Rates.TextDraft,
                billing.Rates.LongFormSection,
                billing.Rates.ImageDraft,
                billing.Rates.ImageStandard,
                billing.Rates.ImageHigh));
    }

    private static PlanServiceModel Describe(PlanOption plan)
    {
        // A year paid up front, and what that saves against twelve monthly payments. Both are derived from the
        // two prices the owner sets, so a catalogue cannot claim a saving its own prices do not add up to.
        var annualPrice = plan.AnnualMonthlyPrice * 12;
        var annualSaving = (plan.MonthlyPrice * 12) - annualPrice;

        return new PlanServiceModel(
            plan.Key,
            plan.Name,
            plan.MonthlyPrice,
            plan.AnnualMonthlyPrice,
            annualPrice,
            annualSaving,
            plan.Credits,
            plan.ExampleMix,
            plan.DailyImageCap,
            [.. plan.QualityClasses],
            plan.Recommended,
            plan.TrialDays,
            plan.TrialImageCap);
    }
}
