# Implemented Architecture — Phases 0, 1, and 2

This document describes what is actually built in the repository today, as a companion to the plan in `docs/prompts/creatorpantry-scrub-microprompts.md`. It covers Phase 0 (repository and runtime foundation), Phase 1 (identity, gateway, and browser boundary), and Phase 2 (workspace tenancy core) — every prompt in these phases is marked `- done`. Architectural rules remain governed by `CLAUDE.md` and `.claude/rules/`; this document is a snapshot of how those rules are currently realized in code, not a replacement for them.

## TL;DR

| Phase | What exists | Proves |
| --- | --- | --- |
| **0 — Runtime foundation** | Aspire AppHost owns SQL Server 2025, Redis, and blob storage; a one-shot MigrationService applies EF migrations before anything else starts; ServiceDefaults gives every process the same telemetry/health/resilience baseline; Angular 22 workspace scaffolded, served through the Web host in production. | `aspire run` brings up a fully healthy local stack from an empty data volume. |
| **1 — Identity, gateway, browser boundary** | ASP.NET Core Identity owns accounts; the Angular SPA never sees a token — the Gateway holds an `HttpOnly` session cookie and mints short-lived, gateway-signed internal tokens for every call it proxies to the API. There is **no** OAuth/OIDC authorization server in this codebase. | A browser can register, sign in, call the API through the Gateway, refresh, and sign out, and no bearer token is ever observable client-side. |
| **2 — Workspace tenancy core** | `Workspace` / `WorkspaceMembership` model ordered roles; `IWorkspaceContext` is resolved once per request from the route + session and fails closed if read early; a global EF query filter (applied by convention, not per-entity) and a `SaveChanges` interceptor make cross-workspace reads/writes structurally hard to produce. | Two workspaces with similarly named records cannot read, mutate, or enumerate each other's data — enforced by a reusable two-workspace test fixture. |

## Glossary

A few terms recur throughout this document and the codebase:

- **BFF (Backend-for-Frontend)** — the Gateway project. The browser only ever talks to the BFF; the BFF is the only thing that talks to the API.
- **YARP** — the .NET reverse-proxy library the Gateway uses to forward `/api/**` requests to `CreatorPantry.ApiService`.
- **Internal token** — a short-lived (≤2 minute), ES256-signed JWT the Gateway mints for itself on every proxied request. It is *not* an OAuth access token; it exists purely so the API can trust "this request really came through the Gateway, on behalf of this session."
- **`token_use` claim** — distinguishes the two kinds of internal token: `user` (a real signed-in creator, used by product routes) and `service` (the Gateway acting as itself, used only by `/api/v1/internal/**` routes).
- **`IWorkspaceContext`** — a request-scoped object that answers "which workspace, and with what role, is this request allowed to act as." It is resolved once, from the route slug and the caller's membership — never from anything the client sends directly.
- **Idempotency key** — a client-supplied header that lets a retried mutating request (e.g. after a dropped connection) return the original result instead of performing the action twice.
- **Outbox** — a durable table of "things that still need to happen after this database transaction commits" (e.g. background jobs), written in the same transaction as the domain change so the two can never drift apart.

---

## Contents

- [Phase 0 — Repository and runtime foundation](#phase-0--repository-and-runtime-foundation)
- [Phase 1 — Identity, gateway, and browser boundary](#phase-1--identity-gateway-and-browser-boundary)
- [Phase 2 — Workspace tenancy core](#phase-2--workspace-tenancy-core)
- [Migrations applied so far](#migrations-applied-so-far)
- [Request seam in practice](#request-seam-in-practice)
- [Verifying it yourself](#verifying-it-yourself)

---

## Phase 0 — Repository and runtime foundation

### Aspire AppHost

`src/CreatorPantry.AppHost/AppHost.cs` is the single source of truth for the local application model. It declares:

- **SQL Server 2025** (`AddSqlServer("sql", ...)`) pinned to image tag `2025-CU9-ubuntu-22.04` (required for the native `VECTOR` type a later phase uses), persistent lifetime, data volume `creatorpantry-sql-data`, database resource `creatorpantrydb`.
- **Redis** (`AddRedis("cache", ...)`), persistent, volume `creatorpantry-redis-data`.
- **Blob storage** via `AddAzureStorage("storage").RunAsEmulator(...)` (Azurite) with a `blobs` container.
- Secret parameters: `sql-password`, `redis-password`, `internal-token-signing-key`, `internal-token-public-key`, `idempotency-fingerprint-key`.

Application resources and their dependency wiring:

| Resource | Waits for | Notes |
| --- | --- | --- |
| `CreatorPantry_MigrationService` (`migrations`) | `sql` | One-shot host, `WaitFor(db)` |
| `CreatorPantry_ApiService` (`api`) | `db`, `cache`, `blobs`, `WaitForCompletion(migrations)` | Health check `/health`; receives `InternalToken__PublicKeyPem` |
| `CreatorPantry_Worker` | `db`, `blobs`, `migrations` | |
| `CreatorPantry_Web` (`web`) | — | External endpoint, health check |
| `CreatorPantry_Gateway` (`gateway`) | `api`, `web` | External endpoint; receives `InternalToken__SigningKeyPem` and `Cors__AllowedOrigins__0` = web's HTTPS endpoint |

In `IsRunMode` only, the Angular dev server is added via `AddJavaScriptApp("web-dev", "../web", "start").WithNpm()` and wired in as `Cors__AllowedOrigins__1` on the gateway. `web.WithEnvironment("Client__GatewayUrl", ...)` feeds `CreatorPantry.Web`'s runtime-config endpoint (see below), so the browser learns the Gateway's URL at runtime instead of it being baked into the Angular build.

### ServiceDefaults

`src/CreatorPantry.ServiceDefaults/Extensions.cs` wires OpenTelemetry (ASP.NET Core, HttpClient, and runtime instrumentation, OTLP export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set), service discovery, and `AddStandardResilienceHandler()` on every `HttpClient`. `AddDefaultHealthChecks()` registers a `"self"` check tagged `"live"`; `MapDefaultEndpoints()` maps `/health` and `/alive`, anonymous, Development-only — liveness deliberately never calls SQL, Redis, blob, or AI dependencies, so a slow dependency can't be mistaken for a dead process.

`EdgeHardening.cs` holds logic shared by the Gateway and Web hosts:

- `UseRequestBodyLimit()` — 4 MB default, configurable, per-endpoint `[RequestSizeLimit]` override, 413 ProblemDetails on violation.
- `UseSecurityHeaders(csp)` — CSP, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, Referrer-Policy, Cross-Origin-Opener-Policy, Permissions-Policy, HSTS outside Development, no `Server` header.

`InternalTokenDefaults.cs` defines the shared contract for the gateway-to-API trust token described in Phase 1: issuer `creatorpantry-gateway`, audience `creatorpantry-api`, algorithm `ES256`, claims `sid`/`role`/`token_use`, default lifetime 60s, max lifetime 2 minutes, 15s clock skew. Putting this in `ServiceDefaults` (rather than in the Gateway or API individually) is what guarantees both sides agree on the contract without importing from each other.

### Application clock and time-zone seam

`src/CreatorPantry.Domain/Time/`:

- `IClock.UtcNow` (implemented by `SystemClock`, wrapping `TimeProvider`) is the only source of "now" used by domain code, jobs, and audit timestamps — there is no scattered `DateTime.UtcNow` in feature code, which is what makes fixed-clock tests possible later.
- `ITimeZoneConverter` (NodaTime-backed `NodaTimeZoneConverter`, kept `internal` so only BCL types cross the boundary) provides `IsValidZone`, `ToZoned`, `ToUtc` (returns a `LocalToUtcResult` carrying a `LocalTimeResolution` of `Exact`, `ShiftedForward`, or `AmbiguousEarlier` for DST edge cases), `LocalDate`, and `LocalDayOfWeek`.

Registered via `AddApplicationTime()`.

### MigrationService

`src/CreatorPantry.MigrationService/MigrationDatabaseExtensions.AddMigrationDatabase()` registers `CreatorPantryDbContext` **unpooled** — pooling would break the scoped `IWorkspaceContext` the context depends on — enriches it for Aspire, and calls `AddCreatorPantrySchema()`, which registers `AddDbContextMigration<CreatorPantryDbContext>()` (through `IMigrationTarget` / `DbContextMigrationTarget`) and `AddPlatformRoleSeeding()`.

`MigrationWorker : BackgroundService` runs every `IMigrationTarget.MigrateAsync`, then every `IDataSeeder.SeedAsync`, records the outcome in `MigrationRunState`, and calls `lifetime.StopApplication()` — a genuine one-shot host that exits cleanly, matching the AppHost's `WaitForCompletion(migrations)`. No dependent service applies migrations itself.

### CreatorPantry.Web

`src/CreatorPantry.Web/Program.cs` serves the Angular production bundle as static files and falls back to `index.html` (`no-cache`) for client-side routing. It exposes `GET /runtime-config.json` (`no-store`) returning `RuntimeConfig(GatewayUrl)` — the *only* backend origin the SPA is ever told about, sourced from the `Client__GatewayUrl` environment variable AppHost sets. `ClientOptions.SpaContentSecurityPolicy()` builds a strict CSP scoped to `self`, the Gateway origin, and Google Fonts.

### Angular 22 workspace

`src/web/` is an Angular 22 workspace (`^22.0.0`) with `"prefix": "cp"` set for both projects in `angular.json`:

- `projects/creator-pantry-ui` — the reusable `@creator-pantry/ui` library (`src/lib/styles`, `src/lib/components`).
- The showcase/product-shell app, currently holding only `src/app/core/runtime-config.service.ts`, which reads `/runtime-config.json` before bootstrap.

The dev server always runs through Aspire (`scripts/serve.mjs` / `npm start`), never standalone — this keeps the CORS origin AppHost registers for it in sync with whatever port it actually starts on.

---

## Phase 1 — Identity, gateway, and browser boundary

### Identity model

`src/CreatorPantry.Domain/Data/ApplicationUser.cs`: `ApplicationUser : IdentityUser` with `DisplayName`, `CreatedAt` (UTC, stamped via `IClock`), and `LastWorkspaceId` (a nullable navigation hint only — it never authorizes anything; see Phase 2 for why).

`CreatorPantryDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string>` implements `IWorkspaceIdSource` (`CurrentWorkspaceIdOrNull`, sourced from an optional constructor parameter `IWorkspaceContext?`). It exposes `DbSet`s for `IdempotencyRecords`, `Workspaces`, `WorkspaceMemberships`, `AuditLogs`, and `OutboxMessages`, alongside the standard Identity sets. `OnConfiguring` registers two `SaveChangesInterceptor`s — `WorkspaceOwnershipInterceptor` and `AuditLogImmutabilityInterceptor` (both covered under Phase 2) — and `OnModelCreating` applies `WorkspaceOwnershipConvention.Apply` plus every `IEntityTypeConfiguration` in the assembly. It is a single modular-monolith `DbContext`, registered under connection name `creatorpantrydb`.

### Auth request seam

The auth vertical follows the mandated seam, Controller → Facade → Business → DataLayer → Repository:

- **`AuthController`** (`api/v{version}/auth`, all `[AllowAnonymous]`): `POST register`, `POST password-reset`, `POST password-reset/complete` — all return a uniform 202 regardless of whether the account exists, so responses never disclose which emails are registered.
- **`AccountController`** (`.../account`): `POST password` — change password, signed-in only.
- **`InternalSessionsController`** (`.../internal/sessions`, `[Authorize(Policy = GatewayService)]`, excluded from OpenAPI): `POST` verifies credentials, `POST validate` revalidates a session/security stamp. This route only ever accepts the Gateway's own service-to-service token and is never proxied to browsers.
- **`IAuthFacade` / `AuthFacade`** (`Domain/Facade/Auth/`) — shape validation, delegates to Business.
- **`IAuthBusiness` / `AuthBusiness`** (`Domain/Business/Auth/`) — `RegisterAsync`, `RequestPasswordResetAsync`, `CompletePasswordResetAsync`, `ChangePasswordAsync`, `VerifyCredentialsAsync`, `ValidateSessionAsync`.
- **`IAuthDataLayer` / `AuthDataLayer`** (`Domain/Data/Auth/`) — same operation set over the repository.
- **`IUserRepository` / `UserRepository`** (`Domain/Data/Repositories/`) — thin wrapper over ASP.NET Core Identity's `UserManager`. `CheckCredentialsAsync` deliberately performs a password-hash computation on every path (unknown account, locked account, wrong password) so the *time taken to respond* can't be used to enumerate which accounts exist.

Supporting files: `AccountPolicy.cs`, `AuthErrorCodes.cs`, `AuthServiceCollectionExtensions.AddAuthDomain()`.

### PlatformAdmin role

`PlatformRoles.PlatformAdmin` is the one Identity role seeded platform-wide. Workspace roles (`Viewer`/`Contributor`/`Editor`/`Owner`) are deliberately **not** Identity roles — they live only in `WorkspaceMembership` (Phase 2), because a user's authority is different in every workspace, while `PlatformAdmin` is a single, platform-wide grant. `PlatformRoleSeeder : IDataSeeder` checks existence before creating and tolerates a `DbUpdateException` race, so repeated `MigrationWorker` runs never duplicate the role.

### Gateway and routing

`src/CreatorPantry.Gateway/appsettings.json` configures YARP: route `api` matches `/api/{**catch-all}` to cluster `api`, destination `https+http://api` (resolved through Aspire service discovery — no literal hostnames or ports anywhere), plus a reduced `api-health` route. Registration is:

```text
AddReverseProxy()
  .LoadFromConfig(...)
  .AddServiceDiscoveryDestinationResolver()
  .AddTrustedHeaderTransform()
  .AddInternalTokenTransform()
```

The trusted-header allowlist runs before the internal token is attached, so by the time a request reaches the API it carries only headers the Gateway explicitly chose to forward, plus the Gateway's own freshly minted token.

### API versioning and OpenAPI

`Http/ApiVersioning.cs` uses URL-segment versioning (`UrlSegmentApiVersionReader`) with `AssumeDefaultVersionWhenUnspecified = false`, so an unversioned request is a 404 rather than a silent default. `V1 = new(1, 0)`; every route is `api/v{version:apiVersion}/...`.

`Http/OpenApiDocumentation.cs` maps `/openapi/v1.json` and `/scalar` in **Development only**. A document transformer strips the `Servers` array (internal hostnames are never disclosed) and declares the actual security scheme — an `apiKey`-in-cookie scheme named `bffSession` (`__Host-creatorpantry-session`) — plus shared schemas (`ProblemDetails`, `ValidationProblemDetails`, `CursorPage`) and parameters (`Idempotency-Key`, `X-XSRF-TOKEN`, cursor/limit). An operation transformer auto-adds the security requirement unless the action is `[AllowAnonymous]`, and adds the antiforgery header parameter on unsafe methods.

### Gateway-to-API trust (no OAuth/OIDC server)

There is **no external authorization server** in this codebase — no OpenIddict, no OAuth/OIDC client registration, no token ever issued to the browser. This matches the B-13 baseline decision in `.claude/rules/auth.md` exactly, and it's worth being explicit about because it's easy to assume a JWT implies OAuth: here the JWT is purely an internal, same-team handshake between two processes that already trust each other.

- `InternalTokenDefaults` (`ServiceDefaults`) fixes the contract: issuer `creatorpantry-gateway`, audience `creatorpantry-api`, ES256, ≤2 minute lifetime.
- `Gateway/InternalTokens/InternalTokenIssuer.cs` mints two kinds of token from an ECDSA P-256 private key (loaded from PEM):
  - `Issue(SessionPrincipal)` for product requests (`token_use=user`, carrying `sub`/`sid`/`role`).
  - `IssueService()` for the Gateway's own service identity (`token_use=service`, fixed subject `creatorpantry-gateway`).
- `Gateway/InternalTokens/InternalTokenTransforms.cs` is the YARP transform that attaches the minted token as the *only* credential on every proxied request — it replaces whatever the client sent, so a forged `Authorization` header from the browser is irrelevant.
- `ApiService/Authorization/InternalTokenAuthentication.cs` configures `JwtBearer` on the API to accept only ES256 tokens with the fixed issuer/audience, validated against the Gateway's public key (also PEM), with a custom `LifetimeValidator` enforcing the max-lifetime cap.
- `AuthorizationPolicies.cs` defines the default policy (`token_use=user` required) and a `GatewayService` policy (`token_use=service` + the fixed Gateway subject), used only by `InternalSessionsController`.

The upshot: the Gateway is the sole issuer and the API is the sole verifier of this internal trust; the API never talks to an external identity provider; the browser never receives a JWT at all — only the `HttpOnly` session cookie described next.

### Gateway BFF session

`Gateway/Sessions/GatewaySessionOptions` defines the session cookie:

| Setting | Value |
| --- | --- |
| Cookie name | `__Host-creatorpantry-session` |
| Idle timeout | 8 hours |
| Absolute lifetime | 7 days |
| Revalidation interval | 5 minutes |
| Max staleness tolerance | 15 minutes |

`EdgeSecurity.AddBffSession()` wires cookie authentication (`SecurePolicy.Always`, `SameSite.Strict`, `HttpOnly`, JSON 401/403 instead of redirects — this is an API-shaped session, not a page-redirect login flow), `DistributedTicketStore : ITicketStore` backed by Redis, and Data Protection keys stored in Redis (`RedisXmlRepository`, key `bff:data-protection-keys` — required so multiple Gateway instances can decrypt each other's cookies). A `SessionValidator` revalidates against the API's `InternalSessionsController` on `OnValidatePrincipal`, using `ApiSessionClient` — deliberately configured *without* HTTP resilience retries, so a genuine failed login attempt is never silently retried and double-counted against lockout policy.

`BffEndpoints.cs` maps `/bff/antiforgery`, `/bff/session`, `/bff/login` (rate-limited, rotates the session ID on login to prevent fixation, issues a fresh antiforgery token), and `/bff/logout`.

### Header sanitization, CORS, and antiforgery

`Sessions/TrustedRequestHeaders.cs` is an explicit allowlist of headers forwarded to the API (`Accept`, `Accept-Encoding`/`Accept-Language`, `User-Agent`, conditional-request headers, `Idempotency-Key`, W3C trace context). Everything else — including any client-supplied `Authorization`, `Cookie`, `X-Forwarded-*`, or `X-MS-CLIENT-PRINCIPAL` — is stripped; the Gateway re-adds its own `X-Forwarded-*` values afterward.

CORS is built from `Cors:AllowedOrigins` with exact-origin validation (`ValidateOrigins` rejects wildcards, paths, and embedded userinfo) and `AllowCredentials()` — never a reflected wildcard origin.

Antiforgery uses cookie `__Host-creatorpantry-xsrf` and header `X-XSRF-TOKEN`, enforced in `UseEdgeSecurity()` for every unsafe method under `/bff` or `/api`, returning `400 csrf.invalid` on failure.

### Idempotency

`IdempotencyRecord` (`Domain/Data/IdempotencyRecord.cs`): `Id`, `UserId`, `WorkspaceId`, `Operation`, `Key`, `FingerprintHash` (HMAC-SHA256 of the normalized request — never the raw payload), `ResultJson`, `CreatedAt`, `ExpiresAt`.

`IIdempotencyDataLayer` / `IdempotencyDataLayer.ExecuteAsync<T>` runs the underlying operation and commits it together with the idempotency row in one transaction (`context.Database.CreateExecutionStrategy()`, up to 3 attempts), so a first call and its outcome are recorded atomically — there's no window where the action happened but the idempotency row failed to save (or vice versa). HTTP glue lives in `ApiService/Http/IdempotencyHttp.cs`; storage is `IdempotencyRepository` + `IdempotencyRecordConfiguration`.

### Edge hardening

Response-header and body-limit logic is shared from `ServiceDefaults/EdgeHardening.cs` (Phase 0). Trusted-proxy handling lives in `Gateway/Sessions/TrustedProxies.cs`: forwarded headers (`Edge:TrustedProxies` / `Edge:TrustedNetworks`) are honored for exactly one hop, and are simply never applied — with a warning logged outside Development — when no proxy is explicitly configured. This means a misconfigured deployment fails safe (ignores `X-Forwarded-*` entirely) rather than trusting an attacker-controlled header. Forwarded headers are also stripped whenever `RemoteIpAddress` is null.

---

## Phase 2 — Workspace tenancy core

### Entities

`Workspace` (`Domain/Data/Workspace.cs`): `Id`, `Name`, `Slug` (unique, lowercase kebab-case), `CreatedAt`. It is the root of the ownership tree and is not itself workspace-scoped.

`WorkspaceMembership` (`Domain/Data/WorkspaceMembership.cs`): `Id`, `WorkspaceId`, `UserId`, `Role` (`WorkspaceRole`), `Status` (`WorkspaceMembershipStatus`), `JoinedAt`. It implements `IWorkspaceOwned` but is the one documented exception to the strict global query filter — its filter is relaxed rather than throwing, because membership must be queryable *before* a workspace context exists (see below — resolving a workspace context requires reading membership first, so membership can't itself require an already-resolved context).

`WorkspaceRole` (`Domain/Tenancy/WorkspaceRole.cs`): `Viewer = 0, Contributor = 10, Editor = 20, Owner = 30` — an ordered, gapped enum compared with `>=`, not a flags enum. `WorkspaceMembershipStatus`: `Invited = 0, Active = 10, Removed = 20` — only `Active` counts for resolution and authorization.

### IWorkspaceContext

`Domain/Tenancy/IWorkspaceContext` exposes `IsResolved`, `WorkspaceId`, `WorkspaceSlug`, `MembershipId`, and `Role`. Every property throws `InvalidOperationException` if read before resolution — there is no `Guid.Empty` or nullable-default fallback that code could silently misuse. It is populated exactly once per request by `IWorkspaceContextResolver` (`WorkspaceContext`).

### Workspace resolution stack

Controller → Facade → Business → DataLayer → Repository, mirrored for tenancy itself:

- `Facade/Tenancy/IWorkspaceResolutionFacade` / `WorkspaceResolutionFacade` — `ResolveAsync(userId, ResolveWorkspaceViewModel, ct)`.
- `Facade/Tenancy/IWorkspaceFacade` / `WorkspaceFacade` — `CreateAsync`, `GetCurrentAsync`, `RenameCurrentAsync`, `GetMyMembershipsAsync`.
- `Business/Tenancy/IWorkspaceBusiness` / `WorkspaceBusiness`.
- `Data/Tenancy/IWorkspaceDataLayer` / `WorkspaceDataLayer`.
- `Data/Repositories/IWorkspaceRepository` / `WorkspaceRepository` — the one place allowed to query `WorkspaceMemberships` before a context is resolved, always with an explicit `(WorkspaceId, UserId)` predicate rather than a broad scan.

Supporting: `Tenancy/WorkspacePolicy.cs`, `TenancyErrorCodes.cs`, `TenancyServiceCollectionExtensions.AddTenancy()`, `IWorkspaceOwned.cs` (the marker interface every workspace-scoped entity implements).

### Workspace resolution middleware

`ApiService/Tenancy/WorkspaceResolutionMiddleware.cs` matches `^/api/v\d+/workspaces/(?<slug>[^/]+)(/.*)?$`, proceeds only for tokens with `token_use=user`, extracts the `sub` claim as the user ID, and calls `IWorkspaceResolutionFacade.ResolveAsync`. On failure it sets a bare status code (converted to `ProblemDetails` downstream), so an unknown workspace and a workspace the caller isn't a member of return the *same* 404 — a client can't distinguish "doesn't exist" from "exists but you can't see it." On success it calls `IWorkspaceContextResolver.Resolve(...)`, populating `IWorkspaceContext` for the rest of the pipeline. It runs between `UseAuthentication` and `UseAuthorization`, i.e. after we know who the caller is but before policy checks run.

### Global query filter and ownership enforcement

`WorkspaceOwnershipConvention.cs` applies by reflection: every entity type in the model that implements `IWorkspaceOwned` automatically gets `HasQueryFilter` — so a brand-new workspace entity added in a future phase is covered the moment it implements the interface, with no per-entity configuration to remember. `WorkspaceMembership` gets the relaxed filter described above; every other entity gets a strict filter that throws if it's ever queried before a workspace context is resolved, which turns "forgot to resolve tenancy first" into an immediate exception instead of a silent cross-workspace leak.

`WorkspaceOwnershipInterceptor.cs` (a `SaveChangesInterceptor`) stamps `WorkspaceId` on inserted entities from the resolved context (or accepts a caller-supplied ID only when no context has been resolved yet — the first-workspace-creation path, where there's no existing workspace to resolve against). It rejects an insert pre-stamped with a *different* workspace, and throws on any update that attempts to change `WorkspaceId` — ownership is immutable once set. Feature Business code never assigns `WorkspaceId` itself; it's always the interceptor's job.

### Authorization policies

`ApiService/Authorization/WorkspaceRoleRequirement.cs` is a record, `WorkspaceRoleRequirement(WorkspaceRole MinimumRole) : IAuthorizationRequirement`. `WorkspaceRoleAuthorizationHandler : AuthorizationHandler<WorkspaceRoleRequirement>` succeeds only when `IWorkspaceContext.IsResolved && Role >= MinimumRole` — it fails closed simply by never calling `Succeed`. `AuthorizationPolicies.cs` defines `WorkspaceViewer`, `WorkspaceContributor`, `WorkspaceEditor`, and `WorkspaceOwner` policies that all share this one requirement/handler pair, so role implication (Owner can do anything an Editor can, etc.) is encoded once instead of being re-derived on every endpoint. These sit alongside the `PlatformAdmin` and `GatewayService` policies from Phase 1. `MapAuthenticatedControllers()` auto-applies `[Authorize]` to any controller action that doesn't explicitly opt out with `[AllowAnonymous]`.

### Workspace management endpoints

- **`MeController`** — `GET /api/v1/me` → `GetMyMembershipsAsync(userId)`, returning every membership (active or not) for the signed-in user. No workspace route segment; default policy (any signed-in user).
- **`WorkspacesController`**:
  - `POST /api/v1/workspaces` — create; the caller becomes Owner atomically, 201 with `Location`.
  - `GET /api/v1/workspaces/{workspaceSlug}` — `[Authorize(Policy = WorkspaceViewer)]`, returns the resolved workspace plus the caller's membership.
  - `PATCH /api/v1/workspaces/{workspaceSlug}` — `[Authorize(Policy = WorkspaceOwner)]`, rename only — the slug itself is immutable once created.

### Workspace cache-key helper

`Domain/Caching/CacheKeys.cs`: `Workspace(workspaceId, category, id)` produces `"workspace:{id:D}:{category}:{id}"` and validates a non-empty workspace ID; `Global(category, id)` produces `"global:{category}:{id}"`. There is no overload that lets a global key carry workspace scope (or a workspace key omit it), so the two key families can't be confused at the call site — a typo can't accidentally cache one workspace's data under a key another workspace's request might read. It is a pure string builder; it does not touch a cache store itself.

### Audit log

`AuditLog` (`Domain/Data/AuditLog.cs`) implements `IWorkspaceOwned`: `Id`, `WorkspaceId`, `ActorUserId?`, `Action` (a stable code, e.g. `workspace.renamed`), `ResourceType`, `ResourceId`, `CorrelationId`, `OccurredAt`, `Summary`, `BeforeReference?`, `AfterReference?` — the before/after fields are documented as *pointers* only, never secrets or content bodies (per SEC-007 / DATA-RQ-001). `AuditLogImmutabilityInterceptor : SaveChangesInterceptor` throws on any attempt to modify or delete an existing `AuditLog` row, so the audit trail can't be edited after the fact even by application code. The writer seam is `Data/Audit/IAuditWriter` / `AuditWriter`, registered via `AddAudit()`.

### Transactional outbox

`OutboxMessage` (`Domain/Data/OutboxMessage.cs`): `Id`, `Type` (a keyed-DI routing key), `PayloadJson`, `CorrelationId`, `CreatedAt`, `AvailableAt`, `Attempts`, `Status` (`OutboxMessageStatus`), `LeasedBy`, `LeaseExpiresAt`, `CompletedAt`, `LastError`. The row itself is not workspace-owned — any workspace ID a handler needs lives inside `PayloadJson` — because outbox dispatch is infrastructure plumbing, not a workspace-scoped read/write path in its own right.

`IOutboxWriter` / `OutboxWriter` and `IOutboxDispatcher` / `OutboxDispatcher` live under `Domain/Data/Outbox/` and `Domain/Outbox/`, registered via `AddOutbox()`. Writing a message happens in the *same* database transaction as the domain change it describes, so a committed domain write can never "forget" to also record the outbox message.

`src/CreatorPantry.Worker/OutboxDispatcherHostedService.cs` is a `BackgroundService` driven by a `PeriodicTimer` (interval from `OutboxPolicy`), taking a fresh DI scope each pass and calling `IOutboxDispatcher.DispatchDueAsync`, logging claimed/completed/retrying/poisoned counts. It is documented as single-instance-only for now — claiming a message is a plain read-then-update, not a race-free lease — so running more than one Worker replica today would let two instances double-process a message. That's a known, deliberate limitation to revisit before scaling the Worker horizontally.

### Two-workspace test harness

`src/CreatorPantry.Tests/Tenancy/TwoWorkspaceGatewayFixture.cs` spins up a real Gateway (`WebApplicationFactory<Gateway::Program>`) in front of a real SQLite-backed API host, creates two workspaces through the actual `POST /api/v1/workspaces` endpoint (both named "Sam's Kitchen", so their slugs disambiguate naturally — `sams-kitchen` / `sams-kitchen-2`), and gives each an Owner plus one Editor/Viewer member, exposed as `WorkspaceA` / `WorkspaceB`. Using the real HTTP pipeline (not a repository-level shortcut) is the point: it's the standard fixture referenced by `.claude/rules/tenancy.md`'s isolation-testing requirement, and it's what every later workspace feature is expected to reuse. It's consumed today by `WorkspaceEndpointIsolationTests` and `WorkspaceResolutionDisclosureTests`, alongside unit-level coverage in `WorkspaceOwnershipConventionTests`, `WorkspaceOwnershipInterceptorTests`, `WorkspaceResolutionMiddlewareTests`, `WorkspaceContextTests`, and `WorkspaceOwnershipConventionAppliesToNewEntitiesTests`.

---

## Migrations applied so far

`src/CreatorPantry.Domain/Migrations/`:

| Migration | Adds |
| --- | --- |
| `20260922134903_InitialIdentity` | Identity schema (`Users`, `Roles`) plus `ApplicationUser` extensions |
| `20260922150038_AddIdempotencyRecords` | `IdempotencyRecords` |
| `20260922200932_AddWorkspaceAndMembership` | `Workspaces`, `WorkspaceMemberships` |
| `20260922213459_AddAuditLog` | `AuditLogs` |
| `20260922213848_AddOutboxMessages` | `OutboxMessages` |

`CreatorPantryDbContextModelSnapshot.cs` is current through migration 5. The order matches the Phase 0 → 2 dependency order: identity first (everything else references a user), then idempotency infrastructure, then tenancy, then audit, then outbox.

## Request seam in practice

Every HTTP feature built so far follows the mandated seam:

```text
Controller → Facade → Business → DataLayer → Repository | Gateway
```

Concretely, for `POST /api/v1/workspaces`:

```text
WorkspacesController
  → IWorkspaceFacade.CreateAsync           (validation, policy coordination)
    → IWorkspaceBusiness.CreateAsync       (invariants: caller becomes Owner atomically)
      → IWorkspaceDataLayer.CreateAsync    (transaction boundary)
        → IWorkspaceRepository             (EF Core persistence)
```

And for the gateway-to-API trust boundary that sits in front of every one of these calls:

```text
Browser
  → Gateway     (session cookie, CSRF check, header sanitization, CORS)
  → Gateway mints a token_use=user ES256 internal token
  → API         (validates issuer/audience/lifetime, resolves IWorkspaceContext)
  → WorkspaceRoleAuthorizationHandler checks Role >= policy minimum
  → Controller → Facade → Business → DataLayer → Repository
```

No layer in this chain accepts a client-, header-, or model-supplied `WorkspaceId`; it is always resolved server-side from the authenticated session and the route slug.

## Verifying it yourself

These map to the checkpoints in `docs/prompts/creatorpantry-scrub-microprompts.md`:

- **Checkpoint 0.7** — `aspire run` from the repository root should show SQL, Redis, blob storage, the migration service, worker, API, gateway, and web host all healthy in the dashboard, starting from an empty data volume.
- **Checkpoint 1.10** — through the Gateway (not by calling the API directly), a user can register, sign in, make an authenticated request, have their session revalidate/refresh, and sign out — with no access or refresh token ever visible in browser storage, JavaScript, or logs.
- **Checkpoint 2.10** — `dotnet test` including `src/CreatorPantry.Tests/Tenancy/` should pass, demonstrating that Workspace A and Workspace B (via `TwoWorkspaceGatewayFixture`) cannot read, mutate, cache, or enumerate one another's data through the real HTTP pipeline.

General verification: `dotnet build` at the solution root, `dotnet test` for backend coverage, and `dotnet ef migrations has-pending-model-changes --project src/CreatorPantry.Domain --startup-project src/CreatorPantry.ApiService` to confirm the model and the migrations in the table above are in sync.
