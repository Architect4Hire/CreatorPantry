using System.Globalization;
using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.AspNetCore.Hosting;

namespace CreatorPantry.Tests;

/// <summary>
/// Settings every in-process ApiService host needs: a connection string (never opened by these tests) and
/// the gateway's public key for internal-token validation.
/// </summary>
internal static class TestDatabase
{
    public const string ConnectionString =
        "Server=127.0.0.1,1;Database=creatorpantrydb-test;User Id=test;Password=test;TrustServerCertificate=true";

    public static void Configure(IWebHostBuilder web)
    {
        web.UseSetting($"ConnectionStrings:{CreatorPantryDbContext.ConnectionName}", ConnectionString);
        web.UseSetting("InternalToken:PublicKeyPem", TestKeyPair.Shared.PublicKeyPem);
        web.UseSetting("Idempotency:FingerprintKey", FingerprintKey);

        // Off for every host that is not about limits (15.3). A test host never drains a queue, so an in-progress
        // cap would refuse the eleventh AI request in any test that makes eleven; the tests that are about limits
        // switch them back on with the numbers they need (LimitsTestConfiguration).
        web.UseSetting("Limits:Enabled", "false");

        // A credit rate for the fake image model (AP.2.5). A picture with no rate is refused at admission and
        // never generated, so without this every generation test would be answered "this host cannot price
        // that" — which is the right answer to a real misconfiguration and the wrong one here. The figure is
        // arbitrary; the tests that are about what a picture costs set their own.
        web.UseSetting($"Ai:Quota:ImageRates:{FakeImageModel}", FakeImageRate.ToString(CultureInfo.InvariantCulture));
        web.UseSetting($"Ai:Cost:Images:{FakeImageModel}", "0.008");

        // The default quality class sends to the fake model (AP.3.1), so a request that names no class is priced
        // and sent exactly as it was before classes: the fake model at its own rate. Blank clears the shipped
        // quality and resolution, which the fake model takes none of. The other classes keep the shipped
        // mapping and its real rates, which is what the per-class tests price against.
        web.UseSetting($"Ai:Images:Classes:{DefaultQualityClass}:Model", FakeImageModel);
        web.UseSetting($"Ai:Images:Classes:{DefaultQualityClass}:Quality", string.Empty);
        web.UseSetting($"Ai:Images:Classes:{DefaultQualityClass}:Resolution", string.Empty);
    }

    /// <summary>The class a request that names none is made at, as the shipped configuration says.</summary>
    public const string DefaultQualityClass = "Standard";

    /// <summary>
    /// The model the fake image generator reports, which <see cref="Configure"/> rates so a hosted test can
    /// generate at all.
    /// </summary>
    /// <remarks>
    /// Stated here rather than read from <c>FakeImageGenerator</c> so this file depends on no fixture: several
    /// suites bring their own double, and all of them report this id.
    /// </remarks>
    public const string FakeImageModel = "fake-image-1";

    /// <summary>What one fake picture costs, in credits.</summary>
    public const decimal FakeImageRate = 10m;

    /// <summary>A fixed test-only HMAC key for idempotency fingerprints.</summary>
    public static readonly string FingerprintKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    /// <summary>Also disables the Aspire DbContext health check so /health tests exercise routing only.</summary>
    public static void ConfigureWithoutHealthCheck(IWebHostBuilder web)
    {
        Configure(web);
        web.UseSetting("Aspire:Microsoft:EntityFrameworkCore:SqlServer:DisableHealthChecks", "true");
    }

    /// <summary>
    /// The shape Foundry Local publishes for a model deployment: its own endpoint and key, plus the loaded
    /// model id. Nothing here is reachable — it exists so a client can be constructed, never called.
    /// </summary>
    public const string ModelDeploymentConnectionString =
        "Endpoint=http://127.0.0.1:61799/v1;Key=foundry-local-test-key;Model=phi-4-mini-instruct-generic-gpu:5";

    /// <summary>
    /// Supplies both model deployments, which a host outside Development requires.
    /// </summary>
    /// <remarks>
    /// Not part of <see cref="Configure"/>: a Development host is meant to start without deployments, and
    /// several tests assert exactly that. Any test that raises the environment needs this, though, or
    /// <c>AddCreatorPantryAi</c>'s own startup failure pre-empts whichever one it meant to observe.
    /// </remarks>
    public static void ConfigureModelDeployments(IWebHostBuilder web)
    {
        web.UseSetting($"ConnectionStrings:{AiModelConnections.Chat}", ModelDeploymentConnectionString);
        web.UseSetting($"ConnectionStrings:{AiModelConnections.Embeddings}", ModelDeploymentConnectionString);

        // And a rate and a price for the model that deployment runs. Outside Development an unrated chat
        // model refuses to start (AP.2.1), so without these the host fails on that before it reaches whatever
        // the test was about — which is how the account-message tests came to assert one refusal and get
        // another. The figures are arbitrary; only their presence is read here.
        web.UseSetting($"Ai:Quota:ModelRates:{DeployedChatModel}:CreditsPerMillionInputTokens", "1");
        web.UseSetting($"Ai:Quota:ModelRates:{DeployedChatModel}:CreditsPerMillionOutputTokens", "1");
        web.UseSetting($"Ai:Cost:Models:{DeployedChatModel}:InputPerMillionTokens", "0.001");
        web.UseSetting($"Ai:Cost:Models:{DeployedChatModel}:OutputPerMillionTokens", "0.001");
    }

    /// <summary>
    /// The model <see cref="ModelDeploymentConnectionString"/> reports, as a rate table keys it.
    /// </summary>
    /// <remarks>
    /// Without Foundry Local's <c>:N</c> catalogue version, which <c>AiModelRateKeys</c> strips when it looks
    /// a rate up — and which cannot appear in a configuration key, where <c>:</c> is the section delimiter.
    /// </remarks>
    public const string DeployedChatModel = "phi-4-mini-instruct-generic-gpu";
}
