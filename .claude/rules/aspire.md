# Aspire and Local Composition

`CreatorPantry.AppHost` is the single source of truth for the complete application model. Do not hide required infrastructure in manual setup scripts or developer-specific configuration.

## Required resources

- Gateway, Web host, API, Worker, and MigrationService projects.
- SQL Server 2025 with persistent local data.
- Redis.
- Blob/object storage or a compatible local emulator.
- Configured AI model and embedding endpoints; local substitutes may be used when supported.
- Angular development server through `AddJavaScriptApp` or the selected Aspire integration.

## Dependency order

- MigrationService waits for SQL and completes before API/Worker depend on the schema.
- API waits for SQL, Redis, storage, and required model resources.
- Gateway waits for API and Web.
- Worker waits for its queues/stores and the migrated database.
- Use `WaitFor`, health checks, and service references; do not use arbitrary sleeps.

## Configuration

- Use resource references and service discovery instead of literal localhost ports.
- Secrets are parameters marked secret and supplied through user-secrets or the deployment secret store.
- AppHost stays declarative; it contains no domain workflows.
- ServiceDefaults owns common OpenTelemetry, health, resilience, and service-discovery setup.
- Pin SQL Server to a version supporting the vector capabilities actually used.

## Verification

Run `aspire run`, inspect the dashboard, and verify health, logs, traces, dependencies, and clean startup from an empty data volume. A project that works only when services are started manually is incomplete.

## First run on a new machine

`sql-password`, `redis-password`, `idempotency-fingerprint-key`, and `internal-token-signing-key` need nothing
from you: each generates once (the signing key as a matched ECDSA P-256 pair; see `EcdsaP256PrivateKeyDefault`)
and persists to that machine's user secrets on first `aspire run`. You will not be prompted for any of them, and
if the dashboard ever does prompt for one, something upstream reset that machine's parameter store — it is not
the normal first-run experience.

The Foundry-Azure parameters (`foundry-endpoint`, `foundry-key`, `foundry-chat-deployment`,
`foundry-embeddings-deployment`) are different: they are real Azure values, so nothing can generate them for
you, and each developer supplies their own. They only exist as parameters — and so only get created at all —
when `Foundry:Azure` is `true`, and that flag itself **cannot** be set through the dashboard: it decides which
resources exist before the app model (and therefore the dashboard) is built, so there is no way to prompt for
it. It therefore defaults to `true` in the AppHost's `appsettings.json`, so a new machine is prompted without
any setup. The API and Worker wait on those values, so two non-interactive switches remain, each once per
machine and only for the developer who needs it:

```
# No model account: start without one. The API and Worker register model clients that throw.
dotnet user-secrets set "Foundry:Azure" "false" --project src/CreatorPantry.AppHost

# The resource is Azure AI Foundry rather than Azure OpenAI (the default) — changes the endpoint's URL shape:
dotnet user-secrets set "Foundry:AzureOpenAI" "false" --project src/CreatorPantry.AppHost
```

A machine that already supplies a deployment — `ConnectionStrings:chat` or `ConnectionStrings:embeddings`, or
`Foundry:LocalCli` — is not prompted; those take precedence over the default.

On first `aspire run` the dashboard prompts for endpoint, key, and both deployment names, and checking *Save to
user secret* on each writes it to that developer's own machine — no further setup, and the developer never has
to hand their key to anyone else or paste it into a shared script.

That prompt does have a reproduced failure mode worth knowing about: its login URL carries a single-use token
(`Login to the dashboard at https://localhost:.../login?t=...`), and a browser reload after that token is spent
can permanently drop a pending prompt — the server still has the request queued (a fresh dashboard connection
replays whatever is pending), but the browser never gets back into a state that shows it, so the affected
resource just sits in `Waiting`/`ValueMissing` with nothing further in the console or the UI. There is no
in-session recovery once that happens: stop `aspire run` and start it again rather than reloading the tab. If
it keeps happening, the fallback is setting the value directly, which reaches the same store:

```
dotnet user-secrets set "Parameters:foundry-endpoint" "<value>" --project src/CreatorPantry.AppHost
dotnet user-secrets set "Parameters:foundry-key" "<value>" --project src/CreatorPantry.AppHost
dotnet user-secrets set "Parameters:foundry-chat-deployment" "<value>" --project src/CreatorPantry.AppHost
dotnet user-secrets set "Parameters:foundry-embeddings-deployment" "<value>" --project src/CreatorPantry.AppHost
```

The dashboard prompt — for any parameter, generated or not — also requires an interactive session in the first
place: it does not appear under `aspire run --non-interactive`, in most CI runners, or in any launch context
without a real TTY, because the underlying `IInteractionService` is unavailable there. `ops-api-key` stays unset
by default (a clean clone has no ops credential) and is the one parameter meant to stay empty rather than be set.

