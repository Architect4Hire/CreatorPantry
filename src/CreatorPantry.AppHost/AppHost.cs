using System.Security.Cryptography;
using Aspire.Hosting.Foundry;
using CreatorPantry.AppHost;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Generated and persisted to this machine's user secrets on first run — not prompted for. The Aspire
// dashboard's own "unresolved parameter" prompt exists for this, but it has a reproduced failure mode: its
// login URL carries a single-use token, and a browser reload after that token is spent can permanently drop a
// pending prompt with no way back to it short of restarting with a clean parameter store. None of these three
// values need a human to choose them anyway, so they skip that prompt entirely.
var sqlPassword = builder.AddParameter("sql-password", new GenerateParameterDefault(), secret: true, persist: true);
var redisPassword = builder.AddParameter("redis-password", new GenerateParameterDefault(), secret: true, persist: true);

// Gateway-to-API trust (baseline B-13): the gateway signs with the private key; the API verifies with the
// public key derived from it below. Generated as a matched ECDSA P-256 pair and persisted to this machine's
// user secrets on first run — nobody types an EC key into a dashboard field, and generating both halves
// independently could produce a mismatched pair (see EcdsaP256PrivateKeyDefault).
var internalTokenSigningKey = builder.AddParameter(
    "internal-token-signing-key", new EcdsaP256PrivateKeyDefault(), secret: true, persist: true);

// Keys the HMAC of idempotent request fingerprints, so stored hashes cannot be tested against guessed payloads.
var idempotencyFingerprintKey = builder.AddParameter(
    "idempotency-fingerprint-key", new GenerateParameterDefault(), secret: true, persist: true);

// The platform operator's machine credential (baseline B-14): a hashed, rotatable key accepted only on
// /api/v1/ops/*. Only the migration service sees it, and only to store its hash — the API verifies against
// that hash and never holds the key itself. Optional, so a clean clone starts with no ops credential; supply
// it with `dotnet user-secrets set Parameters:ops-api-key "cpops_<prefix>.<secret>"` to enable the routes.
var opsApiKey = builder.AddParameter("ops-api-key", secret: true, value: string.Empty);

// SQL Server 2025 is required for the native VECTOR type; the tag is pinned deliberately.
var sql = builder.AddSqlServer("sql", sqlPassword)
    .WithImageTag("2025-CU9-ubuntu-22.04")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("creatorpantry-sql-data");
var db = sql.AddDatabase("creatorpantrydb");

// Development only: a browser SQL client the app model wires to `sql` itself, so reading the tables costs no
// port lookup and no pasted password — both of which change whenever that container is recreated. Run mode
// only, because it receives the sa credential and has no business in a published manifest. Persistent so a
// saved connection and open query tabs survive a restart, matching the server it points at.
//
// It queries with no workspace filter, so it shows every workspace at once: the right tool for inspecting
// schema and data, the wrong one for judging what a request can reach.
if (builder.ExecutionContext.IsRunMode)
{
    sql.WithDbGate(dbGate => dbGate.WithLifetime(ContainerLifetime.Persistent));
}

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

    // Provisioning the ops client is a seed step, so it belongs to the one host that seeds. Rotation is
    // changing this parameter and letting the migration service run again; the previous key stops working the
    // moment the new hash commits.
    .WithEnvironment("Ops__Clients__0__Name", "platform-operator")
    .WithEnvironment("Ops__Clients__0__Key", opsApiKey)
    .WithEnvironment("Ops__Clients__0__Scopes__0", "ai-usage.admin")
    .WaitFor(db);

var api = builder.AddProject<Projects.CreatorPantry_ApiService>("api", launchProfileName: "https")
    .WithEnvironment(async context =>
    {
        var privateKeyPem = await internalTokenSigningKey.Resource.GetValueAsync(context.CancellationToken)
            ?? throw new InvalidOperationException("internal-token-signing-key did not resolve to a value.");
        context.EnvironmentVariables["InternalToken__PublicKeyPem"] = DerivePublicKeyPem(privateKeyPem);
    })
    .WithEnvironment("Idempotency__FingerprintKey", idempotencyFingerprintKey)
    .WithReference(db).WaitFor(db)
    .WithReference(cache).WaitFor(cache)
    .WithReference(blobs).WaitFor(blobs)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

// The same fingerprint key the API gets, and for the same reason: the worker registers AddIdempotency, whose
// options are ValidateOnStart, so an absent key stops this host at startup rather than at first use. It must
// also be the *same* key, because a fingerprint hashed by one host is compared by the other.
var worker = builder.AddProject<Projects.CreatorPantry_Worker>("worker")
    .WithEnvironment("Idempotency__FingerprintKey", idempotencyFingerprintKey)
    .WithReference(db).WaitFor(db)
    .WithReference(cache).WaitFor(cache)
    .WithReference(blobs).WaitFor(blobs)
    .WaitForCompletion(migrations);

// Microsoft Foundry (B-15). Chat and embeddings are separate named deployments, so they can be sized,
// swapped and traced independently — the API and Worker consume them as "chat" and "embeddings", the names
// CreatorPantry.AiProvider's AiModelConnections declares.
//
// Opt-in, because RunAsFoundryLocal() drives the Foundry CLI on this machine: with Foundry:Enabled off, the
// API and Worker register the unconfigured clients instead and `aspire run` still starts on a clean clone
// with no Foundry install and no model account. Turning it on costs a one-time model download on first run,
// which is why the dependents WaitFor the deployments rather than racing them.
//
// Three states, not two. A developer with no Foundry install and no model account gets the unconfigured
// clients; one running Foundry Local gets deployments this AppHost owns; one pointing at deployments that
// already exist — an Azure Foundry resource, a shared team endpoint — supplies their connection strings and
// gets those. The third case exists because RunAsFoundryLocal() is a per-machine install, and requiring it
// of somebody who already has a model account would be asking them to download a model twice.
if (builder.Configuration.GetValue("Foundry:Enabled", false))
{
    var foundry = builder.AddFoundry("foundry").RunAsFoundryLocal();

    var chat = AddConfiguredDeployment(foundry, "chat", "Foundry:ChatModel");
    var embeddings = AddConfiguredDeployment(foundry, "embeddings", "Foundry:EmbeddingModel");

    api.WithReference(chat).WaitFor(chat)
        .WithReference(embeddings).WaitFor(embeddings);
    worker.WithReference(chat).WaitFor(chat)
        .WithReference(embeddings).WaitFor(embeddings);
}
else if (builder.Configuration.GetValue("Foundry:Azure", false))
{
    // These four are real Azure values — an endpoint, a key, two deployment names — so unlike the generated
    // secrets above, a human has to supply them; there is nothing to auto-generate. The Aspire dashboard offers
    // an "Enter values" form for unresolved parameters with a "Save to user secret" checkbox, and
    // WithCustomInput below labels that form for each of these. Treat it as a convenience, not the primary
    // path: it has a reproduced failure mode where a browser reload after its single-use login token is spent
    // permanently drops a pending prompt with no way back to it. The reliable path is setting the value
    // directly, which reaches the exact same store:
    //   dotnet user-secrets set "Parameters:foundry-endpoint" "<value>" --project src/CreatorPantry.AppHost
    //   dotnet user-secrets set "Parameters:foundry-key" "<value>" --project src/CreatorPantry.AppHost
    //   dotnet user-secrets set "Parameters:foundry-chat-deployment" "<value>" --project src/CreatorPantry.AppHost
    //   dotnet user-secrets set "Parameters:foundry-embeddings-deployment" "<value>" --project src/CreatorPantry.AppHost
    //
    // The endpoint is not marked secret because it is not one: masking it would only make the value harder to
    // check against the portal when a deployment name is wrong.
    // Two resource kinds, two URL shapes. An AI Foundry resource serves every deployment from one /models
    // endpoint and takes the deployment as a parameter; an Azure OpenAI resource puts the deployment in the
    // path, so each one is a different URL. Azure.AI.Inference appends "/chat/completions" to whatever it is
    // given either way, which is the only reason one client can talk to both.
    var azureOpenAi = builder.Configuration.GetValue("Foundry:AzureOpenAI", false);

    // Pre-filled from configuration when a developer has recorded which resource they use, so the dialog is a
    // confirmation rather than a transcription. Trailing slash trimmed here because the Azure portal shows the
    // endpoint with one and a path is appended below: the alternative is a double slash and a 404 that reads
    // like a wrong deployment name.
    var endpointDefault = builder.Configuration["Foundry:Endpoint"]?.Trim().TrimEnd('/');

    var endpoint = builder.AddParameter("foundry-endpoint")
        .WithDescription(
            azureOpenAi
                ? "The endpoint of your Azure OpenAI resource — `https://<resource>.openai.azure.com`, with "
                  + "no path. The `/openai/deployments/<deployment>` part is appended for you, so a trailing "
                  + "slash is removed if you paste one. Azure shows it under *Keys and Endpoint*."
                : "The **Models** endpoint of your Azure AI Foundry resource, including the `/models` path — "
                  + "`https://<resource>.services.ai.azure.com/models`. Azure shows it under *Keys and "
                  + "Endpoint*.",
            enableMarkdown: true)
        .WithCustomInput(_ => new InteractionInput
        {
            Name = "foundry-endpoint",
            InputType = InputType.Text,
            Label = azureOpenAi ? "Azure OpenAI endpoint" : "Foundry Models endpoint",
            Placeholder = azureOpenAi
                ? "https://<resource>.openai.azure.com"
                : "https://<resource>.services.ai.azure.com/models",
            Value = endpointDefault,
        });

    var key = builder.AddParameter("foundry-key", secret: true)
        .WithDescription(
            "**Key 1** or **Key 2** from the same *Keys and Endpoint* page. Tick *Save to user secret* so it "
            + "is not asked for again. It is never written to source control or returned by the API.",
            enableMarkdown: true);

    // Prompted too, rather than read from configuration. An Azure OpenAI deployment is named whatever it was
    // called at creation — `gpt-4o-mini` as often as `chat` — and a wrong name does not fail at startup: the
    // connection string is not exercised until the first generation, so it surfaces as a 404 on a creator's
    // request. Asking beside the endpoint, with the portal open, is where that mistake is cheapest to avoid.
    AddPromptedDeployment("chat", "Foundry:ChatModel:Deployment", "Chat deployment", "gpt-4o-mini");
    AddPromptedDeployment("embeddings", "Foundry:EmbeddingModel:Deployment", "Embedding deployment", "text-embedding-3-small");

    void AddPromptedDeployment(string connectionName, string section, string label, string placeholder)
    {
        var deployment = builder.AddParameter($"foundry-{connectionName}-deployment")
            .WithDescription(
                $"The **deployment name** for {connectionName} as it appears under *Deployments* in the Azure "
                + "portal. This is the name given at creation, which is often the model name rather than "
                + $"`{connectionName}`.",
                enableMarkdown: true)
            .WithCustomInput(_ => new InteractionInput
            {
                Name = $"foundry-{connectionName}-deployment",
                InputType = InputType.Text,
                Label = label,
                Placeholder = placeholder,
                Value = builder.Configuration[section]?.Trim(),
            });

        // Composed from parameters rather than from a literal, so the key is resolved by Aspire at the point
        // it is handed to a resource. It is not read into this process's own configuration on the way past.
        //
        // DeploymentId is sent in both shapes. Azure OpenAI ignores it in favour of the path, but the
        // connection-string parser requires it, and a client that reports which deployment answered is worth
        // more than a field saved.
        var resource = builder.AddConnectionString(
            connectionName,
            azureOpenAi
                ? ReferenceExpression.Create(
                    $"Endpoint={endpoint}/openai/deployments/{deployment};Key={key};DeploymentId={deployment}")
                : ReferenceExpression.Create(
                    $"Endpoint={endpoint};Key={key};DeploymentId={deployment}"));

        api.WithReference(resource);
        worker.WithReference(resource);
    }
}
else
{
    // The deployment names stay "chat" and "embeddings" whichever way they are supplied, because that name
    // is the configuration key CreatorPantry.AiProvider's AiModelConnections reads them back under. Each is
    // decided on its own: chat without embeddings is a real intermediate state, and AddCreatorPantryAi
    // already falls back per deployment rather than all-or-nothing.
    //
    // No secret is written here. AddConnectionString names a value the AppHost's own configuration supplies
    // under ConnectionStrings — user-secrets locally, the deployment's secret store otherwise — so a key in
    // an Azure Foundry connection string never reaches source control.
    AddExternalDeployment("chat", "Foundry:ChatModel");
    AddExternalDeployment("embeddings", "Foundry:EmbeddingModel");
}

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
    // isProxied: false — the browser talks to vite directly instead of through the DCP proxy.
    // Vite's dev server needs a WebSocket for HMR/live-reload, and the proxy does not forward
    // that upgrade correctly, which otherwise stalls the whole page (blank tab, no console error)
    // since main.js never gets a turn to execute. Going direct also keeps this endpoint's URL —
    // and therefore the CORS origin below, which is derived from it — pointed at whatever the
    // browser is actually using.
    //
    // HTTPS, matching the gateway's scheme: the gateway's antiforgery and session cookies are
    // SameSite=Strict, and browsers apply schemeful same-site — an http dev server talking to the
    // https gateway counts as cross-site, so those cookies would be set but never sent back, and
    // every unsafe request would fail CSRF validation. scripts/serve.mjs exports the trusted
    // ASP.NET Core dev cert for `ng serve` to use so the two origins agree on scheme.
    var webDev = builder.AddJavaScriptApp("web-dev", "../web", "start")
        .WithNpm()
        .WithHttpsEndpoint(env: "PORT", isProxied: false)
        .WithReference(web)
        .WaitFor(web);

    gateway.WithEnvironment("Cors__AllowedOrigins__1", webDev.GetEndpoint("https"));
}

builder.Build().Run();

// The API only ever needs the public half; deriving it here from the resolved private key — instead of
// resolving a second, independently-stored parameter — is what keeps the pair from drifting apart.
static string DerivePublicKeyPem(string privateKeyPem)
{
    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(privateKeyPem);
    return ecdsa.ExportSubjectPublicKeyInfoPem();
}

// Which model a deployment runs is configuration, not a literal here: a Foundry Local model id in
// development, an Azure deployment name when deployed, and neither belongs in the application model's
// source. This takes AddDeployment's string overload rather than a FoundryModels.Local.* constant for the
// same reason.
IResourceBuilder<FoundryDeploymentResource> AddConfiguredDeployment(
    IResourceBuilder<FoundryResource> foundry,
    string name,
    string configurationSection)
{
    var model = builder.Configuration.GetSection(configurationSection);

    return foundry.AddDeployment(
        name,
        Required("Name"),
        Required("Version"),
        Required("Format"));

    string Required(string key) => model[key]
        ?? throw new InvalidOperationException(
            $"'{configurationSection}:{key}' is required when Foundry:Enabled is true.");
}

// Passes a model deployment that already exists through to the API and Worker under the name they read it
// back under. Two sources, in this order: a connection string the configuration supplies, which is an Azure
// Foundry resource or any compatible endpoint; or, when Foundry:LocalCli is on, a local Foundry Local install
// driven through its own CLI. Neither present, nothing is registered and both hosts fall back to their
// unconfigured clients — the clean-clone case, so this is silent by design rather than a configuration error.
void AddExternalDeployment(string connectionName, string modelSection)
{
    var configured = builder.Configuration.GetConnectionString(connectionName);

    if (string.IsNullOrWhiteSpace(configured) && builder.Configuration.GetValue("Foundry:LocalCli", false))
    {
        var alias = builder.Configuration[$"{modelSection}:Name"];

        if (!string.IsNullOrWhiteSpace(alias))
        {
            configured = FoundryLocalCli.TryResolveConnectionString(alias, out var diagnostic);

            // To the console rather than a logger: this runs while the application model is being built,
            // before there is a resource to attribute it to, and a developer who expected a model and got the
            // throwing client needs to be told which of the two it was.
            Console.WriteLine(configured is null
                ? $"Foundry Local: '{connectionName}' unavailable — {diagnostic}."
                : $"Foundry Local: '{connectionName}' resolved — {diagnostic}.");
        }
    }

    if (string.IsNullOrWhiteSpace(configured))
    {
        return;
    }

    // Held as a literal rather than re-read from configuration: when it came from the CLI above there is no
    // configuration entry to read it back from.
    var resolved = configured;
    var deployment = builder.AddConnectionString(connectionName, ReferenceExpression.Create($"{resolved}"));

    api.WithReference(deployment);
    worker.WithReference(deployment);
}
