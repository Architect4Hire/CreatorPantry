namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The published plan catalogue as the public landing page reads it (LP.6). Application output: no account,
/// workspace or usage of any kind is in here, which is what lets the route serving it be anonymous.
/// </summary>
/// <param name="Currency">ISO 4217 code every price in this catalogue is in.</param>
/// <param name="Plans">The plans on offer, cheapest first. Empty while the catalogue is unpublished.</param>
/// <param name="Packs">Prepaid credit packs, smallest first. Empty while the catalogue is unpublished.</param>
/// <param name="Rates">What one action of each kind typically costs. All nought while unpublished.</param>
/// <param name="PictureClasses">
/// What one picture of each quality costs, ascending (PRICE-006, AP.4.5). Read from the charging table the
/// product actually holds against, so a figure here is the figure a creator is charged — never a second copy.
/// Empty while unpublished, and a class no plan includes is left out rather than advertised.
/// </param>
/// <param name="DefaultPictureClass">
/// The quality a picture is made at when a creator chooses none — <c>Ai:Images:DefaultClass</c> (AP.4.5). Null
/// while unpublished, or when that class is not among <paramref name="PictureClasses"/>.
/// </param>
/// <remarks>
/// <strong>Published so the estimator does not have to guess.</strong> Pricing a month at the cheapest quality
/// would quietly under-estimate and send a visitor to a plan too small for what they make; at the dearest it
/// would overcharge them in their head. What they get without choosing is the honest figure to start from, and
/// it is the host's to state rather than the browser's to assume.
/// </remarks>
public sealed record PlanCatalogueServiceModel(
    string Currency,
    IReadOnlyList<PlanServiceModel> Plans,
    IReadOnlyList<CreditPackServiceModel> Packs,
    CreditRatesServiceModel Rates,
    IReadOnlyList<PictureClassRateServiceModel> PictureClasses,
    string? DefaultPictureClass)
{
    /// <summary>What an unpublished catalogue answers: a shape the page can read, with nothing in it to show.</summary>
    public static PlanCatalogueServiceModel Unpublished(string currency) =>
        new(currency, [], [], new CreditRatesServiceModel(0, 0), [], null);
}

/// <summary>
/// What one picture of one quality class costs, in credits (PRICE-006, PRICE-013, AP.4.5).
/// </summary>
/// <param name="QualityClass">
/// The class as a request names it — <c>Draft</c>, <c>Standard</c>, <c>High</c>, <c>High2K</c>, <c>High4K</c>.
/// </param>
/// <param name="PerPicture">
/// Credits per picture: the rate <c>Ai:Quota:ImageRates</c> holds for exactly what that class sends, read
/// through the converter admission charges with (B-55). Exact, not typical — a picture's price is known before
/// it is made, which is why no line on the page says "about" for one.
/// </param>
/// <remarks>
/// It names no model. A request names a class, and what that class sends is this host's configuration, which a
/// public page has no business publishing.
/// </remarks>
public sealed record PictureClassRateServiceModel(string QualityClass, decimal PerPicture);

/// <summary>
/// The typical cost of one action of each kind, in credits — the "what a credit buys" menu (LP.7).
/// </summary>
/// <remarks>
/// <para>
/// Typical, and labelled as such wherever it is shown: what a run actually costs is settled against the
/// provider's own usage, and the allowance is enforced server-side rather than by these figures.
/// </para>
/// <para>
/// <strong>Text only, since AP.4.5.</strong> The three picture figures that used to live here were a second,
/// hand-typed copy of what a picture costs; pictures are now published by
/// <see cref="PictureClassRateServiceModel"/>, read from the table that charges them. Text stays here because
/// it genuinely is typical — it settles at the tokens the model reported.
/// </para>
/// <para>
/// <strong>Removing them was a breaking change, made without a new API version, and the reason is not that
/// nobody had seen them.</strong> They were on the wire on every host: an unpublished catalogue answered them
/// as noughts, which is what every deployment serves. What makes the removal safe is that the only consumer is
/// this repository's own SPA, which ships with the API it reads — and on an unpublished host the credit menu
/// those figures fed was already hidden. A third-party client would have needed <c>/api/v2</c>.
/// </para>
/// </remarks>
public sealed record CreditRatesServiceModel(int TextDraft, int LongFormSection);

/// <summary>
/// One plan. <paramref name="AnnualSaving"/> is worked out here rather than in the browser, so the figure a
/// visitor is shown is the one the catalogue stands behind.
/// </summary>
/// <param name="TrialImageCap">Pictures the whole trial may generate; null on a paid plan (B-52).</param>
/// <param name="PicturesAtDefaultQuality">
/// How many pictures this plan's credits buy at <see cref="PlanCatalogueServiceModel.DefaultPictureClass"/> —
/// the quality a creator gets without choosing one (AP.4.5).
/// </param>
/// <remarks>
/// <para>
/// <strong>Why this is published rather than worked out in the browser.</strong> Pictures are what a creator's
/// credits mostly go on, so "how many can I make" is the figure that decides which plan they need — and the one
/// place the page may not compute it is the browser, which formats figures and never divides a rate by anything
/// (AP.4.5's RESTRICTION). Derived here, from this plan's own credits and the charging table's own rate.
/// </para>
/// <para>
/// <strong>Bounded by the trial's lifetime cap where there is one.</strong> A trial's credits would buy more
/// pictures than B-52 lets it make — 1,000 credits is twenty standard pictures and the cap is ten — so the
/// smaller of the two is the true answer, and the larger would be a promise the product refuses to keep.
/// </para>
/// <para>
/// <strong>Not bounded by the daily cap</strong>, which <see cref="DailyImageCap"/> states on its own row: that
/// would need a view about how many days a month has, and a figure resting on one is not a figure this record
/// should publish. Null when no default quality is published, or when its price is unknown.
/// </para>
/// </remarks>
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
    int? TrialDays,
    int? TrialImageCap,
    int? PicturesAtDefaultQuality);

/// <summary>
/// A prepaid credit pack. It carries no expiry because a pack does not have one: the credits are bought, and
/// they keep until they are spent.
/// </summary>
public sealed record CreditPackServiceModel(string Key, int Credits, decimal Price);
