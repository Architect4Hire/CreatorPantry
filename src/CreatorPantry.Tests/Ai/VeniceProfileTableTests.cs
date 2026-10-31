using System.Runtime.CompilerServices;
using CreatorPantry.AiProvider;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The shipped Venice sizing profiles (AP.2.3): each one usable, the API's and the Worker's the same, and the
/// model the AppHost configures by default among them — so a clean clone starts and sizes its pictures.
/// </summary>
public sealed class VeniceProfileTableTests
{
    private const string ApiSettings = "src/CreatorPantry.ApiService/appsettings.json";
    private const string WorkerSettings = "src/CreatorPantry.Worker/appsettings.json";
    private const string AppHostSettings = "src/CreatorPantry.AppHost/appsettings.json";

    [Theory]
    [InlineData(ApiSettings)]
    [InlineData(WorkerSettings)]
    public void Every_shipped_profile_is_usable(string file)
    {
        var profiles = Profiles(file);

        Assert.NotEmpty(profiles);
        Assert.All(profiles, profile => Assert.Empty(profile.Value.Problems()));
    }

    /// <summary>Both hosts generate pictures; two tables would size the same request two ways.</summary>
    [Fact]
    public void The_api_and_the_worker_carry_the_same_profiles()
    {
        static List<KeyValuePair<string, string?>> Table(string file) =>
            [.. Load(file).GetSection(VeniceModelProfile.SectionName).AsEnumerable().OrderBy(pair => pair.Key)];

        Assert.Equal(Table(ApiSettings), Table(WorkerSettings));
    }

    /// <summary>The model a new machine is configured with has a profile, or that machine would not start.</summary>
    [Fact]
    public void The_default_image_model_has_a_profile_at_medium_1k()
    {
        var model = Load(AppHostSettings)["Venice:ImageModel"];

        Assert.False(string.IsNullOrWhiteSpace(model));

        var profile = Profiles(ApiSettings)[model!];
        Assert.Equal(VeniceSizing.Resolution, profile.Sizing);
        Assert.Equal("medium", profile.DefaultQuality);
        Assert.Equal("1K", profile.DefaultResolution);
    }

    /// <summary>One profile for each way a Venice model is sized, so every request dialect is configured.</summary>
    [Fact]
    public void Every_sizing_kind_has_a_shipped_profile()
    {
        Assert.Equal(
            Enum.GetValues<VeniceSizing>().Order(),
            Profiles(ApiSettings).Values.Select(profile => profile.Sizing!.Value).Distinct().Order());
    }

    private static Dictionary<string, VeniceModelProfile> Profiles(string file)
    {
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        Load(file).GetSection(VeniceModelProfile.SectionName).Bind(profiles);

        return profiles;
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
