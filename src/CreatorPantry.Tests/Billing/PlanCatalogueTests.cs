using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The published plan catalogue (LP.6): what the figures come to, and which catalogues a host refuses to start
/// with. The catalogue is configuration rather than a table, so startup is the only place a wrong price can be
/// caught before a visitor reads it.
/// </summary>
public sealed class PlanCatalogueTests
{
    private static IServiceProvider Host(params (string Key, string? Value)[] settings)
    {
        // Later settings override earlier ones, so a case below states a whole catalogue and then the one
        // value that makes it wrong. An in-memory source refuses a repeated key outright.
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection().AddBillingModule(configuration);

        // The catalogue prices a picture by asking Media what it offers (AP.4.5), so the two facades that read
        // come with it. Registered rather than AddMediaModule: these are the only parts of Media the catalogue
        // touches, and both are singletons over configuration with no database behind them.
        services.AddOptions<GeneratedImageQualityClassOptions>()
            .Bind(configuration.GetSection(GeneratedImageQualityClassOptions.SectionName));
        services.AddOptions<AiQuotaOptions>().Bind(configuration.GetSection(AiQuotaOptions.SectionName));
        services.AddSingleton<IAiImagePriceFacade, AiImagePriceFacade>();
        services.AddSingleton<IGeneratedImageQualityOfferFacade, GeneratedImageQualityOfferFacade>();

        return services.BuildServiceProvider();
    }

    private static (string, string?)[] TwoPlans(bool publish = true) =>
    [
        ("Billing:PublishPlans", publish ? "true" : "false"),
        ("Billing:Currency", "USD"),
        ("Billing:DefaultPlan", "creator"),
        ("Billing:Plans:0:Key", "pro"),
        ("Billing:Plans:0:Name", "Pro"),
        ("Billing:Plans:0:MonthlyPrice", "49"),
        ("Billing:Plans:0:AnnualMonthlyPrice", "39"),
        ("Billing:Plans:0:Credits", "18000"),
        ("Billing:Plans:0:ExampleCounts:RecipeDrafts", "150"),
        ("Billing:Plans:0:ExampleCounts:SocialPosts", "300"),
        ("Billing:Plans:0:ExampleCounts:Pictures", "200"),
        ("Billing:Plans:0:DailyImageCap", "75"),
        ("Billing:Plans:0:Period", "Monthly"),
        ("Billing:Plans:0:RunningImages", "2"),
        ("Billing:Plans:0:QualityClasses:0", "draft"),
        ("Billing:Plans:1:Key", "creator"),
        ("Billing:Plans:1:Name", "Creator"),
        ("Billing:Plans:1:MonthlyPrice", "19"),
        ("Billing:Plans:1:AnnualMonthlyPrice", "15"),
        ("Billing:Plans:1:Credits", "6000"),
        ("Billing:Plans:1:ExampleCounts:RecipeDrafts", "50"),
        ("Billing:Plans:1:ExampleCounts:SocialPosts", "100"),
        ("Billing:Plans:1:ExampleCounts:Pictures", "80"),
        ("Billing:Plans:1:DailyImageCap", "25"),
        ("Billing:Plans:1:Period", "Monthly"),
        ("Billing:Plans:1:RunningImages", "1"),
        ("Billing:Plans:1:Recommended", "true"),
        ("Billing:Packs:0:Key", "large"),
        ("Billing:Packs:0:Credits", "20000"),
        ("Billing:Packs:0:Price", "60"),
        ("Billing:Packs:1:Key", "small"),
        ("Billing:Packs:1:Credits", "2500"),
        ("Billing:Packs:1:Price", "10"),
        ("Billing:Rates:TextDraft", "10"),
        ("Billing:Rates:LongFormSection", "10"),
    ];

    /// <summary>A third plan: the trial, free, with its length and its lifetime picture cap (B-48, B-52).</summary>
    private static (string, string?)[] WithTrial(bool publish = true) =>
    [
        .. TwoPlans(publish),
        ("Billing:Plans:2:Key", "trial"),
        ("Billing:Plans:2:Name", "Trial"),
        ("Billing:Plans:2:MonthlyPrice", "0"),
        ("Billing:Plans:2:AnnualMonthlyPrice", "0"),
        ("Billing:Plans:2:Credits", "1000"),
        ("Billing:Plans:2:ExampleCounts:RecipeDrafts", "20"),
        ("Billing:Plans:2:ExampleCounts:SocialPosts", "20"),
        ("Billing:Plans:2:ExampleCounts:Pictures", "10"),
        ("Billing:Plans:2:DailyImageCap", "5"),
        ("Billing:Plans:2:Period", "Once"),
        ("Billing:Plans:2:RunningImages", "1"),
        ("Billing:Plans:2:TrialDays", "14"),
        ("Billing:Plans:2:TrialImageCap", "10"),
    ];

    private static PlanCatalogueServiceModel Read(params (string, string?)[] settings) =>
        Host(settings).GetRequiredService<IPlanCatalogueFacade>().Published();

    [Fact]
    public void A_published_catalogue_lists_its_plans_cheapest_first_and_its_packs_smallest_first()
    {
        var catalogue = Read(TwoPlans());

        Assert.Equal(["creator", "pro"], catalogue.Plans.Select(plan => plan.Key));
        Assert.Equal(["small", "large"], catalogue.Packs.Select(pack => pack.Key));
        Assert.Equal("USD", catalogue.Currency);
    }

    /// <summary>
    /// The yearly price and the saving are worked out from the two prices the owner sets, never typed. A
    /// catalogue therefore cannot advertise a saving its own prices do not add up to.
    /// </summary>
    [Fact]
    public void The_yearly_price_and_the_saving_are_derived_from_the_two_prices()
    {
        var creator = Read(TwoPlans()).Plans.Single(plan => plan.Key == "creator");

        Assert.Equal(180m, creator.AnnualPrice);
        Assert.Equal(48m, creator.AnnualSaving);
    }

    /// <summary>
    /// An unpublished catalogue is a state, not a fault: the shape still answers, with nothing in it, and the
    /// landing page renders no pricing section. Nothing about the configured plans leaks while it is off.
    /// </summary>
    [Fact]
    public void An_unpublished_catalogue_answers_with_nothing_in_it()
    {
        var catalogue = Read(TwoPlans(publish: false));

        Assert.Empty(catalogue.Plans);
        Assert.Empty(catalogue.Packs);
        Assert.Equal("USD", catalogue.Currency);
    }

    /// <summary>The menu's figures, published beside the plans they are spent against (LP.7).</summary>
    [Fact]
    public void A_published_catalogue_states_what_each_kind_of_action_costs()
    {
        var rates = Read(TwoPlans()).Rates;

        Assert.Equal(10, rates.TextDraft);
        Assert.Equal(10, rates.LongFormSection);
    }

    /// <summary>
    /// A picture's price is not on the menu's rates any more (AP.4.5): the three figures that used to be typed
    /// there were a second copy of what admission charges, and <c>PictureClasses</c> reads the charging table.
    /// </summary>
    [Fact]
    public void The_rates_carry_no_picture_figure_to_drift_from_the_charging_table() =>
        Assert.Equal(
            ["TextDraft", "LongFormSection"],
            typeof(CreditRatesServiceModel).GetProperties().Select(property => property.Name));

    /// <summary>
    /// Nothing about what work costs leaks from a catalogue that is not published, any more than a price does.
    /// </summary>
    [Fact]
    public void An_unpublished_catalogue_states_no_rates()
    {
        var rates = Read(TwoPlans(publish: false)).Rates;

        Assert.Equal(0, rates.TextDraft);
        Assert.Equal(0, rates.LongFormSection);
    }

    /// <summary>
    /// The trial's gate is a lifetime picture cap rather than a card on file (B-52), so the catalogue states it
    /// on the trial and on no other plan, and the page can say the figure admission will hold it to.
    /// </summary>
    [Fact]
    public void A_published_trial_states_how_many_pictures_it_may_make_in_all()
    {
        var plans = Read(WithTrial()).Plans;

        var trial = plans.Single(plan => plan.Key == "trial");
        Assert.Equal(14, trial.TrialDays);
        Assert.Equal(10, trial.TrialImageCap);
        Assert.All(plans.Where(plan => plan.Key != "trial"), plan => Assert.Null(plan.TrialImageCap));
    }

    [Fact]
    public void The_recommended_plan_is_the_one_the_catalogue_flags()
    {
        var catalogue = Read(TwoPlans());

        Assert.Equal("creator", catalogue.Plans.Single(plan => plan.Recommended).Key);
    }

    /// <summary>
    /// What a plan's credits make in a month is published as counts, which the card leads with, and as the
    /// sentence <c>exampleMix</c> carried before there were counts — the same figures both ways (owner,
    /// 2026-10-28).
    /// </summary>
    [Fact]
    public void A_published_plan_states_its_example_as_counts_and_as_the_same_sentence()
    {
        var creator = Read(TwoPlans()).Plans.Single(plan => plan.Key == "creator");

        Assert.Equal(new PlanExampleServiceModel(50, 100, 80), creator.ExampleCounts);
        Assert.Equal("50 recipe drafts, 100 social posts and 80 pictures", creator.ExampleMix);
    }

    /// <summary>A mix with no social posts leaves them out of the sentence rather than saying "0 social posts".</summary>
    [Fact]
    public void An_example_with_no_social_posts_says_none()
    {
        var creator = Read([.. TwoPlans(), ("Billing:Plans:1:ExampleCounts:SocialPosts", "0")])
            .Plans.Single(plan => plan.Key == "creator");

        Assert.Equal("50 recipe drafts and 80 pictures", creator.ExampleMix);
    }

    public static TheoryData<string, (string, string?)[]> RefusedCatalogues() => new()
    {
        { "publishes nothing", [("Billing:PublishPlans", "true")] },
        {
            "two plans share a key",
            [.. TwoPlans(), ("Billing:Plans:1:Key", "pro")]
        },
        {
            "two plans are recommended",
            [.. TwoPlans(), ("Billing:Plans:0:Recommended", "true")]
        },
        {
            "a year costs more than twelve months",
            [.. TwoPlans(), ("Billing:Plans:1:AnnualMonthlyPrice", "25")]
        },
        {
            "a plan has no name",
            [.. TwoPlans(), ("Billing:Plans:1:Name", "")]
        },
        {
            "a trial lasts no days",
            [.. WithTrial(), ("Billing:Plans:2:TrialDays", "0")]
        },
        {
            "a paid plan has a trial picture cap",
            [.. TwoPlans(), ("Billing:Plans:1:TrialImageCap", "10")]
        },
        {
            "a trial picture cap is nought",
            [.. WithTrial(), ("Billing:Plans:2:TrialImageCap", "0")]
        },
        {
            "a published trial has no picture cap",
            [.. WithTrial(), ("Billing:Plans:2:TrialImageCap", null)]
        },
        {
            "a published plan states no example",
            [.. TwoPlans(), ("Billing:Plans:1:ExampleCounts:RecipeDrafts", null), ("Billing:Plans:1:ExampleCounts:SocialPosts", null), ("Billing:Plans:1:ExampleCounts:Pictures", null)]
        },
        {
            "a published example has no pictures",
            [.. TwoPlans(), ("Billing:Plans:1:ExampleCounts:Pictures", "0")]
        },
        {
            "an example count is negative",
            [.. TwoPlans(), ("Billing:Plans:1:ExampleCounts:SocialPosts", "-1")]
        },
        {
            "a trial's example makes more pictures than its cap",
            [.. WithTrial(), ("Billing:Plans:2:ExampleCounts:Pictures", "11")]
        },
        {
            "a pack has no credits",
            [.. TwoPlans(), ("Billing:Packs:0:Credits", "0")]
        },
        {
            "the currency is not a code",
            [.. TwoPlans(), ("Billing:Currency", "dollars")]
        },
        {
            // A written rate, since AP.4.5 took the picture figures out of this section: a picture's price is
            // the charging table's, and this validation guards the figures that are still typed here.
            "a published rate is nought",
            [.. TwoPlans(), ("Billing:Rates:LongFormSection", "0")]
        },
    };

    /// <summary>
    /// Each of these would otherwise be found by a visitor reading a wrong price. A host that would publish one
    /// does not start.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedCatalogues))]
    public void A_catalogue_that_would_publish_a_wrong_price_refuses_to_start(string why, (string, string?)[] settings)
    {
        var options = Host(settings).GetRequiredService<IOptions<BillingOptions>>();

        var error = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.NotEmpty(error.Failures);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    public static TheoryData<string, (string, string?)[]> UnresolvableCatalogues() => new()
    {
        { "there is no default plan", [.. TwoPlans(), ("Billing:DefaultPlan", null)] },
        { "the default names no plan", [.. TwoPlans(), ("Billing:DefaultPlan", "enterprise")] },
        { "a plan has no allowance period", [.. TwoPlans(), ("Billing:Plans:1:Period", null)] },
        { "a paid plan grants its credits once", [.. TwoPlans(), ("Billing:Plans:1:Period", "Once")] },
        { "a trial renews monthly", [.. WithTrial(), ("Billing:Plans:2:Period", "Monthly")] },
        { "a plan may have no picture running", [.. TwoPlans(), ("Billing:Plans:1:RunningImages", "0")] },
        { "a plan key is wider than its column", [.. TwoPlans(), ("Billing:Plans:1:Key", new string('a', 65))] },
        {
            "a plan lists a quality class twice",
            [.. TwoPlans(), ("Billing:Plans:0:QualityClasses:1", "Draft")]
        },
    };

    /// <summary>
    /// What an account resolves to is checked at startup as well (AP.4.1). Each of these would otherwise surface
    /// on a creator's request — a default that is not a plan, a plan with no allowance period — or, for the key,
    /// as a plan nobody can be put on. Unpublished, because none of them is about what the page shows.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnresolvableCatalogues))]
    public void A_catalogue_an_account_could_not_be_resolved_against_refuses_to_start(
        string why, (string, string?)[] settings)
    {
        var options = Host([.. settings, ("Billing:PublishPlans", "false")]).GetRequiredService<IOptions<BillingOptions>>();

        var error = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.NotEmpty(error.Failures);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// No plans means no default to name: every deployment today, which must start, and whose accounts resolve to
    /// no plan at all (<c>AccountPlanResolutionTests</c>).
    /// </summary>
    [Fact]
    public void An_empty_catalogue_needs_no_default_plan()
    {
        var options = Host(("Billing:PublishPlans", "false")).GetRequiredService<IOptions<BillingOptions>>();

        Assert.Null(options.Value.DefaultPlan);
        Assert.Empty(options.Value.Plans);
    }

    /// <summary>A deployment that publishes nothing is the default, and it is not an error.</summary>
    [Fact]
    public void An_empty_catalogue_that_is_not_published_starts()
    {
        var catalogue = Read(("Billing:PublishPlans", "false"));

        Assert.Empty(catalogue.Plans);
    }
}
