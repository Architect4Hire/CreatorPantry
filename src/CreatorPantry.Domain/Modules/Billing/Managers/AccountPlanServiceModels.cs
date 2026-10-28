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

    /// <summary>
    /// The account is already on this plan from this instant, so nothing was written (AP.4.4) — a repeated
    /// command rather than a refusal.
    /// </summary>
    Unchanged = 7,

    /// <summary>The key is not a plan in this catalogue.</summary>
    UnknownPlan = 2,

    /// <summary>
    /// This host configures no catalogue at all, so there is no plan to put the account on — a different fact
    /// from a key the catalogue does not have, and answered differently (AP.4.4).
    /// </summary>
    NoCatalogue = 8,

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

/// <summary>
/// Which of the quality classes a caller asked about the account's plan allows, and what would allow the rest
/// (PRICE-006/007, AP.4.3). Application output for Media, which gates a picture request on it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The caller names the classes and never reads the catalogue.</strong> A plan lists its classes in the
/// landing page's spelling (<c>high-2k</c>) and Media spells the same class <c>High2K</c>; the matching is
/// <see cref="CreatorPantry.Domain.Managers.Reference.PictureQualityVocabulary"/>'s, here, so no other module normalises a class name or iterates
/// <c>Billing:Plans</c>.
/// </para>
/// <para>
/// No price and no account figures: what a plan costs is the published catalogue's, and what the account has
/// left is the quota's.
/// </para>
/// </remarks>
/// <param name="PlanKey">The catalogue key of the plan in force.</param>
/// <param name="PlanName">Its display name.</param>
/// <param name="Classes">One row per class asked about, in the order they were asked.</param>
public sealed record AccountPlanQualityServiceModel(
    string PlanKey,
    string PlanName,
    IReadOnlyList<PlanQualityClassServiceModel> Classes);

/// <summary>
/// One quality class weighed against the account's plan (AP.4.3).
/// </summary>
/// <param name="QualityClass">
/// The name exactly as it was asked about, so the caller matches its own rows back without normalising anything.
/// </param>
/// <param name="Allowed">Whether the plan in force allows a picture of this class.</param>
/// <param name="RequiresPlanKey">
/// The cheapest plan in the catalogue that does allow it — catalogue order, which is cheapest first. Null when
/// the class is allowed already, and null when no plan allows it at all: a locked class with no plan to name.
/// </param>
/// <param name="RequiresPlanName">That plan's display name, under the same conditions.</param>
public sealed record PlanQualityClassServiceModel(
    string QualityClass,
    bool Allowed,
    string? RequiresPlanKey,
    string? RequiresPlanName);
