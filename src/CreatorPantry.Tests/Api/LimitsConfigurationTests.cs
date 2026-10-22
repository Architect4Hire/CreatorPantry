using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Api;

/// <summary>The 15.3 limits as configuration states them, and what a request is charged.</summary>
public sealed class LimitsConfigurationTests
{
    [Fact]
    public void The_defaults_are_the_approved_table()
    {
        var limits = new LimitsOptions();

        Assert.True(limits.Enabled);
        Assert.Equal(TokenBucketRule.PerMinute(10, 20), limits.RateFor(LimitedOperation.AiRequest).User);
        Assert.Equal(TokenBucketRule.PerMinute(30, 60), limits.RateFor(LimitedOperation.AiRequest).Workspace);
        Assert.Equal(TokenBucketRule.PerMinute(5, 10), limits.RateFor(LimitedOperation.PdfExport).User);
        Assert.Null(limits.RateFor(LimitedOperation.PdfExport).Workspace);
        Assert.Null(limits.RateFor(LimitedOperation.Nutrition).User);
        Assert.Equal(TimeSpan.FromHours(1), limits.RateFor(LimitedOperation.WorkspaceCreate).User!.Per);
        Assert.Equal(200, limits.ImageVariantsPerWorkspacePerDay);
        Assert.Equal(100, limits.PublicationsPerWorkspacePerDay);
        Assert.Equal(4, limits.AiRunningPerWorkspace);

        // Every operation has a rule, so none is unlimited by omission.
        Assert.All(Enum.GetValues<LimitedOperation>(), operation =>
        {
            var rule = limits.RateFor(operation);
            Assert.True(rule.User is { IsValid: true } || rule.Workspace is { IsValid: true }, operation.ToString());
        });
    }

    [Fact]
    public void Configuration_overrides_one_number_and_keeps_the_rest()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Rates:Search:User:Burst"] = "7",
                ["Limits:ImageVariantsPerWorkspacePerDay"] = "50",
            })
            .Build();

        var limits = new ServiceCollection().AddLimits(configuration).BuildServiceProvider()
            .GetRequiredService<IOptions<LimitsOptions>>().Value;

        Assert.Equal(7, limits.RateFor(LimitedOperation.Search).User!.Burst);
        Assert.Equal(120, limits.RateFor(LimitedOperation.Search).User!.Refill);
        Assert.Equal(50, limits.ImageVariantsPerWorkspacePerDay);
        Assert.Equal(TokenBucketRule.PerMinute(10, 20), limits.RateFor(LimitedOperation.AiRequest).User);
    }

    [Theory]
    [InlineData("AiRunningPerWorkspace")]
    [InlineData("ImageRunningPerWorkspace")]
    [InlineData("PublicationsPerWorkspacePerDay")]
    public void A_cap_of_nought_is_refused_rather_than_stalling_a_queue(string cap)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Limits:{cap}"] = "0" })
            .Build();

        var limits = new ServiceCollection().AddLimits(configuration).BuildServiceProvider()
            .GetRequiredService<IOptions<LimitsOptions>>();

        Assert.Throws<OptionsValidationException>(() => limits.Value);
    }

    [Theory]
    [InlineData(ChannelPostDecision.Regenerate, 1)]
    [InlineData(ChannelPostDecision.Accept, 0)]
    [InlineData(ChannelPostDecision.Reject, 0)]
    [InlineData(ChannelPostDecision.Reaffirm, 0)]
    public void Only_a_regeneration_is_charged_as_an_ai_request(ChannelPostDecision decision, int cost)
    {
        ILimitCost model = new ChannelPostDispositionViewModel { Decision = decision };

        Assert.Equal(cost, model.LimitCost);
    }

    [Fact]
    public void The_daily_budget_resets_at_the_next_utc_midnight()
    {
        var lateInSydney = new DateTimeOffset(2026, 10, 21, 23, 30, 0, TimeSpan.FromHours(11));

        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), LimitDay.StartOf(lateInSydney));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), LimitDay.NextAfter(lateInSydney));
    }
}
