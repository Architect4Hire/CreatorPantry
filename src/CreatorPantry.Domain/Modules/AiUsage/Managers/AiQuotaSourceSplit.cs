namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Which source pays for a hold and for its charge: the period's allowance first, then the account's grants
/// (PRICE-008, B-50, AP.5.2). Deterministic arithmetic over figures already rounded to the allowance's scale.
/// </summary>
/// <remarks>
/// One place for the order, so admission, the read-only answers that predict it and posting cannot disagree about
/// where a credit came from or where it goes back to.
/// </remarks>
public static class AiQuotaSourceSplit
{
    /// <summary>
    /// How a hold of <paramref name="hold"/> divides when the allowance has <paramref name="allowanceLeft"/> left:
    /// as much as it has from the allowance, the rest from grants.
    /// </summary>
    public static (decimal FromAllowance, decimal FromGrants) Hold(decimal hold, decimal allowanceLeft)
    {
        var fromAllowance = Math.Min(hold, Math.Max(allowanceLeft, 0m));

        return (fromAllowance, hold - fromAllowance);
    }

    /// <summary>
    /// Whether the allowance left and the account's grants between them cover <paramref name="hold"/>. At the line
    /// exactly is covered.
    /// </summary>
    public static bool Covers(decimal hold, decimal allowanceLeft, decimal grantsLeft) =>
        Math.Max(allowanceLeft, 0m) + Math.Max(grantsLeft, 0m) >= hold;

    /// <summary>
    /// How a settled <paramref name="charge"/> divides between a hold's sources: the allowance keeps first, up to what
    /// it covered; the grants keep the next part, up to what they gave; anything beyond the whole hold lands on the
    /// allowance.
    /// </summary>
    /// <remarks>
    /// <strong>Never more from grants than the hold took from them.</strong> A completion can cost more than its
    /// estimate, and that overshoot is the allowance's to record, as it was before grants (AP.5.2): a grant's
    /// credits are paid for, and charging them beyond a hold the creator saw taken would be a surprise on money.
    /// </remarks>
    public static (decimal ToAllowance, decimal FromGrants) Charge(
        decimal charge, decimal reservedFromAllowance, decimal reservedAmount)
    {
        var grantsGave = reservedAmount - reservedFromAllowance;
        var fromGrants = Math.Min(Math.Max(charge - reservedFromAllowance, 0m), grantsGave);

        return (charge - fromGrants, fromGrants);
    }
}
