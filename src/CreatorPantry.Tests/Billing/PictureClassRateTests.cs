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
    /// Every development plan's <c>ExampleMix</c> is priced at the published rates and still fits its credits
    /// (AP.4.5). Pictures are counted at the Standard class, which is what "a picture" means on the menu and
    /// what a request naming no class is made at.
    /// </summary>
    [Fact]
    public async Task Each_plans_example_mix_fits_its_credits_at_the_published_rates()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: services =>
            services.PostConfigure<BillingOptions>(billing => billing.PublishPlans = true));

        var catalogue = CatalogueOf(host);
        var perPicture = catalogue.PictureClasses
            .Single(row => row.QualityClass == nameof(GeneratedImageQualityClass.Standard))
            .PerPicture;

        Assert.NotEmpty(catalogue.Plans);
        Assert.All(catalogue.Plans, plan =>
        {
            // "40 recipe drafts, 40 social posts and 10 pictures" — the counts the mix states, priced at the
            // menu's own figures. Written as a sum rather than parsed: the mix is prose, and a parser for it
            // would be a second thing to get wrong.
            var counts = ExampleMixCounts(plan.ExampleMix);
            var cost = (counts.Text * catalogue.Rates.TextDraft) + (counts.Pictures * perPicture);

            Assert.True(
                cost <= plan.Credits,
                $"{plan.Key}'s example mix costs {cost} credits at the published rates but includes {plan.Credits}.");
        });
    }

    /// <summary>
    /// The written actions and the pictures a mix names. Every shipped mix reads "N recipe drafts, N social
    /// posts and N pictures", so the figures are pulled out by the noun that follows them.
    /// </summary>
    private static (int Text, int Pictures) ExampleMixCounts(string mix)
    {
        var words = mix.Replace(",", string.Empty, StringComparison.Ordinal).Split(' ');
        var text = 0;
        var pictures = 0;

        for (var index = 0; index < words.Length - 1; index++)
        {
            if (!int.TryParse(words[index], System.Globalization.CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }

            if (words[index + 1].StartsWith("picture", StringComparison.OrdinalIgnoreCase))
            {
                pictures += count;
            }
            else
            {
                text += count;
            }
        }

        Assert.True(text > 0 && pictures > 0, $"Could not read counts out of '{mix}'.");

        return (text, pictures);
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
