using System.Runtime.CompilerServices;
using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The shipped quality classes (AP.3.1): all five offered, the API's and the Worker's the same, each one sized
/// by a shipped profile and priced by the shipped rate table — and Standard exactly what every picture was
/// before classes.
/// </summary>
public sealed class QualityClassTableTests
{
    private const string ApiSettings = "src/CreatorPantry.ApiService/appsettings.json";
    private const string WorkerSettings = "src/CreatorPantry.Worker/appsettings.json";
    private const string AppHostSettings = "src/CreatorPantry.AppHost/appsettings.json";

    /// <summary>The API prices a class and the Worker sends it; two tables would send one class two ways.</summary>
    [Fact]
    public void The_api_and_the_worker_carry_the_same_classes()
    {
        static List<KeyValuePair<string, string?>> Table(string file) =>
            [.. Load(file).GetSection(GeneratedImageQualityClassOptions.SectionName).AsEnumerable().OrderBy(pair => pair.Key)];

        Assert.Equal(Table(ApiSettings), Table(WorkerSettings));
    }

    [Fact]
    public void Every_class_is_offered_with_standard_the_default()
    {
        var classes = Classes(ApiSettings);

        Assert.Equal(Enum.GetValues<GeneratedImageQualityClass>(), classes.Classes.Keys.Order());
        Assert.Equal(GeneratedImageQualityClass.Standard, classes.DefaultClass);
    }

    /// <summary>
    /// Every class can be sent and is priced, by the shipped tables — the check startup makes, made here against
    /// the files a deployment actually starts from.
    /// </summary>
    [Fact]
    public void Every_shipped_class_is_sized_by_a_shipped_profile_and_priced_by_the_shipped_tables()
    {
        var settings = Load(ApiSettings);
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        settings.GetSection(VeniceModelProfile.SectionName).Bind(profiles);
        var rates = settings.GetSection(AiQuotaOptions.SectionName).Get<AiQuotaOptions>()!;
        var prices = new AiImageListPriceOptions();
        settings.GetSection(AiImageListPriceOptions.SectionName).Bind(prices);

        Assert.All(GeneratedImageQualityClassCatalogue.All(Classes(ApiSettings)), sent =>
        {
            Assert.True(profiles.TryGetValue(sent.ModelName, out var profile), $"{sent.QualityClass} has no profile");

            var options = new ImageGenerationOptions { AdditionalProperties = [] };

            if (sent.Quality is { } quality)
            {
                options.AdditionalProperties[ImageGenerationSettings.Quality] = quality;
            }

            if (sent.Resolution is { } resolution)
            {
                options.AdditionalProperties[ImageGenerationSettings.Resolution] = resolution;
            }

            Assert.Null(profile!.Settle(options).Refusal);
            Assert.NotNull(AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, sent.ModelName, sent.Resolution, sent.Quality, rates));
            Assert.NotNull(AiImageRateKeys.Figure(prices.Images, sent.ModelName, sent.Resolution, sent.Quality));
        });
    }

    /// <summary>
    /// Standard is what a request with no class got before AP.3.1 — the AppHost's model at its profile's own
    /// default — so a shipped client sees no change in what is made or what it costs.
    /// </summary>
    [Fact]
    public void Standard_is_the_default_model_at_its_own_defaults()
    {
        var standard = GeneratedImageQualityClassCatalogue.Settings(Classes(ApiSettings), GeneratedImageQualityClass.Standard)!;
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        Load(ApiSettings).GetSection(VeniceModelProfile.SectionName).Bind(profiles);
        var model = Load(AppHostSettings)["Venice:ImageModel"]!;

        Assert.Equal(model, standard.ModelName);
        Assert.Equal(profiles[model].DefaultQuality, standard.Quality);
        Assert.Equal(profiles[model].DefaultResolution, standard.Resolution);
    }

    /// <summary>
    /// The prices B-55 records, read from the table rather than restated anywhere: Draft 25 (qwen at 1K, until
    /// AP.3.2 lets a shorter-prompt model take it), Standard 50, High 87.5, High2K 187.5, High4K 287.5.
    /// </summary>
    [Fact]
    public void Each_class_costs_the_row_it_sends()
    {
        var rates = Load(ApiSettings).GetSection(AiQuotaOptions.SectionName).Get<AiQuotaOptions>()!;

        var prices = GeneratedImageQualityClassCatalogue.All(Classes(ApiSettings))
            .ToDictionary(
                sent => sent.QualityClass!.Value,
                sent => AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, sent.ModelName, sent.Resolution, sent.Quality, rates));

        Assert.Equal(25m, prices[GeneratedImageQualityClass.Draft]);
        Assert.Equal(50m, prices[GeneratedImageQualityClass.Standard]);
        Assert.Equal(87.5m, prices[GeneratedImageQualityClass.High]);
        Assert.Equal(187.5m, prices[GeneratedImageQualityClass.High2K]);
        Assert.Equal(287.5m, prices[GeneratedImageQualityClass.High4K]);
    }

    private static GeneratedImageQualityClassOptions Classes(string file)
    {
        var classes = new GeneratedImageQualityClassOptions();
        Load(file).GetSection(GeneratedImageQualityClassOptions.SectionName).Bind(classes);

        return classes;
    }

    private static IConfigurationRoot Load(string file) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(CallerPath()), file), optional: false)
            .Build();

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Ai/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
