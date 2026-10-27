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
    string? TimeZoneId)
{
    /// <summary>The answer for a host with no provider or no price for its picture: nothing to quote.</summary>
    public static GeneratedImageQuoteServiceModel Unavailable(int variants) =>
        new(GeneratedImageQuoteOutcome.Unavailable, null, variants, null, null, null, null, null, 0, false, null, null);
}
