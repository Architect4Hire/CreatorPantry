---
name: add-aspire-resource
description: >
  Add a new locally-orchestrated resource (database, cache, model deployment, message queue,
  container, or another project) to the CreatorPantry Aspire AppHost and wire it into the services that
  use it. Use for requests like "add a Redis cache", "add a second model deployment", "add a
  background worker", "run this container alongside the API". Keeps everything local and driven
  through service discovery.
---

# Add an Aspire resource

Work primarily in `src/CreatorPantry.AppHost/`. The goal: declare the resource, then reference it — never
hardcode connection details.

1. **Add the hosting package** if needed: `aspire add <resource>` (or add the
   `Aspire.Hosting.<Resource>` package). Confirm the exact package name at https://aspire.dev — some
   of these have been renamed recently (`Aspire.Hosting.Azure.AIFoundry` → `Aspire.Hosting.Foundry`).
2. **Declare the resource in the AppHost.** Keep resources as local containers or local runtimes.
3. **Wire it into consumers.** Add `.WithReference(x)` to the projects that use it, and `.WaitFor(x)`
   so they start after it's healthy. Use `.WaitForCompletion(x)` for a run-once resource like the
   migration service.
4. **Consume it in the service** via the matching Aspire client integration, keyed to the resource
   name — the connection is injected, not configured by hand.
5. **Verify.** Run `aspire run`, open the dashboard, and confirm the resource is healthy and the
   consuming service connected.

## The shapes this repo uses

**SQL Server** — the image tag is not optional, the 2022 default has no `VECTOR` type:
```csharp
var sqlPassword = builder.AddParameter("sql-password", secret: true);
var sql = builder.AddSqlServer("sql", sqlPassword)
                 .WithImageTag("2025-latest")
                 .WithLifetime(ContainerLifetime.Persistent)
                 .WithDataVolume();
var db = sql.AddDatabase("creatorpantrydb");
```
Consume with `builder.AddSqlServerDbContext<CreatorPantryDbContext>("creatorpantrydb")`
(`Aspire.Microsoft.EntityFrameworkCore.SqlServer`). If the context is already registered by hand,
use `EnrichSqlServerDbContext<T>` instead of registering it twice.

**Redis**:
```csharp
var cache = builder.AddRedis("cache");
```
Consume with `builder.AddRedisDistributedCache("cache")`.

**Microsoft Foundry** — the provider this repo chose (B-15). Both integrations are preview at 13.5.4:
```csharp
// Aspire.Hosting.Foundry. RunAsFoundryLocal() needs the Foundry CLI on the machine, which is why
// AppHost.cs gates the whole block on Foundry:Enabled.
var foundry = builder.AddFoundry("foundry").RunAsFoundryLocal();

// Model name/version/format come from configuration, so take the string overload rather than a
// FoundryModels.Local.* constant.
var chat       = foundry.AddDeployment("chat", "phi-4-mini", "5", "Microsoft");
var embeddings = foundry.AddDeployment("embeddings", "qwen3-embedding-0.6b", "1", "Microsoft");
```
Consume with `Aspire.Azure.AI.Inference` — two different client types, so neither needs a service key:
```csharp
builder.AddAzureChatCompletionsClient("chat").AddChatClient();            // non-keyed IChatClient
builder.AddAzureEmbeddingsClient("embeddings").AddEmbeddingGenerator();   // non-keyed IEmbeddingGenerator
```
Both read `ConnectionStrings:<name>`, take the deployment name from it, and register the
`Microsoft.Extensions.AI` trace sources and meters themselves — ServiceDefaults needs no change. No API key
belongs in configuration: Foundry Local publishes its own key in the connection string, and Azure mode
publishes none and uses the ambient Azure credential.

Chat and embedding deployments stay **separate named resources** so they can be sized, swapped and traced
independently. This wiring belongs in `CreatorPantry.AiProvider`, the only assembly allowed to name a
provider SDK; `CreatorPantry.Domain` sees `IChatClient` and `IEmbeddingGenerator` alone.

**Another project**:
```csharp
var worker = builder.AddProject<Projects.CreatorPantry_Worker>("worker")
                  .WithReference(db).WithReference(embeddings)
                  .WaitForCompletion(migrations);
```

**A secret the resource needs** is a parameter, never a literal:
```csharp
var secret = builder.AddParameter("publishing-provider-client-secret", secret: true);
```
Set it with user secrets on the AppHost project. If you are about to type a key into `AppHost.cs` or
`appsettings.json`, stop — the `secret-guard` hook will block the write anyway.

## Rules
- Prefer local containers or emulators for development. Production bindings may use managed
  resources while preserving the same logical resource names and configuration shape.
- No connection strings, keys, or ports in application code; the AppHost + service discovery own that.
  Pin a host port only when a documented external tool requires it.
- The AppHost stays declarative — no business logic, no HTTP calls, no data shaping.

## Checklist before done
- [ ] Resource declared in the AppHost as a local container/runtime
- [ ] Consumers wired with `WithReference` + `WaitFor` (or `WaitForCompletion` for run-once)
- [ ] Service consumes it via the Aspire client integration (name-keyed, injected)
- [ ] Any credential is an `AddParameter(..., secret: true)`, not a literal
- [ ] SQL Server, if touched, still pins a 2025 image tag
- [ ] Dashboard shows the resource healthy and connected
