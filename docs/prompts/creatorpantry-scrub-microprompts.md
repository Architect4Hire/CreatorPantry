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
- **The `- done` markers before Phase 8 are not a reliable completion record.** They are patchy — 4.1 through
  4.4 are unmarked while 4.5 through 4.8 are marked, and 4.6 through 4.8 carried the marker twice until it was
  cleaned up on 2026-09-25 — and nothing in phases 5 through 7 is marked at all despite that work being
  committed. Read the git history, not these markers, to find out what shipped. They are maintained from
  Phase 8 onward.

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
| USAGE-001 | Record every AI provider attempt against the Identity account that requested it, independently of the workspace the work was done in. |
| USAGE-002 | Keep account usage in a platform-scoped, counts-only ledger that carries no creator content and therefore needs no workspace query filter. |
| USAGE-003 | Maintain a per-account AI allowance with an explicit unit, period length, anchor, time zone, carry-over rule, and suspension flag, defaulting to platform configuration when no account quota is set. |
| USAGE-004 | Reserve allowance before a provider call and settle it against actual reported usage afterwards, releasing abandoned reservations. |
| USAGE-005 | Attribute usage to the account even when the provider reports no token counts, recording "not reported" rather than zero. |
| USAGE-006 | Refuse an over-quota or suspended account's AI request at admission, before the operation runs and before any provider call. |
| USAGE-007 | Tell a refused caller what is exhausted, what remains, and when the period resets, without disclosing another account or workspace. |
| USAGE-008 | Let a creator see their own current-period consumption, remaining allowance, reset time, and per-workspace and per-task breakdown across all their workspaces. |
| USAGE-009 | Let a platform administrator read any account's usage, set or clear a quota, suspend or restore AI access, and list top consumers, with an audit event for every change. |
| USAGE-010 | Present allowance in plain creator language with progress, warning, and exhausted states that never silently discard a filled-in request. |

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
| **9A.11** | One account's AI consumption and remaining allowance are visible and enforced across every workspace they belong to, and an exhausted account is refused before any provider call |
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

### 4.6 Reference seed set - done

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

### 4.7 Reference repository and read seam - done

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

### 4.8 Reference-domain audit - done

```text
SCOPE: Review all Phase 4 tables, migrations, APIs, and cache keys against the global/private data-zone
rules and recipe calculation prerequisites.
CONSTRAINT: .claude/rules/tenancy.md and recipes.md.
RESTRICTION: Report first. Do not turn global data into workspace copies to simplify tests.
UTILIZATION: architecture-reviewer, workspace-isolation-auditor, and test-gap-analyzer.
BEHAVIOR: Produce a finding list with file/line evidence, wait for approval, fix blockers, rerun tests,
and confirm pending model changes are clean.
```

## Phase 4A — Module-first domain restructure - Done

> Structural only. This phase moves files and namespaces. It changes no behavior, no schema, no HTTP contract, and no layer seam. Run it before Phase 5 so the reference vertical every later feature copies is built in its final layout.

### 4A.1 Module boundary decision record

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

### 8.1 AI provider resources and abstractions - done

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

### 8.2 Versioned prompt-template loader - done

```text
SCOPE: Add a prompt-template store/loader for versioned files with declared template ID, semantic
version, required inputs, output schema version, safety class, and checksum.
CONSTRAINT: AI-001/002; .claude/rules/ai.md.
RESTRICTION: No large inline prompt strings in C#. Templates cannot access secrets or arbitrary files.
Loading a missing/invalid template fails before provider invocation.
BEHAVIOR: Show file convention and validation, wait for approval, implement with checksum, version,
missing-input, and invalid-manifest tests.
```

### 8.3 AI operation entity and state machine - done

*Delivery note, now closed: "no migration yet" here and in 8.4 left the EF model ahead of the migration
history, and the phase 5–7 SQL Server fixtures added since this sequence was written call `MigrateAsync`,
which refuses on `PendingModelChangesWarning`. Between 8.3 and 8.5 that meant 122 failing tests on one cause
and an `aspire run` that could not start. 8.5 closed it; the suite is green. **If this sequence is ever run
again, expect that window and do not chase it as a regression** — and note that the SQLite-backed model tests
cannot see a multiple-cascade-path defect, which is why 8.5 found one that 8.3 and 8.4 had both passed.*

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

### 8.4 AI proposal and structured-change entities - done

```text
SCOPE: Add workspace-owned AiProposal, AiStructuredChange, AiWarning, AiExecutionMetadata, and
AiProposalFeedback linked to operation/source version with schema/template/provider/model provenance.
CONSTRAINT: DATA-008, AIREC-GR-001/007; add-workspace-entity and add-ai-capability skills.
RESTRICTION: A proposal never stores executable SQL, file paths, credentials, or unvalidated arbitrary
commands. Store only approved diagnostic content according to privacy policy.
BEHAVIOR: Plan record boundaries and retention, wait for approval, implement configuration and immutable
fields without migration.
```

### 8.5 AI proposal migration - done

```text
SCOPE: Generate the migration for AI operations, proposals, changes, warnings, execution metadata, and
feedback with WorkspaceId-leading indexes and idempotency/source-version constraints.
CONSTRAINT: .claude/rules/backend.md, tenancy.md, and ai.md.
RESTRICTION: Do NOT apply before review. No cascade from recipe deletion that destroys retained audit
records before policy permits it.
BEHAVIOR: Show migration and retention implications, wait for approval, apply through MigrationService,
and confirm pending model changes are clean.
```

### 8.6 Structured-output validator - done

```text
SCOPE: Add a versioned JSON-schema validation and deserialization boundary for model output, followed by
domain-level checks that return stable failure categories.
CONSTRAINT: AI-003, AIREC-GR-001; .claude/rules/ai.md.
RESTRICTION: Raw model JSON never reaches Business or persistence. Do not repair malformed output by
guessing; bounded retry may ask the provider for schema correction.
BEHAVIOR: Show validation stages, wait for approval, implement with valid, extra-field, wrong-version,
truncated, hostile-string, and domain-invalid fixtures.
```

### 8.7 Untrusted-context envelope - done

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

### 8.8 AI execution telemetry and resilience - done

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

### 8.9 AI operation repository and durable job claim - done

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

### 8.10 Generic AI proposal request/status endpoints - done

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

### 8.11 Server-calculated proposal diff - done, except snapshot loading (8.14)

*Delivery note: the mapping rules, the diff calculator and the staleness rule are complete and tested. Loading
the pinned version's snapshot is not here — `IRecipeFacade` exposes no read for one, the existing data path is
keyed by version number rather than id, and 8.14's own scope says the worker "loads the exact authorized
recipe/version ... calculates diff". Adding a four-layer read to the recipe module from this prompt would have
been scope growth into another module; the calculator takes the snapshot as an argument instead.*

*Amended by 8.12: the ingredient mapping rules written here were for changes the recipe update seam cannot apply,
and have been removed. See 8.12's note for what replaced them.*


```text
SCOPE: Convert validated structured changes into a deterministic comparison against the exact source
RecipeVersion and store the server-calculated diff used for review.
CONSTRAINT: AIREC-GR-002 and REC-008; add-ai-capability and add-recipe-feature skills.
RESTRICTION: Never trust a model-provided before value or diff. If source content no longer matches the
pinned version, fail the operation; do not silently rebase.
BEHAVIOR: Show mapping rules, wait for approval, implement with forged-before, missing-field, reorder,
duplicate-change, and stale-source tests.
```

### 8.12 Proposal disposition and atomic acceptance - done

*Delivery note, in two parts.*

*First, the narrowing. 8.11's mapping rules let a proposal offer changes the recipe update seam has no way to
apply, which meant a creator could review a suggestion, accept it, and only then be told no. Two allow-lists now
prevent that, both documented where they live: `AiDiffFields` no longer lists ingredient fields (the recipe patch
contract has no ingredients field, and the entries to restore are named there for when it does), and the new
`AiChangeApplicability` restricts change kinds to the pairs the patch can express — `Set` on the recipe, an
instruction group or a step; `Remove`/`Move` on a group or step; `Add`/`Remove` on a tag. Adding an instruction
step is refused for a structural reason rather than a policy one: `AiStructuredChange` carries one value, and a
step is text, a note, a duration and a temperature. A tag is the one child whose whole content is one string.*

*Second, the contract. The disposition is addressed by the **request** id, the same one the `GET` takes, because
an operation has at most one proposal and one route segment meaning two identities would be a trap; the
`ProposalId` in the reply is for correlation. The confirmation is `acceptedChangeIds`, required for both accept
decisions — `AcceptAll` must name every change, which is what makes it a confirmation rather than a flag.
Accepted changes become an ordinary `CanonicalRecipePatch` and travel the same merge path a creator's own edit
travels, so nothing about the recipe domain can be skipped. The AI data layer owns an explicit
`CreateExecutionStrategy().ExecuteAsync` transaction and calls the recipe facade through a delegate, so the
version, the per-change dispositions, the feedback row, the status change and the audit entry commit together.
Replay compares the requested decision with the recorded one: the same decision answers without writing, and a
different one is a 409.*

*One defect fixed in passing: 8.10's error codes used underscores throughout (`ai.recipe_not_found`), and
`ProblemResults.StatusFor` reads the segment after the last dot — so the routes advertised 404 and returned 400.
The codes are now dotted (`ai.recipe.not_found`).*

*Third, what `ai-safety-reviewer` found, because most of it was real. The apply path runs no ViewModel validator
— deliberately, there is no ViewModel — and the consequence had not been thought through: every length, range and
format rule that lived only in `UpdateRecipeViewModelValidator` was unenforced for accepted changes. The worst of
it was `sourceUrl`, whose scheme check exists to keep a `javascript:` value out of something later rendered as a
link; that field is now not proposable at all, on the same grounds as an identifier — a model cannot know where a
recipe came from. The rest became `ProposedRecipeValues`, owned by the recipe module and called from both the diff
calculator and the apply path, so an over-long title is a refusal rather than a truncation error surfacing as a
500. A step temperature was the other real one: it was settable on a step with no temperature unit, which no
change row can supply, so accepting one could only ever fail at the database — the exact outcome
`AiChangeApplicability` exists to prevent. Also fixed: an inexpressible stored change now refuses instead of
throwing inside the transaction; the change tracker is cleared if the apply callback throws; the audit summary
counts accepted changes that carried a safety caution; and the reply's `recipeVersionNumber` documents its third
null case (a replay). The reviewer's remaining notes were on test coverage, and six end-to-end tests were added —
a step change, a tag addition, the temperature refusal, an over-long value, an inexpressible target, and accepting
onto an archived recipe.*

*Fourth, a collision worth recording. Work landing alongside this one added an ingredients patch field to the
recipe update seam, and the `MergeAsync` extraction above moved the body that consumes its resolved unit
dimensions out of that parameter's scope — `aspire run` failed to build on `CS0103: ingredientUnitDimensions`.
The fix threads the map through `MergeAsync`, with the AI path passing an empty one because no proposal can name
a unit. The larger consequence is that the premise for excluding ingredients from `AiDiffFields` has expired:
the patch can now express them, so the exclusion is a lag rather than a limit, and the remarks on
`AiDiffFields`, `AiChangeApplicability` and `ProposedRecipeTarget` now say so and name what restoring them
needs. That restoration is not done here.*

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

### 8.13 AI proposal review UI - done, unmounted

*Delivery note, in five parts.*

*First, what shipped and what did not. `features/ai/` holds `cp-ai-proposal-panel` and
`cp-ai-operation-status`, over `models/ai-proposal.models.ts` and `services/ai-proposal.service.ts`. Nothing
mounts them: 8.14's worker does not exist, `diagnostic` ships disabled, and 9.2a/9.4a are the named hosts. One
consequence to know — `tsconfig.app.json` compiles from `src/main.ts`, so `npm run build` does **not**
type-check an unmounted component. `npm test` does, through the specs, and it is the real gate until a host
lands. The panel takes `requestId` as a `model` and publishes a retry's new id through it, so a host that keeps
that id in the URL survives a refresh; the panel's own resume-from-id is what a refresh reduces to, and is
tested.*

*Second, "edit before accept", which the disposition contract cannot express. `AiProposalDispositionViewModel`
carries ids and nothing else, so an edited value has nowhere to go. An edited row is therefore left out of
`acceptedChangeIds` — the server records it declined — and emitted to the host as the creator's own wording, to
be applied through the ordinary recipe patch path. `changeEdited` carries `value: string | null`, and the null is
not a nicety: without a withdrawal event, a creator who rewrote a field and then ticked the suggestion instead
would have both written to it, the host's copy of their words and the accepted change. `dropEdit` is the single
place an edit is forgotten, precisely so no path can forget to say so.*

*Third, "cancellation", which the API does not offer. The controller has three actions — ask, read, decide — and
none of them cancels. The button therefore reads "Stop checking" and says the request keeps running; claiming to
cancel generation would be promising what the server cannot honour. Teardown aborts the read in flight, which is
what `watchStatus` returning an Observable buys.*

*Fourth, what the two reviewers found, because most of it was real and all of it is fixed. `ai-safety-reviewer`
and `design-review` independently found the same blocker: the disposition reply says what the *operation*
became, not what became of each *change*, so the rows kept their `Pending` disposition and reported "Not
decided" directly under a summary saying two suggestions had been kept. The fix is a re-read, not a local
mapping of the accepted ids — deriving the server's answer here is the one thing this panel must not do. Two
more honesty defects went with it: `unavailable` on a disposition asserted "nothing was saved" about a
transaction that may well have committed before the connection dropped (it now says unconfirmed, and checks),
and an edit announced "Saved with your own edits" before anything was saved (now future tense). Also fixed: a
403 on deciding no longer wipes a readable proposal off the screen; `Select all` no longer destroys typed
wording; an `Add` no longer reads "moves to"; a bare `temperatureValue` now says which scale it is in, since
`AiDiffFields` excludes the unit on purpose; the legend is derived from the kinds actually present, so the
defensive `warning` glyph cannot appear unexplained; `ensureLoaded` is called and an unknown role no longer
reads as a refusal; the provenance names the source version the "was" values came from; and one standing line
says that an unflagged suggestion has not been checked rather than letting an empty flag list imply it.*

*Fifth, two things found in passing and deliberately not fixed here. **The published contract misdescribes this
route's request body**: `AiProposalDispositionViewModel` and `AiProposalDispositionServiceModel` both strip to
`AiProposalDisposition` in `OpenApiDocumentation.PublicSchemaId`, so `openapi-v1.json` declares the disposition
request as the *response* shape and a generated client would send the wrong body. This panel is written against
the C# ViewModel and is correct. The fix — distinct ids for a colliding pair, plus a regenerated snapshot —
belongs in its own backend diff. **And `AI-UI-001` through `AI-UI-006` do not exist in this repository**: only
the ids appear, here. The panel was built against this prompt's RESTRICTION line plus
`.claude/rules/{ai,frontend,design-system}.md`, and is worth a read-back if the requirement text lives
somewhere else.*

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

## Phase 9A — Account AI usage accounting and quota

> Usage is measured **per account**, not per workspace. One person who belongs to five workspaces has one
> balance, and it is spent from whichever workspace they happen to be working in. The existing AI tables cannot
> answer that question: `AiExecutionMetadata` is `IWorkspaceOwned` and globally filtered, and `AiOperation`
> records `RequestedByMembershipId` — a workspace-scoped identity — so summing one person's tokens across
> workspaces from those rows would mean `IgnoreQueryFilters()`, which `tenancy.md` prohibits. This phase adds a
> platform-scoped ledger keyed by the Identity account instead, and the reason that ledger is safe to read with
> no workspace filter is that it holds **counts and never content**.

### 9A.1 Account usage module and ledger entity

```text
SCOPE: Add a platform-scoped Modules/AiUsage bounded context owning AccountAiUsageEntry: identity account id,
occurred-at UTC, the attributed AiOperation and attempt number, task type, provider, model, deployment, input
tokens, output tokens, total tokens, estimated cost, billable flag, usage-reported flag, outcome, and the
WorkspaceId the work was done in as a reporting dimension only.
CONSTRAINT: USAGE-001/002; .claude/rules/tenancy.md, ai.md, and backend.md; add-workspace-entity skill for the
configuration conventions only.
RESTRICTION: The entry is NOT IWorkspaceOwned and carries no global query filter — that is the whole point, and
it is defensible only because the row holds no recipe title, prompt body, proposal text, or any other creator
content, and no column may ever be added that does. The account id comes from Identity, never from a request
field, an unsigned header, or a model. Provider-reported token counts stay nullable: null is "not reported",
never zero.
BEHAVIOR: Show the entity, the counts-only column list, the account/period and account/workspace indexes, and
why this is deliberately not a filtered entity, wait for approval, implement entity and configuration only.
```

### 9A.2 Account quota policy and period model

```text
SCOPE: Add platform-scoped AccountAiQuota and AccountAiQuotaPeriod: the account, the quota unit, the allowance,
the period length and anchor, the account's time zone, the current period's consumed and reserved totals, the
carry-over rule, a suspension flag, effective-from/to, and the actor who last changed it.
CONSTRAINT: USAGE-003/004; .claude/rules/tenancy.md and auth.md.
RESTRICTION: A quota belongs to an account, never to a workspace and never to a membership — a creator does not
earn a fresh allowance by joining another workspace. Periods roll forward deterministically in the account's
stored zone and are never recomputed from "now". No default allowance is invented in domain code: an account
with no explicit quota resolves to the configured platform default.
BEHAVIOR: Show the period roll rule, the unit decision (raw tokens versus an internal credit), what happens to
an in-flight reservation at a period boundary, and the uniqueness indexes, wait for approval, implement
entities and configuration only.
```

### 9A.3 Account usage and quota migration

```text
SCOPE: Generate the migration for the usage ledger, quota, and period tables with account-leading indexes and
the uniqueness constraint that makes one attempt post at most one ledger entry.
CONSTRAINT: .claude/rules/backend.md and tenancy.md.
RESTRICTION: Do NOT apply before review. No cascade from workspace or membership deletion that erases an
account's usage history — the WorkspaceId on a ledger entry is a reporting dimension, not an owner, and
deleting a workspace does not unspend the tokens. Account deletion follows the documented erasure path and is
decided here rather than improvised later.
BEHAVIOR: Show the migration, the cascade decisions, and the retention implication, wait for approval, apply
through MigrationService, restart `aspire run`, and confirm pending model changes are clean.
```

### 9A.4 Usage recording at the execution boundary

```text
SCOPE: Post one ledger entry per provider attempt from the same code path that writes AiExecutionMetadata,
resolving the account id from the operation's membership server-side and recording tokens, cost estimate,
outcome, and the originating WorkspaceId.
CONSTRAINT: USAGE-001/005; AI-004 through AI-007; add-ai-capability skill; .claude/rules/ai.md.
RESTRICTION: Cross-module traffic is facade to facade: the Ai module calls the AiUsage facade and never its
repositories, and AiUsage never reads an AI or recipe table. The ledger write commits with the attempt record —
a settled attempt that posts no usage is a defect, and a duplicate delivery posts once. A failed, blocked,
cancelled, or usage-unreported attempt still posts an entry carrying its outcome, because "we do not know what
this cost" must be visible rather than absent.
BEHAVIOR: Show the attribution path from membership to account and the transaction boundary, wait for approval,
implement with success, retry, timeout, safety-blocked, unreported-usage, duplicate-delivery, and
two-workspaces-one-account tests.
```

### 9A.5 Admission check, reservation, and settlement

```text
SCOPE: Implement deterministic quota admission before any provider call: resolve the account's current period,
reserve an estimated amount, then settle that reservation against actual reported usage after the attempt,
releasing it on failure or lease expiry.
CONSTRAINT: USAGE-004/006; NFR-004/005; add-background-job and add-ai-capability skills.
RESTRICTION: A model never participates in this decision and never sees a balance. Reservation and settlement
are each one concurrency-safe transaction; two workers claiming work for the same account must not both be
admitted past the last of the allowance. An abandoned reservation expires with the operation lease rather than
stranding an account's balance. Settlement is idempotent: a replay settles once. No provider call inside a
database transaction.
BEHAVIOR: Show the reserve/settle/release state table and the estimation rule for a task whose tokens are not
yet known, wait for approval, implement with competing-worker, over-reserve, under-reserve, unreported-usage,
lease-expiry, replay, and period-boundary tests.
```

### 9A.6 Quota enforcement in the AI request seam

```text
SCOPE: Refuse an over-quota AI operation at request admission through the existing generic lifecycle, returning
ProblemDetails with a stable code, the period reset time, and the remaining allowance.
CONSTRAINT: USAGE-006/007; API-004/005; add-endpoint and add-ai-capability skills.
RESTRICTION: Refusal happens before the operation reaches Running and before a provider is called; a refused
request leaves no orphan operation and consumes no allowance. The refusal states what is exhausted and when it
resets, and never discloses another account's usage or another workspace's existence. A suspended account and
an exhausted period are distinguishable error codes. Workspace role grants no exemption — a Workspace Owner is
not a platform administrator.
BEHAVIOR: Plan the status code, error codes, and response fields, wait for approval, implement with at-limit,
over-limit, suspended, reset-boundary, replay-of-a-refused-request, and two-workspace isolation tests, then
regenerate the OpenAPI snapshot and read the diff.
```

### 9A.7 Backfill and reconciliation
```text
SCOPE: Add a one-time reconciliation that posts ledger entries for AiExecutionMetadata rows written before this
phase, plus a repeatable check reporting attempts with no matching entry.
CONSTRAINT: USAGE-002; .claude/rules/tenancy.md.
RESTRICTION: This is the one path here permitted to read execution metadata across workspaces, so it is a
documented `IgnoreQueryFilters()` carve-out: it selects identifiers, token counts, and outcome only — never a
title, snapshot, or any creator content — and its file is listed by path in
`BulkOperationBoundaryTests.Exemptions`. Reconciliation is idempotent, resumable, and never double-posts. It
reports drift; it does not silently rewrite a settled period's totals.
BEHAVIOR: Show the query, the exemption line, and the restart behaviour, wait for approval, implement with
partial-run, re-run, and drift-detection tests, then run workspace-isolation-auditor.
```

### 9A.8 Account usage read seam

```text
SCOPE: Add GET /api/v1/me/ai-usage and .../ai-usage/history returning the signed-in account's current period,
consumed and reserved totals, remaining allowance, reset time, and a breakdown by task type and by workspace
across every workspace the account belongs to.
CONSTRAINT: USAGE-008; API-001 through API-003; add-endpoint skill; .claude/rules/api-contract.md and auth.md.
RESTRICTION: This route is account-scoped and must not live under /api/v1/workspaces/{workspaceSlug} —
deliberately, because the answer spans workspaces. It returns the caller's own account only; an account id is
never accepted from route, body, query, or header. The per-workspace breakdown names only workspaces the
account currently holds active membership in, and carries counts, never recipe titles or operation detail. A
revoked membership leaves the historical total intact and withholds the workspace name.
BEHAVIOR: Plan the contract and the withheld-name rule, wait for approval, implement with
one-account-many-workspaces, revoked-membership, another-account's-id, and unauthenticated tests, then
regenerate the OpenAPI snapshot and run api-contract-checker.
```

### 9A.9 Platform quota administration

```text
SCOPE: Add PlatformAdmin-only ops routes to read any account's usage, set or clear an account quota, suspend or
restore an account's AI access, and list the highest-consuming accounts for a period.
CONSTRAINT: USAGE-009; baseline B-13/B-14; .claude/rules/auth.md and api-contract.md.
RESTRICTION: These are explicit `ops` routes behind the platform-admin policy and hashed rotatable API keys;
they are never proxied from a browser session, and PlatformAdmin never implies workspace membership. Every
quota change, suspension, and restoration writes an audit event naming actor, before and after values, and a
reason. An administrator sees numbers, accounts, and workspace identifiers — never a recipe, a prompt, a
proposal, or any workspace content.
BEHAVIOR: Plan routes, policy, and audit shape, wait for approval, implement with non-admin-forbidden,
workspace-owner-forbidden, audit-written, suspend-then-request-refused, and clear-quota-falls-back-to-default
tests.
```

### 9A.10 Usage and quota UI

```text
SCOPE: Build the account AI-usage surface — remaining allowance with reset date, consumed-versus-allowance
progress, per-workspace and per-task breakdown, an at-limit state — plus an in-context warning on AI request
screens when the allowance is nearly or fully spent.
CONSTRAINT: USAGE-008/010; EASE-004; creatorpantry-design-system and new-component skills.
RESTRICTION: Plain language and no infrastructure jargon: a creator reads AI credits, a reset date, and what
they can still do — not a raw token count as the headline figure, and never a provider, deployment, or model
name. Status is never conveyed by color alone. An exhausted allowance disables the request action with a stated
reason and a next step; it never silently fails a submit or discards a filled-in request. The screen shows only
the signed-in account's own figures.
BEHAVIOR: Plan states — loading, healthy, nearly spent, exhausted, suspended, degraded, error — wait for
approval, implement with component, contract, at-limit, reset-boundary, and accessibility tests.
```

### 9A.11 Account usage audit

```text
SCOPE: Verify that every provider attempt posts exactly one ledger entry, that account totals reconcile across
workspaces, that no cross-workspace read returns creator content, that quota is enforced before the provider
call, and that no account can read or spend another account's allowance.
CONSTRAINT: USAGE-001 through USAGE-010.
RESTRICTION: Report first. Do not resolve a finding by making the ledger workspace-owned, by widening the
`IgnoreQueryFilters()` exemption list, or by adding a content-bearing column to a table read without a
workspace filter.
UTILIZATION: workspace-isolation-auditor, ai-safety-reviewer, api-contract-checker, architecture-reviewer,
design-review, and test-gap-analyzer.
BEHAVIOR: Produce one deduplicated severity report, wait for approval, fix blockers, rerun the AI and tenancy
suites, and record the resulting per-account accounting guarantees.
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

### 11.1 Creator brand profile entity - done

```text
SCOPE: Add workspace-owned BrandProfile with brand name, short description, default audience, channel
defaults, locale, time zone, website/reference links, logo/asset pointers, and version/audit fields.
CONSTRAINT: DEC-010 and RCPUB-001/002; add-workspace-entity and add-content-feature skills.
RESTRICTION: Do not store voice, tone, tenor, writing style, or visual direction here; Phase 11A's
BrandStyleGuideVersion is their sole source of truth. No endpoint/UI/AI training.
BEHAVIOR: Show schema/index/version design, wait for approval, implement migration, review/apply, test.
```

### 11.1a Read brand profile - done

```text
SCOPE: Implement only the authorized profile read through the full request seam.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No update/create; unknown and cross-workspace are indistinguishable.
BEHAVIOR: Plan ServiceModel, wait for approval, implement found/empty/isolation tests.
```

### 11.1b Create or update brand profile - done

```text
SCOPE: Implement only create/update with submitted-field semantics, validation, version/audit record,
optimistic concurrency, and Editor policy.
CONSTRAINT: add-endpoint and add-content-feature skills.
RESTRICTION: No delete and no voice/style fields. Brand defaults cannot weaken platform safety policy.
BEHAVIOR: Plan patch/version semantics, wait for approval, implement validation/conflict/isolation tests.
```

*Delivery note: `POST`/`PATCH .../brand-profile` are live, with an immutable `BrandProfileRevisions` history and
audit entries. Four things were deliberately left for later prompts: a non-empty `assets` list answers
`422 brand.assets.unprocessable` until 12.10k can verify ownership through the DAM; `ChannelKey` is an opaque
string until 12.1 supplies the channel keys to validate against; `BrandProfile.TimeZoneId` is the workspace
scheduling zone that 13.10a, 13A.22 and 13A.30 read; and 11A.19 records the profile revision that each
generation used. Neither migration has been applied to a dev database by the prompt itself — restart
`aspire run`.*

### 11.1c Brand settings UI - done

```text
SCOPE: Build only settings UI for brand identity, description, default audience, locale/time zone, website/
reference links, logo/asset selection, and channel defaults using profile read/update contracts.
CONSTRAINT: creatorpantry-design-system and new-component skills. Run 12.1 first: channel defaults pick from
its channel configuration, so there is nothing to pick from before it. The time zone field is the workspace
scheduling zone.
RESTRICTION: No voice/style editing, AI generation, or model-training claim. Link users to Brand Style
Guide setup for voice, tone, tenor, writing, and visual direction. Logo selection is a disabled,
explained state here: the profile refuses asset links until 12.10k, which enables it.
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

### 11.10a Recipe approval in the editor - done

```text
SCOPE: Mount the existing readiness checklist and state-transition controls (10.7d) in the recipe editor as a
Readiness area, so a creator can move a recipe Draft → InDevelopment → Testing → ReadyForReview → Approved
from the browser and then export it. The endpoints (10.7, 10.7a) already exist; add no new route.
CONSTRAINT: TESTRUN-004/005 and TEST-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: Do not evaluate readiness or decide legal moves in the editor. Show the status the server last
reported, never the one a click hoped for. Do not offer the tab while the form has unsaved edits, because a
confirmed move replaces the form's state and would overwrite them.
BEHAVIOR: Hold the server's status beside the form, apply the returned recipe after a confirmed move, point the
Publish panel's "not approved" message at the Readiness tab, and test the tab, the unsaved-edits guard and the
applied result.
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

### 11A.18 Accept or partially accept a brand-guide proposal - done

*Delivery note, in four parts.*

*First, the gap this had to fill. 11A.15 — edit a guide and create a new version — was never implemented, so
`IBrandStyleGuideFacade` had `Get`/`ListVersions`/`CompareVersions`/`Create`/`ActivateVersion` and no way to
write a second version of anything. Acceptance therefore introduced one:
`CreateVersionFromProposalAsync`, the brand analogue of `IRecipeFacade.CreateFromProposalAsync`, taking a
`BrandStyleGuideProposalApplication` and deliberately not wrapped in `IIdempotentCommandExecutor` — that opens a
transaction of its own, which would nest and throw, and clears the change tracker, which would discard the
dispositions staged above it. It is proposal-only and not exposed over HTTP, so 11A.15's creator-typed edit can
land beside it rather than inherit a shape bent around AI.*

*Second, what "accept" means here, which the restriction did not settle. The handler flattens one piece of
guidance into an `Add` row carrying its text plus `Set` rows for its dimension, channel, evidence and citations,
so the unit a creator sees is the item and not the row: `acceptedChangeIds` names text rows, Business expands
the selection onto their companions before recording anything, and naming a companion alone is a `400`. Without
the expansion the stored record would have said a section was taken and its own dimension declined. `acceptAll`
still has to name every item, which is what keeps it a confirmation. The new version is the working version with
the accepted guidance laid over it — sections replace by `(SectionKey, ChannelKey)`, rules append and
deduplicate the way the create validator matches them, citations union — so everything the proposal did not speak
to survives; `BrandStyleGuideVersionMerge` is that rule and is unit-tested on its own. A conflict or an
uncertainty is a finding about the creator's own material, so it stays selectable, produces nothing, and is
counted in `droppedItemCount`. Accepted guidance identical to what the guide already said writes **no version at
all**, the same no-op rule a creator's own edit follows.*

*Third, the two stalenesses, which get different answers. A guide edited since the proposal was composed is
refused with `brand.guide.workingVersion.conflict`, checked inside the transaction and naming both version
numbers: laying the guidance over the newer version is the rebase the restriction forbids, and branching from
the pinned one would discard the edit in between. A cited source document replaced since is **not** refused —
the citation is written at the version the proposal actually read, never re-pointed, and `staleSourceCount`
reports it, which is what activation already gates on. The mapping from a citation to that provenance is the
brand module's, through a new `IBrandSourcePassageFacade.ResolveOriginsAsync` that reads superseded chunk sets
too, because a pinned version has to stay resolvable after the document moves on.*

*Fourth, two things found rather than planned. The OpenAPI snapshot caught a real defect:
`OpenApiDocumentation.StripLayerSuffix` drops `ViewModel` and `ServiceModel` alike, so a request called
`AiBrandGuideAcceptanceViewModel` beside `AiBrandGuideAcceptanceServiceModel` claimed one schema id and the
published document described the **reply's** shape as the request body. The request is `AcceptBrandGuideProposalViewModel`
for that reason, matching the verb-first spelling the brand module's own request models use. **The identical
collision exists today on `AiDraftAcceptance`** — 8.12/8.14's acceptance route publishes its reply as its request
body and emits no schema for `AiDraftFieldEditViewModel` — and it is not fixed here, because correcting it edits
a shipped contract document and belongs to its own change. Separately, `IAiOperationDataLayer` now holds three
decision methods with the same execution-strategy/transaction/re-read/replay shape; only the identical
`StampDispositions` loop was extracted, because rewriting two tested seams inside a change about a third is the
regression risk `CLAUDE.md`'s atomicity rule argues against. A fourth decision seam should unify them.*

*No migration: no new entity, column or index. `AiChangeTargetKind.BrandGuideSection` remains absent from
`AiChangeApplicability` and still answers `null` in `AiChangeTargetPolicy` — there is still no path from one of
these rows to a recipe — and the remarks on both, and on `AiTaskType.BrandGuideProposal`, were amended rather
than left to read as still true. See [B-23](../architecture-decisions/baseline.md#b-23-brand-guide-proposal-acceptance).*

```text
SCOPE: Reuse the proposal diff/disposition pattern to accept all or selected brand-guide sections into
one new Draft guide version, preserving source citations and creator edits.
CONSTRAINT: AIREC-007 and AIREC-GR-002/004/007; add-ai-capability and add-content-feature skills.
RESTRICTION: Stale source guide/document versions cannot be silently rebased. Acceptance does not activate
the guide. Replay creates no duplicate version.
BEHAVIOR: Plan section selection/transaction, wait for approval, implement full/partial/reject/stale/
replay/rollback/isolation tests.
```

### 11A.19 Deterministic BrandContextPackage assembler - done, unconsumed

*Delivery note, in four parts.*

*First, one correction to the scope. It lists "workspace" as an input; it is not one. The workspace reaches
every read through the resolved `IWorkspaceContext`, and `BrandContextRequest` has no field for it — a
`workspaceId` argument on a seam the worker calls is exactly the AI-supplied workspace tenancy.md forbids, with
no route policy in the way. `BrandContextPackageShapeTests` asserts the request's property list, so the field
cannot reappear quietly.*

*Second, what "relevant" and "bounded" were made to mean, since the prompt sets both as requirements rather
than rules. Relevance is a table — `BrandContextSelection.SectionKeysFor` — and the entries that matter most are
the empty ones: `IngredientSubstitution`, `RecipeReview`, `RecipeRevision` and `RecipeAdaptation` get no brand
context at all, by an early return rather than by filtering, because style rules may not alter canonical recipe
facts or safety and a filter is one populated field away from leaking. `BrandGuideProposal` is excluded as
circular and `UserNotes` from every task, being the creator's notes to themselves. Boundedness is two caps: a
named document list is refused above `BrandContextMaxSourceDocuments` rather than trimmed, while an unnamed one
is ranked on purpose, channel and audience and capped lower, because a guess the server makes on the creator's
behalf should spend less of the budget than an instruction.*

*Third, three judgement calls worth knowing about. **Precedence is per field, not per source** — a channel
variant silent on audience must not shadow the profile's default, which is the bug a per-source rule would ship.
**A conflict is only ever checkable**: nothing here reads prose for meaning, so semantic contradiction between
two sections is deliberately not detected and the enum says so, the same honesty 11A.17's handler already
records. **The token figure is an estimate and is labelled one everywhere it surfaces** — four characters per
token, rounded up; the domain holds no tokenizer and should not, since a real count depends on the provider's
vocabulary and would make a package's content vary by deployment, while the exact count already arrives
afterwards through `AccountAiUsageEntry`.*

*Fourth, three things the work changed on contact. **The relevance ranking moved into the brand module**, and
`ModuleBoundaryTests` is what moved it: the assembler first built a library query itself, which meant naming
`BrandSourceDocumentListViewModel` across a module boundary, and a module's view models never cross. The rank
now belongs to `IBrandSourceDocumentFacade.ListGroundingCandidatesAsync`, which is the better home anyway — the
facts a document is ranked on are the library's — and the AI module supplies only the budget. One brand read was
missing and had to be added:
`IBrandStyleGuideFacade.GetActiveAsync`, keyed on the workspace, because `GetAsync` answers "this guide, and its
active version if the default happens to be its own" and a caller that does not know which guide holds the
default cannot use it. And a `SourceVersionSuperseded` conflict was written and then removed as unreachable —
the assembler pins each document's current version itself, so no assembly can cite one the document has moved
past; a guide version whose own citations are stale is a different fact, published by the history as
`staleSourceCount`. The enum keeps a comment where the member was rather than silently reusing the number.*

*Nothing consumes a package yet, and that is the prompt's boundary: 11A.20 wires task handlers one at a time and
is what records the checksum and provenance against a generation. The assembler is registered in the Worker so
the container proves its dependency graph resolves now rather than at the first handler. No migration — nothing
here is persisted. See
[B-24](../architecture-decisions/baseline.md#b-24-deterministic-brand-context-for-generation).*

```text
SCOPE: Build a server-side assembler that takes workspace, task type, channel, audience, optional guide
version, and source-document selections and returns a bounded BrandContextPackage containing exact guide
version, relevant structured rules, cited excerpts, conflicts, omissions, checksum, and token estimate. It also
reads the workspace BrandProfile through the brand facade (default audience, locale, channel defaults) and
records the exact profile `revision` beside the guide version, so a generation can be traced to the profile
state it used.
CONSTRAINT: AI-001/002/008, DEC-010; .claude/rules/ai.md and content.md.
RESTRICTION: No arbitrary all-document stuffing, cross-workspace retrieval, client-supplied blob key,
provider call, or hidden fallback to another guide. Exact context inputs are recorded with generation.
Profile text is untrusted prompt content, delimited from instructions; no profile field switches a platform
safety rule, warning, or check on or off.
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

### 11A.24 Voice and visual style test-drive - done

*Delivery note, in four parts.*

*First, what "comparing neutral versus guide-informed" was made to mean, since the prompt sets it as a scope
rather than a mechanism. **It is two provider calls, not one.** One call writes the three samples with no brand
context whatsoever; the second writes them from the selected version. A single call holding the guide while
writing both halves would produce a left-hand column labelled "without your guide" that was written with it,
and the screen would be asserting the one thing it must not. The two calls differ in exactly one respect — the
second carries the package's `PREFERENCES` and `REFERENCES` segments — so same template, same schema, same
validator, same subject. The cost is the honest consequence and is published rather than absorbed: the
confirmation says two pieces of work, and `Ai:Quota:TaskEstimates` carries a `BrandStyleTestDrive` entry at
twice the per-call default in both hosts, because the API refuses at the edge and the worker takes the hold.*

*Second, how "identify guide rules/citations used" was held rather than claimed. **The output document has no
field for an attribution**, so the model has nowhere to say which of the creator's rules it followed — that
claim is unfalsifiable and is the "sounds more like you" the RESTRICTION names. The screen lists what
`BrandContextSelection` actually selected, in the creator's own words, beside the passages the package carried.
Those labels are re-derived on the read rather than stored, which is safe for a reason worth writing down: a
guide version's sections are immutable (`BrandStyleGuideSection` is an `IImmutableRecord`), so the wording named
beside the samples **is** the wording that produced them. What can still move is the brand profile and the
example library, so the read compares a freshly assembled checksum against the recorded one and publishes
`groundingChangedSince`. A stored table of applied section keys was written into the plan and dropped: it was a
migration bought to remove a drift the version's immutability already removes.*

*Third, three judgement calls. **An empty guide version is refused before anything is spent** — twice, at the
edge and again in the handler — because two identical columns are not a comparison and a creator should not pay
to find that out; assembly makes no provider call, so the refusal is free. **The version is required and never
defaulted**, unlike every other brand-grounded seam: trying a draft out before activating it is the point, and
falling back to the active guide would answer a question nobody asked. **The samples are two target kinds, not
a before and an after.** Reading the plain column as the "before" of the guided one was tempting and wrong —
`AiStructuredChange.BeforeValue` is documented, and asserted by `AiProposalAssembler`, as a value the server
read from a pinned source and never one the model supplied — so it is null on all six rows and the variant is
an enum member instead.*

*Fourth, what the work changed on contact. **`BrandStyleTestDrive` is the only entry in the relevance table
that unions a writing list with the visual one**, because it writes prose and an image prompt in one pass;
`PurposesFor` became a switch to match, and it passes no channel for the reason B-25 gives. **The API now
registers `AddAiBrandContext`**, which only the worker did: this seam asks the assembler whether a named version
resolves before queueing anything, and asks it again on the read. No migration — neither `TaskType` nor
`TargetKind` has an enumerated check constraint, and 11A.20's provenance tables already exist. The screen is
reachable from every row of a guide's version history, draft included, and from the setup wizard once a guide
exists. See
[B-26](../architecture-decisions/baseline.md#b-26-a-read-only-comparison-is-two-provider-calls).*

```text
SCOPE: Add a read-only “Test my style” experience comparing neutral versus guide-informed sample blog
intro, social caption, and image prompt using one selected guide version.
CONSTRAINT: easy-as-pie product rule and AI proposal requirements.
RESTRICTION: Writes no canonical content and incurs no hidden live cost. Identify guide rules/citations
used rather than merely claiming the output “sounds more like you.”
BEHAVIOR: Plan comparison/cost confirmation, wait for approval, implement fake-provider/a11y tests.
```

### 11A.24a Brand voice and style audit - done

*Delivery note, in four parts.*

*First, the finding that matters most, and it is structural rather than a defect in any one file. **The brand
guide is authored end to end and wired into nothing that writes.** 11A.20 is unimplemented:
`grep -c BrandContext` is 0 in both `EditorialPackageAiTaskHandler` and `SeoPackageAiTaskHandler`, which send
the brand **profile's** name, audience and locale and nothing from the style guide — not voice, tone, tenor,
writing style, point of view, vocabulary, rhythm, formatting, storytelling, calls to action, blog guidance, or
any uploaded example. 11A.21 has no image handlers at all. 11A.21a/b shipped their endpoints and their
TypeScript models and no screen consumes either. 11A.22e offers "Draft it for me" and nothing drafts. So "Test
my style" is currently the only place in the product where an active guide has any effect. Fixing that is
11A.20 and 11A.21's own work and was not attempted here.*

*Second, the two blockers, both of which were about telling the creator something untrue. **B1: the activation
screens promised what does not happen** — "becomes the voice this workspace writes and makes pictures with.
Everything generated from now on is grounded on it", and the wizard's "every new blog post, caption and
newsletter starts from it". An Owner was making a workspace-wide decision on a false premise, which is the
audit's own RESTRICTION inverted: it concealed the absence of an activation effect. All three places now say
what is true today and carry a `REVISIT WITH 11A.20/11A.21` marker, and the specs assert the absence as well as
the presence so the promise cannot drift back in before those prompts land. **B2: the test-drive prompt banned
food-safety claims the server never checked.** The prompt forbids a quantity, a time, a temperature, a safety
assurance and an allergen label; the validator checked only lengths, hygiene, links and warning labels, and
nothing stated that the creator's guidance does not outrank that ban — which
`BrandContextPromptRenderer`'s own remarks say belongs in each task template. A guide rule saying "always
reassure readers it's allergen-free" would have produced exactly that, shown as a demonstration of the
creator's voice. The prompt now states the precedence and the handler runs a server-side net over both halves.*

*Third, three judgement calls in the B2 fix worth knowing about. **The net is the editorial one, not a second
copy** — `AiEditorialClaimScanner` already maintains that phrase list and calls itself a net rather than a
proof; two nets would drift. **Only its safety check runs, not its figure check**: a style sample has no recipe
behind it, so running the figure check against nothing flagged "one pan" and "one lemon" on almost every
sample, and noise is what makes a real finding easy to ignore — so `ScanTextForSafetyClaims` was added for the
no-source case and the number ban stays a prompt rule that nothing enforces, which is stated rather than
implied. **Findings warn rather than refuse**: nothing on that screen can be accepted or published, so refusing
the whole comparison over one phrase would cost the creator the demonstration they paid for and tell them less
than a warning does.*

*Fourth, one approved item was implemented against its own wording, and deliberately. **S5 asked for the actual
cost on the test-drive result**, but `AiProposalDetailServiceModel` documents the opposite decision — "latency,
tokens and cost are operational, and putting them in a creator-facing reply would be answering a question
nobody asked" — and the allowance screen already exists for them. So the cost is disclosed where it is
actionable instead: the confirmation now states the ceiling ("two short pieces of AI work — up to four if an
answer comes back malformed") before anything is spent, and the result points at the allowance rather than
becoming a second, partial account of the same figures. Three audit findings were deliberately **not** fixed
and remain open as their own prompts: S1 (accepted AI guidance loses its origin, evidence basis and
uncertainties, then returns to the model as the creator's own answers — likely a schema change), S3 (naming a
living writer only warns), and S7 (the assembler publishes no stale-source notice). Also open:
`WireEnumMirrorTests` does not cover `BrandAssetRole` or `BrandSetupSessionStatus`.*

*Verification: 6535 backend tests pass, with the three documented `BulkOperationBoundaryTests` failures that
occur whenever the build output is redirected around a live `aspire run`; 176 + 2451 frontend tests pass; the
OpenAPI snapshot is unchanged by this pass. The `design-review` reviewer returned "PASSED, no changes required"
across all fourteen easy-as-pie bullets and was not relied on: it misidentified the display font and dismissed
the brand-library degraded gap that was then confirmed and fixed. Two other reviewer claims were checked and
rejected — that a failed second provider call stores a half-written proposal (it does not; the handler returns
before the assembler and a test asserts it), and that `AiTaskType` is missing from `WireEnumMirrorTests` (it is
not).*

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

### 12.1 Content channel and weekly-theme configuration - channels done, themes rescoped as 12.1a

```text
SCOPE: Add validated application configuration or reference data for the six content channels and seven
weekly themes required by DATA-RQ-001 through 003, with stable keys and display metadata.
CONSTRAINT: SEED-001 and DATA-RQ-001 through 003; .claude/rules/content.md.
RESTRICTION: Do not scatter channel/theme literals across prompts, Angular, or providers. If user-managed
configuration is chosen, stop and add separate ILF/function-point scope before implementation.
Expose channel-key validation through a facade, and make the brand profile's create/update call it: 11.1b
stores `channelDefaults` as opaque keys, so this prompt is what makes an unknown key a validation failure.
Decide and test what happens to keys already stored when a channel is retired.
BEHAVIOR: Show source-of-truth choice and schema, wait for approval, implement with uniqueness, invalid-key,
brand-profile unknown-key, and retired-key tests.
```

*Delivery note: the channel half is built as code-owned configuration (`ContentChannelCatalog`, shared kernel),
with a facade, `GET /api/v1/reference/content-channels`, and brand-profile validation that refuses unknown keys
and refuses newly choosing a retired one while leaving a retired key a profile already stores alone. **The eight
channels are provisional**: the requirement text behind DATA-RQ-001 to 003 is not in the repository, so they are
the SOC-001 social platforms (Instagram, TikTok, Pinterest, Facebook, X, Threads) plus the two channels the
creator owns outright, `blog` and `newsletter`. The owned pair was added on 2026-10-04 because
`BrandSourceDocument.ChannelKey` records the channel a sample exemplifies and a blog post and a newsletter are
the likeliest samples a food blogger has — without those keys there was nothing to tag them with, while
`BrandProfileInputChecks.Channels` was already offering "blog or newsletter" as its example keys. Owned channels
lead the display order; order is presentation only, since every consumer resolves a key through `Find` rather
than by position. Correcting the list further is an edit to `ContentChannels.cs` plus
`ContentChannelCatalogTests`.*

*Note that the brand wizard's `CHANNEL_OPTIONS` (`brand-setup-goals.ts`) is a **separate** answer vocabulary
that merely overlaps: it also offers `youtube` and `other`, and the wizard draft is opaque JSON the server never
interprets, so those answers are not channel keys and never reach `channelDefaults`. Reconciling the two lists
is not this prompt's.*

*The **weekly-theme half moved out**. The themes are creator-managed — a creator writes their own, up to seven
and often fewer — which is user-managed configuration, so this prompt's own RESTRICTION applies: it stops
implementation and takes separate ILF/function-point scope, now written as 12.1a. No platform theme catalogue
exists or should be built, and that includes 12.2: its `day/theme` must come from the workspace's own themes
through a facade, and must degrade to no theme for a workspace that has none. Both were delivered on 2026-10-04 and
12.2 does exactly that, through `IWorkspaceWeeklyThemeFacade`.*

### 12.1a Creator-managed weekly themes - done

```text
SCOPE: Add the workspace-owned weekly theme the creator writes themselves — at most one per day of the week,
so at most seven and fewer is normal — with a stable key, the creator's own display name and description,
retirement, and the read and write surface they manage them through.
CONSTRAINT: DATA-RQ-001 through 003 and SEED-001; .claude/rules/tenancy.md and .claude/rules/content.md;
add-workspace-entity and add-content-feature skills.
RESTRICTION: A theme is creator intellectual property, not reference data: workspace-scoped with a global
query filter, never global, never deduplicated across workspaces, and never seeded server-side. One day holds
at most one theme (unique WorkspaceId + Day). Do not reintroduce a platform theme catalogue beside
ContentChannelCatalog. A theme a workspace has retired or deleted must not break a record that already stores
its key.
BEHAVIOR: Show the entity, indexes, write shape, and what retirement and deletion do to stored keys; wait for
approval; implement configuration and migration, review and apply it, and test one-theme-per-day uniqueness,
unknown-key rejection, retirement, deletion with a stored key, and two-workspace isolation.
```

*Scope note — why this is its own prompt. 12.1's RESTRICTION says that choosing user-managed configuration
stops implementation and takes separate ILF/function-point scope. On 2026-10-04 the creator-managed answer was
chosen deliberately: a creator invents their own week, so the platform cannot own the list. The themes are
therefore an ILF rather than two more lines of configuration, and the function-point scope is:*

| Function | Type | Complexity | FP |
| --- | --- | --- | --- |
| `WorkspaceWeeklyTheme` — Id, WorkspaceId, Day, Key, DisplayName, Description, RetiredAt, Revision, CreatedAt, UpdatedAt; one RET | ILF | Low | 7 |
| `PUT /api/v1/workspaces/{workspaceSlug}/weekly-themes` — replace the week in one idempotent write | EI | Average | 4 |
| `GET /api/v1/workspaces/{workspaceSlug}/weekly-themes` | EQ | Low | 3 |
| | | **Total** | **14** |

*Recommended write shape: one write that replaces the whole set, not per-theme CRUD. At most seven day-keyed
rows make the week a single value, which keeps the write idempotent, lets a creator clear a day by omitting it,
and keeps "one theme per day" an invariant of one transaction instead of a race between three endpoints.*

*Starter suggestions — `meat-free-monday`, `taco-tuesday`, `one-pan-wednesday`, `throwback-thursday`,
`fakeaway-friday`, `baking-saturday`, `sunday-supper` — belong to the UI as prefill the creator edits or
discards, never to a server-side default, which would make the themes platform configuration again. Whether to
offer them at all is part of this prompt's approval. The display name is the creator's own words and the server
does not police it; "Meatless Monday" being a registered mark of The Monday Campaigns is a reason not to
**suggest** it, not a reason to validate what a creator types.*

*Delivery note (2026-10-04). Built in the **content** module as `WorkspaceWeeklyTheme` over the full seam, with
the three decisions above approved as recommended: omission retires, concurrency is last-write-wins, and no
starter suggestions reach the server. The delivered scope is therefore **17 FP**, the table's 14 plus the
Owner-only `DELETE .../weekly-themes/{key}` (EI, low, 3).*

*Two indexes carry the rules, and the split between them is the design.
`UX_WorkspaceWeeklyThemes_Workspace_Day` is unique and **filtered to `RetiredAt IS NULL`** — one theme per live
day, filtered because an unfiltered index would make a day unusable forever once its first theme retired.
`UX_WorkspaceWeeklyThemes_Workspace_Key` is unique and **unfiltered**, so a key is never reused in a workspace
and a key some other record stored resolves to exactly one theme or to nothing. Nothing holds a foreign key to a
theme row: a consumer stores the `Key` string as a weak reference, which is what makes retirement and deletion
safe. Retiring keeps the row, so a stored key still resolves and is reported with `retiredAt`; deleting removes
it, so the key resolves to nothing and the consumer keeps the creator's text with no theme detail, never an
error and never a cascade. A deleted key may be written again later.*

*One thing to know before touching the write. A replace reaches the database as **two ordered statement
batches** — release every day that is changing hands, then place the week — because a day changing hands would
otherwise be claimed before its old theme let go, and the live-day index is what makes that an error rather than
a silent second Monday. Swapping two themes between two days is the same problem with two updates and no
statement ordering to appeal to. So `WorkspaceWeeklyThemeDataLayer.ReplaceAsync` joins the idempotency
transaction when there is one and opens its own when there is not, and
`WeeklyThemeWeekPlan`/`WeeklyThemeChange` are plain values rather than staged mutations precisely so a retrying
execution strategy can re-run the write from a clean change tracker. `ExecuteUpdateAsync` was deliberately not
used: `BulkOperationBoundaryTests` forbids it in domain code and this is none of tenancy.md's carve-outs, so the
exemption list is unchanged.*

*A failed save is **classified, not assumed**. `LostToAnotherWriterAsync` re-reads the committed week and asks
whether another writer took a key this attempt was inserting or a live day it was claiming; only then is it a
conflict, and anything else — a deadlock victim, a command timeout — keeps being an error rather than becoming
"reload and try again", which would send a caller round a loop that cannot end differently. The `MaxThemes` cap
is checked against Business's pre-transaction read, so it is a bound on growth rather than an invariant and two
racing replaces can overshoot it by a row or two; one-theme-per-day and one-row-per-key are the real invariants
and they are unique indexes.*

*For 12.2: read the week through `IWorkspaceWeeklyThemeFacade`. `GetAsync` returns the live week Monday-first
then the retired themes, and `FindAsync(key)` is the seam a consumer validates a stored key against — an unknown
key, another workspace's key and a deleted key are deliberately one answer. A workspace with no themes answers
`200` with an empty list, never `404`, which is the degrade-to-no-theme path 12.1 asked for.*

### 12.2 Content-seed generator - done

```text
SCOPE: Implement SEED-001 deterministic/randomized seed selection returning cuisine, dish type, method,
photography style, channel, day/theme, description, and occasion from approved reference/configuration.
CONSTRAINT: SEED-001; add-content-feature skill.
RESTRICTION: Invalid channels fail. Inject randomness for deterministic tests. No AI call or persistence.
BEHAVIOR: Plan selection weights and reproducibility, wait for approval, implement with channel/day/
invalid/seeded-random tests and expose the endpoint through the full seam.
```

*Delivery note (2026-10-04). `GET /api/v1/workspaces/{workspaceSlug}/content-seeds`, Viewer and above,
`no-store`, through Controller → Facade → Business. **It has no data layer or repository, because it owns no
table** — Business composes the vocabulary and brand facades, this module's weekly themes, and three code-owned
catalogues. A deliberate deviation from the standard seam, recorded here rather than left to be discovered.*

*Where the eight facets come from. Cuisine is `Cuisine`; **dish type is `Course`**, which already documents itself
as covering "course, meal type, and dish type", so no table was added; method is `CookingTechnique`; channel is
`ContentChannelCatalog`; day/theme is 12.1a's `IWorkspaceWeeklyThemeFacade`. **Photography style and occasion are
new** — `PhotographyStyleCatalog` (10) and `OccasionCatalog` (12) beside `ContentChannels.cs`, both **provisional**
for the reason the channels are: SEED-001's requirement text is not in the repository. Description is the eighth
and is **assembled, not selected**: `ContentSeedDescription` writes one line out of the facets' own display names
and fixed connecting text, so it reads as a prompt to the creator ("Develop a Thai main course…") and never as a
judgement about a dish nobody has cooked.*

*The boundary to keep. `BrandStyleGuideSectionKey.PhotographyDirection` is the sole source of truth for how a
workspace's photography looks, and it is prose the creator wrote. The seed's photography facet is a **shot type**,
the way `CookingTechnique` is a method — craft vocabulary, never a statement about a brand. The seed does not read
the guide and must never grow fields that would make it a second source for it;
`SeedFacetCatalogTests.A_photography_style_is_not_a_brand_statement` fails on a new property. The residual risk,
accepted: a seed can suggest "dark and moody" to a brand whose guide says "bright and airy". Every facet is
replaceable, which is what makes that acceptable for an idea generator.*

*Reproducibility, which is the part worth reading before changing anything. The response always carries the
`token` that produced it, and sending that token back returns the same seed. `IContentSeedTokenSource` is the
**only** randomness in the generator; everything after a token is a pure function of it, so a fixed token fixes
the whole result — where injecting a shared `Random` would have made each facet depend on the order the others
drew from it. `ContentSeedSelector` uses **rendezvous hashing**, scoring every candidate as
`SHA-256(token ␟ facet ␟ key)` and taking the lowest, rather than indexing into a sorted list: adding one entry to
a list of n moves that facet for about 1 token in n+1, where indexing would shift every pick the moment a row was
inserted and silently break every shared seed link. The documented limit: a token reproduces against a given
catalogue state and a given week, not forever — retiring the entry it selected changes that facet, as does
rewriting the theme for its day.*

*Weights, since the BEHAVIOR asks for them. **Uniform over active entries, deliberately.** Nothing in the
repository says one cuisine deserves to come up more often, and per-entry weights would be the fabricated
editorial metric content.md forbids. The one grounded exception is the channel: a workspace that listed
`BrandChannelDefault` has said where it publishes, so seeds draw from those and fall back to the catalogue
otherwise — the creator's own answer honoured rather than a preference guessed. The weighting a creator would most
feel, not repeating last week, needs a history this endpoint is forbidden to keep; `ContentSeedSelector` records
how weighted rendezvous would be added without a contract change.*

*Refusals and degrading are opposite on purpose. A **pinned** facet naming nothing, or naming a retired entry,
answers `400 content.seed.invalid` with a field error — a caller who pinned Thai and silently got Korean would
have no way to tell, and the code-owned catalogues keep retired entries so they can say "no longer available"
rather than "unknown". An **absent** facet is never an error: an empty catalogue or a day with no theme yields a
seed with fewer parts. That path is real rather than theoretical — `SqliteApiHost` does not run the reference
seeder, so the first endpoint test got a three-facet seed instead of a failure.*

*`method` carries `requiresSafetyCaution` through to the response rather than the eight flagged techniques being
excluded from selection. That flag's own documentation says it exists as the structured hook generation and review
paths read, so this uses it as designed, and `false` still means only that none has been attached, never that a
technique is safe. **The caution is also in the `description`**, as fixed text about following tested, authoritative
guidance — not only in the flag beside it. That was a defect found in review and it mattered: the description is
the sentence a creator reads, copies and shares, so a client that forgot to render the flag would have handed
someone "Develop a dessert using the pressure canning method" with nothing attached. Note for whoever revisits
this: **8 of 30 techniques are flagged**, so roughly a quarter of unpinned draws land on one, and excluding them
from random selection while still allowing a creator to pin one remains a defensible alternative.*

*The description's clause forms are chosen to read for **every** entry in each catalogue, not for the tidiest
example. An earlier version appended the method as a bare noun and the occasion after "for", which produced "a main
course, stir-fry" and "for budget"; "using the … method" and "with … in mind" read for every entry, and the shot
style is a label (`Shot: Dark and moody.`) because no preposition reads for both an angle and a mood.
`ContentSeedDescriptionTests` asserts the clause shape over every shipped entry, which a fixture holding two of
each would not have caught.*

*One thing to know before feeding a seed to a model: `Description` can contain the creator's own words, because a
weekly theme's display name is free text they wrote. It is creator content, so a prompt including it must delimit
it as untrusted source material (ai.md). Nothing calls a model today, which is why that is a note on
`ContentSeedServiceModel` rather than a rule being broken.*

### 12.3 Prompt library entities - done

```text
SCOPE: Add workspace-owned PromptRecord with channel, text, label, source type/ID, recipe/version lineage,
asset lineage, content type, template version, creator edits, and timestamps.
CONSTRAINT: PRM-001 through PRM-005 and DATA-001; add-workspace-entity and add-media-feature skills.
RESTRICTION: Prompts are private creator content. No blob URL as authority; store stable object identity.
Do not create prompt endpoints yet.
BEHAVIOR: Show data model/indexes/retention, wait for approval, implement configuration and migration,
review/apply it, and test workspace scoping.
```

*Delivery note (2026-10-04). `PromptRecord` in the **content** module as `ContentRevision`'s sibling, which is the
closest analogue in the repository: both store a generated artifact together with the exact sources it was produced
from. Entity, configuration, migration and tests only — **no DbSet-adjacent seam, no facade, no endpoints**, because
nothing may read it yet and a facade with no caller is a guess about 12.3a's shape. Moving the type to a media
module when 12.6/12.9 arrive is a namespace change, not a schema one, as long as the table name stays.*

*Three shapes were copied from `ContentRevision` rather than invented. The **template pin is a triple** —
`PromptTemplateId` + `PromptTemplateVersion` + `PromptTemplateBodyChecksum` — because a version string alone is a
claim: a template body can change under a version, and a record pinned by version alone then cannot say what wrote
it. **Asset lineage is a bare Guid with no foreign key**, since `GeneratedImage` (12.6) and `DamAsset` (12.9) do not
exist; that is the documented path `ContentRevision.BrandStyleGuideVersionId` and `RecipeVersion.AiProposalId` each
took, and these gain their key the same way. **Recipe lineage does get real keys**, workspace-paired on
`(WorkspaceId, RecipeId, RecipeVersionId)` with `Restrict`, so one workspace's prompt pinned to another's recipe —
or to a version of a different recipe — is unrepresentable rather than merely refused.*

*The three decisions put to approval, all taken as recommended. **"Content type" is editorial**, a `PromptImageKind`
(hero, process step, ingredient layout, styled scene, social tile, pin graphic, detail shot, other): a prompt holds
no bytes, so a MIME type here would describe a file the asset rows own authoritatively, and a copy could only
disagree. **The record is immutable** (`IImmutableRecord`), because it is the evidence of what produced an image that
may already be published; the cost, tested and stated rather than discovered later, is that even `Label` cannot be
changed, so reorganising a library needs its own annotation row. **Source is an enum with a typed FK** —
`PromptRecordSource` plus a workspace-paired `AiProposalId`, tied by check constraint — since all three AI sources
are proposals and `AiProposal` already has the alternate key.*

*"Creator edits" is **two columns, not a flag**: `GeneratedText` holds the model's draft beside the authoritative
`Text`. A flag would record that an edit happened and lose what it was; this answers "what did the creator change"
for as long as the row exists, which is the same reason a recipe keeps the creator's own words beside the normalised
reference.*

*Indexes: `(WorkspaceId, CreatedAt DESC, Id)` for PRM-002's newest-first default and its keyset cursor,
`(WorkspaceId, ChannelKey, CreatedAt DESC, Id)` for its channel filter, and a **filtered unique**
`(WorkspaceId, GeneratedImageId)`. That last one is load-bearing: it makes 12.3a's "no duplicate prompt records on
DAM retry" a property of the schema rather than something a worker has to remember — a retry loses the index. An
explicit `(WorkspaceId, RecipeId)` index was written and then **removed on reviewing the generated migration**: the
recipe-version foreign key already creates `(WorkspaceId, RecipeId, RecipeVersionId)`, whose leading columns answer
the same question, so it would have cost every insert a second write for nothing.*

*Retention: **none, and that is the decision**. No expiry and no soft-delete column. This row is a few kilobytes of
text and is the provenance of a possibly-live asset; 12.6 gives staged *images* retention deadlines because bytes are
expensive, which is not an argument that reaches text. Workspace deletion cascades. Soft-versus-hard delete belongs
to whichever later prompt actually adds a delete capability, with a use case in hand.*

*Privacy: `Text` and `GeneratedText` are prompt bodies and are never logged or put in an audit summary (ai.md).
**There is no URL, path or byte column anywhere**, and `PromptRecordModelShapeTests` fails on a property named like
one — a rule about what must not be added, which no ordinary test would notice breaking. It also pins the absence of
`UpdatedAt`/`RowVersion` (a write-once row with an updated timestamp would tell two stories about itself) and the
absence of a `WasEdited` flag.*

*Two things review changed. **Immutability creates a validation obligation** nobody else can discharge: the two
unkeyed lineage ids cannot be checked by the schema and cannot be corrected once written, so 12.3a must resolve them
in the workspace before inserting or 12.6's foreign key becomes unaddable. That is now written into 12.3a and 12.6
rather than living in a code comment. And **erasure ordering was an open question worth answering**: this row
cascades from `Workspace` while holding `Restrict` pins to a recipe, a version and a proposal that cascade from the
same workspace, so if SQL Server checked those mid-cascade a workspace holding a prompt could never be deleted —
and immutability would prevent clearing the way through EF.
`PromptRecordSqlServerTests.Deleting_a_workspace_still_succeeds_with_a_fully_pinned_prompt_in_it` proves it does
work. It reasoned sound beforehand; it is the kind of reasoning worth a test.*

*What this commit does **not** establish, stated so the entity-only scope is not mistaken for more: tenancy.md's full
isolation list — mutation through a facade, cache, search, background processing, AI retrieval — cannot be tested
here because no seam exists to test. It is owed to 12.3a and PRM-002, and is noted in 12.3a.*

### 12.3a Save prompt record - done

```text
SCOPE: Implement only PRM-001 as the idempotent prompt-save operation used when a generated asset is
committed, preserving channel, creator-edited prompt, source/recipe/image lineage, and content type.
CONSTRAINT: add-content-feature, add-media-feature, and add-endpoint skills.
RESTRICTION: Do not create duplicate prompt records on DAM retry. No list/detail/download in this prompt.
BEHAVIOR: Plan transaction linkage to DAM-001, wait for approval, implement replay/rollback/isolation tests.
```

*Carried forward from 12.3 (2026-10-04), and **not optional**: `PromptRecord.GeneratedImageId` and `DamAssetId` have
no foreign key, because their tables arrive in 12.6 and 12.9. This write seam must therefore **resolve both ids
inside the resolved workspace, through the owning facade, before inserting**. Nothing in the schema can check them,
and the row is `IImmutableRecord`, so a value written wrongly can never be corrected — a record left holding another
workspace's id would make 12.6's composite foreign key impossible to add without an erasure carve-out. Duplicate
prevention is already structural: `UX_PromptRecords_Workspace_GeneratedImage` is unique and filtered, so a retry
loses the index rather than relying on this seam remembering. `CreatedByMembershipId` is stamped from the resolved
membership, never from input. And the isolation coverage tenancy.md asks for — mutation, cache, search, background
and AI retrieval — is **owed here**, because 12.3 could only test the schema.*

*Delivery note (2026-10-04). The full seam plus `POST /api/v1/workspaces/{workspaceSlug}/prompts` — Contributor,
`201` with a `Location`, optional `Idempotency-Key` — in the content module. Three stable codes:
`content.prompt.invalid` (400, field errors), `content.prompt.lineage.unprocessable` (422) and
`content.prompt.conflict` (409). The OpenAPI diff is purely additive — one path, two schemas and the two enums,
which were also added to `OpenApiContractTests`' member-naming theory because both are stored as their number
and documented "append, never renumber".*

*The carried-forward rule above could not be executed as written, and that was the prompt's one real decision.
It says to resolve both ids through the owning facade before inserting; **there is no owning facade yet**, so the
only way to honour it was **not to accept either id**. `generatedImageId` and `damAssetId` are absent from the
ViewModel and from the facade's command, written null, and their absence is pinned by a test in the style of
`PromptRecordModelShapeTests` — a rule about what must not be added, which no ordinary test would notice
breaking, because a field that merely works is exactly how a client writes an unverifiable id into a row that
can never be corrected. **12.6 and 12.9 each add their field together with the facade check and the composite
foreign key**, which is an additive contract change, and each deletes its line from that test. The consequence,
stated rather than left implied: this seam cannot reach
`UX_PromptRecords_Workspace_GeneratedImage` at all, so the RESTRICTION's "no duplicate prompt records on DAM
retry" rests entirely on the index 12.3 already tested, and what this prompt protects against is a lost response
on a client retry.*

*Transaction linkage to DAM-001, which is what BEHAVIOR asked be planned. `PromptRecordDataLayer.SaveAsync`
**opens no transaction** and that is the linkage rather than the absence of one: a single insert enlists in
whatever transaction the caller already began on the scope's `DbContext`, which both a keyed request and DAM-001
will have. `WorkspaceWeeklyThemeDataLayer.ReplaceAsync` needs the conditional `CurrentTransaction` shape because
a week replace is two ordered statement batches; one insert needs no ordering.
`A_save_joins_a_transaction_its_caller_already_opened_and_rolls_back_with_it` holds it. **The direction is
decided by immutability and is not a preference**: DAM-001 writes the prompt inside its own transaction, never
after the asset commits, because `ImmutableRecordInterceptor` refuses every delete — a prompt row committed
beside an asset whose object copy then fails has no compensating delete to write, only an erasure. That is now
written into 12.9a. A failure detaches only the record this call added rather than clearing the change tracker,
because the caller may be mid-transaction with its own entities staged.*

*A refused insert is **classified, not assumed**, following `LostToAnotherWriterAsync`'s reasoning. The data
layer returns false; Business re-asks the pins. A pin that has gone is the 422, because that cannot end
differently however often it is retried; anything else — a deadlock victim, a command timeout — is the 409,
which genuinely can. Both branches are tested.*

*Three smaller decisions. **The proposal pin needed a new seam**, because `IAiProposalFacade.GetAsync` is keyed
by recipe and *request* id and an image-prompt proposal need not belong to a recipe at all; the id a record
stores is `AiProposal.Id`. It is `IAiProposalLookupFacade`, a class of its own in the AI module and registered
by `AddAiModule` rather than `AddAiProposalSeam` — the reason `IAiProposalFacade.SummarizeOutstandingAsync`
already records, that `AiProposalBusiness` drags `IRecipeFacade`, the quota gate and the task options behind it,
which is a lot of graph for one existence check. The recipe pin needed nothing new:
`IRecipeFacade.GetSnapshotAsync` resolves recipe and version together and refuses a version of another recipe.
**No audit row**, matching recipe create, which writes none either: the row *is* the record — immutable, with
its own author and timestamp — and a prompt body is the one thing an audit summary must never hold. **Contributor
and `KeyRequired: false`**, the house default; the fingerprint is the whole model including the prompt text,
which is acceptable only because it is HMAC-hashed and the hash alone is stored.*

*One thing the isolation audit surfaced that is **not** a leak and is **not** fixed here, recorded so it is a
decision rather than an oversight: the proposal pin is checked for **existence only**. Nothing ties it to the
pinned recipe, or the stored template triple to the triple on the proposal that supposedly wrote the draft — so
a creator's own client can pair their own proposal with their own recipe or template that did not produce it,
permanently, because the row is immutable. It is private data in one workspace either way, which is why it did
not block. It is not enforced now because the three producing tasks do not exist: 12.4, 12.4a and 12.5 are what
settle what the relationship actually is, and guessing it here would have to be relaxed rather than tightened.
**12.4a is the place to decide it**, and the strongest available answer is probably to stop accepting the
template triple at all and derive it from the proposal, since the server already knows it.*

*What is **still owed**, said plainly rather than papered over. Mutation isolation is delivered in full — A
cannot save into B, B's recipe, version and proposal are unusable as pins from A in the same words an unknown id
gets, and nothing saves before a workspace is resolved — and so are the role bars and replay. **Cache, search
and AI retrieval have nothing to test here**: this seam caches nothing, lists nothing and no plugin retrieves a
prompt yet. Those three move to 12.3b's read seam and to 12.4, and writing tests that only looked like coverage
would have been worse than saying so. One loose end: the `Location` header names PRM-003's detail route, which
12.3c serves — a forward reference until then.*

*Amended by 12.3b (2026-10-04): `SavedPromptRecordServiceModel` briefly published `createdByMembershipId`, and
should not have. `RecipeDetailServiceModel` already states the rule — `WorkspaceId` and the membership ids never
leave the server — and this contradicted it for an id no route can turn into a person anyway. Removed before
anything consumed it, with a test pinning the absence, and the row still stores the value. Adding an author to
PRM-003 is a compatible change once a workspace-members endpoint can name one. **Search isolation and the cache
item are discharged by 12.3b**, the latter by design rather than by test: see its note.*

### 12.3b List/search prompts - done

```text
SCOPE: Implement only PRM-002 paged prompt list with channel filter, search, newest-first default, and
summary projection.
CONSTRAINT: add-content-feature and add-endpoint skills.
RESTRICTION: No unbounded list, full asset bytes, raw object path, or cross-workspace result.
BEHAVIOR: Show query/index plan, wait for approval, implement filter/paging/isolation tests.
```

*Delivery note (2026-10-04). `GET /api/v1/workspaces/{workspaceSlug}/prompts` — Viewer, cursor-paged,
`no-store` — built on the recipe search's shape: a module-owned `PromptSearchPageServiceModel` carrying
`totalCount` beside the two `CursorPage` fields, a `PromptSearchQueryFactory` that takes the workspace id (the
one place the shared paging kernel does not carry over), and `PromptSearchScope` binding the cursor to the
workspace. Two codes: `content.prompt.search.invalid` and `content.prompt.cursor.invalid`, separate so a paging
client can tell "your filters are wrong" from "start again". **No migration**: 12.3 built both indexes for this
read, and this prompt is where that pays off.*

*The index plan, and it is the reason this seam has no `sort` parameter. Unfiltered, the keyset rides
`IX_PromptRecords_Workspace_Created`; with a channel it rides
`IX_PromptRecords_Workspace_Channel_Created`; both are `(…, CreatedAt DESC, Id DESC)`, so the ordering is a
seek that arrives already sorted rather than a sort operator. The keyset predicate names those same two columns
in those same two directions, which is the whole correctness condition — disagree by one column or one
direction and paging silently repeats or skips rows rather than failing. Offering a second ordering would mean
either an unindexed sort or a migration this prompt does not own, so `sort` is absent rather than present with
one accepted value; `PromptSearchScope` pins the ordering by name anyway, so adding one later does not
invalidate cursors already in flight.*

*The search cost is **stated rather than discovered**: a substring match is not indexable, and neither index
covers `Text` or `Label`, so a search examines the workspace's rows and pays a key lookup for each. That is
affordable at a creator-library scale precisely because 12.3 made `Text` `nvarchar(4000)` rather than `MAX`;
full-text indexing is the escalation path, and it would be its own prompt with its own migration. **The draft
column is deliberately not searched** — `GeneratedText` exists to record what the creator changed, so matching
it would find a prompt by the very words they deleted.*

*A summary row carries a **preview, not the prompt**: `textPreview` truncated to
`ContentPolicy.PromptPreviewMaxLength` by `SUBSTRING` in the database, beside `textLength` so a client can show
an ellipsis honestly. A hundred-row page of full prompts would be the size of the library, which is what
PRM-003 exists for. Proved on both engines, because SQLite's `substr`/`length` and SQL Server's
`SUBSTRING`/`LEN` are different translations of the same expression — and `LEN` has a quirk theirs does not, in
not counting trailing spaces, which cannot bite a row whose text was trimmed on the way in but is now a test
rather than an assumption. The row publishes no prompt body, no membership id, no proposal id and no template
triple, and `PromptSearchTests` fails on a field named like a URL or an object path.*

*Two decisions worth their own line. **Nothing is cached**, and that is how the cache item 12.3a owed here is
discharged — a workspace-private list that changes whenever a prompt is saved is a poor cache candidate, and a
cache key missing its workspace prefix is the classic way a list leaks across the boundary; there is no cached
value to isolate. And **a `channel` naming nothing is an empty page, not a refusal**, unlike a save, which
validates the key against the catalogue: a read has to keep answering for keys the catalogue has retired,
because a prompt that stored one is still in the library.*

*Isolation is covered where 12.3a could not cover it: a library holds only its own workspace's rows and counts
only its own; a term appearing only in B's prompts matches nothing in A, by text and by label; a channel filter
never reaches B; a cursor minted in A is refused in B, with `PromptSearchScope.Build` asserted directly to
prove the two fingerprints genuinely differ rather than merely differing because the filters did; and a list
cannot be read at all before a workspace is resolved. The search, the separate `COUNT` and the keyset predicate
are each proved scoped on the **real engine** too, because a lowered `LIKE` over two columns and a second
statement are different queries from the bare `DbSet` the 12.3 test covered — any of them could be widened by a
hand-written predicate without that one noticing.*

*Two things the isolation review changed, both worth recording because the first was a defect whose own comment
claimed otherwise. **The scope's variable parts are length-prefixed.** The string is assembled from `|` and
`=`, and *two* of its values are text a caller chooses — the channel is not catalogue-validated here, by
design — so `?channel=instagram|q=foo` and `?channel=instagram&search=foo` built the identical scope and would
have accepted each other's cursors. Same workspace and same caller, so never a leak, but exactly the silent
skipping and repeating the keyset exists to prevent. `ReferenceQueryKey`'s put-the-term-last rule cannot fix it
once there are two such values; writing each value's length in front can, and a test now pins that two
different filter sets never share a scope. **And the fingerprint is not an authorization control** — it is an
unkeyed checksum that catches a cursor used in the wrong place. A hand-forged cursor is reachable, so there is
now a test that one carrying this workspace's fingerprint and the other workspace's row as its position still
returns none of theirs, on SQLite and on SQL Server: safe by construction, because a position is only ever a
`WHERE` over a set the query filter already scoped, which is precisely why it was worth pinning.*

*The "nothing is cached" decision has a **guard test** rather than only a sentence: a reflection check that no
implementation on the read path — facade, business, data layer, search repository — takes a constructor
dependency whose type name contains `Cache`. A list of workspace-private rows put behind `CachedPageReader`
would work perfectly, and the first key written without a `workspace:{id}:` prefix would serve one creator's
library to another.*

### 12.3c Prompt detail - done

```text
SCOPE: Implement only PRM-003 prompt detail by ID with complete lineage and authorized metadata.
CONSTRAINT: add-content-feature and add-endpoint skills.
RESTRICTION: Unknown and cross-workspace are indistinguishable; no download presentation in this prompt.
BEHAVIOR: Plan ServiceModel, wait for approval, implement found/not-found/isolation tests.
```

*Owed from 12.3a and 12.3b (2026-10-04). This is the route that serves the **full prompt text**: PRM-002's rows
carry a truncated `textPreview` and a `textLength`, deliberately, so detail is the only place the whole thing,
the model's draft and the template triple are published. It is also where the `Location` header PRM-001 returns
starts resolving — until this ships, that header is a forward reference. On "authorized metadata": the
membership ids are **not** metadata a route may publish (tenancy.md, and the rule
`RecipeDetailServiceModel` states), so authorship waits for a workspace-members endpoint that can turn one into
a person; 12.3a published `createdByMembershipId` by mistake and it was removed. Unknown and cross-workspace
must be one answer, which the global query filter already makes true — the test worth writing is the one that
proves the message and body are identical too, not just the status.*

*Delivery note (2026-10-04). `GET /api/v1/workspaces/{workspaceSlug}/prompts/{promptRecordId}` — Viewer,
`no-store`, one new code `content.prompt.not_found`. **No migration, no validator, no cache, no transaction and
no audit row**: a keyed read of an immutable row needs none of them, and the `:guid` route constraint is the
only shape check a single id can fail. The `Location` header PRM-001 has been returning since 12.3a now
resolves, and `A_created_prompt_is_readable_at_its_location_header` **follows the header** rather than
rebuilding the URL, so the two route strings cannot drift apart unnoticed.*

*`PromptDetailServiceModel` is **a separate record whose fields match `SavedPromptRecordServiceModel`
exactly**, which is worth justifying because the duplication is the visible cost. The two are two contracts,
and the fields that are coming are the reason: an author once a workspace-members endpoint can name one, and
`generatedImageId`/`damAssetId` once something can verify them. Neither belongs on the reply to a save, and
welding the shapes would put them there. `Detail_reports_the_same_prompt_the_save_reported` compares the whole
record field for field, so "separate" cannot quietly become "divergent" while they are still meant to agree —
a field added to one and not the other has to be a decision rather than an accident.*

*What detail publishes that nothing else does: the **whole `text`**, the model's **`generatedText`**, and the
**template triple**. It deliberately does *not* carry `textPreview` or `textLength` — a client holding the text
can measure it — which is the one place this shape is not a superset of the list's row. **The asset ids were
considered and omitted**, against a literal reading of "complete lineage": nothing can write either column yet,
so they would be null on every row that exists, and a field that is always null documents a capability this
server does not have rather than a prompt that produced no asset. Each arrives with the release that can check
the id (12.6, 12.9), and both are compatible additions.*

*On "authorized metadata", which is the phrase that needed deciding: **a prompt cannot say who saved it, and
that is the answer rather than a gap left open**. The row stores `CreatedByMembershipId` and it stays on the
server with `WorkspaceId` (tenancy.md, and the rule `RecipeDetailServiceModel` states); no route can turn a
membership id into a person anyway, so publishing it would be disclosure with no use. 12.3a's mistake is
pinned twice now — once over the save response, and here over the published shape by reflection rather than
over one response, so a field added later is caught whether or not a test happens to read it.*

*The read is a **primary-key seek on the entity, read untracked**, not a projection. The search repository
projects because a hundred-row page is where two discarded Guids stop being negligible; one row is not, and the
entity never crosses the HTTP boundary — Business maps it and drops both columns. Untracked matters for a
reason beyond cost: the row is `IImmutableRecord`, so a tracked copy could only ever be staged into an update
`ImmutableRecordInterceptor` would refuse. It went on `IPromptRecordRepository`, which that interface's own
comment had anticipated, and which puts a write-seam repository on a read path for the first time — so the
no-cache guard test is repeated for the detail path with that repository included.*

*Isolation. The two answers are **one refusal in the code**, not two written to match: the global query filter
means Business never sees the neighbour's row, so there is a single `record is null` branch and nothing that
could drift. The tests prove it at both levels anyway — at the facade, code, message and field-error count
compared between a borrowed id and an invented one, plus an assertion that the shared wording says nothing
about a workspace (two identical disclosures would pass an equality check); and over HTTP, the entire problem
body compared with only `traceId` removed. **And on the real engine**, because a key seek is exactly the query
a hand-written `WorkspaceId` predicate looks unnecessary on and the shape most likely to be "simplified" into
`FindAsync(id)` later — which would read across the boundary while still passing every single-workspace test.
That test proves the row is readable by id in its own workspace first, so the null cannot be a seeding mistake
reading as isolation. The isolation review added one the first pass had missed: **the route-level refusal**, a
non-member asking for B's real prompt id on B's route, answering as an unknown slug does — the
`WorkspaceViewer` policy is what makes that so, and a route shipped without it would have passed every other
test here, because A's own ids are not in B's library either.*

*Two things stated rather than left implicit. **A malformed id is routing's 404, carrying the edge's generic
`not_found` rather than this module's code** — nothing is disclosed, but a client branching on `code` sees two
codes for what is one condition to it, so it is a test and a documented note rather than something quietly
tolerated. And the OpenAPI diff is **purely additive**: one path, one `PromptDetail` schema, no shipped shape
touched.*

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

*Owed from 12.3a (2026-10-04). PRM-001 validates `aiProposalId` for **existence in the workspace and nothing
more** — it does not check that the proposal relates to the pinned recipe, nor that the record's
`PromptTemplateId`/`Version`/`BodyChecksum` match the triple on the proposal that wrote the draft. That was left
open deliberately, because this prompt is one of the three that decide what the relationship is. Decide it here,
and consider **deriving the template triple from the proposal instead of accepting it** on the save: the server
already holds it, the row is immutable, and a mismatch written once can only be erased. Whatever is decided,
`PromptRecordInputChecks` and `PromptRecordBusiness.UnresolvedPinAsync` are where it lands.*

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

*Note from 12.3 (2026-10-04): `PromptRecord.GeneratedImageId` is waiting for this table and should gain a
**composite** `(WorkspaceId, GeneratedImageId)` foreign key when it arrives, so give `GeneratedImage` the
workspace-paired alternate key that needs. Add the key in the same change, and expect the migration to fail if any
stored id is wrong — `PromptRecord` is immutable, so a bad row cannot be repaired, only erased. 12.3a is required to
validate the id before insert for exactly this reason.*

*How 12.3a answered that (2026-10-04): it **refuses to accept the id at all** until an owning facade exists, so
every stored value is null and this migration cannot find a wrong one. This is therefore the prompt that adds
`generatedImageId` to `SavePromptRecordViewModel`, together with the facade check resolving it inside the
resolved workspace and the composite key above — three parts of one change. A test in
`PromptRecordValidatorTests` pins the field's absence and must have its matching line deleted here; leaving it
would be the clearest possible signal that the check was not added.*

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

*What 12.7 decided (2026-10-05). **There is no image deployment, and that is now a recorded state rather than
a gap.** B-15 names `chat` and `embeddings` and nothing else, so the gateway depends on
`Microsoft.Extensions.AI.IImageGenerator` and every host registers `UnconfiguredImageGenerator` beside the two
existing fallbacks. A claimed operation therefore settles as `provider-not-configured` — terminal, recorded,
and visible — rather than hanging. Wiring a real deployment is a third Foundry branch through all three AppHost
modes plus `Azure.AI.OpenAI`/`Microsoft.Extensions.AI.OpenAI` in `CreatorPantry.AiProvider`, and it amends
B-15, so it is its own change. The job, the gateway and their tests are the parts that do not have to move
when it lands.*

*`MEAI001` is suppressed in exactly three files — `UnconfiguredImageGenerator`, `GeneratedImageProviderGateway`
and the registration helper — because `IImageGenerator` is still marked experimental. Narrowly rather than
project-wide, so a future experimental API elsewhere still has to be opted into deliberately. The alternative
was a parallel image abstraction of our own, which would be a provider-neutral seam that is not the
ecosystem's provider-neutral seam.*

*This prompt also added the **enqueue** half, which its scope sentence does not mention and which no later
prompt adds either: `IGeneratedImageGenerationFacade.RequestAsync`, Contributor-gated, validated by
`GeneratedImageInputChecks`, idempotent on `(WorkspaceId, IdempotencyKey)`. Without it the worker has no input
and 12.7 ships dead code.*

*The HTTP route this owed — `POST /api/v1/workspaces/{workspaceSlug}/generated-images` — **landed on
2026-10-06**, after 12.9a and before 12.9b. It answers **202 Accepted** rather than 201, because nothing is
generated in the request: the operation is queued and a worker claims it, which is what api-contract.md asks
of long-running work. The `Idempotency-Key` header is **required** on it, unlike every other route, and the
action returns the standard `idempotency.key_required` error itself rather than going through
`IIdempotentCommandExecutor` — because this operation's replay guard is its own unique index on
`(WorkspaceId, IdempotencyKey)`, not a stored response, and wrapping it would be two mechanisms for one
guarantee. Removing the read-first lookup leaves the endpoint tests green: the index is the authority and the
read is the optimisation, which is the shape that was intended.*

*Still owed from it: **a way to read an operation back.** A 202 hands a client an id and a status and no
route to poll. 12.10b and 12.10c both need one, and whichever lands first should add
`GET /api/v1/workspaces/{workspaceSlug}/generated-images/operations/{operationId}` — or whatever shape the
Image Studio actually wants, which is why it was not guessed at here.*

*Two notes for 12.8, which owns the sweep. First, **orphan reconciliation has a known producer**: a worker that
writes a staging object and dies before committing the row leaves bytes nothing references. The data layer
compensates on a failed commit, and a replay clears an orphan at its own deterministic key — but a delete that
itself fails is logged and swallowed, so 12.8's reconciliation is the real backstop and should walk the
container, not just the rows. Second, the crash between object write and row commit **costs one re-generation
of that variant**, deliberately: avoiding it needs a row that claims an object before bytes exist, which 12.6's
entity documentation rejects outright.*

*One thing this change fixed in passing: the **Worker had no malware scanner**. The API calls
`AddDevelopmentMalwareScanning`; the Worker never did, because until now it had no inbound-bytes path. Provider
bytes are scanned before they are stored — media.md asks for a scanning policy on media, not on media from
sources we happen to trust — so without that registration every generated image would have been refused by the
fail-closed scanner, on a developer's machine as much as anywhere, and read as a broken job rather than a
missing scanner. The call is a no-op outside Development, so a deployed host still fails closed.*

*`GeneratedImageFailureCategory.ProviderRefused` is deliberately **not** `content-blocked`. Telling a safety
refusal from a malformed request or a rejected credential means reading a provider SDK's exception type, and
the Ai module's `IAiFailureClassifier` — which solves exactly this — lives in its `Gateways` namespace, which
no other module may cross into. A creator told their prompt was blocked when the real fault was an expired key
would go and rewrite a prompt that was never wrong. A narrower category arrives with a real deployment and a
classifier that can see the difference.*

*Two things review changed. **`GeneratedImageOperation` has no `RowVersion`**, unlike every sibling queue's
operation row — so the load-then-save claim those repositories use would have let two workers win the same
row and each buy up to four images. `GeneratedImageClaimRepository` therefore claims with a guarded
`ExecuteUpdate` instead: the `WHERE` is the race and the affected-row count is who won, which needs no
concurrency token. It also made tenancy.md's identifiers-only condition literal rather than approximate —
every query there projects to ids before materializing, so a prompt never enters this process from a
workspace nobody has resolved. `ExecuteUpdate` bypasses `WorkspaceOwnershipInterceptor`, which is why it is
on the forbidden list; no `SetProperty` here names `WorkspaceId`, and none ever may.*

*And `ExecuteAsync` now **checks** that its caller is `WorkspaceServiceIdentity` rather than documenting it.
"Called by the Worker only; never routed" was enforced by a comment, and the facade is in DI where any
controller could inject it. A creator retrying a failed generation queues a new request; nothing a person
does reaches that method.*

*One test honesty note for whoever extends this. `A_claimed_operation_leaves_the_queue_and_spends_one_attempt`
is **not** a concurrency test, and its remarks say so: sequentially the second claim finds nothing because the
candidate scan filters on `Requested`, so deleting the conditional `WHERE` leaves it green. A shared in-memory
SQLite connection serializes commands, so the interleaving the guard exists for cannot be reproduced there.
The guard is argued, not asserted. Two other tests in that file were written vacuous and caught by mutating
the code they claimed to cover — worth doing again for anything in this area, because the expensive failures
here are silent.*

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

*What 12.8 decided (2026-10-05). **Orphan reconciliation needed the object store to be enumerable.** An
orphan is by definition an object whose row was never committed, so no query over the database can find one
— only the container knows it is there. `IPrivateObjectStore` gained `ListAsync(container, prefix, limit)`,
implemented on the Azure adapter, the unconfigured fallback and the in-memory test store. It is additive and
the Brand module is untouched by it.*

*Reconciliation deletes an object only when **two** conditions hold, and the second is the one that matters:
no row of the workspace names the key — asked of the database, not inferred from the key's shape — and the
object is older than `MediaPolicy.OrphanGracePeriod`. A generation writes its object and then commits the row
that owns it, so between those two steps every in-flight image is indistinguishable from an orphan. Without
the window the sweep would race the generation job and delete images a creator was about to be shown. The
window is comfortably longer than `GenerationLeaseDuration`, because a worker's lease bounds how long it can
legitimately sit between the two writes. Both conditions are mutation-tested: deleting either one reds a
test.*

*`GeneratedImage.ObjectDeletedAt` is new, and it is what makes the sweep finite. The row outlives its bytes —
`PromptRecord` may hold a foreign key nothing may delete, and a creator who generated four and declined three
should still see that they did — so "the bytes are gone" has to be a fact on the row rather than the row's
absence. Without it every rejected image ever would be a deletion candidate on every pass, one storage call
per row per minute, learning nothing.*

*The retention scan lives in `GeneratedImageClaimRepository` rather than a new file, so the queue-claim
exemption list stays at five entries. It returns **workspace ids and nothing else**. Orphan reconciliation
cannot be in that query for the reason above, so the sweep reconciles the workspaces that have database work:
a workspace whose only staging content is an orphan waits until it next has real work. That is a slower sweep
rather than a wrong one, and the alternative is listing a storage container once per workspace per hour
forever.*

*`DELETE` marks the row `Rejected` and leaves the bytes to the sweep. One system is written to per request, so
there is no half-done delete to recover from — and it is why the route answers `204` whether this call
declined the image or a previous one already did. A kept or expired image answers `409`, because every state
but `Staged` is terminal (12.6).*

*Error codes follow the **underscore reason-suffix** convention `ProblemResults.StatusFor` keys on
(`.not_found` → 404, `.forbidden` → 403, `.conflict` → 409, `.unavailable` → 503). 12.7 shipped
`media.generation.invalid-request` with a hyphen, which fell through to 400 correctly but by accident; it is
now `invalid_request`. Nothing had consumed either.*

*Traversal is unreachable rather than filtered: the routes are `{generatedImageId:guid}`-constrained and the
storage key is generated from the resolved workspace, the operation and the variant. There is no code path
from a URL segment to an object key, and no URL for a staging object is ever issued.*

*Known gap for whoever adds workspace deletion: a deleted workspace cascades its rows away, so its staging
objects become orphans that no sweep will ever visit — reconciliation resolves a workspace before it can list
that workspace's prefix. There is no delete-workspace path today, so nothing produces this yet.*

*What review changed. **The first cut of orphan reconciliation was wrong, not merely slow.** It took one
page of the listing and sorted it by age — but a blob store lists in key order and a staging key is a pair
of GUIDs, so key order is effectively random: an orphan whose key sorted past the first page would never
have been looked at. Worse, the in-memory fake sorted by age before paging, so every test agreed with the
broken implementation. `ListAsync` is now honestly paged with a continuation token in the store's own
order, the fake behaves the way Azure does, reconciliation walks the whole prefix (one ownership query per
page rather than one per object, bounded by `ReconciliationMaxPages`), and a test stages a page and a half
of owned objects with the orphan forced to sort last. Capping the walk at one page reds that test.*

*Three smaller things from the same review. The retention workspace scan is now ordered, so a page is the
same page twice running — it is **not** anti-starvation, and the comment says so: a workspace that is always
skipped keeps its slot, which only bites past a hundred workspaces with simultaneous work. The store refuses
an empty prefix, so a future gateway cannot enumerate a whole container by mistake. And `PurgeAsync` used to
treat a refused key the same as "nothing there" and stamp the row purged; a row whose `ObjectKey` this
workspace could not have written is corruption, so it is now logged at error — by image id, never by the key
— and still settled, because the bytes are unreachable from there whatever happens and leaving the row in
the queue would retry it hourly forever.*

*Not done, deliberately: no audit event on declining an image. auth.md wants audit on destructive deletion,
and this is arguably that — but 12.10l is the creative-production audit prompt, and scattering one event
here would be the start of a second audit design. Worth deciding there rather than in passing.*

### 12.9 DAM aggregate and migration

```text
SCOPE: Add workspace-owned DamAsset, DamAssetVersion, DamUtilization, and recipe/prompt lineage records
plus private object identity, media metadata, soft-delete state, audit fields, and concurrency token.
CONSTRAINT: DATA-003 and .claude/rules/media.md; add-media-feature and add-workspace-entity skills.
RESTRICTION: Original bytes/versions are immutable. Database stores object identity, not public URL.
No DAM endpoint in this prompt.
BEHAVIOR: Show aggregate/index/delete design, wait for approval, implement migration, review/apply, test.
```

*What 12.9 decided (2026-10-05). **The table is `MediaAssets`, not `DamAssets`, and that is not a liberty
with the prompt's wording.** `RecipeAssetLink`, `BrandAssetLink` and `TestAttachmentLink` have each carried a
`MediaAssetId` since Phase 2 with no foreign key at all, and `RecipeAssetLinkConfiguration` says in so many
words that "the asset aggregate arrives with the media library, and **its own migration adds the
constraint** … composite `(WorkspaceId, MediaAssetId) -> MediaAssets (WorkspaceId, Id)`". `tenancy.md` lists
`MediaAsset` in the workspace-owned set too. Renaming three shipped columns across three modules to match a
prompt's phrasing would have been a migration that changed nothing. **"DAM" stays the language of the
facades, routes and UI**, so DAM-001 through DAM-010 still read naturally.*

*The migration therefore **adds those three constraints**, which is the single most valuable thing in this
change: until now only the write seam stopped one workspace's recipe naming another's photograph, and the
database would have stored it. All three are `Restrict` rather than `Cascade`, because `Workspace` already
cascades into each of those tables through `Recipe`, `BrandProfile` and `RecipeTestRun`, and a second cascade
path is what SQL Server refuses outright — a thing SQLite would have accepted silently. The migration has
since been applied by every SQL Server fixture in the suite, so that is verified rather than assumed.*

*Two tables the scope named do **not** exist, because they would have been duplicate truth.*
*— **Recipe lineage is `RecipeAssetLink`**, which already has `Role`, `SortOrder` and `Caption`, and which
12.10i and 12.10j are the command and UI for. A second DAM-owned recipe link table would be a second answer
to the same question.*
*— **Prompt lineage is `PromptRecord.DamAssetId`**, which 12.9a adds. `PromptRecord` is `IImmutableRecord`,
so an asset pointing back would close a cycle neither side could populate at insert — the same reasoning 12.6
used to keep `GeneratedImageOperation` from naming a prompt record. 12.9's job was to make the target exist.
12.9c reads prompt lineage through the Content facade.*

*Other shape decisions. `CurrentVersionNumber` is a counter rather than a foreign key, so the root and its
versions never point at each other (`BrandSourceDocument`'s pattern). Soft delete is `DeletedAt` plus
`DeletedByMembershipId` rather than a status flag, because DAM-005 asks for timestamp **and** actor, with a
check constraint refusing half a tombstone. Versions are `IImmutableRecord` and carry `SourceKind` plus a
nullable workspace-paired `SourceGeneratedImageId`, with a check constraint making the two agree — so a row
cannot claim a provenance it has no evidence for. Tags link the existing **`WorkspaceTag`** vocabulary rather
than inventing a third; Brand's own `BrandSourceTag` is the thing not to repeat, because a creator who tags a
recipe and an asset "weeknight" means one word.*

*Indexes follow `RecipeConfiguration`'s documented stance rather than one per filter: a filtered
`(WorkspaceId, CreatedAt desc, Id) WHERE DeletedAt IS NULL` and a filtered title index for DAM-002's two
sorts, `(WorkspaceId, MediaAssetId, VersionNumber desc)` for "latest" in DAM-006/007, and
`(WorkspaceId, MediaAssetId, UtilizedOn desc)` for DAM-003's history. Channel, platform, day, style, cuisine,
course, the date bounds and free text are residual predicates after the workspace seek — a creator's library
is thousands of rows, and an index per filter combination would be paid for on every edit to save nothing
measurable on a read.*

*The cost, for the record: **302 tests failed the moment those three constraints existed**, every one of them
a fixture seeding `MediaAssetId = Guid.NewGuid()` — which is exactly the hole the key closes, visible at last.
`SeededMediaAsset` and `RecipeAggregateFixture.MediaAssetIdFor(workspaceId)` are how they seed now. One test,
`BrandProfileAggregateTests.Two_workspaces_hold_identical_child_values…`, previously shared one asset id
across both workspaces; it now gives each its own, which is the constraint working rather than the test being
weakened — every other child value in it is still identical.*

*What review changed. **The migration would have failed on a real database.** `BrandAssetLink` is writable
today through the brand profile endpoint, straight from a client-supplied asset id — so any machine that has
saved a brand profile with a logo holds rows naming assets that never existed, and `AddForeignKey` refuses
them. The migration now clears link rows that do not resolve to an asset before it adds the three keys,
written as "the ones that do not resolve" rather than "all of them" so the intent is legible and nothing that
does resolve is touched. It is the one hand-written part of that migration. Nothing creator-visible is lost
that was not already broken: a link to an asset that has never existed could not be read or rendered.*

*Two of the three link tests were passing on any `DbUpdateException` at all — a missing required field on the
profile would have satisfied them — so each now has a same-workspace positive control beside it. The
version-to-`GeneratedImage` pairing had no cross-workspace test at all; the one that looked like it was
tripping the `Source_Agrees` check instead. Both gaps are closed, and the three stale "no foreign key until
the media aggregate lands" remarks on the link entities and the DbContext now say what is true.*

*Left for 12.9b and 12.9c, deliberately: `MediaAsset` has **no global filter for `DeletedAt`**, so every
reader has to exclude tombstones itself, and nothing at the schema level stops a link pointing at a deleted
asset. That is the design — 12.9c says soft-deleted detail follows its own policy, and a global filter would
put it out of reach — but it means the first reader should land a single shared filtered query rather than
repeating the predicate, and a boundary test flagging a bare `db.MediaAssets` read would be worth having
once there is something to flag.*

### 12.9a Create DAM asset

```text
SCOPE: Implement only DAM-001 to create an asset from validated upload or staged image, initial version,
metadata, prompt/recipe lineage, and object copy/move with compensating consistency.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No search/update/delete. Idempotent retry creates one asset. Preserve actual media metadata.
BEHAVIOR: Show transaction/compensation plan, wait for approval, implement rollback/replay/isolation tests.
```

*What 12.9a decided (2026-10-06). **The transaction is the whole design, and the prompt is inside it.**
Bytes are written before any row names them; the asset, its version, its tags, its recipe link, the staged
image's move to `Kept` and the prompt record all commit together or not at all; and the object is the only
thing compensated, under `CancellationToken.None` so it is not cancelled with the request. 12.3a's note said
the prompt must be written inside this transaction because `PromptRecord` is `IImmutableRecord` and one
committed beside a failed asset could only be erased — a test now holds that: moving the prompt save after
the commit reds `A_refused_prompt_leaves_no_asset_no_object_and_a_still_staged_image`.*

*Two things the build refused, and both were right.*
*— **A dependency cycle**, which turned out to be a symptom. The first cut had `MediaAssetDataLayer` inject
`IPromptRecordFacade` directly, and `PromptRecordBusiness` resolves `DamAssetId` through this module on its
way in — so the container refused the graph at startup. The fix that review found is the right one and it
dissolves the cycle rather than routing around it: **the prompt save goes down as a delegate from Business**,
which is exactly how `IAiOperationDataLayer.AcceptDraftAsync` composes the recipe module's create. Whether a
prompt is written is a decision and belongs in Business; the transaction it must happen inside belongs in the
data layer; neither needs the data layer to know another module's facade exists. The lookup keeps its own
facade and business — the split `IGeneratedImageLookupBusiness` makes, so the cross-module surface stays one
boolean — but reads through the one data layer, because the cycle that forced a second one is gone.*

*Worth recording because I got it wrong first: I justified the direct injection by citing
`AiOperationDataLayer`, which does inject `IAiUsageRecordingFacade` and `IAiQuotaAdmissionFacade`. Those are
side-effect recording, not composing another module's write — and for the writes, that same class takes
delegates. The precedent said the opposite of what I claimed it said.*
*— **A module-boundary violation.** Media named Content's `SavePromptRecordViewModel`, and a ViewModel is
one of the types `ModuleBoundaryTests` refuses by name. Content now publishes `PromptRecordSaveInput` and
`IPromptRecordFacade.SaveForAssetAsync`, which maps it and fills in the asset id from inside the caller's
transaction. The asset id is a parameter rather than a field, because the caller is the thing creating the
asset.*

*Idempotency is three layers and only one is the header. Keeping a staged image is **naturally** idempotent —
an image is kept once, so a repeat returns the asset the first call made, and removing that guard reds a
test. An upload has no natural key, because a creator may legitimately upload one photograph twice as two
assets, so there the `Idempotency-Key` is the only thing between a lost response and a duplicate — and a test
asserts that absence of a key really does create two, rather than leaving the gap undocumented. The unique
object key is the third layer.*

*`PromptRecord.DamAssetId` needed no column: it has existed since 12.3 and was simply never writable. This
prompt added the ViewModel field, the resolution through `IMediaAssetLookupFacade`, the composite foreign key,
and the deletion of the `PromptRecordValidatorTests` line pinning its absence — four parts of one change, as
12.6 did for `GeneratedImageId`. The migration needs no data cleanup, unlike `MediaAssetAggregate`'s, because
every stored value is null by construction.*

*On "copy or move": the staged bytes are copied, the image becomes `Kept`, and 12.8's retention reclaims the
staging copy — nothing is deleted until a committed asset owns one. **The sweep checks that rather than
trusting it.** A kept image is only purgeable once a `MediaAssetVersion` actually names it, which is
unreachable in production (only a committed creation sets `Kept`) but means that anything which ever set the
status without copying cannot make the sweep delete a creator's only copy. `StagedImageRetentionTests`'
kept-image case is what pins it.*

*From 12.3a (2026-10-04), and it constrains the compensation plan this prompt has to show: when this creates an
asset from a staged image, it calls `IPromptRecordFacade.SaveAsync` **inside its own transaction**, never after
the asset row commits. `PromptRecord` is `IImmutableRecord`, so `ImmutableRecordInterceptor` refuses every
delete — there is no compensating delete to write for a prompt row, and one committed beside an asset whose
object copy then fails could only be erased. `PromptRecordDataLayer.SaveAsync` deliberately opens no
transaction of its own so that it enlists in this one. This is also the prompt that adds `DamAssetId` to
`SavePromptRecordViewModel` together with the facade check that resolves it in the resolved workspace; until
then that field does not exist, and a test pins its absence.*

### 12.9b Search DAM assets

```text
SCOPE: Implement only DAM-002 paged search for channel, platform, day, style, cuisine, meal, tags, date,
recipe link, and free text, excluding soft-deleted assets.
CONSTRAINT: add-media-feature and add-endpoint skills; PERF-001/002.
RESTRICTION: No binary bytes in results; clamp page size; deterministic sort; workspace scope required.
BEHAVIOR: Show query/index plan, wait for approval, implement combined-filter/paging/isolation tests.
```

*What 12.9b decided (2026-10-06). **No new index, deliberately.** Two filtered indexes from 12.9 do the
seeking — `IX_MediaAssets_Workspace_CreatedAt` for the default ordering and `IX_MediaAssets_Workspace_Title`
for the alphabetical one, both `WHERE DeletedAt IS NULL`, so a tombstone never enters the seek rather than
being filtered out of it. Channel, platform, day, style, cuisine, course, the date bounds and the free-text
match are all **residual predicates** evaluated after one of those seeks has narrowed the read to one
workspace: a creator's library is thousands of rows, and an index per filter combination would be paid for on
every edit to save nothing measurable on a read. Tags and the recipe link are **semi-joins, not joins**, so an
asset carrying two requested tags appears once — a plain join would also corrupt the page size and the cursor
following it; `IX_MediaAssetTags_Workspace_Tag_Asset` and `IX_RecipeAssetLinks_Workspace_MediaAsset` serve
them. Answered during planning: **tags match any** of the ids given, matching `RecipeSearchFilters` and the
brand source list, and the **date bounds are the asset's own created date**, half-open — "when did this enter
the library" needs no join, while "when did I last use this" is a utilization question 12.9c surfaces.*

*The two things that cost a correction. `MediaAssetSearchRecord` first computed its cursor halves **in the
projection**, including `CreatedAt.ToString("O")`; EF would have client-evaluated that top-level `Select` and
read every column to produce two strings. It now carries the `Sort` and computes both as properties, which is
what `RecipeSummaryRecord` does and why. And `MediaAssetSearchViewModel` initially published **PascalCase**
query names (`Search`, `Channel`) because the OpenAPI generator takes the C# name: every parameter now carries
`[property: FromQuery(Name = "…")]` and a `[property: Description]`, as `RecipeSearchViewModel` explains at
length. Both were visible only in the regenerated snapshot — reading that diff is what caught the second.*

*A third, caught by the contract review: the page first published **`total`** where
`RecipeSearchPageServiceModel` and `PromptSearchPageServiceModel` both publish `TotalCount`. The layers below
all call it `Total` in their tuples, which is where the name came from, but three sibling paged routes
publishing two names for one fact is exactly the drift api-contract.md is about — and the published name is the
one that cannot be changed later. Renamed to `TotalCount`. Also corrected an isolation test of my own that
asserted `Assert.All(rows, ...)` over a prefix and so would have passed on an empty page; it now names both
expected rows, and the two workspaces are seeded at different instants so the expected page does not depend on
how the engine orders two GUIDs.*

*Promoted `QueryFilterParser` from `Modules/Recipes/Managers/` into **`Managers/Paging/`**, which the file's
own note had said should happen once a second module wanted it. It sits beside `ReferenceCursor` and
`PageBuilder` because every cursor-paged search needs all three, and stays `internal`.*

*One real tripwire, worth separating from the three known `BaseOutputPath` failures:
`ModuleBoundaryTests.The_scan_actually_sees_the_domain` asserts the file count is in `150..1000`, and 12.9b
crossed it at 1002. The ceiling is a sanity bound on the scan — it catches a root that has picked up `bin/obj`
or the whole solution — not a budget on the domain, so it was raised to 1400 rather than the new files not
being counted.*

*`SqlServerMediaFixture` is a **class fixture**, not per-test lifetime, and that is load-bearing: xUnit builds
a new instance of a test class per test method, so the container-owning `*SqlServerTests` pattern used
elsewhere — each with a handful of tests — would have started one SQL Server per test for the two dozen here.
It does add a container to a full run, which raises contention: a full suite showed
`AiUsageReconciliationConcurrencyTests.A_losing_pass_still_posts_the_rest_of_its_batch` failing after 32s and
passing in 2s when its class was re-run alone. Re-run a class before believing a concurrency failure.*

*Not cached, following `IRecipeFacade.SearchAsync`: a filtered page keyed by eleven filters plus a cursor has
a cache key per query and an invalidation scope of "any asset changed", which is a cache that pays for itself
on nothing. The response is `no-store` — a creator's private index of their own work must not sit in a shared
proxy (gateway.md).*

*Still owed, unchanged from 12.9: `MediaAsset` has no global `DeletedAt` filter, so this feature states
`DeletedAt == null` in its own query. The second reader should land one shared filtered query rather than
repeating the predicate, and a boundary test flagging a bare `db.MediaAssets` read would be worth having.*

### 12.9c Retrieve DAM detail

```text
SCOPE: Implement only DAM-003 detail with metadata, prompt/recipe lineage, utilization and version counts,
and paged or bounded histories.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No raw object path or cross-workspace link; soft-deleted detail follows approved policy.
BEHAVIOR: Plan ServiceModel, wait for approval, implement found/not-found/soft-delete/isolation tests.
```

*What 12.9c decided (2026-10-06). Approved during planning: a soft-deleted asset answers **404 unless
`?includeDeleted=true`**, versions travel **inline** while utilization is **cursor-paged** on its own route, and
lineage is **resolved to titles and labels** rather than left as ids.*

*The soft-delete policy is the one 12.9e needs. An ordinary read of a tombstone is a 404, so "exclusion from
ordinary reads" holds and a panel that forgot to check `deletedAt` cannot render a deleted asset as live. Asked
for explicitly it returns with `deletedAt` and `deletedByMembershipId` — the only way DAM-005 can ever report who
deleted an asset and when, and the reason `MediaAsset` still has no global filter on `DeletedAt`. The flag widens
which of the caller's **own** assets are visible and nothing else; a test proves it cannot reach the neighbour's.
Every byte route refuses regardless, and the utilization route has no flag at all — a creator looking at a
tombstone is reading its detail, not paging its usage log.*

*Two cross-module reads had to be added, and the architecture left no choice.
`IPromptRecordFacade.ListForAssetAsync` exists because prompt lineage points the **other way**:
`PromptRecord.DamAssetId` is Content's column, so Media cannot read it — no foreign key runs from an asset to a
prompt, and the boundary rule only lets an entity cross where one already does. It publishes a label, a kind and a
date and **never a prompt body** (ai.md); a test asserts the seeded text appears nowhere in the response.
`IRecipeFacade.ListTitlesAsync` exists because `RecipeAssetLink` is readable here (its key crosses into Media) but
`Recipe` is not — and because both live in one namespace, `ModuleBoundaryTests` would **not** have caught that
reach. Both compositions sit in Business, which is the house pattern: dozens of Business classes across Ai, Brand,
Content and AiUsage already inject a foreign module's facade. Verified before copying, after 12.9a's lesson about
citing a precedent without checking it.*

*The claim I had to withdraw. The detail first documented `recipeLinks` as possibly **shorter** than
`recipeLinkCount`, with a test seeding a link to the other workspace's recipe to prove it. The insert failed:
`RecipeAssetLink` carries `(WorkspaceId, RecipeId)` to `(WorkspaceId, Id)` on `Recipe` and cascades on delete, so
such a row is refused by the database — on SQLite as well as SQL Server — and the two counts **always** agree. The
test now asserts the agreement and, separately, that the database refuses the foreign link; the
`Where(ContainsKey)` filter stays as a guard against a future shape that key does not cover, documented as a guard
rather than a path, and the service model, controller and facade remarks were all corrected. A schema disproving a
design note is the good case — it was written down before it could be believed.*

*Other decisions. Four statements rather than one `Include` graph, because one query over three collections is a
cartesian product — three versions, four tags and two links is twenty-four rows carrying the root twenty-four
times — and `AsSplitQuery` would do the same thing less explicitly while still materialising entities. The three
counts ride on the root's statement as correlated subqueries; two tests pin them to separate values and prove
another asset's children do not inflate them. `ContentChecksum` **is** published on the detail and stays off the
search row, following `BrandSourceDocumentVersionDetailServiceModel`: it is what a download's `ETag` is built
from, so a client holding both can tell whether its bytes are current. `MediaConcurrencyToken` is Media's own,
duplicating Brand's and Recipes' almost exactly — a token is part of each module's published contract, and sharing
one helper would make three contracts move together.*

*`MediaErrorCodes.AssetSourceNotFound` became `AssetNotFound`, same published value `media.asset.not_found`, now
covering both the asset on a read and the staged source on a create. One constant rather than two with one value:
a client cannot act differently on them, and splitting it would publish a distinction whose only use is telling a
caller which of two things they cannot see exists.*

*Tooling note: `.OrderBy(x => x.Name)` **after** projecting a join into a record does not translate — EF cannot see
through the constructor to the property, and the whole class failed at once with one cause. Order on the column,
then project.*

*Still owed. 12.9i is what writes `MediaAssetUtilization`, so the history route reads a table nothing fills yet;
the schema and the paging are real and tested, but no end-to-end use exists until then. The utilization route takes
no filters — narrowing a usage log by platform or date is worth having once there is enough of it to narrow, and
adding parameters later is compatible.*

*What the reviewers caught. The isolation audit found no leak but three real weaknesses, all fixed.
`A_tag_join_cannot_name_the_other_workspaces_tag` **proved nothing**: it read B's asset from B's own scope and
asserted the tag id came back as B's, but that id comes from the `MediaAssetTag` row itself and `WorkspaceTag` is
keyed on `Id` alone — the assertion held whether or not the join was workspace-aware. Replaced with the real
guarantee one level down: `MediaAssetTag` carries `(WorkspaceId, WorkspaceTagId)` to `(WorkspaceId, Id)`, so a row
naming a neighbour's tag is refused. Same class of defect as the vacuous `Assert.All` in 12.9b — worth noticing that
both were tests whose summary described a stronger claim than the body made.*

*The two new lineage facades had **no isolation test of their own**, only inference from the global filter. They are
public facade surface anything may call with any id, so they are now called directly from workspace A with B's ids
and asserted empty, each with a same-workspace positive control beside it. Mutation-tested by adding
`IgnoreQueryFilters()` to both repository reads: both tests red. The utilization history gained a direct
cross-workspace read too, pinning the filter on `MediaAssetUtilizations` itself rather than on the visibility check
above it.*

*`deletedByMembershipId` is the only membership id this read publishes, which the audit flagged as inconsistent
with the test asserting no `createdByMembershipId` or `updatedByMembershipId`. It is deliberate — who deleted an
asset is the question DAM-005 exists to answer and no other route could — so the asymmetry is now asserted in that
same test and explained on the model, rather than left as a silent difference a reader would have to guess about.*

*Also from the audit, and a fair hit: `GetUtilizationAsync` was running the whole four-statement detail bundle,
counts included, purely to check visibility. It now uses a one-column `EXISTS` on the same filtered set — three
fewer statements per page, same 404 for an unknown asset, a neighbour's, and a tombstone.*

*The architecture review found no defects, and supplied a precedent I had not cited: `AiDraftAcceptanceBusiness`
says in so many words that "Business is the only layer permitted to reach another module", with writes still
delegated to the DataLayer so the transaction boundary stays there. That is exactly the shape 12.9a arrived at for
the prompt save and 12.9c uses for the two reads.*

*A full run showed two further failures — `PromptsEndpointTests` and `RecipePdfEndpointTests`, at 10s and 11s —
both passing when their classes were re-run alone. Same SQL container contention `SqlServerMediaFixture` added to
in 12.9b. The reliable signal remains: a slow failure in a full run is re-run before it is believed; a clean run is
the three `BaseOutputPath` root-discovery failures and nothing else.*

### 12.9d Update DAM metadata

```text
SCOPE: Implement only DAM-004 submitted-field metadata patch with optimistic concurrency and audit fields.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: Metadata update never replaces bytes/version, ownership, object identity, or immutable lineage.
BEHAVIOR: Plan patch semantics, wait for approval, implement partial/clear/conflict/isolation tests.
```

*What 12.9d decided (2026-10-07). Approved during planning: **`kind` is not patchable**, a patch aimed at a
tombstone answers **404**, and a successful patch returns **the full detail**.*

*`kind` records where the bytes came from — an upload, an import, a derivative, a model — which is lineage rather
than classification, and the restriction here is that a metadata patch never replaces lineage. Letting a creator
relabel an `AiGenerated` asset as `Original` would erase the only column that remembers a model was involved,
which ai.md requires be recorded. The 12.9 entity doc's "retitle, retag and reclassify" therefore means the
editorial fields: cuisine, course, channel, platform, style, day and tags. `kind` is **absent from the view model**
rather than validated away, so it is unrepresentable; a test sends it anyway along with eleven other forbidden
fields and asserts none of them moved.*

*Nothing had to be invented for the merge semantics. `PatchField<T>` already exists in the shared kernel with
absent / set / clear encoded in the type, and `UpdateRecipeViewModel` already uses it — so this is the fourth
PATCH in the codebase and the third to inherit the same three-state contract. Tags replace rather than merge,
following the recipe patch: merging leaves no way to remove one, and an add/remove pair would be two ways to say
one thing. An empty list and an explicit `null` are the same request.*

*The validation decision worth recording: the merge produces a `MediaAssetMetadataInput` and runs
**`MediaAssetInputChecks.Metadata` — the creation path's own checks — over the merged result**, not per-field patch
rules. A patch therefore cannot leave an asset in a state a create would have refused, and no rule is written
twice. One consequence falls out for free: clearing `title` fails with "A title is required", because that is
already what the shared check says about a missing one.*

*Concurrency is two guards, not one, and that distinction is the whole of what went wrong first. The caller's
`expectedConcurrencyToken` is compared in Business against the row as loaded; separately, `RowVersion` is a real
`rowversion`, so EF puts the original value in the `UPDATE`'s `WHERE` and the database refuses a write whose row
moved between this transaction's read and its write. **Neither covers the other.** EF's guard cannot catch a
caller quoting an older read when nobody else has written since — Business loads the row fresh, so the guard
compares the current value against itself and succeeds. A malformed token is a 400 naming the field rather than a
409, because a conflict would be a lie about a token that never existed; that is what
`MediaConcurrencyToken.IsWellFormed` is separate from `Matches` for. A no-op patch writes nothing and leaves the
token spendable, so **there is no way to "touch" an asset**.*

*Audit fields, no audit event: `UpdatedAt` and `UpdatedByMembershipId` are set in the DataLayer so no write path
can forget them, while `WorkspaceId`, `CreatedAt` and `CreatedByMembershipId` are never assigned. 12.9a's create
writes no audit entry either and DAM audit is deferred to the creative-production audit prompt — adding one here
only for edits would be inconsistent.*

*The test-shape lesson, and it cost two wrong turns. The conflict tests were written against the gateway's SQLite
host first and **passed for the wrong reason, then failed honestly**: `SqliteModelCustomizer` gives every
concurrency token a `randomblob(8)` default so inserts work, but nothing bumps it on update. Under SQLite a token
therefore always looks unchanged, a stale one always matches, and three assertions were untestable or vacuous — the
"refreshed token differs" check, the two-editor conflict, and the no-op's "token still valid". All three moved to
`MediaAssetPatchSqlServerTests`, where `rowversion` is a real database behaviour, and the SQLite class now says in
its own remarks why they are not there.*

*Then mutation testing found a hole the move had opened. Breaking the merge reddened two tests; **breaking
Business's token check reddened nothing**, because the SQL Server tests exercised the DataLayer directly and
nothing covered Business calling `Matches`. That is exactly the case EF's `WHERE` cannot catch, so the guarantee
had no test at all. Closed by driving `IMediaAssetBusiness.PatchMetadataAsync` with a superseded token on SQL
Server — which meant `SqlServerMediaFixture` had to register what Business transitively needs (Content, Recipes,
Vocabulary, Measurement, Ingredients, a cache, an outbox, idempotency and the AI proposal lookup). That set was
copied from `MediaAssetCreateTests` rather than rediscovered one DI failure at a time, which is how it was found
the first time. Re-running the same mutation now reds the new test.*

*Worth watching: the Media SQL Server fixture is a second container, and full runs now show two or three slow
failures that pass when their class is re-run alone — this time `BrandSourceExtractionReviewEndpointTests`,
`RecipeTestRunEndpointTests` and `PromptsEndpointTests`, all at 10–11s. Three prompts running, three different
victims, same signature. A clean run remains the three `BaseOutputPath` root-discovery failures and nothing else,
but the contention is no longer occasional and a shared fixture may be worth considering before it costs real
debugging time.*

*What the reviewers caught, and two were real defects rather than weaknesses. The **idempotency fingerprint
ignored the patch body** — it was `$"{assetId}|{token}"`, where the create and keep paths both fingerprint their
whole payload. One key with two different edits therefore matched, and the second was replayed as the first with
`Idempotent-Replayed: true`: the client told it had succeeded, the edit silently dropped. The obvious fix does not
compile — `PatchFieldJsonConverter.Write` throws by design, so the view model cannot be serialized — so the patch
now publishes a `Fingerprint()` that puts **only submitted fields in a dictionary**, which is what keeps the three
states apart through serialization: a `title` key with `null` is a request to clear, and no `title` key at all is a
request to leave it alone. Tags are ordered so re-sending the same set is not a false mismatch.*

*And **`CuisineId`/`CourseId` were never checked for existence**. Both are client-supplied ids into shared
vocabulary behind `Restrict` foreign keys, so a stale id reached `SaveChanges`, threw `DbUpdateException` — not the
`DbUpdateConcurrencyException` the write path catches — and surfaced as a **500**. Confirmed with a test before
fixing it. The check went into `ResolveAsync`, which the create path shares, so 12.9a's identical gap closed with
it.*

*The third finding was mine to own: I had claimed the stale-token 409 could not be tested over SQLite and moved all
of it to SQL Server. Half right. The **two-editor** case genuinely cannot be — SQLite never moves a token — but a
token that was never that row's mismatches deterministically, because every row is seeded `randomblob(8)` and eight
zero bytes are not that. So the HTTP contract around a conflict is now proved at the endpoint where it belongs, with
a second test spending one asset's token on another, and SQL Server keeps only what a real `rowversion` can show.*

*Both fixes were mutation-tested: reverting the fingerprint to the id alone, and removing the cuisine check, reds
one test each.*

*Worth raising rather than just recording: **the SQL-container contention is no longer occasional.** This run
produced seven slow failures at 10–12s — across `BrandStyleGuideCompare`, `BrandStyleGuideCreate`,
`BrandSourceDocumentList`, `BrandSetupSession`, `RecipeDuplicate` and `Prompts` — every one passing when its class
was re-run alone. They are SQLite endpoint tests, so they are not waiting on SQL Server themselves; they are starved
while containers start. The count has gone 1 → 3 → 7 over 12.9b, 12.9c and 12.9d. A clean run is still the three
`BaseOutputPath` root-discovery failures and nothing else, but the signal-to-noise is bad enough now that a full run
no longer tells a reviewer much without six re-runs. Sharing one container across the `*SqlServerTests` classes, or
serialising the fixtures, is worth doing before it costs real debugging time.*

### 12.9e Soft-delete DAM asset

```text
SCOPE: Implement only DAM-005 soft delete with confirmation policy, timestamp/actor, linked-content impact,
and exclusion from ordinary reads.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No physical object deletion in request path. Do not silently delete recipe/card links.
BEHAVIOR: Plan linked-state behavior, wait for approval, implement repeat/delete/read/isolation tests.
```

*What 12.9e decided (2026-10-07). Approved during planning: **links stay and the response reports the impact**, a
repeat answers **200 with the existing tombstone**, and deleting requires **Editor** where creating and patching
require Contributor.*

*The linked-state rule is the whole shape of this prompt. All three link types — `RecipeAssetLink`,
`BrandAssetLink`, `TestAttachmentLink` — carry workspace-paired `Restrict` foreign keys into `MediaAsset`, so a hard
delete was never representable while any existed; a soft delete leaves every row standing. Cutting a recipe's
photograph silently is the one thing DAM-005 must not do, so instead of writing into two other modules the response
**names** what is now pointing at a tombstone: `affected.recipes` with titles through `IRecipeFacade.ListTitlesAsync`
(12.9c's method, reused), `brandProfileCount` and `testAttachmentCount` as counts, and `affected.any` as the one flag
a client needs. Recipes are named because a recipe is the thing a creator goes and fixes; the other two are reached
from their own screens, and naming them would mean two more facade methods for something nobody asked to see. A
recipe linked twice is reported once — distinct recipes, not link rows.*

*Nothing physical is removed and nothing is scheduled to be. The objects stay, which is what makes the operation
answer fast and makes a half-done deletion impossible: there is no second system to fail. A test asserts the version
row, its object key and its checksum all survive, and that the asset still points at version 1 — so there is nothing
to repair if a restore route ever arrives. **There is no restore route in this prompt**, deliberately: `DeletedAt` is
set and nothing clears it.*

*`confirmed: true` is required, following `ActivateBrandStyleGuideVersionViewModel`: a deletion should be a step a
creator took in the interface, not somewhere a well-formed body arrives (publishing.md's confirmation rule).
Checked before the token, so a client that forgot the flag is told about the flag rather than about concurrency, and
a request wrong in both ways hears about both.*

*Editor rather than Contributor, which is the one place this prompt departs from 12.9a and 12.9d.
`RecipesController.Archive` made the same call with the same reasoning — archiving "takes a recipe out of every
collaborator's library rather than contributing to one" — and removing a shared asset can leave somebody else's
recipe pointing at a tombstone.*

*The ordering that matters, and it is borrowed rather than invented. **The token is checked before the
already-deleted answer**, following `RecipeBusiness.TransitionAsync`, which says why: a caller quoting a stale token
has not seen what the asset looks like now, and answering "already deleted" would hide a collaborator's work from
them. The consequence worth stating plainly is that retrying a deletion whose response was lost needs a fresh read
first — and with a current token the repeat is then a true no-op: original timestamp, original actor, no second
audit entry, token unmoved. No idempotency key, because the command is already idempotent and a key would only add a
store to the path.*

*An audit event **is** written here, and that is a deliberate departure from 12.9a and 12.9d, which both wrote audit
fields and no event while deferring DAM auditing to the creative-production audit prompt. auth.md names "destructive
deletion" explicitly among the operations requiring one, and an asset leaving every collaborator's library with no
record of who removed it is exactly the case that rule exists for. `IAuditWriter.Record` stages on the ambient
context, so the entry and the tombstone commit together or neither does — a test proves a refused concurrent write
takes its own entry with it. The summary carries the title and nothing else the creator wrote: no description, no
object key, no count of anybody else's content.*

*Mutation testing found one hole. Breaking the token ordering and the confirmation gate each reddened tests, but
**weakening the facade's role gate to Contributor broke nothing** — the route's `WorkspaceEditor` policy refuses
first, so the facade check never runs and the endpoint test proving a contributor cannot delete would pass with that
check deleted. Two guards are right; only one was covered. Closed by giving `SqlServerMediaFixture.ScopeFor` an
optional role and calling the facade directly with a Contributor context, which is the only way to reach it.
Re-running the same mutation now reds that test.*

*The contention is now the loudest thing in a full run: **eight** slow failures this time (7–12s), across
`WorkspaceWeeklyThemeSqlServer`, `WeeklyThemes`, four Brand source/style classes, `BrandStyleGuideApproval` and
`BrandSourceDocumentReplace` — every one green when its class was re-run alone. The count has gone 1 → 3 → 7 → 8
across 12.9b–12.9e. A full run now needs eight re-runs before it says anything, which is past the point where it is
a useful signal. Sharing one SQL Server container across the `*SqlServerTests` classes is the fix and is worth doing
before the next prompt.*

*What the reviewers caught, and one of their findings was wrong on the facts. The architecture review found no
defects. The isolation audit found no leak but three things worth acting on, plus one I had to check before
believing.*

*The real one: **the audit summary carried the asset title**, and that is creator-authored content.
`AuditLog` says `Summary` "must never carry ... content", and `RecipeBusiness` spells out what that means where it
writes its own entries — "no title, no creator text". A title here would have been this module deciding the rule
applies less to it. The summary is now content-free, the asset is identified by `ResourceId`, and
`BeforeReference` carries the version number it stood at — a pointer, which is what that field is for. The test
flipped from asserting the title is present to asserting it is absent, along with the description, the object key
and the file name. Mutation-tested: putting the title back reds it. Worth noting the architecture reviewer read the
old summary and approved it; the explicit rule in `AuditLog` plus the Recipes precedent is the stronger authority.*

*Two test gaps, both closed. `testAttachmentCount` was asserted as zero everywhere and never made non-zero, so
that query could have been miswired entirely — it now seeds a run with three attachments and asserts the count,
with the other workspace holding two of its own. And `A_member_of_one_workspace_cannot_remove_the_others_asset`
looped over a one-element array for no reason; it now says why both slugs are worth testing, because they fail at
different layers — B's slug is the query filter refusing, A's slug is membership resolution refusing before the
action runs at all. A new test pins the refusal ordering so a foreign asset can never answer 409.*

*The finding that did not survive checking: the audit called the unseeded attachment count "the exact leak" of a
lost workspace filter. It is not. `TestAttachmentLink` carries `(WorkspaceId, MediaAssetId)` to
`(WorkspaceId, Id)`, so every attachment row for an asset is in that asset's workspace by construction, and the
asset id was already resolved through the filtered read — the `MediaAssetId` predicate does the isolating. Adding
`IgnoreQueryFilters()` to that query changes no result, which is exactly what the mutation showed. The same holds
for the recipe and brand counts. The test's own remarks now say what it does and does not prove, because "the
filter is untested here" and "the filter is not what protects this" look identical from outside.*

*And one more comment that overclaimed, the third time this has come up in this group of prompts:
`Removing_an_already_removed_asset_changes_nothing` read as though its call sequence proved the
token-before-already-deleted ordering. It cannot — SQLite never moves a rowversion, so the "current" token it
passes is the original. The SQL Server test carries that proof and the comment now says so. The recurring lesson is
that a test whose summary describes a stronger claim than its body makes is worse than no test, because it stops
anyone looking.*

*Left as it stands, with the reasoning recorded: `IMediaAssetBusiness.SoftDeleteAsync` has no role check of its own,
only the facade's. backend.md does put resource authorization in Business, but all three DAM write paths — create,
patch, delete — gate the role in the facade, and changing only this one would be the inconsistency rather than the
fix. If anything other than the facade ever calls Business directly, all three need revisiting together.*

### 12.9f Render latest DAM image

```text
SCOPE: Implement only DAM-006 authorized inline rendering of the latest active version.
CONSTRAINT: add-media-feature and add-endpoint skills; API-010.
RESTRICTION: Accurate content type/cache policy; no raw blob redirect/path; exclude soft-deleted assets.
BEHAVIOR: Plan response/range/cache behavior, wait for approval, implement found/missing/isolation tests.
```

*What 12.9f decided (2026-10-07). Approved during planning: **`private, no-cache` with a strong ETag**, and
**`/content` for the inline render** with `/download` left to 12.9g.*

*The cache decision turns on one fact worth writing down: **this URL does not name a version.** It serves whichever
version the asset's counter points at, so `immutable` or any `max-age` would be wrong — a creator who just added a
version would keep seeing the old image for the length of the window, with nothing to tell them why. Revalidating
every time costs one conditional request and a 304 sends no bytes, which is cheap enough for a grid rendering the
same asset repeatedly. `private` keeps it out of any shared proxy, which gateway.md requires of personalised
responses. 12.8 chose `no-store` for staged images and that was right there — a staged image is looked at once while
deciding whether to keep it — but a library asset is rendered on every card and recipe page.*

*The ETag is the **store's** checksum, not the row's. A version's bytes are write-once so the digest identifies the
representation exactly, and a new current version changes it, which is precisely what a "latest version" route
needs. The test seeds a row whose `ContentChecksum` is deliberately *not* the real digest, so a test asserting on
the tag cannot pass by reading the database instead of the response.*

*No ranges, and said rather than implied: `Accept-Ranges: none`, `enableRangeProcessing` left off, and no
`EntityTag` on the `FileStreamResult` so exactly one place compares a tag. A `Range` header is ignored — the whole
image comes back, not a 206 and not a 416 — because these are images a browser renders in one pass and offering
ranges would be a contract to keep for no benefit. 12.8 took the same line.*

*`inline` **with no filename**: a rendered image is not being saved, and the creator's own file name is 12.9g's
business. One less piece of creator text on a route that does not need it.*

*What had to be built: `IMediaAssetObjectGateway` had `PutVersionAsync` and `DeleteAsync` and no read, so it gained
`OpenReadAsync` — which **re-checks the key's workspace** exactly as `DeleteAsync` does, on the same principle that
finding a key is not authorization. That is the second guard behind the query filter that produced the key, and a
test sets up the state no write path can produce (a version row pointing at another workspace's key, written with
raw SQL because `MediaAssetVersion` is immutable) to prove the gateway refuses rather than serving the wrong bytes.
The repository read is the narrowest in the module and **the only one that selects `ObjectKey`** — no title, no
description, nothing else — because a key read alongside creator content would be a key sitting in a layer that
publishes things. It stops at the gateway and is never returned.*

*Four causes, one 404: an unknown id, another workspace's, a tombstone, and an asset whose current version row is
missing. Storage being unreachable is a **503** instead, because the asset is there and retrying is the remedy —
the same split the staged-image open and the brand source download both make. A version row with no object behind
it is a 404 rather than a 503: there is nothing to retry for.*

*The lease is registered with `RegisterForDisposeAsync` before anything that can throw, so the 304 path releases it
too. A test asserts the store's open-read count returns to zero on the bytes path, the not-modified path, and the
storage-failure path — a leaked stream is the characteristic failure of a proxied download and is invisible to a
test that only reads the body.*

*Mutation-tested: dropping the gateway's key check, dropping the tombstone exclusion, and weakening the cache header
to `public, max-age=600` each red tests — four between them.*

*Contention, again and worse: **nine** slow failures this run, one at 27 seconds, across `AiUsageReconciliation`,
four Brand classes, `RecipePdf`, `RecipeTestRunHistory` and `Prompts` — all eight re-ran green alone. The trend
across 12.9b–12.9f is 1 → 3 → 7 → 8 → 9. Raised with the user twice now; the fix is sharing one SQL Server
container across the `*SqlServerTests` classes and it is worth its own change before 12.9g.*

### 12.9g Download latest DAM version

```text
SCOPE: Implement only DAM-007 authorized download of the latest version with accurate type, extension,
and deterministic safe filename.
CONSTRAINT: add-media-feature and add-endpoint skills; API-010.
RESTRICTION: No selected historical version or raw object path in this prompt.
BEHAVIOR: Plan disposition/filename, wait for approval, implement type/name/isolation tests.
```

*What 12.9g decided (2026-10-07). Approved during planning: the download is named **`{title-slug}-v{n}.{ext}`** —
`soda-bread-hero-v2.jpg` — matching `BrandSourceDownloadFileName` exactly, so the two downloads in this codebase are
named alike.*

*Three properties, each with a reason rather than a preference. **Deterministic**: the same asset, version and media
type always give the same name, with no timestamp or counter. **Safe by construction**: `FileNameSlug` reduces any
title to lower-case ASCII letters, digits and single hyphens, so nothing a title carries can become a path, a
traversal or a second header — a theory covers `../../etc/passwd`, a Windows path, an embedded quote, a CRLF and a
`; filename=` and asserts the name arrives parseable with exactly one dot. **Accurate**: the extension comes from the
**stored media type**, never the uploaded filename, which is optional creator text that may claim `.jpg` over PNG
bytes. A media type with no known extension yields a name with none rather than a guessed one — a missing extension
is recoverable where a wrong one misleads whatever opens the file. A title that folds away entirely falls back to
`image`, because `-v2.jpg` is worse than `image-v2.jpg`.*

*The `-v{n}` is load-bearing rather than decorative: saving version 1 and version 2 without it gives two identical
names and the second silently becomes "… (1)", with nothing recording which is which.*

*The two routes **share one sender**, so rendering and downloading differ by exactly two headers and nothing else can
drift. That is the argument 12.8's staged pair makes, and a test asserts the ETag, the cache policy, the range policy
and the sniffing header are identical across `/content` and `/download` — so a change reaching one and not the other
fails. `MediaAssetRender.FileName` is null for a render, which makes "a render names no file" a shape rather than a
convention somebody has to remember.*

*One correction to 12.9f, recorded because the claim was mine. Its repository read was documented as touching
"nothing else about the asset — no title, no description, no tags". The download names its file from the title, so
the record now carries it and the comment says "nothing else **but** the title". The alternative was a second query
for one column; narrowing the claim was the honest choice over narrowing the read.*

*And one real bug the test database caught. Adding `asset.Title` to that read turned a correlated `SelectMany` into
**SQL APPLY**, which SQLite does not support — every render answered 500 under the gateway tests. Worth being precise
about what this was: SQL Server supports `APPLY`, so production would have worked and only the SQLite suite failed.
It is a portability trap rather than a production defect, and it is exactly what running the endpoint tests on a
second engine is for. Rewritten as a join on `(asset id, current version number)`, which translates on both.*

*Contention: eight slow failures this run across two Brand classes, `RecipeUpdate`, `WeeklyThemes` and
`BrandSourceDocumentList`, all green alone. The trend across 12.9b–12.9g is 1 → 3 → 7 → 8 → 9 → 8. Raised three
times now; the shared-container fix is still outstanding and still worth doing before the next prompt.*

### 12.9h Download historical DAM version

```text
SCOPE: Implement only DAM-008 authorized download of one selected version belonging to the requested asset.
CONSTRAINT: add-media-feature and add-endpoint skills; API-010.
RESTRICTION: Verify both identifiers and workspace; do not fall back to latest on missing version.
BEHAVIOR: Plan contract, wait for approval, implement ownership/not-found/type/isolation tests.
```

*What 12.9h decided (2026-10-07). Approved during planning: **`private, no-cache` with a strong ETag**, uniform with
the other two byte routes.*

*The interesting part is what was turned down. This is the one DAM route whose URL is **genuinely immutable** —
version 2's bytes cannot change — so `max-age=31536000, immutable` would have been literally true here where it would
be a lie on `/content` and `/download`. It was still declined, for a reason that is about access rather than
correctness: a long `max-age` leaves a usable copy in the browser cache of somebody who has since been removed from
the workspace, where revalidating refuses them. A downloaded file is on disk anyway, so the caching win was small
against that. Keeping all three uniform also means the shared sender needs no branch at all, which is why the policy
is asserted equal across the three routes rather than written three times.*

*By **version number**, and that is forced rather than chosen: `MediaAssetVersionServiceModel` publishes
`VersionNumber` and deliberately not the row's `Id`, so the number is the only identifier a client has ever been
given. Route shape mirrors `BrandSourceDocumentsController`'s `/versions/{versionNumber:int}/content`.*

*The two restrictions, each with a test that fails when the rule is removed. **Both identifiers together:** the
lookup joins on `(asset id, version number)` as one predicate, so two assets that each have a version 2 cannot reach
each other's bytes — keying on the number alone reds two tests. **No fallback:** a number this asset has no version
for is a 404, never its current version; adding `?? FindCurrentVersionObjectAsync` reds five. Zero and negative
numbers answer the same 404, because `CK_MediaAssetVersions_VersionNumber_Positive` makes them unrepresentable and
there is nothing to disclose.*

*A tombstoned asset is a 404 on this route too: removing an asset takes its **history** out of reach, not just its
latest bytes, and a test checks both version 1 and version 2 after a delete.*

*Refactoring rather than adding, which is most of what this prompt was. The DataLayer's two opens now share one
private `OpenAsync` over whichever version row was found, so the gateway call, the storage-failure split and the file
naming cannot drift between "the current version" and "this version". Business shares one `Opened` mapper so the
three routes cannot answer the same outcome differently — which would make one a softer way in than the others. And
the controller's sender now takes the already-opened result and derives the disposition from
`MediaAssetRender.FileName` instead of a flag of its own, so a response cannot claim to be an attachment with no name
or a render with one.*

*Contention: one slow failure this run (`BrandSourceExtractionReview`, 11s), green alone. The trend across
12.9b–12.9h is 1 → 3 → 7 → 8 → 9 → 8 → 1 — this prompt added no fixture, which is consistent with the cause being
container count rather than anything about these tests. The shared-container change is still worth doing and is still
outstanding.*

### 12.9i Log DAM utilization

```text
SCOPE: Implement only DAM-009 utilization logging with platform, utilized date, derived day, campaign,
notes, actor, and timestamp for an active authorized asset.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No utilization for missing/soft-deleted/cross-workspace assets. Derived day uses explicit zone.
BEHAVIOR: Plan contract, wait for approval, implement derivation/validation/replay/isolation tests.
```

*What 12.9i decided (2026-10-07). Approved during planning: **the date is required and the day derives from it**, and
**an optional idempotency key** as the other DAM creates have.*

*The zone question deserves recording because the answer was to remove it rather than answer it. The restriction says
"derived day uses explicit zone", which anticipates a design where the server dates the log from "now" and has to pick
a zone to do it. Requiring the date instead means **a calendar date has exactly one day of the week in every zone**, so
there is no instant to convert and therefore no conversion to get wrong — stronger than naming a zone, not a dodge of
the requirement. It also avoided a dependency that would not have held: `Workspace` has **no** timezone at all, only
`BrandProfile.TimeZoneId`, which is nullable — so "today in the workspace's zone" would have needed a cross-module read
into Brand plus a silent fall back to UTC's today for any workspace that never set one.*

*The day is derived in the DataLayer and `utilizedDay` is absent from the contract, so a client cannot send one that
disagrees with its own date. A test sends `utilizedDay: "Monday"` with a Wednesday date and asserts Wednesday is
stored; another walks all seven days; a third pins Sunday specifically, because `DayOfWeek.Sunday == 0` is the value a
"treat the default as unset" bug hides in.*

*The future bound is `TestRunPolicy.FutureTolerance`'s reasoning applied to a date: **one day, no floor.** Not zero,
because a creator in UTC+13 logging this afternoon is already on tomorrow's date by the server's reckoning and
refusing them would refuse a correct request — no inhabited offset exceeds +14 hours. Not generous, because the error
this catches is a mistyped year and a use dated 2099 would sit at the top of an asset's history permanently. No lower
bound at all: recording where a photograph was used last year is a creator entering their own history, and a product
that refused it would be telling them their records are wrong.*

*`platformKey` stays opaque — required, non-blank, length-bounded, validated against no catalogue, because nothing
else in the codebase validates a platform key against one. Whitespace in `campaignName` or `notes` is stored as absent
rather than as blanks, so a history never shows a campaign whose name is three spaces.*

*Replay: an optional key, and **without one two identical calls record two uses.** That is the intended behaviour
rather than a gap — an asset genuinely can go out twice on one platform on one day, which is why
`MediaAssetUtilizationConfiguration` imposes no uniqueness over `(asset, platform, date)` and carries only a
non-unique index for the history read. A test asserts the two rows, so the behaviour is pinned rather than assumed.
The fingerprint covers the asset and every recorded field, learning 12.9d's lesson: one key with a different log is
refused rather than replayed as the first.*

*Visibility is checked **inside the same call that writes**, not by the caller beforehand, so there is no window where
an asset is deleted between the check and the insert. The composite foreign key would refuse a row for a non-existent
asset anyway; what the check adds is refusing one for a **tombstoned** asset, which the key cannot see. Missing,
soft-deleted and cross-workspace all answer one 404.*

*Mutation-tested: letting the visibility check include tombstones, freezing the derived day to a constant, and
removing the future bound red 11 tests between them.*

*Contention: eight slow failures this run (12–14s) across four Brand classes, `RecipeUpdate`, `RecipePdf` and
`Prompts`, all green alone. The trend across 12.9b–12.9i is 1 → 3 → 7 → 8 → 9 → 8 → 1 → 8. This prompt added no
fixture either, which continues to point at container count rather than anything about the tests.*

### 12.9j Add DAM version

```text
SCOPE: Implement only DAM-010 validated new-version upload with atomic next version number, immutable
original, accurate media metadata, object storage compensation, and audit fields.
CONSTRAINT: add-media-feature and add-endpoint skills.
RESTRICTION: No in-place blob overwrite. Concurrent uploads cannot share a version number.
BEHAVIOR: Show transaction/compensation plan, wait for approval, implement concurrency/failure/isolation tests.
```

*What 12.9j decided (2026-10-07). Approved during planning: a race loser gets a **retryable 409 with no server-side
retry**, and the upload is **bytes only**.*

*The plan, and why the order is forced. A version's object key embeds its number, so the number must be chosen before
anything can be written — which means the allocation cannot be a reservation and the race has to be resolved by the
store. `MAX(VersionNumber) + 1`, then put, then one transaction inserting the row and bumping the counter together, with
the object removed if that transaction fails. `MAX + 1` rather than `CurrentVersionNumber + 1` because it cannot
allocate a number that already exists even if the two ever drifted.*

*Three independent things make two uploads unable to share a number, and none is trusted alone: the store is
create-only, `(MediaAssetId, VersionNumber)` is unique, and the asset's `RowVersion` guards the counter bump.*

*The one rule here that would be a **data-loss** bug to get wrong: a request refused with `AlreadyExists`
**compensates nothing.** 12.9a never had to think about this because its key contains a freshly-minted asset GUID, so
two creates cannot collide. A version key is fully deterministic, so both uploads compute the same one — and a loser
that "cleaned up" would delete the winner's committed bytes. There is a test that sets that state up directly rather
than waiting for the race, and mutation-testing it (making the loser compensate) reds two tests.*

*Reservation was rejected for a concrete reason, not on taste: bumping the counter before the version row exists leaves
`CurrentVersionNumber` pointing at a version that is not there if the upload then fails — an asset that renders nothing
until the next successful upload. The chosen order leaves a failed upload with nothing written at all.*

*Bytes only. A patch is JSON with a concurrency token and merge semantics; an upload is multipart, and **form fields
cannot express absent-versus-clear** — exactly the ambiguity `PatchField` exists to remove. Alt text in particular is
left alone rather than cleared: the image changed, so the old description may now be wrong, but it is the creator's own
words and deleting them because a file changed is not this route's decision. A test asserts no metadata moves.*

*Contributor, where **removing** an asset needs Editor — adding a version takes nothing away, because every earlier
version keeps its row, its object and its download route. A test downloads version 1 after version 2 lands to prove it.*

*The byte acceptance — bound, signature-inspect, scan — was **extracted and shared** with the create path rather than
copied, so a format one takes and the other refuses is not representable. That was a refactor of 12.9a, not new code.*

*Two things worth recording from the testing. First, `GatewayClient.SendAsync` **JSON-serializes whatever it is given**,
so passing multipart content through it answers 400 on every upload; `PostAsync` is the overload that takes
`HttpContent`. Second, the concurrency test originally asserted "at least one upload succeeded", and that is **not an
invariant**: SQLite serializes writers, so under contention both commits can be refused. It now asserts only what must
hold either way — no duplicate numbers, the counter matching the highest committed version, and every committed
version's object still present — with a comment saying why a success is not required. It survived three consecutive
runs after the change.*

*The post-commit-failure compensation has a deterministic test, forced by colliding on `MediaAssetVersion`'s unique
`ObjectKey` index — another asset is given a row already claiming the key this upload will compute, so the object write
succeeds and the row insert is refused. The same technique proved 12.9a's compensation. Mutating that path to leave an
orphan reds it.*

*This completes DAM-001 through DAM-010. Contention: six slow failures this run, all green alone.*

### 12.8a Read an image operation - done

```text
SCOPE: Implement the read that makes 12.7's 202 usable: one image operation of the resolved workspace with
the images it has staged so far, through the complete Controller to Repository seam.
CONSTRAINT: add-endpoint and add-media-feature skills; IMG-003/005.
RESTRICTION: No prompt text, object key, checksum or address of any kind in the response. No new write path.
BEHAVIOR: Build bottom-up, test found/not-found/isolation/disclosure, regenerate the OpenAPI snapshot and
read the diff.
```

*Why this prompt exists (2026-10-07). It was **not** in the plan: 12.10b asked for image-operation progress, a
one-to-four grid, a lightbox, keeper selection, download and rejection, and none of it was reachable.
`GeneratedImagesController` offered `POST`, `/preview`, `/content` and `DELETE`, and
`GeneratedImageOperationServiceModel` carries a status and two counts but **no image identities** — so a client had an
operation id and no way to learn a single thing in it. `IGeneratedImageGenerationFacade` had only `RequestAsync` and
`ExecuteAsync`; `IStagedImageFacade` only `OpenAsync`, `RejectAsync` and `RunRetentionAsync`. 12.6's own note had
anticipated the surface ("12.8's detail surface is where a creator reads their own request back") and 12.8 never
built it.*

*`GET api/v1/workspaces/{slug}/generated-images/operations/{operationId}`, Viewer and above, through
Controller → Facade → Business → DataLayer → a new projection-only repository. Split out as its own prompt rather
than folded into the UI one, because this file's rule is that a prompt crosses layers only when it is explicitly a
vertical-integration prompt — and because an API shape deserves a review of its own rather than arriving inside
several hundred lines of Angular.*

*Three decisions. **Viewer**, matching `/preview` and `/content`: a member who may look at an image but not learn that
it exists would be a contract at odds with itself. **No rule about who asked** — an operation belongs to the workspace,
not to the member who requested it, so a colleague picking up someone's shoot is the ordinary case and a gate on
`RequestedByMembershipId` would break shared work while disclosing nothing extra. And **the existing generation facade
was extended** rather than given a sibling: it is the same resource, the controller already injects it, and a second
seam would have been boilerplate.*

*`StagedCount` is deliberately redundant with `Images.Count` — a row exists only once there are bytes to describe, so
the rows *are* the staged count. It is published anyway so a client decodes one operation shape rather than two, and so
a progress meter reads a number rather than deriving one.*

*Projections throughout, copying `MediaAssetDetailRepository`'s discipline: `PromptText`, `ObjectKey` and
`ContentChecksum` are never **loaded**, which is stronger than reading the row and dropping them, because a later edit
cannot reintroduce one by accident. The logged SQL is the proof — nine columns from each table, none of them those.
Two statements rather than one, since a single query over the collection would repeat the operation's columns once per
image.*

*The `FailureSummary` doc comment first said "sanitised server-side", which was an assertion rather than a fact. It is
now checked and the reason recorded: `GeneratedImageResult` carries an outcome and bytes and nothing else, so a
provider's words have nowhere to travel — every summary is a fixed sentence written by Business. A provider payload
could not reach the response without a new field on the gateway's own result type.*

*What review changed. The isolation audit found a defect I had introduced: inserting the new error code anchored on the
constant rather than on its doc comment, which orphaned `StagedImageNotFound`'s `<summary>` onto the new constant and
left it with two. It also judged two tests weaker than their names — the "exactly the same not found" one
compared only `code`, so it now compares the whole `ProblemDetails` minus `traceId`, and the slug-level
non-disclosure existed only as a comment, so a workspace the caller is not in is now asserted to answer byte for byte
the same as a slug that was never created. A Viewer of one workspace reading another's operation, and `no-store` on a
refusal, are both tested too — a cached 404 is a small signal about which ids exist, so the header moved above the
branch. `@architecture-reviewer` approved the seam with no findings.*

*14 endpoint tests pass; the Media area is 530 green and `OpenApiContractTests` 26. The snapshot diff is **+235 lines,
0 deletions** — one path and two schemas, nothing existing altered. A full backend run reds 11, all of them the known
artefacts: 3 `BulkOperationBoundaryTests` from redirecting `BaseOutputPath`, and 8 SQL-container contention failures
across five classes, every one of which passes when run alone.*

### 12.10 Content Pipeline configuration and seed UI - done

```text
SCOPE: Build only PIPE-UI-001/002 through configuration and seed review: channel, day, variant count,
scene, style, concept, generate seed, edit/accept seed, and persisted in-progress client state.
CONSTRAINT: creatorpantry-design-system, new-component, and add-content-feature skills.
RESTRICTION: No prompt/image/social/DAM action in this prompt.
BEHAVIOR: Show state flow, wait for approval, implement component/contract/a11y tests.
```

*What 12.10 decided (2026-10-07). Approved during planning: the pipeline lives **under `workflows`** rather than as
a thirteenth section; in-progress state is **`localStorage`, per workspace** (narrowed to per *member* during review —
see below); the shell declares **all six steps**, the four unbuilt ones as placeholders; and accepting an idea
**never** fills the creator's concept box.*

*Where the requirement text was not available. PIPE-UI-001 through 006 are named only in this file — the functional
source of truth is not in the repository — so the SCOPE's own list is what was built: channel, day, variant count,
scene, style and concept on a `setup` step, and generate/keep/pick on an `idea` step. The six fields map exactly onto
`RequestPhotographyConceptViewModel` plus `GeneratedImageOperation.VariantCount`, which is the reason to believe the
reading is right: every one of them is a field some later route already takes.*

*"Edit the seed" became **keep and try again**, and the constraint that forced it is worth recording: there is **no
HTTP route for the photography-style or occasion catalogue**. A facet picker is therefore not buildable, so a creator
cannot swap a facet for a named other one. What they can do needs no catalogue at all — the seed response carries each
facet's key, so pinning the ones they like and re-rolling the rest is a pure function of what is already on screen.
Channel and day are the two facets that *do* have a picker, and they live on the `setup` step.*

*The rule that keeps those two honest: **a value has one home.** Keeping a generated channel or day writes it back
into `setup` rather than into the keep map, so the configuration step can never disagree with the idea on screen.
`CONTENT_SEED_KEEP_NAMES` therefore holds five facets, not six, and `contentSeedQueryFor` assembles a request from both
places.*

*Nothing is stored while an idea is only a suggestion — except the token. The seed generator persists nothing by
design, so keeping a copy of an unaccepted idea would quietly make it a record; the draft keeps `lastToken` instead and
reproduces the idea on resume, which is exactly what the contract offers. An idea that has been **picked** is stored
whole, because from that point it is a decision.*

*Query parameters are **PascalCase on the wire** (`Token`, `DishType`, `PhotographyStyle`). `ContentSeedQueryViewModel`
binds by property name, so camelCase keys bind to nothing — which would look like a working request that silently
ignored every pin. A contract test asserts each name rather than trusting it.*

*The safety flag is rendered as asymmetrically as its own documentation demands: `requiresSafetyCaution: true` shows a
caution and carries it into the picked idea, and `false` shows **nothing at all**. Two tests pin that, one of them
asserting the words "is safe" never appear.*

*`persisted in-progress client state` ended up needing more care than the phrase suggests, and the isolation audit is
what found it. The first version keyed the draft by **slug**, which is wrong twice. A slug names a workspace but not a
person, so a lapsed session followed by a colleague signing in on the same browser offered them the first creator's
unfinished wording — a real leak inside one workspace. The fix is to key by **`workspaceId` + `membershipId`**, both
from the caller's own membership row, which is also why the draft can no longer be read from the route alone: the shell
waits for memberships, and `applyRoute` refuses to judge a step against a draft it has not read. Purging on `expired`
was the obvious alternative and was rejected — it would have taken away the resuming this prompt asked for.*

*Two smaller things from the same audit. The step on screen is keyed on the owner as well as the slug
(`track step.slug + '@' + ownerKey()`), because resetting the shell's signals is not enough: Angular can flush the
reset and the reload in one pass, leaving a child component holding the previous workspace's seed. And the sign-out
purge only fires if the service exists to notice, so `AppShellComponent` constructs it for every signed-in page — its
own doc comment had been claiming a guarantee it could not keep.*

*No library component was added. Variant count and day are `CpChoiceGroupComponent`, the override lists are repeatable
`cp-field` rows inside `CpFormSectionComponent` on the `brand-settings` links pattern, and the seed's facet rows carry
domain vocabulary, so they stay in the feature. `@design-review` found nothing.*

*Leaving is deliberately **not** guarded. Every change is written through as it is made, so there is no unsaved work to
warn about — the opposite of `brand-settings`, which guards because its edits live in memory. Start over is the one
action that loses anything, and it is the one that asks.*

*2575 showcase specs and 176 library specs pass; `npm run build` is clean. The backend was not touched, so the OpenAPI
snapshot is unchanged.*

### 12.10a Content Pipeline prompt and reference analysis UI - done

```text
SCOPE: Extend only the pipeline with concept suggestion, prompt composition/review, brief upload, and
reference-image analysis, preserving creator edits as final prompt.
CONSTRAINT: PIPE-UI-001/003; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: No image render, social generation, or DAM save.
BEHAVIOR: Plan states, wait for approval, implement component/contract/a11y/cancellation tests.
```

*What 12.10a decided (2026-10-07). Approved during planning: **one `prompt` step with three sections** rather than
splitting the journey; **draft v3, discarding earlier versions**; a brief and a reference filed as
**`VisualReference`/`VisualDirection`**; and a **new shared poll helper used by new code only**.*

*All three capabilities already shipped, so this prompt is the client for them: `POST` → 202 → poll `GET`, with the
answer arriving as a proposal's **flat `Changes` rows**. Reading them back is the bulk of the model code —
`Add`/`PhotographyConcept` carries a label under a server-minted `targetId` with `Set` rows for `mood`, `palette`,
`rationale`, `channelFit` and `shot.{Kind}.{property}`; `Add`/`ImagePrompt` and `Add`/`ReferenceImageAnalysis` each
carry a prompt with `avoid` and, for a reading, `observation.{Aspect}` plus its `.confidence`.*

*Three things the contracts settled rather than taste. IMG-002 requires a concept **and** a shot and refuses a shot
its concept did not plan, so the two are **one choice in one group** — two pickers would let a creator assemble that
refusal by hand. Neither IMG route takes a brand-guide selection (the server uses the active guide), so
`cp-brand-visual-style-control` does **not** apply here despite its own documentation naming these screens. And
**no route cancels a generation**, so the buttons say "Stop checking"; real cancellation exists only on the upload
path, under an `AbortSignal`.*

*The brief and the reference image are **brand source documents**, which is why there is no upload surface of its
own: `briefDocumentId` and `referenceDocumentId` resolve through the brand facade under the workspace filter, where
a file's signature, decoded format, dimensions and size were already inspected. A second upload path would be a
second set of those checks to keep in step. The picker shows a document that cannot serve as a reference
**disabled, with the reason**, rather than filtering it out — narrowing in the browser would hide matches sitting on
pages the screen never fetched, which is the one list bug a reader cannot see.*

*`promptSource` replaced a plain `promptEdited` boolean, and that was the ai-safety review's finding rather than the
plan's. A boolean was answering two questions badly: *may this be replaced without asking?* and *was this written by
a model?* Taking the wording from a reference reading is not an edit but it is a choice, and the text is still
generated — so `none | composed | reference | creator` answers both, and an unrecognised stored value on a non-empty
prompt reads as `creator`, because the worst mistake available here is replacing words a person wrote.*

*The one finding that was a real defect rather than a refinement: **IMG-002's warnings never reached the screen.**
The server emits `Limitation` warnings for a brief it could not read, and both decoders returned `null` when the
`Add` row was missing — so a proposal whose *only* content was the warning explaining why nothing was produced
rendered nothing at all. Warnings are now read off the proposal through `composedPromptWarnings` and
`referenceReadingWarnings`, independently of whether any content decoded, and the interfaces no longer carry a
`warnings` field that could be read through the thing that might be absent.*

*Four smaller ones from the same review, each worth the line it cost. A composed prompt no longer survives a change
of shot — it was written for a different frame — while words the creator typed do follow them. The disclosure
holding the written prompt is titled by **what it holds** rather than as an undo, because after writing again it is
the *newer* text and "what was written before your changes" was simply false. `Proposed` arriving with no proposal
answers `unavailable` rather than stranding the screen on "Check again" for something polling has already stopped
watching. And shot `styling`, `surface` and `props` are now shown in full: styling is exactly where a garnish nobody
wrote down would appear, and a creator cannot judge that before picking unless they can see it.*

*Idempotency keys now **survive a retry**. Minting one per click meant a "Try again" after an `unavailable` — which
can mean the server accepted and the response was lost — bought a second generation and spent the allowance twice.
`IdempotencyKey` keeps one key per logical ask and mints a fresh one only for a deliberately different question.
Writing that surfaced an adjacent bug: the tracker had no case for `idempotency_key_conflict`, so a reused key was
reported as a generic outage, which reads as something that might fix itself.*

*Written once rather than three times: `ai-poll.ts` (interval, degraded backoff, failure count, wall-clock ceiling),
`ai-operation-tracker.ts` (phase, request id, generation guard — its spec is where "an abandoned answer never lands
on the one the creator is looking at" is pinned), `ai-request.ts` (the refusal precedence, where a suspension is a
403 like a role refusal and a spent allowance a 429 like the edge's rate limiter) and `brand-source-messages.ts`
(wording that was already duplicated twice). The three existing pollers in `features/ai/` are untouched. Writing
`ai-request.spec.ts` caught a real bug: the suspended code is `ai.quota.suspended`, and the `ai_suspended` this was
first written with fell through to `unavailable` — a suspended account reported as an outage.*

*Two review findings were **not** acted on, and the reasoning matters. The contract reviewer called the list
separator broken in all three capabilities, reading `PhotographyConceptInputs.ListSeparator = "\n"`; that constant
is how a **request's** override lists are stored in `TaskInputsJson` and no client ever reads it. Every proposal row
a client does read is joined with `"; "` by the three handlers, and splitting on the semicolon alone and trimming is
correct for all of them — and more robust than splitting on `"; "`, which would turn a row joined without the space
into one long item. It also called the absent `recipeId`/`recipeVersionId` breaking; both are optional, the pipeline
has no recipe selection at all, and adding fields with nothing to populate them would be dead code. A recipe-pinned
shoot is a later prompt's work.*

*2716 showcase specs and 176 library specs pass; `npm run build` is clean. The backend was not touched, so the
OpenAPI snapshot is unchanged.*

### 12.10b Content Pipeline image selection UI - done

```text
SCOPE: Extend only the pipeline with image operation progress, one-to-four grid, keyboard lightbox,
keeper selection, download, rejection, and cleanup action.
CONSTRAINT: PIPE-UI-004; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: No social generation or DAM commit. No provider/blob URL exposed.
BEHAVIOR: Plan state/recovery, wait for approval, implement component/contract/a11y tests.
```

*What 12.10b decided (2026-10-08). Approved during planning: pixels reach the screen as a **`blob:` object URL**
from a credentialed fetch; **cleanup declines every non-keeper on an explicit action**, never on Continue;
**Continue requires at least one keeper**; and the **lightbox is a feature component** for now.*

*The CSP is what settled the first of those, and it is worth recording because the obvious implementation is
simply blocked. `ClientOptions.SpaContentSecurityPolicy` allows `img-src 'self' data: blob:` — the gateway
origin is **not** an allowed image source — while `connect-src` does include it. So `<img src="{gateway}/…/preview">`
never leaves the browser, and the only route to the pixels is `HttpClient` with `responseType: 'blob'` and an
object URL. That reading of "no provider/blob URL exposed" is the strict one rather than the convenient one: an
object URL is a handle inside one tab, and no storage or provider address is produced anywhere. The component
that holds it also revokes it, on replacement and on destroy — a four-picture sheet left behind would keep its
bytes alive for the life of the tab, which is the opposite of what `no-store` on the route was asking for.*

*A **keeper is a mark on the draft, not a filing**, because `GeneratedImageStatus.Kept` only ever arrives from
the DAM commit and this prompt's RESTRICTION excludes it. A kept picture is therefore still `Staged` and
retention will collect it on schedule, so the step says that outright — "nothing is filed in your library on this
step", plus the soonest `retentionExpiresAt` in the run — rather than letting a tick imply something was put
away safely. `STAGED_IMAGE_STATUS_LABELS` reads `Kept` as "Filed in your library" for the same reason: a creator
who sees it got there another way, and the word has to tell them so instead of echoing the mark they made here.*

*`aiProposalId` is sent as **null**, and this is a known gap rather than an oversight.
`GeneratedImageInputChecks.Request` does not validate it and `GeneratedImageOperationConfiguration` pins it with
a `Restrict` foreign key onto `(WorkspaceId, Id)` of `AiProposal` — so a proposal that has since been swept would
fail the insert and turn "Make the pictures" into a 500 rather than a refusal. The draft also keeps the IMG-002
*request* id and never a proposal id, by 12.10a's own design. Nothing a creator sees depends on it, since
`promptText` is the same either way; wiring provenance properly needs a server-side existence check or a durable
proposal id in the draft, and both belong to a later prompt.*

*The state kept is **an id and a decision**: `operationId` so the run is re-read on resume, and `keepers` because
a mark is a decision. Nothing about the pictures themselves — bytes, media type, size, dimensions, provider,
model, retention deadline — is stored, and a test asserts the stored JSON contains none of those field names.
What was *declined* is not stored either: each picture's own status is the truth about that, and a client copy
would be a second account free to disagree. On resume, `keepersStillPresent` drops any mark the run no longer
lists or lists as declined, expired or filed — coming back a day later is an ordinary way to reach that, and
carrying the mark would gate Continue on a picture that is gone. Draft version 4, discarded rather than migrated.*

*Three things the contract settled rather than taste. There is **no cancel route**, so the control says "Stop
checking" and the step states that nothing there cancels the generation — the same constraint 12.10a hit.
Unlike the AI routes this one answers a **repeated idempotency key with the first operation** rather than a 422,
so there is no `key_reused` phase; `IdempotencyKey` is still reused across a retry, because an ask that answered
`unavailable` may have reached the server and a fresh key would buy a second set. And **`PartiallySucceeded` is
a success**: some variants landed and those are what the creator came for, so it lands in `ready` with the
shortfall said in a sentence rather than in a failure state.*

*`GeneratedImageTracker` is a sibling of `AiOperationTracker` rather than a reuse of it: that one is typed to
`AiProposalStatus`, reads a `proposal` off it, and models AI allowance refusals this route does not have — the
generation facade has no quota check at all, so `AiAllowanceNoticeComponent` has nothing to say here and is not
imported. What is shared is the poll loop: `pollAiOperation` was split into a generic `pollOperation` taking the
caller's own finished condition, leaving the interval, degraded backoff, failure cap and wall-clock ceiling in
one place. The existing three pollers are untouched.*

*Alt text states **only what the server stated** — which picture of how many, and its pixel dimensions. Nothing
in this application has analysed the bytes, so `media.md` forbids describing them, and a test asserts the alt
text never contains the prompt: reading the prompt back as a description would claim the picture shows what was
asked for, which is precisely the claim a creator is on this screen to judge.*

*One finding from writing the tests rather than from reading the contract: **a tile's frame cannot be a button.**
A picture whose load failed has to offer "Try again", and nesting that inside the frame's button is invalid
markup and unreachable with a keyboard. So "View larger" is its own control naming the variant it opens, and a
spec walks every control asserting none contains another. `CpDialogComponent` supplied the modal shell and the
lightbox adds what it lacks — Escape, a focus trap, `←`/`→`/`Home`/`End`, and focus returned to the opener.
Arrow keys **clamp rather than wrap**: with at most four pictures there is no distance to cover, and being told
"this is the end" by nothing moving beats being returned to the first picture unannounced.*

*Tidying up is **sequential, and reports per reason**: four deletes at once is the shape the edge's rate limiter
exists to refuse, and "2 pictures were declined. One had already gone." is what a creator can act on, where one
sentence per picture would say the same thing three times. Each call is idempotent — `204` whether this call
declined the picture or an earlier one did — so a half-finished tidy-up is simply run again. A decline takes the
mark off whatever the outcome: a picture the server would not let us decline is still one the creator said they
do not want.*

*Two small spill-overs into the prompt step, both in the pipeline and both the same fix. The prompt box now
carries `maxlength`, because the image route is the first thing to consume it and refuses over 4000 characters;
and a resumed draft already holding a longer prompt disables Generate with the length and where to shorten it,
rather than earning a refusal. `CONTENT_PIPELINE_LIMITS.promptMaxLength` reads from
`GENERATED_IMAGE_PROMPT_MAX_LENGTH`, so the server's number has one home.*

*What review changed. The isolation audit found a defect I had introduced, and it was in the one place the
tracker's whole discipline lives: `destroy()` unsubscribed but did **not** bump the generation counter, so a
`submit` still awaiting its answer when the step was destroyed passed the staleness check on the way back,
opened a poll loop with nothing left to tear it down, and then called `emitImages` and `announced.emit` on a
destroyed component — a write of the old workspace's draft that was being stopped only by framework behaviour
nobody had asserted. Being destroyed **is** abandonment, so `destroy()` now goes through `begin()` like every
other abandonment, and a test resolves a submit after teardown and asserts no poll was started.*

*The same audit was right that the step relied on its caller for isolation. The tracker's poll closure reads
the slug input on **every** request, so the step was correct only because the shell keys it on
`step.slug + '@' + ownerKey()` and therefore rebuilds rather than re-points it. `ngOnInit`'s one-shot resume is
now an effect keyed on `slug|operationId` — the `loadedKey` pattern the shell and the staged-image component
both already use — so arriving with a kept run, a change of workspace and a change of run are one path, and a
step that was reused instead of rebuilt could not ask a new workspace for the old one's run. `generate()`
claims the key before the draft carries it, so the run it just started polling is not resumed a second time on
top of itself. Six two-workspace tests now cover what `tenancy.md` asks for and the step had no coverage of:
A's run is never asked of B, a changed slug re-points even when the run id is identical, A's pictures leave
when B has no run, and A's late answer never lands on what B is looking at.*

*One smaller finding worth the line. Keeper pruning only runs when a reading arrives, and for a run that is
**gone** none ever will — so its marks survived, and `canContinue` would have let the creator leave the step
with nothing on screen. The marks are now cleared on `gone`; the run id stays, because it is still the truthful
answer to which run that was. `@design-review` and `@api-contract-checker` both reported no findings.*

*2887 showcase specs and 176 library specs pass; `npm run build` is clean. No library export changed, so
`public-api.ts`, the showcase, `README.md` and `DESIGN-SYSTEM.md` are untouched. The backend was not touched, so
the OpenAPI snapshot is unchanged.*

### 12.10c Image Studio screen

```text
SCOPE: Build IMAGE-UI-001 through 004 using existing concept/prompt/reference/generation/staging contracts.
CONSTRAINT: creatorpantry-design-system, new-component, and add-media-feature skills.
RESTRICTION: No social workflow or board creation. DAM save uses existing DAM-001 only when added as a
separate action prompt.
BEHAVIOR: Show component/state map, wait for approval, implement with UI/a11y/error/cancellation tests.
```

### 12.10c-1 Image deployment wiring

> Inserted 2026-10-08. 12.7 recorded that no image deployment exists and called wiring one "its own change";
> no prompt was ever added for it, so every generation settles as `provider-not-configured` and nothing built in
> 12.10b or 12.10c can be shown a real picture. This is that change. Run it before 12.10d.

```text
SCOPE: Amend B-15 to name a third deployment, `images`, and wire a real Microsoft Foundry image deployment
behind `Microsoft.Extensions.AI.IImageGenerator` so a queued generated-image operation produces staged
pictures. Covers the baseline record, the AppHost resource/parameter in all three Foundry modes
(Foundry:Enabled, Foundry:Azure prompted, supplied connection string), the `images` branch in
`AiProviderRegistration.AddCreatorPantryAi`, provider/model provenance reported by the gateway, and the
first-run notes in .claude/rules/aspire.md and CLAUDE.md.
CONSTRAINT: B-15, INT-010 through 013, IMG-003; add-aspire-resource and add-external-integration skills.
`CreatorPantry.AiProvider` stays the only assembly naming a provider SDK. Verify current package names, the
`IImageGenerator` registration API, and which image models the target Foundry/Azure OpenAI resource serves
against first-party documentation before choosing anything.
RESTRICTION: Do not change `GeneratedImageProviderGateway`'s contract, the worker job, the staging model, or
any HTTP shape — 12.7 built those so they would not move. No key in source control, logs, or responses; the
deployment name and endpoint are parameters like `chat` and `embeddings`. `images` is decided independently:
a missing image deployment must not take chat or embeddings down, and a host with `Foundry:Azure=false` still
starts and still settles `provider-not-configured`. Keep `MEAI001` suppressed narrowly, not project-wide. No
fake or placeholder generator outside tests.
BEHAVIOR: Show the B-15 amendment, the parameter/connection design for each of the three modes, the
missing-deployment behaviour in and out of Development, and the failure-category mapping for refusal, rate
limit, and bad credential; wait for approval. Implement with registration tests for configured, unconfigured,
and images-only-missing hosts. Then prove it live: `aspire run` with a real deployment, walk the Content
Pipeline to "Make the images", and report that one to four pictures staged, rendered in the grid and lightbox,
and recorded provider, model, and cost-relevant usage. Say plainly if the live run was not performed.
```

*What 12.10c-1 decided (2026-10-08). **Not marked done: the live run has not been performed**, because it needs
an image deployment on the Azure OpenAI resource and its name in `Parameters:foundry-images-deployment`. The
code, the registration tests and the B-15 amendment are in.*

*`Azure.AI.Inference` has no image route, so `images` is a **second provider SDK** in `CreatorPantry.AiProvider`:
`Aspire.Azure.AI.OpenAI` → `GetImageClient` → `AsIImageGenerator()`. Approved during planning: the image
deployment lives on the **same resource** as chat and embeddings, so the prompted mode reuses `foundry-endpoint`
and `foundry-key` and asks for one new value. `Foundry:Images` defaults on and is opted out of in user secrets,
for the reason `Foundry:Azure` does. A missing image deployment never stops a host, in any environment —
unlike chat and embeddings.*

*Foundry Local has no image-generation models and a Foundry `/models` endpoint has no image route, so "all
three modes" came out as one mode that prompts and two that pass through a supplied `ConnectionStrings:images`.*

*The gateway was not touched. The OpenAI SDK throws `ClientResultException`, which the gateway would have
retried as an outage three times at cost, so `AzureOpenAIImageGenerator` rethrows it as `HttpRequestException`
with the status and none of the provider's text. A content-filter block and a rejected key are still one
category, `provider-refused`; the provider's error code is logged so the two can be told apart in the dashboard.*

*Two things only the live run can settle. The gateway asks for bytes, which the SDK sends as `response_format`;
the gpt-image-1 series rejects that parameter and the documentation does not say whether `gpt-image-2` does. And
the staged-image row has no usage columns, so token usage is logged as counts when the deployment reports any —
the Azure response examples show none.*

*Superseded the same day for the provider only: the `gpt-image` deployment's throughput could not serve the
step, so `images` is now Venice.ai over plain HTTP (`VeniceImageGenerator`, B-15's second amendment). The
prompted value is `Parameters:venice-api-key`; `Foundry:Images`, `foundry-images-deployment`, the second SDK and
`AzureOpenAIImageGenerator` are gone. **The live run is still not performed** — it now needs that key.*

### 12.10c-2 Workflows entry point for the Content Pipeline

> Inserted 2026-10-08. The pipeline is routed at `workflows/content-pipeline` but nothing links to it — the
> Workflows section is still `PlaceholderSectionComponent` — so 12.10 through 12.10b are reachable only by typing
> the URL. 13A.13 builds the real Workflow Hub some fifty prompts from here; this is the stopgap until then.

```text
SCOPE: Replace the placeholder at the `workflows` index route with a minimal Workflows page that lists the
guided journeys that exist today — the Content Pipeline only — as one plain-language card that starts it, or
says "Continue" with the step reached when a draft is already kept for this workspace and account.
CONSTRAINT: FLOW-004 and the easy-as-pie product rule; creatorpantry-design-system and new-component skills.
Compose existing `@creator-pantry/ui` exports; read resume state through `ContentPipelineDraftService`.
RESTRICTION: No workflow catalogue, run entity, endpoint, or shared guided-workflow shell — those are 13A.1
through 13A.12 and this page is replaced by 13A.13, so keep it small enough to delete. No card for a journey
that is not built, no decorative metrics, no new library component. The resume state shown must be the
current workspace's and account's own, never another's.
BEHAVIOR: Show the component/state map (first use, draft in progress, unreadable draft), wait for approval,
implement with component, route, a11y, and two-workspace tests. Update `app.routes.spec.ts`. Confirm in the
running app that Workflows → Content Pipeline reaches the first step by clicking, with no typed URL.
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

*What 12.10d decided (2026-10-08). Web only: `prompt-library` is a route tree (list, and `:promptRecordId`
detail) over the PRM routes 12.3 built, with no endpoint, migration or snapshot change. PROMPT-UI-001 through
003 are defined nowhere but this prompt, so they were read as list, preview and detail.*

*Approved during planning. **There is no sort control**: the route has one ordering and no `sort` parameter, and
sorting loaded pages is what the restriction forbids, so the page states "newest first". **Search and one
channel are the only filters**, because they are the only ones the route has. Pages are appended by following
`nextCursor`. Copy is offered in the preview and on the detail page, never on a row, because a row holds a
truncated preview. **Reuse goes to Image Studio**: it writes a new studio draft holding a copy of the text and
leaves the record untouched, asking first when that would replace unfinished studio work; a Viewer is not
offered it.*

*Left open. **Nothing in the web app saves a prompt yet** — no screen calls `POST …/prompts` — so the library
shows its first-use state until a save action is built. That is its own prompt.*

### 12.10e DAM Library list UI

```text
SCOPE: Build only DAM search/filter/paging and thumbnail-card results with channel, label, metadata, usage,
version count, loading, empty, error, and retry states.
CONSTRAINT: DAM-UI-001/002; creatorpantry-design-system and new-component skills.
RESTRICTION: No detail or mutation. Use authorized thumbnail/render endpoint, never raw blob URL.
BEHAVIOR: Show query/state design, wait for approval, implement component/contract/a11y tests.
```

*What 12.10e decided (2026-10-08). `dam` is the library list over DAM-002, with search, channel, day and the
route's two orderings sent to the server, pages appended by following `nextCursor`, and cards that are not
links. DAM-UI-001/002 are defined nowhere but this prompt. "Label" was read as the asset's title, and version
count is shown from `currentVersionNumber`, which is the count because numbers start at one and none is removed.*

*Approved during planning. **One additive backend change**: `utilizationCount` on
`MediaAssetSummaryServiceModel`, counted in the search statement as the detail read counts it, because usage
was on the detail route only and a detail read per card was the alternative. The OpenAPI snapshot gained that
one field. **There is no thumbnail derivative**: the card fetches the full current version from the authorized
`/content` route as a blob — the SPA's content security policy refuses a gateway `<img>` — and only when the
card nears the screen. A real thumbnail is a backend job and its own prompt.*

*Left out. Platform, style and tag filters have no route listing their choices; cuisine, course, date and
recipe filters have no control yet.*

### 12.10f DAM asset detail UI

```text
SCOPE: Build only DAM detail with metadata, prompt/recipe lineage, utilization history, version history,
and authorized preview/download actions.
CONSTRAINT: DAM-UI-003; creatorpantry-design-system and new-component skills.
RESTRICTION: No edit/upload/utilization/delete action in this prompt.
BEHAVIOR: Plan sections/states, wait for approval, implement component/contract/a11y tests.
```

*What 12.10f decided (2026-10-08). Web only: `dam` became a route tree with `:assetId` beside the library, over
DAM-003 and the render and download routes. DAM-UI-003 is defined nowhere but this prompt. The page is the
picture, what the creator said about it, recipe and prompt lineage, every version with its own download, and
the usage history — which is its own paged read with its own states, so it failing leaves the page standing.*

*Approved during planning. **The library's card titles became links**, since nothing else reached this page.
**Cuisine and course are not shown**: the route sends ids and the web app has no client for those lists.
Platform and style are shown as their stored keys. There is no full-screen viewer. A removed asset answers "not
found" — `includeDeleted` is never sent, and viewing a tombstone belongs to 12.10h. **A use's date is shown as
the calendar date it is**, and its weekday as the server sent it, never recomputed in the browser's zone.*

*The page decodes no concurrency token: nothing here writes. 12.10g will need it and should read it then.*

### 12.10g DAM metadata and version actions UI

```text
SCOPE: Add only metadata edit and new-version upload actions to DAM detail with validation, progress,
concurrency conflict, cancel, and success states.
CONSTRAINT: DAM-UI-004; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: Upload never overwrites current bytes. No utilization/delete/add-to-board action.
BEHAVIOR: Plan dialogs/state, wait for approval, implement component/contract/a11y tests.
```

*What 12.10g decided (2026-10-08). Two dialogs on the asset page, for Contributors and above: **Edit details**
over DAM-004 and **Add a version** over DAM-010. DAM-UI-004 is defined nowhere but this prompt. The edit sends a
merge patch of only the fields that changed, with an emptied field sent as a clear, so an untouched field can
never be blanked. The upload sends the file alone, shows the real transfer progress, and says in its own words
that every earlier version stays.*

*Approved during planning. **A conflict keeps the creator's edits**: on `409 media.asset.stale.conflict` the
dialog stays open and offers to load the latest, carrying their changes onto it and taking the newer value for
every field they did not touch. Leaving with an unsaved edit or a running upload asks first. A cancelled upload
makes the page re-read the asset, because an aborted request may still have landed.*

*Added at approval: **tags can be added as well as removed**, which needed a route this codebase did not have.
`GET /api/v1/workspaces/{workspaceSlug}/tags` lists the workspace's active tags by name through the recipes
module, which owns `WorkspaceTag` — any member may read, not paged, capped at 500. **A tag still cannot be
created from the DAM**: DAM-004 refuses an id it does not know, so the dialog chooses from the vocabulary and
says that a tag is made by tagging a recipe. Cuisine and course remain uneditable here, and are never sent.*

### 12.10h DAM utilization and soft-delete actions UI

```text
SCOPE: Add only utilization logging and soft-delete actions with clear retention/link impact and confirmation.
CONSTRAINT: DAM-UI-004/005; creatorpantry-design-system and add-media-feature skills.
RESTRICTION: Do not imply physical immediate deletion or permit utilization of inactive assets.
BEHAVIOR: Show copy/state, wait for approval, implement component/contract/a11y tests.
```

*What 12.10h decided (2026-10-08). **Log a use** is a dialog over DAM-009 for Contributors and above: platform
and a calendar date, with campaign and notes optional. The date defaults to today in the browser's own
calendar, is sent as a plain date, and no weekday is sent. One idempotency key per entry, because without one
two identical calls record two uses. **Remove from library** is over DAM-005 for Editors and Owners, through
`ConfirmService`; a Contributor is not shown it. DAM-UI-005 is defined nowhere but this prompt.*

*The copy never says "delete". The confirmation says the picture leaves the library for everyone, that its
files, versions and history are kept, what still links to it, and — approved as written — that the app cannot
bring it back yet. After a removal the page becomes an account of what happened and what still points at the
asset, with no action on it, since its own routes answer not-found from then on. A use logged against an asset
removed meanwhile is refused in those words and the page is re-read when the dialog closes.*

*Approved during planning: **one additive backend change**, `brandProfileCount` and `testAttachmentCount` on
`MediaAssetDetailServiceModel`, counted as DAM-005's own report counts them, so the warning before a removal
names every kind of link rather than recipes only. A test holds the two reads to the same numbers.*

### 12.10i Recipe asset-link command

```text
SCOPE: Implement only RCPUB-005 link/unlink commands for authorized DAM asset roles hero, step, social,
or test image, preserving utilization history and optional version pinning.
CONSTRAINT: RCPUB-005; add-recipe-feature, add-media-feature, and add-endpoint skills.
RESTRICTION: Prevent cross-workspace links. Unlink does not delete asset or history.
BEHAVIOR: Plan contract/invariants, wait for approval, implement link/unlink/replay/isolation tests.
```

*What 12.10i decided (2026-10-08). **Linking is an edit of the recipe.** `POST` and `DELETE
.../recipes/{recipeId}/asset-links[/{linkId}]` take the recipe's `expectedConcurrencyToken`, write one new
recipe version, reopen an approved recipe, and answer `200` with the whole recipe — not `201` with a link —
because a link is part of what a version records and the caller needs the new token. Contributor and above.
An `Idempotency-Key` replays the first answer; without one a repeat is refused, as stale or as a duplicate.*

*Roles are `Hero`, `Gallery`, `Process`, `Social` and `Step`; `Gallery` and `Process` were kept. A `Step`
picture names a step of the same recipe, and **removing that step demotes the link to `Process` rather than
dropping it** — the step key is `Restrict`, so the demotion is required, not a courtesy. A second `Hero` and
the same asset in the same role twice are each refused with `409` and nothing replaced. `versionNumber` pins a
link to one asset version and **the pin stays** when the asset gains another; restore and duplicate carry the
pin, and a duplicate's step pictures follow the copy's own steps.*

*A **test image is not a recipe link**. It attaches to one test run at
`.../test-runs/{testRunId}/attachments` (`GET`, `POST`, `DELETE`), optionally against one issue of that run,
writes no recipe version and needs no token. The `GET` was added because nothing else lists them.*

*Cross-workspace: an unknown asset, a neighbour's and one removed from the library answer the same `422` in the
same words, naming `mediaAssetId` and never the version. The asset is resolved through
`IMediaAssetLookupFacade`, which avoids a dependency cycle with the main media facade. Unlink and detach remove
one row; the asset, its versions and its usage history are not reachable from either command, and tests hold
the counts still. Migration `RecipeAssetLinkRolesAndPins` adds the pin and step columns and keys.*

### 12.10j Recipe asset-link UI

```text
SCOPE: Add only DAM browser/select/link/unlink UI within recipe media section with role, version pinning,
preview, and authorization-aware actions.
CONSTRAINT: RCPUB-005; creatorpantry-design-system and new-component skills.
RESTRICTION: No upload/edit/delete asset action inside recipe editor.
BEHAVIOR: Show interaction, wait for approval, implement component/contract/a11y tests.
```

*What 12.10j decided (2026-10-08). The recipe editor's **Media** tab lists the saved recipe's pictures under
their roles and offers two actions: **Add from library** and **Unlink**. Adding is one `CpDialog` in two
steps — browse the library and choose, then say what the picture is for (role, the step for a step picture,
follow the current version or keep one, caption). Unlink asks through `ConfirmService` and says the picture
stays in the library. The words "delete" and "remove" are not used, and nothing here uploads, edits or
removes an asset.*

*Both actions are edits that write a version, so **neither is offered while the form has unsaved edits**. The
editor owns the recipe and its token; the panel is handed both and hands back the recipe each command
returns. Viewers and archived recipes get the list with no actions. A link whose asset has since left the
library keeps its row and can still be unlinked.*

*Approved during planning: test images are **not** in this panel (they belong to a test run); changing a link
is unlink then link, with no edit in place; no reordering; `Gallery` and `In progress` are offered beside the
three roles the prompt names. **A kept older version is previewed by reading that version's download route as
bytes** — there is no render route for a numbered version, and none was added. No backend change.*

### 12.10k Brand logo link command and UI

```text
SCOPE: Enable BrandProfile logo links. Validate each asset id through the DAM facade in the resolved
workspace and lift the 11.1b `brand.assets.unprocessable` refusal in both the facade and Business. Add the
composite foreign key (WorkspaceId, MediaAssetId) -> DamAssets (WorkspaceId, Id) for BrandAssetLink and for
the two earlier unconstrained pointers, RecipeAssetLink and TestAttachmentLink. Enable logo selection in the
brand settings UI using the DAM browser from 12.10j.
CONSTRAINT: DAM-001, RCPUB-005, DEC-010; .claude/rules/media.md; add-media-feature, add-workspace-entity,
add-endpoint, and new-component skills.
RESTRICTION: Composite keys only, never a single-column key. A foreign, unknown, or soft-deleted asset is
one indistinguishable not-found. Unlink never deletes an asset or its history. One primary logo. No
visual-direction fields.
BEHAVIOR: Show the FK/migration plan including how rows that already exist without a matching asset are
found and handled, wait for approval, implement migration, guard replacement, and link/unlink/replay and
cross-workspace-asset isolation tests.
```

*What 12.10k decided (2026-10-08). **No migration was added, because the keys already exist.** 12.9's
`MediaAssetAggregate` migration gave `BrandAssetLinks`, `RecipeAssetLinks` and `TestAttachmentLinks` the
composite key `(WorkspaceId, MediaAssetId) → MediaAssets (WorkspaceId, Id)`, `Restrict`, and before adding
each it deleted any link whose asset was not in the same workspace. Brand links could not have been written
through the API since 11.1b refused them. What this prompt adds is the proof on SQL Server — one test per
table — that a link naming another workspace's asset is refused by the key itself.*

*The blanket `brand.assets.unprocessable` refusal is gone from the facade and from Business. The facade
resolves each submitted id through `IMediaAssetLookupFacade` in the resolved workspace and hands Business the
set that can be linked; Business refuses anything else, naming `assets[i].MediaAssetId`. Unknown, another
workspace's and removed-from-the-library are one `422` in the same words — the key cannot see `DeletedAt`, so
this check is what covers a removed asset. **A logo the profile already links is kept when re-submitted even
if its asset has since been removed**, so an unrelated edit is never blocked by it; once unlinked it cannot be
linked again. No new route and no change of shape: `assets` on `POST` and `PATCH` simply works, `PATCH`
replaces the list whole, and `[]` unlinks everything. Unlinking removes link rows and nothing else.*

*In brand settings the disabled "Choose a logo" became a working **Logo** section: one primary logo and any
alternates, chosen from the library. **Logos are part of the form** — held in the draft, written by the one
Save, and no question is asked on unlink because nothing has happened until Save. Choosing a new primary
replaces the old one rather than demoting it. No version pin: a logo follows its asset's current version. The
library browser from 12.10j was extracted into `DamAssetPickerComponent`, which the recipe dialog and brand
settings now share; its search is a `role="search"` region rather than a form, because a form nested in the
settings form would hand its Enter to Save.*

### 12.10l Creative-production audit

```text
SCOPE: Audit content pipeline, image studio, prompt library, staging, DAM, and recipe asset links.
CONSTRAINT: SEED, IMG, PRM, DAM, RCPUB-005 and UI requirements.
RESTRICTION: Report first; no unrelated redesign.
UTILIZATION: design-review, api-contract-checker, workspace-isolation-auditor, integration-reviewer,
ai-safety-reviewer, and test-gap-analyzer.
BEHAVIOR: Return findings, wait for approval, fix blockers, rerun focused and end-to-end pipeline tests.
```

*What 12.10l found and fixed (2026-10-08). Six reviewers ran; no finding leaked data across workspaces. The
instruction after the report was "fix all".*

*Image generation: `aiProposalId` on a request is now checked through the Ai module's lookup facade, and one
that is unknown or another workspace's answers `422 media.generation.proposal.unprocessable` — it used to reach
the foreign key and throw. **A key names one request**: the same `Idempotency-Key` with a different prompt,
avoid list, count or proposal, or from a different member, answers `422 idempotency.key_reused` instead of
returning the first request's operation. The web keys an attempt by what was asked, so a reworded prompt takes
a new key. A declined or expired staged image is no longer served by `/preview` or `/content`, from the moment
it is declined rather than from whenever the sweep collects its bytes.*

*Elsewhere: brand settings field errors now reach their fields — the server sends `links[0].Url`, the form
matched `links[0].url`, and the service normalises keys at the boundary. The outbox claim is guarded per row
(`Status` and `LeasedBy` are concurrency tokens; no column change), so a second Worker instance cannot
dispatch a message the first already claimed, and a handler that outlives its lease cannot record an outcome
over whoever took it. The recipe Media tab uses the shared `DamLinkedAssetComponent` instead of a row of its
own. Prompt template provenance is taken only from the proposal; the edge already refused it otherwise, and
Business now does too.*

*Tests added: `CreativePipelineEndToEndTests` — request, worker pass, keep into the DAM with its prompt, link
to a recipe, unlink, and the other workspace reaching no step of it; before this no test ran two of those
steps in a row over HTTP, and keeping a generated image was not exercised through its route at all. Also specs
of their own for the asset picker, the linked-asset row and the brand logos, and unit tests for the link
validators and the step-demotion rule.*

***Not done, and why.** Image generation still has no allowance check: the AI allowance is metered in credits
per text task, and charging pictures against it needs decisions this audit could not make — what a picture
costs, whether a failed one is refunded, where a creator sees it. It needs a prompt of its own. Also left:
evaluation fixtures for workspace isolation on `content.editorial-package`, `content.seo-package` and the
`recipe.*` capabilities; `inputs: []` on eleven prompt manifests; and kept AI images having no alt text until a
creator writes one.*

## Phase 13 — Social packages, content board, and publishing

### 13.1 Social package model - skip

*Superseded by AF.6.1 (`creatorpantry-ai-fluency-scrub-prompts.md`): creator-selected channels replace the seven fixed outputs. Kept for the record; do not run.*

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

### 13.2 Seven-output social generation - skip

*Superseded by AF.6.3–AF.6.4 (`creatorpantry-ai-fluency-scrub-prompts.md`). Kept for the record; do not run.*

```text
SCOPE: Implement SOC-001 through the shared AI lifecycle for Instagram, TikTok, Pinterest, Facebook, X,
Threads, and Blog Intro using selected prompt/recipe/content versions, channel, optional theme, and voice.
CONSTRAINT: SOC-001; add-ai-capability and add-content-feature skills.
RESTRICTION: Platform limits are deterministic validators. Do not invent recipe facts, search metrics,
image content, or hashtags outside configured policy. Source version is pinned.
BEHAVIOR: Show output schema and platform rules, wait for approval, implement task/endpoint with evaluation
fixtures for limits, claims, missing source, injection, staleness, and workspace isolation.
```

### 13.3 Social Studio UI - skip

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

*Superseded by AF.6.5 (`creatorpantry-ai-fluency-scrub-prompts.md`). Kept for the record; do not run.*

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

*Narrowed by AF.4.3 (`creatorpantry-ai-fluency-scrub-prompts.md`), which delivers the DAM save and prompt lineage. The board card remains here.*

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
Store UTC plus the originating workspace scheduling zone, read from `BrandProfile.TimeZoneId` (11.1b,
publishing.md); when it is unset, refuse with a prompt to set it rather than guessing a zone.
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
copy/media preview, schedule time in the workspace zone, explicit confirmation, status, and cancellation guidance.
CONSTRAINT: KAN-001 through KAN-006 and PUB-001 through PUB-003; easy-as-pie rule.
RESTRICTION: No publish without explicit confirmation or provider outcome assumption. Show UTC only as
secondary diagnostic detail, not the primary scheduling language. The zone is the workspace scheduling zone
(`BrandProfile.TimeZoneId`); when unset, link to brand settings instead of guessing.
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
weekly routines with next occurrence, workspace scheduling zone (`BrandProfile.TimeZoneId`, stored with the
template so later profile edits do not move existing occurrences), default fields, active state, and
idempotent Worker.
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
| USAGE-001 through USAGE-010 | Phase 9A |
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
- every provider attempt posts exactly one account usage entry and spends the requesting account's allowance, not the workspace's;
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
- AI cost is an account fact and a workspace dimension: one person has one balance across every workspace they belong to, and the ledger that proves it holds counts, never content.
- A two-workspace test is mandatory evidence, not optional ceremony.
- The design system is a dependency of product UI, not a screenshot to approximate.
- If a prompt starts adding a schema, backend stack, provider, UI, and end-to-end suite at once, split it.
- Use `/rewind` or revert a bad atomic step instead of stacking corrective mega-prompts on top of it.
