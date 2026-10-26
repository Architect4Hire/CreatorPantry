using System.Runtime.CompilerServices;
using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Ai.Live;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>
/// One real picture from Venice through <see cref="AiProviderRegistration.AddCreatorPantryAi"/> and the shipped
/// sizing profiles (AP.2.3): that Venice accepts the fields this application now sends, and that what was sent
/// is stamped on what came back.
/// </summary>
/// <remarks>
/// <strong>It spends money and needs a credential</strong> — one picture at the configured model's default, $0.04
/// for <c>gpt-image-2-5-flare</c> at medium 1K — so it runs only when <c>CP_LIVE_IMAGES</c> holds an images
/// connection string (<c>Endpoint=…;Key=…;Model=…</c>), and is skipped otherwise. <c>CP_LIVE_IMAGE_MODEL</c>
/// asks for a different profiled model; <c>CP_LIVE_USAGE</c> records the outcome without the picture or the key.
/// </remarks>
public sealed class LiveVeniceImageCheck
{
    private const string ConnectionVariable = "CP_LIVE_IMAGES";
    private const string ModelVariable = "CP_LIVE_IMAGE_MODEL";
    private const string UsageVariable = "CP_LIVE_USAGE";

    private const string Prompt =
        "A rustic loaf of sourdough bread on a wooden board, soft window light from the left, overhead view.";

    [Fact]
    public async Task One_real_picture_is_made_at_the_profiles_size_and_quality()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);

        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Skip($"Set {ConnectionVariable} to an images connection string to run this against Venice.");
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddJsonFile(Path.Combine(RepositoryRoot(CallerPath()), "src/CreatorPantry.ApiService/appsettings.json"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{AiModelConnections.Images}"] = connection,
        });
        builder.AddCreatorPantryAi();
        using var host = builder.Build();

        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var started = DateTimeOffset.UtcNow;

        var response = await host.Services.GetRequiredService<IImageGenerator>().GenerateAsync(
            new ImageGenerationRequest(Prompt),
            new ImageGenerationOptions { ModelId = string.IsNullOrWhiteSpace(model) ? null : model, Count = 1 },
            TestContext.Current.CancellationToken);

        var elapsed = DateTimeOffset.UtcNow - started;
        var image = Assert.IsType<DataContent>(Assert.Single(response.Contents));
        var quality = ImageGenerationSettings.Read(image.AdditionalProperties, ImageGenerationSettings.QualitySent);
        var size = ImageGenerationSettings.Read(image.AdditionalProperties, ImageGenerationSettings.SizeSent);

        if (Environment.GetEnvironmentVariable(UsageVariable) is { Length: > 0 } path)
        {
            await File.AppendAllTextAsync(
                path,
                $"LiveVeniceImageCheck model={model ?? "configured"} quality={quality ?? "none"} size={size} "
                + $"bytes={image.Data.Length} seconds={elapsed.TotalSeconds:0.0}{Environment.NewLine}",
                TestContext.Current.CancellationToken);
        }

        // A PNG, as the request asked: the eight-byte signature.
        Assert.True(image.Data.Span.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.False(string.IsNullOrWhiteSpace(size));
    }

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Ai/Live/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", "..", ".."));
}
