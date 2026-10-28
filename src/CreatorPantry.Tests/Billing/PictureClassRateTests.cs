using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The landing page prices a picture at what the product charges for it (PRICE-004/006/013, AP.4.5): one rate
/// source, read per quality class, with no figure written down on the way.
/// </summary>
/// <remarks>
/// Against the real API host, because the claim worth testing is that the published figure and this host's own
/// charging agree — which needs the host's real <c>Ai:Images</c> and <c>Ai:Quota:ImageRates</c>, not a fixture
/// that could be wrong in the same direction twice.
/// </remarks>
public sealed class PictureClassRateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <strong>The published price per class is the charged price per class.</strong> Asked of the same host's
    /// admission, class by class, rather than compared with a table in this file.
    /// </summary>
    [Fact]
    public async Task Each_published_class_costs_what_this_host_charges_for_it()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Published());

        var classes = host.Factory.Services.GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>().Value;

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var catalogue = scope.ServiceProvider.GetRequiredService<IPlanCatalogueFacade>().Published();
        var quota = scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>();

        Assert.NotEmpty(catalogue.PictureClasses);
        Assert.All(catalogue.PictureClasses, row =>
        {
            var quality = Enum.Parse<GeneratedImageQualityClass>(row.QualityClass);
            var sent = GeneratedImageQualityClassCatalogue.Settings(classes, quality)!;

            Assert.Equal(
                quota.ImageCredits(sent.ModelName, sent.Resolution, sent.Quality),
                row.PerPicture);
        });
    }

    /// <summary>
    /// <strong>Changing one rate row changes the published price, and nothing else is edited.</strong> This is
    /// what "one rate source" means in practice: there is no second figure to keep in step.
    /// </summary>
    [Fact]
    public async Task Changing_one_image_rate_row_changes_the_published_price()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Published());

        var classes = host.Factory.Services.GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>().Value;
        var standard = GeneratedImageQualityClassCatalogue.Settings(
            classes, GeneratedImageQualityClass.Standard)!;

        var before = PriceOf(host, nameof(GeneratedImageQualityClass.Standard));

        // The one row the Standard class sends to, raised by a credit. Nothing about Billing is touched, and the
        // key is built by the grammar that reads it rather than spelled here, so this cannot pass by agreeing
        // with a format of its own.
        var quota = host.Factory.Services.GetRequiredService<IOptions<AiQuotaOptions>>().Value;
        var key = AiImageRateKeys.Key(standard.ModelName, standard.Resolution, standard.Quality);

        Assert.True(quota.ImageRates.ContainsKey(key), $"This host has no rate row for '{key}'.");
        quota.ImageRates[key] += 1m;

        Assert.Equal(before + 1m, PriceOf(host, nameof(GeneratedImageQualityClass.Standard)));
    }

    /// <summary>
    /// A class no plan includes is not advertised: nothing on sale reaches it, so a price for it would be an
    /// offer the product cannot honour.
    /// </summary>
    [Fact]
    public async Task A_class_no_plan_includes_is_not_published()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Published(
            // Every plan stops at Standard, so the three above it are offered by the host and sold by nobody.
            plan => plan.QualityClasses = ["draft", "standard"]));

        var published = CatalogueOf(host).PictureClasses.Select(row => row.QualityClass);

        Assert.Equal(["Draft", "Standard"], published);
    }

    /// <summary>
    /// A plan publishes its classes in the enum's spelling, whatever the catalogue writes, so the page reads
    /// one vocabulary and needs no copy of the comparison (AP.4.5; the spelling AP.4.3 settled).
    /// </summary>
    [Fact]
    public async Task A_plans_classes_are_published_in_one_spelling()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Published(
            plan => plan.QualityClasses = ["DRAFT", "high-2k", "standard"]));

        var plan = CatalogueOf(host).Plans[0];

        // Ascending, as the enum declares them, and not the order the catalogue happened to list.
        Assert.Equal(["Draft", "Standard", "High2K"], plan.QualityClasses);
    }

    /// <summary>An unpublished catalogue states no picture prices, any more than it states a plan's.</summary>
    [Fact]
    public async Task An_unpublished_catalogue_publishes_no_picture_prices()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: services =>
            services.PostConfigure<BillingOptions>(billing => billing.PublishPlans = false));

        Assert.Empty(CatalogueOf(host).PictureClasses);
    }

    /// <summary>
    /// Every development plan's <c>ExampleCounts</c> — the recipes and pictures its card leads with — is priced at the
    /// published rates and still fits its credits (AP.4.5; owner, 2026-10-28). A recipe draft and a social post
    /// are each a <c>TextDraft</c>; a picture is the default class's rate, which is what a request naming no
    /// class is made at and what the example's pictures are counted in. All three together, because the card
    /// says "and": a mix that fits only one at a time would promise more than the plan holds.
    /// </summary>
    [Fact]
    public async Task Each_plans_example_fits_its_credits_at_the_published_rates()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: services =>
            services.PostConfigure<BillingOptions>(billing => billing.PublishPlans = true));

        var catalogue = CatalogueOf(host);
        var perPicture = catalogue.PictureClasses
            .Single(row => row.QualityClass == catalogue.DefaultPictureClass)
            .PerPicture;

        Assert.NotEmpty(catalogue.Plans);
        Assert.All(catalogue.Plans, plan =>
        {
            var example = Assert.IsType<PlanExampleServiceModel>(plan.ExampleCounts);
            var cost = ((example.RecipeDrafts + example.SocialPosts) * catalogue.Rates.TextDraft)
                + (example.Pictures * perPicture);

            Assert.True(
                cost <= plan.Credits,
                $"{plan.Key}'s example costs {cost} credits at the published rates but includes {plan.Credits}.");
        });
    }

    private static decimal PriceOf(SqliteApiHost host, string qualityClass) =>
        CatalogueOf(host).PictureClasses.Single(row => row.QualityClass == qualityClass).PerPicture;

    /// <summary>The published catalogue, read in a scope — the facade is scoped, like every other.</summary>
    private static PlanCatalogueServiceModel CatalogueOf(SqliteApiHost host)
    {
        using var scope = host.Factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IPlanCatalogueFacade>().Published();
    }

    /// <summary>
    /// The host's own development catalogue, published, with each plan optionally adjusted — so the figures
    /// under test are the shipped ones rather than a fixture's.
    /// </summary>
    private static Action<IServiceCollection> Published(Action<PlanOption>? adjust = null) =>
        services => services.PostConfigure<BillingOptions>(billing =>
        {
            billing.PublishPlans = true;

            foreach (var plan in billing.Plans)
            {
                adjust?.Invoke(plan);
            }
        });
}
