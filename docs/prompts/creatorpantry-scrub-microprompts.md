# CreatorPantry — SCRUB Microprompts

Atomic prompts for implementing the CreatorPantry requirements with Claude Code and the CreatorPantry `.claude/` toolkit.

This sequence is modeled on the delivery discipline in [`Architect4Hire/AegisScribe/docs/scrub-prompts.md`](https://github.com/Architect4Hire/AegisScribe/blob/main/docs/scrub-prompts.md), reviewed at repository commit `f7f0d9e`. It preserves the useful mechanics of that document—one seam per prompt, plan-first behavior, explicit restrictions, milestone checkpoints, and separate audit prompts—while replacing the game-specific domain with CreatorPantry's recipe-development, content-production, media, and publishing requirements.

The functional source of truth is `creatorpantry-ai-augmented-recipe-development-requirements.md`. The architectural source of truth is `CLAUDE.md` plus `.claude/rules/` and `.claude/skills/`. If they conflict, stop and reconcile the documents before writing code.

**These are deliberately small.** Part 1 contains **19 phases and 331 microprompts**. Run one prompt at a time. A prompt may cross layers only when it is explicitly a vertical-integration or verification prompt. Most prompts should produce one contract, entity, service, endpoint, component, migration, adapter, or focused test group.

## The “easy as pie” product rule

CreatorPantry hides system complexity from creators. The user should never need to understand prompt engineering, model selection, blob storage, SQL pointers, embeddings, workflow engines, or internal status machines.

Every user-facing workflow must:

- begin with a plain-language goal such as “Write a blog post” or “Plan this week”;
- show one obvious primary action per step;
- disclose advanced options only when requested;
- provide useful defaults from the active workspace, brand guide, recipe, and recent choices;
- explain what information is needed and why in ordinary language;
- show progress, completed steps, and the next recommended action;
- save work automatically as a draft and allow safe resume on another session;
- preserve user edits across refreshes and recoverable failures;
- preview generated or destructive changes before applying them;
- make Skip, Back, Save and exit, Cancel, and Start over behavior explicit;
- distinguish source material, AI suggestions, accepted content, and published content;
- provide an undo or version-history path for every meaningful content change;
- meet WCAG 2.2 AA without requiring drag-and-drop or pointer-only interaction.

## The reusable SCRUB skeleton

```text
SCOPE:        one observable change and the exact repository seam it touches
CONSTRAINT:   stack, rule files, skills, requirement IDs, and prerequisites
RESTRICTION:  explicit exclusions and invariants that must not be weakened
UTILIZATION:  skills, read-only reviewers, and tools to invoke
BEHAVIOR:     inspect, plan, wait for approval, implement, test, and report
```

## How to use this file

- Run Part 1 in order, one prompt at a time.
- Do not paste an entire phase into Claude Code.
- Start each prompt in a clean context when practical.
- Every prompt that adds an HTTP route uses `add-endpoint` and follows `Controller → Facade → Business → DataLayer → Repository | Gateway`.
- Every workspace-owned feature uses `add-workspace-entity` and owes a two-workspace endpoint test.
- Every recipe mutation uses `add-recipe-feature` and preserves creator-entered text.
- Every AI capability uses `add-ai-capability`; generated content remains a proposal until explicit acceptance.
- Every UI prompt uses `creatorpantry-design-system` and `new-component`; feature components do not invent tokens or primitives.
- Every external provider uses `add-external-integration`; provider DTOs never cross the gateway boundary.
- Every document-backed prompt uses an approved BrandContextPackage assembled server-side from an exact
  BrandStyleGuideVersion and authorized source-document pointers.
- Every multi-step creator journey uses the shared guided-workflow shell and can save/resume without
  keeping private content only in browser memory.
- Editorial workflow state and the user's daily work plan are separate concepts; link them by stable ID
  instead of overloading one Kanban card to mean both.
- Plan first and wait for approval whenever the prompt says to wait. The plan is the cheapest place to stop a wrong architecture.
- Mark a prompt `- done` only after its stated verification passes.

## Credentials and external prerequisites

| First needed | Requirement |
| --- | --- |
| Phase 0 | Local .NET 10, Node/npm, Docker-compatible container runtime, and Aspire CLI |
| Phase 1 | No production identity secret; local development credentials only |
| Phase 8 | Azure AI Foundry/OpenAI or an approved compatible local provider |
| Phase 12 | Blob storage and an approved image-generation provider |
| Phase 13 | Buffer API credentials and a disposable test profile |
| Phase 14 | Approved nutrition/reference-data provider or licensed dataset |
| Phase 16 | Target Azure subscription, registry, secret store, and deployment identity |

Everything through Phase 7 must run against seeded local data with no paid AI or publishing calls.

## Extension requirements added by this revision

| ID | Requirement |
| --- | --- |
| BRAND-001 | Store every private brand source document as an immutable blob object version with workspace-scoped SQL metadata and stable object pointers. |
| BRAND-002 | Extract, review, correct, version, chunk, and optionally embed authorized document content without altering the original. |
| BRAND-003 | Maintain a structured, versioned guide for voice, tone, tenor, writing style, audience, language, channels, blog content, social content, and visual/image-generation style. |
| BRAND-004 | Generate a source-cited brand-guide proposal from selected examples and questionnaire answers without activating it automatically. |
| BRAND-005 | Let the creator manually create, edit, compare, approve, activate, and roll forward guide versions. |
| BRAND-006 | Assemble a bounded, deterministic, workspace-safe BrandContextPackage pinned to exact guide and document versions for each generation task. |
| BRAND-007 | Apply approved brand context and provenance to blog, editorial, SEO, social, and other writing prompts. |
| BRAND-008 | Apply approved visual style and image-reference guidance to photography concepts and image prompts. |
| BRAND-009 | Provide a plain-language “Create my voice” wizard with autosave, resume, review, test drive, and explicit activation. |
| BRAND-010 | Provide Brand Library, source-detail, guide-editor, version-history, comparison, and activation screens. |
| FLOW-001 | Provide a versioned catalogue of goal-oriented workflows that orchestrate existing product capabilities rather than duplicating domain logic. |
| FLOW-002 | Persist workflow runs and step progress so every workflow can save, exit, resume, recover, skip optional work, and show one recommended next action. |
| FLOW-003 | Provide dedicated guided screens for recipe creation, recipe improvement, testing/approval, brand setup, blog/SEO, images, social content, asset organization, and planning/publishing. |
| FLOW-004 | Display active, recently completed, and attention-needed workflows in a central Workflow Hub and dashboard. |
| TASK-001 | Maintain personal workspace tasks separately from editorial Content Board cards while allowing stable links between them. |
| TASK-002 | Provide one-field quick capture and a focused daily view that answers “What should I do next?” |
| TASK-003 | Provide an accessible weekly Kanban with Backlog, scheduled work, In progress, Waiting, and Done plus keyboard move/reorder controls. |
| TASK-004 | Allow an explicit workflow-step-to-task link without silently creating a task for every step. |
| TASK-005 | Optionally support idempotent daily/weekly recurring task templates in the user's time zone. |
| EASE-001 | Present plain-language goals and one obvious primary action per step. |
| EASE-002 | Use progressive disclosure and useful workspace/guide/recipe defaults. |
| EASE-003 | Autosave drafts and preserve user work through refreshes and recoverable failures. |
| EASE-004 | Show progress, provenance, cost, warnings, and next action without exposing infrastructure jargon. |
| EASE-005 | Preview generated or destructive changes and provide confirmation, version history, or undo paths. |
| EASE-006 | Meet WCAG 2.2 AA with non-drag alternatives and responsive daily/weekly workflows. |

## Checkpoints

| After | Demonstrable result |
| --- | --- |
| **0.7** | `aspire run` shows SQL, Redis, blob storage, migration service, worker, API, gateway, web host, and Angular app healthy |
| **1.10** | A user can register, sign in through the BFF, refresh, sign out, and never expose a browser token |
| **2.10** | Two workspaces with similar data cannot read, mutate, cache, or enumerate one another |
| **3.8** | The CreatorPantry design-system showcase renders in light/dark themes and passes the accessibility smoke test |
| **4A.9** | The domain is organized by bounded context, the request seam and workspace isolation are unchanged, and the move produces no migration |
| **5.12** | A creator can create, retrieve, edit, and list a structured recipe through the complete request seam |
| **6.10** | Versions, comparison, restore, duplicate, archive, and search work without mutating history |
| **7.12** | Parsing, scaling, conversion, normalization, and yield previews are deterministic and never overwrite a recipe silently |
| **8.12** | An AI proposal can be generated, reviewed, partially accepted, rejected, retried, and audited without bypassing domain rules |
| **9.10** | Recipe ideation, drafting, revision, substitution, adaptation, review, and explanation run through the shared proposal lifecycle |
| **10.8** | Test runs and readiness gates take a recipe from Draft to Approved with an auditable history |
| **11.10** | An approved recipe produces editorial, SEO, JSON-LD, Markdown, and PDF outputs tied to an exact version |
| **11A.24a** | A creator can upload writing/image examples, generate an editable brand guide, approve a version, and see it shape writing and image prompts |
| **12.10** | Recipe-aware images and prompts move from staging into the DAM with lineage and safe cleanup |
| **13.10** | Social content moves through the board and can be scheduled/cancelled idempotently through Buffer |
| **13A.34** | A creator can start or resume every major workflow and organize Today/This Week work on an accessible Kanban board |
| **14.7** | Dietary, allergen, nutrition, and safety analysis exposes evidence, uncertainty, and unresolved mappings |
| **15.7** | Security, observability, resilience, performance, and accessibility gates pass |
| **16.6** | CI/CD, infrastructure, runbooks, restore test, and release audit are ready for deployment |

---

# Part 1 — Build sequence

## Phase 0 — Repository and runtime foundation

### 0.1 Baseline decisions and architecture record - done

```text
SCOPE: Create docs/architecture-decisions/baseline.md recording the decisions still expressed as
working directions in the requirements: workspace tenancy, BFF authentication, SQL Server 2025,
Redis, blob storage, staged media, AI proposal lifecycle, unit systems, nutrition optionality, and
Buffer as the first publishing adapter. Do not implement code.
CONSTRAINT: CLAUDE.md; requirements DEC-001 through DEC-012.
RESTRICTION: Do NOT silently resolve an open product choice. Mark unresolved items DECIDE with options,
impact, and the default assumed by later prompts.
BEHAVIOR: Inspect existing docs, show the proposed decision table, wait for approval, then write only
the architecture record and link it from docs/architecture.md.
```

### 0.2 Solution skeleton - done

```text
SCOPE: Create the .NET 10 solution and empty projects under src/: CreatorPantry.AppHost,
.ServiceDefaults, .Gateway, .Web, .ApiService, .Domain, .Worker, .MigrationService, and .Tests. Add only
the references required by CLAUDE.md; ApiService, Worker, and MigrationService reference Domain.
CONSTRAINT: .claude/rules/aspire.md and backend.md; aspire-init skill.
RESTRICTION: No domain entities, endpoints, resources, auth, provider packages, or feature code. Domain
must not reference any host project.
UTILIZATION: aspire-init.
BEHAVIOR: Show the exact commands and project-reference graph, wait for approval, execute, and confirm
`dotnet build` succeeds.
```

### 0.3 SQL Server, Redis, and blob resources - done

```text
SCOPE: Declare SQL Server 2025 with database `creatorpantrydb`, Redis, and the selected Aspire-compatible
blob resource in AppHost. Use secret parameters, persistent container lifetimes, and named volumes.
CONSTRAINT: .claude/rules/aspire.md and media.md; add-aspire-resource skill.
RESTRICTION: Do NOT reference resources from application projects yet. No literal passwords, ports, or
connection strings. SQL Server must support the later vector workload.
UTILIZATION: add-aspire-resource.
BEHAVIOR: Plan resource names and persistence, wait for approval, implement, and show each resource
healthy in the Aspire dashboard.
```

### 0.4 ServiceDefaults and health endpoints - done

```text
SCOPE: Wire ServiceDefaults into every executable project and add separate /health and /alive routes to
the API, gateway, web host, worker, and migration service where appropriate.
CONSTRAINT: .claude/rules/aspire.md.
RESTRICTION: Do NOT add custom exporters or business dependency probes yet. Liveness must not call SQL,
Redis, blob, AI, or external providers.
BEHAVIOR: Implement, run focused tests, and report the liveness/readiness behavior per process.
```

### 0.4a Application clock and time-zone seam - done

```text
SCOPE: Add one injectable application clock and one time-zone conversion service used by domain code,
jobs, tests, scheduled publishing, derived day-of-week, and audit timestamps.
CONSTRAINT: API-009 and CALC-002; backend rules.
RESTRICTION: No scattered DateTime.UtcNow in feature code, no server-local time, and no inferred user zone.
BEHAVIOR: Show API/DST policy, wait for approval, implement with fixed-clock and DST boundary tests.
```

### 0.5 Migration service host - done

```text
SCOPE: Implement MigrationService as a one-shot worker that resolves the future DbContext, runs EF
migrations through an execution strategy, and exits. Wire WaitFor(sql) in AppHost.
CONSTRAINT: .claude/rules/backend.md and aspire.md.
RESTRICTION: The DbContext does not exist yet; create only the host loop and extension seam. Do NOT
apply migrations from ApiService startup.
BEHAVIOR: Show the loop and failure behavior, wait for approval, implement, and prove a no-migration
run exits cleanly.
```

### 0.6 Angular 22 workspace and production web host - done

```text
SCOPE: Scaffold strict Angular 22 in src/web with standalone components and cp- selector prefix. Wire
the development server into AppHost. Configure CreatorPantry.Web to serve the production bundle and a
runtime configuration endpoint for the gateway URL.
CONSTRAINT: .claude/rules/frontend.md, gateway.md, and aspire.md.
RESTRICTION: Do NOT hardcode service URLs, run ng serve outside Aspire, add feature screens, install a
second CSS framework, or add authentication yet.
UTILIZATION: aspire-init and add-aspire-resource.
BEHAVIOR: Plan development and production paths, wait for approval, implement, then run `ng build` and
show the starter SPA served by Aspire.
```

### 0.7 First complete local run - done

```text
SCOPE: Run the complete empty system and repair only wiring that prevents startup: SQL, Redis, blob,
migration service, worker, API, gateway, web host, and Angular development app.
CONSTRAINT: .claude/rules/aspire.md.
RESTRICTION: No feature work, schema, auth, or placeholder business endpoints.
UTILIZATION: aspire-monitoring.
BEHAVIOR: Give one health line per resource, list any startup fix with evidence, and leave the solution
building cleanly.
```

## Phase 1 — Identity, gateway, and browser boundary

> Build the browser security boundary before workspace tenancy. Later authorization tests must exercise the same edge the real SPA uses.

### 1.1 ApplicationUser and DbContext - done

```text
SCOPE: Add ApplicationUser : IdentityUser with DisplayName, CreatedAt, and LastWorkspaceId, plus
CreatorPantryDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string> in Domain/Data.
Register it through the Aspire SQL integration in ApiService and MigrationService.
CONSTRAINT: .claude/rules/auth.md and backend.md.
RESTRICTION: One DbContext for the current modular monolith. No recipe or workspace entities yet. Do
NOT create a migration in this prompt.
BEHAVIOR: Show entity/context shape and registrations, wait for approval, implement, and prove DI
resolution in a focused test.
```

### 1.2 Identity user store and registration seam - done

```text
SCOPE: Implement user registration through AuthController → IAuthFacade → IAuthBusiness →
IAuthDataLayer → IUserRepository wrapping UserManager. Include request validation and a stable result.
CONSTRAINT: .claude/rules/auth.md and backend.md; add-endpoint skill.
RESTRICTION: Controller injects only IAuthFacade. No login token issuance, cookies, workspace roles,
or MapIdentityApi bearer tokens. Never reveal whether an email already exists outside the approved
registration response.
UTILIZATION: add-endpoint.
BEHAVIOR: Plan every layer and tests, wait for approval, implement, and run focused per-layer tests.
```

### 1.3 Password management seam - done

```text
SCOPE: Add password-change and password-reset initiation/completion operations through the existing
auth layer stack.
CONSTRAINT: .claude/rules/auth.md; add-endpoint skill.
RESTRICTION: Responses must not enumerate accounts. Reset tokens never enter logs. Do NOT implement
email delivery; expose an injectable notification seam and a development sink only.
UTILIZATION: add-endpoint and add-notification.
BEHAVIOR: Plan the response and token-handling rules, wait for approval, implement, and test known and
unknown addresses returning indistinguishable public responses.
```

### 1.4 PlatformAdmin role and policy - done

```text
SCOPE: Add role support, seed exactly one platform-wide role named PlatformAdmin idempotently, and add
the PlatformAdmin authorization policy.
CONSTRAINT: .claude/rules/auth.md.
RESTRICTION: Viewer, Contributor, Editor, and Owner are workspace membership roles, not Identity roles.
Do NOT seed them into ASP.NET Core Identity.
BEHAVIOR: Implement and add a test that repeated startup does not duplicate the role.
```

### 1.5 Initial identity migration - done

```text
SCOPE: Generate the initial Identity/OpenIddict-ready EF migration without applying it.
CONSTRAINT: .claude/rules/backend.md; migration command from CLAUDE.md.
RESTRICTION: Do NOT hand-edit generated migration code. Do NOT apply before review.
BEHAVIOR: Show the model diff, indexes, and rollback shape; wait for approval; then apply through the
migration service and confirm `dotnet ef migrations has-pending-model-changes` is clean.
```

### 1.6 Gateway and API routes - done

```text
SCOPE: Configure CreatorPantry.Gateway with YARP routes for /api/{**catch-all} to ApiService through
Aspire service discovery. Keep the API reachable only as approved by the baseline decision.
CONSTRAINT: .claude/rules/gateway.md and aspire.md.
RESTRICTION: No literal hostnames or ports. Do NOT add browser authentication in this prompt.
UTILIZATION: add-aspire-resource.
BEHAVIOR: Show route/cluster configuration, wait for approval, implement, and prove an anonymous
health request traverses the gateway.
```

### 1.6a API versioning convention - done

```text
SCOPE: Add the approved URL-based API versioning convention under /api/v1, default/unsupported-version
behavior, version metadata, and tests before feature routes depend on it.
CONSTRAINT: API-001 and .claude/rules/api-contract.md; add-endpoint skill.
RESTRICTION: No feature endpoint or duplicate unversioned route. Gateway preserves versioned paths.
BEHAVIOR: Verify current first-party package/API, show convention, wait for approval, implement/tests.
```

### 1.6b OpenAPI and Scalar - done

```text
SCOPE: Configure versioned OpenAPI and Scalar in approved development environments with auth, ProblemDetails,
upload, pagination, and idempotency-header conventions represented.
CONSTRAINT: API-006 and .claude/rules/api-contract.md.
RESTRICTION: No production exposure unless approved; no secret/example token or internal provider schema.
BEHAVIOR: Show document/security plan, wait for approval, implement and snapshot-test the generated contract.
```

### 1.7 Authorization server clients - done

```text
SCOPE: Configure the approved OAuth/OIDC server arrangement and seed the confidential CreatorPantry
BFF client plus an operations client. Use authorization code + PKCE, refresh tokens, revocation, and
logout as specified in auth.md.
CONSTRAINT: .claude/rules/auth.md and gateway.md.
RESTRICTION: The BFF client secret stays server-side. Do NOT register a native/mobile client; mobile is
out of scope. Do NOT expose opaque provider objects from the API.
BEHAVIOR: Plan clients, grants, redirects, and secret storage; wait for approval; implement and add
configuration tests.
```

### 1.8 Gateway BFF session - done

```text
SCOPE: Make the gateway the confidential browser client: initiate sign-in, handle callback, keep tokens
server-side, issue a secure HttpOnly session cookie, refresh server-side, and implement logout.
CONSTRAINT: .claude/rules/gateway.md and auth.md.
RESTRICTION: No access or refresh token in browser storage, JavaScript, HTML, or logs. Cookie uses
Secure, HttpOnly, SameSite, bounded lifetime, and antiforgery protection for unsafe methods.
BEHAVIOR: Plan the complete handshake, wait for approval, implement, and show the browser cookie/token
boundary under test.
```

### 1.9 Header sanitization, CORS, and antiforgery - done

```text
SCOPE: Strip caller-supplied identity and forwarding headers, add only trusted downstream headers,
configure exact-origin CORS, and enforce antiforgery on unsafe BFF-proxied requests.
CONSTRAINT: .claude/rules/gateway.md and auth.md.
RESTRICTION: No wildcard origin with credentials. No forwarded caller token may reach the API. GET and
health behavior must remain usable.
BEHAVIOR: Implement with forged-header, disallowed-origin, missing-antiforgery, and valid-request tests.
```

### 1.9a Idempotency foundation - done

```text
SCOPE: Add a reusable workspace/user-scoped idempotency store and facade helper for declared mutating
operations, binding key to operation name, normalized request hash, outcome, and expiry.
CONSTRAINT: API-005 and NFR-006; backend and tenancy rules.
RESTRICTION: No global bare key, no replay with different payload, no caching of secrets or binary bodies,
and no automatic use on every endpoint.
BEHAVIOR: Show key/state/concurrency design, wait for approval, implement first/replay/conflict/expiry tests.
```

### 1.9b Edge hardening: response headers, body limits, trusted proxies - done

*Added during delivery: a review of the sequence found no prompt covering these gateway.md requirements.*

```text
SCOPE: Add browser security response headers (CSP, nosniff, frame/referrer/opener/permissions policy, HSTS
outside Development, no Server header) to the gateway and the SPA host; a configurable default request-body
limit (4 MB) with per-endpoint overrides on the gateway and API; and forwarded-header handling that trusts
only configured proxies/networks, one hop, so rate limits and Secure cookies see the real client and scheme.
CONSTRAINT: .claude/rules/gateway.md and auth.md.
RESTRICTION: No wildcard or unsafe-eval CSP; scripts stay 'self'. No forwarded header is trusted when no
proxy is configured. Oversized bodies are rejected before they are proxied. Upload routes raise limits
explicitly rather than lifting the default.
BEHAVIOR: Implement with header-presence, HSTS, oversized-body, endpoint-override, trusted/untrusted-proxy,
one-hop, and per-client rate-limit tests; render the production SPA under its CSP.
```

### 1.10 Edge verification - done

```text
SCOPE: Add end-to-end edge tests for registration, sign-in, authenticated API proxying, refresh,
logout, anonymous 401, forged-header stripping, antiforgery failure, and absence of browser tokens.
CONSTRAINT: .claude/rules/auth.md and gateway.md.
RESTRICTION: Assert behavior through Gateway/Web, not by bypassing them with direct controller calls.
UTILIZATION: api-contract-checker and architecture-reviewer.
BEHAVIOR: Run the reviewers, fix only edge findings, run all focused tests, and report proof for each
security property.
```

## Phase 2 — Workspace tenancy core

> Workspace isolation is the invariant beneath every creator-owned recipe, prompt, asset, proposal, card, and publication. A missing filter fails silently, so this phase is intentionally repetitive.

### 2.1 Workspace and membership entities - done

```text
SCOPE: Add Workspace and WorkspaceMembership with WorkspaceRole { Viewer = 0, Contributor = 10,
Editor = 20, Owner = 30 }, status, joined timestamps, and required indexes. Generate but do not apply
the migration.
CONSTRAINT: .claude/rules/tenancy.md and auth.md; add-workspace-entity skill.
RESTRICTION: Roles are ordered, not flags. Membership is authorization data, not an Identity role.
Workspace slug uniqueness and membership uniqueness must be explicit.
UTILIZATION: add-workspace-entity.
BEHAVIOR: Show entity/index shapes and role implications, wait for approval, create the migration,
review it, apply through MigrationService, and test constraints.
```

### 2.2 Workspace-owned contract and request context - done

```text
SCOPE: Add the common workspace-owned entity contract with required WorkspaceId and a scoped immutable
IWorkspaceContext exposing resolved workspace and membership.
CONSTRAINT: .claude/rules/tenancy.md.
RESTRICTION: Access before resolution throws. Never return Guid.Empty, nullable defaults, or accept a
caller-supplied WorkspaceId.
BEHAVIOR: Show fail-closed behavior, wait for approval, implement, and test resolved/unresolved paths.
```

### 2.3 Workspace resolution layer stack - done

```text
SCOPE: Implement workspace lookup and membership verification through IWorkspaceResolutionFacade →
IWorkspaceBusiness → IWorkspaceDataLayer → IWorkspaceRepository.
CONSTRAINT: .claude/rules/tenancy.md and backend.md; add-endpoint skill.
RESTRICTION: No middleware yet. Unknown and inaccessible workspaces must share one not-found result.
Business, not Repository, owns disclosure behavior.
UTILIZATION: add-endpoint.
BEHAVIOR: Plan signatures and result types, wait for approval, implement, and test member, nonmember,
inactive, and unknown cases.
```

### 2.4 Workspace resolution middleware - done

```text
SCOPE: Add middleware resolving `{workspaceSlug}` from `/api/v1/workspaces/{workspaceSlug}/...`, calling
the resolution facade, and populating IWorkspaceContext before authorization and controllers.
CONSTRAINT: .claude/rules/tenancy.md and auth.md.
RESTRICTION: Route only. Never read workspace identity from header, query, body, LastWorkspaceId, AI
argument, or cache entry. Nonmember and unknown both return 404.
BEHAVIOR: Show pipeline position, wait for approval, implement, and test valid, nonmember, inactive,
unknown, and workspace-less routes.
```

### 2.5 Global workspace query filter - done

```text
SCOPE: Apply a global EF query filter by convention to every workspace-owned entity using the current
IWorkspaceContext.
CONSTRAINT: .claude/rules/tenancy.md.
RESTRICTION: Do NOT configure filters entity by entity. Do NOT use IgnoreQueryFilters on a request
path. Plan the pre-resolution WorkspaceMembership lookup explicitly.
BEHAVIOR: Show the convention and membership exception design, wait for approval, implement, and add a
test that a newly discovered workspace-owned entity would be covered automatically.
```

### 2.6 WorkspaceId SaveChanges interceptor - done

```text
SCOPE: Add a SaveChanges interceptor that stamps WorkspaceId on added workspace-owned entities and
throws when an entity arrives with another workspace's ID or attempts ownership mutation.
CONSTRAINT: .claude/rules/tenancy.md.
RESTRICTION: Feature Business code must never assign WorkspaceId. Updates cannot transfer ownership.
BEHAVIOR: Implement and test automatic stamp, cross-workspace insert rejection, and ownership-change
rejection.
```

### 2.7 Workspace membership policies - done

```text
SCOPE: Implement WorkspaceViewer, WorkspaceContributor, WorkspaceEditor, and WorkspaceOwner policies
with one ordered requirement/handler reading IWorkspaceContext and verified membership.
CONSTRAINT: .claude/rules/auth.md and tenancy.md.
RESTRICTION: Fail closed when context or membership is absent. Encode role implication once. Do not
list role names repeatedly on endpoints.
BEHAVIOR: Plan policy mapping, wait for approval, implement, and test every role against every policy.
```

### 2.8 Workspace management endpoints - done

```text
SCOPE: Add GET /api/v1/me with memberships, POST /api/v1/workspaces, and GET/PATCH
/api/v1/workspaces/{workspaceSlug} through the full request seam. Creator becomes Owner atomically.
CONSTRAINT: add-endpoint and add-workspace-entity skills; .claude/rules/tenancy.md.
RESTRICTION: A workspace must retain at least one Owner. Slug derivation and name rules live in
Business. No controller-side entity mapping or DbContext access.
BEHAVIOR: Plan the operations and transaction, wait for approval, implement with per-layer and endpoint
tests.
```

### 2.9 Workspace cache-key helper - done

```text
SCOPE: Add a cache-key builder that requires WorkspaceId for private values and provides a distinct
global-reference path for shared values.
CONSTRAINT: .claude/rules/tenancy.md.
RESTRICTION: A private key can never be created without workspace scope; a global key can never accept
workspace scope. Do not cache EF entities.
BEHAVIOR: Show the API before writing it, wait for approval, implement, and test both correct use and
misuse prevention.
```

### 2.9a Workspace audit log - done

```text
SCOPE: Add immutable workspace-owned AuditLog with actor, action, resource type/id, timestamp, correlation,
safe summary, and before/after references plus one injectable audit writer.
CONSTRAINT: SEC-007 and DATA-RQ-001; add-workspace-entity skill.
RESTRICTION: No secrets, tokens, prompt bodies, recipe bodies, or provider payloads in audit detail.
Feature code cannot update/delete ordinary audit rows.
BEHAVIOR: Show schema/redaction policy, wait for approval, implement migration and immutability/isolation tests.
```

### 2.9b Transactional outbox foundation - done

```text
SCOPE: Add OutboxMessage plus DataLayer transaction helper and Worker dispatcher for internal durable events,
with lease, attempt, next-attempt, completion, poison state, correlation, and idempotent handler contract.
CONSTRAINT: NFR-006 and .claude/rules/backend.md; add-background-job skill.
RESTRICTION: Domain write and outbox row commit together. Handlers never assume exactly-once delivery.
No external provider call inside the source transaction.
BEHAVIOR: Show state/transaction design, wait for approval, implement commit/replay/restart/poison tests.
```

### 2.10 Two-workspace test harness and audit - done

```text
SCOPE: Build a reusable integration fixture with Workspace A and Workspace B, similar record names,
users in distinct memberships, an Owner plus lower roles, and clients authenticated through Gateway.
Prove isolation against the workspace endpoints.
CONSTRAINT: .claude/rules/tenancy.md testing requirements.
RESTRICTION: Assertions go through HTTP and the real middleware/policies. A repository-only test is not
isolation coverage.
UTILIZATION: workspace-isolation-auditor, then architecture-reviewer.
BEHAVIOR: Show fixture shape, wait for approval, implement, run both reviewers, fix blockers, and keep
the fixture reusable by every later workspace feature.
```

## Phase 3 — CreatorPantry design system and frontend foundation

### 3.1 Install the approved design-system package - done

```text
SCOPE: Copy the approved CreatorPantry Angular 22 design-system library into src/web without changing
its public API, then wire its tokens, themes, and exports into the application.
CONSTRAINT: .claude/rules/design-system.md and frontend.md; creatorpantry-design-system skill; the
approved `creator-pantry-angular22-design-system` package is the source of truth.
RESTRICTION: Do NOT recreate components from screenshots, rename selectors, replace tokens, or add a
parallel Tailwind/material component system.
UTILIZATION: creatorpantry-design-system.
BEHAVIOR: Inventory incoming files and destination paths, wait for approval, copy and wire them, then
run `ng build` and library tests.
```

### 3.2 Token and theme verification - done

```text
SCOPE: Verify semantic color, typography, spacing, radius, shadow, motion, and z-index tokens plus
light/dark theme switching. Add only missing tests or documentation needed to make the contract clear.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system skill.
RESTRICTION: No literal brand colors in feature code. Do not change visual values without a documented
design decision and approval.
BEHAVIOR: Report token gaps, wait for approval before any value change, then verify contrast and
prefers-reduced-motion behavior.
```

### 3.3 Primitive showcase - done

```text
SCOPE: Add a development-only design-system showcase route rendering button, field, badge, card,
dialog, progress, and quick-action primitives in every supported state.
CONSTRAINT: creatorpantry-design-system and new-component skills.
RESTRICTION: Use public library exports only. Do not build feature screens or page-specific variants.
BEHAVIOR: Plan the state matrix, wait for approval, implement, and add component/a11y tests.
```

### 3.4 Workflow-recipe gap inventory - done

```text
SCOPE: Compare the approved design-system package with the requirements and write a gap inventory for
table/list shell, tabs, toolbar, empty state, uploader shell, status pill, diff legend, and live status
region. Implement nothing.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system skill.
RESTRICTION: Do not infer that every named item requires a new component; identify reuse/composition
first. No CSS or TypeScript edits.
BEHAVIOR: Inspect public exports and states, report each gap with consumers and priority, and wait.
```

### 3.4a Table/list shell recipe - done

```text
SCOPE: Add only the reusable table/list shell approved in 3.4, including responsive presentation,
caption/heading association, loading, empty, error, selected, and pagination slots.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system and new-component skills.
RESTRICTION: No recipe-specific columns, data fetching, arbitrary style input, or other workflow recipe.
BEHAVIOR: Show public API/state matrix, wait for approval, implement with component/a11y tests, report.
```

### 3.4b Tabs recipe - done

```text
SCOPE: Add only the reusable tabs recipe approved in 3.4 with keyboard navigation, selected/disabled
states, focus management, and lazy-panel support.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system and new-component skills.
RESTRICTION: No toolbar, page routing, or feature-specific labels. Follow the ARIA tabs pattern.
BEHAVIOR: Show API/keyboard model, wait for approval, implement with component/a11y tests, report.
```

### 3.4c Toolbar recipe - done

```text
SCOPE: Add only the reusable responsive toolbar recipe approved in 3.4 with labelled action, filter,
search, overflow, loading, and disabled slots.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system and new-component skills.
RESTRICTION: No data service, recipe-specific action, or arbitrary CSS escape hatch.
BEHAVIOR: Show composition API, wait for approval, implement with responsive/keyboard tests, report.
```

### 3.4d Empty-state recipe - done

```text
SCOPE: Add only the reusable empty-state recipe approved in 3.4 with title, description, optional
illustration/icon, primary/secondary action, and no-results versus first-use variants.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system and new-component skills.
RESTRICTION: No hardcoded feature copy or food imagery requirement. Action order must remain accessible.
BEHAVIOR: Show variants, wait for approval, implement with component/a11y tests, report.
```

### 3.4e Uploader-shell recipe - done

```text
SCOPE: Add only the reusable uploader shell approved in 3.4 with browse/drop affordances, progress,
validation, retry, cancel, remove, and accessible status.
CONSTRAINT: .claude/rules/design-system.md and media.md; creatorpantry-design-system and new-component.
RESTRICTION: No actual upload transport, MIME policy, or feature-specific metadata. Drag/drop must have a
keyboard-equivalent browse action.
BEHAVIOR: Show state machine, wait for approval, implement with component/a11y tests, report.
```

### 3.4f Status-pill recipe - done

```text
SCOPE: Add only the semantic status-pill recipe approved in 3.4 with neutral, progress, success, warning,
error, and stale variants plus icon/text support.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system and new-component skills.
RESTRICTION: Meaning cannot rely on color. Do not encode recipe, AI, or publishing state machines here.
BEHAVIOR: Show semantic mapping, wait for approval, implement with contrast/a11y tests, report.
```

### 3.4g Diff-legend recipe - done

```text
SCOPE: Add only the accessible diff legend/presentation recipe approved in 3.4 for added, removed,
changed, moved, unchanged, warning, and selected changes.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system and new-component skills.
RESTRICTION: Presentation only; it does not calculate diffs or accept proposals. No color-only meaning.
BEHAVIOR: Show visual/text semantics, wait for approval, implement with component/a11y tests, report.
```

### 3.4h Live status/toast recipe - done

```text
SCOPE: Add only the status/toast region approved in 3.4 with polite/assertive announcement policy,
deduplication, dismissal, timeout pause, persistent error, and reduced-motion behavior.
CONSTRAINT: .claude/rules/design-system.md; creatorpantry-design-system and new-component skills.
RESTRICTION: No global business-event bus or feature-specific copy. Critical errors cannot disappear
before they are perceivable.
BEHAVIOR: Show announcement/timing policy, wait for approval, implement with timer/a11y tests, report.
```

### 3.5 Runtime configuration and typed API base - done

```text
SCOPE: Add an Angular runtime-configuration service that loads the Web host's gateway URL before app
bootstrap and exposes it to typed API services.
CONSTRAINT: .claude/rules/frontend.md and gateway.md.
RESTRICTION: No environment-specific URL baked into the bundle. Feature components never read runtime
configuration directly.
BEHAVIOR: Implement with success, missing-config, malformed-config, and degraded-start tests.
```

### 3.6 BFF authentication client and guards - done

```text
SCOPE: Add typed auth/me services, credentials-aware BFF requests, route guards, sign-in/sign-out
actions, and antiforgery support for unsafe methods.
CONSTRAINT: .claude/rules/frontend.md, auth.md, and gateway.md.
RESTRICTION: Angular never reads, stores, parses, or displays access/refresh tokens. Components do not
inject HttpClient.
BEHAVIOR: Plan the session state machine, wait for approval, implement, and test authenticated,
anonymous, expired, refresh-failed, and logout states.
```

### 3.7 Application shell and workspace switcher - done

```text
SCOPE: Build the responsive application shell with navigation to Dashboard, Workflows, My Day, My Week,
Recipes, Brand, AI Recipe Studio, Image Studio, Social Studio, DAM, Content Board, and Prompt Library plus
a membership-aware workspace switcher.
CONSTRAINT: APP-UI-001 through APP-UI-004; creatorpantry-design-system and new-component skills.
RESTRICTION: Switching changes the route context; it does not send WorkspaceId in a header or body.
Hide is not authorize—server policies remain authoritative.
BEHAVIOR: Plan responsive and keyboard behavior, wait for approval, implement with loading/empty/error
states and component tests.
```

### 3.8 Frontend foundation verification - done

```text
SCOPE: Audit the shell, showcase, theme, auth states, and workspace switcher for Angular conventions,
design-system use, responsive behavior, and WCAG 2.2 AA.
CONSTRAINT: .claude/rules/frontend.md and design-system.md.
RESTRICTION: Report first; no silent redesign. Fix only approved blockers and regressions.
UTILIZATION: design-review, api-contract-checker, and test-gap-analyzer.
BEHAVIOR: Run `ng test`, `ng build`, and the read-only reviewers; return a deduplicated report, wait for
approval, fix blockers, and rerun.
```

## Phase 4 — Platform reference domain

> Shared ingredient and measurement facts are global. Creator wording and recipe decisions are private workspace data. Keep those zones distinct.

### 4.1 Measurement unit model

```text
SCOPE: Add global MeasurementUnit and UnitAlias entities with stable code, display names, dimension
(mass, volume, count, temperature, or qualitative), system, base-unit factor where valid, precision,
and active state. Generate the migration.
CONSTRAINT: .claude/rules/recipes.md and tenancy.md.
RESTRICTION: Global entities have no WorkspaceId. A factor cannot bridge dimensions. Do not add density
or ingredient data yet.
BEHAVIOR: Show value/domain choices and indexes, wait for approval, implement, review/apply the
migration, and test uniqueness and dimension rules.
```

### 4.2 Ingredient reference model

```text
SCOPE: Add global Ingredient and IngredientAlias entities with canonical name, search text, category,
default count noun, and active state. Generate the migration.
CONSTRAINT: .claude/rules/recipes.md and tenancy.md.
RESTRICTION: This reference enriches creator input; it never replaces RecipeIngredient. No nutrition,
allergen, density, or embedding columns yet.
BEHAVIOR: Show natural keys and normalization rules, wait for approval, implement, migrate, and test
case/alias uniqueness.
```

### 4.3 Density reference model

```text
SCOPE: Add IngredientDensityReference linking one ingredient to a source, temperature/condition note,
mass, volume, precision, effective date, and review status.
CONSTRAINT: CALC-004 and DATA-RQ-007; .claude/rules/recipes.md.
RESTRICTION: No universal cup-to-gram conversion. A density without ingredient and provenance is
invalid. Do not calculate recipe conversions yet.
BEHAVIOR: Plan provenance and uniqueness, wait for approval, implement with migration and validation
tests.
```

### 4.4 Dietary and allergen vocabularies

```text
SCOPE: Add global DietaryProfile, Allergen, IngredientDietaryTrait, and IngredientAllergenTrait with
evidence source, confidence/review state, notes, and effective date.
CONSTRAINT: ING-007, DATA-RQ-007; .claude/rules/recipes.md and ai.md.
RESTRICTION: Missing trait data means unknown, never safe. No certified-safe boolean. Do not infer
cross-contact or medical suitability.
BEHAVIOR: Show the uncertainty model, wait for approval, implement, migrate, and test unknown versus
positive/negative evidence explicitly.
```

### 4.5 Cuisine, course, technique, equipment, and food category - done

```text
SCOPE: Add the remaining global controlled vocabularies required by recipe metadata and filtering,
each with stable key, display name, aliases where needed, and active state.
CONSTRAINT: requirements sections 8 and 17; .claude/rules/recipes.md.
RESTRICTION: Do not put creator-specific tags or custom equipment into the global catalogue. No UI or
search endpoint yet.
BEHAVIOR: Present the smallest schema that covers current requirements, wait for approval, implement,
migrate, and test stable keys.
```

### 4.6 Reference seed set - done - done

```text
SCOPE: Add deterministic development/test seed data for units, aliases, a compact ingredient set,
densities with citations, dietary profiles, allergens, cuisines, courses, techniques, equipment, and
categories.
CONSTRAINT: .claude/rules/recipes.md and tenancy.md.
RESTRICTION: Seed data must be clearly marked as development/test unless licensing permits production.
Do not invent nutrition or safety claims. Repeated seeding is idempotent.
BEHAVIOR: Show seed inventory and provenance, wait for approval, implement, and prove two runs produce
the same records.
```

### 4.7 Reference repository and read seam - done - done

```text
SCOPE: Implement paged/searchable reads for ingredients, units, cuisines, techniques, equipment, and
tags through ReferenceController → IReferenceFacade → IReferenceBusiness → IReferenceDataLayer →
purpose-built repositories.
CONSTRAINT: add-endpoint skill; .claude/rules/backend.md and tenancy.md.
RESTRICTION: Tenant-less, read-only, and global. Cache under global keys only. No generic repository or
EF entities in HTTP responses.
UTILIZATION: add-endpoint.
BEHAVIOR: Plan one consistent contract, wait for approval, implement, and test cache hit/miss,
pagination clamp, filtering, and cancellation.
```

### 4.8 Reference-domain audit - done - done

```text
SCOPE: Review all Phase 4 tables, migrations, APIs, and cache keys against the global/private data-zone
rules and recipe calculation prerequisites.
CONSTRAINT: .claude/rules/tenancy.md and recipes.md.
RESTRICTION: Report first. Do not turn global data into workspace copies to simplify tests.
UTILIZATION: architecture-reviewer, workspace-isolation-auditor, and test-gap-analyzer.
BEHAVIOR: Produce a finding list with file/line evidence, wait for approval, fix blockers, rerun tests,
and confirm pending model changes are clean.
```

## Phase 4A — Module-first domain restructure

> Structural only. This phase moves files and namespaces. It changes no behavior, no schema, no HTTP contract, and no layer seam. Run it before Phase 5 so the reference vertical every later feature copies is built in its final layout.

### 4A.1 Module boundary decision record - see docs/architecture-decisions

```text
SCOPE: Record the bounded contexts that become top-level modules inside CreatorPantry.Domain, the
internal shape every module repeats, and the shared kernel that stays at the project root, as an ADR in
docs/architecture-decisions.
CONSTRAINT: CLAUDE.md "Initial solution layout" and mandatory request seam; .claude/rules/backend.md; the
add-endpoint skill, which already places response models under Domain managers/models.
RESTRICTION: One assembly, one DbContext, one migration history. No new projects, no separate services,
no second DbContext, and no change to Controller → Facade → Business → DataLayer → Repository | Gateway.
Do not rename or re-shape any type in this prompt.
UTILIZATION: architecture-reviewer.
BEHAVIOR: Propose the module list with the entities, facades, and EF configurations each one owns. Fix the
repeated internal shape explicitly: Facade, Business, Data (repositories and configurations), Managers —
the module's ViewModels, ServiceModels, domain models, validators, and mapping — and the module's own
ServiceCollectionExtensions composition root. State the rule for cross-module calls and what a module may
consume from another module's Managers. Wait for approval, then write the ADR only. No file moves and no
code changes in this prompt.
```

### 4A.2 Shared kernel consolidation

```text
SCOPE: Gather genuinely cross-cutting code — caching, clock/time zone, validation primitives, result
types, idempotency, outbox, and audit — under an explicit shared location, and confirm nothing
context-specific is hiding there.
CONSTRAINT: the approved ADR from 4A.1; .claude/rules/backend.md.
RESTRICTION: Shared code may not reference any module. Shared managers hold only primitives every module
depends on, such as result and error types; a model, validator, or mapper used by exactly one context is
not shared and moves with that context. Do not create a "Common" bucket for things nobody classified.
UTILIZATION: architecture-reviewer.
BEHAVIOR: Classify every type currently under Models and Validation as shared primitive or module-owned
manager, with its actual referencing contexts as evidence. Wait for approval, move only the approved
shared set, update namespaces, and prove `dotnet build` and `dotnet test` are unchanged.
```

### 4A.3 Pilot module move — Tenancy

```text
SCOPE: Move the Tenancy context into a single module folder as the pattern every later move copies:
facade, business, data layer, repositories, EF configurations, its Managers area, and its
ServiceCollectionExtensions.
CONSTRAINT: the approved ADR from 4A.1; .claude/rules/tenancy.md and backend.md.
RESTRICTION: Moves and namespace updates only. Dissolve Models/ViewModels, Models/ServiceModels,
Models/DomainModels, and Validation for this context into the module's Managers area — do not leave a
second home for the same kind of type. No renamed types, no changed signatures, no altered query filters,
no touched workspace resolution behavior, and no edits to the two-workspace harness assertions.
UTILIZATION: architecture-reviewer and workspace-isolation-auditor.
BEHAVIOR: Show the file-by-file move map with each type's old path and its Managers destination, wait for
approval, execute it, then prove `dotnet build` and the full `dotnet test` run are green and
`dotnet ef migrations has-pending-model-changes` reports none.
```

### 4A.4 Auth and identity module move

```text
SCOPE: Apply the 4A.3 pattern to the authentication and identity context, including its Managers area,
the account-message gateway, and its ServiceCollectionExtensions.
CONSTRAINT: the approved ADR from 4A.1; .claude/rules/auth.md and backend.md.
RESTRICTION: Do not move ASP.NET Core Identity store registration out of its current composition root and
do not change the gateway-signed internal token, its key handling, or any policy name. Identity's own
UserManager and RoleManager are framework types and are unrelated to the module Managers area; do not
conflate them. Moves only.
UTILIZATION: architecture-reviewer.
BEHAVIOR: Show the move map, wait for approval, execute it, and prove build, tests, and a working sign-in
path through the BFF are unchanged.
```

### 4A.5 Reference module move

```text
SCOPE: Apply the 4A.3 pattern to the Phase 4 platform reference context — units, ingredients, aliases,
densities, dietary and allergen vocabularies, categories, sources, their EF configurations, their
Managers area, and their seed data.
CONSTRAINT: the approved ADR from 4A.1; .claude/rules/tenancy.md data zones.
RESTRICTION: Reference data stays global and tenant-less. Moving it must not introduce WorkspaceId, a
query filter, or a workspace-scoped cache key. Global cache keys keep their exact current strings.
UTILIZATION: architecture-reviewer and workspace-isolation-auditor.
BEHAVIOR: Show the move map, wait for approval, execute it, and prove build, tests, idempotent seed
execution, and `has-pending-model-changes` are all clean.
```

### 4A.6 Configuration, validator, and migration safety

```text
SCOPE: Confirm that after the moves the DbContext still discovers every IEntityTypeConfiguration, every
validator and manager type still resolves from DI through its module's ServiceCollectionExtensions, the
migrations output path is unchanged, and the model snapshot is byte-identical to before Phase 4A.
CONSTRAINT: CLAUDE.md migration command; .claude/rules/backend.md.
RESTRICTION: Do not add a migration to "fix" the move. A restructure that changes the model snapshot is a
defect in the restructure, not a schema change to accept. Do not paper over a dropped registration with a
new assembly-wide scan unless the ADR chose one.
UTILIZATION: architecture-reviewer.
BEHAVIOR: Report the discovery and registration mechanism for configurations, validators, and managers;
configuration and validator counts before and after; the snapshot diff; and `has-pending-model-changes`
output. Fix any drift by correcting the move, then confirm clean.
```

### 4A.7 Module boundary enforcement

```text
SCOPE: Add tests that fail when a module reaches past another module's facade — a business class touching
another module's repositories or data layer, a repository referenced across modules, a module's Managers
types consumed directly by another module, or a module referenced from the shared kernel.
CONSTRAINT: .claude/rules/backend.md layer ownership; src/BannedSymbols.txt for symbol-level bans.
RESTRICTION: Enforce structure, not naming fashion. Cross-module traffic goes facade to facade, and the
only manager type that crosses a module boundary is the ServiceModel a facade returns — never another
module's ViewModels, validators, or domain models. Do not grant exemptions to make an existing violation
pass; report it instead.
UTILIZATION: architecture-reviewer and test-gap-analyzer.
BEHAVIOR: Propose the rule set and how each is detected, wait for approval, implement the tests, prove
each rule fails on a deliberate violation, and list any existing violation as a finding.
```

### 4A.8 Toolkit realignment

```text
SCOPE: Make the module and Managers conventions explicit everywhere the toolkit describes the
architecture so generated code lands in the right place: the CLAUDE.md solution layout and request seam,
.claude/rules/backend.md, the architecture-reviewer report list, and every add-* skill.
CONSTRAINT: the approved ADR from 4A.1; existing rule and skill structure and tone.
RESTRICTION: Additive clarification only. Do not weaken, reword, or remove an architectural rule while
editing around it. The seam, the data zones, and the isolation requirements are unchanged.
UTILIZATION: skills-evals.
BEHAVIOR: Inventory with line evidence every place that describes domain structure, including the ones
that currently omit it — CLAUDE.md and backend.md never name the Managers area that add-endpoint already
assumes, and architecture-reviewer reports only vertical layer violations and so cannot see a
cross-module one. Wait for approval, update them, add the module-ownership and Managers rules, and re-run
the skills audit to confirm nothing still points at a structure that no longer exists.
```

### 4A.9 Restructure audit

```text
SCOPE: Review the complete Phase 4A diff and confirm it is a pure restructure: no behavior change, no
schema change, no contract change, and no weakened isolation.
CONSTRAINT: .claude/rules/backend.md, tenancy.md, and auth.md.
RESTRICTION: Report first. Do not bundle a behavior fix, a rename, or a "while we're here" improvement
into the restructure commit. Anything found becomes its own prompt.
UTILIZATION: architecture-reviewer, workspace-isolation-auditor, api-contract-checker, and
test-gap-analyzer.
BEHAVIOR: Produce a finding list with file/line evidence; confirm the diff is moves and namespaces only,
that every module repeats the same internal shape, and that no Models or Validation tree survives outside
the shared kernel; wait for approval, fix blockers, rerun the full test suite, and confirm the model
snapshot is clean.
```

## Phase 5 — Canonical recipe: the reference vertical

> This is the vertical every later workspace feature will copy. Build and audit it layer by layer before adding lifecycle breadth.

### 5.1 Recipe aggregate entities

```text
SCOPE: Add workspace-owned Recipe plus RecipeIngredientGroup, RecipeIngredient, RecipeInstructionGroup,
RecipeInstructionStep, RecipeEquipment, and RecipeAssetLink entities covering REC-001/REC-003 and the
ingredient model in section 10.1. Include stable child IDs and explicit ordering.
CONSTRAINT: .claude/rules/recipes.md and tenancy.md; add-recipe-feature and add-workspace-entity skills.
RESTRICTION: Preserve original ingredient and instruction text. Normalized reference IDs are nullable
and additive. No AI, version snapshots, test runs, or publication fields yet.
UTILIZATION: add-recipe-feature and add-workspace-entity.
BEHAVIOR: Show aggregate boundary, ownership, delete behavior, and indexes; wait for approval; implement
entities/configuration only.
```

### 5.2 Recipe version snapshot model

```text
SCOPE: Add immutable RecipeVersion with version number, source, actor, timestamp, reason, concurrency
lineage, optional proposal ID, readiness state, and a complete reconstructable snapshot of recipe
content.
CONSTRAINT: REC-004, REC-007 through REC-009, DATA-RQ-002; add-recipe-feature skill.
RESTRICTION: Historical snapshots are never updated or deleted by ordinary recipe edits. Do not store
only an opaque prose blob; the snapshot must support structured comparison and restore.
BEHAVIOR: Present snapshot strategy and storage tradeoffs, wait for approval, implement entities and
mapping without creating the migration yet.
```

### 5.3 Recipe schema migration

```text
SCOPE: Generate the migration for recipe aggregate and version tables with WorkspaceId-leading indexes,
unique `(WorkspaceId, RecipeId, VersionNumber)`, child-order constraints, and safe delete behavior.
CONSTRAINT: .claude/rules/backend.md, tenancy.md, and recipes.md.
RESTRICTION: Do NOT apply before inspection. Do not cascade-delete immutable versions or linked DAM
assets. No hand edits to generated code.
BEHAVIOR: Show generated migration and rollback story, wait for approval, apply through MigrationService,
and confirm no pending model changes.
```

### 5.4 Recipe create ViewModel and validation

```text
SCOPE: Define versioned HTTP ViewModels and FluentValidation for REC-001: working title plus optional
description, attribution, cuisine/course/method, timing, yield, tags, status, and notes.
CONSTRAINT: .claude/rules/api-contract.md and recipes.md; add-endpoint skill.
RESTRICTION: No WorkspaceId, OwnerId, version number, audit field, EF entity, or provider field in the
request. Do not persist anything.
BEHAVIOR: Show the boundary contract and validation matrix, wait for approval, implement with validator
tests.
```

### 5.5 Recipe repository create/detail

```text
SCOPE: Implement IRecipeRepository methods to add an aggregate and retrieve one complete aggregate by
ID with current children and selected current-version metadata.
CONSTRAINT: .claude/rules/backend.md and tenancy.md; add-recipe-feature skill.
RESTRICTION: EF access only. No domain decisions, HTTP models, cache, or IgnoreQueryFilters. The global
filter must enforce workspace scope.
BEHAVIOR: Plan query shape and includes/projection, wait for approval, implement with real SQL integration
tests in both workspaces.
```

### 5.6 Recipe DataLayer create transaction

```text
SCOPE: Implement IRecipeDataLayer.CreateAsync to persist Recipe, children, and version 1 atomically,
returning the created aggregate/version identity.
CONSTRAINT: .claude/rules/backend.md; REC-001 and DATA-RQ-005.
RESTRICTION: DataLayer owns the transaction but not title/yield/content rules. No cache or HTTP logic.
Failure must leave neither recipe nor version behind.
BEHAVIOR: Show transaction boundary, wait for approval, implement, and test rollback after an injected
version-write failure.
```

### 5.7 Recipe Business create mapping and invariants

```text
SCOPE: Implement IRecipeBusiness.CreateAsync to apply creation invariants, assign owner from authenticated
context, map creator input to aggregate, preserve entered text, and define version-1 reason/source.
CONSTRAINT: .claude/rules/recipes.md and backend.md.
RESTRICTION: Business calls only DataLayer. No DbContext, cache, HTTP, WorkspaceId assignment, or AI.
BEHAVIOR: Plan invariant list, wait for approval, implement with mocked DataLayer tests for valid,
invalid-time, invalid-yield, and ordering cases.
```

### 5.8 Recipe Facade create

```text
SCOPE: Implement IRecipeFacade.CreateAsync with input validation, Contributor-or-higher authorization
coordination, idempotency-key handling, and call to Business.
CONSTRAINT: .claude/rules/backend.md, auth.md, and tenancy.md; add-endpoint skill.
RESTRICTION: No repository or DbContext access. An idempotent replay returns the original outcome and
does not create version 2.
BEHAVIOR: Implement tests for validation failure, insufficient role, first success, replay, and key reuse
with a different payload.
```

### 5.9 Recipe create endpoint

```text
SCOPE: Add POST /api/v1/workspaces/{workspaceSlug}/recipes calling only IRecipeFacade and returning 201
with the stable recipe location and ServiceModel.
CONSTRAINT: REC-001; add-endpoint skill; .claude/rules/api-contract.md.
RESTRICTION: Controller contains no mapping beyond HTTP response selection. Do not accept ownership,
workspace, version, or audit fields from the client.
UTILIZATION: add-endpoint.
BEHAVIOR: Implement endpoint tests through Gateway for Owner, Contributor, Viewer, replay, invalid body,
and Workspace B isolation.
```

### 5.10 Recipe detail read seam

```text
SCOPE: Complete GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId} through Facade → Business →
DataLayer → Repository, returning the structured RecipeDetailServiceModel required by REC-003.
CONSTRAINT: REC-003; add-endpoint and add-recipe-feature skills.
RESTRICTION: Not found and cross-workspace are indistinguishable. Do not return EF entities, hidden
provider metadata, or raw blob URLs.
BEHAVIOR: Plan the ServiceModel, wait for approval, implement per-layer tests plus a two-workspace HTTP
test.
```

### 5.11 Recipe update seam

```text
SCOPE: Add PATCH /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId} across all layers for REC-004,
using submitted-field semantics, an expected concurrency token, and one atomic new version.
CONSTRAINT: REC-004, DATA-RQ-002/005; add-endpoint and add-recipe-feature skills.
RESTRICTION: No update-in-place of historical versions. No silent last-write-wins. Unsubmitted fields
remain unchanged; explicit clears use an unambiguous contract.
BEHAVIOR: Plan patch semantics and conflict response, wait for approval, implement, and test partial
update, explicit clear, no-op, conflict, rollback, and workspace isolation.
```

### 5.12 Recipe typed API service

```text
SCOPE: Add Angular recipe request/response models and a typed API service for the create, detail, and
update endpoints built in Phase 5.
CONSTRAINT: .claude/rules/frontend.md and api-contract.md.
RESTRICTION: No component, route, HttpClient injection outside the service, or duplicated domain rule.
Models must mirror supported ServiceModels exactly.
UTILIZATION: api-contract-checker.
BEHAVIOR: Show model/endpoint map, wait for approval, implement with HTTP contract tests, review, report.
```

### 5.12a Recipe library route shell

```text
SCOPE: Build only the recipe-library route shell with first-use empty state, create action, loading,
error/retry, and temporary display of recipes supplied by a local test fixture until Phase 6 search.
CONSTRAINT: RECIPE-UI-001/003; creatorpantry-design-system and new-component skills.
RESTRICTION: No server search/filter/paging, editor, history, duplicate, archive, or direct HttpClient.
BEHAVIOR: Plan route/state/components, wait for approval, implement with component/a11y tests, report.
```

### 5.12b Recipe editor shell

```text
SCOPE: Build only the recipe-editor route for create/detail/update using the typed service: sections for
metadata, ingredients, instructions, notes, timing/yield, media placeholder, and history placeholder.
CONSTRAINT: EDITOR-UI-001 through 004; creatorpantry-design-system, new-component, add-recipe-feature.
RESTRICTION: No AI, calculation, history, duplicate, archive, test-kitchen, media action, or HttpClient in
components.
BEHAVIOR: Show form/state ownership, wait for approval, implement loading/error/save/success/conflict
states with component/a11y tests, report.
```

### 5.12c Unsaved-change protection

```text
SCOPE: Add unsaved-change detection and route/browser-leave confirmation to the recipe editor, including
recovery after a recoverable save failure.
CONSTRAINT: UX-005 and EDITOR-UI-003; frontend and design-system rules.
RESTRICTION: No autosave or local persistence in this prompt. A successful save clears dirty state only
after the server response is applied.
BEHAVIOR: Show dirty-state transitions, wait for approval, implement with navigation/save-failure tests.
```

### 5.12d Phase 5 UI audit

```text
SCOPE: Audit only the Phase 5 recipe typed service, library shell, editor shell, and unsaved-change flow.
CONSTRAINT: RECIPE-UI and EDITOR-UI requirements implemented so far.
RESTRICTION: Report before fixes; no new feature scope.
UTILIZATION: design-review, api-contract-checker, and test-gap-analyzer.
BEHAVIOR: Return deduplicated findings, wait for approval, fix blockers, rerun `ng test`/`ng build`.
```

## Phase 6 — Recipe lifecycle, search, and history

### 6.1 Recipe search repository

```text
SCOPE: Add a purpose-built paged Recipe search query supporting text, status, tag, cuisine, course,
dietary-review state, readiness, creator, and date filters with deterministic sorting.
CONSTRAINT: REC-002 and PERF-001; .claude/rules/backend.md and tenancy.md.
RESTRICTION: Project summaries in SQL; do not load aggregates or binary assets. Page size is server
clamped. Every index begins with WorkspaceId where applicable.
BEHAVIOR: Show query/index plan, wait for approval, implement with real SQL tests for combined filters,
sorting, clamping, and workspace isolation.
```

### 6.2 Recipe list endpoint

```text
SCOPE: Expose REC-002 through the full layer stack and GET
/api/v1/workspaces/{workspaceSlug}/recipes using a typed filter contract and paged ServiceModel.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: No client-supplied WorkspaceId, arbitrary sort column, or unbounded result. Cache only if
invalidation is explicit and workspace-scoped.
BEHAVIOR: Plan contract, wait for approval, implement per-layer tests and a two-workspace endpoint test.
```

### 6.3 Recipe library search UI

```text
SCOPE: Connect the recipe-library screen to server paging, filters, sort, URL state, lifecycle badges,
latest version, last-modified data, and clear-empty behavior.
CONSTRAINT: RECIPE-UI-001/002; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not re-filter a partial server page locally. No infinite list without accessible paging.
BEHAVIOR: Implement with debounced typed service calls, cancellation, keyboard behavior, and tests for
loading, no results, retry, filter reset, and URL restoration.
```

### 6.4 Version history endpoint

```text
SCOPE: Implement REC-007 and GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions through
the full seam, returning metadata summaries newest first.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Do not return every snapshot in the list. Historical records are immutable. Cross-workspace
and unknown recipe return the same result.
BEHAVIOR: Plan ServiceModel, wait for approval, implement and test pagination, lineage, proposal link,
and isolation.
```

### 6.5 Version comparison service

```text
SCOPE: Implement deterministic section-aware comparison of two recipe snapshots for metadata,
ingredients, instructions, timing, yield, notes, and publication fields.
CONSTRAINT: REC-008 and AIREC-GR-002; add-recipe-feature skill.
RESTRICTION: No AI-generated diff. Match stable child IDs first and represent moves separately from
text replacement. Comparison writes nothing.
BEHAVIOR: Show diff algebra and examples, wait for approval, implement with unit tests for add, remove,
replace, reorder, group move, and no change.
```

### 6.6 Version comparison endpoint and UI

```text
SCOPE: Add GET .../versions/compare?from=&to= and a recipe-history comparison panel using the shared
diff legend and accessible non-color indicators.
CONSTRAINT: REC-008 and AI-UI-003; add-endpoint, creatorpantry-design-system, and new-component skills.
RESTRICTION: Read-only. Do not expose snapshots from another recipe/workspace. UI does not compute a
second competing diff.
BEHAVIOR: Implement endpoint and UI tests, run api-contract-checker and design-review, and report.
```

### 6.7 Restore historical version

```text
SCOPE: Implement REC-009 as POST .../versions/{version}/restore with Editor policy, expected current
version, reason, and idempotency key. Copy the snapshot into one new current version.
CONSTRAINT: add-endpoint and add-recipe-feature skills; DATA-RQ-002/005.
RESTRICTION: Never delete or reactivate an old row as current. A stale expected version returns a
recoverable conflict. Replays cannot create extra versions.
BEHAVIOR: Plan transaction and lineage, wait for approval, implement, and test restore, stale conflict,
replay, rollback, and isolation.
```

### 6.8 Duplicate recipe

```text
SCOPE: Implement REC-005 as POST .../recipes/{recipeId}/duplicate selecting a source version and new
title; create an independent Draft with attribution/lineage and version 1.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Do not copy test runs, publishing state, proposal history, audit records, or asset ownership.
Asset links follow the approved duplicate policy explicitly.
BEHAVIOR: Plan included/excluded fields, wait for approval, implement, and test source-version selection,
idempotency, and isolation.
```

### 6.9 Archive and restore lifecycle

```text
SCOPE: Implement REC-006 archive/restore commands with Editor policy, optimistic concurrency, audit
metadata, and default-search exclusion.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Archive is not hard delete. Versions and links remain. Archived recipes cannot accept
ordinary edits, AI proposals, publishing, or new tests until restored.
BEHAVIOR: Implement command/endpoint tests for state transitions, repeated commands, forbidden actions,
search exclusion, and isolation.
```

### 6.10 Version-history UI

```text
SCOPE: Add only the version-history panel to recipe detail with paged metadata, source, actor, reason,
readiness, proposal link, and selection of two versions for comparison.
CONSTRAINT: REC-007 and RECIPE-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: No restore, duplicate, archive, or client-calculated diff.
BEHAVIOR: Plan states/keyboard behavior, wait for approval, implement with component/a11y tests, report.
```

### 6.10a Version-compare UI

```text
SCOPE: Add only the version-comparison view using the server diff and shared diff presentation.
CONSTRAINT: REC-008 and AI-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: Read-only. Do not calculate or modify diff in Angular. No restore action.
BEHAVIOR: Show layout for metadata/ingredients/steps/moves, wait for approval, implement and test.
```

### 6.10b Restore-version UI action

```text
SCOPE: Add only the restore action with selected version, current-version warning, reason, confirmation,
concurrency conflict recovery, and success navigation.
CONSTRAINT: REC-009 and RECIPE-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not imply the old row becomes current; copy must say a new version will be created.
BEHAVIOR: Plan confirmation/conflict states, wait for approval, implement with tests, report.
```

### 6.10c Duplicate-recipe UI action

```text
SCOPE: Add only the duplicate action with source-version choice, new title, included/excluded summary,
idempotent submission, and navigation to the new Draft.
CONSTRAINT: REC-005 and RECIPE-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not expose unsupported copy options or copy test/publishing state in client code.
BEHAVIOR: Show dialog/state contract, wait for approval, implement with tests, report.
```

### 6.10d Archive/restore UI action

```text
SCOPE: Add only archive and lifecycle-restore actions with permission-aware affordance, confirmation,
concurrency handling, archived-state banner, and clear effect on editing/search.
CONSTRAINT: REC-006 and RECIPE-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: Archive is not presented as deletion. Server authorization remains authoritative.
BEHAVIOR: Show states/copy, wait for approval, implement with component/contract tests, report.
```

### 6.10e Recipe vertical audit

```text
SCOPE: Audit the complete recipe vertical before later features copy it.
CONSTRAINT: REC-001 through REC-009 and implemented recipe UI requirements.
RESTRICTION: Report first; no new feature or redesign.
UTILIZATION: design-review, code-reviewer, api-contract-checker, workspace-isolation-auditor, skills-evals,
and test-gap-analyzer.
BEHAVIOR: Return one deduplicated report, wait for approval, fix blockers, rerun full recipe suites.
```

## Phase 7 — Ingredient parsing and deterministic calculations

### 7.1 Quantity value objects

```text
SCOPE: Add domain value objects for exact decimal/rational quantity, range, qualitative quantity, and
display precision plus JSON persistence/contract converters.
CONSTRAINT: ING-001 through ING-006 and CALC-001; .claude/rules/recipes.md.
RESTRICTION: No binary floating-point arithmetic. Preserve null/qualitative meaning. Do not parse text
or convert units in this prompt.
BEHAVIOR: Show representation and serialization examples, wait for approval, implement, and property-test
round trips, simplification, extremes, and invalid denominators.
```

### 7.2 Ingredient-line tokenizer

```text
SCOPE: Implement a deterministic tokenizer that segments free-form ingredient lines into candidate
quantity/range, unit text, ingredient text, preparation text, optionality, and group marker without
performing catalogue matching.
CONSTRAINT: ING-001; add-recipe-feature skill.
RESTRICTION: Preserve the original line exactly. Never invent a quantity or silently choose among
ambiguous parses. No AI or database access.
BEHAVIOR: Present grammar and ambiguity examples, wait for approval, implement with a checked-in fixture
set covering fractions, mixed numbers, ranges, package sizes, Unicode fractions, and qualitative lines.
```

### 7.3 Ingredient reference matcher

```text
SCOPE: Match tokenizer ingredient/unit candidates to global references using exact/alias normalization,
returning ranked candidates, confidence, and unresolved ambiguity.
CONSTRAINT: ING-001 and AIREC-GR-003; recipes and tenancy rules.
RESTRICTION: Low confidence remains unresolved. Do not overwrite display text. Global reference cache
uses global keys, never workspace keys.
BEHAVIOR: Plan thresholds and normalization, wait for approval, implement with ambiguity, alias,
punctuation, plural, and no-match tests.
```

### 7.4 Ingredient parse endpoint

```text
SCOPE: Expose ING-001 as POST /api/v1/workspaces/{workspaceSlug}/ingredient-tools/parse through the
full request seam, combining deterministic tokenization and matching into a proposal response.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: The operation is read-only and does not modify a recipe. Clamp line count/length. Return
confidence and ambiguity; do not hide unresolved values.
BEHAVIOR: Plan contract, wait for approval, implement with endpoint tests for valid, ambiguous, abusive,
cancelled, and cross-workspace contexts.
```

### 7.5 Structured ingredient editor

```text
SCOPE: Add recipe-editor patterns for paste/parse review, ingredient groups, add/edit/remove/reorder,
original text, structured fields, normalized match, ambiguity confirmation, optionality, and garnish.
CONSTRAINT: ING-002 and EDITOR-UI-002/004; creatorpantry-design-system, new-component, and
add-recipe-feature skills.
RESTRICTION: Parsed values remain a proposal until the user confirms and saves. Keyboard reordering is
required. Do not use color alone for confidence.
BEHAVIOR: Plan interaction/state model, wait for approval, implement with component/a11y tests and a
contract test against the parse response.
```

### 7.6 Scaling domain service

```text
SCOPE: Implement deterministic recipe scaling by multiplier or target servings from canonical
quantities, producing a preview with rounded display, warnings, and unscalable lines.
CONSTRAINT: ING-003, CALC-001/002/005/006; add-recipe-feature skill.
RESTRICTION: Do not mutate or persist a recipe. Cooking time, vessel size, leavening, fermentation,
spices, and thickeners do not scale silently as linear facts.
BEHAVIOR: Show rules and warning taxonomy, wait for approval, implement with property tests and fixtures
for qualitative, discrete, range, package, and extreme factors.
```

### 7.7 Unit conversion domain service

```text
SCOPE: Implement compatible same-dimension unit conversion plus ingredient-specific mass-volume
conversion using approved density references. Return formula, source, precision, and rounding.
CONSTRAINT: ING-004, CALC-003/004/006; add-recipe-feature skill.
RESTRICTION: Block cross-dimension conversion without density. Temperature is a separate operation.
Always calculate from canonical values, never previously rounded display values.
BEHAVIOR: Present conversion matrix, wait for approval, implement with round-trip, incompatible,
missing-density, source, and precision tests.
```

### 7.8 Temperature conversion service

```text
SCOPE: Add deterministic Celsius/Fahrenheit conversion for explicit recipe temperatures while
preserving source value, display precision, oven-mode context, and safety-note linkage.
CONSTRAINT: CALC-003 and .claude/rules/recipes.md.
RESTRICTION: Do not convert vague heat descriptions or infer doneness/safety. No AI.
BEHAVIOR: Implement exact formula tests, conventional display rounding tests, and unknown-context
preservation tests.
```

### 7.9 Yield and portion reconciliation

```text
SCOPE: Implement ING-005 to reconcile batch yield, serving count, serving size, and pan/vessel
assumptions into a preview with explicit inputs, formula, and confirmation needs.
CONSTRAINT: ING-005 and CALC-002/005; add-recipe-feature skill.
RESTRICTION: Do not invent missing pan geometry, density, trim loss, or serving definition. No recipe
mutation.
BEHAVIOR: Show calculation cases, wait for approval, implement with complete, partial, contradictory,
and unsupported-input tests.
```

### 7.10 Display normalization service

```text
SCOPE: Implement ING-006 readable display normalization: decimal-to-fraction policy, fraction
simplification, unit selection, range preservation, and configurable rounding.
CONSTRAINT: ING-006 and CALC-001/006.
RESTRICTION: Normalization changes presentation only. Never compound prior rounding or replace canonical
quantity.
BEHAVIOR: Implement golden tests for common kitchen fractions, tiny/large values, ranges, count units,
and configured metric/US presentation.
```

### 7.11 Scale-preview endpoint

```text
SCOPE: Expose only recipe scaling as POST .../calculations/scale through the full request seam.
CONSTRAINT: ING-003; add-endpoint and add-recipe-feature skills.
RESTRICTION: Read-only; require source version; return inputs, warnings, formula/provenance. No persistence.
BEHAVIOR: Plan contract, wait for approval, implement validation/authorization/cancellation/isolation tests.
```

### 7.11a Unit-conversion preview endpoint

```text
SCOPE: Expose only compatible unit conversion as POST .../calculations/convert-units.
CONSTRAINT: ING-004; add-endpoint and add-recipe-feature skills.
RESTRICTION: Read-only. No temperature operation or cross-dimension conversion without approved density.
BEHAVIOR: Plan contract, wait for approval, implement source/provenance/error/isolation tests.
```

### 7.11b Temperature-conversion preview endpoint

```text
SCOPE: Expose only temperature conversion as POST .../calculations/convert-temperature.
CONSTRAINT: CALC-003; add-endpoint and add-recipe-feature skills.
RESTRICTION: Read-only. No vague heat interpretation, doneness inference, or recipe persistence.
BEHAVIOR: Plan contract, wait for approval, implement validation/context/isolation tests.
```

### 7.11c Yield-preview endpoint

```text
SCOPE: Expose only yield/portion reconciliation as POST .../calculations/recalculate-yield.
CONSTRAINT: ING-005; add-endpoint and add-recipe-feature skills.
RESTRICTION: Read-only. Unknown pan/serving assumptions remain unresolved and visible.
BEHAVIOR: Plan contract, wait for approval, implement complete/partial/conflict/isolation tests.
```

### 7.11d Display-normalization preview endpoint

```text
SCOPE: Expose only display normalization as POST .../calculations/normalize-display.
CONSTRAINT: ING-006; add-endpoint and add-recipe-feature skills.
RESTRICTION: Presentation only; canonical quantities and recipe version remain unchanged.
BEHAVIOR: Plan contract, wait for approval, implement rounding/range/system/isolation tests.
```

### 7.12 Scaling-preview UI

```text
SCOPE: Add only scaling preview to the recipe editor with factor/target-servings input, before/after
quantities, non-linear warnings, unscalable lines, and clear unsaved status.
CONSTRAINT: EDITOR-UI-005; creatorpantry-design-system and add-recipe-feature skills.
RESTRICTION: No other calculation UI or persistence.
BEHAVIOR: Show interaction states, wait for approval, implement with component/contract/a11y tests.
```

### 7.12a Unit and temperature preview UI

```text
SCOPE: Add only unit-system and temperature preview controls showing formula, density source, precision,
rounding, incompatible dimensions, and unresolved values.
CONSTRAINT: EDITOR-UI-005; creatorpantry-design-system and add-recipe-feature skills.
RESTRICTION: No yield/scaling UI or save operation. Never show unsupported conversion as successful.
BEHAVIOR: Plan states, wait for approval, implement with component/contract/a11y tests.
```

### 7.12b Yield and display-normalization UI

```text
SCOPE: Add only yield/portion and normalized-display previews with assumptions, formula, warnings, and
canonical-versus-display distinction.
CONSTRAINT: EDITOR-UI-005; creatorpantry-design-system and add-recipe-feature skills.
RESTRICTION: No direct mutation or duplicated arithmetic in TypeScript.
BEHAVIOR: Show state model, wait for approval, implement with component/contract/a11y tests.
```

### 7.12c Apply calculation proposal

```text
SCOPE: Add one explicit apply action that converts a selected calculation preview into a recipe diff and
saves it through the ordinary REC-004 update seam.
CONSTRAINT: EDITOR-UI-005 and REC-004; add-recipe-feature skill.
RESTRICTION: No bypass persistence. Require source-version match and confirmation; create one version.
BEHAVIOR: Plan handshake, wait for approval, implement replay/conflict/rejection/success tests.
```

### 7.12d Calculation audit

```text
SCOPE: Audit deterministic calculations, preview APIs, UI, and apply flow.
CONSTRAINT: ING-003 through ING-006 and CALC-001 through 006.
RESTRICTION: Report first; do not weaken arithmetic or warnings.
UTILIZATION: design-review, code-reviewer, test-gap-analyzer, workspace-isolation-auditor.
BEHAVIOR: Return findings, wait for approval, fix blockers, rerun property/integration/UI suites.
```

## Phase 8 — AI foundation and proposal lifecycle

> Build one safe proposal pipeline before individual AI features. Models propose; deterministic server code validates, diffs, authorizes, and writes.

### 8.1 AI provider resources and abstractions

```text
SCOPE: Add Aspire resources/configuration for separate chat and embedding deployments and register
provider-neutral IChatClient and IEmbeddingGenerator abstractions in API and Worker as needed.
CONSTRAINT: .claude/rules/ai.md and aspire.md; add-ai-capability and add-aspire-resource skills.
RESTRICTION: Domain/application code cannot name a provider SDK. Endpoints/deployments/keys come from
configuration or secrets. Do not add a feature prompt or model call yet.
UTILIZATION: add-ai-capability and add-aspire-resource.
BEHAVIOR: Verify current first-party APIs, show resource/DI plan, wait for approval, implement, and prove
the system starts with a configured fake/local client.
```

### 8.2 Versioned prompt-template loader

```text
SCOPE: Add a prompt-template store/loader for versioned files with declared template ID, semantic
version, required inputs, output schema version, safety class, and checksum.
CONSTRAINT: AI-001/002; .claude/rules/ai.md.
RESTRICTION: No large inline prompt strings in C#. Templates cannot access secrets or arbitrary files.
Loading a missing/invalid template fails before provider invocation.
BEHAVIOR: Show file convention and validation, wait for approval, implement with checksum, version,
missing-input, and invalid-manifest tests.
```

### 8.3 AI operation entity and state machine

```text
SCOPE: Add workspace-owned AiOperation with Requested, Running, Proposed, Accepted, PartiallyAccepted,
Rejected, Failed, and Expired states plus task type, recipe/version IDs, scope, idempotency key, actor,
timestamps, and failure category.
CONSTRAINT: requirements 9.1 and DATA-008; add-workspace-entity and add-ai-capability skills.
RESTRICTION: State transitions are explicit and append audit evidence. Accepted states cannot return to
Running. Do not store private prompt bodies by default.
BEHAVIOR: Show transition table and uniqueness indexes, wait for approval, implement entity/configuration
with exhaustive transition tests; no migration yet.
```

### 8.4 AI proposal and structured-change entities

```text
SCOPE: Add workspace-owned AiProposal, AiStructuredChange, AiWarning, AiExecutionMetadata, and
AiProposalFeedback linked to operation/source version with schema/template/provider/model provenance.
CONSTRAINT: DATA-008, AIREC-GR-001/007; add-workspace-entity and add-ai-capability skills.
RESTRICTION: A proposal never stores executable SQL, file paths, credentials, or unvalidated arbitrary
commands. Store only approved diagnostic content according to privacy policy.
BEHAVIOR: Plan record boundaries and retention, wait for approval, implement configuration and immutable
fields without migration.
```

### 8.5 AI proposal migration

```text
SCOPE: Generate the migration for AI operations, proposals, changes, warnings, execution metadata, and
feedback with WorkspaceId-leading indexes and idempotency/source-version constraints.
CONSTRAINT: .claude/rules/backend.md, tenancy.md, and ai.md.
RESTRICTION: Do NOT apply before review. No cascade from recipe deletion that destroys retained audit
records before policy permits it.
BEHAVIOR: Show migration and retention implications, wait for approval, apply through MigrationService,
and confirm pending model changes are clean.
```

### 8.6 Structured-output validator

```text
SCOPE: Add a versioned JSON-schema validation and deserialization boundary for model output, followed by
domain-level checks that return stable failure categories.
CONSTRAINT: AI-003, AIREC-GR-001; .claude/rules/ai.md.
RESTRICTION: Raw model JSON never reaches Business or persistence. Do not repair malformed output by
guessing; bounded retry may ask the provider for schema correction.
BEHAVIOR: Show validation stages, wait for approval, implement with valid, extra-field, wrong-version,
truncated, hostile-string, and domain-invalid fixtures.
```

### 8.7 Untrusted-context envelope

```text
SCOPE: Implement a reusable prompt-context builder that separates system policy, task template,
workspace/creator preferences, canonical recipe snapshot, retrieved references, and untrusted user or
imported text with explicit delimiters.
CONSTRAINT: AI-001/008 and AIREC-GR-006; .claude/rules/ai.md.
RESTRICTION: Retrieved text cannot redefine tool permissions, workspace, output schema, or system rules.
No cross-workspace examples or prompt bodies in logs.
BEHAVIOR: Show envelope structure and injection cases, wait for approval, implement with adversarial
tests proving untrusted instructions remain data.
```

### 8.8 AI execution telemetry and resilience

```text
SCOPE: Add a provider execution wrapper recording model/deployment, template version, latency, tokens,
estimated cost, safety outcome, correlation ID, retries, cancellation, and result status; apply bounded
timeout/retry/rate-limit/circuit-breaker policy.
CONSTRAINT: AI-004 through AI-007; .claude/rules/ai.md and external.md.
RESTRICTION: Do not log recipe content, prompt bodies, provider credentials, or generated private text.
Never retry nontransient policy/schema/domain failures blindly.
BEHAVIOR: Plan telemetry fields and error taxonomy, wait for approval, implement with fake-client tests
for success, transient retry, timeout, cancellation, rate limit, safety block, and malformed output.
```

### 8.9 AI operation repository and durable job claim

```text
SCOPE: Implement repository/DataLayer methods to create idempotent operations, claim Requested work,
lease Running work, store validated proposals atomically, fail/expire operations, and recover abandoned
leases.
CONSTRAINT: requirements 9.1, NFR-004/005; add-background-job and add-ai-capability skills.
RESTRICTION: Workers must re-resolve and validate workspace before invoking a facade. One idempotency key
and payload produce one operation. No provider call inside a database transaction.
UTILIZATION: add-background-job.
BEHAVIOR: Show state/lease transaction design, wait for approval, implement with competing-worker,
replay, lease-expiry, partial-failure, and workspace-isolation tests.
```

### 8.10 Generic AI proposal request/status endpoints

```text
SCOPE: Add POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/ai-proposals and GET
.../ai-proposals/{proposalId} through the full seam for an initially disabled/test task type, returning
operation status and validated proposal detail.
CONSTRAINT: API-004/005, AIREC lifecycle; add-endpoint, add-ai-capability, and add-recipe-feature skills.
RESTRICTION: Client selects only an allow-listed task discriminator and task-specific ViewModel. No
arbitrary prompt, tool list, workspace ID, model, template path, or provider parameter.
BEHAVIOR: Plan contract and status codes, wait for approval, implement with authorization,
idempotency, polling, cancellation, not-found, and isolation tests.
```

### 8.11 Server-calculated proposal diff

```text
SCOPE: Convert validated structured changes into a deterministic comparison against the exact source
RecipeVersion and store the server-calculated diff used for review.
CONSTRAINT: AIREC-GR-002 and REC-008; add-ai-capability and add-recipe-feature skills.
RESTRICTION: Never trust a model-provided before value or diff. If source content no longer matches the
pinned version, fail the operation; do not silently rebase.
BEHAVIOR: Show mapping rules, wait for approval, implement with forged-before, missing-field, reorder,
duplicate-change, and stale-source tests.
```

### 8.12 Proposal disposition and atomic acceptance

```text
SCOPE: Implement AIREC-007: accept all, accept selected, reject, and feedback through POST
.../ai-proposals/{proposalId}/disposition. Accepted changes pass ordinary recipe domain validation and
create exactly one new RecipeVersion atomically with proposal provenance.
CONSTRAINT: DATA-RQ-004/005, AIREC-GR-007; add-endpoint, add-ai-capability, and add-recipe-feature skills.
RESTRICTION: Explicit confirmation required. Stale source cannot be accepted. Invalid partial selections
fail without mutation. Replay is idempotent. AI cannot bypass readiness/safety/authorization rules.
BEHAVIOR: Plan selection and transaction contract, wait for approval, implement with accept-all,
partial, reject, stale, invalid, replay, rollback, and two-workspace tests; then run ai-safety-reviewer.
```

### 8.13 AI proposal review UI

```text
SCOPE: Build the AI proposal panel and operation-status UI with Requested/Running/Proposed/final states,
field-level diff, assumptions, warnings, provenance, accept all, accept selected, edit before accept,
reject, feedback, retry, and stale-source recovery.
CONSTRAINT: AI-UI-001 through AI-UI-006; creatorpantry-design-system, new-component, and
add-ai-capability skills.
RESTRICTION: Generated content never appears saved before acceptance. Add/remove/change cannot rely on
color alone. UI cannot override server selection validation or compute canonical diff.
BEHAVIOR: Plan state machine and keyboard interaction, wait for approval, implement with component,
contract, accessibility, refresh-resume, cancellation, and stale tests.
```

### 8.14 Proposal worker

```text
SCOPE: Add the Worker loop that claims AI operations, loads the exact authorized recipe/version and
task handler, builds safe context, invokes the provider wrapper, validates output, calculates diff,
and stores proposal/failure state.
CONSTRAINT: add-background-job and add-ai-capability skills; .claude/rules/ai.md and tenancy.md.
RESTRICTION: Worker never trusts WorkspaceId from payload without re-resolution. No DbContext in task
handlers or Semantic Kernel plugins. Cancellation and shutdown preserve accurate state.
BEHAVIOR: Show orchestration sequence, wait for approval, implement with fake provider and integration
tests for restart, duplicate delivery, cancellation, and workspace isolation.
```

### 8.15 AI foundation audit and evaluation harness

```text
SCOPE: Create a versioned AI evaluation harness and initial fixtures for schema validity, refusal/safety,
workspace isolation, prompt injection, stale source, deterministic-tool routing, and proposal acceptance.
Then audit the complete foundation.
CONSTRAINT: AI-012 and TEST-003/004/010; .claude/rules/ai.md.
RESTRICTION: Default CI uses fake/recorded deterministic responses, not paid live models. Reviewers
report before fixes.
UTILIZATION: ai-safety-reviewer, workspace-isolation-auditor, architecture-reviewer, test-gap-analyzer,
and skills-evals.
BEHAVIOR: Show fixture format and pass criteria, wait for approval, implement, run reviewers, fix
approved blockers, and publish an evaluation baseline report.
```

## Phase 9 — AI-assisted recipe capabilities

> Every capability plugs into Phase 8. Do not create one-off AI endpoints, persistence paths, or acceptance logic.

### 9.1 Recipe concept schema and template

```text
SCOPE: Define the versioned structured output schema and prompt template for AIREC-001 concepts using
audience, course, cuisine, dietary goals, available ingredients, exclusions, equipment, skill,
season, time budget, and creator style; return multiple distinct concepts with assumptions.
CONSTRAINT: AIREC-001; .claude/rules/ai.md and recipes.md; add-ai-capability skill.
RESTRICTION: No canonical recipe mutation, fabricated nutrition/safety claim, or arbitrary user prompt
outside declared fields.
BEHAVIOR: Show schema/template/evaluation cases, wait for approval, implement task handler and fake-client
tests, then run ai-safety-reviewer.
```

### 9.2 Concept request endpoint

```text
SCOPE: Add only the allow-listed AIREC-001 concept request contract/endpoint using the generic operation
lifecycle.
CONSTRAINT: add-endpoint and add-ai-capability skills.
RESTRICTION: No UI, recipe creation, provider choice, system prompt, or arbitrary task field.
BEHAVIOR: Plan contract, wait for approval, implement field/idempotency/status/isolation tests.
```

### 9.2a Concept Studio UI

```text
SCOPE: Build only the AI Recipe Studio concept form and result selection for AIREC-001.
CONSTRAINT: AI-UI-001/002/006; creatorpantry-design-system and new-component skills.
RESTRICTION: Selecting a concept does not create a recipe. No free-form system prompt field.
BEHAVIOR: Plan states, wait for approval, implement errors/cancellation/selection/a11y tests.
```

### 9.3 Structured first-draft schema and template

```text
SCOPE: Define and implement AIREC-002 task output for title, description, yield, timing, ingredient
groups, ordered instructions, equipment, notes, optional components, unresolved questions, and warnings.
CONSTRAINT: AIREC-002; add-ai-capability and add-recipe-feature skills.
RESTRICTION: Output is a proposal only. Quantities and units must deserialize to supported structured
types or remain explicitly unresolved. Never fill unknown safety-critical temperatures as facts.
BEHAVIOR: Show schema and failure fixtures, wait for approval, implement handler/evaluations, and run
ai-safety-reviewer.
```

### 9.4 First-draft request endpoint

```text
SCOPE: Add only the AIREC-002 request contract/endpoint from a selected concept or declared brief fields,
using the generic AI operation lifecycle.
CONSTRAINT: add-endpoint and add-ai-capability skills.
RESTRICTION: No recipe creation or UI. Unresolved values remain explicit.
BEHAVIOR: Plan contract, wait for approval, implement validation/idempotency/schema/isolation tests.
```

### 9.4a First-draft review UI

```text
SCOPE: Build only the structured first-draft proposal review/edit/reject UI using the shared proposal panel.
CONSTRAINT: AIREC-002 and AI-UI requirements; creatorpantry-design-system and new-component skills.
RESTRICTION: Review does not create a recipe. Unresolved required fields remain visible.
BEHAVIOR: Plan states, wait for approval, implement edit/reject/error/a11y tests.
```

### 9.4b Create recipe from accepted draft

```text
SCOPE: Implement only explicit acceptance of a first-draft proposal into a new Recipe/version 1 through
the established recipe creation rules.
CONSTRAINT: AIREC-002/007 and REC-001; add-ai-capability and add-recipe-feature skills.
RESTRICTION: Acceptance is the only create step. Replay creates no duplicate recipe; invalid unresolved
required fields fail atomically.
BEHAVIOR: Show special transaction, wait for approval, implement replay/edit/reject/rollback/isolation tests.
```

### 9.5 Scoped recipe revision

```text
SCOPE: Implement AIREC-003 task schema/template/handler and request UI for selected recipe sections and
goals, returning only scoped structured changes, rationale, assumptions, and warnings.
CONSTRAINT: AIREC-003 and AIREC-GR-004; add-ai-capability and add-recipe-feature skills.
RESTRICTION: Unselected sections must remain unchanged at the domain level. The server rejects a
proposal that touches fields outside declared scope.
BEHAVIOR: Show scope allow-list and enforcement, wait for approval, implement with out-of-scope attack,
no-op, multi-field, stale-version, and evaluation tests.
```

### 9.6 Ingredient substitution advisor

```text
SCOPE: Implement AIREC-004 for one selected ingredient, returning ranked alternatives, quantity
guidance, technique/flavor/texture impact, dietary/allergen consequences, confidence, evidence, and
test recommendation.
CONSTRAINT: AIREC-004 and AIREC-GR-005; add-ai-capability skill.
RESTRICTION: Culinary plausibility is distinct from dietary/allergen safety. Unknown evidence stays
unknown. No guaranteed equivalence or automatic replacement.
BEHAVIOR: Show output schema and high-risk cases, wait for approval, implement with safe/unknown/allergen/
no-alternative fixtures and ai-safety-reviewer.
```

### 9.7 Recipe adaptation task

```text
SCOPE: Implement AIREC-005 for one declared adaptation goal—dietary, equipment, yield, or skill level—
returning a complete cross-field proposal covering ingredients, method, timing, texture, safety, and
yield implications.
CONSTRAINT: AIREC-005; add-ai-capability and add-recipe-feature skills.
RESTRICTION: One goal per operation. Deterministic yield math routes through calculation services.
Impossible or unsafe goals produce an explicit limitation, not fabricated success.
BEHAVIOR: Plan goal-specific schemas or discriminator, wait for approval, implement with evaluation
fixtures for each goal and refusal/limitation cases.
```

### 9.8 Recipe quality and safety review

```text
SCOPE: Implement AIREC-006 producing field-linked findings for completeness, consistency, timing,
temperature, ambiguous steps, unused ingredients, likely failures, allergen/dietary conflicts, and
unsupported claims with severity and evidence.
CONSTRAINT: AIREC-006, AIREC-GR-005, AI-009/010; add-ai-capability skill.
RESTRICTION: Findings are review signals, not certifications. High-risk methods require approved
reference checks; unsupported claims must say what is unknown.
BEHAVIOR: Show severity/evidence schema, wait for approval, implement evaluation cases for ordinary,
raw-protein, canning, fermentation, allergen, medical-diet, and prompt-injection recipes.
```

### 9.9 Proposal explanation

```text
SCOPE: Implement AIREC-008 as a concise creator-facing explanation derived from an existing validated
proposal/diff, linking each explanation item to actual proposed fields and verification needs.
CONSTRAINT: AIREC-008; add-ai-capability skill.
RESTRICTION: Explanation cannot add new changes or claims. Prefer deterministic templating for factual
diff summary; use AI only for bounded prose if approved.
BEHAVIOR: Plan deterministic versus AI portions, wait for approval, implement with missing-link,
unsupported-claim, and no-change tests.
```

### 9.10 AI capabilities integration audit

```text
SCOPE: Verify all recipe AI tasks reuse one operation/proposal/disposition lifecycle, versioned prompts,
structured validation, server diffs, deterministic tools, workspace filters, and common UI patterns.
CONSTRAINT: AIREC-001 through AIREC-008 and AIREC-GR-001 through 008.
RESTRICTION: Report first. Do not fix by weakening schemas or merging task-specific contracts into an
arbitrary prompt endpoint.
UTILIZATION: ai-safety-reviewer, workspace-isolation-auditor, api-contract-checker, design-review,
test-gap-analyzer, and skills-evals.
BEHAVIOR: Produce one deduplicated severity report, wait for approval, fix blockers, rerun evaluation
sets and all recipe/AI tests, and record prompt-template versions.
```

## Phase 10 — Test kitchen and recipe readiness

### 10.1 Test-run entities

```text
SCOPE: Add workspace-owned RecipeTestRun, TestObservation, TestIssue, TestAttachmentLink, and
TestIssueResolution tied to an exact RecipeVersion with date, tester, environment, equipment, actual
yield/time, rating, issue severity, and audit fields.
CONSTRAINT: TESTRUN-001/002 and DATA-009; add-workspace-entity and add-recipe-feature skills.
RESTRICTION: A test run cannot float against “latest”; version is required. Attachments link authorized
assets rather than duplicating blobs. No readiness transition yet.
BEHAVIOR: Show aggregate/lifecycle/index design, wait for approval, implement configuration and migration,
review/apply it, and test constraints.
```

### 10.2 Create test-run seam

```text
SCOPE: Implement TESTRUN-001 through POST .../recipes/{recipeId}/test-runs across all layers with Editor
or approved Contributor policy and exact source-version validation.
CONSTRAINT: add-endpoint, add-workspace-entity, and add-recipe-feature skills.
RESTRICTION: No cross-workspace asset or version link. Actual observations never mutate the recipe.
Test media remains governed by DAM authorization.
BEHAVIOR: Plan contract, wait for approval, implement per-layer tests plus endpoint tests for valid,
missing version, invalid asset, role, idempotency, and isolation.
```

### 10.3 Update test run and resolve issue

```text
SCOPE: Implement TESTRUN-002 update and issue-resolution commands with optimistic concurrency, audit
fields, and optional link to the recipe version containing the correction.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Resolving an issue does not rewrite the test observation. The resolution version must
belong to the same recipe and workspace and must not predate the issue without explicit override.
BEHAVIOR: Implement update and resolution as separate atomic commands with tests for conflict, invalid
version, repeated resolution, and isolation.
```

### 10.4 Test history endpoint

```text
SCOPE: Implement TESTRUN-003 paged list with version, tester, outcome, date, and unresolved-issue filters
plus summary counts.
CONSTRAINT: add-endpoint skill; PERF-001.
RESTRICTION: Server paging only. Do not load attachment bytes or full recipe snapshots in the list.
BEHAVIOR: Plan projection/indexes, wait for approval, implement with combined-filter, paging, count, and
two-workspace tests.
```

### 10.5 Deterministic readiness evaluator

```text
SCOPE: Implement TESTRUN-004 as deterministic rules over required fields, unresolved ingredient
ambiguities, AI warnings, allergen review, optional nutrition mappings, test coverage, linked hero
media, and publication metadata; return blocking issues and recommendations.
CONSTRAINT: TESTRUN-004 and DATA-RQ-009; add-recipe-feature skill.
RESTRICTION: No model decides readiness. Rules are versioned/configurable and explain exactly which
record/field caused each result. Evaluation writes nothing.
BEHAVIOR: Show rule catalogue and release defaults, wait for approval, implement with one test per rule
plus combined-state and workspace-isolation tests.
```

### 10.6 Readiness transition state machine

```text
SCOPE: Implement TESTRUN-005 transitions Draft → InDevelopment → Testing → ReadyForReview → Approved,
plus Archive/restore rules, permissions, reason, actor, and immutable transition history.
CONSTRAINT: TESTRUN-005 and REC-006; add-recipe-feature skill.
RESTRICTION: Invalid jumps fail. Approval requires a fresh readiness evaluation with no blockers and
creates a new recipe version when readiness is versioned. No controller-side transition logic.
BEHAVIOR: Present transition/role matrix, wait for approval, implement exhaustive state tests and an
atomic transition persistence test.
```

### 10.7 Readiness-evaluation endpoint

```text
SCOPE: Expose TESTRUN-004 as GET .../recipes/{recipeId}/readiness through the full request seam.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Read-only; return rule IDs, evidence links, blockers/recommendations, and evaluated version.
BEHAVIOR: Plan contract, wait for approval, implement authorization/stale-version/isolation tests.
```

### 10.7a Readiness-transition endpoint

```text
SCOPE: Expose TESTRUN-005 as POST .../recipes/{recipeId}/readiness-transitions through the full seam.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Require expected version, target state, reason, role, fresh evaluation, and idempotency key.
BEHAVIOR: Plan contract, wait for approval, implement transition/blocker/replay/conflict/isolation tests.
```

### 10.7b Test-run entry UI

```text
SCOPE: Build only the Test Kitchen form for creating/editing a test against an exact version, including
actual yield/time, environment, equipment, observations, rating, issues, and authorized attachments.
CONSTRAINT: TEST-UI-001; creatorpantry-design-system and new-component skills.
RESTRICTION: No history/readiness/transition UI or client-side domain rule duplication.
BEHAVIOR: Show form states, wait for approval, implement component/contract/a11y tests.
```

### 10.7c Test-history and issue UI

```text
SCOPE: Build only test history, filters, issue severity, resolution details, and correction-version links.
CONSTRAINT: TEST-UI-002; creatorpantry-design-system and new-component skills.
RESTRICTION: No readiness transition or modification of immutable observations.
BEHAVIOR: Plan states, wait for approval, implement paging/filter/empty/error/a11y tests.
```

### 10.7d Readiness UI

```text
SCOPE: Build only readiness blockers/recommendations and permitted state-transition controls with evidence
links, reason, confirmation, conflict recovery, and server-confirmed final state.
CONSTRAINT: TEST-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not duplicate rule evaluation or imply approval before response success.
BEHAVIOR: Show state/copy, wait for approval, implement component/contract/a11y tests.
```

### 10.8 Test-kitchen audit

```text
SCOPE: Audit test-run ownership, recipe-version linkage, attachment authorization, issue history,
readiness rules, state transitions, UI wording, and accessibility.
CONSTRAINT: TESTRUN-001 through TESTRUN-005 and TEST-UI-001 through 003.
RESTRICTION: Report before fixes. Do not relax a readiness gate to make a test pass.
UTILIZATION: workspace-isolation-auditor, architecture-reviewer, design-review, test-gap-analyzer, and
code-reviewer.
BEHAVIOR: Run reviewers and focused/end-to-end tests, return one severity report, wait for approval,
fix blockers, and rerun.
```

## Phase 11 — Editorial, SEO, structured data, and exports

### 11.1 Creator brand profile entity

```text
SCOPE: Add workspace-owned BrandProfile with brand name, short description, default audience, channel
defaults, locale, time zone, website/reference links, logo/asset pointers, and version/audit fields.
CONSTRAINT: DEC-010 and RCPUB-001/002; add-workspace-entity and add-content-feature skills.
RESTRICTION: Do not store voice, tone, tenor, writing style, or visual direction here; Phase 11A's
BrandStyleGuideVersion is their sole source of truth. No endpoint/UI/AI training.
BEHAVIOR: Show schema/index/version design, wait for approval, implement migration, review/apply, test.
```

### 11.1a Read brand profile

```text
SCOPE: Implement only the authorized profile read through the full request seam.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No update/create; unknown and cross-workspace are indistinguishable.
BEHAVIOR: Plan ServiceModel, wait for approval, implement found/empty/isolation tests.
```

### 11.1b Create or update brand profile

```text
SCOPE: Implement only create/update with submitted-field semantics, validation, version/audit record,
optimistic concurrency, and Editor policy.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No delete and no voice/style fields. Brand defaults cannot weaken platform safety policy.
BEHAVIOR: Plan patch/version semantics, wait for approval, implement validation/conflict/isolation tests.
```

### 11.1c Brand settings UI

```text
SCOPE: Build only settings UI for brand identity, description, default audience, locale/time zone, website/
reference links, logo/asset selection, and channel defaults using profile read/update contracts.
CONSTRAINT: creatorpantry-design-system and new-component skills.
RESTRICTION: No voice/style editing, AI generation, or model-training claim. Link users to Brand Style
Guide setup for voice, tone, tenor, writing, and visual direction.
BEHAVIOR: Show form/states, wait for approval, implement component/contract/a11y tests.
```

### 11.2 Content proposal and revision entities

```text
SCOPE: Add workspace-owned ContentProposal/ContentRevision records for editorial and SEO packages pinned
to recipe/version, brand/voice versions, task/template version, status, accepted content, and staleness.
CONSTRAINT: RCPUB-001/002 and add-content-feature skill.
RESTRICTION: Derivatives never become canonical recipe facts. Accepted revisions are retained; source
recipe changes mark them NeedsReview rather than rewriting them.
BEHAVIOR: Show lifecycle/staleness design, wait for approval, implement configuration/migration and test
immutable accepted history.
```

### 11.3 Derivative staleness propagation

```text
SCOPE: When a canonical recipe version changes, mark linked content proposals, social packages, prompts,
and board packages NeedsReview through an outbox/durable job after commit.
CONSTRAINT: add-content-feature and add-background-job skills; NFR-005.
RESTRICTION: Recipe write and durable event/outbox row commit together. Do not synchronously rewrite or
delete derivatives. Consumer is idempotent.
BEHAVIOR: Plan event and transaction, wait for approval, implement with commit/failure/replay tests and
prove a recipe update cannot leave an accepted derivative falsely current.
```

### 11.4 Editorial package AI task

```text
SCOPE: Implement RCPUB-001 through the shared AI proposal lifecycle for headnote, introduction, tips,
substitutions, storage/reheating, FAQ, and CTA using exact approved recipe and voice versions.
CONSTRAINT: add-ai-capability and add-content-feature skills; .claude/rules/ai.md and content.md.
RESTRICTION: Generated prose cannot alter quantities, steps, yield, time, temperatures, or safety facts.
Unsupported factual claims are warnings. Output remains editable before acceptance.
BEHAVIOR: Show schema/template/evaluation cases, wait for approval, implement endpoint/UI task and run
ai-safety-reviewer plus stale-source tests.
```

### 11.5 SEO package AI task

```text
SCOPE: Implement editable title, slug, meta description, key phrases, alt-text suggestions, and
internal-link ideas through the shared proposal lifecycle.
CONSTRAINT: RCPUB-002, AI-012; add-ai-capability and add-content-feature skills.
RESTRICTION: Do not invent search volume, ranking, competitor data, or visible image details. Enforce
configured length/format rules deterministically.
BEHAVIOR: Plan schema and validators, wait for approval, implement with unsupported-metric, length,
slug, injection, and accessibility evaluation fixtures.
```

### 11.6 Recipe JSON-LD generator

```text
SCOPE: Implement deterministic schema.org Recipe JSON-LD from canonical approved fields and accepted
editorial metadata, with validation and an explicit missing-required-data report.
CONSTRAINT: RCPUB-002; .claude/rules/content.md and recipes.md.
RESTRICTION: No AI-generated structured facts. Do not publish inferred nutrition, ratings, images,
times, or dietary claims. Generator writes nothing.
BEHAVIOR: Verify the current official schema vocabulary, show mapping, wait for approval, implement
golden JSON tests and invalid/missing-field cases.
```

### 11.7 Markdown export

```text
SCOPE: Implement RCPUB-003 deterministic Markdown export for a selected approved recipe version,
template, unit presentation, and optional accepted editorial sections with attribution/export metadata.
CONSTRAINT: .claude/rules/content.md; add-content-feature skill.
RESTRICTION: Export is a projection, not a stored competing recipe. Escape unsafe content and use
canonical calculation services for alternate unit display.
BEHAVIOR: Show template contract, wait for approval, implement golden-file tests for complete, minimal,
special-character, and alternate-unit exports.
```

### 11.8 Print/PDF export

```text
SCOPE: Implement RCPUB-004 accessible print/PDF generation with selected image, ingredients,
instructions, notes, yield, timing, attribution, recipe version, and stable page layout.
CONSTRAINT: .claude/rules/content.md and media.md; add-content-feature skill.
RESTRICTION: Do not fetch arbitrary external image URLs. Do not mutate recipe or asset data. PDF must
carry text, not image-only pages.
BEHAVIOR: Plan template and renderer, wait for approval, implement, render representative fixtures,
visually inspect every page, and add text/accessibility assertions.
```

### 11.9 JSON-LD endpoint

```text
SCOPE: Add only GET .../recipes/{recipeId}/exports/json-ld for a selected version/revision.
CONSTRAINT: RCPUB-002; add-endpoint and add-content-feature skills.
RESTRICTION: Correct JSON content type; no invented fields, raw path, or unapproved draft without policy.
BEHAVIOR: Plan parameters/cache/error contract, wait for approval, implement isolation/validation tests.
```

### 11.9a Markdown-export endpoint

```text
SCOPE: Add only GET .../recipes/{recipeId}/exports/markdown for selected version, template, unit display,
and accepted editorial revision.
CONSTRAINT: RCPUB-003; add-endpoint and add-content-feature skills.
RESTRICTION: Accurate text/markdown content type and safe deterministic filename; no raw object path.
BEHAVIOR: Plan contract, wait for approval, implement authorization/content-disposition/isolation tests.
```

### 11.9b PDF-export endpoint

```text
SCOPE: Add only GET .../recipes/{recipeId}/exports/pdf for selected version, template, unit display,
accepted editorial revision, and authorized image.
CONSTRAINT: RCPUB-004; add-endpoint and add-content-feature skills.
RESTRICTION: Accurate application/pdf content type and safe filename; no external arbitrary image fetch.
BEHAVIOR: Plan contract, wait for approval, implement authorization/render-failure/isolation tests.
```

### 11.9c Recipe publish panel

```text
SCOPE: Build only the Publish panel showing source version, accepted content revision, template/unit
choices, readiness, staleness, preview links, and download actions for JSON-LD/Markdown/PDF.
CONSTRAINT: RCPUB-002 through 004; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not perform export logic in Angular or expose storage/provider URLs.
BEHAVIOR: Plan states, wait for approval, implement component/contract/a11y tests; api-contract-checker.
```

### 11.10 Editorial/export audit

```text
SCOPE: Audit source-version pinning, staleness, AI claims, structured-data mappings, export determinism,
content types, file naming, accessibility, and workspace isolation.
CONSTRAINT: RCPUB-001 through RCPUB-004 and AI-009/010.
RESTRICTION: Report first. Do not hide missing canonical data by filling it with generated prose.
UTILIZATION: ai-safety-reviewer, workspace-isolation-auditor, api-contract-checker, design-review,
test-gap-analyzer, and code-reviewer.
BEHAVIOR: Return deduplicated findings, wait for approval, fix blockers, rerun golden/render/evaluation
tests, and report exact export samples verified.
```

## Phase 11A — Brand Voice, Style Guide, and prompt context

> The creator owns the voice. Uploaded examples are private source material, AI produces an editable proposal, and only an explicitly approved guide version becomes the default context for writing or visual generation.

### 11A.1 Brand source-document model

```text
SCOPE: Add workspace-owned BrandSourceDocument and BrandSourceDocumentVersion metadata with title,
document type, purpose, channel, audience, tags, status, media type, size, checksum, original filename,
private blob object key, extracted-text object key, created/updated actor, timestamps, and concurrency.
CONSTRAINT: .claude/rules/content.md, media.md, and tenancy.md; add-workspace-entity skill.
RESTRICTION: SQL stores metadata and private blob pointers, not document bodies or public URLs. Original
versions are immutable. No upload endpoint or style-guide entity yet.
BEHAVIOR: Show lifecycle, supported document types, indexes, and retention, wait for approval, implement
entities/configuration only.
```

### 11A.2 Structured Brand Style Guide model

```text
SCOPE: Add workspace-owned BrandStyleGuide and immutable BrandStyleGuideVersion with display name,
purpose, status, source-document links, and structured sections for voice, tone, tenor, writing style,
audience, point of view, vocabulary, sentence rhythm, formatting, storytelling, calls to action, do/don't
rules, channel variants, blog guidance, social guidance, visual identity, photography direction, image-
prompt guidance, negative visual guidance, and user notes.
CONSTRAINT: DEC-010, AI-001/002, RCPUB-001/002; add-workspace-entity and add-content-feature skills.
RESTRICTION: Guide content is user-editable source data, not a provider prompt blob. Historical approved
versions are immutable. No AI generation or endpoint yet.
BEHAVIOR: Show aggregate, section cardinality, version/status rules, and active-default constraints; wait
for approval, implement entities/configuration only.
```

### 11A.3 Brand document and style-guide migration

```text
SCOPE: Generate the migration for BrandSourceDocument, document versions, BrandStyleGuide, guide versions,
guide/source links, BrandProfile's nullable default approved guide-version pointer, and active/default
constraints with WorkspaceId-leading indexes.
CONSTRAINT: .claude/rules/backend.md and tenancy.md.
RESTRICTION: Do not apply before review. No cascade that deletes blob-backed history or approved guide
versions from an ordinary profile delete. Do not hand-edit generated migration code.
BEHAVIOR: Show migration, unique/index/delete behavior, and rollback implications; wait for approval,
apply through MigrationService, and confirm no pending model changes.
```

### 11A.4 Private brand-document object gateway

```text
SCOPE: Add a typed internal gateway for put, get, copy, and delete of brand source objects under a server-
generated workspace/document/version prefix, returning stable object identity and metadata.
CONSTRAINT: .claude/rules/media.md and external.md; add-media-feature skill.
RESTRICTION: No caller-supplied object key, public container, permanent public URL, provider DTO leakage,
or document parsing in this prompt.
BEHAVIOR: Show object-key and access policy, wait for approval, implement with fake/local blob tests for
write/read/copy/delete, traversal rejection, and workspace isolation.
```

### 11A.5 Upload brand source document

```text
SCOPE: Implement one upload operation through the full request seam for approved PDF, DOCX, Markdown,
plain text, HTML, and supported image formats, creating SQL metadata/version plus private blob object.
CONSTRAINT: API-003/005, SEC-004/005; add-endpoint, add-content-feature, and add-media-feature skills.
RESTRICTION: Validate size, signature, decoded format, and malware policy. Do not trust filename/MIME,
expose blob paths, or leave a committed SQL row when object storage fails. Retry is idempotent.
BEHAVIOR: Show validation and compensation plan, wait for approval, implement success/replay/spoofed/
oversized/malware/blob-failure/isolation tests.
```

### 11A.6 List brand source documents

```text
SCOPE: Implement a paged workspace-scoped list filtered by document type, channel, tag, status, and free
text, returning current-version metadata and extraction state.
CONSTRAINT: add-endpoint and add-content-feature skills; PERF-001/002.
RESTRICTION: No document bytes, extracted text, blob paths, or unbounded result in the list.
BEHAVIOR: Show query/index plan, wait for approval, implement combined-filter/paging/isolation tests.
```

### 11A.7 Brand document detail and authorized download

```text
SCOPE: Implement detail metadata and a separate authorized original-version download with accurate content
type, extension, and safe deterministic filename.
CONSTRAINT: API-010; add-endpoint, add-content-feature, and add-media-feature skills.
RESTRICTION: Unknown and cross-workspace are indistinguishable. Never return raw object key or storage URL.
BEHAVIOR: Plan the two contracts, wait for approval, implement detail/download/not-found/isolation tests.
```

### 11A.8 Replace brand source document with a new version

```text
SCOPE: Implement replacement as a new immutable BrandSourceDocumentVersion with fresh validation, blob
object, checksum, extraction state, actor, and timestamp while retaining historical versions.
CONSTRAINT: add-endpoint, add-content-feature, and add-media-feature skills.
RESTRICTION: No in-place blob overwrite. Existing approved guide versions remain pinned to their source
version and are marked stale only by explicit staleness rules.
BEHAVIOR: Show version/concurrency/compensation plan, wait for approval, implement conflict/blob-failure/
history/isolation tests.
```

### 11A.9 Archive or remove a brand source document

```text
SCOPE: Implement archive and retention-policy-aware removal for a source document, exposing linked guide
versions and prompt contexts before confirmation.
CONSTRAINT: SEC-008 and DATA-RQ-009; add-content-feature and add-media-feature skills.
RESTRICTION: Ordinary removal cannot break approved historical guide provenance or physically delete held
objects. Archive is the safe default.
BEHAVIOR: Show link/retention behavior, wait for approval, implement archive/repeat/linked/held/isolation tests.
```

### 11A.10 Document extraction operation and worker

```text
SCOPE: Add a durable extraction operation and Worker handler that reads an authorized document version,
extracts text and basic structure using format-specific parsers, stores the normalized extracted artifact
in private blob storage, and updates SQL pointer/status/provenance.
CONSTRAINT: add-background-job, add-media-feature, and add-content-feature skills.
RESTRICTION: Treat document content as untrusted data. No provider/model call, prompt execution, SQL body
storage, macro execution, or silent OCR claim. Unsupported/scanned content returns a clear review state.
BEHAVIOR: Show parser/isolation/lease/failure matrix, wait for approval, implement PDF/DOCX/MD/TXT/HTML/
image-unsupported/corrupt/replay/restart/isolation tests.
```

### 11A.11 Extracted-text review and correction

```text
SCOPE: Add an authorized read plus creator correction operation for extracted text; corrections create a
new extracted-artifact version and record actor/reason without altering the original upload.
CONSTRAINT: .claude/rules/content.md and media.md; add-endpoint skill.
RESTRICTION: Do not overwrite original document or historical extraction. Corrected text remains private
and untrusted when later placed in prompts.
BEHAVIOR: Plan version/concurrency contract, wait for approval, implement read/correct/conflict/isolation tests.
```

### 11A.12 Brand-source chunk and embedding model

```text
SCOPE: Add workspace-owned BrandSourceChunk metadata with source document/version, extracted-artifact
version, ordinal, text-object offset or bounded text, checksum, embedding, embedding model/dimension, and
EmbeddedAt for task-relevant retrieval.
CONSTRAINT: .claude/rules/ai.md and tenancy.md; add-ai-capability skill.
RESTRICTION: Never combine chunks across workspaces. A mixed model/dimension index is invalid. Do not
embed binary objects or unreviewed failed extraction.
BEHAVIOR: Show storage/tokenization/vector-index strategy and privacy tradeoffs, wait for approval,
implement entity/configuration/migration and fixed-vector isolation tests.
```

### 11A.13 Brand-source embedding worker

```text
SCOPE: Add a durable Worker job that chunks the selected extracted-artifact version, generates embeddings
in bounded batches, writes model/version provenance, and retires stale chunks after successful replacement.
CONSTRAINT: add-background-job and add-ai-capability skills; .claude/rules/ai.md.
RESTRICTION: No per-request embedding, cross-workspace batch, provider SDK outside adapter, or deletion of
current chunks before replacement succeeds.
BEHAVIOR: Show batching/retry/re-embedding plan, wait for approval, implement fake-vector/replay/model-
change/partial-failure/isolation tests.
```

### 11A.14 Create a manual brand guide

```text
SCOPE: Implement creation of a BrandStyleGuide and version 1 from a short plain-language questionnaire,
optional selected source documents, and directly entered structured sections.
CONSTRAINT: add-endpoint, add-content-feature, and add-workspace-entity skills.
RESTRICTION: No AI call. Blank optional fields remain blank. User-selected source pointers must resolve to
authorized exact versions.
BEHAVIOR: Show minimal questionnaire and mapping, wait for approval, implement validation/idempotency/
source-link/isolation tests.
```

### 11A.15 Edit brand guide and create a new version

```text
SCOPE: Implement submitted-field editing of guide sections with optimistic concurrency, change reason,
actor, source-link edits, and one new immutable guide version.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Never update an approved historical version in place or remove provenance silently. A no-op
does not create a version.
BEHAVIOR: Plan patch/version semantics, wait for approval, implement partial/clear/no-op/conflict/isolation tests.
```

### 11A.16 Read current brand guide

```text
SCOPE: Implement only authorized read of one brand guide's current working and active approved version.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No list, compare, activation, or mutation. Unknown and cross-workspace are indistinguishable.
BEHAVIOR: Plan ServiceModel, wait for approval, implement current/active/empty/isolation tests.
```

### 11A.16a List brand-guide versions

```text
SCOPE: Implement only paged brand-guide version metadata with version, status, source count, actor, reason,
created date, staleness, and active marker.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Do not return every complete guide body in the list or mutate status.
BEHAVIOR: Plan projection, wait for approval, implement order/paging/isolation tests.
```

### 11A.16b Compare brand-guide versions

```text
SCOPE: Implement only deterministic section-aware comparison of two versions, including source-link changes.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No AI-generated diff, activation, or mutation. Stable section keys drive comparison.
BEHAVIOR: Show diff model, wait for approval, implement add/remove/change/move/no-change/isolation tests.
```

### 11A.16c Activate approved brand-guide version

```text
SCOPE: Implement only explicit activation of one approved version as workspace default with expected current
active version, confirmation, actor, reason, audit event, and idempotency.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Draft/stale/invalid version cannot activate without approved override policy. No content edit.
BEHAVIOR: Show activation transaction, wait for approval, implement valid/replay/conflict/reject/isolation tests.
```

### 11A.17 AI-assisted brand guide proposal

```text
SCOPE: Implement a brand-guide analysis task through the shared AI lifecycle using selected authorized
document versions and questionnaire answers to propose structured voice, tone, tenor, style, language,
channel, blog, social, and visual/image-generation guidance with source citations and uncertainties.
CONSTRAINT: AI-001 through AI-012; add-ai-capability and add-content-feature skills.
RESTRICTION: Output is a proposal, not an active guide. Do not imitate a living writer by name, infer
sensitive traits, copy long source passages, mix workspaces, or hide thin/contradictory evidence.
BEHAVIOR: Show schema/template/retrieval/citation/evaluation plan, wait for approval, implement with fake
provider plus sparse-source/conflicting-source/injection/schema/isolation tests; ai-safety-reviewer.
```

### 11A.18 Accept or partially accept a brand-guide proposal

```text
SCOPE: Reuse the proposal diff/disposition pattern to accept all or selected brand-guide sections into
one new Draft guide version, preserving source citations and creator edits.
CONSTRAINT: AIREC-007 and AIREC-GR-002/004/007; add-ai-capability and add-content-feature skills.
RESTRICTION: Stale source guide/document versions cannot be silently rebased. Acceptance does not activate
the guide. Replay creates no duplicate version.
BEHAVIOR: Plan section selection/transaction, wait for approval, implement full/partial/reject/stale/
replay/rollback/isolation tests.
```

### 11A.19 Deterministic BrandContextPackage assembler

```text
SCOPE: Build a server-side assembler that takes workspace, task type, channel, audience, optional guide
version, and source-document selections and returns a bounded BrandContextPackage containing exact guide
version, relevant structured rules, cited excerpts, conflicts, omissions, checksum, and token estimate.
CONSTRAINT: AI-001/002/008, DEC-010; .claude/rules/ai.md and content.md.
RESTRICTION: No arbitrary all-document stuffing, cross-workspace retrieval, client-supplied blob key,
provider call, or hidden fallback to another guide. Exact context inputs are recorded with generation.
BEHAVIOR: Show precedence, retrieval, conflict, and token-budget rules, wait for approval, implement
deterministic task/channel/no-guide/override/injection/isolation tests.
```

### 11A.20 Apply brand context to writing tasks

```text
SCOPE: Update editorial, SEO, social, and other writing AI task handlers to request a BrandContextPackage,
record its guide/source/checksum provenance, and expose the chosen guide in proposal detail.
CONSTRAINT: RCPUB-001/002, SOC-001, AI-001/002; add-ai-capability and add-content-feature skills.
RESTRICTION: Update one task handler per atomic turn. Do not copy guide text into scattered prompt files,
silently select stale guide, or allow style rules to alter canonical recipe facts/safety.
BEHAVIOR: Show handler-by-handler change map, wait for approval, implement each with guide/no-guide/
channel-override/provenance tests and evaluation comparisons.
```

### 11A.21 Apply visual style to image generation

```text
SCOPE: Update photography-concept and image-prompt composition tasks to use the approved guide's visual
identity, photography direction, composition, palette descriptors, texture, lighting, prop, food-styling,
negative guidance, and selected image-reference documents through BrandContextPackage.
CONSTRAINT: IMG-001/002, AI-001/002; add-ai-capability and add-media-feature skills.
RESTRICTION: The guide cannot override image safety/provider policy or invent recipe ingredients. Do not
send original image bytes when a reviewed visual summary is sufficient and approved.
BEHAVIOR: Show visual-context schema and precedence, wait for approval, implement guide/no-guide/reference/
conflict/provenance tests and image-prompt evaluations.
```

### 11A.21a Brand guide controls for writing workflows

```text
SCOPE: Add a shared, simple guide control to editorial, SEO, and social setup screens showing the active
guide name/version, “Use my brand voice” default, change selection, no-guide option, stale warning, and
plain-language preview of applicable channel rules.
CONSTRAINT: BRAND-007 and easy-as-pie product rule; creatorpantry-design-system skill.
RESTRICTION: No raw context/chunk/prompt display in the primary UI and no silent stale-guide fallback.
Advanced provenance remains available behind details.
BEHAVIOR: Show default/override/stale/no-guide states, wait for approval, implement one workflow integration
per atomic turn with component/contract/a11y tests.
```

### 11A.21b Brand visual-style controls for image workflows

```text
SCOPE: Add a shared guide control to photography and image-prompt setup screens showing the active visual
style, selected reference examples, optional override, negative guidance summary, and exact guide version.
CONSTRAINT: BRAND-008 and easy-as-pie product rule; creatorpantry-design-system skill.
RESTRICTION: Do not expose blob paths, embeddings, or provider jargon. No silent reference-image upload or
guide activation.
BEHAVIOR: Show default/override/conflict/no-visual-guide states, wait for approval, implement component/
contract/a11y tests.
```

### 11A.22 “Create my voice” wizard shell

```text
SCOPE: Build only the resumable wizard shell, step routing, progress, autosave indicator, Back, Save and
exit, Cancel, Start over, help, and completion summary for the brand setup workflow.
CONSTRAINT: The easy-as-pie product rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No questionnaire, upload, extraction, AI generation, guide editor, test drive, or activation.
BEHAVIOR: Show step/state/resume/accessibility map, wait for approval, implement shell/recovery/a11y tests.
```

### 11A.22a Brand goals wizard step

```text
SCOPE: Add only the plain-language goal/audience/channel selection step with useful defaults and optional
advanced details.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No prompt/model terminology or source upload. One primary Continue action.
BEHAVIOR: Show copy/defaults, wait for approval, implement component/autosave/a11y tests.
```

### 11A.22b Voice, tone, tenor, and style questionnaire step

```text
SCOPE: Add only a short guided questionnaire using understandable choices, examples, and optional free text
for voice, tone, tenor, rhythm, vocabulary, point of view, formality, humor, and calls to action.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No jargon-only scales, forced completion of optional fields, or AI call.
BEHAVIOR: Show question order/examples, wait for approval, implement autosave/keyboard/a11y tests.
```

### 11A.22c Source examples wizard step

```text
SCOPE: Add only upload/paste/select-example controls and simple labels for what each sample demonstrates:
“sounds like me,” “doesn't sound like me,” blog, social, newsletter, visual reference, or other.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and uploader recipe.
RESTRICTION: No extraction review or guide generation. File validation errors use plain-language recovery.
BEHAVIOR: Show minimal/advanced states, wait for approval, implement upload/paste/autosave/a11y tests.
```

### 11A.22d Extraction review wizard step

```text
SCOPE: Add only extraction status and review/correction for selected textual sources, with clear handling
for scanned, unsupported, failed, or partially extracted documents.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No raw blob path, parser jargon, or silent continuation with failed/incorrect text.
BEHAVIOR: Show states/copy, wait for approval, implement correction/retry/skip/autosave/a11y tests.
```

### 11A.22e Guide creation choice step

```text
SCOPE: Add only the choice between “Draft it for me” and “I'll write it myself,” showing selected sources,
expected result, optional live-AI cost confirmation, and the next action.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No hidden provider call or default activation. Do not expose model selection unless advanced.
BEHAVIOR: Show copy/decision states, wait for approval, implement confirmation/error/a11y tests.
```

### 11A.22f Guide review and edit step

```text
SCOPE: Add only section-by-section review/edit of the draft guide with source citations, AI-change markers,
plain-language help, completeness hints, and Save draft.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No activation or test drive. User edits are authoritative and cannot be regenerated silently.
BEHAVIOR: Show editor sections/states, wait for approval, implement edit/autosave/conflict/a11y tests.
```

### 11A.22g Test-drive and activation step

```text
SCOPE: Add only the final test-drive summary and explicit activation decision, showing what will become the
workspace default and offering Activate, Keep as draft, or Back to edit.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No implicit activation, publication, or content mutation. Live generation needs cost confirmation.
BEHAVIOR: Show confirmation/success/conflict states, wait for approval, implement component/contract/a11y tests.
```

### 11A.23 Brand Library list screen

```text
SCOPE: Build only the Brand Library list/search/filter with source type, purpose, channel, status,
extraction state, version, updated date, first-use empty state, and Upload example action.
CONSTRAINT: creatorpantry-design-system and new-component skills.
RESTRICTION: No source detail, guide editor, or raw blob path.
BEHAVIOR: Show layout/states, wait for approval, implement paging/filter/empty/error/a11y tests.
```

### 11A.23a Brand source detail screen

```text
SCOPE: Build only source detail with metadata, versions, authorized download, extraction status/review,
replace-new-version, archive, source usage, and plain-language error recovery.
CONSTRAINT: creatorpantry-design-system, add-content-feature, and add-media-feature skills.
RESTRICTION: No guide editing or direct blob URL. Replacement never overwrites history.
BEHAVIOR: Show actions/states, wait for approval, implement contract/conflict/a11y tests.
```

### 11A.23b Style Guide editor screen

```text
SCOPE: Build only structured guide section editing, source citations, Draft/Approved/Stale status,
autosave, concurrency recovery, and Save new version.
CONSTRAINT: creatorpantry-design-system and new-component skills.
RESTRICTION: No version comparison or activation. No hidden AI regeneration after user edits.
BEHAVIOR: Show editor hierarchy/states, wait for approval, implement autosave/conflict/a11y tests.
```

### 11A.23c Style Guide history, compare, and activate screen

```text
SCOPE: Build only version history, two-version comparison, source-link differences, active marker, and
explicit activation action using existing endpoints.
CONSTRAINT: creatorpantry-design-system and new-component skills.
RESTRICTION: UI does not calculate diff or activate Draft/stale version outside server policy.
BEHAVIOR: Show interaction/confirmation, wait for approval, implement component/contract/a11y tests.
```

### 11A.24 Voice and visual style test-drive

```text
SCOPE: Add a read-only “Test my style” experience comparing neutral versus guide-informed sample blog
intro, social caption, and image prompt using one selected guide version.
CONSTRAINT: easy-as-pie product rule and AI proposal requirements.
RESTRICTION: Writes no canonical content and incurs no hidden live cost. Identify guide rules/citations
used rather than merely claiming the output “sounds more like you.”
BEHAVIOR: Plan comparison/cost confirmation, wait for approval, implement fake-provider/a11y tests.
```

### 11A.24a Brand voice and style audit

```text
SCOPE: Audit the complete brand document, extraction, embedding, guide, context assembly, writing/image
integration, wizard, Brand Library, guide editor, test drive, versioning, and workspace isolation.
CONSTRAINT: Phase 11A requirements and easy-as-pie product rule.
RESTRICTION: Report before fixes. Do not solve simplicity by hiding provenance, cost, uncertainty, or activation.
UTILIZATION: ai-safety-reviewer, workspace-isolation-auditor, integration-reviewer, design-review,
api-contract-checker, architecture-reviewer, and test-gap-analyzer.
BEHAVIOR: Run audits and first-use/returning-user E2E journeys, return findings, wait for approval, fix
approved blockers in atomic turns, and rerun evaluations.
```

## Phase 12 — Creative production, generated images, and DAM

### 12.1 Content channel and weekly-theme configuration

```text
SCOPE: Add validated application configuration or reference data for the six content channels and seven
weekly themes required by DATA-RQ-001 through 003, with stable keys and display metadata.
CONSTRAINT: SEED-001 and DATA-RQ-001 through 003; .claude/rules/content.md.
RESTRICTION: Do not scatter channel/theme literals across prompts, Angular, or providers. If user-managed
configuration is chosen, stop and add separate ILF/function-point scope before implementation.
BEHAVIOR: Show source-of-truth choice and schema, wait for approval, implement with uniqueness and
invalid-key tests.
```

### 12.2 Content-seed generator

```text
SCOPE: Implement SEED-001 deterministic/randomized seed selection returning cuisine, dish type, method,
photography style, channel, day/theme, description, and occasion from approved reference/configuration.
CONSTRAINT: SEED-001; add-content-feature skill.
RESTRICTION: Invalid channels fail. Inject randomness for deterministic tests. No AI call or persistence.
BEHAVIOR: Plan selection weights and reproducibility, wait for approval, implement with channel/day/
invalid/seeded-random tests and expose the endpoint through the full seam.
```

### 12.3 Prompt library entities

```text
SCOPE: Add workspace-owned PromptRecord with channel, text, label, source type/ID, recipe/version lineage,
asset lineage, content type, template version, creator edits, and timestamps.
CONSTRAINT: PRM-001 through PRM-005 and DATA-001; add-workspace-entity and add-media-feature skills.
RESTRICTION: Prompts are private creator content. No blob URL as authority; store stable object identity.
Do not create prompt endpoints yet.
BEHAVIOR: Show data model/indexes/retention, wait for approval, implement configuration and migration,
review/apply it, and test workspace scoping.
```

### 12.3a Save prompt record

```text
SCOPE: Implement only PRM-001 as the idempotent prompt-save operation used when a generated asset is
committed, preserving channel, creator-edited prompt, source/recipe/image lineage, and content type.
CONSTRAINT: add-content-feature, add-media-feature, and add-endpoint skills.
RESTRICTION: Do not create duplicate prompt records on DAM retry. No list/detail/download in this prompt.
BEHAVIOR: Plan transaction linkage to DAM-001, wait for approval, implement replay/rollback/isolation tests.
```

### 12.3b List/search prompts

```text
SCOPE: Implement only PRM-002 paged prompt list with channel filter, search, newest-first default, and
summary projection.
CONSTRAINT: add-content-feature and add-endpoint skills.
RESTRICTION: No unbounded list, full asset bytes, raw object path, or cross-workspace result.
BEHAVIOR: Show query/index plan, wait for approval, implement filter/paging/isolation tests.
```

### 12.3c Prompt detail

```text
SCOPE: Implement only PRM-003 prompt detail by ID with complete lineage and authorized metadata.
CONSTRAINT: add-content-feature and add-endpoint skills.
RESTRICTION: Unknown and cross-workspace are indistinguishable; no download presentation in this prompt.
BEHAVIOR: Plan ServiceModel, wait for approval, implement found/not-found/isolation tests.
```

### 12.3d Prompt text download

```text
SCOPE: Implement only PRM-004 deterministic plain-text prompt download.
CONSTRAINT: add-content-feature and add-endpoint skills; API-010.
RESTRICTION: Return only authorized prompt text with safe `.txt` filename and accurate content type.
BEHAVIOR: Plan disposition/filename, wait for approval, implement content/name/isolation tests.
```

### 12.3e Prompt JSON download

```text
SCOPE: Implement only PRM-005 deterministic JSON prompt-record download.
CONSTRAINT: add-content-feature and add-endpoint skills; API-010.
RESTRICTION: Exclude secrets/internal storage paths; safe `.json` filename and accurate content type.
BEHAVIOR: Plan export contract, wait for approval, implement schema/name/isolation tests.
```

### 12.4 Photography-concept AI task

```text
SCOPE: Implement only IMG-001 as a structured photography-concept AI task using channel memory, optional
recipe/version, creator concept, and scene/style overrides.
CONSTRAINT: add-ai-capability, add-content-feature, and add-media-feature skills.
RESTRICTION: No final prompt composition, image generation, or recipe mutation.
BEHAVIOR: Show schema/template/evaluations, wait for approval, implement through shared AI lifecycle,
run ai-safety-reviewer.
```

### 12.4a Image-prompt composition task

```text
SCOPE: Implement only IMG-002 to compose an editable final image prompt from an approved concept, channel
memory, optional recipe/version, scene/style overrides, and authorized brief.
CONSTRAINT: add-ai-capability, add-content-feature, and add-media-feature skills.
RESTRICTION: No rendering. Brief text is untrusted. Creator-edited final prompt is authoritative.
BEHAVIOR: Show schema/template/evaluations, wait for approval, implement through shared AI lifecycle,
run ai-safety-reviewer.
```

### 12.5 Reference-image analysis

```text
SCOPE: Implement IMG-004 for validated JPEG/PNG/WEBP/GIF reference images, returning structured visual
observations and an editable prompt with uncertainty and no recipe mutation.
CONSTRAINT: IMG-004, API-003; add-ai-capability and add-media-feature skills.
RESTRICTION: Validate size, signature, decoded format, dimensions, and authorization. Do not infer unseen
ingredients, brand ownership, people identity, or unsupported food-safety facts.
BEHAVIOR: Plan upload/vision boundary, wait for approval, implement with valid, spoofed MIME, oversized,
corrupt, animated, unsafe, and cancellation tests.
```

### 12.6 Generated-image workspace

```text
SCOPE: Add workspace-owned GeneratedImageOperation/GeneratedImage records and staging-object identity,
prompt lineage, provider/model metadata, status, dimensions, media type, checksum, retention deadline,
and idempotency key.
CONSTRAINT: IMG-003/005/006 and DATA-002; add-workspace-entity and add-media-feature skills.
RESTRICTION: Database stores metadata; bytes stay in private staging storage. No public permanent URL.
Rejected images are not DAM assets.
BEHAVIOR: Show lifecycle/retention/index design, wait for approval, implement configuration/migration,
review/apply it, and test workspace ownership.
```

### 12.7 Image-provider gateway and generation worker

```text
SCOPE: Add provider-neutral image gateway plus Worker job for IMG-003 generating one to four variants,
validating responses, storing each staged object, and persisting accurate status/provenance.
CONSTRAINT: INT-010 through 013, IMG-003; add-external-integration, add-background-job, and
add-media-feature skills.
RESTRICTION: One provider call per image unless verified batch support exists. Never stretch images,
trust provider media blindly, expose credentials, or hold SQL transaction during provider calls.
BEHAVIOR: Plan operation/compensation/idempotency, wait for approval, implement with fake-provider tests
for partial batch failure, corrupt bytes, cancellation, replay, rate limit, and orphan cleanup.
```

### 12.8 Staged-image retrieval, deletion, and cleanup

```text
SCOPE: Implement IMG-005/006 authorized preview/download/delete endpoints plus a background retention job
for expired/uncommitted staged images and orphan reconciliation.
CONSTRAINT: INT-020 through 023; add-endpoint, add-background-job, and add-media-feature skills.
RESTRICTION: Prevent traversal and cross-workspace object access. Return accurate media type/extension.
Cleanup is idempotent and never deletes a committed DAM object.
BEHAVIOR: Implement endpoint/job tests for found/not-found, traversal, isolation, repeat delete, expiry,
and database/blob partial failures.
```

### 12.9 DAM aggregate and migration

```text
SCOPE: Add workspace-owned DamAsset, DamAssetVersion, DamUtilization, and recipe/prompt lineage records
plus private object identity, media metadata, soft-delete state, audit fields, and concurrency token.
CONSTRAINT: DATA-003 and .claude/rules/media.md; add-media-feature and add-workspace-entity skills.
RESTRICTION: Original bytes/versions are immutable. Database stores object identity, not public URL.
No DAM endpoint in this prompt.
BEHAVIOR: Show aggregate/index/delete design, wait for approval, implement migration, review/apply, test.
```

### 12.9a Create DAM asset

```text
SCOPE: Implement only DAM-001 to create an asset from validated upload or staged image, initial version,
metadata, prompt/recipe lineage, and object copy/move with compensating consistency.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No search/update/delete. Idempotent retry creates one asset. Preserve actual media metadata.
BEHAVIOR: Show transaction/compensation plan, wait for approval, implement rollback/replay/isolation tests.
```

### 12.9b Search DAM assets

```text
SCOPE: Implement only DAM-002 paged search for channel, platform, day, style, cuisine, meal, tags, date,
recipe link, and free text, excluding soft-deleted assets.
CONSTRAINT: add-media-feature and add-endpoint skills; PERF-001/002.
RESTRICTION: No binary bytes in results; clamp page size; deterministic sort; workspace scope required.
BEHAVIOR: Show query/index plan, wait for approval, implement combined-filter/paging/isolation tests.
```

### 12.9c Retrieve DAM detail

```text
SCOPE: Implement only DAM-003 detail with metadata, prompt/recipe lineage, utilization and version counts,
and paged or bounded histories.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No raw object path or cross-workspace link; soft-deleted detail follows approved policy.
BEHAVIOR: Plan ServiceModel, wait for approval, implement found/not-found/soft-delete/isolation tests.
```

### 12.9d Update DAM metadata

```text
SCOPE: Implement only DAM-004 submitted-field metadata patch with optimistic concurrency and audit fields.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: Metadata update never replaces bytes/version, ownership, object identity, or immutable lineage.
BEHAVIOR: Plan patch semantics, wait for approval, implement partial/clear/conflict/isolation tests.
```

### 12.9e Soft-delete DAM asset

```text
SCOPE: Implement only DAM-005 soft delete with confirmation policy, timestamp/actor, linked-content impact,
and exclusion from ordinary reads.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No physical object deletion in request path. Do not silently delete recipe/card links.
BEHAVIOR: Plan linked-state behavior, wait for approval, implement repeat/delete/read/isolation tests.
```

### 12.9f Render latest DAM image

```text
SCOPE: Implement only DAM-006 authorized inline rendering of the latest active version.
CONSTRAINT: add-media-feature and add-endpoint skills; API-010.
RESTRICTION: Accurate content type/cache policy; no raw blob redirect/path; exclude soft-deleted assets.
BEHAVIOR: Plan response/range/cache behavior, wait for approval, implement found/missing/isolation tests.
```

### 12.9g Download latest DAM version

```text
SCOPE: Implement only DAM-007 authorized download of the latest version with accurate type, extension,
and deterministic safe filename.
CONSTRAINT: add-media-feature and add-endpoint skills; API-010.
RESTRICTION: No selected historical version or raw object path in this prompt.
BEHAVIOR: Plan disposition/filename, wait for approval, implement type/name/isolation tests.
```

### 12.9h Download historical DAM version

```text
SCOPE: Implement only DAM-008 authorized download of one selected version belonging to the requested asset.
CONSTRAINT: add-media-feature and add-endpoint skills; API-010.
RESTRICTION: Verify both identifiers and workspace; do not fall back to latest on missing version.
BEHAVIOR: Plan contract, wait for approval, implement ownership/not-found/type/isolation tests.
```

### 12.9i Log DAM utilization

```text
SCOPE: Implement only DAM-009 utilization logging with platform, utilized date, derived day, campaign,
notes, actor, and timestamp for an active authorized asset.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No utilization for missing/soft-deleted/cross-workspace assets. Derived day uses explicit zone.
BEHAVIOR: Plan contract, wait for approval, implement derivation/validation/replay/isolation tests.
```

### 12.9j Add DAM version

```text
SCOPE: Implement only DAM-010 validated new-version upload with atomic next version number, immutable
original, accurate media metadata, object storage compensation, and audit fields.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No in-place blob overwrite. Concurrent uploads cannot share a version number.
BEHAVIOR: Show transaction/compensation plan, wait for approval, implement concurrency/failure/isolation tests.
```

### 12.10 Content Pipeline configuration and seed UI

```text
SCOPE: Build only PIPE-UI-001/002 through configuration and seed review: channel, day, variant count,
scene, style, concept, generate seed, edit/accept seed, and persisted in-progress client state.
CONSTRAINT: creatorpantry-design-system, new-component, and add-content-feature skills.
RESTRICTION: No prompt/image/social/DAM action in this prompt.
BEHAVIOR: Show state flow, wait for approval, implement component/contract/a11y tests.
```

### 12.10a Content Pipeline prompt and reference analysis UI

```text
SCOPE: Extend only the pipeline with concept suggestion, prompt composition/review, brief upload, and
reference-image analysis, preserving creator edits as final prompt.
CONSTRAINT: PIPE-UI-001/003; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: No image render, social generation, or DAM save.
BEHAVIOR: Plan states, wait for approval, implement component/contract/a11y/cancellation tests.
```

### 12.10b Content Pipeline image selection UI

```text
SCOPE: Extend only the pipeline with image operation progress, one-to-four grid, keyboard lightbox,
keeper selection, download, rejection, and cleanup action.
CONSTRAINT: PIPE-UI-004; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: No social generation or DAM commit. No provider/blob URL exposed.
BEHAVIOR: Plan state/recovery, wait for approval, implement component/contract/a11y tests.
```

### 12.10c Image Studio screen

```text
SCOPE: Build IMAGE-UI-001 through 004 using existing concept/prompt/reference/generation/staging contracts.
CONSTRAINT: creatorpantry-design-system, new-component, and add-media-feature skills.
RESTRICTION: No social workflow or board creation. DAM save uses existing DAM-001 only when added as a
separate action prompt.
BEHAVIOR: Show component/state map, wait for approval, implement with UI/a11y/error/cancellation tests.
```

### 12.10d Prompt Library list and detail UI

```text
SCOPE: Build PROMPT-UI-001 through 003 for server search/filter/sort/page, preview, detail, copy, text/JSON
download, and reuse navigation.
CONSTRAINT: creatorpantry-design-system, new-component, and add-content-feature skills.
RESTRICTION: Do not locally page an incomplete set or expose storage paths. Reuse creates a new workflow,
not an in-place prompt mutation.
BEHAVIOR: Plan routes/states, wait for approval, implement component/contract/a11y tests.
```

### 12.10e DAM Library list UI

```text
SCOPE: Build only DAM search/filter/paging and thumbnail-card results with channel, label, metadata, usage,
version count, loading, empty, error, and retry states.
CONSTRAINT: DAM-UI-001/002; creatorpantry-design-system and new-component skills.
RESTRICTION: No detail or mutation. Use authorized thumbnail/render endpoint, never raw blob URL.
BEHAVIOR: Show query/state design, wait for approval, implement component/contract/a11y tests.
```

### 12.10f DAM asset detail UI

```text
SCOPE: Build only DAM detail with metadata, prompt/recipe lineage, utilization history, version history,
and authorized preview/download actions.
CONSTRAINT: DAM-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: No edit/upload/utilization/delete action in this prompt.
BEHAVIOR: Plan sections/states, wait for approval, implement component/contract/a11y tests.
```

### 12.10g DAM metadata and version actions UI

```text
SCOPE: Add only metadata edit and new-version upload actions to DAM detail with validation, progress,
concurrency conflict, cancel, and success states.
CONSTRAINT: DAM-UI-004; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: Upload never overwrites current bytes. No utilization/delete/add-to-board action.
BEHAVIOR: Plan dialogs/state, wait for approval, implement component/contract/a11y tests.
```

### 12.10h DAM utilization and soft-delete actions UI

```text
SCOPE: Add only utilization logging and soft-delete actions with clear retention/link impact and confirmation.
CONSTRAINT: DAM-UI-004/005; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: Do not imply physical immediate deletion or permit utilization of inactive assets.
BEHAVIOR: Show copy/state, wait for approval, implement component/contract/a11y tests.
```

### 12.10i Recipe asset-link command

```text
SCOPE: Implement only RCPUB-005 link/unlink commands for authorized DAM asset roles hero, step, social,
or test image, preserving utilization history and optional version pinning.
CONSTRAINT: RCPUB-005; add-recipe-feature, add-media-feature, and add-endpoint skills.
RESTRICTION: Prevent cross-workspace links. Unlink does not delete asset or history.
BEHAVIOR: Plan contract/invariants, wait for approval, implement link/unlink/replay/isolation tests.
```

### 12.10j Recipe asset-link UI

```text
SCOPE: Add only DAM browser/select/link/unlink UI within recipe media section with role, version pinning,
preview, and authorization-aware actions.
CONSTRAINT: RCPUB-005; creatorpantry-design-system and new-component skills.
RESTRICTION: No upload/edit/delete asset action inside recipe editor.
BEHAVIOR: Show interaction, wait for approval, implement component/contract/a11y tests.
```

### 12.10k Creative-production audit

```text
SCOPE: Audit content pipeline, image studio, prompt library, staging, DAM, and recipe asset links.
CONSTRAINT: SEED, IMG, PRM, DAM, RCPUB-005 and UI requirements.
RESTRICTION: Report first; no unrelated redesign.
UTILIZATION: design-review, api-contract-checker, workspace-isolation-auditor, integration-reviewer,
ai-safety-reviewer, and test-gap-analyzer.
BEHAVIOR: Return findings, wait for approval, fix blockers, rerun focused and end-to-end pipeline tests.
```

## Phase 13 — Social packages, content board, and publishing

### 13.1 Social package model

```text
SCOPE: Add workspace-owned SocialPackage and SocialRevision pinned to source prompt and optional recipe/
editorial/asset versions, with seven platform outputs, character-limit results, template/model provenance,
status, acceptance, and staleness.
CONSTRAINT: SOC-001 and add-content-feature skill.
RESTRICTION: Accepted history is immutable. Social copy never becomes canonical recipe data. Provider-
specific scheduling IDs do not belong here.
BEHAVIOR: Show lifecycle/indexes, wait for approval, implement configuration/migration, review/apply it,
and test staleness and workspace scoping.
```

### 13.2 Seven-output social generation

```text
SCOPE: Implement SOC-001 through the shared AI lifecycle for Instagram, TikTok, Pinterest, Facebook, X,
Threads, and Blog Intro using selected prompt/recipe/content versions, channel, optional theme, and voice.
CONSTRAINT: SOC-001; add-ai-capability and add-content-feature skills.
RESTRICTION: Platform limits are deterministic validators. Do not invent recipe facts, search metrics,
image content, or hashtags outside configured policy. Source version is pinned.
BEHAVIOR: Show output schema and platform rules, wait for approval, implement task/endpoint with evaluation
fixtures for limits, claims, missing source, injection, staleness, and workspace isolation.
```

### 13.3 Social Studio UI

```text
SCOPE: Build SOCIAL-UI-001 through 004: choose saved/pasted prompt or brief, channel/theme, generate,
review seven outputs independently, show counts/limit status, edit, copy, and download individual/full
packages.
CONSTRAINT: creatorpantry-design-system, new-component, and add-content-feature skills.
RESTRICTION: Pasted/uploaded text is untrusted and does not become canonical source automatically.
Copy/download uses accepted edits, not stale raw generation.
BEHAVIOR: Plan state and accessibility, wait for approval, implement with component/contract tests and
run design-review.
```

### 13.3a Content Pipeline social step

```text
SCOPE: Extend only the Content Pipeline after keeper selection with source/version summary, optional
theme/voice selection, seven-output generation, independent review/edit/copy, and accepted package state.
CONSTRAINT: PIPE-UI-005 and SOC-001; creatorpantry-design-system and add-content-feature skills.
RESTRICTION: No DAM/card commit or scheduling. Generated outputs do not appear accepted automatically.
BEHAVIOR: Show state transitions, wait for approval, implement component/contract/a11y/error tests.
```

### 13.4 Content-board entities

```text
SCOPE: Add workspace-owned ContentCard, CardMoveHistory, RecipeContentPackageLink, and stable ordered
columns ToDo, InProcess, ReadyForReview, Approved with optional recipe/prompt/asset/social lineage.
CONSTRAINT: KAN-001 through KAN-006 and DATA-004; add-workspace-entity and add-content-feature skills.
RESTRICTION: Movement history is immutable. Ordering is deterministic. Do not add publishing provider
state to ContentCard.
BEHAVIOR: Show aggregate/index/concurrency design, wait for approval, implement configuration/migration,
review/apply it, and test invariants.
```

### 13.5 Content-board query and detail

```text
SCOPE: Implement KAN-001/002 list/detail through the full seam with channel/day/exact-date filters,
column/order sorting, move count, lineage, movement history, and publishing-summary projection.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Server pagination/limits where needed. Do not load image bytes or provider payloads. Cross-
workspace and unknown cards are indistinguishable.
BEHAVIOR: Plan projections and contract, wait for approval, implement with filter/order/detail/isolation
tests.
```

### 13.6 Create content card

```text
SCOPE: Implement only KAN-003 create with title, column, channel, description, platform, date, lineage,
food metadata, actor, derived day, initial movement record, next order, and idempotency.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No patch/move/delete. Validate every linked record belongs to the workspace.
BEHAVIOR: Plan transaction, wait for approval, implement replay/rollback/role/isolation tests.
```

### 13.6a Update content card

```text
SCOPE: Implement only KAN-004 submitted-field update with optimistic concurrency, date/day derivation,
metadata persistence, and explicit prompt unlinking.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No move/reorder or silent lineage unlink. Unsubmitted fields remain unchanged.
BEHAVIOR: Plan patch semantics, wait for approval, implement partial/clear/conflict/isolation tests.
```

### 13.6b Move or reorder content card

```text
SCOPE: Implement only KAN-005 move/reorder with target column/order, optimistic concurrency, note/actor,
and movement history only for cross-column transition.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Reordering within a column does not create false movement history. Resolve order atomically.
BEHAVIOR: Plan ordering transaction, wait for approval, implement move/reorder/conflict/isolation tests.
```

### 13.6c Delete content card

```text
SCOPE: Implement only KAN-006 delete with dependent move/publishing record policy and explicit optional
linked-asset action.
CONSTRAINT: add-endpoint, add-content-feature, and add-media-feature skills.
RESTRICTION: Never silently delete a DAM asset. Unknown/cross-workspace are indistinguishable.
BEHAVIOR: Plan cascade/asset behavior, wait for approval, implement policy/replay/isolation tests.
```

### 13.7 Recipe-to-board package

```text
SCOPE: Implement RCPUB-006 idempotently: create a board card linked to exact approved recipe, editorial,
social, and asset versions plus channels and desired publishing window.
CONSTRAINT: RCPUB-006; add-content-feature and add-recipe-feature skills.
RESTRICTION: Do not duplicate source records or flatten them into an untraceable blob. A NeedsReview
source creates a blocked package unless explicitly allowed by policy.
BEHAVIOR: Plan package validation and idempotency, wait for approval, implement with stale, missing-link,
replay, transaction-failure, and isolation tests.
```

### 13.7a Content Pipeline keeper commit

```text
SCOPE: Add only the final pipeline commit action that idempotently saves the selected staged image to DAM,
saves prompt lineage, links the accepted social package, and creates one linked board package/card.
CONSTRAINT: PIPE-UI-006, DAM-001, PRM-001, RCPUB-006.
RESTRICTION: Partial failure must resume/compensate without duplicate asset, prompt, or card. No scheduling.
BEHAVIOR: Show persisted-operation/compensation plan, wait for approval, implement failure-point/replay tests.
```

### 13.8 Content Board UI

```text
SCOPE: Build BOARD-UI-001 through 005 with four columns, accessible drag/drop and keyboard alternative,
deterministic reorder, create/detail/edit/delete, history, lineage, social action, and publishing status.
CONSTRAINT: creatorpantry-design-system, new-component, and add-content-feature skills.
RESTRICTION: Optimistic movement must reconcile or roll back on conflict. Do not encode state-transition
rules only in Angular. No color-only status.
BEHAVIOR: Plan interaction and conflict recovery, wait for approval, implement in atomic slices with
component/a11y/API tests and run design-review.
```

### 13.9 Publishing profile and Buffer adapter

```text
SCOPE: Implement PUB-001 and INT-030: typed Buffer gateway, normalized profile ServiceModel, configured
credential handling, transient resilience, and profile retrieval through the full seam.
CONSTRAINT: .claude/rules/publishing.md and external.md; add-external-integration and add-endpoint skills.
RESTRICTION: Provider DTOs/IDs stay in integration records. Do not expose credentials or raw provider
errors. Missing configuration is a stable degraded result.
BEHAVIOR: Verify current Buffer API contract, show mapping/error plan, wait for approval, implement with
fake/recorded contract tests and no live call in default CI.
```

### 13.10 Publishing ledger entity and migration

```text
SCOPE: Add workspace-owned PublishingRecord with card/profile/platform/content/media/schedule, status,
external update ID, attempt/uncertainty metadata, actor, timestamps, idempotency, and concurrency token.
CONSTRAINT: DATA-005/006 and .claude/rules/publishing.md; add-workspace-entity skill.
RESTRICTION: Provider IDs live only in integration records. No schedule/cancel/provider call yet.
BEHAVIOR: Show state/index/retention design, wait for approval, implement migration, review/apply, test.
```

### 13.10a Schedule Buffer post

```text
SCOPE: Implement only PUB-002 schedule command: validate card/profile/platform/text/media/future UTC and
approval, create pending ledger idempotently, call adapter, and record external ID or failure/uncertainty.
CONSTRAINT: INT-031 through 033; add-external-integration and add-endpoint skills.
RESTRICTION: No SQL transaction across provider call. Replay creates no second post. Never guess timeout outcome.
BEHAVIOR: Show state/compensation plan, wait for approval, implement success/replay/reject/timeout/isolation tests.
```

### 13.10b Cancel Buffer post

```text
SCOPE: Implement only PUB-003 cancellation for one publishing record, requesting upstream cancellation
when applicable and recording cancelled, failed, or uncertain state idempotently.
CONSTRAINT: INT-032/033; add-external-integration and add-endpoint skills.
RESTRICTION: Repeated cancel is safe. Do not mark cancelled when provider outcome is unknown.
BEHAVIOR: Plan state transitions, wait for approval, implement pending/sent/already-cancelled/timeout/isolation tests.
```

### 13.10c Publishing reconciliation worker

```text
SCOPE: Add only the Worker job that polls/reconciles pending or uncertain publishing records and applies
idempotent state transitions with attempt/backoff policy.
CONSTRAINT: DATA-RQ-031; add-background-job and add-external-integration skills.
RESTRICTION: Revalidate workspace/profile ownership; no duplicate submit; bounded retry and poison state.
BEHAVIOR: Show claim/reconcile matrix, wait for approval, implement restart/replay/rate-limit/isolation tests.
```

### 13.10d Board publishing controls UI

```text
SCOPE: Add only profile selection, schedule form, validation, submit progress, status, cancel, uncertain-
outcome explanation, and retry guidance to board card detail.
CONSTRAINT: BOARD-UI-004/005; creatorpantry-design-system and new-component skills.
RESTRICTION: UI cannot declare success/cancelled before server confirmation or expose provider errors/secrets.
BEHAVIOR: Plan states/copy, wait for approval, implement component/contract/a11y tests.
```

### 13.10e Publishing audit

```text
SCOPE: Audit Buffer adapter, ledger, schedule, cancel, reconciliation, board controls, and workspace scope.
CONSTRAINT: PUB-001 through 003 and INT-030 through 033.
RESTRICTION: Report first; do not convert uncertain state into success/failure for convenience.
UTILIZATION: integration-reviewer, workspace-isolation-auditor, api-contract-checker, test-gap-analyzer.
BEHAVIOR: Return findings, wait for approval, fix blockers, rerun recorded contract/integration/UI tests.
```

### 13.11 Dashboard read model

```text
SCOPE: Add one workspace-scoped dashboard query/read model for recipes in progress, readiness blockers,
recent AI proposals, test follow-ups, upcoming content, board totals, recent uploads, and publishing status.
CONSTRAINT: HOME-UI-001/002 and PERF-001; add-endpoint skill.
RESTRICTION: Read-only projection; no new source of truth, unbounded histories, binary bytes, or cross-workspace
aggregation. Omit metrics not yet backed by implemented data.
BEHAVIOR: Show query/source/index plan, wait for approval, implement endpoint and isolation/performance tests.
```

### 13.12 Dashboard UI

```text
SCOPE: Replace the placeholder home route with the CreatorPantry dashboard using the Phase 13.11 read model,
design-system cards/statuses, actionable blocker links, loading, empty, partial/degraded, error, and success.
CONSTRAINT: HOME-UI-001/002; creatorpantry-design-system and new-component skills.
RESTRICTION: No client-side recomputation of authoritative counts and no decorative chart without decision value.
BEHAVIOR: Plan hierarchy/responsive states, wait for approval, implement component/contract/a11y tests; design-review.
```

## Phase 13A — Guided Workflow Hub and daily/weekly work planner

> Product workflows guide the creator through existing domain capabilities. They do not create a second recipe, content, media, or publishing model. Personal work planning is separate from editorial Content Board state.

### 13A.1 Workflow definition contract

```text
SCOPE: Define the versioned code/configuration contract for WorkflowDefinition and WorkflowStepDefinition:
stable key/version, plain-language goal, description, expected outcome, step key/title/help, required or
optional state, capability link, prerequisites, completion rule, and next-step suggestions.
CONSTRAINT: The easy-as-pie product rule and .claude/rules/content.md.
RESTRICTION: No executable arbitrary expression, database entity, UI, or duplicated domain validation.
Definitions orchestrate existing capabilities and remain versioned with the application.
BEHAVIOR: Show contract and one example workflow, wait for approval, implement validation and version tests.
```

### 13A.2 Workflow run entities

```text
SCOPE: Add workspace-owned WorkflowRun and WorkflowStepRun with definition key/version, owner, title,
status, current step, progress, started/updated/completed timestamps, last route, domain resource links,
step draft pointer, skip/completion metadata, and concurrency token.
CONSTRAINT: add-workspace-entity and add-content-feature skills; DATA-RQ-001.
RESTRICTION: Store workflow state and stable pointers, not duplicate recipe/article/media bodies or secret
prompt/provider data. A run is private to its workspace and optionally assigned user.
BEHAVIOR: Show lifecycle/indexes/ownership, wait for approval, implement entities/configuration only.
```

### 13A.3 Workflow run migration

```text
SCOPE: Generate the migration for WorkflowRun and WorkflowStepRun with WorkspaceId-leading indexes,
definition/version index, user/status/update indexes, and unique step key per run.
CONSTRAINT: .claude/rules/backend.md and tenancy.md.
RESTRICTION: Do not apply before review. No cascade that deletes linked domain resources or audit history.
BEHAVIOR: Show migration/rollback, wait for approval, apply through MigrationService, verify model clean.
```

### 13A.4 Workflow catalogue

```text
SCOPE: Register and validate the initial workflow catalogue: Create a recipe, Improve a recipe, Test and
approve, Create my brand voice, Write a blog post and SEO package, Create food images, Create social posts,
Organize assets, Plan and publish content, and Plan my day/week.
CONSTRAINT: The easy-as-pie product rule and WorkflowDefinition contract.
RESTRICTION: Define goal/steps/prerequisites/outcome only. No screen, persistence, or domain operation.
Every step must point to an implemented capability or be explicitly marked unavailable.
BEHAVIOR: Show the catalogue and step count/copy, wait for approval, implement definitions and validation tests.
```

### 13A.5 Start workflow run

```text
SCOPE: Implement one command/endpoint to start a selected workflow definition with optional starting
resource, creator-supplied title, owner, and idempotency key, returning first actionable step and route.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Validate resource/workspace/role and prerequisites. Do not execute the first capability or
invent a missing resource automatically unless the definition explicitly declares safe creation.
BEHAVIOR: Show contract/prerequisite results, wait for approval, implement start/replay/invalid/isolation tests.
```

### 13A.6 Get or resume workflow run

```text
SCOPE: Implement read/resume for one workflow run, resolving its pinned definition version, current step,
completed/skipped steps, linked resources, last route, recommended next action, and recoverable warnings.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No mutation on read. Unknown/cross-workspace/unassigned access follows authorization policy.
Do not silently upgrade an in-progress run to a newer definition.
BEHAVIOR: Plan ServiceModel, wait for approval, implement current/completed/old-definition/isolation tests.
```

### 13A.7 Autosave workflow step draft

```text
SCOPE: Implement one debounced/idempotent step-draft save operation using the step's declared typed draft
contract or stable pointer to an existing domain draft, with concurrency and last-saved timestamp.
CONSTRAINT: UX-005, API-005; add-endpoint and add-content-feature skills.
RESTRICTION: Do not store arbitrary untyped JSON when a domain draft exists, claim success before commit,
or overwrite a newer edit. Private draft content never lives only in browser storage.
BEHAVIOR: Show draft strategy per step type, wait for approval, implement save/replay/conflict/failure tests.
```

### 13A.8 Complete workflow step

```text
SCOPE: Implement one command to mark the current step complete after server-side completion-rule evaluation,
record linked result IDs, update progress/current step atomically, and return next recommended action.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Workflow completion cannot bypass domain approval/readiness/publishing rules. A client boolean
is never proof of completion. Replay is idempotent.
BEHAVIOR: Show rule evaluation/transaction, wait for approval, implement incomplete/success/replay/isolation tests.
```

### 13A.9 Back workflow step

```text
SCOPE: Implement only Back navigation to the previous permitted step with current draft preserved and
deterministic current-step update.
CONSTRAINT: easy-as-pie product rule; add-content-feature skill.
RESTRICTION: Never delete linked resources or bypass definition rules.
BEHAVIOR: Show rule, wait for approval, implement first-step/draft/conflict/replay tests.
```

### 13A.9a Skip optional workflow step

```text
SCOPE: Implement only Skip for a definition-declared optional step with reason, audit metadata, and next step.
CONSTRAINT: easy-as-pie product rule; add-content-feature skill.
RESTRICTION: Required steps cannot skip; skipping never fabricates a capability result.
BEHAVIOR: Show rule, wait for approval, implement optional/required/replay/conflict tests.
```

### 13A.9b Start workflow over

```text
SCOPE: Implement only Start over with explicit confirmation, archive/history preservation for the old run,
and idempotent creation/reset behavior according to approved policy.
CONSTRAINT: easy-as-pie product rule; add-content-feature skill.
RESTRICTION: Do not delete linked recipes/content/assets or silently discard unsaved draft.
BEHAVIOR: Show policy/transaction, wait for approval, implement confirm/cancel/replay/isolation tests.
```

### 13A.10 Save and exit workflow

```text
SCOPE: Implement only SaveAndExit with persisted current draft, last route, updated timestamp, and resumable status.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Do not mark complete/abandoned or lose a committed step draft.
BEHAVIOR: Show transaction, wait for approval, implement save/failure/replay/resume tests.
```

### 13A.10a Complete workflow run

```text
SCOPE: Implement only CompleteRun after server verification that every mandatory step completion rule passes.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Client progress/checkboxes are not proof; no linked-domain mutation on completion.
BEHAVIOR: Show verification, wait for approval, implement incomplete/success/replay/isolation tests.
```

### 13A.10b Abandon workflow run

```text
SCOPE: Implement only AbandonRun with explicit confirmation, optional reason, audit metadata, and retained links.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Do not delete linked resources or confuse abandon with complete.
BEHAVIOR: Show transition, wait for approval, implement confirm/replay/conflict/isolation tests.
```

### 13A.11 Workflow run list and progress query

```text
SCOPE: Implement a paged query for My active, Recently completed, Needs attention, and All workflow runs,
with definition type, owner, progress, current step, linked resource, last updated, and next action.
CONSTRAINT: add-endpoint skill; PERF-001.
RESTRICTION: No domain bodies or binary data in list results. Server paging and workspace/user scope required.
BEHAVIOR: Show projection/index/filter plan, wait for approval, implement filter/paging/isolation tests.
```

### 13A.12 Shared guided-workflow shell

```text
SCOPE: Build a reusable workflow shell with goal/outcome heading, step list/progress, one primary action,
Back, optional Skip, Save and exit, help, last-saved state, error recovery, and resume routing.
CONSTRAINT: The easy-as-pie product rule; creatorpantry-design-system and new-component skills.
RESTRICTION: Shell renders orchestration only; it does not embed domain forms, duplicate validation, or
require drag/pointer interaction. Advanced controls remain collapsed by default.
BEHAVIOR: Show component/state/keyboard/responsive design, wait for approval, implement loading/error/
offline-retry/conflict/resume/reduced-motion/a11y tests.
```

### 13A.13 Workflow Hub screen

```text
SCOPE: Build the Workflow Hub with plain-language “What do you want to accomplish?” cards, Continue where
you left off, Needs attention, Recent outcomes, quick search, and role/availability-aware actions.
CONSTRAINT: The easy-as-pie product rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No technical capability names, model/prompt jargon, decorative metrics, or dead workflow card.
Unavailable actions explain the prerequisite and link to it.
BEHAVIOR: Show information hierarchy and first-use/returning/error states, wait for approval, implement
component/contract/a11y tests and run design-review.
```

### 13A.14 “Create a recipe” guided workflow screen

```text
SCOPE: Compose the existing recipe concept/manual start, structured draft, ingredient review, instruction
review, timing/yield, calculation preview, save, and next-action capabilities into the shared workflow shell.
CONSTRAINT: REC-001, AIREC-001/002, ING-001 through ING-006; easy-as-pie rule.
RESTRICTION: Orchestration only—no second recipe editor, calculation implementation, or AI mutation path.
The user may choose “Start from scratch” or “Help me draft it” without seeing prompt jargon.
BEHAVIOR: Show step-to-capability/state map, wait for approval, implement one step screen per atomic turn,
then add save/resume/back/reload/end-to-end tests.
```

### 13A.15 “Improve a recipe” guided workflow screen

```text
SCOPE: Compose recipe selection/version, improvement goal, scoped AI revision or manual edit, substitutions/
adaptation, calculation previews, quality review, diff, acceptance, and save into the shared shell.
CONSTRAINT: AIREC-003 through AIREC-008 and REC-004; easy-as-pie rule.
RESTRICTION: No unscoped “make it better” mutation or auto-accept. Show exactly what will change and why.
BEHAVIOR: Show step map and safe defaults, wait for approval, implement one step screen per turn with
resume/stale-version/reject/accept/end-to-end tests.
```

### 13A.16 “Test and approve” guided workflow screen

```text
SCOPE: Compose test-run setup, observations/issues, correction link, quality/safety review, readiness
blockers, and permitted approval transition into the shared shell.
CONSTRAINT: TESTRUN-001 through TESTRUN-005; easy-as-pie rule.
RESTRICTION: Workflow progress cannot waive blockers or mark approval before the domain transition succeeds.
BEHAVIOR: Show step map, wait for approval, implement one step screen per turn with blocker/resume/a11y/E2E tests.
```

### 13A.17 “Create my brand voice” guided workflow screen

```text
SCOPE: Register the Phase 11A setup wizard inside the shared workflow shell with goal, source examples,
questionnaire, extraction review, guide proposal/manual edit, test drive, and explicit activation.
CONSTRAINT: Phase 11A and easy-as-pie rule.
RESTRICTION: Reuse Phase 11A screens/contracts; do not fork a second guide wizard or activate implicitly.
BEHAVIOR: Show integration/state map, wait for approval, implement resume/deep-link/error/E2E tests.
```

### 13A.18 “Write a blog post and SEO package” guided workflow screen

```text
SCOPE: Compose recipe/version selection, active brand guide, audience/goal, editorial proposal review/edit,
SEO proposal review/edit, JSON-LD validation, preview, and export into the shared shell.
CONSTRAINT: RCPUB-001 through RCPUB-004 and Phase 11A; easy-as-pie rule.
RESTRICTION: No automatic acceptance/publication or hidden stale source. Advanced SEO controls are optional.
BEHAVIOR: Show step map/defaults, wait for approval, implement one step screen per turn with resume/stale/
no-guide/export/end-to-end tests.
```

### 13A.19 “Create food images” guided workflow screen

```text
SCOPE: Compose recipe/brief selection, active visual guide, photography concept, prompt review, optional
reference analysis, variant generation, keeper selection, DAM save, and recipe asset link into the shell.
CONSTRAINT: IMG-001 through IMG-006, DAM-001, RCPUB-005, Phase 11A; easy-as-pie rule.
RESTRICTION: Show expected cost before live generation, preserve creator-edited prompt, and do not expose
provider/storage details.
BEHAVIOR: Show step/cost/recovery map, wait for approval, implement one step screen per turn with cancel/
partial-failure/resume/save/end-to-end tests.
```

### 13A.20 “Create social posts” guided workflow screen

```text
SCOPE: Compose source selection, active guide/channel variant, theme, seven-output generation, independent
review/edit, accepted package, asset choice, and add-to-board action into the shared shell.
CONSTRAINT: SOC-001, RCPUB-006, Phase 11A; easy-as-pie rule.
RESTRICTION: No automatic scheduling or silent acceptance. Show platform limits in plain language.
BEHAVIOR: Show step map, wait for approval, implement one step screen per turn with limit/stale/resume/E2E tests.
```

### 13A.21 “Organize assets” guided workflow screen

```text
SCOPE: Compose upload or staged-item selection, metadata, recipe/prompt/brand lineage, version choice,
tags, utilization, review, and save into the shared shell.
CONSTRAINT: DAM-001 through DAM-010; easy-as-pie rule.
RESTRICTION: No duplicate asset model, raw blob path, or silent replacement of original bytes.
BEHAVIOR: Show step map/defaults, wait for approval, implement one step screen per turn with validation/
conflict/resume/end-to-end tests.
```

### 13A.22 “Plan and publish content” guided workflow screen

```text
SCOPE: Compose content package selection, readiness/staleness check, board placement, profile/platform,
copy/media preview, schedule time in user zone, explicit confirmation, status, and cancellation guidance.
CONSTRAINT: KAN-001 through KAN-006 and PUB-001 through PUB-003; easy-as-pie rule.
RESTRICTION: No publish without explicit confirmation or provider outcome assumption. Show UTC only as
secondary diagnostic detail, not the primary scheduling language.
BEHAVIOR: Show step/state/uncertain-outcome map, wait for approval, implement one step screen per turn with
timezone/validation/replay/timeout/resume/end-to-end tests.
```

### 13A.23 Personal work-task entity and migration

```text
SCOPE: Add workspace-owned WorkTask with owner/assignee, title, optional description, status, priority,
scheduled day, planning week start, due date, estimate, labels, position, recurrence/template pointer,
linked workflow/step/recipe/card/asset IDs, created/completed timestamps, and concurrency token.
CONSTRAINT: add-workspace-entity and add-content-feature skills.
RESTRICTION: WorkTask tracks the person's work, not editorial approval or publishing state. Linked domain
records remain authoritative. No task endpoint or UI yet.
BEHAVIOR: Show status model/indexes/link rules, wait for approval, implement migration, review/apply, test.
```

### 13A.24 Quick-capture work task

```text
SCOPE: Implement a drop-dead-simple create command requiring only a title, defaulting owner, workspace,
Backlog status, and current planning context; accept optional day/week/priority/link details.
CONSTRAINT: API-005 and easy-as-pie rule; add-endpoint skill.
RESTRICTION: Do not require workflow, recipe, due date, estimate, or labels. Replay creates one task.
BEHAVIOR: Show minimal/expanded contract, wait for approval, implement default/replay/role/isolation tests.
```

### 13A.25 Daily task query

```text
SCOPE: Implement a Today query returning overdue, scheduled today, in progress, waiting, completed today,
and optional suggested-next tasks for the authenticated user with deterministic ordering.
CONSTRAINT: add-endpoint skill; PERF-001.
RESTRICTION: Suggestions are labelled and do not silently schedule tasks. No other user's private tasks
without an approved manager policy. Server applies user's time zone.
BEHAVIOR: Show projection/time-zone/order rules, wait for approval, implement midnight/DST/isolation tests.
```

### 13A.26 Weekly task query

```text
SCOPE: Implement a selected-week query grouping unscheduled backlog, each local day, in progress, waiting,
and completed with capacity totals where estimates exist.
CONSTRAINT: add-endpoint skill; API-009 and PERF-001.
RESTRICTION: Missing estimates remain unknown; do not fabricate capacity or mix editorial board columns.
BEHAVIOR: Show week-boundary/order rules, wait for approval, implement locale/week-start/DST/isolation tests.
```

### 13A.27 Edit work task

```text
SCOPE: Implement submitted-field update for title, description, priority, schedule/week, due date, estimate,
labels, assignee where authorized, and links with optimistic concurrency.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No move/reorder/complete or linked-domain mutation. Unsubmitted fields stay unchanged.
BEHAVIOR: Plan patch semantics, wait for approval, implement partial/clear/conflict/link/isolation tests.
```

### 13A.28 Move work task

```text
SCOPE: Implement only task status/group move with target, deterministic position, optimistic concurrency,
and idempotency.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Does not complete linked workflow/domain state or reorder unrelated groups.
BEHAVIOR: Show transaction, wait for approval, implement valid/invalid/conflict/replay/isolation tests.
```

### 13A.28a Reorder work task

```text
SCOPE: Implement only within-group task reorder with deterministic positions and optimistic concurrency.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No status change or linked-resource mutation.
BEHAVIOR: Show ordering strategy, wait for approval, implement first/last/concurrent/isolation tests.
```

### 13A.28b Complete or reopen work task

```text
SCOPE: Implement only complete/reopen with timestamps, previous planning context, optimistic concurrency,
and idempotency.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: Task completion does not complete a linked workflow step unless independently satisfied.
BEHAVIOR: Show transitions, wait for approval, implement complete/reopen/replay/conflict/isolation tests.
```

### 13A.29 Create task from workflow step

```text
SCOPE: Implement an explicit “Add to my plan” command that creates or links one WorkTask from a workflow
step with suggested title, estimate, desired day/week, and deep link back to the run.
CONSTRAINT: easy-as-pie rule; add-content-feature skill.
RESTRICTION: Never create a task silently for every step. Require user confirmation and make replay
idempotent. Workflow and task statuses remain independent.
BEHAVIOR: Show suggestion/link/idempotency design, wait for approval, implement confirm/replay/isolation tests.
```

### 13A.30 Recurring task template

```text
SCOPE: Add optional workspace-owned WorkTaskTemplate plus recurrence materialization for common daily/
weekly routines with next occurrence, local time zone, default fields, active state, and idempotent Worker.
CONSTRAINT: add-workspace-entity and add-background-job skills; API-009.
RESTRICTION: No natural-language recurrence in this prompt. Materialization never duplicates an occurrence
or edits completed tasks when a template changes.
BEHAVIOR: Show recurrence boundary/DST rules, wait for approval, implement migration and replay/DST tests.
```

### 13A.31 Quick-capture task UI

```text
SCOPE: Add a globally available quick-capture interaction requiring one title field and one Save action,
with optional progressive disclosure for day/week, priority, estimate, label, and resource link.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No modal maze or mandatory metadata. Preserve typed title on recoverable failure and announce
success without moving focus unexpectedly.
BEHAVIOR: Show one-field/expanded/error states, wait for approval, implement component/contract/a11y tests.
```

### 13A.32 “My Day” focus screen

```text
SCOPE: Build a focused daily view with overdue, Today, In progress, Waiting, and Done today; quick capture;
start/resume deep links; simple complete/move controls; and optional “Choose my top three” emphasis.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: No dense project-management dashboard, hidden task, drag-only action, or duplicated workflow
state. The primary view answers “What should I do next?”
BEHAVIOR: Show hierarchy/mobile/keyboard states, wait for approval, implement component/contract/a11y tests.
```

### 13A.33 “My Week” Kanban screen

```text
SCOPE: Build an accessible weekly Kanban with Backlog, each configured day or a compact This Week view,
In progress, Waiting, and Done; keyboard move/reorder alternative; capacity summary; filters; quick capture;
and deep links to workflows/resources.
CONSTRAINT: easy-as-pie rule; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not reuse editorial Content Board columns as personal task state. Optimistic movement must
roll back or reconcile on conflict; no color-only status or drag-only operation.
BEHAVIOR: Show desktop/mobile/keyboard interaction and column-density plan, wait for approval, implement
one board interaction per turn with component/contract/a11y/conflict tests.
```

### 13A.33a Dashboard workflow and work-plan integration

```text
SCOPE: Extend the existing dashboard read model and UI with Continue workflow, Needs attention, Today,
This week, Overdue, and Quick capture summaries plus deep links to the exact workflow step or task.
CONSTRAINT: HOME-UI-001/002 and easy-as-pie product rule.
RESTRICTION: Summaries are bounded and read-only; do not duplicate task/workflow source data or turn the
dashboard into another Kanban board.
BEHAVIOR: Show projection/card priority and first-use/returning states, wait for approval, implement
endpoint/component/performance/a11y/isolation tests.
```

### 13A.34 Workflow Hub and planner integration audit

```text
SCOPE: Audit every workflow screen, run/step persistence, autosave/resume, task links, My Day, My Week,
dashboard entry points, plain-language copy, default choices, progressive disclosure, keyboard use,
mobile behavior, failure recovery, and workspace isolation.
CONSTRAINT: The easy-as-pie product rule and UX-001 through UX-006.
RESTRICTION: Observe/report before fixes. Do not solve simplicity findings by hiding required safety,
approval, provenance, cost, or error information.
UTILIZATION: design-review, workspace-isolation-auditor, api-contract-checker, architecture-reviewer,
test-gap-analyzer, and playwright-cli.
BEHAVIOR: Run first-use and returning-user usability journeys, count decisions/clicks per workflow, return
blocker/high/medium/low findings, wait for approval, fix in atomic turns, and rerun end-to-end tests.
```

## Phase 14 — Dietary, allergen, nutrition, and food-safety evidence

### 14.1 Evidence-source gateway

```text
SCOPE: Add a provider-neutral gateway and internal reference contract for approved nutrition, density,
allergen, dietary, and food-safety evidence, recording source/version/license/retrieval date.
CONSTRAINT: AI-009/010, DATA-RQ-007, DATA-011; add-external-integration skill.
RESTRICTION: Do not scrape or import an unapproved source. Provider records cannot become EF/domain/HTTP
types directly. No analysis endpoint yet.
BEHAVIOR: Confirm the approved source and license decision, show mapping/cache plan, wait for approval,
implement with recorded contract fixtures and missing-source behavior.
```

### 14.2 Ingredient-to-nutrition mapping

```text
SCOPE: Add workspace-aware creator-confirmed mapping records from normalized recipe ingredients to
external nutrition references with match method, confidence, quantity basis, source version, override,
and unresolved state.
CONSTRAINT: ING-008 and DATA-RQ-007; add-workspace-entity and add-recipe-feature skills.
RESTRICTION: Low-confidence matches require confirmation. Never replace ingredient display text or
silently share tenant-specific overrides.
BEHAVIOR: Show mapping lifecycle/indexes, wait for approval, implement migration and tests for automatic
candidate, confirmation, rejection, remap, stale source, and isolation.
```

### 14.3 Deterministic nutrition calculator

```text
SCOPE: Implement ING-008 arithmetic from confirmed ingredient mappings, canonical quantities, edible
yield policy, and recipe yield; return whole-recipe/per-serving estimates, source coverage, confidence,
and unmapped items.
CONSTRAINT: ING-008, CALC-001/006; .claude/rules/recipes.md.
RESTRICTION: AI performs no nutrition arithmetic. Do not publish results when required mappings are
unresolved. Label all output as estimate with method/source.
BEHAVIOR: Show calculation/rounding/missing-data rules, wait for approval, implement golden and
property-based tests for scaling, servings, partial coverage, and extremes.
```

### 14.4 Dietary and allergen analyzer

```text
SCOPE: Implement ING-007 deterministic analysis of confirmed/reference traits against selected profiles,
returning matched evidence, possible hidden sources, unknowns, cross-contact disclaimer, and conflicts.
CONSTRAINT: ING-007; .claude/rules/recipes.md and ai.md.
RESTRICTION: Never label a recipe certified safe. Missing data is unknown, not absent. Model commentary
may explain results but cannot change deterministic findings.
BEHAVIOR: Present decision table, wait for approval, implement fixtures for positive, negative, unknown,
conflicting, hidden-source, and cross-contact cases.
```

### 14.5 High-risk cooking-method warnings

```text
SCOPE: Add evidence-backed deterministic detection and warnings for canning, fermentation, curing, sous
vide, raw proteins, internal temperatures, pregnancy/medical-diet language, and unsupported safety claims.
CONSTRAINT: AIREC-GR-005 and AI-009/010.
RESTRICTION: This is not a general safety guarantee. Each warning cites an approved reference or says
that verification is required. Do not block ordinary recipes with invented rules.
BEHAVIOR: Show trigger/evidence matrix, wait for approval, implement with positive/negative/ambiguous and
source-unavailable tests.
```

### 14.6 Dietary/allergen analysis endpoint

```text
SCOPE: Expose only ING-007 as POST .../analysis/dietary-allergen for selected recipe version/profiles.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Read-only; return evidence, confidence, unknowns, disclaimer, and evaluated source version.
BEHAVIOR: Plan contract, wait for approval, implement profile/evidence/unknown/isolation tests.
```

### 14.6a Nutrition analysis endpoint

```text
SCOPE: Expose only ING-008 as POST .../analysis/nutrition for selected recipe version and yield basis.
CONSTRAINT: add-endpoint and add-recipe-feature skills.
RESTRICTION: Read-only; unresolved mappings remain explicit and may block publish. Label estimates.
BEHAVIOR: Plan contract, wait for approval, implement mapping/coverage/yield/isolation tests.
```

### 14.6b Ingredient mapping confirmation command

```text
SCOPE: Add only the creator-confirmation/rejection/remap command for one ingredient-to-reference candidate.
CONSTRAINT: ING-008; add-endpoint and add-recipe-feature skills.
RESTRICTION: No bulk silent confirmation or display-text replacement. Require version/concurrency context.
BEHAVIOR: Plan contract, wait for approval, implement confirm/reject/remap/conflict/isolation tests.
```

### 14.6c Dietary/allergen review UI

```text
SCOPE: Build only dietary/allergen evidence, confidence, hidden-source, unknown, conflict, and disclaimer UI.
CONSTRAINT: ING-007 and AI-UI-002; creatorpantry-design-system and new-component skills.
RESTRICTION: No “safe” certification badge or nutrition UI.
BEHAVIOR: Show wording/state plan, wait for approval, implement component/contract/a11y tests.
```

### 14.6d Nutrition mapping and estimate UI

```text
SCOPE: Build only nutrition mapping review/confirm/reject plus per-recipe/per-serving estimates, coverage,
sources, unresolved items, method, and readiness effect.
CONSTRAINT: ING-008; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not hide estimates, low confidence, or missing mappings; no client arithmetic.
BEHAVIOR: Plan states/copy, wait for approval, implement component/contract/a11y tests; run design-review.
```

### 14.7 Food-domain safety audit

```text
SCOPE: Audit substitution, adaptation, quality review, readiness, nutrition, allergen, dietary, and
high-risk method behavior for evidence, uncertainty, wording, deterministic routing, and source licenses.
CONSTRAINT: AIREC-GR-005, AI-009/010, ING-007/008.
RESTRICTION: Report first. Do not weaken warnings or fabricate a source to close a finding.
UTILIZATION: ai-safety-reviewer, integration-reviewer, test-gap-analyzer, and architecture-reviewer.
BEHAVIOR: Return blocker/high/medium/low findings with exact evidence, wait for approval, fix blockers,
rerun domain/evaluation tests, and publish the reviewed source/version matrix.
```

## Phase 15 — Hardening, observability, accessibility, and performance

### 15.1 Unified ProblemDetails and validation contract

```text
SCOPE: Standardize RFC 9457 responses, stable application error codes, validation details, correlation
ID, retryability, concurrency conflict payload, and degraded-provider results across every controller.
CONSTRAINT: API-002/003; .claude/rules/api-contract.md.
RESTRICTION: No stack trace, provider body, secret, storage path, SQL detail, or cross-workspace existence
signal. Do not change successful ServiceModel contracts.
BEHAVIOR: Inventory current responses, show target matrix, wait for approval, implement centrally, and
add contract tests per error family.
```

### 15.2 Upload and identifier security pass

```text
SCOPE: Centralize file signature/size/dimension/decoded-format/malware-policy checks plus safe object-key
generation and identifier ownership verification for briefs, reference images, DAM uploads, and test media.
CONSTRAINT: SEC-004/005 and API-003/010; .claude/rules/media.md.
RESTRICTION: Extension/MIME alone is insufficient. No caller object path. Never serve soft-deleted or
cross-workspace objects.
BEHAVIOR: Show control matrix, wait for approval, implement or consolidate, and run malicious/polyglot,
traversal, oversized, corrupt, duplicate, and isolation tests.
```

### 15.3 Rate limits, quotas, and cost controls

```text
SCOPE: Add configured per-user/workspace limits for AI, image, upload, export, search, slug availability,
and publishing operations with clear retry/reset responses and telemetry.
CONSTRAINT: SEC-006, AI-006, PERF-004.
RESTRICTION: Do not implement billing/plans. Limits cannot leak another workspace's usage. Background
workers honor the same approved operation budget.
BEHAVIOR: Present limit table and storage strategy, wait for approval, implement with burst, reset,
concurrency, worker, and isolation tests.
```

### 15.4 Distributed telemetry and business metrics

```text
SCOPE: Propagate correlation/trace context across Web/Gateway/API/SQL/Redis/blob/Worker/AI/image/nutrition/
Buffer; add structured metrics for operation latency/failure, proposal disposition, orphan cleanup,
readiness, exports, publishing, and estimated provider cost.
CONSTRAINT: NFR-001 through NFR-007 and AI-004/005.
RESTRICTION: No private recipe/prompt/generated content, secrets, tokens, raw provider bodies, or high-
cardinality user data in telemetry.
UTILIZATION: aspire-monitoring.
BEHAVIOR: Show signal inventory and redaction rules, wait for approval, implement, and demonstrate one
trace per major workflow plus metrics in the Aspire dashboard.
```

### 15.5 Performance and index verification

```text
SCOPE: Define workload fixtures and measure recipe/DAM/board/proposal/test searches, calculation previews,
operation polling, and export queues; inspect SQL plans and add only evidence-backed indexes/limits.
CONSTRAINT: PERF-001 through PERF-004.
RESTRICTION: No speculative caching or index explosion. Binary bytes do not enter list queries. Preserve
WorkspaceId-leading indexes.
BEHAVIOR: Record baseline p50/p95 and query plans, show proposed changes, wait for approval, implement,
rerun, and report before/after evidence.
```

### 15.6 Accessibility and usability sweep

```text
SCOPE: Verify every route/workflow for keyboard-only use, focus order/visibility, labels, contrast,
status announcements, dialog focus, drag alternatives, error recovery, text alternatives, unsaved edits,
and generated-versus-approved distinctions.
CONSTRAINT: UX-001 through UX-006 and WCAG 2.2 AA.
RESTRICTION: Report before changing shared components. Do not patch accessibility with page-specific CSS
when the defect belongs in the design system.
UTILIZATION: design-review, playwright-cli, and test-gap-analyzer.
BEHAVIOR: Produce a route-by-route matrix, wait for approval, fix shared defects first, add regression
tests, and rerun keyboard/screen-reader automation where available.
```

### 15.7 Consolidated security and architecture audit

```text
SCOPE: Audit auth/BFF, workspace isolation, layer boundaries, data ownership, AI tools, uploads, external
adapters, background jobs, caches, erasure paths, audit events, and secrets. Report only before fixes.
CONSTRAINT: SEC-001 through SEC-008, DATA-RQ-001 through 009, and all .claude/rules.
RESTRICTION: Findings require file/line or executable-test evidence. Unverifiable items go under “Not
verifiable here.” Do not accept a green single-workspace happy path as isolation proof.
UTILIZATION: architecture-reviewer, workspace-isolation-auditor, ai-safety-reviewer,
integration-reviewer, api-contract-checker, code-reviewer, skills-evals, and test-gap-analyzer.
BEHAVIOR: Return one deduplicated report ordered by blocker/severity, wait for approval, fix in atomic
turns, and rerun every affected audit and test suite.
```

## Phase 16 — Delivery, recovery, and release

### 16.1 CI quality pipeline

```text
SCOPE: Add CI for restore/build, formatting, backend unit/integration tests, Angular tests/build,
accessibility checks, deterministic AI evaluations, contract tests, migration drift, secret scanning,
dependency scanning, and selected end-to-end tests.
CONSTRAINT: DEL-001 through DEL-003 and TEST-008/010.
RESTRICTION: Default CI performs no paid live-provider or production call. No secret in workflow files.
Do not mark flaky tests allowed-to-fail to obtain green.
BEHAVIOR: Show job graph/cache/artifact plan, wait for approval, implement, and produce one successful
local-equivalent or branch run with artifact inventory.
```

### 16.1a End-to-end release journeys

```text
SCOPE: Add deterministic browser/API end-to-end journeys for registration/sign-in, workspace creation,
manual recipe authoring/versioning, AI draft/partial accept/reject/stale conflict, calculations, test run/
approval, brand source upload/extraction/guide edit/activation, brand-informed writing/image prompts,
workflow start/save/exit/resume/complete, quick task capture, My Day/My Week Kanban, editorial/export,
image pipeline/DAM, social/board, schedule/cancel, and two-workspace denial.
CONSTRAINT: TEST-005, TEST-008 through 010; playwright-cli skill.
RESTRICTION: Use fake/recorded providers in default CI. Do not collapse journeys into one order-dependent mega
test or bypass Gateway/UI for browser acceptance paths.
UTILIZATION: playwright-cli and test-gap-analyzer.
BEHAVIOR: Show independent journey/fixture map, wait for approval, implement one journey per turn, add CI
selection, and report runtime/flakiness evidence.
```

### 16.2 Infrastructure as code and environment configuration

```text
SCOPE: Provision the approved Azure topology for AppHost-derived services, Azure SQL, Redis, blob,
secret store, telemetry, identities, networking, and configured AI resources using the selected IaC tool.
CONSTRAINT: DEL-005 and .claude/rules/aspire.md; aspire-deployment skill.
RESTRICTION: No production apply in this prompt. No credential literals or public data stores. Keep
environment differences in configuration, not forked templates.
UTILIZATION: aspire-deployment.
BEHAVIOR: Confirm target topology and cost-sensitive choices, show plan, wait for approval, generate IaC,
validate/preview it, and report resources without deploying.
```

### 16.3 Deployment and migration workflow

```text
SCOPE: Add versioned artifact promotion, explicit pre-deploy database migration, health-gated rollout,
rollback, prompt-template/version deployment, and worker drain/restart behavior.
CONSTRAINT: DEL-004/006; aspire-deployment skill.
RESTRICTION: Production app startup does not auto-migrate. Rollback never assumes a destructive schema
downgrade is safe. A prompt/model version is part of release identity.
BEHAVIOR: Show deployment/rollback sequence, wait for approval, implement pipeline definitions and test
against a disposable environment or dry-run path.
```

### 16.4 Retention and purge policy jobs

```text
SCOPE: Implement only approved retention/purge jobs for soft-deleted assets, staged images, rejected AI
proposals, expired operations, old exports, archived workflow runs/tasks, retired brand document/guide
versions, extracted artifacts, embeddings, and orphaned blobs with legal-hold checks.
CONSTRAINT: SEC-008, DATA-RQ-009, INT-023; add-background-job skill.
RESTRICTION: Stop if retention periods are undecided. Jobs are idempotent and never cross workspace or
purge held/current records.
BEHAVIOR: Present policy matrix, wait for approval, implement dry-run plus boundary/replay/isolation tests.
```

### 16.4a User/workspace export and erasure

```text
SCOPE: Implement only authorized export and erasure workflow for user/workspace private SQL rows, blobs,
embeddings, cache entries, job payloads, and searchable derivatives with durable progress/audit evidence.
CONSTRAINT: SEC-008 and DATA-RQ-009; add-background-job skill.
RESTRICTION: No direct synchronous bulk delete. Preserve records required by approved legal hold. Prove
Workspace B remains intact.
BEHAVIOR: Show inventory/state/rollback limits, wait for approval, implement two-workspace export/erasure tests.
```

### 16.4b Backup and point-in-time restore drill

```text
SCOPE: Configure and execute one documented SQL/blob backup and point-in-time restore drill into an
isolated recovery environment, then verify referential integrity and workspace boundaries.
CONSTRAINT: SEC-008 and DEL-006.
RESTRICTION: Do not restore over production or claim success from configuration alone. No secret in report.
BEHAVIOR: Show drill plan/RPO/RTO assumptions, wait for approval, run safely, capture evidence and gaps.
```

### 16.5 Operator runbooks and alert verification

```text
SCOPE: Write and exercise runbooks for provider outage, stuck AI/image operation, orphan blob, SQL/blob
inconsistency, failed schedule/uncertain Buffer outcome, secret rotation, database saturation, restore,
workspace erasure, and cost anomaly.
CONSTRAINT: DEL-007 and NFR-007.
RESTRICTION: A runbook must name signals, diagnosis, containment, recovery, verification, and escalation;
do not write generic prose disconnected from implemented dashboards/commands.
BEHAVIOR: Draft runbook index and owners, wait for approval, write one at a time, tabletop each against
the actual system, and record gaps as issues.
```

### 16.6 Final release gate

```text
SCOPE: Audit the complete CreatorPantry release against every requirement ID, open decision, migration,
API contract, Angular route, evaluation, provider contract, accessibility check, runbook, and checkpoint.
Report only; fix nothing until findings are approved.
CONSTRAINT: creatorpantry-ai-augmented-recipe-development-requirements.md and all repository rules.
RESTRICTION: Every “pass” needs file/test/runtime evidence. Anything dependent on unavailable credentials
or environment is “Not verified,” never assumed. Blockers remain blockers.
UTILIZATION: architecture-reviewer, workspace-isolation-auditor, ai-safety-reviewer, integration-reviewer,
api-contract-checker, design-review, skills-evals, code-reviewer, and test-gap-analyzer.
BEHAVIOR: Produce a requirement-traceability matrix and one deduplicated severity report; wait for
approval; fix findings in separate atomic prompts; rerun the gate and publish the final evidence summary.
```

---

# Part 2 — Operational SCRUB templates

Copy one block, replace bracketed values, delete inapplicable lines, and keep the requested change atomic.

## Template A — One backend seam

```text
SCOPE: Implement only [entity | repository method | DataLayer operation | Business rule | Facade
operation | controller endpoint] for [requirement ID and outcome].
CONSTRAINT: CLAUDE.md, .claude/rules/backend.md, and [nearest domain rule]; use [matching skill].
RESTRICTION: Touch only [allowed projects/files]. Do NOT pull forward the next layer, change unrelated
contracts, add dependencies, or weaken workspace/content/AI invariants.
UTILIZATION: [matching skill]; code-reviewer after implementation.
BEHAVIOR: Inspect the existing adjacent pattern, show the exact change/test plan, wait for approval,
implement, run focused tests, run the reviewer, and summarize files plus evidence.
```

## Template B — One endpoint

```text
SCOPE: Add [HTTP method/path] for [one elementary process] through Controller → Facade → Business →
DataLayer → Repository | Gateway.
CONSTRAINT: .claude/rules/api-contract.md and backend.md; add-endpoint skill; [requirement ID].
RESTRICTION: Controller injects only Facade. No EF/provider types at the boundary. No client WorkspaceId.
No adjacent CRUD operation. If workspace-owned, unknown and inaccessible are indistinguishable.
UTILIZATION: add-endpoint; api-contract-checker; workspace-isolation-auditor when applicable.
BEHAVIOR: Show request/result/error contracts and per-layer responsibilities, wait for approval,
implement with focused layer tests and one endpoint isolation test, then review and report.
```

## Template C — Workspace-owned entity and migration

```text
SCOPE: Add [entity] as workspace-owned data for [requirement], including EF configuration and a generated
migration.
CONSTRAINT: .claude/rules/tenancy.md and backend.md; add-workspace-entity skill.
RESTRICTION: Required WorkspaceId, convention filter, server-side stamp, WorkspaceId-leading indexes,
and ownership immutability. Create migration but do NOT apply until reviewed. No IgnoreQueryFilters.
UTILIZATION: add-workspace-entity; workspace-isolation-auditor and code-reviewer.
BEHAVIOR: Defend the data-zone choice, show entity/index/delete behavior and migration, wait for approval,
apply through MigrationService, test both workspaces, and confirm no pending model changes.
```

## Template D — Recipe mutation

```text
SCOPE: Add [one recipe mutation] for [requirement ID], producing [draft | new immutable version | state
transition] with explicit source version and concurrency behavior.
CONSTRAINT: .claude/rules/recipes.md and tenancy.md; add-recipe-feature skill.
RESTRICTION: Preserve creator-entered text. No historical version update, silent last-write-wins,
cross-workspace reference, or derivative rewrite. Deterministic work stays deterministic.
UTILIZATION: add-recipe-feature; workspace-isolation-auditor and test-gap-analyzer.
BEHAVIOR: Show invariant, transaction, version, conflict, and staleness behavior; wait for approval;
implement with success/no-op/conflict/rollback/isolation tests; report.
```

## Template E — AI capability

```text
SCOPE: Add [one AI task] for [requirement ID] using the existing AiOperation/AiProposal lifecycle,
versioned prompt file, structured output schema, and explicit disposition flow.
CONSTRAINT: .claude/rules/ai.md and [domain rule]; add-ai-capability skill.
RESTRICTION: No arbitrary prompt endpoint, provider SDK in domain code, model-supplied WorkspaceId,
unvalidated output, model-calculated deterministic value, silent mutation, or private prompt-body log.
UTILIZATION: add-ai-capability; ai-safety-reviewer, workspace-isolation-auditor, and test-gap-analyzer.
BEHAVIOR: Show schema/template/context/evaluation/acceptance plan, wait for approval, implement with fake
provider and adversarial fixtures, run reviewers and evaluations, and report provenance/version.
```

## Template F — External integration

```text
SCOPE: Add [one provider operation] through a typed Gateway and stable internal contract for [requirement].
CONSTRAINT: .claude/rules/external.md and [media | publishing | ai] rule; add-external-integration skill.
RESTRICTION: Provider DTOs/IDs stay at the boundary. No credential in source/log/output. Apply timeout,
bounded transient retry, rate-limit handling, cancellation, idempotency, and auditable outcome. No live
provider call in default CI.
UTILIZATION: add-external-integration; integration-reviewer and code-reviewer.
BEHAVIOR: Verify current official provider contract, show mapping/error/resilience plan, wait for
approval, implement with recorded/fake contract tests, run reviewers, and report.
```

## Template G — Background job

```text
SCOPE: Add [one durable job] for [requirement] with persisted state, lease/claim, retry, cancellation,
idempotency, and recovery.
CONSTRAINT: .claude/rules/backend.md, tenancy.md, and external.md; add-background-job skill.
RESTRICTION: Re-resolve workspace before Facade call. No provider call in SQL transaction. Duplicate
delivery produces no duplicate effect. Shutdown leaves accurate recoverable state.
UTILIZATION: add-background-job; architecture-reviewer and workspace-isolation-auditor.
BEHAVIOR: Show state machine and failure matrix, wait for approval, implement with competing-worker,
restart, replay, poison, cancellation, and isolation tests, then review.
```

## Template H — Design-system UI slice

```text
SCOPE: Implement only [one screen state/component/interaction] for [requirement] using existing
CreatorPantry tokens, primitives, recipes, and patterns.
CONSTRAINT: .claude/rules/frontend.md and design-system.md; creatorpantry-design-system and new-component
skills.
RESTRICTION: No HttpClient in components, literal brand colors, duplicated backend rule, unapproved
primitive, color-only meaning, inaccessible drag-only action, or unrelated screen work.
UTILIZATION: creatorpantry-design-system and new-component; design-review and api-contract-checker.
BEHAVIOR: Inspect the approved design package, show component/state/accessibility plan, wait for approval,
implement loading/empty/error/success/degraded states and tests, run reviewers, and report.
```

## Template I — Defect repair

```text
SCOPE: Reproduce and fix [one defect], then add the smallest regression test that proves the root cause.
CONSTRAINT: All nearest .claude/rules and the existing architectural seam.
RESTRICTION: Minimal root-cause change only. Do not refactor adjacent code, suppress symptoms, bypass a
domain invariant, or expand scope without re-planning.
UTILIZATION: aspire-monitoring when runtime evidence helps; test-gap-analyzer and code-reviewer after.
BEHAVIOR: Reproduce first, present evidence and root-cause hypothesis, wait for approval, fix, add the
regression test, run focused/broader suites, and report before/after behavior.
```

## Template J — Refactor with stable behavior

```text
SCOPE: Refactor [target] to [goal] without changing externally observable behavior. List every intended
file first.
CONSTRAINT: Repository rules and existing ServiceModel/API contracts.
RESTRICTION: No schema, route, response, domain-rule, authorization, tenancy, prompt, or visual change.
Do not remove pass-through layers, query filters, idempotency, or transaction callbacks as “unnecessary.”
UTILIZATION: architecture-reviewer, api-contract-checker, code-reviewer, and skills-evals.
BEHAVIOR: Return impact map and characterization-test plan, wait for approval, refactor in small green
steps, stop if blast radius grows, then run reviewers and summarize.
```

## Template K — Migration review

```text
SCOPE: Review the generated migration [name] for [model change]. Do not apply it yet.
CONSTRAINT: .claude/rules/backend.md and tenancy.md.
RESTRICTION: Report destructive changes, locks, backfill needs, WorkspaceId/index mistakes, cascade risk,
rollback limits, and deployment ordering. Do not hand-edit generated code before the model is corrected.
UTILIZATION: architecture-reviewer and code-reviewer.
BEHAVIOR: Show model diff, SQL/migration operations, forward/rollback/deployment plan, wait for approval,
then apply through MigrationService and confirm pending model changes are clean.
```

## Template L — Release gate

```text
SCOPE: Audit [release/slice] against [requirement IDs]. Report only; make no edits yet.
CONSTRAINT: Read the named requirement and repository rule sources before evaluating.
RESTRICTION: Every finding/pass needs file, line, test, trace, rendered UI, or runtime evidence. Put
unavailable external/environment checks under “Not verified.” Deduplicate overlapping findings.
UTILIZATION: Select the relevant read-only reviewers; include workspace-isolation-auditor for private
data and ai-safety-reviewer for generated content.
BEHAVIOR: Return blockers first, then high/medium/low, plus traceability and not-verified sections. Wait
for approval before any fix.
```

---

# Part 3 — Traceability and completion controls

## Requirement-to-phase map

| Requirements | Primary phase |
| --- | --- |
| REC-001 through REC-004 | Phase 5 |
| REC-005 through REC-009 | Phase 6 |
| ING-001 through ING-006 and CALC-001 through CALC-006 | Phase 7 |
| AIREC lifecycle and AIREC-GR-001 through 008 | Phase 8 |
| AIREC-001 through AIREC-008 | Phase 9 |
| TESTRUN-001 through TESTRUN-005 | Phase 10 |
| RCPUB-001 through RCPUB-004 | Phase 11 |
| BRAND-001 through BRAND-010 | Phase 11A |
| SEED-001; IMG-001, IMG-002, IMG-003, IMG-004, IMG-005, IMG-006; PRM-001 through PRM-005; DAM-001 through DAM-010 | Phase 12 |
| SOC-001; KAN-001, KAN-002, KAN-003, KAN-004, KAN-005, KAN-006; PUB-001 through PUB-003; RCPUB-006 | Phase 13 |
| FLOW-001 through FLOW-004; TASK-001 through TASK-005; EASE-001 through EASE-006 | Phase 13A |
| ING-007, ING-008, AI-009, AI-010 | Phase 14 |
| API, SEC, NFR, PERF, UX | Phase 15 |
| DEL and release-wide TEST requirements | Phase 16 |

Cross-cutting coverage index:

- Decisions: DEC-001, DEC-002, DEC-003, DEC-004, DEC-005, DEC-006, DEC-007, DEC-008, DEC-009, DEC-010, DEC-011, DEC-012 — Phase 0 plus the owning feature phase.
- Application UI: APP-UI-001, APP-UI-002, APP-UI-003, APP-UI-004 — Phases 3 and 15.
- Recipe UI: RECIPE-UI-001, RECIPE-UI-002, RECIPE-UI-003 and EDITOR-UI-001, EDITOR-UI-002, EDITOR-UI-003, EDITOR-UI-004, EDITOR-UI-005 — Phases 5 through 7.
- AI UI: AI-UI-001, AI-UI-002, AI-UI-003, AI-UI-004, AI-UI-005, AI-UI-006 — Phases 8 and 9.
- AI controls: AI-001, AI-002, AI-003, AI-004, AI-005, AI-006, AI-007, AI-008, AI-009, AI-010, AI-011, AI-012 and AIREC-GR-001, AIREC-GR-002, AIREC-GR-003, AIREC-GR-004, AIREC-GR-005, AIREC-GR-006, AIREC-GR-007, AIREC-GR-008 — Phases 8, 9, and 14.
- API controls: API-001, API-002, API-003, API-004, API-005, API-006, API-007, API-008, API-009, API-010 — Phases 0, 1, 5, 8, 12, and 15.
- Data groups: DATA-001, DATA-002, DATA-003, DATA-004, DATA-005, DATA-006, DATA-007, DATA-008, DATA-009, DATA-010, DATA-011 — Phases 2, 4, 5, 8, 10, 12, 13, and 14.
- Data integrity: DATA-RQ-001, DATA-RQ-002, DATA-RQ-003, DATA-RQ-004, DATA-RQ-005, DATA-RQ-006, DATA-RQ-007, DATA-RQ-008, DATA-RQ-009 — Phases 2, 5 through 8, 12, 14, and 16.
- Calculation controls: CALC-001, CALC-002, CALC-003, CALC-004, CALC-005, CALC-006 — Phase 7.
- Security: SEC-001, SEC-002, SEC-003, SEC-004, SEC-005, SEC-006, SEC-007, SEC-008 — Phases 1, 2, 12, 15, and 16.
- Reliability: NFR-001, NFR-002, NFR-003, NFR-004, NFR-005, NFR-006, NFR-007 — Phases 0, 2, 8, 13, and 15.
- Performance: PERF-001, PERF-002, PERF-003, PERF-004 — Phases 6, 12, 13, and 15.
- Testing: TEST-001, TEST-002, TEST-003, TEST-004, TEST-005, TEST-006, TEST-007, TEST-008, TEST-009, TEST-010 — embedded in each feature phase and consolidated in Phases 15 and 16.

## Definition of done for one microprompt

A prompt is complete only when:

- its one declared observable outcome exists;
- no excluded adjacent capability was pulled forward;
- focused tests pass;
- affected broader tests pass;
- workspace isolation is proven when private data is touched;
- API/UI contracts agree when a boundary changed;
- generated content remains visibly unapproved until acceptance;
- brand-informed generation records the exact BrandStyleGuideVersion and BrandContextPackage provenance;
- a multi-step workflow proves autosave, exit, resume, Back, failure recovery, and one recommended next action;
- user-facing copy avoids prompt/model/storage jargon and keeps advanced options progressively disclosed;
- personal WorkTask status remains independent from linked workflow, recipe, editorial, and publishing state;
- the named reviewer findings are resolved or explicitly recorded;
- documentation/traceability is updated when a contract or decision changed;
- the final report names files changed, commands/tests run, and remaining limitations.

## Final reminders

- A short prompt is safe only because the repository rules and skills carry the architecture.
- The reference recipe vertical is deliberately built before feature breadth.
- AI task handlers plug into one proposal lifecycle; they do not invent new mutation paths.
- Brand documents remain private blob-backed source material; SQL holds ownership, metadata, versions, and pointers.
- The creator's approved guide—not an AI inference—is the authoritative brand voice and visual style.
- Workflow screens orchestrate existing capabilities; they do not create a second domain model.
- My Day/My Week plan the creator's work; the Content Board tracks editorial content state.
- Calculation, authorization, readiness, and state transitions stay deterministic.
- A two-workspace test is mandatory evidence, not optional ceremony.
- The design system is a dependency of product UI, not a screenshot to approximate.
- If a prompt starts adding a schema, backend stack, provider, UI, and end-to-end suite at once, split it.
- Use `/rewind` or revert a bad atomic step instead of stacking corrective mega-prompts on top of it.
