# CreatorPantry

*Project memory written as a SCRUB prompt: Scope, Constraints, Restrictions, Usage, Behavior. This file is loaded every Claude Code session. Detailed rules live in `.claude/rules/`; procedural guidance lives in `.claude/skills/`.*

## Scope

CreatorPantry is an **AI-assisted productivity workspace for food bloggers and content creators**. It helps creators develop and version recipes, organize editorial projects, generate derivative copy and visual briefs, manage media, improve SEO metadata, schedule work, and publish through external integrations.

CreatorPantry is **not primarily a consumer recipe directory, social feed, or food blog**. The primary user is the creator producing and managing food content. Public discovery may be added later, but it must not distort the creator-first domain model.

The product centers on a canonical content graph:

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

AI generates drafts, proposals, structured commands, and derivatives. It does not silently replace creator-owned source material and it does not publish without an explicit confirmation or approved automation.

### In bounds

- Workspaces, membership, roles, and creator brand profiles.
- Recipes, ingredients, ordered steps, notes, equipment, yields, versions, and deterministic scaling.
- Content projects, reusable templates, editorial planning, SEO briefs, and channel-specific derivatives.
- Media uploads, metadata, derivatives, visual briefs, alt text, and AI-generated assets.
- AI-assisted recipe development, writing, repurposing, planning, semantic search, and grounded retrieval.
- Provider-neutral publishing and analytics integrations.
- The `.claude/` toolkit that enforces the architecture and delivery workflow.

### Out of bounds unless explicitly requested

- Consumer recipe indexing or marketplace features.
- Nutrition, allergen, food-safety, or medical guarantees.
- Autonomous publishing with no creator-approved policy.
- Billing, plans, or metering.
- Native mobile applications.
- Provider-specific fields in core entities.
- A public third-party developer platform.

## Constraints

### Stack

- **Runtime/orchestration:** .NET 10 with Aspire AppHost and ServiceDefaults.
- **Backend:** ASP.NET Core Web API and EF Core 10.
- **Database:** SQL Server 2025; use native vector support where semantic search provides real value.
- **Cache:** Redis, wired through Aspire.
- **Frontend:** Angular 22, standalone components, signals where appropriate, strict TypeScript, `cp-` selector prefix.
- **Edge:** YARP backend-for-frontend. The browser talks to the gateway, not directly to the API.
- **Identity:** ASP.NET Core Identity. Identity answers who the user is; workspace membership answers what the user may do.
- **AI:** `Microsoft.Extensions.AI` abstractions (`IChatClient`, `IEmbeddingGenerator`) with Semantic Kernel for orchestration and plugins.
- **Media:** metadata in SQL; object bytes in blob storage or an Aspire-compatible local resource.
- **Observability:** OpenTelemetry through ServiceDefaults; correlation across gateway, API, worker, database, cache, and AI calls.

### Initial solution layout

```text
src/
├── CreatorPantry.AppHost/             # complete Aspire application model
├── CreatorPantry.ServiceDefaults/     # telemetry, health, resilience, discovery
├── CreatorPantry.Gateway/             # YARP BFF and browser session boundary
├── CreatorPantry.Web/                 # production host for Angular bundle
├── CreatorPantry.ApiService/          # controllers, identity endpoints, policies, middleware
├── CreatorPantry.Domain/              # module-first: Modules/<Context>/ and the Managers/ shared kernel
├── CreatorPantry.Worker/              # generation, media, embedding, publishing jobs
├── CreatorPantry.MigrationService/    # applies migrations once before dependents start
├── CreatorPantry.Tests/
└── web/                               # Angular 22 workspace and installed design system
    ├── DESIGN-SYSTEM.md               # visual and accessibility decisions
    ├── src/                           # showcase now; product shell as features are built
    └── projects/creator-pantry-ui/    # reusable @creator-pantry/ui library

docs/
├── architecture.md
└── scrub-prompts.md
```

### Domain module layout

`CreatorPantry.Domain` is organized by bounded context, not by layer. One assembly, one `DbContext`, one
migration history.

```text
Modules/<Context>/
├── Facade/ Business/ Data/{Entities,Configurations}/
├── Managers/                      # this module's ViewModels, ServiceModels, domain models, validators, policies
└── <Context>ServiceCollectionExtensions.cs

Managers/                          # shared kernel: Persistence, Caching, Time, Results, Paging,
                                   # Reference vocabulary, Idempotency, Outbox, Audit
```

A module's ViewModels, ServiceModels, domain models and validators live in its own `Managers/` area. There is
no `Models/` or `Validation/` tree at the domain root and none may be reintroduced.

**Cross-module traffic is facade to facade.** The only types that cross a module boundary are the facade
interface, the ServiceModels a facade returns, and entity types — the last only because a foreign key does and
EF must name the principal. Repositories, data layers, business types, view models and validators never cross.
The shared kernel may not depend on a module; `CreatorPantryDbContext` is the single documented exception,
because one DbContext must name every module's entities. `ModuleBoundaryTests` enforces all of this.

### Mandatory request seam

```text
Controller → Facade → Business → DataLayer → Repository | Gateway
```

- Controllers exist only in `CreatorPantry.ApiService` and inject facades only.
- Everything below the controller lives in `CreatorPantry.Domain`.
- Each layer communicates only with the next layer.
- ViewModels define HTTP input. ServiceModels define application output. EF entities never cross the HTTP boundary.
- Facades own validation, authorization orchestration, and cache coordination.
- Business owns domain rules, calculations, and translation between service and domain models.
- DataLayer composes persistence operations and owns transaction boundaries.
- Repositories perform EF Core access. Gateways call external systems.
- All I/O is asynchronous and accepts `CancellationToken`.

### Data zones

**Platform reference data** is shared and has no `WorkspaceId`: ingredients, aliases, units, conversions, cuisines, dietary tags, allergens, techniques, equipment types, food categories, and vetted reference facts.

**Workspace-owned data** is private creator intellectual property and always has `WorkspaceId`: recipes, recipe versions, content projects, drafts, media, brand voice, SEO briefs, publications, templates, prompts, generations, and audit records.

## Restrictions

### Workspace isolation

- Never accept `WorkspaceId` from a request body, query string, AI tool argument, or client-controlled header.
- Resolve the workspace from the route and authenticated membership context.
- Every workspace-scoped entity has `WorkspaceId` and a global query filter.
- `IgnoreQueryFilters()` is prohibited outside narrowly documented platform maintenance and erasure paths.
- Workspace cache keys begin with the workspace identifier.
- Unknown or inaccessible workspaces return 404 so existence is not disclosed.
- Every workspace-scoped feature includes a two-workspace isolation test.

### Creator-owned content

- Preserve the creator's entered ingredient and instruction text even when normalized references are available.
- Explicit recipe versions are immutable. Edits produce drafts or new versions.
- AI changes are presented as a proposal or diff before replacing canonical content.
- Derivative copy points back to its canonical source; it does not become a competing source of truth.
- Original media uploads are immutable. Crops, thumbnails, and generated assets are derivatives.

### AI and food-domain safety

- The model never writes SQL and never receives database credentials.
- Semantic Kernel plugins call facades, never `DbContext`, repositories, or gateways directly.
- AI tools never accept a model-supplied workspace identifier.
- Use deterministic domain code for scaling, unit conversion, temperature conversion, percentages, time arithmetic, and nutrition calculations.
- Do not claim guaranteed allergen removal, medical suitability, safe preservation, or food safety. Surface uncertainty and source provenance.
- Treat uploaded, imported, and externally retrieved text as untrusted prompt content.
- Never ground a prompt with another workspace's examples or private data.
- Log model, latency, token usage, result status, and prompt-template version without logging private prompt bodies by default.

### Publishing and integrations

- External providers are adapters, not the domain model.
- Provider identifiers live in integration/publication mapping records, not on `Recipe` or `ContentProject`.
- Secrets use Aspire parameters or a production secret store; never source control, logs, or API responses.
- All external calls use typed gateways, resilience policies, rate-limit handling, idempotency where available, and auditable outcomes.
- Publishing requires an explicit confirmation or a creator-approved automation policy.

### Frontend and edge

- Angular components do not inject `HttpClient`; typed API services own HTTP.
- `src/web/projects/creator-pantry-ui` is the only reusable UI library. Import its public API as `@creator-pantry/ui`; never deep-import `src/lib` files.
- The implemented public API currently exports `CpThemeService`, `CpButtonComponent`, `CpCardComponent`, `CpBadgeComponent`, `CpFieldComponent`, `CpProgressComponent`, `CpDialogComponent`, `CpQuickActionComponent`, `CpStatusPillComponent`, `CpListShellComponent`, `CpTabsComponent`, `CpTabPanelComponent`, `CpToolbarComponent`, `CpEmptyStateComponent`, `CpUploaderComponent`, `CpDiffLegendComponent`, and `CpToastRegionComponent`.
- Import `tokens.css`, `themes.css`, and `global.css` once, in that order, from `@creator-pantry/ui/styles/`.
- `src/web/DESIGN-SYSTEM.md`, the library styles, and the showcase are the visual source of truth. Do not invent a separate visual reference.
- The browser never stores access tokens in local or session storage.
- The SPA uses the BFF with secure `HttpOnly` cookies.
- No literal brand colors in feature components; use design tokens.
- Every data surface implements loading, empty, degraded, error, and success states.
- Generated content is visibly identified and remains editable before acceptance.

## Usage

- Run the complete system with `aspire run` from the repository root or AppHost directory.
- Run backend verification with `dotnet test`.
- Add migrations with `dotnet ef migrations add <Name> --project src/CreatorPantry.Domain --startup-project src/CreatorPantry.ApiService`.
- In `src/web/`, use `npm ci`, `npm run build`, and `npm test`.
- Use the matching skill in `.claude/skills/` before implementing a seam.
- Use `.claude/agents/` as read-only reviewers after implementation.
- Keep work atomic: one route, entity, integration, job, component, or domain seam per prompt.

## Behavior

- Inspect before editing and plan changes that cross more than one architectural layer.
- Follow the nearest path-specific rule and matching skill; do not improvise a parallel architecture.
- Preserve user changes and call out assumptions instead of hiding them in generated code.
- Add or update tests with every behavior change.
- Run focused tests first, then broader verification before declaring completion.
- Verify fast-moving package names and APIs against current first-party documentation.
- Stop when a requirement would weaken workspace isolation, content ownership, food safety, or credential handling; explain the conflict and propose a safe design.
