var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", secret: true);
var redisPassword = builder.AddParameter("redis-password", secret: true);

// Gateway-to-API trust (baseline B-13): the gateway signs with the private key; the API holds the public key.
var internalTokenSigningKey = builder.AddParameter("internal-token-signing-key", secret: true);
var internalTokenPublicKey = builder.AddParameter("internal-token-public-key");

// Keys the HMAC of idempotent request fingerprints, so stored hashes cannot be tested against guessed payloads.
var idempotencyFingerprintKey = builder.AddParameter("idempotency-fingerprint-key", secret: true);

// SQL Server 2025 is required for the native VECTOR type; the tag is pinned deliberately.
var sql = builder.AddSqlServer("sql", sqlPassword)
    .WithImageTag("2025-CU9-ubuntu-22.04")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("creatorpantry-sql-data");
var db = sql.AddDatabase("creatorpantrydb");

var cache = builder.AddRedis("cache", password: redisPassword)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("creatorpantry-redis-data");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(azurite => azurite
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume("creatorpantry-storage-data"));
var blobs = storage.AddBlobs("blobs");

// One-shot: applies EF migrations, then exits. Dependents use WaitForCompletion(migrations).
var migrations = builder.AddProject<Projects.CreatorPantry_MigrationService>("migrations")
    .WithReference(db)
    .WaitFor(db);

var api = builder.AddProject<Projects.CreatorPantry_ApiService>("api", launchProfileName: "https")
    .WithEnvironment("InternalToken__PublicKeyPem", internalTokenPublicKey)
    .WithEnvironment("Idempotency__FingerprintKey", idempotencyFingerprintKey)
    .WithReference(db).WaitFor(db)
    .WithReference(cache).WaitFor(cache)
    .WithReference(blobs).WaitFor(blobs)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.CreatorPantry_Worker>("worker")
    .WithReference(db).WaitFor(db)
    .WithReference(blobs).WaitFor(blobs)
    .WaitForCompletion(migrations);

// Production host for the Angular bundle; serves /runtime-config.json with the gateway's public URL.
var web = builder.AddProject<Projects.CreatorPantry_Web>("web", launchProfileName: "https")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// The only browser-facing backend (baseline B-02). The API has no external endpoints.
var gateway = builder.AddProject<Projects.CreatorPantry_Gateway>("gateway", launchProfileName: "https")
    .WithEnvironment("InternalToken__SigningKeyPem", internalTokenSigningKey)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WithReference(cache).WaitFor(cache) // BFF session tickets and Data Protection keys
    .WithEnvironment("Cors__AllowedOrigins__0", web.GetEndpoint("https"))
    .WaitFor(api)
    .WaitFor(web);

web.WithEnvironment("Client__GatewayUrl", gateway.GetEndpoint("https"));

// Development only: the Angular dev server, which proxies /runtime-config.json to the web host.
if (builder.ExecutionContext.IsRunMode)
{
    var webDev = builder.AddJavaScriptApp("web-dev", "../web", "start")
        .WithNpm()
        .WithHttpEndpoint(env: "PORT")
        .WithReference(web)
        .WaitFor(web);

    gateway.WithEnvironment("Cors__AllowedOrigins__1", webDev.GetEndpoint("http"));
}

builder.Build().Run();
