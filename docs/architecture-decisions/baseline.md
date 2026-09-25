# Baseline Architecture Decisions

*Status: Accepted — 2026-09-22. Recorded by microprompt 0.1.*

This record fixes the decisions that the requirements still express as working directions, so later
microprompts build on a stated baseline instead of an assumption. It does not implement code.

## Sources

- `CLAUDE.md` and `.claude/rules/` — architectural source of truth.
- `docs/prompts/creatorpantry-scrub-microprompts.md` — delivery sequence and prerequisites.
- `creatorpantry-ai-augmented-recipe-development-requirements.md` — functional source of truth,
  defines DEC-001 through DEC-012. **Not present in the repository when this record was written.**

### DEC reconciliation

The DEC column below is `TBD` except where the microprompts tie an ID to a topic (DEC-010). When the
requirements document is added, map each DEC ID to a row, add rows for any DEC not covered here, and
stop to reconcile if a DEC conflicts with a recorded decision. Do not renumber the `B-` identifiers;
later documents may reference them.

## Decision table

| ID | Topic | DEC | Status | Decision |
| --- | --- | --- | --- | --- |
| B-01 | Workspace tenancy | TBD | Decided | Route-resolved workspace, membership-derived authorization, global query filters, 404 on unknown or inaccessible workspaces. |
| B-02 | BFF authentication | TBD | Decided | ASP.NET Core Identity behind the YARP BFF; `HttpOnly` `Secure` cookie; Redis-backed session ticket store. |
| B-03 | SQL Server 2025 | TBD | Decided | Single `creatorpantrydb`; native vector support used from Phase 9 grounded retrieval. |
| B-04 | Redis | TBD | Decided | Application cache and BFF session tickets; workspace-prefixed keys. |
| B-05 | Blob storage | TBD | Decided | Azure Blob Storage in production, Azurite emulator locally, private access only. |
| B-06 | Staged media | TBD | Decided | Private staging before explicit DAM commit; uncommitted objects expire after 7 days (configurable). |
| B-07 | AI proposal lifecycle | TBD | Decided | All AI output is a proposal or artifact until explicit acceptance. |
| B-08 | Unit systems | TBD | Decided | Workspace default system plus per-view metric/US toggle; creator-entered text preserved. |
| B-09 | Nutrition optionality | TBD | Decided | Nutrition is optional; USDA FoodData Central is the planned reference source. |
| B-10 | First publishing adapter | TBD | Decided | Buffer, behind a provider-neutral publishing model. |
| B-11 | Brand profile and context | DEC-010 | Decided | Versioned workspace brand guide; generation pinned to an exact `BrandContextPackage`. |
| B-12 | Unmapped DEC items | TBD | **DECIDE** | See [Open items](#open-items). |
| B-13 | Gateway-to-API trust (no OIDC server) | TBD | Decided | Direct BFF: the gateway owns the session and forwards a short-lived gateway-signed internal token; no OAuth/OIDC authorization server. |
| B-14 | Machine operations access | TBD | Decided | Hashed, rotatable per-client API keys limited to explicit `ops` routes; never a creator identity. |
| B-15 | Model provider | TBD | Decided | Microsoft Foundry: Foundry Local in development, Azure Foundry deployments when deployed; separate `chat` and `embeddings` deployments behind `IChatClient` and `IEmbeddingGenerator`. |
| B-16 | Prompt templates | TBD | Decided | Embedded `.prompt.md` files with JSON front matter and a declared body checksum; validated at startup; many versions of an id coexist. |

## Decisions

### B-01 Workspace tenancy

- Every workspace route is `/api/v1/workspaces/{workspaceSlug}/...`. The server resolves the slug,
  confirms active `WorkspaceMembership`, and creates an immutable request-scoped `IWorkspaceContext`.
- `WorkspaceId` is never accepted from a body, query string, client header, AI tool argument, or
  unvalidated job payload.
- Every workspace-owned entity has a required indexed `WorkspaceId` and an EF Core global query filter.
  `IgnoreQueryFilters()` is limited to documented migration, erasure, and platform-maintenance paths.
- Unknown and inaccessible workspaces both return 404.
- Initial membership roles: `Viewer`, `Contributor`, `Editor`, `Owner`. `PlatformAdmin` does not imply
  workspace access.
- Platform reference data (ingredients, units, conversions, tags, and similar) has no `WorkspaceId`.

**Consequences:** every workspace feature owes a two-workspace isolation test covering list, detail,
mutation, search, cache, background processing, and AI retrieval.
**Rules:** `tenancy.md`, `auth.md`.

### B-02 BFF authentication

- ASP.NET Core Identity owns users, credentials, recovery, and lockout.
- The Angular SPA talks only to `CreatorPantry.Gateway`. The browser receives a `HttpOnly`, `Secure`
  session cookie with the narrowest viable domain, path, and SameSite policy. No access or refresh
  token reaches browser storage, JavaScript, URLs, or logs.
- **Session tickets are stored server-side in Redis** (B-04). The cookie carries only a ticket
  reference. Logout deletes the Redis ticket and clears the cookie, so a copied cookie stops working.
- State-changing requests require CSRF protection.
- The gateway strips client-supplied `Authorization`, forwarding, and internal identity headers before
  adding trusted values.

**Options considered:** a cookie-only encrypted ticket was rejected because it cannot be revoked
server-side, conflicting with the `auth.md` logout rule, and it does not scale cleanly across gateway
instances.
**Consequences:** the gateway depends on Redis for authenticated requests; Redis unavailability means
sign-in is unavailable, and health checks must reflect that.
**Rules:** `auth.md`, `gateway.md`.

### B-03 SQL Server 2025

- One application database, `creatorpantrydb`, running SQL Server 2025 with persistent local data.
  The container image is pinned to a version supporting the vector features in use.
- Migrations run once through `CreatorPantry.MigrationService` before API and Worker start.
- **Native `VECTOR` columns and vector indexes are used from Phase 9** for grounded retrieval and
  semantic search. Embeddings carry `WorkspaceId`, and every vector query applies the workspace filter.
- Vector search is added only where semantic retrieval gives real value; keyword and structured
  queries stay the default for exact lookups.

**Consequences:** embedding-model changes need a re-embedding plan; embedding storage is covered by
isolation tests.
**Rules:** `aspire.md`, `ai.md`, `tenancy.md`.

### B-04 Redis

- Wired through Aspire and used for the application cache and BFF session tickets (B-02).
- Workspace cache keys start with `workspace:{workspaceId}:`; global reference keys never include a
  workspace. Invalidation uses the same scope as the cached value.
- Personalized responses are never cached at a shared proxy layer.

**Consequences:** Redis is a required dependency for the gateway and API, not an optional optimization.
**Rules:** `tenancy.md`, `gateway.md`, `aspire.md`.

### B-05 Blob storage

- **Azure Blob Storage in deployed environments; the Azurite emulator in local Aspire runs**, declared
  in the AppHost through the Aspire Azure Storage integration.
- SQL holds metadata, ownership, relationships, and processing status; blob storage holds bytes only.
- Containers are private. Access is authorized against metadata first, then served through short-lived
  controlled access or a proxy. Storage credentials are never exposed.

**Options considered:** S3-compatible storage (MinIO) was rejected because Phase 16 targets Azure.
**Rules:** `media.md`, `aspire.md`.

### B-06 Staged media

- Generated and uploaded bytes land first in private **staging** storage with workspace-scoped staging
  records and provenance.
- An object becomes a DAM asset only through an explicit commit, which records lineage to the staging
  record and any source recipe, prompt, or generation.
- **Uncommitted staged objects expire after 7 days by default.** The retention period is configuration,
  not a code constant. A durable, idempotent cleanup job deletes expired objects and reconciles orphans
  between SQL and storage.
- Committed originals are immutable; crops, thumbnails, and conversions are derived assets.

**Consequences:** resume-later workflows must warn before a staged object expires.
**Rules:** `media.md`; microprompts 12.7–12.10.

### B-07 AI proposal lifecycle

- Every AI output is a `Draft`, `Proposal`, or `GenerationArtifact` until the creator explicitly
  accepts it. Proposals support review, partial acceptance, rejection, and retry.
- Retrying never creates duplicate accepted artifacts. Cancellation stops streaming and records the
  outcome accurately.
- Each generation records model/provider, prompt-template version, input record references, timestamps,
  token usage, latency, status, safety flags, and disposition. Private prompt bodies and generated
  content are not logged by default.
- Deterministic work (scaling, conversions, arithmetic, authorization, state transitions) is performed
  by domain code; the model may only produce constrained commands for it.
- Domain code depends on `IChatClient` and `IEmbeddingGenerator`; Semantic Kernel plugins call facades
  only and never accept a model-supplied workspace id.

**Rules:** `ai.md`, `recipes.md`, `content.md`.

### B-08 Unit systems

- Each workspace sets a **default display system (metric or US customary)**. A viewer can toggle the
  system for the current view without changing the recipe or the workspace default.
- Creator-entered ingredient text is always stored and shown as entered; converted values are a
  presentation or a proposal, never a silent overwrite.
- Conversion distinguishes mass, volume, count, and temperature and never crosses dimensions without
  ingredient-specific density data. Non-scalable or non-convertible lines are flagged for review.

**Options considered:** a per-user preference was deferred; it can be layered on later without changing
stored recipe data.
**Rules:** `recipes.md`; microprompts in Phase 7.

### B-09 Nutrition optionality

- Nutrition is optional. It is never required to save, approve, export, or publish a recipe.
- **USDA FoodData Central is the planned reference source.** Phase 14 verifies its current API, terms,
  and rate limits before integration and places it behind a typed gateway.
- Every nutrition value records method and source. AI-only estimates are labeled as estimates. Missing
  data never implies absence of an allergen, and nutrition output is not a medical or dietary guarantee.
- Unmapped ingredients are surfaced as unresolved rather than guessed.

**Consequences:** until Phase 14 lands, the product shows no computed nutrition.
**Rules:** `recipes.md`, `external.md`; Phase 14.

### B-10 Buffer as the first publishing adapter

- Publishing is a provider-neutral domain: `PublishingTarget`, `Publication`, `PublicationRevision`,
  `ExternalPublicationReference`, and `DeliveryAttempt`. Buffer is the first adapter behind a typed
  gateway; Buffer DTOs never cross that boundary.
- Provider identifiers live only in publication mapping records, never on `Recipe` or
  `ContentProject`.
- Scheduling and cancelling are idempotent commands with persisted idempotency keys. Publishing
  requires explicit confirmation or a creator-approved automation policy.
- Buffer credentials are Aspire secret parameters locally and a secret store in production.
- Without a provider connection, export and download workflows still work.

**Rules:** `publishing.md`, `external.md`; microprompts 13.9–13.10b.

### B-11 Brand profile and context (DEC-010)

- `BrandProfile` and a versioned `BrandStyleGuide` are workspace-owned. Guide versions are immutable and
  activated explicitly by the creator.
- Each generation assembles a bounded, deterministic `BrandContextPackage` server-side, pinned to an
  exact guide version and authorized source-document versions.

**Note:** the DEC-010 mapping is inferred from microprompts 11.1, 11A.2, and 11A.19; confirm it against
the requirements document.
**Rules:** `content.md`, `ai.md`.

### B-13 Gateway-to-API trust (no OIDC server)

*Recorded 2026-09-22 by microprompt 1.7, which assumed an "approved OAuth/OIDC server arrangement" that
no record approved.*

- There is **no OAuth/OIDC authorization server** and no OAuth clients. ASP.NET Core Identity in the API
  remains the only credential store.
- **Sign-in (prompt 1.8):** the gateway calls an internal API credential check (Identity password check
  with lockout; confirmed email required) that is not reachable through the browser-facing `/api/**`
  proxy. On success the gateway creates the session (B-02: Redis ticket, `__Host-` `HttpOnly` `Secure`
  cookie).
- **Per request:** the gateway strips client-supplied `Authorization`, `Cookie`, and forwarding headers,
  then attaches a **gateway-signed internal token** for authenticated sessions: an ES256 JWT with issuer
  `creatorpantry-gateway`, audience `creatorpantry-api`, `sub` = user id, `sid` = session id, and a lifetime
  of at most two minutes. The API accepts no other authentication scheme.
- **Service vs user tokens:** user tokens carry `token_use=user` and are the only tokens product routes accept.
  The gateway calls the internal session routes (`/api/v1/internal/sessions`, credential check and
  security-stamp revalidation) with a `token_use=service` token; those routes accept nothing else, and the
  gateway returns 404 for `/api/*/internal/**` from browsers.
- **Keys:** the gateway holds the private signing key (secret parameter locally, secret store when
  deployed); the API holds only the public key. Key rotation by `kid` is a later hardening step.
- **Refresh and revocation:** no refresh tokens exist. Sessions slide; the gateway periodically revalidates
  the user's security stamp with the API so password change or reset ends sessions. Logout deletes the
  Redis ticket and clears the cookie; "log out everywhere" rotates the security stamp.
- **Secure by default:** every API controller action requires an authenticated user; public
  actions opt out explicitly with `[AllowAnonymous]`.

**Options considered:** OpenIddict hosted in the API (gateway as a confidential code + PKCE client with
server-side refresh tokens) was rejected for this phase in favor of fewer moving parts.
**Consequences:** mobile or third-party clients would need a new decision (and likely an authorization
server). The OpenIddict-ready note in prompt 1.5 no longer applies.
**Rules:** `auth.md`, `gateway.md`.

### B-14 Machine operations access

- Operational automation authenticates with a **per-client API key**, delivered through the secret store,
  stored only as a hash, rotatable, and audited on use.
- Keys are accepted only on explicit `/api/v1/ops/*` routes under an `ops` authorization policy. An ops
  key never acts as a creator and never grants workspace access.
- Implementation is deferred until the first ops route exists.

**Options considered:** OAuth `client_credentials` (no authorization server under B-13) and gateway-minted
service tokens were rejected.
**Rules:** `auth.md`, `external.md` (credential handling).

### B-15 Model provider

*Recorded 2026-09-25 by microprompt 8.1, which required a provider before any prompt or model call.*

- The provider is **Microsoft Foundry**, added to the AppHost with `Aspire.Hosting.Foundry`.
  `RunAsFoundryLocal()` runs models on the developer's machine in run mode; publish mode targets Azure
  Foundry deployments. The logical resource names do not change between the two.
- **Chat and embeddings are separate named deployments** (`chat`, `embeddings`), not one resource with two
  uses, so they can be sized, priced, swapped and traced independently. Re-embedding a workspace is a far
  more expensive change than switching chat models, and the two should never be coupled by accident.
- **Which model** each deployment runs is AppHost configuration (`Foundry:ChatModel`,
  `Foundry:EmbeddingModel`), never a literal in the application model. Local development runs
  `phi-4-mini` and `qwen3-embedding-0.6b`; the intended deployed chat model is `claude-sonnet-5`
  (Foundry format `Anthropic`), with a Cohere or OpenAI embedding deployment beside it. Foundry hosts
  several model families, which is the main reason it was chosen over a single-vendor endpoint.
- **No credential lives in configuration.** Foundry Local publishes its own endpoint and key in the
  deployment's connection string; Azure mode publishes no key at all and the client authenticates with the
  ambient Azure credential.
- **Application code depends on `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` only.**
  `CreatorPantry.AiProvider` is the single assembly permitted to name a provider SDK — it consumes the
  deployments through `Aspire.Azure.AI.Inference` — and `DomainReferenceTests` fails if one reaches
  `CreatorPantry.Domain`.
- **Foundry is opt-in locally** (`Foundry:Enabled`, default off), because `RunAsFoundryLocal()` drives the
  Foundry CLI on the developer's machine. With it off, the API and Worker register clients that throw
  rather than answer, so a clean clone starts with no Foundry install and no model account, and nothing can
  mistake a stub for a generation.

**Options considered:** an OpenAI-compatible endpoint via `Aspire.Hosting.OpenAI` (stable rather than
preview, and redirectable at Ollama or LM Studio with `WithEndpoint`) was rejected because it fixes both
deployments to one model family. Splitting the two across two hosting integrations was rejected as two
credential paths to maintain before anything calls a model.
**Consequences:** `Aspire.Hosting.Foundry` pulls in `Aspire.Hosting.Azure`, so publish mode now wants
`Azure:SubscriptionId`, `Azure:ResourceGroupPrefix` and `Azure:Location`; run mode wants none of them. Both
the hosting and client integrations are preview at 13.5.4, and `Azure.AI.Inference` is itself `1.0.0-beta.5`
— revisit the pins when a stable AI client integration ships.
**Rules:** `ai.md`, `aspire.md`.

### B-16 Prompt templates

*Recorded 2026-09-25 by microprompt 8.2. Note that AI-001 and AI-002, which that prompt cites, are not
defined anywhere in this repository — the same gap [B-12](#b-12-unmapped-dec-items--decide) records for
`DEC-001`–`DEC-012`. This decision takes them from `ai.md`: prompts are versioned files with declared inputs
and outputs, and structured outputs use schemas and server validation.*

- A template is **one `.prompt.md` file**: a JSON front-matter manifest fenced by `---`, then the body.
  The manifest declares `id`, `version`, `outputSchemaVersion`, `safetyClass`, `inputs`, and `bodyChecksum`.
- **Embedded as a resource, never read from disk.** "Templates cannot access secrets or arbitrary files"
  becomes a property of the design rather than a rule to police: there is no path to traverse and no ambient
  read. A prompt also cannot drift from the code whose output schema it promises, because they ship together.
- **JSON front matter, not YAML** — the solution has no YAML package and this does not justify adding one.
  **`.md`, not `.txt`** — `.gitattributes` already declares `*.md text eol=lf`, which is what makes a body
  checksum the same value in a Windows checkout and a Linux one.
- **The body checksum is declared in the manifest and verified at load.** A prompt body therefore cannot
  change without its manifest changing in the same diff, which puts a prompt change in front of a reviewer
  instead of letting it land as a text tweak. The verified value is carried on as provenance.
- **Validated at startup**, during service registration rather than on first use, so a malformed, mis-declared
  or duplicated template stops the host instead of surfacing inside a creator's generation.
- **Several versions of one id coexist.** New work resolves the highest; anything reproducing or explaining a
  stored generation resolves the exact version recorded against it. Otherwise a template bump would orphan
  every proposal that named the old one.
- **`safetyClass` is required and has no default**, with `Unspecified` occupying zero so that omitting the
  field fails rather than silently meaning the least restrictive class. The five classes — `None`,
  `CulinaryAdvice`, `DietaryOrAllergen`, `NutritionEstimate`, `FoodSafety` — are one per distinct caution in
  `ai.md`. This phase requires the declaration; the structured-output validator and the individual
  capabilities are what act on it.
- A template body carries **task instructions only**. System policy, retrieved references and untrusted
  creator or imported text are assembled around it by the context envelope, which owns the delimiting.

**Options considered:** files under a configured content root were rejected — editing a prompt without a
rebuild is not worth a traversal surface, a deployed copy that can drift from the build that declared its
output schema, and turning a structural guarantee into a rule. A single version per id was rejected because it
makes the template version recorded on a stored proposal unresolvable as soon as the template moves on.
**Consequences:** changing a prompt's wording requires recomputing `bodyChecksum` and rebuilding. Old versions
accumulate until a retention decision retires them.
**Rules:** `ai.md`.

## Open items

### B-12 Unmapped DEC items — DECIDE

- **Question:** the microprompts reference twelve decisions (DEC-001–DEC-012), but only ten topics
  are named for this baseline and only DEC-010 has a known topic. At least one DEC is unrecorded.
- **Options:** (a) add the requirements document and reconcile; (b) the team restates the missing
  decisions directly in this record.
- **Impact:** a missing decision may affect any phase; the owning feature phase is unknown.
- **Default assumed by later prompts:** no additional decision beyond B-01–B-11. A prompt that meets an
  unrecorded product choice stops and asks instead of choosing.
