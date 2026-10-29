using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Configuration;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing;

public static class BillingServiceCollectionExtensions
{
    /// <summary>
    /// The plan catalogue (LP.6), which plan each account is on (AP.4.1), and the credits granted to it beyond its
    /// allowance (AP.5.1). The API and the Worker both register
    /// it, with the same <c>Billing</c> section: the Worker opens allowance periods when it admits text work, and
    /// a period opened there has to grant what the API's read of it shows (AP.4.2).
    /// </summary>
    /// <param name="configuration">
    /// Read for <see cref="BillingOptions.SectionName"/>. Optional so a test registering the modules that ask for a
    /// plan can do so without one, and get no catalogue — every account then resolves to no plan, and whatever
    /// reads a plan keeps its flat limits.
    /// </param>
    /// <remarks>
    /// <para>
    /// The catalogue is validated at startup rather than at the request. A price of nought on a paid plan, two
    /// plans both flagged as recommended or a duplicate key are all the sort of mistake that would otherwise be
    /// found by a visitor reading the page, and a host that would publish a wrong price should not start.
    /// </para>
    /// <para>
    /// The same goes for what an account resolves to: a default that names no plan, or a plan with no allowance
    /// period, would otherwise be found by a creator's request. The one thing startup cannot see is a stored row
    /// naming a plan since removed, which resolves to the default with a warning instead.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBillingModule(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        var catalogue = services.AddOptions<BillingOptions>();

        if (configuration is not null)
        {
            catalogue.Bind(configuration.GetSection(BillingOptions.SectionName));
        }

        catalogue
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
                // A picture cap on a plan with no trial would publish a limit on a paid plan nobody decided.
                billing => billing.Plans.All(plan => plan.TrialImageCap is null || (plan.TrialDays is not null && plan.TrialImageCap > 0)),
                "A plan under Billing:Plans has a TrialImageCap but no trial, or a cap of nought; set it on a trial plan only.")
            .Validate(
                // The trial gate is the cap (B-52); a published trial without one would promise unbounded pictures.
                billing => !billing.PublishPlans || billing.Plans.All(plan => plan.TrialDays is null || plan.TrialImageCap is not null),
                "A published trial plan under Billing:Plans has no TrialImageCap; the trial's picture cap is its gate.")
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
                billing => billing.Plans.All(plan => plan.ExampleCounts is null
                    || (plan.ExampleCounts.RecipeDrafts >= 0 && plan.ExampleCounts.SocialPosts >= 0 && plan.ExampleCounts.Pictures >= 0)),
                "A plan under Billing:Plans has a negative count in its ExampleCounts.")
            .Validate(
                // The card leads with the recipes and the pictures, so a published plan needs both.
                billing => !billing.PublishPlans || billing.Plans.All(plan =>
                    plan.ExampleCounts is { RecipeDrafts: > 0, Pictures: > 0 }),
                "A published plan under Billing:Plans has no ExampleCounts with recipe drafts and pictures; every card "
                    + "leads with what its credits make.")
            .Validate(
                // A trial's example is a promise about the whole trial, and B-52's cap is the most it may make.
                billing => billing.Plans.All(plan =>
                    plan.ExampleCounts is null || plan.TrialImageCap is not { } cap || plan.ExampleCounts.Pictures <= cap),
                "A trial plan under Billing:Plans has more pictures in its ExampleCounts than its TrialImageCap allows.")
            .Validate(
                // A rate of nought would have the estimator price that kind of work at nothing, and the menu
                // tell a visitor it is free.
                billing => !billing.PublishPlans || billing.Rates.All().All(rate => rate > 0),
                "A rate under Billing:Rates is nought; a published menu states what each kind of action costs.")
            .Validate(
                // What an account with no plan row is on. Without plans there is nothing to default to, and an
                // account resolves to no plan at all.
                billing => billing.Plans.Count == 0
                    || billing.Plans.Any(plan => string.Equals(plan.Key, billing.DefaultPlan, StringComparison.OrdinalIgnoreCase)),
                "Billing:DefaultPlan does not name a plan under Billing:Plans; an account with no plan of its own is on it.")
            .Validate(
                // Stored on AccountPlan.PlanKey, so a key the column cannot hold is a plan nobody can be put on.
                billing => billing.Plans.All(plan => plan.Key.Length <= BillingPolicy.PlanKeyMaxLength),
                $"A plan key under Billing:Plans is longer than {BillingPolicy.PlanKeyMaxLength} characters.")
            .Validate(
                billing => billing.Plans.All(plan => plan.Period != PlanAllowancePeriod.Unspecified),
                "A plan under Billing:Plans has no Period; say whether its credits come Monthly or Once.")
            .Validate(
                // A trial's credits are granted once, not per period (B-48), and only a trial's are.
                billing => billing.Plans.All(plan => (plan.Period == PlanAllowancePeriod.Once) == (plan.TrialDays is not null)),
                "A plan under Billing:Plans grants its credits Once without a trial, or is a trial that does not; Once is the trial's period and only the trial's.")
            .Validate(
                // Nought would be a plan that may ask for pictures and can never have one running.
                billing => billing.Plans.All(plan => plan.RunningImages >= 1),
                "A plan under Billing:Plans has RunningImages below one.")
            .Validate(
                billing => billing.Plans.All(plan =>
                    plan.QualityClasses.All(quality => !string.IsNullOrWhiteSpace(quality))
                    && plan.QualityClasses.Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.QualityClasses.Count),
                "A plan under Billing:Plans lists a blank or repeated quality class.")
            .Validate(
                // A typo in a class name would otherwise be a class every plan quietly lacks and no creator can
                // ever ask for, found by whoever first wonders why High is locked on the top plan. Matched
                // through the shared vocabulary, which is the comparison admission makes too (AP.4.3), so a
                // plan that starts is a plan whose classes resolve.
                billing => billing.Plans.All(plan =>
                    plan.QualityClasses.All(PictureQualityVocabulary.IsKnown)),
                "A plan under Billing:Plans lists a quality class that is not one of them; the classes are "
                    + $"{string.Join(", ", PictureQualityVocabulary.Names)}, spelled with or without hyphens.")
            .Validate<IOptions<LimitsOptions>>(
                // Both caps apply to a request (AP.4.2): the plan's to the account, the workspace's to everyone in
                // the workspace. A plan above the workspace's could never be reached by a creator who works in one
                // workspace, so its card would promise a number the product cannot give.
                (billing, limits) => !limits.Value.Enabled || billing.Plans.All(plan =>
                    plan.DailyImageCap <= limits.Value.ImageVariantsPerWorkspacePerDay),
                "A plan under Billing:Plans has a DailyImageCap above Limits:ImageVariantsPerWorkspacePerDay; "
                    + "a creator in one workspace could never reach it.")
            .Validate<IOptions<LimitsOptions>>(
                (billing, limits) => !limits.Value.Enabled || billing.Plans.All(plan =>
                    plan.RunningImages <= limits.Value.ImageRunningPerWorkspace
                    && plan.RunningImages <= limits.Value.ImageInFlightPerWorkspace),
                "A plan under Billing:Plans has RunningImages above Limits:ImageRunningPerWorkspace or "
                    + "Limits:ImageInFlightPerWorkspace; a creator in one workspace could never reach it.")
            .Validate<IOptions<AiQuotaOptions>>(
                // A plan's credits are credits a month. Granted under another unit or period they would be a
                // different allowance from the one the page states, so the default terms must be those.
                (billing, quota) => billing.Plans.Count == 0
                    || (quota.Value.DefaultUnit == AiQuotaUnit.Credits
                        && quota.Value.DefaultPeriodLength == AiQuotaPeriodLength.Monthly),
                "Billing:Plans is set but Ai:Quota:DefaultUnit is not Credits or Ai:Quota:DefaultPeriodLength is not "
                    + "Monthly; a plan's credits are a monthly allowance in credits.")
            .Validate(
                // A provider page sends the creator back to one of these, so each must be an origin and nothing else:
                // a path or a query here would be glued to every return URL (AP.6.5).
                billing => billing.ReturnOrigins.All(BillingAccountOrigins.IsOrigin),
                "An entry under Billing:ReturnOrigins is not an http(s) origin (scheme, host and port only).")
            .ValidateOnStart();

        services.AddSingleton<RemovedPlanWarnings>();

        // The ops plan change writes a platform audit row in the same save (AP.4.4), as the quota routes do, and
        // TryAdd so a host that already called AddAudit keeps the one registration.
        services.TryAddScoped<IPlatformAuditWriter, PlatformAuditWriter>();
        services.AddScoped<IValidator<SetAccountPlanViewModel>, SetAccountPlanViewModelValidator>();

        services.AddScoped<IPlanCatalogueBusiness, PlanCatalogueBusiness>();
        services.AddScoped<IPlanCatalogueFacade, PlanCatalogueFacade>();

        services.AddScoped<IAccountPlanRepository, AccountPlanRepository>();
        services.AddScoped<IAccountPlanDataLayer, AccountPlanDataLayer>();
        services.AddScoped<IAccountPlanBusiness, AccountPlanBusiness>();
        services.AddScoped<IAccountPlanFacade, AccountPlanFacade>();

        // A trial ends TrialDays after its account was created (AP.7.1), which only Auth knows. Registered here,
        // TryAdd-based, so every host and test that can resolve a plan can resolve when its trial ends — the
        // Worker included, which admits text work and hosts no Identity.
        services.AddAccountCreationFacade();

        // The signed-in account's own billing (AP.6.5): what its page shows, and the Checkout, portal and plan-change
        // commands behind its buttons. Every command is idempotent, so a host resolving the facade needs
        // AddIdempotency; both hosts that register this module already call it.
        services.AddScoped<IValidator<StartPlanCheckoutViewModel>, StartPlanCheckoutViewModelValidator>();
        services.AddScoped<IValidator<StartPackCheckoutViewModel>, StartPackCheckoutViewModelValidator>();
        services.AddScoped<IValidator<ChangePlanViewModel>, ChangePlanViewModelValidator>();
        services.AddScoped<IValidator<OpenBillingPortalViewModel>, OpenBillingPortalViewModelValidator>();
        services.AddScoped<IBillingAccountDataLayer, BillingAccountDataLayer>();
        services.AddScoped<IBillingAccountBusiness, BillingAccountBusiness>();
        services.AddScoped<IBillingAccountFacade, BillingAccountFacade>();

        // Credits beyond the allowance (AP.5.1). The ops grant takes an idempotency key, so a host resolving the
        // facade needs AddIdempotency too; both hosts that register this module already call it.
        services.AddScoped<IValidator<GrantAccountCreditsViewModel>, GrantAccountCreditsViewModelValidator>();
        services.AddScoped<IValidator<ClawBackAccountCreditGrantViewModel>, ClawBackAccountCreditGrantViewModelValidator>();
        services.AddScoped<IAccountCreditGrantRepository, AccountCreditGrantRepository>();
        services.AddScoped<IAccountCreditGrantDataLayer, AccountCreditGrantDataLayer>();
        services.AddScoped<IAccountCreditGrantBusiness, AccountCreditGrantBusiness>();
        services.AddScoped<IAccountCreditGrantFacade, AccountCreditGrantFacade>();

        // Spending them after the allowance (AP.5.2): AiUsage's admission, settlement and expiry reach the grants
        // only through this facade, on the same scoped DbContext, so a split hold commits as one.
        services.AddScoped<IAccountCreditSpendRepository, AccountCreditSpendRepository>();
        services.AddScoped<IAccountCreditSpendDataLayer, AccountCreditSpendDataLayer>();
        services.AddScoped<IAccountCreditSpendBusiness, AccountCreditSpendBusiness>();
        services.AddScoped<IAccountCreditSpendFacade, AccountCreditSpendFacade>();

        // The payment provider (AP.6.1). "Not configured" unless a host adds one (AddStripeBilling): a TryAdd, so the
        // provider's registration wins whichever order the two are called in.
        services.TryAddScoped<IBillingPaymentGateway, UnconfiguredBillingPaymentGateway>();
        services.AddScoped<IBillingCustomerRepository, BillingCustomerRepository>();
        services.AddScoped<IBillingCustomerDataLayer, BillingCustomerDataLayer>();

        // The provider's webhook (AP.6.2). The API takes deliveries; the Worker, whose outbox dispatcher runs the
        // handler, acts on them. The verifier says "not configured" unless a host was given a signing secret
        // (AddStripeBilling), which only the API is. The writer is the outbox's, TryAdd for the reason Brand gives.
        services.TryAddSingleton<IBillingWebhookVerifier, UnconfiguredBillingWebhookVerifier>();
        services.TryAddScoped<IOutboxWriter, OutboxWriter>();
        // A pack bought, refunded or disputed (AP.6.3): the credits half of the same webhook, on the same scoped
        // context, so a grant and the purchase that records it are one request's work.
        services.AddScoped<IBillingPackPurchaseRepository, BillingPackPurchaseRepository>();
        services.AddScoped<IBillingPackPurchaseDataLayer, BillingPackPurchaseDataLayer>();
        services.AddScoped<IBillingPackPurchaseBusiness, BillingPackPurchaseBusiness>();

        services.AddScoped<IBillingWebhookRepository, BillingWebhookRepository>();
        services.AddScoped<IBillingWebhookDataLayer, BillingWebhookDataLayer>();
        services.AddScoped<IBillingWebhookBusiness, BillingWebhookBusiness>();
        services.AddScoped<IBillingWebhookFacade, BillingWebhookFacade>();
        services.AddKeyedScoped<IOutboxMessageHandler, BillingProviderEventOutboxHandler>(BillingWebhookPolicy.OutboxMessageType);

        return services;
    }
}
