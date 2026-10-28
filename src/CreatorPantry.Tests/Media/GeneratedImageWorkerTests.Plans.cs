using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// A plan's picture caps (AP.4.2): pictures an account may ask for in a UTC day across every workspace it works
/// in, and pictures it may have generating at once — beside the workspace's flat limits, never instead of them.
/// </summary>
/// <remarks>
/// The fixture registers Billing with no catalogue, as every deployment has today; <see cref="OnPlan"/> gives it
/// one through the options instance the module reads, the same way the 15.3 tests above change a limit.
/// </remarks>
public sealed partial class GeneratedImageWorkerTests
{
    private const string Other = "acct-other";

    private BillingOptions Billing => _provider.GetRequiredService<IOptions<BillingOptions>>().Value;

    [Fact]
    public async Task A_plans_daily_cap_is_the_accounts_across_its_workspaces()
    {
        OnPlan(dailyCap: 3);

        await RequestAsync(WorkspaceA, variants: 2);
        await RequestAsync(WorkspaceB, variants: 1);

        var refused = await RequestRawAsync(WorkspaceA);

        Assert.Equal(LimitErrorCodes.BudgetExhausted, refused.Error!.Code);
        Assert.Equal("User", refused.Error.Extensions!["scope"]);
        Assert.Equal(3, refused.Error.Extensions["limit"]);
        Assert.Equal(LimitDay.NextAfter(_clock.UtcNow), refused.Error.Extensions["resetsAt"]);
        Assert.DoesNotContain("workspace", refused.Error.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing queued for the refusal, in either workspace.
        Assert.Single(await OperationsAsync(WorkspaceA));
        Assert.Single(await OperationsAsync(WorkspaceB));
    }

    [Fact]
    public async Task Two_accounts_in_one_workspace_do_not_share_a_plans_daily_cap()
    {
        OnPlan(dailyCap: 2);

        await RequestAsync(WorkspaceA, variants: 2);
        await RequestAsync(WorkspaceA, variants: 2, account: Other);

        Assert.Equal(2, (await OperationsAsync(WorkspaceA)).Count);
    }

    /// <summary>The workspace's flat day still bounds everyone in it, and is asked first.</summary>
    [Fact]
    public async Task The_workspaces_day_still_applies_beside_the_plans()
    {
        OnPlan(dailyCap: 10);
        Limits.ImageVariantsPerWorkspacePerDay = 3;

        await RequestAsync(WorkspaceA, variants: 2);
        await RequestAsync(WorkspaceA, variants: 1, account: Other);

        var refused = await RequestRawAsync(WorkspaceA);

        Assert.Equal(LimitErrorCodes.BudgetExhausted, refused.Error!.Code);
        Assert.Equal("Workspace", refused.Error.Extensions!["scope"]);
    }

    [Fact]
    public async Task A_replay_is_never_refused_by_the_plans_daily_cap()
    {
        OnPlan(dailyCap: 1);

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();

        var first = await facade.RequestAsync(Request(key: "the-one-key"), Ct);
        var again = await facade.RequestAsync(Request(key: "the-one-key"), Ct);

        Assert.True(again.Succeeded, again.Error?.Code);
        Assert.Equal(first.Value!.Id, again.Value!.Id);
    }

    /// <summary>A plan's caps are limits like the workspace's, and switch off with them.</summary>
    [Fact]
    public async Task With_limits_off_the_plans_caps_do_not_apply()
    {
        OnPlan(dailyCap: 1);
        Limits.Enabled = false;

        var first = await RequestAsync(WorkspaceA, variants: 2);
        await RequestAsync(WorkspaceA, variants: 2);

        var recorded = await OperationAsync(WorkspaceA, first.Id);
        Assert.Null(recorded.RequestedByAccountId);
        Assert.Null(recorded.RunningImagesCap);
    }

    [Fact]
    public async Task A_request_records_its_account_and_its_plans_running_cap()
    {
        OnPlan(running: 2);

        var requested = await RequestAsync(WorkspaceA);

        var recorded = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(DefaultAccount, recorded.RequestedByAccountId);
        Assert.Equal(2, recorded.RunningImagesCap);
    }

    /// <summary>With no plans configured — every deployment today — nothing is recorded and the claim is as before.</summary>
    [Fact]
    public async Task With_no_plans_a_request_records_no_account_and_no_running_cap()
    {
        var requested = await RequestAsync(WorkspaceA);

        var recorded = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Null(recorded.RequestedByAccountId);
        Assert.Null(recorded.RunningImagesCap);

        await using var scope = _provider.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<GeneratedImageClaimRepository>();
        Assert.Equal(requested.Id, (await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct, runningPerAccount: true))!.OperationId);
    }

    [Fact]
    public async Task An_account_at_its_plans_running_cap_waits_while_another_account_is_served()
    {
        OnPlan(running: 1);

        var running = await RequestAsync(WorkspaceA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var waiting = await RequestAsync(WorkspaceA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var theirs = await RequestAsync(WorkspaceA, account: Other);

        await using var scope = _provider.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<GeneratedImageClaimRepository>();

        Assert.Equal(running.Id, (await ClaimAsync(claims))!.OperationId);
        Assert.Equal(theirs.Id, (await ClaimAsync(claims))!.OperationId);
        Assert.Null(await ClaimAsync(claims));

        // Without the account's share, its second request is simply next.
        Assert.Equal(waiting.Id, (await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct))!.OperationId);
    }

    /// <summary>The plan is the account's (B-47), so a picture running in one workspace holds its next in another.</summary>
    [Fact]
    public async Task A_plans_running_cap_counts_across_workspaces()
    {
        OnPlan(running: 1);

        var running = await RequestAsync(WorkspaceA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await RequestAsync(WorkspaceB);

        await using var scope = _provider.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<GeneratedImageClaimRepository>();

        Assert.Equal(running.Id, (await ClaimAsync(claims))!.OperationId);
        Assert.Null(await ClaimAsync(claims));
    }

    /// <summary>
    /// The cap is taken from the plan at the request: a request already waiting keeps its own, and the next
    /// request carries the new one.
    /// </summary>
    [Fact]
    public async Task A_plan_changed_after_a_request_does_not_change_that_requests_running_cap()
    {
        OnPlan(running: 1);

        var running = await RequestAsync(WorkspaceA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var waiting = await RequestAsync(WorkspaceA);

        await PutOnAsync(DefaultAccount, "pro");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var later = await RequestAsync(WorkspaceA);

        await using var scope = _provider.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<GeneratedImageClaimRepository>();

        Assert.Equal(running.Id, (await ClaimAsync(claims))!.OperationId);
        Assert.Equal(later.Id, (await ClaimAsync(claims))!.OperationId);
        Assert.Null(await ClaimAsync(claims));
        Assert.Equal(1, (await OperationAsync(WorkspaceA, waiting.Id)).RunningImagesCap);
    }

    private Task<GeneratedImageClaim?> ClaimAsync(GeneratedImageClaimRepository claims) =>
        claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct, runningPerWorkspace: 10, runningPerAccount: true);

    /// <summary>
    /// Every account defaults to a plan with these caps; <c>pro</c> beside it allows three running, for a change.
    /// </summary>
    private void OnPlan(int dailyCap = 100, int running = 3)
    {
        Billing.DefaultPlan = "studio";
        Billing.Plans.Add(Plan("studio", dailyCap, running));
        Billing.Plans.Add(Plan("pro", dailyCap, 3));
    }

    private static PlanOption Plan(string key, int dailyCap, int running) => new()
    {
        Key = key,
        Name = key,
        Credits = 40000,
        DailyImageCap = dailyCap,
        RunningImages = running,
        Period = PlanAllowancePeriod.Monthly,
        QualityClasses = ["standard"],
    };

    /// <remarks>Through Business: the change is not on the facade until AP.4.4 gives it an audit.</remarks>
    private async Task PutOnAsync(string accountId, string planKey)
    {
        await using var scope = _provider.CreateAsyncScope();

        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            (await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
                .ChangeAsync(accountId, planKey, AccountPlanSource.Ops, null, audit: null, Ct)).Outcome);
    }
}
