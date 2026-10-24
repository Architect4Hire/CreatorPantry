namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The published plan catalogue, in configuration so the owner can change a price without a code change
/// (AP.4.1). This is the catalogue half of that prompt and nothing more: there is no <c>AccountPlan</c> table
/// yet, so nothing here is attached to an account, and no code reads these figures to admit or refuse work.
/// The landing page is the only consumer (LP.6).
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

    public IList<PlanOption> Plans { get; set; } = [];

    public IList<CreditPackOption> Packs { get; set; } = [];

    /// <summary>What one action of each kind costs, as the public "what a credit buys" menu states it.</summary>
    public CreditRatesOption Rates { get; set; } = new();
}

/// <summary>
/// The typical credit cost of one action of each kind (LP.7). Published so a visitor can see what a credit
/// buys and estimate which plan fits what they make.
/// </summary>
/// <remarks>
/// These are the figures the page states, not the ones admission charges. Text settles against
/// <c>AiQuotaOptions.DefaultTaskEstimate</c> today, and pictures are not charged against an allowance at all
/// until AP.2.4 and AP.2.5 ship — which is why the whole catalogue stays unpublished outside development.
/// Publishing a rate the product does not charge would be a claim the product cannot keep.
/// </remarks>
public sealed class CreditRatesOption
{
    public int TextDraft { get; set; }

    public int LongFormSection { get; set; }

    public int ImageDraft { get; set; }

    public int ImageStandard { get; set; }

    public int ImageHigh { get; set; }

    /// <summary>Every rate, for the validation that none of them is nought in a published catalogue.</summary>
    public IEnumerable<int> All() => [TextDraft, LongFormSection, ImageDraft, ImageStandard, ImageHigh];
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

    /// <summary>AI credits the plan includes each period.</summary>
    public int Credits { get; set; }

    /// <summary>
    /// What those credits buy, in the creator's terms — "about 20 recipe drafts, 40 social posts and 60
    /// pictures". Prose, because it is an illustration rather than an entitlement.
    /// </summary>
    public string ExampleMix { get; set; } = string.Empty;

    /// <summary>Pictures that may be generated in a UTC day.</summary>
    public int DailyImageCap { get; set; }

    /// <summary>The picture quality classes the plan may ask for, in increasing order.</summary>
    public IList<string> QualityClasses { get; set; } = [];

    /// <summary>The one plan the page recommends. Exactly one plan may set it; startup refuses otherwise.</summary>
    public bool Recommended { get; set; }

    /// <summary>Set only on a trial plan: how many days it lasts. Null on a paid plan.</summary>
    public int? TrialDays { get; set; }
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
