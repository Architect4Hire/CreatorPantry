# CreatorPantry Developer Onboarding

A jump start for a developer new to this repository: what the product is, how the system is put together, what you must configure before `aspire run` works, and the rules you will be held to in review.

This page summarizes. The sources of truth are the root [CLAUDE.md](../../CLAUDE.md), the rules in [.claude/rules/](../../.claude/rules/), and the decision record in [architecture-decisions/baseline.md](../architecture-decisions/baseline.md). When this page and those disagree, they win — and please fix this page.

## Contents

1. [Day-one checklist](#1-day-one-checklist)
2. [What CreatorPantry is](#2-what-creatorpantry-is)
3. [Prerequisites](#3-prerequisites)
4. [Secrets and configuration](#4-secrets-and-configuration)
5. [Running the system](#5-running-the-system)
6. [Your first sign-in](#6-your-first-sign-in)
7. [Technical architecture](#7-technical-architecture)
8. [Non-negotiable rules](#8-non-negotiable-rules)
9. [Everyday commands](#9-everyday-commands)
10. [How work is delivered](#10-how-work-is-delivered)
11. [Troubleshooting](#11-troubleshooting)
12. [Where to read next](#12-where-to-read-next)

---

## 1. Day-one checklist

1. Install the [prerequisites](#3-prerequisites) and start Docker.
2. Clone the repository and run `npm ci` in `src/web/`.
3. Decide how you will supply an AI model — see [4.2](#42-the-model-values-you-supply). If you have no model account, run this once and move on:

   ```bash
   dotnet user-secrets set "Foundry:Azure" "false" --project src/CreatorPantry.AppHost
   ```
4. Run `aspire run` from the repository root. If you kept the default, enter the four Foundry values in the dashboard and tick **Save to user secret**.
5. Open the `web-dev` endpoint from the dashboard, sign up, and confirm your account using the [development message sink](#6-your-first-sign-in).
6. Run the backend and frontend test suites ([section 9](#9-everyday-commands)) to confirm a clean baseline.

---

## 2. What CreatorPantry is

CreatorPantry is an **AI-assisted productivity workspace for food bloggers and content creators**. A creator takes a tested recipe and their own source material and turns it into a coordinated package: blog article, recipe card, social copy, newsletter copy, SEO metadata, and supporting media, then schedules and publishes it through external integrations.

It is **not** a consumer recipe directory, a social feed, or a food blog. The user is the creator producing the content, and the data model is built around that.

### The content graph

```text
Recipe and creator-owned source material
        ↓
Content project
        ├── Blog article
        ├── Recipe card
        ├── Social copy
        ├── Newsletter copy
        ├── SEO metadata
        └── Supporting media
        ↓
Publication and external delivery
```

The recipe is the canonical source. Everything below it is a derivative that points back to the recipe version it was made from, so stale derivatives can be detected when the recipe changes.

### Product pillars

| Pillar              | What it covers                                                                                                           |
| ------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| Recipe workspace    | Structured recipes, creator-entered wording, ordered steps, equipment, yields, immutable versions, deterministic scaling |
| Content studio      | Blog, email, social, recipe-card, and SEO drafts derived from canonical sources                                          |
| Media library       | Originals, derivatives, AI generations, crops, alt text, provenance                                                      |
| Brand intelligence  | Workspace-specific voice, audience, style guide, examples, constraints                                                   |
| Editorial planning  | Content projects, status, schedules, publication targets, audit history                                                  |
| AI assistance       | Grounded drafting, refinement, repurposing, semantic search — always as a proposal the creator accepts                   |
| Publishing adapters | Provider-neutral publication records; Buffer is the first planned adapter                                                |

### Business rules that shape the code

- **Creators own their content.** Text a creator wrote or pasted is never rewritten. Normalized reference data (a recognized ingredient, a unit) enriches a line; it does not replace the wording.
- **AI proposes, the creator decides.** Every AI output is a draft or proposal until explicitly accepted. It never silently replaces canonical content and never publishes on its own.
- **Workspaces are private.** A user can hold a different role in each workspace (`Viewer`, `Contributor`, `Editor`, `Owner`). One workspace's content never reaches another, including through AI grounding.
- **No food-safety promises.** Allergen, nutrition, preservation, and medical information is descriptive metadata with stated uncertainty, never a guarantee.
- **Publishing needs consent.** An explicit confirmation or a creator-approved automation policy.

### Out of scope unless explicitly requested

Consumer recipe indexing or marketplace features, nutrition or allergen guarantees, autonomous publishing, billing and metering, native mobile apps, provider-specific fields on core entities, and a public third-party developer platform.

### What is built today

Implemented: identity and the BFF session, workspace tenancy, the recipe module (library, editor, versions, test runs, readiness, exports), brand profile and style guides, AI recipe proposals (concepts, first draft, review, revision, substitution, adaptation), AI usage allowances, the ops usage route, the Prompt Library (list, preview, detail, copy, download, and reuse into Image Studio), and the DAM library (search, filter, sort, paging and thumbnail cards, plus an asset page with metadata, lineage, versions, usage history and downloads, where a Contributor can edit the details, add a version and log a use, and an Editor can remove the asset from the library), and the recipe editor's Media tab (link a library picture to a recipe as its lead, step, gallery, in-progress or social picture, following or keeping a version, and unlink it), and brand settings can link a primary logo and alternates from the library.

Placeholders in the app shell: Dashboard, Workflows, My Day, My Week, Social Studio, and Content Board. Publishing and media pipelines are designed in the rules but not yet built.

---

## 3. Prerequisites

| Tool                                 | Notes                                                                              |
| ------------------------------------ | ---------------------------------------------------------------------------------- |
| .NET 10 SDK                          | The solution targets `net10.0`                                                     |
| Aspire CLI                           | Provides `aspire run`; the AppHost uses Aspire SDK 13.5.4                          |
| Docker Desktop (or compatible)       | Runs SQL Server 2025, Redis, Azurite, and DbGate containers                        |
| Node.js and npm                      | For the Angular 22 workspace in `src/web/`                                         |
| Chrome                               | Karma runs the frontend tests in Chrome                                            |
| `dotnet-ef` tool                     | Only when you add migrations                                                       |
| Trusted ASP.NET Core dev certificate | `dotnet dev-certs https --trust`; the Angular dev server serves over HTTPS with it |
| Foundry Local 0.8.x                  | Optional; only for running a chat model on your own machine                        |

---

## 4. Secrets and configuration

Every secret is an Aspire parameter declared in [AppHost.cs](../../src/CreatorPantry.AppHost/AppHost.cs) and stored in the **AppHost's user-secrets store** on your machine. Nothing secret belongs in source control, logs, or API responses.

Set a value by hand with either command — they write to the same store:

```bash
dotnet user-secrets set "<name>" "<value>" --project src/CreatorPantry.AppHost
aspire secret set <name> <value>
```

### 4.1 Generated for you — do nothing

These generate on the first `aspire run` and persist to your user secrets. You are never prompted.

| Parameter                     | Used by                                         | Purpose                                                                                                                                               |
| ----------------------------- | ----------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `sql-password`                | SQL Server container                            | `sa` password for the local SQL Server 2025 instance                                                                                                  |
| `redis-password`              | Redis container                                 | Password for the cache and BFF session store                                                                                                          |
| `internal-token-signing-key`  | Gateway (private key), API (derived public key) | ECDSA P-256 key the gateway uses to sign the short-lived internal token the API trusts. The AppHost derives the public half, so the pair cannot drift |
| `idempotency-fingerprint-key` | API and Worker                                  | Keys the HMAC of idempotent request fingerprints. Both hosts must share the same value                                                                |

If the dashboard ever prompts for one of these, something reset your parameter store. That is not the normal first-run experience.

### 4.2 The model values you supply

These are real Azure values, so nothing can generate them. Each developer supplies their own.

| Parameter                       | Secret  | What to enter                                                                                                                         |
| ------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| `foundry-endpoint`              | No      | Azure OpenAI: `https://<resource>.openai.azure.com` with no path. Azure AI Foundry: `https://<resource>.services.ai.azure.com/models` |
| `foundry-key`                   | **Yes** | Key 1 or Key 2 from the resource's *Keys and Endpoint* page                                                                           |
| `foundry-chat-deployment`       | No      | The chat deployment's name as shown under *Deployments*, for example `gpt-4o-mini`. It must support tool calling                      |
| `foundry-embeddings-deployment` | No      | The embedding deployment's name, for example `text-embedding-3-small`                                                                 |

In Azure you need one resource with two deployments: a chat model and an embedding model.

**The default route is the dashboard.** On first `aspire run`, the dashboard shows *Unresolved parameters* with an **Enter values** form. Tick **Save to user secret** on each and you are not asked again.

**The fallback is the command line**, which reaches the same store:

```bash
dotnet user-secrets set "Parameters:foundry-endpoint" "<value>" --project src/CreatorPantry.AppHost
dotnet user-secrets set "Parameters:foundry-key" "<value>" --project src/CreatorPantry.AppHost
dotnet user-secrets set "Parameters:foundry-chat-deployment" "<value>" --project src/CreatorPantry.AppHost
dotnet user-secrets set "Parameters:foundry-embeddings-deployment" "<value>" --project src/CreatorPantry.AppHost
```

A wrong deployment name does not fail at startup. It surfaces as a 404 on the first generation, so check the names against the portal when you enter them.

**Images come from Venice.ai, not Azure.** The same form asks for one more value:

| Parameter        | Secret  | What to enter                                              |
| ---------------- | ------- | ---------------------------------------------------------- |
| `venice-api-key` | **Yes** | An API key from your venice.ai account, used for image generation |

```bash
dotnet user-secrets set "Parameters:venice-api-key" "<value>" --project src/CreatorPantry.AppHost
```

No Venice account? Set `Venice:Images` to `false` in user secrets. Everything except image generation works, and a generation settles as `provider-not-configured`. The model is `Venice:ImageModel` in the AppHost's `appsettings.json`.

### 4.3 Switches that choose the model route

These are configuration flags, not prompts. They decide which resources exist before the dashboard is built, so they can only be set in user secrets. Defaults live in the AppHost's [appsettings.json](../../src/CreatorPantry.AppHost/appsettings.json).

| Setting                                                                                 | Default | Effect                                                                                                                                                            |
| --------------------------------------------------------------------------------------- | ------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Foundry:Azure`                                                                         | `true`  | Prompt for the four values above. Set to `false` to start with no model; the API and Worker then register clients that throw when a generation is requested       |
| `Foundry:AzureOpenAI`                                                                   | `true`  | The endpoint is an Azure OpenAI resource. Set to `false` for an Azure AI Foundry resource, which uses a single `/models` endpoint                                 |
| `Foundry:LocalCli`                                                                      | unset   | Set to `true` to use a Foundry Local install on your machine. Skips the dashboard prompt                                                                          |
| `Foundry:Enabled`                                                                       | `false` | **Leave off.** The Aspire Foundry preview integration marks its own resource `FailedToStart` and takes the whole application down. Use `Foundry:LocalCli` instead |
| `ConnectionStrings:chat`, `ConnectionStrings:embeddings`                                | unset   | Supply a full connection string directly: `Endpoint=...;Key=...;DeploymentId=...`. Takes precedence over the dashboard prompt. Treat as a secret                  |
| `Foundry:Endpoint`, `Foundry:ChatModel:Deployment`, `Foundry:EmbeddingModel:Deployment` | unset   | Optional. Only pre-fill the dashboard form                                                                                                                        |

Pick one route:

| Your situation                        | What to do                                                                                                                                                                                                 |
| ------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| You have an Azure OpenAI resource     | Nothing. Run `aspire run` and fill in the dashboard form                                                                                                                                                   |
| You have an Azure AI Foundry resource | Set `Foundry:AzureOpenAI` to `false`, then fill in the form with the `/models` endpoint                                                                                                                    |
| You have no model account             | Set `Foundry:Azure` to `false`. Everything except AI generation works                                                                                                                                      |
| You want a local model                | Install Foundry Local 0.8.x, set `Foundry:LocalCli` to `true`, and run `foundry model download phi-4-mini`. Chat only; 0.8.x has no embedding models. Details are in the root [README.md](../../README.md) |

### 4.4 The ops key — leave it empty

| Parameter     | Default | Purpose                                                                                                                                                                |
| ------------- | ------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ops-api-key` | empty   | The platform operator's machine credential for `/api/v1/ops/*` routes. Format `cpops_<prefix>.<secret>`. Only the MigrationService sees it, and only to store its hash |

A clean clone has no ops credential and every ops route answers 401. That is intended. Set it only when you are working on an ops route:

```bash
dotnet user-secrets set "Parameters:ops-api-key" "cpops_<prefix>.<secret>" --project src/CreatorPantry.AppHost
```

Changing the value and restarting rotates the key; the previous one stops working immediately.

### 4.5 Configuration that is not a secret

- **AI tasks ship disabled.** A request for a task that is not enabled is refused with `TaskNotEnabled`. [appsettings.Development.json](../../src/CreatorPantry.ApiService/appsettings.Development.json) enables `diagnostic`, `recipe.concepts`, and `recipe.first-draft` locally. Add others under `Ai:Tasks:Enabled` when you need to exercise them.
- **AI cost estimates** need `Ai:Cost` configuration. Without it every estimate is null by design.
- **Publish mode** additionally wants `Azure:SubscriptionId`, `Azure:ResourceGroupPrefix`, and `Azure:Location`. Local runs need none of them.

### 4.6 Handling rules

- Never commit a secret, paste one into a shared script, or send your key to a teammate. Each developer uses their own.
- The `.claude/hooks/secret-guard.sh` hook blocks some accidental secret writes; do not rely on it alone.
- Secrets must never appear in logs or API responses.

---

## 5. Running the system

```bash
aspire run
```

Run it from the repository root or the AppHost directory. The AppHost is the single source of truth for the application model; a setup that works only when services are started by hand is considered broken.

### What starts

| Resource                              | Kind               | Role                                                                                                                                       |
| ------------------------------------- | ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------ |
| `sql` / `creatorpantrydb`             | Container          | SQL Server 2025 (`2025-CU9-ubuntu-22.04`), persistent volume                                                                               |
| DbGate                                | Container          | Browser SQL client wired to `sql`. Shows every workspace unfiltered — good for inspecting data, wrong for judging what a request can reach |
| `cache`                               | Container          | Redis: application cache, BFF session tickets, Data Protection keys                                                                        |
| `storage` / `blobs` / `brand-sources` | Container          | Azurite blob emulator and the brand source container                                                                                       |
| `migrations`                          | Project            | Applies EF migrations and seed data once, then exits                                                                                       |
| `api`                                 | Project            | ASP.NET Core Web API. No external endpoints                                                                                                |
| `worker`                              | Project            | Background jobs: generation, media, embedding, publishing                                                                                  |
| `web`                                 | Project            | Production host for the Angular bundle                                                                                                     |
| `gateway`                             | Project            | YARP BFF — the only browser-facing backend                                                                                                 |
| `web-dev`                             | npm                | Angular dev server over HTTPS. **Open this one while developing**                                                                          |
| `chat` / `embeddings`                 | Connection strings | The model deployments, when configured                                                                                                     |

Startup order is enforced with `WaitFor`: migrations complete before the API and Worker start, and the gateway waits for the API and the web host.

The containers and their volumes are persistent, so your data survives restarts.

---

## 6. Your first sign-in

There is no seeded user. Create one:

1. Open the `web-dev` endpoint from the dashboard and choose sign up.
2. Sign-in requires a confirmed email. Locally no email is sent; messages go to an in-memory sink on the API. Open the `api` resource's endpoint from the dashboard and browse to:

   ```text
   /_dev/account-messages
   ```

   It returns the most recent messages with their tokens. This route exists only in the Development environment and is not reachable through the gateway.
3. Confirm the account by visiting `/confirm-email?email=<email>&token=<token>` on the `web-dev` origin. URL-encode the token.
4. Sign in and create a workspace.

The sink is in memory. Restarting the API clears it.

---

## 7. Technical architecture

### Stack

| Area                      | Technology                                                                                           |
| ------------------------- | ---------------------------------------------------------------------------------------------------- |
| Runtime and orchestration | .NET 10, Aspire AppHost and ServiceDefaults                                                          |
| Backend                   | ASP.NET Core Web API, EF Core 10                                                                     |
| Database                  | SQL Server 2025, native `VECTOR` support for semantic search                                         |
| Cache                     | Redis                                                                                                |
| Frontend                  | Angular 22, standalone components, signals, strict TypeScript, `cp-` selector prefix                 |
| Edge                      | YARP backend-for-frontend                                                                            |
| Identity                  | ASP.NET Core Identity; authorization from workspace membership                                       |
| AI                        | `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`), Semantic Kernel, Microsoft Foundry |
| Media                     | Metadata in SQL, bytes in blob storage                                                               |
| Observability             | OpenTelemetry through ServiceDefaults                                                                |

### Solution layout

```text
src/
├── CreatorPantry.AppHost/             # the complete Aspire application model
├── CreatorPantry.ServiceDefaults/     # telemetry, health, resilience, discovery
├── CreatorPantry.Gateway/             # YARP BFF and browser session boundary
├── CreatorPantry.Web/                 # production host for the Angular bundle
├── CreatorPantry.ApiService/          # controllers, identity endpoints, policies, middleware
├── CreatorPantry.Domain/              # everything below the controller, organized by module
├── CreatorPantry.AiProvider/          # the only assembly allowed to name a model provider SDK
├── CreatorPantry.Worker/              # background jobs
├── CreatorPantry.MigrationService/    # applies migrations and seeds before dependents start
├── CreatorPantry.Tests/
└── web/                               # Angular 22 workspace
    ├── src/                           # product shell, features, showcase
    └── projects/creator-pantry-ui/    # the @creator-pantry/ui library
```

### Request path

```text
Browser → Gateway (YARP BFF) → API
                                └─ Controller → Facade → Business → DataLayer → Repository | Gateway
```

| Layer                | Owns                                               | Must not                                            |
| -------------------- | -------------------------------------------------- | --------------------------------------------------- |
| Controller           | HTTP shape, route context, one facade call         | Contain EF, cache, provider, or domain logic        |
| Facade               | Validation, policy checks, cache coordination      | Touch `DbContext` or repositories                   |
| Business             | Domain rules, calculations, resource authorization | Know about HTTP, EF tracking, or provider SDK types |
| DataLayer            | Composed persistence, transaction boundaries       | Make product decisions                              |
| Repository / Gateway | EF Core queries / external calls                   | Do anything else                                    |

Each layer talks only to the next one. ViewModels are HTTP input, ServiceModels are output, and EF entities never cross the HTTP boundary. All I/O is async and takes a `CancellationToken`.

### Domain modules

`CreatorPantry.Domain` is one assembly with one `DbContext` and one migration history, organized by bounded context rather than by layer.

```text
Modules/<Context>/
├── Facade/ Business/ Data/{Entities,Configurations}/
├── Managers/        # this module's ViewModels, ServiceModels, domain models, validators, policies
└── <Context>ServiceCollectionExtensions.cs

Managers/            # shared kernel: Persistence, Caching, Time, Results, Paging, Reference,
                     # Idempotency, Outbox, Audit
```

Current modules: `Ai`, `AiUsage`, `Auth`, `Brand`, `Content`, `Ingredients`, `Measurement`, `Recipes`, `Tenancy`, `Vocabulary`.

Modules talk **facade to facade**. Only three things cross a module boundary: the facade interface, the ServiceModels it returns, and entity types where a foreign key already crosses. `ModuleBoundaryTests` enforces this.

### Authentication

There is no OAuth or OIDC server.

1. The browser signs in through the gateway and receives only an `HttpOnly`, `Secure` session cookie. The session ticket lives in Redis.
2. On each request the gateway strips client-supplied credentials and attaches a gateway-signed internal token: ES256, lifetime at most two minutes.
3. The API accepts only that token. Product routes require `token_use=user`; `/api/v1/internal/**` accepts only the gateway's service token and is never proxied from browsers.
4. Every controller action requires authentication unless it opts out with `[AllowAnonymous]`.
5. State-changing requests require CSRF protection.

No token is ever stored in browser storage.

### Tenancy and data zones

| Zone                    | Has `WorkspaceId` | Examples                                                                                             |
| ----------------------- | ----------------- | ---------------------------------------------------------------------------------------------------- |
| Platform reference data | No                | Ingredients, aliases, units, conversions, cuisines, dietary tags, allergens, techniques              |
| Workspace-owned data    | Always            | Recipes, versions, content projects, media, brand voice, publications, AI generations, audit records |

Workspace routes are `/api/v1/workspaces/{workspaceSlug}/...`. The server resolves the slug, confirms active membership, and builds a request-scoped `IWorkspaceContext`. Every workspace entity has an EF global query filter. Unknown and inaccessible workspaces both return 404.

### AI

- Domain code depends on `IChatClient` and `IEmbeddingGenerator` only. Provider SDKs live in `CreatorPantry.AiProvider`.
- Chat and embeddings are separate deployments named `chat` and `embeddings`.
- Prompts are embedded `.prompt.md` files with a JSON front-matter manifest and a body checksum, validated at startup. Changing a prompt body means recomputing the checksum.
- Model output is validated against a C# document type in five stages. Nothing is repaired.
- Instructions go in the system role and data in the user role, in nonce-fenced segments stamped with their workspace.
- Scaling, unit conversion, arithmetic, authorization, and state transitions are deterministic code. A model may only produce a constrained command for domain code to execute.

### Frontend

- `src/web/src/app/` holds `core/`, `shared/`, `features/`, `models/`, `services/`, and `shell/`.
- `@creator-pantry/ui` in `src/web/projects/creator-pantry-ui` is the only reusable UI library. Import its public API; never deep-import `src/lib`.
- Components never inject `HttpClient`; typed API services own HTTP and call relative BFF routes.
- Use semantic `--cp-*` design tokens. Literal colors in feature code are defects.
- The design system showcase is served at `/design-system` in development.

---

## 8. Non-negotiable rules

A change that breaks one of these is stopped in review, not negotiated.

**Workspace isolation**

- Never accept `WorkspaceId` from a body, query string, header, AI tool argument, or job payload.
- `IgnoreQueryFilters()` is prohibited outside listed exemptions.
- Workspace cache keys begin with `workspace:{workspaceId}:`.
- Every workspace-scoped feature ships a **two-workspace** isolation test.

**Creator-owned content**

- Preserve entered ingredient and instruction text.
- Named or published recipe versions are immutable.
- AI changes arrive as a proposal or diff.
- Original media uploads are immutable.

**AI and food safety**

- The model never writes SQL and never receives credentials.
- Semantic Kernel plugins call facades only.
- Treat uploaded, imported, and retrieved text as untrusted prompt content.
- Never claim guaranteed allergen removal, medical suitability, or food safety.
- Do not log private prompt bodies or generated content.

**Integrations**

- Providers are adapters. Provider identifiers live in mapping records, never on `Recipe` or `ContentProject`.

**API contract**

- All routes are versioned under `/api/v1/`.
- Errors are `ProblemDetails` with a stable error code and correlation id.
- The OpenAPI snapshot at `src/CreatorPantry.Tests/Api/Snapshots/openapi-v1.json` is part of the contract. Regenerate it with `UPDATE_OPENAPI_SNAPSHOT=1` in the same change that alters a shape, and read the diff.

**Frontend**

- Every data surface implements loading, empty, degraded, error, and success states.
- WCAG 2.2 AA, keyboard operation, both themes, 40px touch targets.

---

## 9. Everyday commands

### Backend tests

```bash
dotnet run --project src/CreatorPantry.Tests
dotnet run --project src/CreatorPantry.Tests -- --filter-class "CreatorPantry.Tests.Recipes.*"
```

**Do not use `dotnet test`.** It reports "Zero tests ran" and exits 5 on this project, which looks like a pass for a suite that never ran. The cause is recorded in `CreatorPantry.Tests.csproj`.

### Frontend

```bash
cd src/web
npm ci
npm run build
npm test
```

### Migrations

```bash
dotnet ef migrations add <Name> --project src/CreatorPantry.Domain --startup-project src/CreatorPantry.ApiService
```

A migration reaches the running database only when `MigrationService` starts. **Restart `aspire run` after adding one**, or the API will query columns the database does not have.

### Building while `aspire run` is live

A running `aspire run` locks the API's build output, so tests and `dotnet ef` fail to build. Redirect the output:

```bash
# tests
dotnet run --project src/CreatorPantry.Tests -p:BaseOutputPath=<scratch>/

# dotnet ef takes no MSBuild properties, so use the environment (PowerShell)
$env:BaseOutputPath = "<scratch>\\"
```

Three `BulkOperationBoundaryTests` then fail on repository-root discovery. Those are not real failures.

---

## 10. How work is delivered

- **Keep work atomic:** one route, entity, integration, job, component, or domain seam per change.
- **Inspect before editing,** and plan any change that crosses more than one architectural layer.
- **Tests travel with behavior.** Run focused tests first, then the full suite.
- **Use the playbooks.** [.claude/skills/](../../.claude/skills/) has a step-by-step skill for each kind of seam: `add-endpoint`, `add-workspace-entity`, `add-recipe-feature`, `add-content-feature`, `add-media-feature`, `add-ai-capability`, `add-background-job`, `add-external-integration`, `add-notification`, `new-component`, and `creatorpantry-design-system`.
- **Use the reviewers.** [.claude/agents/](../../.claude/agents/) holds read-only reviewers to run after implementation: architecture, workspace isolation, API contract, AI safety, integrations, design, and test gaps.
- **Stop and ask** when a requirement would weaken workspace isolation, content ownership, food safety, or credential handling.

Precedence when instructions conflict: the explicit task, then root `CLAUDE.md`, then the most specific rule, then the matching skill, then nearby code.

---

## 11. Troubleshooting

| Symptom                                                                | Cause and fix                                                                                                                                                                                                                                       |
| ---------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `api` and `worker` sit in `Waiting`                                    | The Foundry parameters are unresolved. Enter them in the dashboard, or set `Foundry:Azure` to `false`                                                                                                                                               |
| A resource stays in `Waiting` or `ValueMissing` after a browser reload | The dashboard login token is single-use and a reload can drop the pending prompt for good. Stop `aspire run` and start it again; do not reload the tab. If it recurs, set the values from the command line ([4.2](#42-the-model-values-you-supply)) |
| No prompt appears at all                                               | The prompt needs an interactive session. It does not appear under `aspire run --non-interactive` or in CI. Set the values from the command line                                                                                                     |
| First AI generation returns 404                                        | A deployment name is wrong, or the endpoint shape does not match `Foundry:AzureOpenAI`                                                                                                                                                              |
| AI request refused with `TaskNotEnabled`                               | Add the task to `Ai:Tasks:Enabled`                                                                                                                                                                                                                  |
| Whole application fails after enabling `Foundry:Enabled`               | Known defect in the preview integration. Turn it off and use `Foundry:LocalCli`                                                                                                                                                                     |
| `foundry service` is not a command                                     | Foundry Local 0.10 renamed it. Pin to 0.8.x                                                                                                                                                                                                         |
| Build fails with a file-lock error                                     | `aspire run` is holding the output. See [Building while `aspire run` is live](#building-while-aspire-run-is-live)                                                                                                                                   |
| API errors about a missing column                                      | A migration was added without restarting `aspire run`                                                                                                                                                                                               |
| `dotnet test` says "Zero tests ran"                                    | Expected. Use `dotnet run --project src/CreatorPantry.Tests`                                                                                                                                                                                        |
| Unsafe requests fail CSRF validation in dev                            | The dev server and gateway must both be HTTPS. Trust the dev certificate and open `web-dev` from the dashboard                                                                                                                                      |
| Cannot sign in after signing up                                        | The email is not confirmed. See [section 6](#6-your-first-sign-in)                                                                                                                                                                                  |
| Ops route returns 401                                                  | `ops-api-key` is unset, which is the default                                                                                                                                                                                                        |

---

## 12. Where to read next

| Topic                                        | Document                                                                                                                |
| -------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| Project constitution                         | [CLAUDE.md](../../CLAUDE.md)                                                                                            |
| Decision record (B-01 to B-22)               | [architecture-decisions/baseline.md](../architecture-decisions/baseline.md)                                             |
| Architecture index                           | [architecture.md](../architecture.md)                                                                                   |
| What phases 0–2 built                        | [implemented-architecture-phase-0-2.md](../implemented-architecture-phase-0-2.md)                                       |
| Recipe editorial states                      | [architecture-decisions/recipe-editorial-state-machine.md](../architecture-decisions/recipe-editorial-state-machine.md) |
| AI evaluation baseline                       | [ai-evaluation-baseline.md](../ai-evaluation-baseline.md)                                                               |
| Path-specific rules                          | [.claude/rules/](../../.claude/rules/)                                                                                  |
| Toolkit map                                  | [.claude/README.md](../../.claude/README.md)                                                                            |
| Design system                                | [src/web/DESIGN-SYSTEM.md](../../src/web/DESIGN-SYSTEM.md)                                                              |
| Model setup in full, including Foundry Local | [README.md](../../README.md)                                                                                            |
