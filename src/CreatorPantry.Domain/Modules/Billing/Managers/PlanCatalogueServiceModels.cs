namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The published plan catalogue as the public landing page reads it (LP.6). Application output: no account,
/// workspace or usage of any kind is in here, which is what lets the route serving it be anonymous.
/// </summary>
/// <param name="Currency">ISO 4217 code every price in this catalogue is in.</param>
/// <param name="Plans">The plans on offer, cheapest first. Empty while the catalogue is unpublished.</param>
/// <param name="Packs">Prepaid credit packs, smallest first. Empty while the catalogue is unpublished.</param>
/// <param name="Rates">What one action of each kind typically costs. All nought while unpublished.</param>
public sealed record PlanCatalogueServiceModel(
    string Currency,
    IReadOnlyList<PlanServiceModel> Plans,
    IReadOnlyList<CreditPackServiceModel> Packs,
    CreditRatesServiceModel Rates)
{
    /// <summary>What an unpublished catalogue answers: a shape the page can read, with nothing in it to show.</summary>
    public static PlanCatalogueServiceModel Unpublished(string currency) =>
        new(currency, [], [], new CreditRatesServiceModel(0, 0, 0, 0, 0));
}

/// <summary>
/// The typical cost of one action of each kind, in credits — the "what a credit buys" menu (LP.7).
/// </summary>
/// <remarks>
/// Typical, and labelled as such wherever it is shown: what a run actually costs is settled against the
/// provider's own usage, and the allowance is enforced server-side rather than by these figures.
/// </remarks>
public sealed record CreditRatesServiceModel(
    int TextDraft,
    int LongFormSection,
    int ImageDraft,
    int ImageStandard,
    int ImageHigh);

/// <summary>
/// One plan. <paramref name="AnnualSaving"/> is worked out here rather than in the browser, so the figure a
/// visitor is shown is the one the catalogue stands behind.
/// </summary>
public sealed record PlanServiceModel(
    string Key,
    string Name,
    decimal MonthlyPrice,
    decimal AnnualMonthlyPrice,
    decimal AnnualPrice,
    decimal AnnualSaving,
    int Credits,
    string ExampleMix,
    int DailyImageCap,
    IReadOnlyList<string> QualityClasses,
    bool Recommended,
    int? TrialDays);

/// <summary>
/// A prepaid credit pack. It carries no expiry because a pack does not have one: the credits are bought, and
/// they keep until they are spent.
/// </summary>
public sealed record CreditPackServiceModel(string Key, int Credits, decimal Price);
