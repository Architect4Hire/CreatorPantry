using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// A stored plan the catalogue no longer has is warned about once an hour per row, not once per request (AP.4.2):
/// admission resolves a plan on every AI and picture request.
/// </summary>
public sealed class RemovedPlanWarningsTests
{
    private static readonly DateTimeOffset Noon = new(2037, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_row_is_warned_about_once_then_not_again_within_the_interval()
    {
        var warnings = new RemovedPlanWarnings();
        var row = Guid.NewGuid();

        Assert.True(warnings.ShouldWarn(row, Noon));
        Assert.False(warnings.ShouldWarn(row, Noon.AddMinutes(1)));
        Assert.False(warnings.ShouldWarn(row, Noon + BillingPolicy.RemovedPlanWarningInterval - TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void A_row_is_warned_about_again_once_the_interval_has_passed()
    {
        var warnings = new RemovedPlanWarnings();
        var row = Guid.NewGuid();

        warnings.ShouldWarn(row, Noon);

        Assert.True(warnings.ShouldWarn(row, Noon + BillingPolicy.RemovedPlanWarningInterval));
        Assert.False(warnings.ShouldWarn(row, Noon + BillingPolicy.RemovedPlanWarningInterval + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Each_row_has_its_own_interval()
    {
        var warnings = new RemovedPlanWarnings();

        Assert.True(warnings.ShouldWarn(Guid.NewGuid(), Noon));
        Assert.True(warnings.ShouldWarn(Guid.NewGuid(), Noon));
    }

    /// <summary>Many requests at once for one row: exactly one of them logs.</summary>
    [Fact]
    public async Task Concurrent_requests_for_one_row_warn_once()
    {
        var warnings = new RemovedPlanWarnings();
        var row = Guid.NewGuid();

        var answers = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => warnings.ShouldWarn(row, Noon))));

        Assert.Single(answers, warned => warned);
    }
}
