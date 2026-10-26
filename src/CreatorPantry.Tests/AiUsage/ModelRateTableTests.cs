using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Tests.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The shipped credit rates and provider prices (AP.2.1): that each rate is its price at the pinned unit
/// (<c>ai-pricing.md</c> B-49, credits = list $ × 1,250), that the API and the Worker carry the same table, and
/// that a provider's reported model id finds its model's rate.
/// </summary>
/// <remarks>
/// These read the real <c>appsettings.json</c> files rather than a copy, because the values are the thing under
/// test: a typo in one of them is exactly what would have every call settle at the estimate.
/// </remarks>
public sealed class ModelRateTableTests
{
    /// <summary>What a list dollar is worth in credits: $0.001 of (list cost × 1.25) per credit.</summary>
    private const decimal CreditsPerListDollar = 1_250m;

    private const string ApiSettings = "src/CreatorPantry.ApiService/appsettings.json";
    private const string WorkerSettings = "src/CreatorPantry.Worker/appsettings.json";

    private const string RatesSection = "Ai:Quota:ModelRates";
    private const string PricesSection = "Ai:Cost:Models";

    /// <summary>The API's rate and price sections as flat settings, for a host that needs the shipped table.</summary>
    public static Dictionary<string, string?> ShippedRateSettings() =>
        Load(ApiSettings).AsEnumerable()
            .Where(pair => pair.Key.StartsWith(RatesSection + ":", StringComparison.OrdinalIgnoreCase)
                || pair.Key.StartsWith(PricesSection + ":", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every rate is its price × 1,250 and every price has a rate: one table derived from the other, so a price
    /// edited without its rate (or the reverse) fails here rather than drifting the margin.
    /// </summary>
    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void Each_rate_is_its_list_price_at_the_pinned_unit(string file)
    {
        var (rates, prices) = Bind(file);

        Assert.NotEmpty(rates.ModelRates);
        Assert.Equal(
            prices.Models.Keys.Order(StringComparer.OrdinalIgnoreCase),
            rates.ModelRates.Keys.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (model, rate) in rates.ModelRates)
        {
            var price = prices.Models[model];

            Assert.Equal(price.InputPerMillionTokens * CreditsPerListDollar, rate.CreditsPerMillionInputTokens);
            Assert.Equal(price.OutputPerMillionTokens * CreditsPerListDollar, rate.CreditsPerMillionOutputTokens);
        }
    }

    /// <summary>
    /// The API holds a run at admission and the Worker settles it; two tables would have them disagree about
    /// what the same call cost.
    /// </summary>
    [Fact]
    public void The_api_and_the_worker_carry_the_same_rates_and_prices()
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
    /// The chat model this deployment runs, and the others it may name, each under the id a response reports:
    /// the plain name, Azure's dated snapshot, and any case. One million tokens each way, so the figure is the
    /// rate itself.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.4-nano", 250 + 1562.5)]
    [InlineData("gpt-5.4-nano-2026-03-17", 250 + 1562.5)]
    [InlineData("gpt-4o-mini-2024-07-18", 187.5 + 750)]
    [InlineData("GPT-4O-MINI", 187.5 + 750)]
    [InlineData("gpt-4.1-mini-2025-04-14", 500 + 2000)]
    [InlineData("gpt-5-mini-2025-08-07", 312.5 + 2500)]
    [InlineData("text-embedding-3-small", 25)]
    public void A_reported_model_id_settles_at_its_models_rate(string reported, double expected)
    {
        var (rates, _) = Bind(ApiSettings);

        var charge = AiQuotaUnitConverter.Charge(AiQuotaUnit.Credits, reported, 1_000_000, 1_000_000, rates);

        Assert.Equal((decimal)expected, charge);
    }

    /// <summary>
    /// The landing page says a recipe draft or post costs "about" <c>Billing:Rates:TextDraft</c> credits. A
    /// typical draft — about 3,000 tokens in and 1,500 out, before any reasoning — on the chat model this
    /// deployment runs has to settle under that, or the page's figure is a promise the rates break.
    /// </summary>
    [Fact]
    public void A_typical_draft_on_the_chat_model_settles_under_the_menus_figure()
    {
        var (rates, _) = Bind(ApiSettings);
        var menu = Load("src/CreatorPantry.ApiService/appsettings.Development.json")
            .GetValue<decimal>("Billing:Rates:TextDraft");

        var draft = AiQuotaUnitConverter.Charge(AiQuotaUnit.Credits, "gpt-5.4-nano", 3_000, 1_500, rates);

        Assert.Equal(5m, menu);
        Assert.NotNull(draft);
        Assert.True(draft < menu, $"A typical draft settles at {draft} credits, over the menu's {menu}.");
    }

    /// <summary>
    /// Each plan card's "about N recipe drafts, N social posts and N pictures" is priced at the menu's own rates —
    /// a draft and a post each a <c>TextDraft</c>, a picture an <c>ImageStandard</c> — and adds up to no more than
    /// the plan's credits, so the card never promises more than the allowance buys.
    /// </summary>
    [Fact]
    public void Each_plans_example_mix_fits_its_credits_at_the_menus_rates()
    {
        var billing = Load("src/CreatorPantry.ApiService/appsettings.Development.json").GetSection("Billing");
        var text = billing.GetValue<int>("Rates:TextDraft");
        var picture = billing.GetValue<int>("Rates:ImageStandard");
        var plans = billing.GetSection("Plans").GetChildren().ToList();

        Assert.NotEmpty(plans);
        Assert.All(plans, plan =>
        {
            var mix = Regex.Match(
                plan["ExampleMix"] ?? string.Empty,
                @"^(\d+) recipe drafts, (\d+) social posts and (\d+) pictures$");
            Assert.True(mix.Success, $"{plan["Key"]}'s ExampleMix is not in the priced shape.");

            var cost = ((int.Parse(mix.Groups[1].Value) + int.Parse(mix.Groups[2].Value)) * text)
                + (int.Parse(mix.Groups[3].Value) * picture);

            Assert.True(cost <= plan.GetValue<int>("Credits"), $"{plan["Key"]}'s example mix costs {cost} credits.");
        });
    }

    /// <summary>
    /// A model that reports usage with no price is said once per id, so a deployment repointed at an unpriced
    /// model shows in the log without flooding it; a priced model says nothing.
    /// </summary>
    [Fact]
    public void An_unpriced_model_is_warned_about_once_and_a_priced_one_never()
    {
        var (_, prices) = Bind(ApiSettings);
        var captured = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(captured));
        var estimator = new ConfiguredAiCostEstimator(prices, factory.CreateLogger<ConfiguredAiCostEstimator>());

        estimator.Estimate("gpt-9-unpriced", 100, 100);
        estimator.Estimate("gpt-9-unpriced", 100, 100);
        estimator.Estimate("gpt-5.4-nano-2026-03-17", 100, 100);

        var warning = Assert.Single(captured.Entries);
        Assert.Contains("gpt-9-unpriced", warning, StringComparison.Ordinal);
    }

    /// <summary>The provider-cost estimate finds a dated id the same way the credit charge does.</summary>
    [Fact]
    public void A_dated_model_id_is_priced_at_its_models_list_price()
    {
        var (_, prices) = Bind(ApiSettings);
        var estimator = new ConfiguredAiCostEstimator(prices);

        // 1,000 tokens each way on gpt-4o-mini: $0.00015 in, $0.0006 out.
        Assert.Equal(0.00075m, estimator.Estimate("gpt-4o-mini-2024-07-18", 1_000, 1_000));
    }

    /// <summary>
    /// A model with no rate is unknown, not free: the converter answers null and settlement charges the
    /// estimate. Nothing is matched by prefix, so the larger <c>gpt-5.4</c> is never priced as its nano.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-9-unpriced")]
    [InlineData("phi-4-mini-instruct-generic-gpu:5")]
    [InlineData("")]
    public void A_model_with_no_rate_converts_to_nothing_rather_than_to_zero(string reported)
    {
        var (rates, prices) = Bind(ApiSettings);

        Assert.Null(AiQuotaUnitConverter.Charge(AiQuotaUnit.Credits, reported, 2_000, 500, rates));
        Assert.Null(new ConfiguredAiCostEstimator(prices).Estimate(reported, 2_000, 500));
    }

    [Theory]
    [InlineData("gpt-5.4-nano", new[] { "gpt-5.4-nano" })]
    [InlineData("gpt-4o-mini-2024-07-18", new[] { "gpt-4o-mini-2024-07-18", "gpt-4o-mini" })]
    [InlineData("phi-4-mini-instruct-generic-gpu:5", new[] { "phi-4-mini-instruct-generic-gpu:5", "phi-4-mini-instruct-generic-gpu" })]
    [InlineData("  gpt-5-mini  ", new[] { "gpt-5-mini" })]
    [InlineData("   ", new string[0])]
    public void Candidate_keys_strip_only_a_snapshot_date_and_a_catalogue_version(string reported, string[] expected)
    {
        Assert.Equal(expected, AiModelRateKeys.Candidates(reported));
    }

    /// <summary>A rate written for one snapshot outranks the model's general rate.</summary>
    [Fact]
    public void An_exact_snapshot_rate_wins_over_the_models_rate()
    {
        var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o-mini"] = 1,
            ["gpt-4o-mini-2024-07-18"] = 2,
        };

        Assert.True(AiModelRateKeys.TryFind(table, "gpt-4o-mini-2024-07-18", out var value));
        Assert.Equal(2, value);
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
