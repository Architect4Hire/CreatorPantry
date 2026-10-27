namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The plan an account is on at one instant, with the catalogue's terms for it (PRICE-007). Application output for
/// other modules: no price, because nothing that admits work needs one.
/// </summary>
/// <param name="Key">The catalogue key of the plan in force.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Credits">The allowance it grants each <paramref name="Period"/>.</param>
/// <param name="DailyImageCap">Pictures the account may generate in a UTC day.</param>
/// <param name="RunningImages">Pictures the account may have generating at once.</param>
/// <param name="QualityClasses">The picture quality classes the plan allows, as the catalogue lists them.</param>
/// <param name="TrialDays">How long the trial lasts; null on a paid plan.</param>
/// <param name="TrialImageCap">Pictures the whole trial may generate; null on a paid plan (B-52).</param>
/// <param name="IsDefault">
/// True when the plan is <c>Billing:DefaultPlan</c> because no row names another: the account has no row, its row
/// says "the default", or its row names a plan the catalogue no longer has.
/// </param>
/// <param name="Source">What wrote the row in force; null when no row is in force.</param>
/// <param name="EffectiveFrom">When the row in force started; null when none is.</param>
/// <param name="EffectiveUntil">
/// When the next row takes over — a change already scheduled, such as a downgrade at period end (B-54) — or null
/// when nothing is scheduled. Not stored: it is the next row's start.
/// </param>
public sealed record AccountPlanServiceModel(
    string Key,
    string Name,
    int Credits,
    PlanAllowancePeriod Period,
    int DailyImageCap,
    int RunningImages,
    IReadOnlyList<string> QualityClasses,
    int? TrialDays,
    int? TrialImageCap,
    bool IsDefault,
    AccountPlanSource? Source,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveUntil);

/// <summary>What a plan change did (AP.4.1). Internal to Billing until AP.4.4 exposes it.</summary>
internal enum AccountPlanChangeOutcome
{
    Changed = 1,

    /// <summary>The key is not a plan in the catalogue, or the catalogue is empty.</summary>
    UnknownPlan = 2,

    /// <summary>The requested start has already passed. History is not rewritten.</summary>
    InThePast = 3,

    /// <summary>
    /// The requested start is not after the account's latest row. A change goes on the end of the chain; one that
    /// would land inside another row's span is an overlap, and the schema would refuse it too.
    /// </summary>
    NotAfterLatest = 4,

    /// <summary>Another writer changed the account's plan first; the caller re-reads and decides again.</summary>
    Contended = 5,

    /// <summary>
    /// A change for now was asked while a later one is already scheduled. It would have to land after the scheduled
    /// one, so nothing is written; replacing a scheduled change is AP.6.2's.
    /// </summary>
    ChangeScheduled = 6,
}
