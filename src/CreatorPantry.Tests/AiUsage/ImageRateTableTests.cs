using System.Runtime.CompilerServices;
using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Tests.Auth;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests.AiUsage;

// MEAI001: ImageGenerationOptions is experimental; the generator and its own tests suppress it the same way.
#pragma warning disable MEAI001

/// <summary>
/// The shipped image credit rates and Venice list prices (AP.2.4): that each rate is its price at the pinned
/// unit (<c>ai-pricing.md</c> B-49, credits = list $ × 1,250), that the table covers exactly what the sizing
/// profiles can request, that the API and the Worker carry the same one, and that a picture with no rate
/// charges nothing rather than nothing-as-zero.
/// </summary>
/// <remarks>
/// These read the real <c>appsettings.json</c> files rather than a copy, because the figures are the thing
/// under test: a typo in one of them is a picture refused at admission for no reason a creator can see.
/// </remarks>
public sealed class ImageRateTableTests
{
    /// <summary>What a list dollar is worth in credits: $0.001 of (list cost × 1.25) per credit.</summary>
    private const decimal CreditsPerListDollar = 1_250m;

    private const string ApiSettings = "src/CreatorPantry.ApiService/appsettings.json";
    private const string WorkerSettings = "src/CreatorPantry.Worker/appsettings.json";

    private const string RatesSection = "Ai:Quota:ImageRates";
    private const string PricesSection = "Ai:Cost:Images";

    /// <summary>
    /// Every rate is its list price × 1,250 and every price has a rate: one table derived from the other, so a
    /// price edited without its rate (or the reverse) fails here rather than drifting the margin.
    /// </summary>
    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void Each_image_rate_is_its_list_price_at_the_pinned_unit(string file)
    {
        var (rates, prices) = Bind(file);

        Assert.NotEmpty(rates.ImageRates);
        Assert.Equal(
            prices.Images.Keys.Order(StringComparer.OrdinalIgnoreCase),
            rates.ImageRates.Keys.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rate) in rates.ImageRates)
        {
            Assert.Equal(prices.Images[key] * CreditsPerListDollar, rate);
        }
    }

    /// <summary>
    /// The API holds a request at admission and the Worker settles it per variant; two tables would have them
    /// disagree about what the same picture cost.
    /// </summary>
    [Fact]
    public void The_api_and_the_worker_carry_the_same_image_rates_and_prices()
    {
        static Dictionary<string, string?> Table(string file) =>
            Load(file).AsEnumerable()
                .Where(pair => pair.Value is not null
                    && (pair.Key.StartsWith(RatesSection, StringComparison.OrdinalIgnoreCase)
                        || pair.Key.StartsWith(PricesSection, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var api = Table(ApiSettings);

        Assert.NotEmpty(api);
        Assert.Equal(api.OrderBy(pair => pair.Key), Table(WorkerSettings).OrderBy(pair => pair.Key));
    }

    /// <summary>
    /// Every row of the shipped table, charged through the converter the way admission will: the configured
    /// model priced by quality at each tier, the draft model priced by tier alone, and the two models that cost
    /// one figure whatever size is asked for.
    /// </summary>
    [Theory]
    [InlineData("gpt-image-2-5-flare", "1K", "low", 37.5)]
    [InlineData("gpt-image-2-5-flare", "1K", "medium", 50)]
    [InlineData("gpt-image-2-5-flare", "1K", "high", 87.5)]
    [InlineData("gpt-image-2-5-flare", "2K", "low", 50)]
    [InlineData("gpt-image-2-5-flare", "2K", "medium", 62.5)]
    [InlineData("gpt-image-2-5-flare", "2K", "high", 187.5)]
    [InlineData("gpt-image-2-5-flare", "4K", "low", 62.5)]
    [InlineData("gpt-image-2-5-flare", "4K", "medium", 87.5)]
    [InlineData("gpt-image-2-5-flare", "4K", "high", 287.5)]
    [InlineData("qwen-image-2-1-turbo", "1K", null, 25)]
    [InlineData("qwen-image-2-1-turbo", "2K", null, 25)]
    [InlineData("seedream-v5-lite", null, null, 62.5)]
    [InlineData("z-image-turbo", null, null, 12.5)]
    public void Each_requestable_picture_charges_its_rate(
        string model, string? resolution, string? quality, double expected)
    {
        var (rates, _) = Bind(ApiSettings);

        var charge = AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, model, resolution, quality, rates);

        Assert.Equal((decimal)expected, charge);
    }

    /// <summary>The same picture's provider cost, at list, for the ledger's cost column.</summary>
    [Theory]
    [InlineData("gpt-image-2-5-flare", "1K", "medium", 0.04)]
    [InlineData("gpt-image-2-5-flare", "4K", "high", 0.23)]
    [InlineData("qwen-image-2-1-turbo", "2K", null, 0.02)]
    [InlineData("z-image-turbo", null, null, 0.01)]
    public void Each_requestable_picture_is_priced_at_list(
        string model, string? resolution, string? quality, double expected)
    {
        var (_, prices) = Bind(ApiSettings);

        Assert.Equal((decimal)expected, new ConfiguredAiCostEstimator(prices).EstimateImage(model, resolution, quality));
    }

    /// <summary>
    /// The table holds exactly what the sizing profiles can request: nothing requestable is unrated, which is
    /// what startup refuses over, and no rate is unreachable, which is how a mistyped key shows itself.
    /// </summary>
    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void The_table_covers_exactly_what_the_sizing_profiles_can_request(string file)
    {
        var (rates, _) = Bind(file);
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        Load(file).GetSection(VeniceModelProfile.SectionName).Bind(profiles);

        var requestable = profiles
            .SelectMany(profile => profile.Value.RequestableSizes()
                .Select(size => AiImageRateKeys.Key(profile.Key, size.Resolution, size.Quality)))
            .ToList();

        Assert.NotEmpty(requestable);
        Assert.Equal(
            requestable.Order(StringComparer.OrdinalIgnoreCase),
            rates.ImageRates.Keys.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <strong>The rule the exhaustive table rests on.</strong> A quality is never charged at its model's
    /// general rate, nor a tier at the model's: a 4K high picture costs 287.5 credits and would settle at 50 if
    /// anything here fell back, which is the under-charge the whole table exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("gpt-image-2-5-flare", null, null)]
    [InlineData("gpt-image-2-5-flare", "4K", null)]
    [InlineData("qwen-image-2-1-turbo", null, null)]
    [InlineData("seedream-v5-lite", "1K", "high")]
    [InlineData("z-image-turbo", "1K", null)]
    public void Nothing_falls_back_from_a_quality_or_a_tier_to_a_coarser_rate(
        string model, string? resolution, string? quality)
    {
        var (rates, prices) = Bind(ApiSettings);

        Assert.Null(AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, model, resolution, quality, rates));
        Assert.Null(new ConfiguredAiCostEstimator(prices).EstimateImage(model, resolution, quality));
    }

    /// <summary>
    /// A picture with no rate is refused, not free: the converter answers null and admission refuses rather
    /// than reserving nothing. A quality with no tier has no key at all — no sizing kind produces one, and it
    /// would be indistinguishable from a tier in the grammar.
    /// </summary>
    [Theory]
    [InlineData("gpt-image-2-5-flare", "8K", "high")]
    [InlineData("gpt-image-2-5-flare", "1K", "ultra")]
    [InlineData("gpt-image-2-5-flare", null, "medium")]
    [InlineData("flux-unpriced", "1K", "medium")]
    [InlineData("", "1K", "medium")]
    public void A_picture_with_no_rate_charges_nothing_rather_than_zero(
        string model, string? resolution, string? quality)
    {
        var (rates, prices) = Bind(ApiSettings);

        Assert.Null(AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, model, resolution, quality, rates));
        Assert.Null(new ConfiguredAiCostEstimator(prices).EstimateImage(model, resolution, quality));
    }

    /// <summary>
    /// The model's name is matched the way settlement matches a reported one, so a decorated id still finds its
    /// model's row; the tier and the quality are not, since they are the deployment's own words.
    /// </summary>
    [Fact]
    public void A_decorated_image_model_id_finds_its_models_rate()
    {
        var (rates, _) = Bind(ApiSettings);

        Assert.Equal(
            50m,
            AiImageQuotaConverter.Charge(
                AiQuotaUnit.Credits, "gpt-image-2-5-flare-2026-10-26", "1K", "medium", rates));
        Assert.Equal(
            87.5m,
            AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, "GPT-IMAGE-2-5-FLARE", "1k", "HIGH", rates));
    }

    /// <summary>
    /// A token-denominated period reads the rate through the credit bridge, because a picture reports no tokens
    /// of its own. Declared rather than used, but a period opened in it still has to settle.
    /// </summary>
    [Fact]
    public void A_token_denominated_period_charges_the_rate_through_the_credit_bridge()
    {
        var (rates, _) = Bind(ApiSettings);

        Assert.Equal(
            50m * rates.TokensPerCredit,
            AiImageQuotaConverter.Charge(AiQuotaUnit.Tokens, "gpt-image-2-5-flare", "1K", "medium", rates));
    }

    /// <summary>
    /// Nothing rounds, so a caller can sum a request's variants and round the total once — the same division
    /// <see cref="AiQuotaUnitConverter"/>'s remarks set out. Three 37.5-credit drafts are 112.5, not 113.
    /// </summary>
    [Fact]
    public void A_rate_is_returned_unrounded_so_a_requests_variants_round_once()
    {
        var (rates, _) = Bind(ApiSettings);

        var variant = AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, "gpt-image-2-5-flare", "1K", "low", rates);

        Assert.Equal(37.5m, variant);
        Assert.Equal(112.5m, AiQuotaUnitConverter.Round(variant!.Value * 3));
    }

    /// <summary>
    /// The landing page's picture figures (LP.7) are each at least what the picture they name actually costs in
    /// credits, at the models <c>ai-pricing.md</c> B-51 names for the class: a draft on the draft model, and a
    /// standard and a high picture at 1K on the configured one. The class-to-model mapping is AP.3.1's to
    /// decide; this only holds the menu to never promising a picture below its cost.
    /// </summary>
    [Theory]
    [InlineData("ImageDraft", "qwen-image-2-1-turbo", "1K", null)]
    [InlineData("ImageStandard", "gpt-image-2-5-flare", "1K", "medium")]
    [InlineData("ImageHigh", "gpt-image-2-5-flare", "1K", "high")]
    public void Each_published_picture_rate_is_at_least_what_that_picture_costs(
        string menuRate, string model, string? resolution, string? quality)
    {
        var (rates, _) = Bind(ApiSettings);
        var menu = Load("src/CreatorPantry.ApiService/appsettings.Development.json")
            .GetValue<decimal>($"Billing:Rates:{menuRate}");

        var charge = AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, model, resolution, quality, rates);

        Assert.NotNull(charge);
        Assert.True(
            menu >= charge,
            $"The menu's {menuRate} of {menu} credits is under the {charge} a {model} picture costs.");
    }

    /// <summary>
    /// An unpriced picture is said once per key, so a deployment that can request one shows in the log without
    /// flooding it; a priced one says nothing. Startup already refuses a host that can reach this.
    /// </summary>
    [Fact]
    public void An_unpriced_picture_is_warned_about_once_and_a_priced_one_never()
    {
        var (_, prices) = Bind(ApiSettings);
        var captured = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(captured));
        var estimator = new ConfiguredAiCostEstimator(prices, factory.CreateLogger<ConfiguredAiCostEstimator>());

        estimator.EstimateImage("flux-unpriced", "1K", "high");
        estimator.EstimateImage("flux-unpriced", "1K", "high");
        estimator.EstimateImage("gpt-image-2-5-flare", "1K", "medium");

        var warning = Assert.Single(captured.Entries);
        Assert.Contains("flux-unpriced/1K/high", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every shipped figure is above nought. A row of nought would reserve nothing and charge nothing for a
    /// picture the provider bills for — the one thing the table exists to prevent, arrived at by a typo rather
    /// than by an omission.
    /// </summary>
    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void Every_shipped_figure_is_above_nought(string file)
    {
        var (rates, prices) = Bind(file);

        Assert.All(rates.ImageRates, row => Assert.True(row.Value > 0m, $"{row.Key} is rated {row.Value}."));
        Assert.All(prices.Images, row => Assert.True(row.Value > 0m, $"{row.Key} is priced {row.Value}."));
    }

    /// <summary>
    /// A rate of nought or below is read as no rate rather than as a free picture, so a configuration changed
    /// after the startup check ran still refuses rather than charging nothing.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-50")]
    public void A_rate_of_nought_or_below_charges_nothing_rather_than_nothing_at_all(string configured)
    {
        var rates = new AiQuotaOptions();
        var prices = new AiCostOptions();
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Quota:ImageRates:free-model"] = configured,
                ["Ai:Cost:Images:free-model"] = configured,
            })
            .Build();

        settings.GetSection(AiQuotaOptions.SectionName).Bind(rates);
        settings.GetSection(AiCostOptions.SectionName).Bind(prices);

        Assert.Null(AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, "free-model", null, null, rates));
        Assert.Null(new ConfiguredAiCostEstimator(prices).EstimateImage("free-model", null, null));
    }

    /// <summary>
    /// <strong>The one route left to a wrong rate once fallback is gone.</strong> A model named <c>m/1K</c>
    /// asked for at <c>high</c> would spell <c>m/1K/high</c> and read the 1K high row — a tier and quality
    /// nobody asked for. A part holding the separator has no key at all instead.
    /// </summary>
    [Theory]
    [InlineData("gpt-image-2-5-flare/1K", "high", null)]
    [InlineData("gpt-image-2-5-flare/1K/high", null, null)]
    [InlineData("gpt-image-2-5-flare", "1K/high", null)]
    [InlineData("gpt-image-2-5-flare", "1K", "high/extra")]
    public void A_name_holding_the_separator_cannot_reach_another_rows_rate(
        string model, string? resolution, string? quality)
    {
        var (rates, prices) = Bind(ApiSettings);

        Assert.Empty(AiImageRateKeys.Candidates(model, resolution, quality));
        Assert.Null(AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, model, resolution, quality, rates));
        Assert.Null(new ConfiguredAiCostEstimator(prices).EstimateImage(model, resolution, quality));
    }

    /// <summary>
    /// <strong>The invariant the whole validation rests on.</strong> The rate table is checked against
    /// <see cref="VeniceModelProfile.RequestableSizes"/>, but what a request may actually carry is decided by
    /// <see cref="VeniceModelProfile.Settle"/>. Every pair the one offers has to be accepted by the other and
    /// settle to the same tier and quality — otherwise a picture that can be requested passes startup unrated,
    /// or a row nothing can reach blocks a host from starting.
    /// </summary>
    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void Everything_a_profile_offers_settles_to_a_rated_picture(string file)
    {
        var (rates, _) = Bind(file);
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        Load(file).GetSection(VeniceModelProfile.SectionName).Bind(profiles);

        Assert.NotEmpty(profiles);
        Assert.All(profiles, entry =>
        {
            var (model, profile) = entry;

            Assert.All(profile.RequestableSizes(), size =>
            {
                var options = new ImageGenerationOptions { AdditionalProperties = [] };

                if (size.Resolution is not null)
                {
                    options.AdditionalProperties[ImageGenerationSettings.Resolution] = size.Resolution;
                }

                if (size.Quality is not null)
                {
                    options.AdditionalProperties[ImageGenerationSettings.Quality] = size.Quality;
                }

                var settled = profile.Settle(options);

                Assert.Null(settled.Refusal);
                Assert.Equal(size.Resolution, settled.Resolution);
                Assert.Equal(size.Quality, settled.Quality);

                // And what it settled on is rated, charged the way admission will have to charge it: from the
                // settled values, never from what the request happened to name.
                Assert.NotNull(AiImageQuotaConverter.Charge(
                    AiQuotaUnit.Credits, model, settled.Resolution, settled.Quality, rates));
            });
        });
    }

    /// <summary>
    /// A request that names nothing settles on the profile's own defaults, and those are rated too: it is the
    /// request every picture makes today, since nothing sets a quality until AP.3.1.
    /// </summary>
    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void A_picture_asked_for_with_no_size_settles_on_a_rated_default(string file)
    {
        var (rates, _) = Bind(file);
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        Load(file).GetSection(VeniceModelProfile.SectionName).Bind(profiles);

        Assert.All(profiles, entry =>
        {
            var settled = entry.Value.Settle(null);

            Assert.Null(settled.Refusal);
            Assert.NotNull(AiImageQuotaConverter.Charge(
                AiQuotaUnit.Credits, entry.Key, settled.Resolution, settled.Quality, rates));
        });
    }

    /// <summary>
    /// The key grammar: the parts a model's request carries and no others, separated by <c>/</c> because
    /// <c>:</c> is the configuration delimiter and would bind as nesting.
    /// </summary>
    [Theory]
    [InlineData("flare", "4K", "high", "flare/4K/high")]
    [InlineData("flare", "1K", null, "flare/1K")]
    [InlineData("flare", null, null, "flare")]
    [InlineData(" flare ", " 1K ", " high ", "flare/1K/high")]
    [InlineData("flare", "", "", "flare")]
    [InlineData("flare", null, "high", "")]
    [InlineData("", "1K", "high", "")]
    public void A_key_names_only_the_parts_the_request_carries(
        string? model, string? resolution, string? quality, string expected)
    {
        Assert.Equal(expected, AiImageRateKeys.Key(model, resolution, quality));
        Assert.DoesNotContain(':', AiImageRateKeys.Key(model, resolution, quality));
    }

    /// <summary>A rate written for one decorated id outranks the model's general row.</summary>
    [Fact]
    public void An_exact_decorated_key_wins_over_the_models_key()
    {
        var table = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["flare/1K/high"] = 1m,
            ["flare-2026-10-26/1K/high"] = 2m,
        };

        Assert.True(AiImageRateKeys.TryFind(table, "flare-2026-10-26", "1K", "high", out var value));
        Assert.Equal(2m, value);
    }

    /// <summary>
    /// <strong>The shipped tables through the real code path.</strong> Everything above reads the files
    /// directly; this builds a deployed host on each one and lets <c>RequireImageRates</c> and the AP.2.1 and
    /// AP.2.2 checks read them as a host does, with only the connection strings supplied. A row the startup
    /// binder cannot see — a key the configuration delimiter breaks, a figure that will not bind — fails here
    /// and nowhere else, and it covers the Worker, which no other startup test names.
    /// </summary>
    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void The_shipped_configuration_starts_a_deployed_host(string file)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
            Args = [],
        });

        builder.Configuration.AddJsonFile(Path.Combine(RepositoryRoot(CallerPath()), file), optional: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The chat model the shipped rates name, and the image model the AppHost configures by default.
            [$"ConnectionStrings:{AiModelConnections.Chat}"] =
                "Endpoint=https://example.openai.azure.com/openai/deployments/gpt-5.4-nano;"
                + "Key=chat-test-key;DeploymentId=gpt-5.4-nano",
            [$"ConnectionStrings:{AiModelConnections.Embeddings}"] = TestDatabase.ModelDeploymentConnectionString,
            [$"ConnectionStrings:{AiModelConnections.Images}"] =
                "Endpoint=https://venice.example/api/v1;Key=images-test-key;Model=gpt-image-2-5-flare",
        });

        builder.AddCreatorPantryAi();

        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<IImageGenerator>());
    }

    private static (AiQuotaOptions Rates, AiCostOptions Prices) Bind(string file)
    {
        var configuration = Load(file);
        var rates = new AiQuotaOptions();
        var prices = new AiCostOptions();

        configuration.GetSection(AiQuotaOptions.SectionName).Bind(rates);
        configuration.GetSection(AiCostOptions.SectionName).Bind(prices);

        return (rates, prices);
    }

    private static IConfigurationRoot Load(string file) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(CallerPath()), file), optional: false)
            .Build();

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/AiUsage/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
