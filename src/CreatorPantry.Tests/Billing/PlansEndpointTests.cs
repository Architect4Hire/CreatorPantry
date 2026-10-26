using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// <c>GET /api/v1/plans</c> over the real API host (LP.6): the one product route a visitor reaches before
/// signing in, and what it is allowed to say.
/// </summary>
public sealed class PlansEndpointTests
{
    private const string Route = "/api/v1/plans";

    private static Action<IServiceCollection> Catalogue(bool published) =>
        services => services.PostConfigure<BillingOptions>(billing =>
        {
            billing.PublishPlans = published;
            billing.Currency = "USD";
            billing.Plans =
            [
                new PlanOption
                {
                    Key = "creator",
                    Name = "Creator",
                    MonthlyPrice = 19,
                    AnnualMonthlyPrice = 15,
                    Credits = 6000,
                    ExampleMix = "50 recipe drafts, 100 social posts and 80 pictures",
                    DailyImageCap = 25,
                    QualityClasses = ["draft", "standard", "high"],
                    Recommended = true,
                },
            ];
            billing.Packs = [new CreditPackOption { Key = "small", Credits = 2500, Price = 10 }];
            billing.Rates = new CreditRatesOption { TextDraft = 10, LongFormSection = 10, ImageDraft = 25, ImageStandard = 50, ImageHigh = 90 };
        });

    /// <summary>
    /// Anonymous on purpose, and the only product route that is: the catalogue is what a deployment chose to
    /// publish, with no account, workspace or usage in it, so there is nothing to authorize against.
    /// </summary>
    [Fact]
    public async Task The_catalogue_is_readable_without_signing_in()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue(published: true));
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(Route, TestContext.Current.CancellationToken);
        var catalogue = await response.Content.ReadFromJsonAsync<PlanCatalogueServiceModel>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = Assert.Single(catalogue!.Plans);
        Assert.Equal("creator", plan.Key);
        Assert.Equal(180m, plan.AnnualPrice);
        Assert.Equal(48m, plan.AnnualSaving);
        Assert.True(plan.Recommended);
        Assert.Single(catalogue.Packs);
        Assert.Equal(50, catalogue.Rates.ImageStandard);
    }

    /// <summary>
    /// The trial's lifetime picture cap travels on the trial alone (B-52), under the name the landing page
    /// decodes, and a paid plan says null rather than leaving the field out.
    /// </summary>
    [Fact]
    public async Task A_trial_states_its_picture_cap_and_a_paid_plan_states_none()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: services =>
        {
            Catalogue(published: true)(services);
            services.PostConfigure<BillingOptions>(billing => billing.Plans.Add(new PlanOption
            {
                Key = "trial",
                Name = "Trial",
                Credits = 1000,
                ExampleMix = "20 recipe drafts",
                DailyImageCap = 5,
                TrialDays = 14,
                TrialImageCap = 10,
            }));
        });
        var client = host.Factory.CreateClient();

        var body = await client.GetStringAsync(Route, TestContext.Current.CancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var plans = document.RootElement.GetProperty("plans").EnumerateArray().ToDictionary(
            plan => plan.GetProperty("key").GetString()!);

        Assert.Equal(10, plans["trial"].GetProperty("trialImageCap").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, plans["creator"].GetProperty("trialImageCap").ValueKind);
    }

    /// <summary>The same answer for every caller is what lets a shared cache hold it.</summary>
    [Fact]
    public async Task The_catalogue_may_be_cached_publicly()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue(published: true));
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.Public);
        Assert.Equal(TimeSpan.FromMinutes(5), cacheControl.MaxAge);
    }

    /// <summary>
    /// With the flag off the route still answers — an unpublished catalogue is a state, not a missing
    /// resource — and says nothing about the plans the deployment has configured.
    /// </summary>
    [Fact]
    public async Task An_unpublished_catalogue_answers_with_nothing_in_it()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue(published: false));
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(Route, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("creator", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("19", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The route reads configuration and nothing else, so a signed-in caller gets the identical answer. If that
    /// ever stops being true, the catalogue has grown something that belongs behind a session.
    /// </summary>
    [Fact]
    public async Task The_answer_does_not_depend_on_who_is_asking()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue(published: true));
        var client = host.Factory.CreateClient();

        var anonymous = await client.GetStringAsync(Route, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add("Cookie", "cp-session=not-a-real-session");
        var withCookie = await client.GetStringAsync(Route, TestContext.Current.CancellationToken);

        Assert.Equal(anonymous, withCookie);
    }
}
