using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// A catalogue whose plans the rest of the configuration would make untrue does not start (AP.4.2): a plan's
/// picture caps above the workspace's flat ones, which a creator in one workspace could never reach, and a plan's
/// monthly credits granted under some other unit or period.
/// </summary>
public sealed class PlanLimitsStartupTests
{
    /// <summary>One plan, Pro, at development's figures; each case below breaks one thing about it.</summary>
    private static (string, string?)[] Pro() =>
    [
        ("Billing:DefaultPlan", "pro"),
        ("Billing:Plans:0:Key", "pro"),
        ("Billing:Plans:0:Name", "Pro"),
        ("Billing:Plans:0:Credits", "18000"),
        ("Billing:Plans:0:DailyImageCap", "75"),
        ("Billing:Plans:0:Period", "Monthly"),
        ("Billing:Plans:0:RunningImages", "2"),
        ("Billing:Plans:0:QualityClasses:0", "standard"),
    ];

    [Fact]
    public void A_catalogue_within_the_workspace_limits_starts()
    {
        var billing = Options([.. Pro()]).Value;

        Assert.Equal("pro", Assert.Single(billing.Plans).Key);
    }

    public static TheoryData<string, (string, string?)[]> RefusedCatalogues() => new()
    {
        {
            "a plan's daily pictures above the workspace's",
            [.. Pro(), ("Limits:ImageVariantsPerWorkspacePerDay", "50")]
        },
        {
            "a plan's running pictures above the workspace's running share",
            [.. Pro(), ("Billing:Plans:0:RunningImages", "4")]
        },
        {
            "a plan's running pictures above what a workspace may have waiting",
            [.. Pro(), ("Limits:ImageInFlightPerWorkspace", "1"), ("Limits:ImageRunningPerWorkspace", "1"), ("Billing:Plans:0:RunningImages", "2")]
        },
        { "monthly credits granted weekly", [.. Pro(), ("Ai:Quota:DefaultPeriodLength", "Weekly"), ("Ai:Quota:DefaultPeriodAnchor", "1")] },
        { "credits granted in another unit", [.. Pro(), ("Ai:Quota:DefaultUnit", "Tokens")] },
    };

    [Theory]
    [MemberData(nameof(RefusedCatalogues))]
    public void A_catalogue_the_rest_of_the_configuration_would_make_untrue_refuses_to_start(
        string why, (string, string?)[] settings)
    {
        var options = Options(settings);

        var error = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.NotEmpty(error.Failures);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>With limits off, neither cap applies, so neither can be out of reach of the other.</summary>
    [Fact]
    public void With_limits_off_a_plan_above_the_workspace_caps_starts()
    {
        var options = Options(
        [
            .. Pro(),
            ("Limits:Enabled", "false"),
            ("Limits:ImageVariantsPerWorkspacePerDay", "50"),
            ("Billing:Plans:0:RunningImages", "4"),
        ]);

        Assert.Equal(4, Assert.Single(options.Value.Plans).RunningImages);
    }

    /// <summary>No plans, nothing to compare: every deployment today, on whatever quota terms it likes.</summary>
    [Fact]
    public void An_empty_catalogue_starts_on_any_quota_terms()
    {
        var options = Options([("Ai:Quota:DefaultPeriodLength", "Weekly"), ("Ai:Quota:DefaultPeriodAnchor", "1")]);

        Assert.Empty(options.Value.Plans);
    }

    /// <summary>The flat running share is three since AP.4.2, so development's Studio can be reached.</summary>
    [Fact]
    public void The_workspace_running_share_reaches_the_most_generous_development_plan() =>
        Assert.Equal(3, new LimitsOptions().ImageRunningPerWorkspace);

    private static IOptions<BillingOptions> Options((string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        return new ServiceCollection()
            .AddLimits(configuration)
            .AddAiUsageModule(configuration)
            .AddBillingModule(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IOptions<BillingOptions>>();
    }
}
