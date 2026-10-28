using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// A catalogue whose plans the rest of the configuration would make untrue does not start (AP.4.2, AP.4.3): a
/// plan's picture caps above the workspace's flat ones, which a creator in one workspace could never reach, a
/// plan's monthly credits granted under some other unit or period, a plan naming a quality class that is not one,
/// and a plan that does not allow the quality class a request naming none is made at.
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

        // AP.4.3. A typo would be a class every plan quietly lacks, found by whoever wondered why the top plan
        // locks High.
        { "a plan naming a quality class that is not one", [.. Pro(), ("Billing:Plans:0:QualityClasses:0", "stanndard")] },

        // The default-class case lives in Media.QualityClassStartupTests since AP.4.5: Ai:Images:DefaultClass is
        // Media's, so the check that every plan allows it is Media's, and this section reads none of Media's
        // configuration any more.
    };

    /// <summary>Two classes as <c>Ai:Images</c> configures them, so there is a default to allow or not allow.</summary>
    private static (string, string?)[] Classes() =>
    [
        ("Ai:Images:DefaultClass", "Standard"),
        ("Ai:Images:Classes:Standard:Model", "a-model"),
        ("Ai:Images:Classes:High:Model", "a-model"),
    ];

    /// <summary>The spelling AP.4.1 left open, settled: the catalogue's hyphens and case carry no meaning.</summary>
    [Theory]
    [InlineData("standard")]
    [InlineData("Standard")]
    [InlineData("STANDARD")]
    public void A_plan_may_spell_the_default_class_however_the_landing_page_does(string spelling)
    {
        var options = Options([.. Pro(), .. Classes(), ("Billing:Plans:0:QualityClasses:0", spelling)]);

        Assert.Equal("pro", Assert.Single(options.Value.Plans).Key);
    }

    /// <summary>The hyphenated spellings the catalogue actually ships resolve to the classes they name.</summary>
    [Theory]
    [InlineData("high-2k")]
    [InlineData("High2K")]
    [InlineData("HIGH-4K")]
    public void A_hyphenated_class_name_is_the_class_it_names(string spelling)
    {
        var options = Options(
        [
            .. Pro(),
            .. Classes(),
            ("Billing:Plans:0:QualityClasses:0", "standard"),
            ("Billing:Plans:0:QualityClasses:1", spelling),
        ]);

        Assert.Equal(2, Assert.Single(options.Value.Plans).QualityClasses.Count);
    }

    /// <summary>A host that offers no classes has no default to allow, so nothing to check against.</summary>
    [Fact]
    public void A_catalogue_starts_on_a_host_that_offers_no_quality_classes()
    {
        var options = Options([.. Pro(), ("Ai:Images:DefaultClass", "High")]);

        Assert.Equal("pro", Assert.Single(options.Value.Plans).Key);
    }

    /// <summary>And classes with no plans: nothing to hold to them.</summary>
    [Fact]
    public void Quality_classes_with_no_plans_start()
    {
        var options = Options([.. Classes()]);

        Assert.Empty(options.Value.Plans);
    }

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

        // Billing alone, with the two sections its own validation reads. It no longer reads any of Media's
        // (AP.4.5): the one check that related the two — every plan allowing Ai:Images:DefaultClass — is Media's
        // now, because the default class is Media's.
        return new ServiceCollection()
            .AddLimits(configuration)
            .AddAiUsageModule(configuration)
            .AddBillingModule(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IOptions<BillingOptions>>();
    }
}
