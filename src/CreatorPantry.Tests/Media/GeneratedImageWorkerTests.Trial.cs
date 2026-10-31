using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// A trial's picture gate through the request a creator makes (AP.7.1, B-52): a lifetime cap that is the account's
/// across every workspace it works in and every day, that outlives a workspace being deleted, and that is read from
/// the catalogue's own trial plan — and a trial's end, after which no picture is queued.
/// </summary>
/// <remarks>
/// The same fixture as the plan caps beside it (<c>GeneratedImageWorkerTests.Plans</c>): Billing with a catalogue
/// put in through the options instance the module reads. The workspace's flat limits are raised out of the way, so
/// what refuses is the trial's own line.
/// </remarks>
public sealed partial class GeneratedImageWorkerTests
{
    private const int TrialCap = 10;

    /// <summary>
    /// Four in A, four in B, two in A the next day: the trial's ten. The eleventh is refused in either workspace with
    /// the account's own figures, and nothing is queued for it.
    /// </summary>
    [Fact]
    public async Task A_trials_picture_cap_is_the_accounts_across_its_workspaces_and_days()
    {
        OnTrial();

        await RequestAsync(WorkspaceA, variants: 4);
        await RequestAsync(WorkspaceB, variants: 4);
        _clock.Advance(TimeSpan.FromDays(1));
        await RequestAsync(WorkspaceA, variants: 2);

        foreach (var workspace in new[] { WorkspaceA, WorkspaceB })
        {
            var refused = await RequestRawAsync(workspace);

            Assert.Equal(AiQuotaErrors.TrialImagesSpent, refused.Error!.Code);
            Assert.Equal(TrialCap, refused.Error.Extensions!["trialImageCap"]);
            Assert.DoesNotContain("workspace", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("remaining", refused.Error.Extensions.Keys);
        }

        Assert.Equal(2, (await OperationsAsync(WorkspaceA)).Count);
        Assert.Single(await OperationsAsync(WorkspaceB));
    }

    /// <summary>
    /// Deleting the workspace the pictures were made in gives none of them back: the count is the account's holds,
    /// which carry no workspace, not the workspace's operations.
    /// </summary>
    [Fact]
    public async Task Deleting_a_workspace_does_not_give_a_trials_pictures_back()
    {
        OnTrial();

        await RequestAsync(WorkspaceA, variants: 4);
        await RequestAsync(WorkspaceA, variants: 4);
        _clock.Advance(TimeSpan.FromDays(1));
        await RequestAsync(WorkspaceA, variants: 2);

        // The fixture's own delete, so whatever cascades from a workspace goes with it.
        await DeleteWorkspaceAsync(WorkspaceA);
        Assert.Empty(await OperationsAsync(WorkspaceA));

        var refused = await RequestRawAsync(WorkspaceB);
        Assert.Equal(AiQuotaErrors.TrialImagesSpent, refused.Error!.Code);
        Assert.Empty(await OperationsAsync(WorkspaceB));
    }

    /// <summary>Another account in the same workspace has its own trial, and its own ten.</summary>
    [Fact]
    public async Task Two_accounts_in_one_workspace_do_not_share_a_trials_pictures()
    {
        OnTrial();

        await RequestAsync(WorkspaceA, variants: 4);
        await RequestAsync(WorkspaceA, variants: 4);
        _clock.Advance(TimeSpan.FromDays(1));
        await RequestAsync(WorkspaceA, variants: 2);

        await RequestAsync(WorkspaceA, variants: 4, account: Other);
    }

    /// <summary>
    /// The figure admission enforces is the trial plan's own <c>TrialImageCap</c>, the one the catalogue publishes:
    /// change it there and the next request is judged by it.
    /// </summary>
    [Fact]
    public async Task The_cap_enforced_is_the_one_the_catalogue_entry_holds()
    {
        OnTrial();
        Billing.Plans.Single(plan => plan.Key == "trial").TrialImageCap = 3;

        await RequestAsync(WorkspaceA, variants: 3);

        var refused = await RequestRawAsync(WorkspaceB);
        Assert.Equal(AiQuotaErrors.TrialImagesSpent, refused.Error!.Code);
        Assert.Equal(3, refused.Error.Extensions!["trialImageCap"]);
    }

    /// <summary>A paid plan has no lifetime cap: twelve pictures over two days are all queued.</summary>
    [Fact]
    public async Task A_paid_plan_has_no_lifetime_picture_cap()
    {
        OnTrial();
        Billing.DefaultPlan = "studio";

        await RequestAsync(WorkspaceA, variants: 4);
        await RequestAsync(WorkspaceB, variants: 4);
        _clock.Advance(TimeSpan.FromDays(1));
        await RequestAsync(WorkspaceA, variants: 4);

        Assert.Equal(2, (await OperationsAsync(WorkspaceA)).Count);
    }

    /// <summary>
    /// After the trial's fourteen days no picture is queued in any workspace, and the quote says so before a creator
    /// asks.
    /// </summary>
    [Fact]
    public async Task After_the_trial_ends_no_picture_is_queued_in_any_workspace()
    {
        OnTrial();
        await AddAccountAsync(DefaultAccount, _clock.UtcNow.AddDays(-TrialDaysLong));

        foreach (var workspace in new[] { WorkspaceA, WorkspaceB })
        {
            var refused = await RequestRawAsync(workspace);

            Assert.Equal(AiQuotaErrors.TrialEnded, refused.Error!.Code);
            Assert.Equal(_clock.UtcNow.AddDays(-TrialDaysLong).AddDays(14), refused.Error.Extensions!["trialEndedAt"]);
            Assert.Empty(await OperationsAsync(workspace));
        }

        await using var scope = ScopeFor(WorkspaceA);
        var quote = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .QuoteAsync(2, null, Ct);

        Assert.True(quote.Succeeded, quote.Error?.Code);
        Assert.Equal(GeneratedImageQuoteOutcome.TrialEnded, quote.Value!.Outcome);
    }

    /// <summary>One day before its end, the same account's trial still makes pictures.</summary>
    [Fact]
    public async Task A_trial_with_days_left_still_queues_pictures()
    {
        OnTrial();
        await AddAccountAsync(DefaultAccount, _clock.UtcNow.AddDays(-13));

        await RequestAsync(WorkspaceA, variants: 2);
    }

    /// <summary>
    /// A picture asked for on the trial's last minute and claimed after it ended is not made (AP.7.2, B-61): its
    /// account is asked again at the claim, as a text run's is, and the hold settles at nothing.
    /// </summary>
    [Fact]
    public async Task A_picture_asked_for_before_the_trial_ended_is_not_made_after_it()
    {
        OnTrial();
        await AddAccountAsync(DefaultAccount, _clock.UtcNow.AddDays(-14).AddMinutes(1));

        var requested = await RequestAsync(WorkspaceA, variants: 2);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var summary = await RunAsync();

        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, _generator.Calls);

        var failed = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageFailureCategory.Quota, failed.FailureCategory);
        // The same workspace-neutral sentence as a switched-off account gets (DL.11): a colleague reading
        // this operation learns that the pictures were not made, never whose trial ran out or when.
        Assert.Equal("AI work was not available when these pictures were to be made.", failed.FailureSummary);
        Assert.DoesNotContain("trial", failed.FailureSummary, StringComparison.OrdinalIgnoreCase);

        // And nothing new is queued now that it has ended.
        Assert.Equal(AiQuotaErrors.TrialEnded, (await RequestRawAsync(WorkspaceA)).Error!.Code);
    }

    /// <summary>
    /// The lifetime cap is the trial's own term, not a limit, so switching limits off does not lift it (owner,
    /// 2026-10-29, AP.7.2). The plan's daily cap does switch off with them, as B-56 says: twelve in one day would pass
    /// it, and what stops the eleventh is the trial's ten.
    /// </summary>
    [Fact]
    public async Task With_limits_off_the_trials_picture_cap_still_applies()
    {
        OnTrial();
        Billing.Plans.Single(plan => plan.Key == "trial").DailyImageCap = 1;
        Limits.Enabled = false;

        await RequestAsync(WorkspaceA, variants: 4);
        await RequestAsync(WorkspaceB, variants: 4);
        await RequestAsync(WorkspaceA, variants: 2);

        var refused = await RequestRawAsync(WorkspaceB);
        Assert.Equal(AiQuotaErrors.TrialImagesSpent, refused.Error!.Code);
    }

    /// <summary>Fifteen days old: a day past a fourteen-day trial.</summary>
    private const int TrialDaysLong = 15;

    /// <summary>Every account defaults to a trial of ten pictures; <c>studio</c> beside it is a paid plan.</summary>
    private void OnTrial()
    {
        Limits.ImageInFlightPerWorkspace = 50;
        Limits.ImageVariantsPerWorkspacePerDay = 50;

        Billing.DefaultPlan = "trial";
        Billing.Plans.Add(new PlanOption
        {
            Key = "trial",
            Name = "Trial",
            Credits = 40000,
            DailyImageCap = 10,
            RunningImages = 3,
            Period = PlanAllowancePeriod.Once,
            QualityClasses = ["standard"],
            TrialDays = 14,
            TrialImageCap = TrialCap,
        });
        Billing.Plans.Add(Plan("studio", dailyCap: 50, running: 3));
    }

    private async Task AddAccountAsync(string accountId, DateTimeOffset created)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Users.Add(new ApplicationUser
        {
            Id = accountId,
            UserName = $"{accountId}@example.test",
            Email = $"{accountId}@example.test",
            DisplayName = accountId,
            CreatedAt = created,
        });

        await db.SaveChangesAsync(Ct);
    }
}
