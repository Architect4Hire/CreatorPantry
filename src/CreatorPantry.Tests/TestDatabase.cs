using CreatorPantry.Domain.Data;
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
}
