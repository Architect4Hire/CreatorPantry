namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The plan catalogue, in configuration so the owner can change a price without a code change (AP.4.1). The
/// landing page publishes it (LP.6), and <c>IAccountPlanFacade</c> reads it to say what terms the plan an account
/// is on carries. Nothing yet admits or refuses work by those terms; AP.4.2 does.
/// </summary>
/// <remarks>
/// <para>
/// <b>The figures are proposals, not decisions.</b> The values shipped in <c>appsettings.Development.json</c>
/// are the "Proposed, awaiting the owner" table from the pricing analysis, seeded so the section renders while
/// it is built. They are not a commitment and AP.1.1 is where they become decided, which is why
/// <see cref="PublishPlans"/> is off everywhere but development.
/// </para>
/// <para>
/// Prices are decimal money in <see cref="Currency"/>, never a formatted string: the browser formats them for
/// the reader's locale, and a figure that has been through a string cannot be added up or compared.
/// </para>
/// </remarks>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>
    /// Whether the catalogue is published to anonymous callers. Off unless a deployment says otherwise, because
    /// a price on a public page is a promise, and the figures below have not been decided yet.
    /// </summary>
    public bool PublishPlans { get; set; }

    /// <summary>ISO 4217 code every price here is in. One currency: there is no localisation of money yet.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// The key of the plan an account is on when no <c>AccountPlan</c> row says otherwise — no row at all, a row
    /// that says "the default", or a row naming a plan the catalogue no longer has. Required, and must name a
    /// plan, whenever <see cref="Plans"/> has any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Proposed, not decided:</b> development sets <c>trial</c>, the daily-limits document's proposal for
    /// what a new sign-up is on. A trial ends <see cref="PlanOption.TrialDays"/> after its account was created
    /// (AP.7.1), and an account still on it after that is admitted no AI work until it chooses a plan.
    /// </para>
    /// <para>
    /// With no plans configured, which is every deployment today, there is no default either, and an account
    /// resolves to no plan at all: whatever reads plans keeps the flat limits it had before them.
    /// </para>
    /// </remarks>
    public string? DefaultPlan { get; set; }

    public IList<PlanOption> Plans { get; set; } = [];

    public IList<CreditPackOption> Packs { get; set; } = [];

    /// <summary>What one action of each kind costs, as the public "what a credit buys" menu states it.</summary>
    public CreditRatesOption Rates { get; set; } = new();
}

/// <summary>
/// The typical credit cost of one written action of each kind (LP.7). Published so a visitor can see what a
/// credit buys and estimate which plan fits what they make.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Text only, since AP.4.5.</strong> There used to be three picture figures here as well, typed by
/// hand. A picture's price is not typical and never was guesswork: it is the <c>Ai:Quota:ImageRates</c> row for
/// what the chosen quality class sends, which is what admission holds against — so publishing a second copy
/// invited the page and the charge to disagree. The page now reads those rates themselves
/// (<c>PictureClassRateServiceModel</c>), and the one source rule in AP.4.5's RESTRICTION is what removed
/// these.
/// </para>
/// <para>
/// Text stays, because it genuinely is typical: a run settles at the tokens the model reported, against
/// <c>AiQuotaOptions</c>'s per-call estimate, so the page says "about" for it and nothing else.
/// </para>
/// </remarks>
public sealed class CreditRatesOption
{
    public int TextDraft { get; set; }

    public int LongFormSection { get; set; }

    /// <summary>Every rate, for the validation that none of them is nought in a published catalogue.</summary>
    public IEnumerable<int> All() => [TextDraft, LongFormSection];
}

/// <summary>One plan as the catalogue publishes it.</summary>
public sealed class PlanOption
{
    /// <summary>Stable identifier, used in <c>/sign-up?plan=</c> and never shown to a reader.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>What a month costs when paid monthly. Zero for a free trial.</summary>
    public decimal MonthlyPrice { get; set; }

    /// <summary>What a month works out at when a year is paid up front. Zero for a free trial.</summary>
    public decimal AnnualMonthlyPrice { get; set; }

    /// <summary>AI credits the plan includes each <see cref="Period"/>: the plan's allowance.</summary>
    public int Credits { get; set; }

    /// <summary>How often <see cref="Credits"/> are granted. Monthly, or once for a trial.</summary>
    public PlanAllowancePeriod Period { get; set; }

    /// <summary>
    /// What those credits make in one period, in the creator's terms: recipes drafted, social posts and
    /// pictures, all in the same month. An illustration rather than an entitlement — the credits are what is
    /// granted — but one that has to fit them, so it is counts rather than prose.
    /// </summary>
    /// <remarks>
    /// Counts, so the landing page can lead each card with the recipes and the pictures as figures, and so a
    /// test can price the mix at the published rates (<c>PictureClassRateTests</c>) instead of parsing a sentence.
    /// </remarks>
    public PlanExampleOption? ExampleCounts { get; set; }

    /// <summary>Pictures that may be generated in a UTC day.</summary>
    public int DailyImageCap { get; set; }

    /// <summary>
    /// Pictures the account may have generating at once. Not published: the landing page makes no claim about it,
    /// and until AP.4.2 nothing enforces it in place of the flat per-workspace limit.
    /// </summary>
    public int RunningImages { get; set; }

    /// <summary>The picture quality classes the plan may ask for, in increasing order.</summary>
    public IList<string> QualityClasses { get; set; } = [];

    /// <summary>The one plan the page recommends. Exactly one plan may set it; startup refuses otherwise.</summary>
    public bool Recommended { get; set; }

    /// <summary>
    /// Set only on a trial plan: how many days it lasts, counted from the account's creation (AP.7.1). Null on a paid
    /// plan.
    /// </summary>
    public int? TrialDays { get; set; }

    /// <summary>
    /// Set only on a trial plan: how many pictures the whole trial may generate, beside its daily cap. The trial
    /// gate the owner chose instead of a card on file (<c>ai-pricing.md</c> B-52). Null on a paid plan.
    /// </summary>
    /// <remarks>
    /// Published so the page states the figure admission enforces, and read from here by admission (AP.7.1) — the
    /// account's plan carries this value, never a second copy of it. Counted from the account's picture holds, across
    /// every workspace and day.
    /// </remarks>
    public int? TrialImageCap { get; set; }
}

/// <summary>
/// One month a plan's credits pay for, all of it at once: the sales pitch on its card (owner, 2026-10-28).
/// </summary>
/// <remarks>
/// <strong>"And", never "or".</strong> Every count here is made in the same period, so the mix is priced as a
/// sum — writing at <c>Billing:Rates:TextDraft</c>, pictures at the default quality's rate — and that sum must
/// not exceed the plan's credits. A ceiling for each on its own ("900 drafts or 90 pictures") would read as
/// "and" on a card and promise twice what the plan holds.
/// </remarks>
public sealed class PlanExampleOption
{
    public int RecipeDrafts { get; set; }

    public int SocialPosts { get; set; }

    /// <summary>Pictures at the default quality — what a creator gets without choosing one.</summary>
    public int Pictures { get; set; }
}

/// <summary>
/// A prepaid credit pack, bought on top of a plan.
/// </summary>
/// <remarks>
/// There is no expiry here, and that is the decision rather than an omission (owner, 2026-10-24): a pack is
/// credits the creator has already paid for, so they keep until they are spent. A nullable
/// <c>ExpiryMonths</c> that had to stay empty would be a trap — one deployment setting it would have the
/// public page promise a term the owner has ruled out.
/// </remarks>
public sealed class CreditPackOption
{
    public string Key { get; set; } = string.Empty;

    public int Credits { get; set; }

    public decimal Price { get; set; }
}
