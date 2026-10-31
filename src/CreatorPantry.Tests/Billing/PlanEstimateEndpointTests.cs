using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// <c>GET /api/v1/plans/estimate</c> over the real API host (LP.7, AP.7.2): the landing page's estimator, worked out
/// where the catalogue lives so the browser only formats it (B-55, owner 2026-10-29).
/// </summary>
/// <remarks>
/// The picture rates are this host's own charging table, so each test reads the published rate from the catalogue
/// beside it rather than writing a figure down — the estimate has to agree with the prices the page shows.
/// </remarks>
public sealed class PlanEstimateEndpointTests
{
    private const string Route = "/api/v1/plans/estimate";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Action<IServiceCollection> Catalogue(bool published = true, int creatorDailyCap = 25) =>
        services => services.PostConfigure<BillingOptions>(billing =>
        {
            billing.PublishPlans = published;
            billing.Currency = "USD";
            billing.DefaultPlan = "trial";
            billing.Plans =
            [
                // The trial first and cheapest, and big enough to cover a small month, so a test proves it is
                // never the answer: its credits come once.
                Plan("trial", "Trial", 0, 0, 1000, dailyCap: 5, trial: true),
                Plan("creator", "Creator", 19, 15, 6000, creatorDailyCap),
                Plan("pro", "Pro", 49, 39, 18000, dailyCap: 75),
            ];
            billing.Packs = [new CreditPackOption { Key = "small", Credits = 2500, Price = 10 }];
            billing.Rates = new CreditRatesOption { TextDraft = 10, LongFormSection = 10 };
        })
        // A published plan's daily cap is enforced only with limits on (DL.6), and the test host leaves them off.
        .PostConfigure<LimitsOptions>(limits => limits.Enabled |= published);

    private static PlanOption Plan(
        string key, string name, decimal monthly, decimal annual, int credits, int dailyCap, bool trial = false) => new()
    {
        Key = key,
        Name = name,
        MonthlyPrice = monthly,
        AnnualMonthlyPrice = annual,
        Credits = credits,
        ExampleCounts = new PlanExampleOption { RecipeDrafts = 5, SocialPosts = 5, Pictures = trial ? 1 : 5 },
        DailyImageCap = dailyCap,
        Period = trial ? PlanAllowancePeriod.Once : PlanAllowancePeriod.Monthly,
        RunningImages = 1,
        QualityClasses = ["draft", "standard", "high"],
        Recommended = key == "creator",
        TrialDays = trial ? 14 : null,
        TrialImageCap = trial ? 10 : null,
    };

    [Fact]
    public async Task Writing_alone_is_priced_at_the_menus_rate_and_fits_the_smallest_paid_plan_never_the_trial()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var client = host.Factory.CreateClient();

        var estimate = await GetAsync(client, "?pictures=0&writing=100");

        // A thousand credits would fit the trial's thousand; the trial's come once, so it is not an answer.
        Assert.Equal(1000m, estimate.Credits);
        Assert.Equal("creator", estimate.FitsPlanKey);
        Assert.Equal("Creator", estimate.FitsPlanName);
        Assert.Equal(5000m, estimate.CreditsToSpare);
        Assert.Equal("Pro", estimate.LargestPlanName);
    }

    [Fact]
    public async Task Pictures_are_priced_at_the_default_class_the_catalogue_publishes()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var client = host.Factory.CreateClient();
        var catalogue = (await client.GetFromJsonAsync<PlanCatalogueServiceModel>("/api/v1/plans", Ct))!;
        var standard = catalogue.PictureClasses.Single(row => row.QualityClass == catalogue.DefaultPictureClass);

        var estimate = await GetAsync(client, "?pictures=20&writing=10");

        Assert.Equal(standard.QualityClass, estimate.QualityClass);
        Assert.Equal((20 * standard.PerPicture) + 100m, estimate.Credits);
    }

    [Fact]
    public async Task A_quality_the_catalogue_publishes_is_priced_at_its_own_rate_in_any_spelling()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var client = host.Factory.CreateClient();
        var catalogue = (await client.GetFromJsonAsync<PlanCatalogueServiceModel>("/api/v1/plans", Ct))!;
        var high = catalogue.PictureClasses.Single(row => row.QualityClass == "High");

        var estimate = await GetAsync(client, "?pictures=10&writing=0&qualityClass=high");

        Assert.Equal("High", estimate.QualityClass);
        Assert.Equal(10 * high.PerPicture, estimate.Credits);
    }

    /// <summary>
    /// Credits are not all a plan has to carry: one whose month covers the credits but whose daily cap over thirty
    /// days does not cover the pictures does not fit, and the next one up is the answer.
    /// </summary>
    [Fact]
    public async Task A_plan_whose_daily_cap_cannot_carry_the_pictures_does_not_fit()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue(creatorDailyCap: 1));
        var client = host.Factory.CreateClient();

        Assert.Equal("creator", (await GetAsync(client, "?pictures=30&writing=0&qualityClass=draft")).FitsPlanKey);
        Assert.Equal("pro", (await GetAsync(client, "?pictures=31&writing=0&qualityClass=draft")).FitsPlanKey);
    }

    [Fact]
    public async Task A_month_past_the_largest_plan_names_no_plan_and_says_which_is_largest()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var client = host.Factory.CreateClient();

        var estimate = await GetAsync(client, "?pictures=0&writing=5000");

        Assert.Null(estimate.FitsPlanKey);
        Assert.Null(estimate.CreditsToSpare);
        Assert.Equal("Pro", estimate.LargestPlanName);
    }

    [Fact]
    public async Task Exactly_a_plans_allowance_fits_it_with_none_to_spare()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var client = host.Factory.CreateClient();

        var estimate = await GetAsync(client, "?pictures=0&writing=600");

        Assert.Equal("creator", estimate.FitsPlanKey);
        Assert.Equal(0m, estimate.CreditsToSpare);
    }

    [Theory]
    [InlineData("?pictures=-1&writing=0")]
    [InlineData("?pictures=0&writing=100001")]
    public async Task A_count_out_of_range_is_a_400_with_a_stable_code(string query)
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(Route + query, Ct);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PlanEstimateErrors.Invalid, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task With_no_catalogue_published_there_is_nothing_to_estimate()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue(published: false));
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(Route + "?pictures=10&writing=10", Ct);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PlanEstimateErrors.NotFound, problem.GetProperty("code").GetString());
    }

    /// <summary>Anonymous and the same for every caller with the same counts, so a shared cache may hold it.</summary>
    [Fact]
    public async Task The_estimate_is_anonymous_and_publicly_cacheable()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(Route + "?pictures=10&writing=10", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Public);
    }

    private static async Task<PlanEstimateServiceModel> GetAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(Route + query, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PlanEstimateServiceModel>(Ct))!;
    }
}
