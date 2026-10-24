using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
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

        return new ServiceCollection().AddBillingModule(configuration).BuildServiceProvider();
    }

    private static (string, string?)[] TwoPlans(bool publish = true) =>
    [
        ("Billing:PublishPlans", publish ? "true" : "false"),
        ("Billing:Currency", "USD"),
        ("Billing:Plans:0:Key", "pro"),
        ("Billing:Plans:0:Name", "Pro"),
        ("Billing:Plans:0:MonthlyPrice", "49"),
        ("Billing:Plans:0:AnnualMonthlyPrice", "39"),
        ("Billing:Plans:0:Credits", "18000"),
        ("Billing:Plans:0:ExampleMix", "150 recipe drafts"),
        ("Billing:Plans:0:DailyImageCap", "75"),
        ("Billing:Plans:0:QualityClasses:0", "draft"),
        ("Billing:Plans:1:Key", "creator"),
        ("Billing:Plans:1:Name", "Creator"),
        ("Billing:Plans:1:MonthlyPrice", "19"),
        ("Billing:Plans:1:AnnualMonthlyPrice", "15"),
        ("Billing:Plans:1:Credits", "6000"),
        ("Billing:Plans:1:ExampleMix", "50 recipe drafts"),
        ("Billing:Plans:1:DailyImageCap", "25"),
        ("Billing:Plans:1:Recommended", "true"),
        ("Billing:Packs:0:Key", "large"),
        ("Billing:Packs:0:Credits", "20000"),
        ("Billing:Packs:0:Price", "60"),
        ("Billing:Packs:1:Key", "small"),
        ("Billing:Packs:1:Credits", "2500"),
        ("Billing:Packs:1:Price", "10"),
        ("Billing:Rates:TextDraft", "10"),
        ("Billing:Rates:LongFormSection", "10"),
        ("Billing:Rates:ImageDraft", "25"),
        ("Billing:Rates:ImageStandard", "50"),
        ("Billing:Rates:ImageHigh", "90"),
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
        Assert.Equal(25, rates.ImageDraft);
        Assert.Equal(50, rates.ImageStandard);
        Assert.Equal(90, rates.ImageHigh);
    }

    /// <summary>
    /// Nothing about what work costs leaks from a catalogue that is not published, any more than a price does.
    /// </summary>
    [Fact]
    public void An_unpublished_catalogue_states_no_rates()
    {
        var rates = Read(TwoPlans(publish: false)).Rates;

        Assert.Equal(0, rates.ImageStandard);
        Assert.Equal(0, rates.TextDraft);
    }

    [Fact]
    public void The_recommended_plan_is_the_one_the_catalogue_flags()
    {
        var catalogue = Read(TwoPlans());

        Assert.Equal("creator", catalogue.Plans.Single(plan => plan.Recommended).Key);
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
            [.. TwoPlans(), ("Billing:Plans:1:TrialDays", "0")]
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
            "a published rate is nought",
            [.. TwoPlans(), ("Billing:Rates:ImageStandard", "0")]
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

    /// <summary>A deployment that publishes nothing is the default, and it is not an error.</summary>
    [Fact]
    public void An_empty_catalogue_that_is_not_published_starts()
    {
        var catalogue = Read(("Billing:PublishPlans", "false"));

        Assert.Empty(catalogue.Plans);
    }
}
