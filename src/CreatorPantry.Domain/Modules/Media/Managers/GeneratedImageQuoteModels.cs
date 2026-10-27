namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>Whether a request for pictures could go ahead right now (AP.2.6).</summary>
public enum GeneratedImageQuoteOutcome
{
    Unspecified = 0,

    /// <summary>The allowance covers the whole request.</summary>
    Affordable = 1,

    /// <summary>It does not; <see cref="GeneratedImageQuoteServiceModel.AffordableVariants"/> says how many it would.</summary>
    Exhausted = 2,

    /// <summary>AI assistance is switched off for the account.</summary>
    Suspended = 3,

    /// <summary>This host has no image provider, or cannot price the picture it would make. No figures.</summary>
    Unavailable = 4,
}

/// <summary>
/// What a request for pictures would cost and leave, for the creator to see before they submit it (PRICE-004).
/// </summary>
/// <param name="Outcome">Whether the request could go ahead right now.</param>
/// <param name="Unit">What the amounts are counted in — <c>Credits</c> for every allowance this product offers.</param>
/// <param name="Variants">How many pictures were asked about.</param>
/// <param name="PerPicture">One picture's price. Null when it cannot be priced.</param>
/// <param name="Total">The whole request's price — the hold its admission would take. Null when it cannot be priced.</param>
/// <param name="Allowance">The period's spendable allowance.</param>
/// <param name="Remaining">What is left of it now.</param>
/// <param name="RemainingAfter">What would be left after this request; null unless it is affordable.</param>
/// <param name="AffordableVariants">How many pictures what is left covers now.</param>
/// <param name="LeavesNearlySpent">Whether going ahead leaves the allowance nearly spent (USAGE-010).</param>
/// <param name="ResetsAt">When the allowance comes back; null for a suspension or no provider.</param>
/// <param name="TimeZoneId">The zone <paramref name="ResetsAt"/> is a local midnight in, so it is shown on the right day.</param>
/// <param name="QualityClass">
/// The class priced — the one asked about, or the default (AP.3.1). Null on a host that offers no classes.
/// </param>
/// <remarks>
/// <strong>Every figure is the server's.</strong> The browser renders it and works nothing out, so the price
/// shown is the price admission charges (AP.2.6). It is a snapshot: admission still decides.
/// </remarks>
public sealed record GeneratedImageQuoteServiceModel(
    GeneratedImageQuoteOutcome Outcome,
    string? Unit,
    int Variants,
    decimal? PerPicture,
    decimal? Total,
    decimal? Allowance,
    decimal? Remaining,
    decimal? RemainingAfter,
    int AffordableVariants,
    bool LeavesNearlySpent,
    DateTimeOffset? ResetsAt,
    string? TimeZoneId,
    GeneratedImageQualityClass? QualityClass = null)
{
    /// <summary>The answer for a host with no provider or no price for its picture: nothing to quote.</summary>
    public static GeneratedImageQuoteServiceModel Unavailable(int variants, GeneratedImageQualityClass? qualityClass = null) =>
        new(GeneratedImageQuoteOutcome.Unavailable, null, variants, null, null, null, null, null, 0, false, null, null, qualityClass);
}

/// <summary>One quality class a creator may ask for, and what one picture of it costs (AP.3.1, B-55).</summary>
/// <param name="QualityClass">The class, as a request names it.</param>
/// <param name="PerPicture">
/// Credits per picture: the rate this host charges for the model, resolution and quality the class sends, read
/// the way admission reads it. Exact, not typical — a picture's price is known before it is made.
/// </param>
/// <param name="IsDefault">Whether a request that names no class is made at this one.</param>
/// <remarks>
/// <strong>No model, no quality, no resolution.</strong> A request names a class and never a model, so a page
/// choosing between them needs the class and its price and nothing it could send instead.
/// </remarks>
public sealed record GeneratedImageQualityClassPriceServiceModel(
    GeneratedImageQualityClass QualityClass, decimal PerPicture, bool IsDefault);

/// <summary>The quality classes this host offers, in ascending order, with their prices (AP.3.1).</summary>
/// <param name="DefaultClass">What a request that names none is made at. Null when no class is offered.</param>
/// <param name="Unit">What <see cref="GeneratedImageQualityClassPriceServiceModel.PerPicture"/> is counted in: <c>Credits</c>.</param>
/// <param name="Classes">
/// Every offered class. Empty on a host that offers none, where every picture is the deployment's own default.
/// </param>
/// <remarks>
/// The price list, not a promise about the asker's balance: what a particular request would cost and leave is
/// the quote's. Every figure is the server's (PRICE-013), so the picker and the landing page format and never
/// multiply.
/// </remarks>
public sealed record GeneratedImageQualityClassesServiceModel(
    GeneratedImageQualityClass? DefaultClass,
    string Unit,
    IReadOnlyList<GeneratedImageQualityClassPriceServiceModel> Classes);
