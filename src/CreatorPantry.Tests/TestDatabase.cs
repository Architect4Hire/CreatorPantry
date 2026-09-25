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
    }

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
    }
}
