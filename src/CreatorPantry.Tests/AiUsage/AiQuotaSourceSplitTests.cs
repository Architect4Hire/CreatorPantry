using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The order AP.5.2 spends in — the allowance, then grants — and how a settled charge divides back between them, as
/// arithmetic. The database cases are in <see cref="AccountCreditSpendTests"/>.
/// </summary>
public sealed class AiQuotaSourceSplitTests
{
    [Theory]
    [InlineData(100, 250, 100, 0)] // the allowance has room: grants are not touched
    [InlineData(100, 100, 100, 0)] // to the credit
    [InlineData(100, 40, 40, 60)] // split
    [InlineData(100, 0, 0, 100)] // the allowance is spent
    [InlineData(100, -15, 0, 100)] // an overshoot is no room, never a negative share
    public void A_hold_takes_the_allowance_first(decimal hold, decimal allowanceLeft, decimal fromAllowance, decimal fromGrants)
    {
        Assert.Equal((fromAllowance, fromGrants), AiQuotaSourceSplit.Hold(hold, allowanceLeft));
    }

    [Theory]
    [InlineData(100, 40, 60, true)]
    [InlineData(100, 40, 59.9999, false)]
    [InlineData(100, 0, 100, true)]
    [InlineData(100, -20, 100, true)] // an overspent allowance does not eat into grants
    [InlineData(100, 0, 0, false)]
    public void Covers_counts_both_sources(decimal hold, decimal allowanceLeft, decimal grantsLeft, bool covered)
    {
        Assert.Equal(covered, AiQuotaSourceSplit.Covers(hold, allowanceLeft, grantsLeft));
    }

    /// <summary>A hold of 100 taken as 40 from the allowance and 60 from grants, settled at different charges.</summary>
    [Theory]
    [InlineData(100, 40, 60)] // the whole hold: each source keeps what it gave
    [InlineData(70, 40, 30)] // under: the allowance keeps first, the grants keep the next 30
    [InlineData(25, 25, 0)] // under the allowance's part: the grants keep nothing
    [InlineData(0, 0, 0)] // released
    [InlineData(130, 70, 60)] // over: the overshoot lands on the allowance, never on a grant
    public void A_charge_keeps_the_allowance_first_and_never_overcharges_grants(
        decimal charge, decimal toAllowance, decimal fromGrants)
    {
        Assert.Equal((toAllowance, fromGrants), AiQuotaSourceSplit.Charge(charge, 40, 100));
    }

    [Theory]
    [InlineData(70)]
    [InlineData(150)]
    public void A_hold_paid_wholly_from_the_allowance_charges_it_all(decimal charge)
    {
        Assert.Equal((charge, 0m), AiQuotaSourceSplit.Charge(charge, 100, 100));
    }
}
