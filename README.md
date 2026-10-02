<div>
<img src="docs/images/logo.png"/>
</div>

CreatorPantry is an AI-assisted productivity platform for food bloggers and content creators. It helps a creator move from a tested recipe and source material to a coordinated package of blog copy, recipe cards, social posts, newsletters, SEO metadata, imagery, and scheduled publications.

It is intentionally **not another recipe index**. The product is a private creator workspace: a place to develop, organize, refine, reuse, and publish food content without losing ownership of the canonical recipe or brand voice.

## Product pillars

- **Recipe workspace:** structured recipes, creator-entered language, ordered steps, equipment, notes, yields, versions, and deterministic scaling.
- **Content studio:** canonical sources transformed into blog, email, social, recipe-card, and SEO drafts.
- **Media library:** originals, derivatives, AI generations, crops, alt text, provenance, and reusable visual briefs.
- **Brand intelligence:** workspace-specific voice, audience, style, examples, and constraints.
- **Editorial planning:** content projects, status, assignments, schedules, publication targets, and audit history.
- **AI assistance:** grounded drafting, refinement, repurposing, semantic search, and structured actions with creator approval.
- **Publishing adapters:** provider-neutral publication records and gateways for external platforms.

## Architecture

The backend uses the same seam for every API feature:

```text
Controller → Facade → Business → DataLayer → Repository | Gateway
```

Controllers live in `CreatorPantry.ApiService`; the remaining layers live in `CreatorPantry.Domain`. Controllers accept ViewModels and return ServiceModels. EF entities do not cross the HTTP boundary. Semantic Kernel plugins call facades, so AI requests inherit the same validation, authorization, workspace isolation, and caching as normal requests.

CreatorPantry is multi-workspace. A single user may own a personal blog workspace, contribute to a client workspace, and edit an agency workspace. ASP.NET Core Identity establishes identity; workspace membership establishes authorization. Roles begin with `Viewer`, `Contributor`, `Editor`, and `Owner`. `PlatformAdmin` is the only platform-wide role.

## Technology baseline

- .NET 10, Aspire AppHost, and ServiceDefaults
- ASP.NET Core Web API and EF Core 10
- SQL Server 2025 with native vector support
- Redis
- Angular 22 with standalone components and strict TypeScript
- YARP backend-for-frontend
- ASP.NET Core Identity
- Microsoft.Extensions.AI and Semantic Kernel
- Azure AI Foundry or a configured compatible model provider
- Blob/object storage for media
- OpenTelemetry for logs, metrics, and traces

Versions are a starting baseline. Confirm current compatible package versions before the first implementation pass.

## Intended repository layout

```text
.
├── CLAUDE.md                         # auto-loaded project constitution
├── README.md
├── .claude/
│   ├── README.md                     # toolkit map and precedence
│   ├── settings.json                 # conservative command and hook policy
│   ├── rules/                        # architecture and domain rules
│   ├── skills/                       # deep implementation playbooks
│   ├── agents/                       # read-only review specialists
│   └── hooks/                        # secret and formatting guards
├── docs/
│   ├── architecture.md
│   └── scrub-prompts.md
└── src/
    ├── CreatorPantry.AppHost/
    ├── CreatorPantry.ServiceDefaults/
    ├── CreatorPantry.Gateway/
    ├── CreatorPantry.Web/
    ├── CreatorPantry.ApiService/
    ├── CreatorPantry.Domain/
    ├── CreatorPantry.AiProvider/       # the only assembly that names a model provider SDK
    ├── CreatorPantry.Worker/
    ├── CreatorPantry.MigrationService/
    ├── CreatorPantry.Tests/
    └── web/                           # installed Angular 22 design-system workspace
        ├── DESIGN-SYSTEM.md
        ├── src/                       # showcase/product shell
        └── projects/creator-pantry-ui/# @creator-pantry/ui library
```

## Data ownership

Platform reference data and creator-owned content are deliberately separated.

| Platform reference data                   | Workspace-owned content                    |
| ----------------------------------------- | ------------------------------------------ |
| Ingredient, alias, unit, conversion       | Recipe, version, ingredient line, step     |
| Cuisine, technique, equipment type        | Content project, article, derivative copy  |
| Dietary and allergen vocabulary           | Media asset, visual brief, generated asset |
| Food categories and vetted references     | Brand voice, audience, SEO brief           |
| Shared templates approved by the platform | Workspace template, publication, audit log |

Normalized reference data may enrich creator content, but it never destructively replaces the creator's wording.

## AI principles

- AI outputs are drafts or structured proposals, not silent mutations.
- Deterministic code performs scaling, conversions, arithmetic, and time calculations.
- Model-generated database queries are prohibited.
- Prompts are versioned files, not large string literals hidden in application code.
- Every generation records provenance and operational metadata.
- Private workspace material never crosses workspace boundaries.
- Food safety, allergens, health, and nutrition require explicit uncertainty and appropriate references.

## Getting started

1. Extract the repository drop-in archive into the root of the CreatorPantry clone and allow directories to merge.

2. Review every item marked `DECIDE` or `TBD`; they are intentional product decisions, not missing implementation.

3. Read `src/web/README.md` and `src/web/DESIGN-SYSTEM.md`. The supplied Angular 22 workspace and `@creator-pantry/ui` package are already installed under `src/web/`.

4. Install and verify the frontend:
   
   ```bash
   cd src/web
   npm ci
   npm run build
   npm test
   cd ../..
   ```

5. Make the hooks executable:
   
   ```bash
   chmod +x .claude/hooks/*.sh
   git update-index --chmod=+x .claude/hooks/*.sh
   ```

6. Scaffold the .NET solution and resources through the AppHost.

7. Build feature seams with the matching `.claude/skills/` playbook and atomic SCRUB prompts.

8. Run `aspire run` for the complete local application, and `dotnet run --project src/CreatorPantry.Tests` plus the frontend commands above for verification. Note that `dotnet test` does **not** work on this solution — it reports "Zero tests ran" for an upstream reason recorded in `CreatorPantry.Tests.csproj`.

9. Supply a model. By default (`Foundry:Azure` is `true` in the AppHost's `appsettings.json`) the dashboard
   prompts on first run for an Azure endpoint, key and two deployment names, and the API and Worker wait for
   them — see *Azure OpenAI, entered through the dashboard* below. To start with no model account, run
   `aspire secret set Foundry:Azure false`; the API and Worker then register model clients that throw rather
   than answer. Setting `Foundry:LocalCli` or either connection string below also skips the prompt.

   **`Foundry:Enabled` does not currently work.** Aspire.Hosting.Foundry 13.5.4-preview's
   `RunAsFoundryLocal()` starts the Foundry service successfully — it comes up healthy and serves requests —
   and then marks its own resource `FailedToStart` about a second later, logging no exception. That cascades
   to everything waiting on the deployment, so the whole application goes down rather than just the AI
   features. Use `Foundry:LocalCli` instead, which drives the same CLI from `FoundryLocalCli.cs` and hands
   the result over as an ordinary connection string:

   ```bash
   winget install --id Microsoft.FoundryLocal --version 0.8.119.102   # see the version pin below
   aspire secret set Foundry:LocalCli true
   foundry model download phi-4-mini
   ```

   Then once per boot, before `aspire run`:

   ```bash
   foundry service start
   foundry model load phi-4-mini
   ```

   Both are optional — the resolver does them itself — but a cold load of a multi-GB model onto the GPU can
   overrun the Aspire CLI's 120s start timeout and abort the launch. The Foundry service outlives an
   `aspire run`, so this is a once-per-boot cost, not once-per-restart. Raise `ASPIRE_CLI_START_TIMEOUT` if
   you would rather let the resolver do it.

   Three things that will otherwise cost an afternoon:

   - **Pin Foundry Local to 0.8.x.** 0.10.0 renamed `foundry service` to `foundry server`, and both the
     Aspire integration and `FoundryLocalCli` call `service`.
   - **"Service is already running" fails the launch** when `Foundry:Enabled` is used: the integration cannot
     adopt a service it did not start, and any `foundry` command touching models starts one. `foundry service
     stop` first. `Foundry:LocalCli` handles this case correctly.
   - **Embeddings are not available on 0.8.x.** Its catalogue contains no embedding models at all. One
     downloaded by a 0.10.x CLI stays in the cache and can be loaded by its full id, but *serving* a request
     from it kills the Foundry service outright — reproducibly, taking chat down with it. Leave
     `Foundry:EmbeddingModel:Name` at an alias 0.8.x cannot resolve, so the resolver skips it and embeddings
     stay on the client that throws. Chat is all the recipe studio needs; semantic search would need a
     genuine 0.8-era embedding model.

   Which models run is `Foundry:ChatModel` and `Foundry:EmbeddingModel` in the AppHost's `appsettings.json`.
   No API key is needed — the local service authenticates nobody.

   **Azure OpenAI, entered through the dashboard.** The default, the least error-prone route, and the one
   that keeps the key out of your shell history. Nothing to set first:

   ```bash
   aspire run
   ```

   The dashboard then shows *Unresolved parameters* with an **Enter values** form asking for the endpoint,
   key and both deployment names, each with a description of where to find it in Azure. Tick **Save to user
   secret** and you are not asked again — it writes to the same AppHost user-secrets store `aspire secret
   set` uses.

   In Azure you need one **Azure OpenAI resource** with two deployments — a chat model that supports tool
   calling, such as `gpt-4o-mini`, and an embedding model such as `text-embedding-3-small`. Supply only the
   base URL, `https://<resource>.openai.azure.com`: an Azure OpenAI resource puts the deployment in the path,
   so the `/openai/deployments/<deployment>` part is appended per deployment.

   **An Azure AI Foundry resource works too** (`kind: AIServices`), and its URL shape is different: one
   **Models** endpoint, ending in `/models`, serves every deployment. Turn the default shape off and supply
   that endpoint instead:

   ```bash
   aspire secret set Foundry:AzureOpenAI false
   ```

   Everything else is asked for in the dashboard: the endpoint, the key, and both **deployment names**. Only
   `Foundry:Azure` and `Foundry:AzureOpenAI` are configuration, because which shape the connection string
   takes has to be decided while the application model is built — before any dialog can be shown. `Foundry:Endpoint`,
   `Foundry:ChatModel:Deployment` and `Foundry:EmbeddingModel:Deployment` are optional and only pre-fill those
   prompts, so the dialog becomes a confirmation rather than a transcription.

   Deployment names are prompted rather than assumed because a wrong one does not fail at startup — the
   connection string is not exercised until the first generation, so it surfaces as a 404 on a creator's
   request rather than as a startup error.

   Or, if you already have model deployments and would rather set the connection strings directly, leave
   `Foundry:Enabled` off and name them; a supplied connection string takes precedence over the dashboard
   prompt. The AppHost passes them through under the same two
   names, so nothing downstream can tell which way they arrived:

   ```bash
   aspire secret set ConnectionStrings:chat "Endpoint=https://<resource>.services.ai.azure.com/models;Key=<key>;DeploymentId=<chat-deployment>"
   aspire secret set ConnectionStrings:embeddings "Endpoint=https://<resource>.services.ai.azure.com/models;Key=<key>;DeploymentId=<embedding-deployment>"
   ```

   Omit `Key=` to authenticate with the ambient Azure credential instead. Each deployment is decided on its
   own: naming only `chat` leaves embeddings on the client that throws, which is a real intermediate state.

10. Enable the AI tasks you want to exercise. The proposal routes ship dark — `AiTaskOptions.Enabled` starts
    empty so no deployment can spend a provider budget merely because a route was deployed — and a request
    for a task that is off is refused with `TaskNotEnabled` rather than queued. `src/CreatorPantry.ApiService/appsettings.Development.json`
    opts in `diagnostic`, `recipe.concepts` and `recipe.first-draft` for local work; a deployed environment
    opts in deliberately, through `Ai:Tasks:Enabled`.

## Installed design system

The drop-in includes the actual CreatorPantry Angular 22 design system rather than a placeholder reference.

- Package: `@creator-pantry/ui`
- Source: `src/web/projects/creator-pantry-ui/src/lib/`
- Public exports: `src/web/projects/creator-pantry-ui/src/public-api.ts`
- Tokens: `src/web/projects/creator-pantry-ui/src/lib/styles/tokens.css`
- Themes: `src/web/projects/creator-pantry-ui/src/lib/styles/themes.css`
- Global foundation: `src/web/projects/creator-pantry-ui/src/lib/styles/global.css`
- Showcase: `src/web/src/app/`
- Theme contract: `data-cp-theme="light|dark"` on `<html>`, managed by `CpThemeService`

The current public components are button, card, badge, field, progress, dialog, and quick action. New reusable UI extends this library and its public API; product workflow components stay in feature folders until repetition proves they belong in the library.

## Toolkit philosophy

The root `CLAUDE.md` holds stable project memory. Rules define non-negotiable boundaries. Skills explain how to implement a task within those boundaries. Agents inspect completed work. Hooks catch a small set of mechanical failures. Prompts should therefore remain small and specific instead of repeating the architecture on every turn.

The files are deliberately editable. Tune naming, providers, roles, and product scope as decisions become concrete, but preserve the core separation between creator-owned sources, derived content, external publication state, and AI-generated proposals.
