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
| B-14 | Machine operations access | TBD | Decided | Hashed, rotatable per-client API keys limited to explicit `ops` routes; never a creator identity. Implemented in 9A.9. |
| B-15 | Model provider | TBD | Decided | Microsoft Foundry: Foundry Local in development, Azure Foundry deployments when deployed; separate `chat`, `embeddings` and `images` deployments behind `IChatClient`, `IEmbeddingGenerator` and `IImageGenerator`. |
| B-16 | Prompt templates | TBD | Decided | Embedded `.prompt.md` files with JSON front matter and a declared body checksum; validated at startup; many versions of an id coexist. |
| B-17 | AI proposal boundaries | TBD | Decided | Proposal is write-once; per-change disposition on the change row; one execution row per provider attempt, owned by the operation; structured change targets, not path strings. |
| B-18 | Model output contract | TBD | Decided | The C# document type is the schema, exported for the prompt with `JsonSchemaExporter`; validated in five stages; no repair; an empty proposal is a valid answer. |
| B-19 | Prompt context envelope | TBD | Decided | Instructions in the system role, data in the user role; nonce-fenced segments; trust fixed by segment kind; every non-instruction segment stamped with its workspace and checked. |
| B-20 | Model execution wrapper | TBD | Decided | Resilience pipeline in ServiceDefaults with the transience predicate supplied by the host; classification behind a provider-implemented interface; one attempt record per call; exactly one corrective re-ask for a schema failure. |
| B-21 | Proposal disposition and atomic acceptance | TBD | Decided | A proposal may only offer a change the recipe patch can apply; accepted changes travel the recipe module's own merge path; the confirmation names the accepted change ids; one explicit transaction in the AI data layer spans both modules; replay compares the decision rather than a key. |
| B-22 | Platform audit trail | TBD | Decided | Actions taken outside any workspace go to a separate unfiltered, immutable `PlatformAuditLog` with a required reason; `AuditLog` stays workspace-owned and filtered. |
| B-33 | Evidence sources | TBD | Decided | USDA FoodData Central (Foundation, SR Legacy; CC0) for nutrition and density; no approved source for allergen, dietary or food safety, which read as unknown. |
| B-34 | Nutrition mappings | TBD | Decided | One creator-confirmed mapping per platform ingredient per workspace; automatic proposals are never confirmed; rejections remembered; refresh flags newer versions for review. |
| B-35 | Nutrition estimate arithmetic | TBD | Decided | Exact arithmetic over one recipe version; rounded once at output; edible portion as written; unknown nutrients never zero; publishable only when every required line is counted. |
| B-36 | Dietary and allergen analysis | TBD | Decided | Approved traits only; missing is unknown; most cautious finding wins; conflicts cited; disclaimer always; nothing is ever "safe". |
| B-37 | High-risk method warnings | TBD | Decided | Deterministic detection from techniques and wording; no approved food-safety source, so every warning says verification is required; ambiguous wording is "possible"; nothing blocks a recipe. |
| B-38 | Dietary and allergen analysis endpoint | TBD | Decided | Read-only POST per version number; confidence describes record completeness, never likelihood; every cited source listed with licence and latest effective date; refusals as the Recipes routes give them. |
| B-40 | Nutrition mapping commands | TBD | Decided | GET plus confirm, reject and remap per ingredient, one decision per request; token required for every decision on an existing mapping; Editor; four mapping codes corrected to answer their status. |
| B-39 | Nutrition estimate endpoint | TBD | Decided | Read-only POST per version number with an optional serving count as the yield basis; labelled estimate; unresolved lines grouped and `publishable` exposed, not wired to readiness; 503 when the source cannot be asked. |
| B-41 | Dietary and allergen review UI | TBD | Decided | A tool in the recipe editor's drawer over the saved version; the server's statements and disclaimer verbatim; no finding is ever a success tone and no sentence of its own says safe, free from, certified or suitable; decoded whole or not at all. |
| B-42 | Nutrition mapping and estimate UI | TBD | Decided | A tool in the recipe editor's drawer: the server's figures rendered as given with no arithmetic in the browser, a partial estimate shown with its gaps named, and confirm/turn-down for the mappings it rests on — reached through the estimate, because a mapping has no other address. |
| B-43 | Food-domain safety audit | TBD | Decided | A recipe capability's free text may not assure safety, absence or suitability, enforced by a scanner rather than asked for in a prompt; a dietary adaptation owes a caution and may not set its own yield; an unquantified required line makes an estimate unpublishable (amends B-35); 14.5's warnings get a route and a screen. |

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

**Consequences:** until Phase 14 lands, the product shows no computed nutrition. FDC's terms were verified and
the source approved in [B-33](#b-33-evidence-sources).
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
- ~~Implementation is deferred until the first ops route exists.~~ **Implemented 2026-09-29 by microprompt
  9A.9**, the first ops route (`/api/v1/ops/ai-usage/**`, USAGE-009).

**As implemented:**

- The credential is `cpops_<prefix>.<secret>`: a public prefix that identifies the client, and 32 random
  bytes that prove it. `OpsApiClient` stores a per-client salt and `SHA256(salt || secret)` and never the
  key. SHA-256 rather than a password hash on purpose — the secret is 256 bits of entropy rather than a
  memorised password, so there is no dictionary to slow down, and this runs on every ops request.
  Comparison is constant-time.
- **Provisioning and rotation go through the secret store only.** `OpsApiClientSeeder` reads `Ops:Clients`
  (the AppHost supplies `ops-api-key` as a secret parameter to the migration service alone) and inserts or
  rotates. There is deliberately **no route that creates, lists, returns or rotates a key** — an API that
  could mint one would be a second, weaker path to the same privilege. The salt is derived from the key's
  own prefix, so re-seeding an unchanged key is a no-op and a changed key rotates, invalidating the previous
  one immediately. With the parameter empty — the default — nothing is provisioned and every ops route
  answers 401.
- **Scopes, not a single "is an ops key" flag.** `OpsScopes.AiUsageAdmin` (`ai-usage.admin`) is the first;
  the `Ops` policy requires it. An automation issued a narrower grant cannot widen it by holding a key.
- **The `Ops` policy names its own authentication scheme** (`OpsApiKey`), registered beside the JWT scheme
  rather than as the default. That keeps the two credentials apart in both directions: a gateway-minted user
  token — *including one carrying `PlatformAdmin`* — is refused on an ops route because the JWT scheme never
  runs there, and an ops key opens no product route because every other policy demands `token_use=user`,
  which an ops principal never carries.
- **The gateway returns 404 for `/api/*/ops/**`**, alongside `internal` and `dev`, so ops routes are never
  browser-reachable and their existence is not disclosed. (The proxy already strips `Authorization` on every
  hop, so a key could not survive the journey regardless; the 404 states the intent.)
- Ops routes carry `[ApiExplorerSettings(IgnoreApi = true)]`, so the reviewed v1 OpenAPI document — the
  browser-facing contract — does not advertise an API-key scheme the SPA must never hold.
- **Audited on use** is `OpsApiClient.LastUsedAt`; *what the key did* is recorded separately in
  `PlatformAuditLog` (see below), which names the acting client, the subject, a required reason, and compact
  before/after state pointers.

**Options considered:** OAuth `client_credentials` (no authorization server under B-13) and gateway-minted
service tokens were rejected.
**Consequences:** a second ops capability adds a scope rather than a credential type. A human-facing admin
surface would be a new decision: it would need a browser-reachable route, which this design deliberately has
none of.
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
- **Foundry Local is opt-in** (`Foundry:Enabled`, default off), because `RunAsFoundryLocal()` drives the
  Foundry CLI on the developer's machine. **An Azure deployment is prompted for by default** (`Foundry:Azure`,
  default on): a clean clone asks in the dashboard for endpoint, key and both deployment names, and the API
  and Worker wait for them. A developer with no model account sets `Foundry:Azure` to false; the API and
  Worker then register clients that throw rather than answer, so nothing can mistake a stub for a generation.

*Amended 2026-10-08 by microprompt 12.10c-1, which added the third deployment. 12.7 had built the image
gateway and worker against `IImageGenerator` with no deployment behind it, so every generation settled as
`provider-not-configured` and nothing downstream could be shown a real picture.*

- **Images are a third named deployment** (`images`), behind `Microsoft.Extensions.AI.IImageGenerator`, and
  independent of the other two for the same reasons they are independent of each other.
- **It is served by a second provider SDK.** `Azure.AI.Inference` has no image-generation route, so `images`
  goes through `Aspire.Azure.AI.OpenAI` → `AzureOpenAIClient.GetImageClient(...)` →
  `Microsoft.Extensions.AI.OpenAI`'s `AsIImageGenerator()`. Both SDKs live in `CreatorPantry.AiProvider` and
  nowhere else. Chat and embeddings stay on `Azure.AI.Inference`.
- **Its connection string is a different shape**: `Endpoint=<resource root>;Key=<key>;Deployment=<name>`. The
  inference client wants the deployment in the endpoint's path; this client adds the path itself.
- **It needs an Azure OpenAI resource.** Images are served from `/openai/deployments/<name>/images/generations`,
  which a Foundry `/models` endpoint does not have, and **Foundry Local has no image-generation models**, so
  `RunAsFoundryLocal()` cannot own this deployment. In the prompted mode it reuses `foundry-endpoint` and
  `foundry-key` and asks only for `foundry-images-deployment` (`Foundry:Images`, default on); in every other
  mode a supplied `ConnectionStrings:images` is passed through.
- **Which model** is the deployment's own business, as with the other two. `gpt-image-2` is what the prompt
  suggests: at the time of writing it is generally available without an access application and retires
  2027-10-21, where `gpt-image-1` retires 2026-10-23 and the 1-series is limited access.
- **A missing image deployment never stops a host**, in or out of Development. Without chat or embeddings the
  product does not work; without images one feature reports `provider-not-configured`. The fallback is still
  a generator that throws, never a placeholder picture.
- **Provider failures are translated in `CreatorPantry.AiProvider`** (`AzureOpenAIImageGenerator`). The OpenAI
  SDK reports a refusal as `ClientResultException`, which the Media gateway may not name and would therefore
  retry as an outage; it is rethrown as `HttpRequestException` carrying the status and none of the provider's
  text, so the gateway's existing mapping applies unchanged: 429 is `rate-limited`, any other 4xx is
  `provider-refused` and terminal, 408/5xx/unreachable is `provider-unavailable`.

*Amended again 2026-10-08, outside the microprompt sequence, to move images off Azure OpenAI. The `gpt-image`
deployment's throughput quota could not serve the image step, so the provider behind `images` is now
Venice.ai. Where the bullets above name Azure OpenAI, `AzureOpenAIImageGenerator` or
`foundry-images-deployment`, these replace them; the rest stands.*

- **`images` is Venice.ai, not a Foundry deployment**, and is decided independently of all three Foundry
  modes. Still behind `IImageGenerator`, still named `images`, so the Media gateway, the worker and their
  tests did not change.
- **Plain HTTP, no SDK.** `VeniceImageGenerator` calls Venice's native `POST /image/generate` through a named
  `HttpClient`. `Aspire.Azure.AI.OpenAI` is removed, so `Azure.AI.Inference` is again the only provider SDK.
  Venice's OpenAI-compatible `/images/generations` was rejected: it caps a prompt at 1,500 characters where
  a creator may write 4,000 plus an avoid list, substitutes its default model for an unknown model id — which
  would make provenance record a model that did not make the picture — and cannot turn the watermark off.
- **The connection string is `Endpoint=<api root>;Key=<key>;Model=<model id>`.** The AppHost prompts for one
  value, the secret `venice-api-key` (`Venice:Images`, default on), and takes the endpoint and model from
  configuration (`Venice:Endpoint`, `Venice:ImageModel`). A supplied `ConnectionStrings:images` takes
  precedence. `Foundry:Images` and `foundry-images-deployment` no longer exist.
- **Which model** is configuration. The default is `gpt-image-2-5-flare`, chosen because its prompt limit
  (10,000 characters) clears the longest prompt this application composes; several cheaper Venice models
  stop at 1,500–7,500 and would refuse long prompts.
- **The request is fixed**: PNG, no watermark, safe mode on, no size. No size because Venice's models
  disagree on how one is expressed and reject each other's fields. *Superseded for size and quality by the
  AP.2.3 amendment below; PNG, no watermark and safe mode stand.*
- **A flagged picture is a refusal.** With safe mode on Venice answers 200 with a blurred image and a header;
  that is reported as `provider-refused` rather than staged.
- **This client opts out of the ServiceDefaults resilience handler.** Its ten-second attempt timeout is
  shorter than a generation and its retries would each be paid for; the gateway's timeout and the
  operation-level requeue are the only bounds.
- **Failure mapping is unchanged**, and now needs no translation: a non-success status is thrown as
  `HttpRequestException` with the status and none of Venice's text. 402 (no balance) is `provider-refused`.

*Amended 2026-10-26 by AI-pricing prompt AP.2.3 (`ai-pricing.md` B-45, PRICE-003): size and quality are now
always sent.* Leaving them out left each model's default in force, and `gpt-image-2-5-flare`'s default quality is
its dearest ($0.07 at 1K, against $0.04 at medium).

- **One sizing profile per model**, in configuration (`Venice:Models:<id>`, identical in the API and Worker,
  held together by `VeniceProfileTableTests`), from the model's own `model_spec.constraints` in Venice's
  `GET /models?type=image`. A profile names how the model is sized — `Resolution` (tier and aspect ratio, plus
  quality when the model lists qualities), `AspectRatio`, or `Pixels` (width and height on its divisor, at most
  1,280) — its allowed values, this deployment's defaults, and its prompt character limit.
- **A request carries its own kind's fields and no other's**, because Venice rejects a field a model does not
  take (its schema is `additionalProperties: false`). The configured model sends `quality: medium`,
  `resolution: 1K`, `aspect_ratio: 1:1` — the 1:1 every picture already had. Shipped profiles:
  `gpt-image-2-5-flare`, `qwen-image-2-1-turbo`, `seedream-v5-lite`, `z-image-turbo` — one of each kind, and
  the AP.3.3 draft candidates.
- **Nothing is guessed.** A configured model with no profile, or a profile that contradicts itself, refuses to
  start in every environment (a host with no image provider still starts). A per-request model with no
  profile, a value the profile does not offer, or a prompt over the model's limit is refused before anything is
  sent, as a 400-shaped `HttpRequestException` the Media gateway already maps to `provider-refused` — terminal,
  never retried, nothing spent. A prompt is never cut.
- **The prompt limit is the model's own**: 10,000 for `gpt-image-2-5-flare` (owner, 2026-10-26), although
  Venice's generic request schema states 1–7,500. Media composes at most 8,023 characters (a 4,000-character
  prompt, the avoid heading and a 4,000-character avoid list).
- **What was sent is provenance.** The generator stamps the quality and size it sent on the returned image under
  provider-neutral keys (`Managers/Ai/ImageGenerationSettings`), and Media stores them on `GeneratedImage` as
  `GenerationQuality` and `GenerationSize` (`1K 1:1`, `1:1`, `1024x1024`), migration
  `AddGeneratedImageGenerationSettings`. Older pictures read null. They are not on the API yet.
- **Choosing a size or quality is provider-neutral.** `ImageGenerationOptions.ImageSize` and the
  `ImageGenerationSettings` keys in its `AdditionalProperties`; nothing sets them yet, so each profile's
  defaults apply. AP.3.1's quality classes are what will.
- **Verified live** on 2026-10-26: `gpt-image-2-5-flare` at medium, 1K, 1:1 returned a 2.8 MB PNG in 15.7 s
  (`LiveVeniceImageCheck`, gated on `CP_LIVE_IMAGES`).

**Options considered:** an OpenAI-compatible endpoint via `Aspire.Hosting.OpenAI` (stable rather than
preview, and redirectable at Ollama or LM Studio with `WithEndpoint`) was rejected because it fixes both
deployments to one model family. Splitting the two across two hosting integrations was rejected as two
credential paths to maintain before anything calls a model.
**Consequences:** `Aspire.Hosting.Foundry` pulls in `Aspire.Hosting.Azure`, so publish mode now wants
`Azure:SubscriptionId`, `Azure:ResourceGroupPrefix` and `Azure:Location`; run mode wants none of them. Both
the hosting and client integrations are preview at 13.5.4, and `Azure.AI.Inference` is itself `1.0.0-beta.5`
— revisit the pins when a stable AI client integration ships. The images amendment adds
`Aspire.Azure.AI.OpenAI` at the same preview build, and `IImageGenerator` is itself still experimental
(`MEAI001`, suppressed per file). A content-filter block and a rejected credential are both `provider-refused`
to a creator; telling them apart is a new failure category on a shipped contract and is not done here — the
provider's error code is logged instead.
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

### B-17 AI proposal boundaries and retention

*Recorded 2026-09-25 by microprompts 8.3 and 8.4. `DATA-008` and `AIREC-GR-001/007`, which they cite, are
undefined in this repository — see [B-12](#b-12-unmapped-dec-items--decide).*

```text
AiOperation                         root, workspace-owned
├── AiProposal            0..1      root, unique per operation
│   ├── AiStructuredChange 0..n     the server-calculated diff
│   ├── AiWarning          0..n     warnings and assumptions
│   └── AiProposalFeedback 0..n     append-only, per member
└── AiExecutionMetadata    0..n     one per provider attempt
```

- **Everything is write-once except the operation and the structured change.** A proposal records what a model
  said at a moment against a pinned source under a named template; a different answer is a different
  operation, never an edit. The change row is mutable solely so the creator's per-change decision can be
  written to it, which keeps "accept selected" answerable without a sixth table — at the cost that
  "content never changes" is a convention there rather than an interceptor-enforced guarantee.
- **The structured changes *are* the diff.** There is no separate diff document: each change carries a
  server-computed before value and the model's proposed after value. A blob beside them would be the same
  information twice with nothing to say which copy was authoritative.
- **A before value is always computed by the server** from the pinned version, never taken from the model. A
  model-supplied before is an assertion about content the server already has, and trusting one would let a
  forged or stale claim decide what a diff appears to change.
- **Change targets are structured** — target kind, target id, field name, change kind, proposed position —
  not path strings, so a change can be checked against its operation's scope before anything is applied. An
  unparseable path arriving from a model is exactly the unvalidated instruction a proposal must not carry.
- **Execution metadata is one row per attempt, owned by the operation.** A failed attempt produces a row and
  no proposal, and lease recovery can re-run an operation, so a proposal-owned record would lose the attempts
  worth diagnosing. Cost per operation is therefore a sum rather than a column read.
- **The privacy line runs between the artifact and the diagnostics.** A structured change legitimately holds
  proposed recipe text — that is what the creator reviews. `AiExecutionMetadata` holds no prompt body, no
  response, no provider payload: its only free-text column is a short sanitized failure summary, and a test
  asserts that column list so a `RawResponse` field cannot be added quietly. No raw model output is stored
  anywhere.
- **Retention:** every AI entity cascades from the workspace, which is the erasure path. References into the
  recipe module are `Restrict`, so deleting a recipe that has AI history is refused and removing one stays a
  deliberate act that says what becomes of the record. Each interior entity has exactly one cascading foreign
  key, keeping the delete path a tree. Age-based retention of proposals is not decided here.

**Options considered:** a separate append-only disposition entity (rejected: a sixth entity and a join for
every "was this accepted?" read), recording acceptance only on the resulting `RecipeVersion` (rejected:
rejections leave no trace and a partial acceptance cannot show what was declined), and one execution row per
operation with counters (rejected: a retry's own latency and failure vanish into aggregates).
**Consequences:** 8.5 states the migration and retention implications; the AI tables carry no age-based
cleanup until a policy exists.
**Rules:** `ai.md`, `tenancy.md`.

### B-18 Model output contract

*Recorded 2026-09-25 by microprompt 8.6. `AI-003` and `AIREC-GR-001`, which it cites, are undefined in this
repository — see [B-12](#b-12-unmapped-dec-items--decide).*

- **`AiOutputDocument` is the schema.** The JSON Schema shown to a model is exported from that type with
  `System.Text.Json.Schema.JsonSchemaExporter`, and its answer is held to the same type by strict
  deserialization. One definition, so what the model is asked for and what it is judged against cannot drift.
  A hand-written schema file beside the type would be the drift B-16's body checksum exists to prevent
  elsewhere. The cost: expressive constraints live in the domain stage rather than in the schema document.
- **Five stages, in this order:** envelope (size, non-empty) → parse → **schema version** → shape → value
  hygiene → domain. The version is checked before the shape deliberately: a different version is a different
  contract, and "unknown field" complaints about the wrong contract explain nothing.
- **Nothing is repaired.** A truncated document is not completed, an unknown field is not dropped, a wrong
  version is not coerced. Each failure reports whether a bounded re-ask could plausibly fix it; the execution
  wrapper decides whether to spend an attempt, and the validator never retries anything itself.
- **The document type has no before value, anywhere.** "Never trust a model-supplied before value" is
  structural rather than a rule someone enforces — an attempt to send one is an unknown field and is refused.
- **Raw payload cannot escape.** The boundary returns a typed document or a failure carrying a stable reason
  code and a sanitized message bounded to the diagnostic limit. A test feeds a recognisable marker through
  three failure paths and asserts it appears nowhere in the result.
- **A hostile string is bounded, not interpreted.** Injection wording, system-prompt delimiters and
  `{{placeholder}}` syntax pass through as inert data; what is refused is an overlong value or a control
  character — tab, CR and LF exempted, because instruction text legitimately contains them.
- **An empty proposal is valid.** "I looked and there is nothing to change" is a real answer, and failing it
  would tell the creator something untrue and pollute the failure metrics the execution wrapper collects.
- **Scope is enforced here.** A change addressing anything outside its operation's `AiOperationScope` is
  rejected rather than trimmed, and reported as not correctable by re-asking. An undeclared scope permits
  nothing, so a missing scope fails closed.
- The domain stage mirrors the check constraints already on `AiStructuredChanges`, so a document that passes
  validation cannot fail on insert.

**Options considered:** hand-written JSON Schema files with a validation library (rejected: a new dependency
and two sources of truth), and prose-described shape with no machine-readable contract (rejected: measurably
worse structured-output compliance, and prose drifts from the type with nothing to catch it).
**Rules:** `ai.md`.

### B-19 Prompt context envelope

*Recorded 2026-09-25 by microprompt 8.7. `AI-001/008` and `AIREC-GR-006`, which it cites, are undefined in
this repository — see [B-12](#b-12-unmapped-dec-items--decide).*

- **Two defences, in order of strength.** Instructions go in the **system** message and data in the **user**
  message, so the separation survives a model that ignores fences entirely. Within each, every segment is
  fenced with a **fresh 128-bit token per envelope**, declared in the policy, so content cannot forge a
  boundary without guessing it.
- **Eight segments**, in fixed order regardless of the order a caller supplies them: `POLICY`, `TASK`,
  `OUTPUT_SCHEMA` (system) then `PREFERENCES`, `SOURCE`, `REFERENCES`, `UNTRUSTED_TEXT`, `REMINDER` (user).
  `OUTPUT_SCHEMA` and `REMINDER` are beyond the six 8.7 lists: the first because that prompt's own restriction
  says retrieved text must not redefine the schema, which requires the schema to be a distinguishable part of
  the envelope; the second because instructions placed only at the start lose ground to recency on a long
  input, and a recipe snapshot plus references is a long input.
- **Trust is a function of the segment kind**, not a field a caller sets. If trust were per-segment, promoting
  an injection into the instruction position would be one wrong argument at one call site, and it would look
  reasonable in review. Creator data is `CreatorData`, not `Instruction`: a creator's headnote can carry an
  instruction as easily as an import can.
- **Content is never altered** — no escaping, no stripping of fence-looking lines. recipes.md makes
  creator-entered text canonical, and mangling it to defend a boundary the nonce already defends would trade a
  real guarantee for a cosmetic one. The one hard failure is content carrying the envelope's *own* live token,
  which cannot happen by chance and can happen when a previous envelope is echoed back through retrieval; an
  envelope whose data contains its own fences has no single reading, so the build fails.
- **Every non-instruction segment names the workspace it was read from**, and the build refuses a mismatch.
  That makes "never ground a prompt with another workspace's material" a property of the builder rather than a
  rule reviewers check — a retrieval bug returning a neighbour's row fails at envelope time instead of
  reaching a model. The friction of passing the id per call site is the feature.
- **`Describe()` is what may be logged**: segment names, trust levels and character counts. No content, no
  nonce, no workspace id.

**What this does not promise.** The envelope guarantees structure: untrusted bytes arrive inside an untrusted
fence and nowhere else, unmodified. It cannot guarantee a model declines an instruction it finds in the data.
That is behaviour, and it belongs to the evaluation harness — treating structural separation as behavioural
compliance is the specific mistake to avoid here.

**Options considered:** a JSON-encoded envelope (rejected: escaping makes breakout impossible in principle,
but long recipe prose becomes unreadable escaped JSON that models follow measurably worse) and fixed tags with
no nonce (rejected: untrusted text containing the closing tag forges a boundary, which is the attack in
question, and defending it would require stripping creator content).
**Rules:** `ai.md`, `tenancy.md`.

### B-20 Model execution wrapper

*Recorded 2026-09-25 by microprompt 8.8. `AI-004` through `AI-007`, which it cites, are undefined in this
repository — see [B-12](#b-12-unmapped-dec-items--decide).*

- **The resilience pipeline lives in `CreatorPantry.ServiceDefaults`**, beside the standard HTTP handler, so
  timeouts, backoff and breaker windows are decided in one place. Timeout 90s per attempt, two jittered
  exponential retries, breaker at 50% failure over a 60s window with a 30s break.
- **The transience predicate is supplied by the host.** Whether a failure is worth retrying is a domain
  judgement — a schema failure, a safety block and a malformed request must never be retried — and no predicate
  in ServiceDefaults could tell those from a 503 without naming domain types. Domain may not reference a host
  assembly and ServiceDefaults may not reference the domain, so `Program.cs` hands the predicate in. Domain
  gains a `Microsoft.Extensions.Resilience` package reference for `ResiliencePipelineProvider` only.
- **Classification sits behind `IAiFailureClassifier`**, implemented in `CreatorPantry.AiProvider`, following
  the `IAccountMessageSender` precedent. This matters concretely: a rate limit arrives as the SDK's
  `RequestFailedException`, not `HttpRequestException`, so the domain's shape-based default would call it a
  generic transient fault and would retry a content-filter block as though it were a dropped connection.
- **Classification happens inside the pipeline**, and only failures judged transient are re-raised as
  `AiTransientFailureException` for it to retry. Anything else passes straight out unretried. That ordering is
  what makes "never retry a nontransient failure blindly" true rather than aspirational.
- **Two retry mechanisms, deliberately distinct.** Transport failures retry inside the pipeline and remain
  *one* attempt record. A **schema** failure is different — the provider answered and the answer was unusable —
  so it gets exactly one corrective re-ask carrying the reason code, recorded as its own attempt with
  `WasSchemaCorrection` set. Provenance would otherwise imply the template alone produced the answer when it
  was the template plus a correction. A **domain** failure is never corrected.
- **One attempt record per provider call**, shaped to `AiExecutionMetadata`. Token counts and estimated cost
  are nullable: a provider that reported no usage did not use zero tokens, and a model with no configured price
  has an unknown cost rather than a free one.
- **Cancellation is recorded, not thrown.** Swallowing an `OperationCanceledException` is usually wrong; here
  the attempt happened and its row must be persisted, which is the reason the wrapper exists.
- **Nothing logs content** — not the envelope, not the response, not a failing value. The envelope is logged
  through `Describe()`.

**Options considered:** a hand-rolled retry loop with a small in-memory breaker (rejected: half-open state,
sampling windows and thread safety are where such code is wrong in ways tests do not reveal) and deferring the
breaker entirely (rejected: a failing provider would be retried once per queued operation).
**Consequences:** `Ai:Cost` configuration is needed before any cost figure appears; without it every estimate
is null, which is the honest default.
**Rules:** `ai.md`, `external.md`.

### B-21 Proposal disposition and atomic acceptance

*Recorded 2026-09-25 by microprompt 8.12. `DATA-RQ-004/005` and `AIREC-GR-007`, which it cites, are undefined in
this repository — see [B-12](#b-12-unmapped-dec-items--decide).*

- **A proposal may only offer a change that can be accepted.** Two allow-lists enforce it, both consulted before
  a proposal is stored rather than at acceptance: `AiDiffFields` names the settable fields per target, and
  `AiChangeApplicability` names the applicable `(kind, target)` pairs. A change outside either is refused with
  `ai.output.not_applicable` and the operation fails before a creator sees anything. The alternative — offering
  everything and refusing at acceptance — means a creator reviews a suggestion, confirms it, and is told no at
  the last moment, which is worse than never being offered it.
- **The applicable set is what the recipe patch contract can express**: `Set` on the recipe's header fields, an
  instruction group's title, or an instruction step's text, note, duration and temperature; `Remove`/`Move` on a
  group or step; `Add`/`Remove` on a tag. Ingredients, equipment and asset links are absent because
  `UpdateRecipeViewModel` has no field for them. Adding an instruction step is absent for a structural reason
  instead: `AiStructuredChange` carries one `AfterValue`, and a step is text plus a note plus a duration plus a
  temperature. A tag is the one child whose whole content is one string.
- **Accepted changes become an ordinary `CanonicalRecipePatch`** and travel the same merge path a creator's own
  edit travels — `RecipeBusiness.MergeAsync`, shared by both. That sharing is the mechanism by which AI output
  cannot skip recipe validation, the archived-recipe refusal or the concurrency guard; a second apply path would
  be a second definition of what a valid recipe is. Changing one instruction step therefore re-emits the whole
  method from the live aggregate, because the patch's instruction list replaces wholesale.
- **The confirmation is the list of accepted change ids**, required for both accept decisions, and `AcceptAll`
  must name every change on the proposal. That is what makes it a confirmation rather than a flag: a client
  naming a different set is looking at something other than this proposal. The body can name no value, field,
  target, recipe, version or workspace — only ids the server itself computed.
- **The disposition is addressed by the request id**, the same id the status `GET` takes. An operation has at most
  one proposal, so the request identifies it unambiguously, and one route segment meaning two identities would be
  a trap. The proposal's own id stays in the reply for correlation.
- **The AI data layer owns an explicit transaction** — `CreateExecutionStrategy().ExecuteAsync` around
  `BeginTransactionAsync`, the pattern `IdempotencyDataLayer` already uses — and Business passes the recipe-facade
  call in as a delegate. Both modules write on the same request-scoped `DbContext`, so the recipe version, the
  per-change dispositions, the feedback row, the terminal status and the audit entry commit together or not at
  all. The apply call runs **first** inside that transaction, because the recipe data layer clears the change
  tracker on a conflict and would otherwise discard dispositions staged before it.
- **Replay compares the decision, not a key.** The same decision on an already-decided proposal answers without
  writing; a *different* decision is `ai.proposal.conflict`. This outlives an idempotency record, and it is why
  the route needs no `Idempotency-Key`. A replay reports no version number, because it wrote none.
- **A stale source cannot be accepted, but can be rejected.** The pinned version id is checked inside the recipe
  module at the point of writing; rejecting reaches no recipe, so there is nothing to apply to the wrong thing.
- **`Contributor` is checked at the edge, in the AI facade, and again in the recipe facade.** The recipe check
  only runs when something is accepted, so without one on the AI facade a non-HTTP caller could close a proposal
  against a workspace's content with no role at all.
- **Value bounds are the recipe module's, not the AI module's** — `ProposedRecipeValues`, called from the diff
  calculator so an unusable value is never offered and from the apply path so a row written before the check
  existed refuses rather than reaching the database. Added after an audit of this seam: the apply path runs no
  ViewModel validator, so length, range and format rules that only lived in `UpdateRecipeViewModelValidator` were
  unenforced for accepted changes, and an over-long value arrived as a truncation error the recipe data layer
  correctly declines to call a conflict and rethrows — a 500 with the creator's decision lost.
- **`sourceUrl` is not a field a proposal may set.** A model cannot know where a recipe came from, so proposing a
  source is inventing one; and the scheme check that keeps a `javascript:` or `data:` value out of a field later
  rendered as a link lived only in the edge validator. Offering the field put a stored-link vector behind a diff a
  creator would plausibly accept. Same audit.
- **A step temperature can only be changed on a step that already records a unit.** All three temperature columns
  move together or a check constraint refuses the row — "bake at 180 g" is a food-safety-adjacent fact recorded
  wrongly — and a change row has nowhere to carry a unit. Clearing a temperature takes the unit with it.
- **An inexpressible stored change refuses rather than throws.** A row whose kind or target the recipe seam cannot
  express should be prevented by `AiChangeApplicability`, but one written before that gate must produce
  `ai.selection.invalid_request` rather than a 500 inside the transaction. The tracker is also cleared if the apply
  callback throws, so "nothing was written" is a property of the method rather than of the host.
- **The audit summary counts accepted changes that carried a safety caution.** Still counts, never content:
  without it, "accepted two changes" reads identically whether or not what the creator took came with a
  preservation, temperature or allergen caution, which is the one question worth asking about this write later.

**Options considered:** letting the AI module build the patch itself (rejected: reaching an instruction step means
re-emitting the whole method from the aggregate, which the AI module neither has nor should assemble); a shared
change type in the kernel (rejected: the kernel may not depend on a module, and a recipe field change is
recipe-specific — so the vocabulary is restated as `ProposedRecipeChange` and translated at the boundary);
Business owning the transaction (rejected: `backend.md` puts transaction boundaries in the DataLayer).
**Consequences:** a creator cannot yet accept an ingredient change or an added step, and no proposal offers one.
`AiOperationScope.Ingredients` currently permits nothing at all. The entries to restore are named in
`AiDiffFields` and `AiChangeApplicability`.

**Superseded in part, the same day.** Work landing alongside this added
`UpdateRecipeViewModel.IngredientGroups`, so the capability gap that justified excluding ingredients is closed on
the recipe side. The AI side has not caught up: restoring the ingredient entries needs `AiDiffFields`,
`AiChangeApplicability`, `ProposedRecipeTarget` and an ingredient draft list in `RecipeProposalApplication`, plus
tests. Until then the applicability table is what keeps a proposal from offering an ingredient change it cannot
apply, and that is the correct state to be in — but it is now a lag rather than a limit. `measurementUnitId` and
`ingredientId` stay excluded permanently regardless, because a model naming a `Guid` is a model inventing one.
Adding an instruction step remains blocked on `AiStructuredChange` being able to carry a child payload.
**Rules:** `ai.md`, `recipes.md`, `backend.md`, `api-contract.md`.

### B-22 Platform audit trail

*Recorded 2026-09-29 by microprompt 9A.9, which had to audit an action taken outside any workspace.*

- `AuditLog` is `IWorkspaceOwned`: its `WorkspaceId` is required, carries a **cascading** foreign key to
  `Workspace`, leads both of its indexes, and is stamped by `WorkspaceOwnershipInterceptor` from the resolved
  request scope. An ops route resolves no workspace, so there is nothing to stamp — and a sentinel workspace
  id would leave the operator's audit trail one workspace deletion away from being erased.
- Platform-level actions therefore go to a separate **`PlatformAuditLog`**: no `WorkspaceId`, no query
  filter, `IImmutableRecord`, and no foreign key to `Workspace`, `ApplicationUser` or `OpsApiClient` — an
  audit row must outlive the credential that acted and the account it acted on.
- It carries `ActorType`/`ActorId`/`ActorName`, an action code, `SubjectType`/`SubjectId`, a **required**
  `Reason`, `BeforeReference`/`AfterReference` as compact state pointers, a correlation id and a timestamp.
- Reading it with no workspace filter is safe for the same reason `AccountAiUsageEntry` is: the row holds
  identifiers, action codes, an operator's own words and state pointers, and **never creator content**.
  `PlatformAuditLogModelShapeTests` pins that, so adding a free-text column has to argue with a test first.
- `IPlatformAuditWriter` stages onto the ambient `DbContext` exactly as `IAuditWriter` does, so the audit row
  and the change it records commit in one `SaveChangesAsync` or neither does.

**Options considered:** making `AuditLog.WorkspaceId` nullable was rejected — it would turn a filtered table
partly unfiltered and change the meaning of every existing audit query.
**Rules:** `auth.md`, `tenancy.md`.

### B-23 Brand-guide proposal acceptance

*Recorded 2026-10-02 by microprompt 11A.18. `AIREC-007` and `AIREC-GR-002/004/007`, which it cites, are
undefined in this repository — see [B-12](#b-12-unmapped-dec-items--decide); they are read as 8.12 and 9.5
used them: staleness against a pinned source, server-side scope enforcement, and explicit confirmation before
generated content replaces creator-owned content.*

- **11A.17's rows now have exactly one write path, and it reaches a draft guide version.**
  `AiChangeTargetKind.BrandGuideSection` stays absent from `AiChangeApplicability` and still answers `null` in
  `AiChangeTargetPolicy`, so there is still **no path from one of these rows to a recipe**; what 11A.18 adds is
  `AiBrandGuideAcceptanceBusiness`, which writes accepted items through `IBrandStyleGuideFacade` as one new
  version. The remarks on both of those types were amended rather than left to be read as still true.
- **11A.15 (edit a guide) was never implemented**, so no "write a further guide version" path existed to reuse.
  Acceptance introduced `CreateVersionFromProposalAsync` — proposal-only, no view model, not exposed over HTTP —
  deliberately narrow so the creator-typed edit can land beside it rather than inherit a shape bent around AI.
- **Selection is by item, not by row.** The handler flattens each piece of guidance into one `Add` row carrying
  its text plus `Set` rows for its dimension, channel, evidence and citations; a creator names text rows and
  Business expands the selection onto their companions. Naming a companion alone is refused. Without the
  expansion the stored record would say a section was taken and its own dimension declined.
- **The new version is the working version with the accepted guidance laid over it** (`BrandStyleGuideVersionMerge`):
  sections replace by `(SectionKey, ChannelKey)`, rules append and deduplicate the way the create validator
  matches them, citations union. Anything the proposal did not speak to travels through untouched — accepting a
  suggestion is not starting the guide again. Accepted guidance identical to what the guide already said writes
  **no version at all**, the same no-op rule a creator's own edit follows.
- **Two kinds of staleness, two different answers.** A guide edited since the proposal was composed is refused
  with `brand.guide.workingVersion.conflict`, checked inside the transaction: laying guidance over a version
  nobody compared it with is a rebase, and branching from the older one would discard the edit in between. A
  cited source document replaced since is **not** refused — the citation is written at the version the proposal
  read, never re-pointed, and `staleSourceCount` reports it, which is what activation already gates on.
- **Replay is the operation's terminal status**, re-read inside the transaction, as for an accepted draft; a
  replay that carried rewrites is refused because nothing stores what the creator's words were. Unlike
  `AcceptDraftAsync`, no id is stamped on the operation and a replay names no version: the caller named the
  guide in the request that started the proposal, so a column coupling `AiOperation` to the brand module would
  buy nothing.
- **A citation names a passage; provenance names a document version.** `IBrandSourcePassageFacade.ResolveOriginsAsync`
  maps between them — in the brand module, because the chunk tables are its own — and it reads superseded chunk
  sets too, or pinning the version a passage was read from would be pointless.
- **A request view model may not strip to the same OpenAPI schema id as its reply.**
  `OpenApiDocumentation.StripLayerSuffix` drops both suffixes, so `AiBrandGuideAcceptanceViewModel` beside
  `AiBrandGuideAcceptanceServiceModel` published the reply's shape as the request body. The request is named
  `AcceptBrandGuideProposalViewModel` for that reason. **The same collision exists today on `AiDraftAcceptance`**
  (8.12/8.14) and is not fixed here: correcting it changes a shipped contract document and belongs to its own
  change.

**Options considered:** refusing acceptance when a cited document had been replaced (rejected: the guidance the
creator read is unchanged and the pinned citation is the honest record, so the refusal would obstruct without
protecting anything); recording the produced guide version on `AiOperation` so a replay could name it (rejected:
a cross-module column and a migration for a value one read away); and generalising the three
`IAiOperationDataLayer` decision methods onto one helper (deferred: rewriting two tested seams inside a change
about a third, against the atomicity rule in `CLAUDE.md` — only the identical `StampDispositions` loop was
extracted, and a fourth decision seam should do the rest).
**Rules:** `ai.md`, `content.md`, `tenancy.md`, `backend.md`, `api-contract.md`.

### B-24 Deterministic brand context for generation

*Recorded 2026-10-02 by microprompt 11A.19, which implements the `BrandContextPackage` half of
[B-11](#b-11-brand-profile-and-context-dec-010). `AI-001/002/008`, which it cites, are undefined in this
repository — see [B-12](#b-12-unmapped-dec-items--decide).*

- **The assembler lives in the AI module and reads only through the brand module's facades** — profile, active
  guide, source library, indexed passages. It is a service in `Managers/` rather than a facade because its
  callers are task handlers, which already inject facades directly and are not controllers.
- **The workspace is never an input.** `BrandContextRequest` has no field for one; the workspace reaches every
  read through the resolved context. The restriction matters more here than on an HTTP contract because this
  seam runs in the worker, where no route policy stands in the way.
- **Precedence is per field, not per source:** request override, then the guide's channel variant for the
  channel being written for, then a general guide section, then the brand profile, then nothing — recorded as an
  omission. A channel variant silent on audience does not shadow the profile's default, which a per-source rule
  would get wrong. Each resolved value records which level won.
- **Relevance is a table, not a default of "everything".** `BrandContextSelection.SectionKeysFor` maps a task to
  its section keys. `IngredientSubstitution`, `RecipeReview`, `RecipeRevision` and `RecipeAdaptation` get
  **nothing at all** — not the guidance, not the rules, not the profile, not a guide id — because style rules may
  not alter canonical recipe facts or safety, and an early return is the only form of that guarantee a later edit
  cannot undo by populating one more field. `BrandGuideProposal` is excluded as circular, and `UserNotes` is
  excluded from every task because it is the creator's notes to themselves.
- **Excerpts are bounded two ways.** A named list is capped at `BrandContextMaxSourceDocuments` and refused if
  longer, never trimmed; an unnamed one is ranked on facts the library already stores — purpose, then channel,
  then audience, ties on recency then id for a total order — and capped lower at
  `BrandContextMaxSelectedSourceDocuments`, because a guess the server makes should spend less than an
  instruction. Candidates come from one page of the library, which keeps the read bounded.
- **That ranking is the brand module's, not the AI module's**, and the boundary rule is what settled it: the
  facts a document is ranked on are the library's, and an assembler building the query would have had to name
  `BrandSourceDocumentListViewModel` across a module boundary — a module's view models never cross, and
  `ModuleBoundaryTests` caught it. `IBrandSourceDocumentFacade.ListGroundingCandidatesAsync` now owns the rank
  and returns exact document versions; the AI module supplies only the budget, meaning which purposes are usable
  and how many documents a guess may spend.
- **A conflict is only ever checkable.** Channel not a brand default, audience overriding the profile, a
  selection that is not the active version, an unapproved selection, a guide holding variants for other channels
  only, and a named document tagged for another channel or audience. **Semantic contradiction between two pieces
  of guidance is deliberately absent**: nothing here reads prose for meaning, and a package claiming to have
  noticed one would assert a judgement deterministic code cannot make. A conflict never stops an assembly.
- **No fallback, in the restriction's own words.** A selection that does not resolve refuses with
  `ai.brandContextGuide.not_found`; it never becomes the active guide. A workspace with no activation gets no
  guide and an omission, which is a legitimate outcome rather than an error — and an approved-but-never-activated
  guide is still no active guide.
- **The token figure is an estimate and is labelled one everywhere.** Four characters per token, rounded up. The
  domain holds no tokenizer and should not: a real count depends on the provider's vocabulary, which would make
  a package's content vary by deployment, and the provider's exact count already arrives afterwards through
  `AccountAiUsageEntry`. The budget is therefore deliberately generous.
- **The budget drops whole items in priority order** — profile and rules, then guidance, then excerpts — and
  records each drop as an omission. Nothing is truncated: half an excerpt is not a citable passage and half a
  section is advice with its qualification cut off. Rules and profile facts are never dropped; a guide whose
  do-and-don't list cannot fit is a product problem to surface, not one to paper over.
- **"No profile field switches a platform safety rule, warning or check on or off" is enforced by shape.** The
  package holds only text, keys, ids, counts and timestamps — one boolean, which is the server's own statement
  about which version it used. `BrandContextPackageShapeTests` asserts the property list and refuses names like
  `allow`, `threshold`, `policy` or `provider`, so widening it has to argue with a test first.
- **The checksum covers what was pinned and selected, and nothing else:** guide version, profile revision, task,
  channel, audience, every section body, every rule, every `(document, version, passage)` with its text. Not the
  assembly time, the estimate, the conflicts or the omissions — those are consequences of the inputs, and
  including them would move the checksum when only the reporting changed. Lengths precede values so creator text
  cannot forge the next field, and it is taken **after** the budget so it names what the package actually carries.
- **One new brand read was needed:** `IBrandStyleGuideFacade.GetActiveAsync`, keyed on the workspace rather than
  a guide. `GetAsync` answers "this guide, and its active version if the default happens to be its own", which a
  caller that does not already know which guide holds the default cannot use.

**Options considered:** putting the assembler in the brand module (rejected: the package's shape — budget,
checksum, task relevance — is prompt construction, and the prompt's own wording has it reading the profile
*through* the brand facade); refusing an unapproved explicit selection as activation does (rejected: the caller
named that exact version, activation already guarantees the active one is approved, so the only route here is a
deliberate choice the package discloses); and a `SourceVersionSuperseded` conflict (removed as unreachable — the
assembler pins each document's current version itself).
**Consequences:** nothing consumes a package yet. 11A.20 wires handlers one at a time and is what records the
checksum and provenance against a generation; the assembler is registered in the Worker so the container proves
the graph resolves now rather than then.
**Rules:** `ai.md`, `content.md`, `tenancy.md`, `backend.md`.

### B-25 Brand context recorded against a generation

*Recorded 2026-10-02 by microprompt 11A.20, which consumes the [B-24](#b-24-deterministic-brand-context-for-generation)
assembler from the writing task handlers. `RCPUB-001/002`, `SOC-001` and `AI-001/002`, which it cites, are
undefined in this repository — see [B-12](#b-12-unmapped-dec-items--decide). This entry records the groundwork
decisions; the per-handler wiring follows one handler at a time, as the prompt's own restriction requires.*

- **The set of handlers is the relevance table's, not a judgement call.** `BrandContextSelection.SectionKeysFor`
  already returns empty for everything but `EditorialPackage`, `SeoPackage`, `RecipeConcepts` and
  `RecipeFirstDraft`, so those four are the ones wired. The prompt also names *social*, which has no task type
  yet: the social writing handler inherits this wiring when it is built.
- **Provenance is a 1:1 child table, not columns on `AiProposal`.** `AiProposalBrandContexts` holds one row per
  grounded proposal and `AiProposalBrandSources` one row per cited passage. Seven of eleven task types ground in
  nothing, so columns on the proposal would be null for most rows, and the cited passages need rows regardless.
  A JSON column was the third option and was rejected for making "every generation grounded on guide version 3"
  a scan.
- **A row exists whenever context was asked for, even when nothing came back; its absence means nothing was
  asked.** A workspace with no profile and no active guide is what later explains a piece that reads in no
  particular voice, and recording nothing there would be indistinguishable from a creator who turned brand voice
  off.
- **A stale guide warns; it does not refuse.** The creator pinned that version deliberately, and a writing task
  only reads a guide — unlike `BrandGuideProposal`, which writes one and therefore refuses when its pinned
  version moves. The version used is recorded and `BrandContextNotices` says out loud that it is no longer
  active, which together are what satisfy "do not silently select stale guide". An unapproved version is a second
  notice rather than a refusal, matching B-24's reasoning that the caller named that exact version.
- **No writing request accepts a channel.** `ContentChannelCatalog` holds social channels only, and a blog
  package, SEO metadata, a recipe concept and a first draft are none of them, so a channel here could only pull a
  social variant over long-form guidance. `BrandContextRequestSelection.ToRequest` passes null and says why; the
  social task is the one that will set it.
- **The request pins references, not values.** The seam this replaces copied brand name, audience and locale into
  `TaskInputsJson` at request time. A guide id with a version number is pinned instead, because a guide version is
  immutable content the assembler re-reads identically, while copying everything a package now carries into a
  dictionary with a 12,000-character ceiling is not a thing to attempt. The consequence: an operation queued
  before this seam reads back as "brand voice on, nothing pinned" — the default a creator who never saw the
  control would have had — and nothing re-interprets an old request as an opt-out.
- **One renderer turns a package into prompt text**, as two fenced reference segments. That is how "do not copy
  guide text into scattered prompt files" is held rather than asserted. It renders no instruction of its own: an
  instruction inside a data segment is indistinguishable from one a source document forged there, so the standing
  rule that style governs wording and never recipe facts belongs in each capability's task template — which is
  the segment the model is told to obey, and which each handler turn amends.
- **No identifier, version number or checksum reaches the prompt.** A writing task does not cite its brand
  context — unlike a guide proposal, which must, and which therefore does pass passage ids — so an id would be
  data the answer cannot legitimately use and could only echo back as content.
- **No guide display name is stored or published.** Resolving the guide through the brand routes gives a caller
  its current name; a name copied in at generation time would make the AI module a second place a brand's names
  live, and would go stale on the first rename.
- **No foreign key from these rows to `Workspaces`, `BrandStyleGuides` or `BrandSourceDocuments`.** The reason is
  the one `AiProposalConfiguration` already records: the proposal cascades from `Workspaces` through its operation
  and a guide does too, so a second edge leaves SQL Server with two cascade paths into the table and it refuses
  the DDL. `Restrict` was the alternative and is worse — it would make archiving a style guide fail once any
  generation had been grounded on it. The consequence, stated rather than discovered: these ids can outlive what
  they name, and a corrected extraction rewrites a passage under the same id, which is why the checksum and not
  the row is what proves which words were used.
- **The excerpt count is rows, not a column.** `GuidanceSectionCount` and `RuleCount` exist because those bodies
  are deliberately not stored; a third count beside the source rows would be a second number for one fact.

**Options considered:** nullable columns on `AiProposal` and a single JSON column (both above); refusing a
superseded guide version (above); and storing the guide's display name for the review UI (rejected above).
**Consequences:** the four handlers are wired one per turn after this, each amending its own prompt template and
dropping the brand keys its request seam pinned by value. The published `AiProposalDetailServiceModel` gained a
nullable `brandContext` block — additive, and the OpenAPI snapshot was regenerated in this change. The Angular
`AiProposalDetail` model is deliberately untouched: its decoder ignores fields it does not read, and the guide
control that will surface this is 11A.21a.
**Rules:** `ai.md`, `content.md`, `tenancy.md`, `backend.md`, `api-contract.md`.

### B-26 A read-only comparison is two provider calls

*Recorded 2026-10-04 by microprompt 11A.24, the read-only "Test my style" test drive. It is the first consumer
of [B-24](#b-24-deterministic-brand-context-for-generation)'s assembler in a generation path, and the first to
populate [B-25](#b-25-brand-context-recorded-against-a-generation)'s provenance rows.*

- **Two provider calls for one operation, and that is the capability.** One call writes the three samples with
  no brand context at all; the second writes them from the guide version the creator named. A single call
  holding the guide while writing both halves would produce a column labelled "without your guide" that was
  written with it — the one claim this screen must not make. The calls differ in exactly one respect: the second
  carries the package's `PREFERENCES` and `REFERENCES` segments. Same template, same output schema, same
  validator, same subject. `AiTaskHandlerOutcome.Attempts` was already a list and `AiExecutionMetadata` already
  one row per attempt, so nothing was invented to hold the second call; the lease is renewed between them.
- **The consequence nobody can configure away: a test drive costs twice.** `Ai:Quota:TaskEstimates` gains a
  `BrandStyleTestDrive` entry at twice the default per-call estimate, in **both** hosts — the API refuses at the
  edge and the worker takes the hold, and the two must agree about what one costs. Leaving it to
  `DefaultTaskEstimate` would have under-reserved every run by half, which settlement would have recorded
  honestly and admission would have let through.
- **An empty guide version is refused before anything is spent, twice over.** The request seam asks the
  assembler whether the named version resolves and holds any guidance — assembly makes no provider call, so the
  refusal is free — and the handler checks again in case the guide was emptied in between. Two identical columns
  are not a comparison, and a creator should not pay to discover that.
- **The version is required and never defaulted.** Every other brand-grounded seam falls back to the workspace's
  active version; this one cannot, because trying a draft out before activating it is the point. A request that
  silently tested the active guide would answer a question the creator did not ask.
- **Which rules applied is the server's answer, never the model's.** The output document has no field for an
  attribution, so the model has nowhere to claim one: a model-supplied "I followed your voice rule" is exactly
  the unfalsifiable "this sounds more like you" the prompt's RESTRICTION names. The screen lists what
  `BrandContextSelection` selected, in the creator's own words, and the passages the package carried.
- **Those rule labels are re-derived on the read rather than stored, and the trade is published.** A guide
  version's sections are immutable — `BrandStyleGuideSection` is an `IImmutableRecord` — so the wording named
  beside the samples *is* the wording that produced them. What can still move is the brand profile and the
  example library, so the read compares a freshly assembled package's checksum with the one recorded against the
  proposal and publishes `groundingChangedSince`. A child table of applied section keys was the alternative, and
  was rejected as a migration bought to remove a drift the immutability of a version already removes.
- **Both halves or neither.** A second call that fails fails the whole operation, with both attempts recorded.
  A stored one-sided result would be rendered as a comparison, and a creator would read a column labelled "with
  your guide" that was never written.
- **One package, unioning the voice and the look.** `BrandStyleTestDrive` is the only entry in the relevance
  table that unions a writing list with the visual one, because it writes a blog introduction, a caption and an
  image prompt in one pass. Two packages would mean two checksums, two provenance rows and two conflict lists
  for one thing a creator asked for once — and either would have recorded a task type it was not assembled for.
  It passes **no channel**, for the reason B-25 gives: one package grounds the introduction and the caption
  together, so a channel key could only pull a social variant over the long-form guidance. The caption therefore
  demonstrates the guide's general social guidance, and the screen says so.
- **The samples are stored as two change-target kinds, not as a before and an after.**
  `BrandStyleSampleWithoutGuide` and `BrandStyleSampleWithGuide` carry three `Set` rows each, with the sample
  name in `FieldName` and `BeforeValue` null throughout. Reading the plain column as the "before" of the guided
  one was the tempting shape and is wrong: that column is documented, and asserted by `AiProposalAssembler`, as
  a value the server read from a pinned source and never one the model supplied. Both kinds are absent from
  `AiChangeApplicability` and answer null in `AiChangeTargetPolicy`, and there is no acceptance route at all —
  unlike `BrandGuideSection`, which has one.
- **The subject is a declared field capped at 120 characters, and reaches both calls unchanged.** It exists so a
  creator sees their voice on their own food rather than on the platform's example; a field long enough to hold
  instructions would be a way to steer a generation the request has no other way to steer. A comparison whose
  subject moved between the calls would be measuring the subject.
- **Hashtags are allowed in the caption and nowhere else; handles nowhere.** A hashtag names nothing and is part
  of how a caption reads, so refusing it would make the social sample a poor demonstration of a social voice. A
  handle names a real account, which is the unverified claim the editorial and SEO validators refuse links for.
- **No migration.** Neither `AiOperations.TaskType` nor `AiStructuredChanges.TargetKind` has an enumerated check
  constraint, and B-25's provenance tables already exist.

**Options considered:** one call returning both halves (rejected above); three samples as three calls (rejected
— the cost triples for no gain, since one call writing three short pieces from one subject is the same
comparison); a `BeforeValue` holding the plain column (rejected above); a stored table of applied section keys
(rejected above); and a fixed-only subject with no creator field (rejected as a worse demonstration, with the
cap and the shared-subject rule as the mitigation).
**Consequences:** `AiTaskType`, `AiChangeTargetKind`, the TypeScript mirrors of both, the OpenAPI snapshot and
`AddAiBrandContext` in the API host all changed additively; the API now registers the assembler as the worker
already did. The capability ships dark like every other and is enabled in development only. The screen is
reachable from every row of a guide's version history, including a draft, and from the setup wizard once a guide
exists.
**Rules:** `ai.md`, `content.md`, `tenancy.md`, `backend.md`, `api-contract.md`, `frontend.md`.

### B-27 Publishing ledger shape and retention

*Recorded 2026-10-13 by microprompt 13.10. `DATA-005/006`, which it cites, are undefined in this
repository — see [B-12](#b-12-unmapped-dec-items--decide).*

```text
PublishingRecord                        root, workspace-owned
├── ExternalPublicationReference 0..1   the provider's id for the post, and the only place it exists
└── PublishingDeliveryAttempt    0..n   append-only, one per call to the provider
```

- **Three tables, so the ledger row holds no provider identifier.** B-10's `Publication`,
  `ExternalPublicationReference` and `DeliveryAttempt`, with `PublishingProfile` (B-10's
  `PublishingTarget`) already in place from 13.9. `PublicationRevision` is not among them: an edit after
  confirmation is a new confirmation and a new row, which is a simpler way to keep the queued payload
  unmutated than versioning one.
- **The payload is a snapshot written once.** Content text, picture version, profile, platform and schedule
  are what the creator confirmed; only the lifecycle columns move afterwards. `Platform` is copied rather than
  read through the profile, because a profile row is a cache of what the provider last said and every
  successful read rewrites it.
- **A schedule is three facts:** the UTC instant, the IANA zone the creator chose (required, read from
  `BrandProfile.TimeZoneId`, never guessed), and the local wall-clock time they asked for.
- **`NeedsAttention` is a real state, not a tidy-up.** An outcome nobody knows is never recorded as success or
  failure: `UncertainOperation` and `UncertainSince` say which write is in doubt and since when, and a
  constraint refuses a record in that state with no reason attached. Only a write can be uncertain —
  reconciliation is a read, so repeating it settles and blocks nothing.
- **The ledger carries its own idempotency key, unique per workspace.** The shared `IdempotencyRecord` replays
  a result for 24 hours and then expires; a confirmation replayed after that must still not publish twice, so
  the durable guard is this index rather than that table.
- **Retention: a record is never deleted to tidy up.** It is the only record that content left for a third
  party. Rows leave by the workspace's cascade or a documented erasure path. Attempt rows are bounded by the
  submit and cancel caps and the reconciliation window rather than pruned, so no sweeper owns them, and nothing
  in the aggregate stores a request body, a response body, a header or a credential.
- **`ContentCardId` is nullable with a restricted key.** 13.6c decided that a delivered or failed publication
  does not block deleting its card, so the delete detaches terminal rows and refuses while one is live. A
  set-null in the store is not available: SQL Server allows one action path from `Workspaces` into this table,
  and the workspace's own cascade is it.
- **The claim index is not led by `WorkspaceId`**, which is tenancy.md's queue-claim carve-out. This phase adds
  the index only — nothing claims yet, so there is no `IgnoreQueryFilters` path and no
  `BulkOperationBoundaryTests` exemption to grant until 13.10c. When that claim arrives it carries the
  carve-out's two conditions: it selects identifiers only, never content, and it resolves the workspace through
  the ordinary tenancy path before reading anything else. Every other read of these tables is workspace-led,
  including a lookup by provider post id, which goes through
  `(WorkspaceId, ProviderKey, ExternalUpdateId)` — the index exists in that order for that reason.

**Options considered:** one table with the provider id on it (rejected by publishing.md's restriction); a
`PublicationRevision` chain (rejected above); attempt counters with no per-call rows (rejected — "refused twice
and then timed out" would only be answerable from logs); deferring the lease columns to 13.10c (rejected — the
filtered claim index needs `AvailableAt` to be worth adding, and the shape is the one
`GeneratedImageOperation` already proved); an automation-policy column (rejected as a column that could only be
null until a policy exists).
**Consequences:** `PublishingRecords` holds `ContentText` as `nvarchar(max)` because the ceiling it shares with
`ContentPolicy.SocialBodyMaxLength` is above 4 000, so the length is an application limit rather than a column
one — a test asserts the two numbers still agree. Nothing creates a row yet: 13.10a is the first writer, and it
also owns wiring the card-delete check that 13.6c left a comment for.
**Rules:** `publishing.md`, `external.md`, `tenancy.md`, `content.md`.

### B-28 Scheduling a publication, and what a provider's silence means

*Recorded 2026-10-13 by microprompt 13.10a. `INT-031` through `INT-033`, which it cites, are undefined in
this repository — see [B-12](#b-12-unmapped-dec-items--decide).*

```text
validate ──► commit Pending + audit ──► commit Submitting + uncertainty ──► createPost ──► commit the outcome
             (transaction 1)             (transaction 2)                     (nothing open)  (transaction 3)
```

- **No transaction spans the provider call**, which is the shape the whole decision follows from. A commit held
  open around `createPost` would hold a database write for as long as a stranger's server takes to answer, and
  a rollback after a successful call would erase the only record that a post exists.
- **The row is marked `Submitting` with `UncertainOperation = Submit` *before* the call**, not after it. That is
  what makes a process dying mid-call recoverable: the ledger then says an outcome is unknown, rather than
  claiming nothing was sent.
- **Exactly one attempt, and no retry anywhere.** Buffer's API has no idempotency key and no client mutation id
  (verified against developers.buffer.com), so nothing can tell a repeated `createPost` from a second post
  somebody wanted — not the gateway, not the HTTP handler (the host already removes the resilience handler for
  this reason), and not the caller.
- **The ledger's unique `(WorkspaceId, IdempotencyKey)` index is the replay guard**, not
  `IIdempotentCommandExecutor`, whose contract is one short transaction committing with the stored result and
  cannot hold a provider call. A replay returns the first record with `Idempotent-Replayed: true`; a key reused
  with a different payload — or by a different member — is `idempotency.key_reused`. The shared record expires
  after a day and this index does not, which is the point: a retry on day three must still not publish twice.
- **An unknown outcome stays unknown.** `Unknown` from the gateway leaves the record `Submitting` with its
  uncertainty intact and writes an attempt row with no completion time. A replay of such a record never calls
  the provider again. Reconciliation will have to match a possibly-created post on channel, `dueAt` and text,
  because Buffer returns no correlation id of ours — which is why the payload snapshot is on the row (13.10c).
- **Only a *provable* non-delivery may be retried, and Buffer can prove exactly one.** A throttle is the one
  failure its documentation describes as happening instead of acting. A 5xx, an `UNEXPECTED`, a dropped
  connection, a timeout, a payload member this server was not written against, and an unreadable answer are
  all `Unknown` — none of them establishes that the mutation did not run, and its error guide actually
  *advises* retrying `UNEXPECTED`, which for `createPost` is the duplicate-post path. `Unavailable` therefore
  exists on the gateway contract but is never returned by the Buffer adapter.
- **A success is read before the complaints beside it.** Buffer documents that a successful mutation can
  answer with an `errors` array, so the reply carries its `data` whatever else came with it: a post that was
  made and warned about must not be recorded as a post that was never made.
- **One claim on a record, and only the call that made it may talk to the provider.** Taking a row from
  `Pending` to `Submitting` reports whether *this* call performed the transition; a row another request or a
  worker already claimed is returned untouched and nothing is sent.
- **A second key does not buy a second post.** The key's unique index cannot see two keys for one publication,
  so a confirmation that matches a *live* record's card, profile, text and time is refused with
  `publishing.duplicate.conflict` and the existing record's id. A failed or cancelled publication may be
  confirmed again.
- **A caller that gives up mid-call still leaves the attempt behind**, written with an uncancellable token:
  the trail that matters most is the one for a call whose end nobody saw. The outcome write after a successful
  provider call is likewise not bound to the client's token.
- **A provider's refusal is a state, not a failed command.** The route answers `201` with the record whenever
  the confirmation was committed; `status` and `outcomeUncertain` carry what became of it. Only a refusal the
  caller can fix is a `ProblemDetails`.
- **A provider's error message never crosses the gateway.** Buffer writes it and it can quote the post — the
  creator's own words — back, so what crosses is the error's GraphQL *type name*, mapped to one of this
  server's own categories, plus a sentence written here.
- **A picture cannot be published yet, and that is said rather than worked around.** `createPost` takes
  `assets[].image.url` and the documentation requires a publicly fetchable address; `IPrivateObjectStore`
  deliberately returns no addresses at all (media.md), and no anonymous media route exists. A confirmation
  naming a picture is refused with `publishing.media.not_publishable.unprocessable` after its ownership is
  checked, so a creator is never told a picture went out when only text did.
- **Confirming takes the Editor role**; reading a record takes the lowest. The route is
  `/publishing/records`, beside 13.9's `/publishing/profiles`.
- **The scheduling zone is the workspace's own**, read from `BrandProfile.TimeZoneId` and never from the
  request; unset refuses rather than guesses. A schedule is stored as three facts: the instant, the zone, and
  the local wall-clock time it means.

**Options considered:** `202` with the provider call left to a worker (rejected — this prompt's scope is the
command calling the adapter, and a creator confirming a post should learn in that request whether it was
taken); wrapping the command in the shared idempotent executor (rejected above); retrying a timed-out submit
(rejected — it is the one call that must never be repeated blind); storing Buffer's error message for the
creator to read (rejected above, with a category and our own sentence as the mitigation); adding a signed-URL
capability to the object store so pictures could be published now (rejected as a reversal of a documented
decision, out of this prompt's scope, and unreachable from the provider in local development anyway).
**Consequences:** `BufferGraphQl` was extracted from 13.9's profile gateway so both gateways classify one
answer the same way; the profile gateway keeps its own retry loop, because only a caller knows whether
repeating what it asked for is safe. A store failure on the outcome write is caught and logged by exception
type alone, because a unique-index violation's message quotes the key — and that key is the provider's id for
the post. A record left `Pending` by a throttle, or `Submitting` by a silence, is
not submitted again by anything until the reconciliation worker exists (13.10c); the response reports its real
state meanwhile. `MaxSubmitAttempts` is unreachable from this route, since an interactive confirmation can
only ever make attempt number one.
**Rules:** `publishing.md`, `external.md`, `api-contract.md`, `tenancy.md`, `media.md`.

### B-29 Cancelling a publication

*Recorded 2026-10-13 by microprompt 13.10b. `INT-032/033`, which it cites, are undefined in this repository —
see [B-12](#b-12-unmapped-dec-items--decide).*

- **The decision is driven by whether we hold the provider's id for a post, not by the status alone.** No
  reference and a certain outcome means nothing was created, so the publication is stopped here with nothing
  asked of anyone. A reference means there is something to remove. No reference and an *unknown* submit means
  there may be a post this server cannot name — the one case that is refused.
- **An unknown send is never cancelled**, which is the restriction this phase exists to honour. It answers
  `publishing.outcome_unknown.conflict`: there is nothing to ask the provider to remove, and recording a
  cancellation would be a claim about a post that may be going out. Reconciliation settles it (13.10c) and the
  cancellation then works.
- **Only two answers end in `Cancelled`:** the provider removed the post, or it does not have it. A refusal
  puts the record back to `Scheduled`, because that is what is true — the post stands, and the category says
  why the removal did not happen. A throttle or a silence leaves it `Cancelling`.
- **A cancellation may be retried where a submit may not.** Asking twice cannot remove two posts: the second
  ask answers `NOT_FOUND`, which is the outcome that was wanted. So the gateway retries a silence up to three
  times, the transition into `Cancelling` may be re-entered, and a repeated request is the documented remedy
  for an unsettled removal rather than a risk. This is the one place the product's asymmetry with `createPost`
  is deliberate and load-bearing.
- **Repeating it is safe for the caller too**, so there is no idempotency key: the record's id is what makes a
  cancellation converge, and a record already `Cancelled` answers with itself rather than an error.
- **The concurrency token is required.** The race it catches is a post going out between a screen being drawn
  and Cancel being pressed; quoting what was read turns that into a refusal a creator can understand.
- **A published record is refused** (`publishing.already_published.conflict`) because taking something down
  again is a separate capability (publishing.md), and a failed one is refused because nothing is going out.
- **`DeletePostPayload`'s members could not be verified**, so the mutation selects `__typename` and nothing
  else — valid against any union — and classifies on that symbol. Naming a member the union does not have
  would make every cancellation a validation error, which is to say an unknown outcome every time.

**Options considered:** recording a cancel intent for an unknown send on a new column (rejected — it writes a
promise only the unbuilt worker can keep, and widens the phase past PUB-003); moving such a record to
`Cancelling` without asking anyone (rejected — the state would describe a request that was never made); a
single provider attempt as the submit has (rejected above); an optional concurrency token (rejected — the race
it guards is the expensive one); `DELETE` on the record (rejected — it would suggest removing the ledger row,
which never happens).
**Consequences:** `PublishingCancellationWrite` is its own shape, with no external id, because a removal
learns nothing new about where the post is and the mapping record stays as it was — that a post once existed
at the provider is part of the trail. `BufferGraphQl` now carries a documented error code through rather than
flattening it to a label, because `NOT_FOUND` is the answer a removal wants. A stale token going stale is
asserted against SQL Server only: the endpoint host is SQLite, which seeds a row version on insert and never
bumps it.
**Rules:** `publishing.md`, `external.md`, `api-contract.md`.

### B-30 Reconciling what the provider has not accounted for

*Recorded 2026-10-13 by microprompt 13.10c. `DATA-RQ-031`, which it cites, is undefined in this repository —
see [B-12](#b-12-unmapped-dec-items--decide).*

- **The claim is what the ledger's filtered index was built for.** `IX_PublishingRecords_Status_AvailableAt`,
  filtered to `Pending`, `Submitting`, `Scheduled` and `Cancelling`, *is* the set of claimable rows: terminal
  records and ones waiting for a person are not in it, which is what makes `NeedsAttention` a resting place
  rather than a loop.
- **Nothing is concluded from a listing that did not happen.** A throttled or unreadable listing leaves every
  record exactly as it was, with a backoff. Only a listing read to its end may support an absence, and only
  after `AbsenceGrace` — because a provider's own list can lag a post it has just made, and "no post was ever
  made" is the one conclusion that can quietly lose a creator's work.
- **An unacknowledged post becomes `Failed`, and is never resubmitted by anything.** A creator may confirm it
  again deliberately, which the duplicate guard allows because `Failed` is not live. The alternative —
  concluding "not created" and sending again — risks the audience seeing the same post twice, and a stale or
  paged listing is exactly the condition in which that mistake would be made.
- **Only `Pending` is ever submitted, through the request path's own transition gate.** The worker is not a
  second way in: it calls the same `MarkSubmittingAsync`, so two claimants remain impossible and no code path
  submits a record that is already `Submitting`.
- **A cancellation, by contrast, is re-asked freely**, since a repeat cannot remove two posts. That asymmetry
  is why an unsettled removal is retried where an unsettled submit is reconciled.
- **One listing per channel per pass.** Records are grouped by workspace and then by profile, so three records
  on one channel cost one call against a quota that is small and shared. A pass with nothing due makes no call
  at all.
- **Finding a row is not authorization.** The claim returns identifiers and queue state only — never the
  post's text — and a fresh scope then resolves the workspace through `ResolveForServiceAsync`, as the service,
  because this work outlives whoever confirmed the publication. Every read after that runs under the
  workspace's own filter, and a record whose workspace is not the credential's is skipped without a provider
  call.
- **A lapsed lease clears the lease and nothing else.** No status moves and no attempt is spent: nobody knows
  how far the dead worker got, and the record's own state already says what is in doubt.
- **A post that vanished at the provider is `Cancelled`** with `provider_post_absent` — it will not go out,
  which is what cancelled means, and the category records that it was not cancelled from here.

**Options considered:** `NeedsAttention` for every unacknowledged post (rejected — it makes a provider hiccup a
manual chore and leaves the record in a state the board cannot act on); resubmitting after an absence
(rejected above); one listing per record (rejected — twenty calls for an answer three would give, against 250
requests a day); not polling `Scheduled` records at all (rejected — `Published` would then never be reached);
a `dueAt` filter on the listing (not available: the provider returns the field but documents no filter for it,
so matching on time happens here, within a tolerance).
**Consequences:** the three per-operation write types from 13.10a/b are now one `PublishingOutcomeWrite`,
because a reconciliation writes the union of what they write; `BufferPublishingRegistration` moved from the
API host to beside the module, since two hosts register it and must not drift on which handler may retry a
mutation — which brought `Microsoft.Extensions.Http.Resilience` into the domain for that one opt-out. The
AppHost now references `publishing-buffer` from the Worker as well as the API, and `aspire.md` says so.
**Rules:** `publishing.md`, `external.md`, `tenancy.md`, `aspire.md`.

### B-31 Publishing on the card detail page

*Recorded 2026-10-13 by microprompt 13.10d. `BOARD-UI-004/005`, which it cites, are undefined in this
repository — see [B-12](#b-12-unmapped-dec-items--decide).*

- **The page says only what the server has said.** A confirmation and a cancellation each render the record
  that came back, whatever state it is in. Nothing is marked scheduled, published or cancelled optimistically,
  and `Idempotent-Replayed` is reported as a replay rather than as a new publication.
- **An unknown outcome is shown as unknown**, with a warning tone, its own sentence and the only guidance on
  the page that tells a creator *not* to act: scheduling again could publish the same post twice while
  reconciliation is still finding out. It is the one state a screen could do real harm by rounding off.
- **The status is read as an open set.** A value this build has never heard of decodes to `Unrecognised` and is
  shown as such, rather than making the publication undecodable and invisible — a publication in a state we
  cannot name still exists.
- **The schedule form is a drawer**, by DESIGN-SYSTEM.md's own test: the creator needs the card in view, would
  not link to the form, and it is one short form. Cancelling is a `ConfirmService` question, not a fourth
  surface.
- **Times are the workspace's, converted in the browser.** The date and time are read in the scheduling zone
  from the brand profile and converted to the instant the route takes, with `Intl` and no library. A creator
  working from another country schedules the hour their audience sees; a workspace with no zone cannot
  schedule at all and is told why, because the server will not guess one either.
- **A card's picture is refused in the form, before a request is spent**, with the same words the route uses —
  so a creator learns a picture cannot go through the provider yet while writing, not from an error afterwards.
- **The head badge follows the publications, not the card's own counts.** `ContentCard.publishing` is a
  summary the server still does not fill, and a badge drawn from it would say "Not scheduled" over a card with
  a post going out this afternoon.
- **A new read was needed to make any of it survive a reload**: `GET /publishing/records?contentCardId=`,
  capped rather than paged, with `complete` saying whether the cap was reached. An unknown card, another
  workspace's and one with no publications are all an empty list.

**Options considered:** embedding the records in the card detail's existing `publishing` slot (rejected — it
puts delivery data on a Content contract the board's list rows share); keeping the status in page memory only
(rejected — a reload would lose it, which makes "status" and "cancel" unmet in practice); using the browser's
zone with a warning (rejected above); sending the wall-clock time for the server to resolve (rejected — the
confirm route takes an instant, shipped two prompts earlier); offering nothing for an unconfirmed cancellation
(rejected — it leaves a creator watching an unresolved post with no way to act, when asking again is both safe
and how it settles).
**Consequences:** two detail-page specs that asserted the counts-based topic were rewritten to the behaviour
that replaced them. `publishingStatusOf` is no longer used by the page and the head badge now comes from the
topic's own read.
**Rules:** `frontend.md`, `design-system.md`, `api-contract.md`, `publishing.md`.

### B-32 What the publishing audit changed, and what it refused to change

*Recorded 2026-10-13 by microprompt 13.10e, auditing `PUB-001`–`PUB-003` and `INT-030`–`INT-033` across the
Buffer adapter, the ledger, schedule, cancel, reconciliation, the board controls, and workspace scope.*

- **Uncertainty was never resolved for convenience, and three fixes exist because it had been.** A transport
  failure the browser could not map said "nothing was sent"; a listing with no readable `pageInfo` counted as
  read to its end; and a `status`-filtered listing could miss a post the provider had put in a state we did not
  ask about. Each is a path from "we do not know" to "it did not happen" — the one direction that loses a
  creator's post or sends it twice. All three now stop at unknown.
- **Replay is decided before duplication is refused.** A second request on a key already spent was answered as
  a duplicate or as a replay depending on which check ran first, which made the shared-key race
  non-deterministic. `ReplayAsync` is now consulted before every duplicate refusal, so the same request always
  gets the same publication back and a genuinely different one is always refused.
- **The database now holds the rule the code was enforcing.** Two requests could each pass the duplicate
  pre-check and create a live publication for the same card, profile and moment.
  `UX_PublishingRecords_Live_Card_Profile_Time` — unique, filtered to the live statuses — makes the loser a
  refusal rather than a second post, and the transition gate's `DbUpdateConcurrencyException` is caught and
  re-read rather than surfacing as a 500.
- **A check constraint that passes on NULL is not a constraint.** `MediaAssetVersionNumber >= 1` evaluates to
  UNKNOWN for a NULL, which SQL Server accepts; the version could be absent on a record that named an asset.
  The constraint now says `IS NOT NULL` explicitly, with the reason written beside it.
- **A cancelled record answers a repeated cancel with itself, before the token is checked.** Asking twice is
  the documented safe operation and the second ask arrives with a stale token by construction, so a
  concurrency refusal there punished the behaviour the rule asks for.
- **Absence is only provable after the moment the post was due.** The grace period ran from when the record
  became uncertain, so a post scheduled for next week could be declared absent from a listing taken today, when
  a provider has no reason to list it yet. Grace now runs from the later of that and `ScheduledAt`, and only
  for a record the provider has acknowledged and not yet sent — narrower than the first two attempts at this,
  each of which broke a case the suite already covered.
- **No provider text reaches a creator.** `failureSummary` is composed from a closed category set in Business
  rather than passed through, so a provider message cannot carry an account detail, an internal id or a key
  fragment onto the page; the documented code (`NOT_FOUND` and its siblings) is still preserved in the audit
  record, where it belongs.
- **The flaky tests were reported, not fixed.** Three pre-existing AI-usage tests fail under load and pass on
  a clean worktree with these changes' domain code; a `git worktree` run was used to establish that rather
  than inferring it. Quieting them would have hidden a real signal about test-suite timing, and they are not
  this prompt's scope.

**Options considered:** returning `409` for the live-duplicate index violation without the `ReplayAsync`
re-read (rejected — the loser of a race on the *same* key is a replay and must get the publication, not an
error); widening the absence grace for every uncertain record (rejected — it delays the `Failed` conclusion for
the posts that really were never made); mapping an unreadable confirmation body to failure (rejected — a `201`
means it was committed, so the body being unreadable is this client's problem and not evidence of anything).
**Consequences:** migration `AddLivePublicationGuard`; the OpenAPI snapshot grew by 640 lines with nothing
removed, so the contract change is additive; `PublishingErrorCodes` gained `ScheduleConflict` for the raced
case and the board reports it as a race rather than a refusal.
**Rules:** `publishing.md`, `external.md`, `tenancy.md`, `api-contract.md`, `frontend.md`.

### B-33 Evidence sources

*Recorded 2026-10-20 by microprompt 14.1. Extends B-09. Cites AI-009/010, DATA-RQ-007 and DATA-011, whose text
is not in the repository (B-12); their meaning here is taken from Phase 14's prompts.*

| Evidence | Approved source | Data sets | Licence |
|---|---|---|---|
| Nutrition | USDA FoodData Central | Foundation, SR Legacy | CC0 1.0 (public domain) |
| Density | USDA FoodData Central (household portions) | Foundation, SR Legacy | CC0 1.0 |
| Allergen | **None** | — | — |
| Dietary | **None** | — | — |
| Food safety | **None** | — | — |

- **FDC's terms were read on 2026-10-20** (fdc.nal.usda.gov/api-guide): REST v1 at
  `https://api.nal.usda.gov/fdc/v1/`, a free api.data.gov key, 1,000 requests an hour **per IP address**, and
  going over blocks the **key** for an hour. The data is "in the public domain and … not copyrighted" under CC0
  1.0. USDA suggests — but CC0 does not require — the citation "U.S. Department of Agriculture, Agricultural
  Research Service. FoodData Central, 2019. fdc.nal.usda.gov."; it is recorded on the source row without the
  year, which changes by release, and travels on every piece of evidence.
- **Branded, Survey (FNDDS) and Experimental foods are not approved.** Branded is manufacturer label data, Survey
  foods are mostly prepared dishes rather than ingredients, and Experimental is research data. A record from
  one is answered `NoApprovedSource`, and a search never asks for them and drops them if they come back.
- **An evidence kind with no approved source is unknown, never absent.** Allergen, dietary and food-safety
  evidence answer `NoApprovedSource` on every host. Approving a source for any of them is a new decision in
  this record, made after reading its terms; nothing is scraped or imported to fill the gap.
- **One catalogue, two readers.** `EvidenceSourceCatalog` is the list of approved sources with their licences.
  The seeder writes its `ReferenceSource` rows from it in every environment (a citation, not imported data),
  and the adapter stamps the same code, citation and licence onto every piece of evidence it returns.
- **Every piece of evidence carries its provenance**: source, data set and publication date as
  `SourceVersion` (`sr-legacy/2019-04-01`), licence, citation, the source's record id, and `RetrievedAt`.
  A cached answer keeps its original retrieval date.
- **The gateway is provider-neutral and maps at the boundary.** `IReferenceEvidenceGateway` returns
  application records only; FDC's field names, nutrient numbers and unit words exist in one file
  (`UsdaFdcFoodReader`). Unknown nutrients, mismatched units and unrecognised portion words are dropped, not
  guessed. Nothing it returns is an EF entity, and nothing becomes a reference fact without a later, reviewed
  step (14.2).
- **Searches use a platform ingredient's name only.** `ReferenceFoodSearch` can only be built from an
  `Ingredient`, so a creator's own text — workspace content — never reaches FDC's logs or the shared cache.
- **Global cache, answers only.** `global:reference:evidence:usda-fdc:v1:food:{id}` for 24 hours and
  `…:search:{sha256}` for one hour, in the DataLayer; throttles, outages and refusals are never cached.
- **The key is a header, and a throttle stops the whole process.** `X-Api-Key`, never the URL, and the client
  follows no redirects. A 429 blocks every read — retries included — for the stated wait (an hour when none is
  stated) through a process-wide `UsdaFdcThrottle`, and the standard resilience handler is removed because it
  would retry the 429s that extend a block. The quota is per address, so other clients on it can spend it
  unseen; and the throttle is in memory, so it moves to a shared store when a second host reads evidence.
- **A food with no nutrient table is unreadable, and an unlisted nutrient is unknown.** Neither is ever read as
  zero; a source's own zero arrives listed, with its derivation ("Assumed zero").
- **Off by default.** `Usda:Evidence` is `false`, so no machine is prompted for `usda-fdc-api-key`, and a host
  with none answers `NotConfigured`. Only the API references it until something in the Worker reads evidence.

**Options considered:** Open Food Facts for allergens (rejected for now — ODbL share-alike obligations and
contributor-entered data need their own decision, not a default); deriving allergen traits from Branded
ingredient statements (rejected — label text for one product is not evidence about an ingredient); a caching
decorator around the gateway (rejected — cache-first belongs in the DataLayer, and a gateway owns only the call).
**Consequences:** migration `AddReferenceSourceLicense` (`LicenseCode`, `LicenseUrl`, and a check that a licence
URL has a licence code); production now seeds one `ReferenceSource`, `usda-fdc`. No endpoint, facade or import.
Recorded contract fixtures are in `src/CreatorPantry.Tests/Ingredients/Fixtures/UsdaFdc/`, with what the
recordings showed that FDC's spec does not.
**Rules:** `external.md`, `recipes.md`, `ai.md`, `tenancy.md`, `aspire.md`.

### B-34 Nutrition mappings

*Recorded 2026-10-20 by microprompt 14.2. Cites ING-008 and DATA-RQ-007, whose text is not in the repository
(B-12).*

- **One mapping per platform ingredient per workspace, not per recipe line.** A creator confirms "all-purpose
  flour is FDC 169761" once and every recipe in the workspace uses it. Line ids are not stable — a move between
  groups, a duplicate or a client that omits ids mints new ones — so a mapping keyed on them would be lost by
  ordinary edits. A line with no platform ingredient has nothing to map and is reported as unmapped by the
  calculator (14.3), never guessed.
- **A new `Nutrition` module** owns `NutritionMapping` and `NutritionMappingRejection`. It reaches evidence only
  through two new `IIngredientFacade` methods (search by a platform ingredient, fetch one record), which return
  ServiceModels because 14.1's gateway types do not cross a module.
- **Lifecycle:** `Candidate` (automatic), `Confirmed`, `NeedsReview` (a newer source version of a confirmed
  record), `Unresolved` (no candidates, creator declined, reference rejected, or withdrawn by the source).
  - **Nothing automatic is ever confirmed**, whatever its confidence — stricter than the prompt, which asked
    only that low-confidence matches be confirmed. Only `Confirmed` is for estimates.
  - **Confidence is recorded, not scored:** `High` for the only open hit or an exact normalized name, `Low`
    otherwise, with the match method beside it.
  - **A rejection is remembered** per mapping, so no proposal offers that reference again; a creator remapping
    to it withdraws the rejection.
  - **A remap** points at any approved record the creator chooses, from any state or none, and is confirmed as
    `CreatorSelected`; a source other than the one approved for nutrition is refused without being asked.
  - **Refresh** compares the source's current version: newer moves a confirmed mapping to `NeedsReview` keeping
    its confirmation, and confirming accepts the new version; a record the source says is gone or unapproved
    withdraws it. An outage, a throttle or a missing provider writes nothing and concludes nothing.
- **Quantity basis.** Nutrients are per 100 g (14.1's contract). One optional portion turns volume or count
  quantities into grams: `Reference` when it is exactly one of the record's own measured portions, otherwise
  `Creator` — this workspace's override. A mass, temperature or qualitative unit is refused.
- **Roles.** Any member reads. A Contributor may ask for a proposal, which writes only a candidate or an
  unresolved mapping nobody decided — never over a confirmation or a creator's "none fits". Confirming,
  rejecting, remapping and marking unresolved take an Editor, the role that approves a recipe, and are audited
  with state names only; so does a refresh, because it can withdraw a confirmed reference and its portion.
- **Nothing crosses a workspace or reaches a recipe.** Mappings, rejections and portions are filtered to their
  workspace; nothing derives a shared or platform mapping from them; nothing writes to `RecipeIngredient`.
- **The schema holds the lifecycle:** check constraints for a reference whole exactly when not unresolved, a
  confidence only on an automatic match, a review version only under review, a confirmation only while
  confirmed or under review, a portion whole and only beside a reference; unique `(WorkspaceId, IngredientId)`;
  rejections cascade through a composite key to their mapping with no second path from `Workspaces`.

**Options considered:** per-line mappings (rejected above); auto-confirming high-confidence matches (rejected —
an estimate is a published claim, and a creator should have said yes to every number it rests on); keeping
mappings in the Recipes module (rejected — mappings, the calculator and the nutrition endpoint are their own
context, and Recipes is already the largest module).
**Consequences:** migration `AddNutritionMappings`; no endpoint (14.6b) and no calculator (14.3). A refresh
reads the evidence cache, so a newer version can take up to a day to be noticed. One portion per mapping, so an
ingredient measured both by the cup and by the piece needs the calculator to report one of them as unconverted.
**Rules:** `tenancy.md`, `recipes.md`, `backend.md`, `external.md`.

### B-35 Nutrition estimate arithmetic

*Recorded 2026-10-20 by microprompt 14.3. Cites ING-008 and CALC-001/006, whose text is not in the repository
(B-12); their meaning here is taken from the Phase 7 prompts that use them.*

- **Of a version, deterministically.** An estimate reads one immutable recipe version, the workspace's confirmed
  mappings and the source's current nutrients, and nothing else: no clock, no model. `NutritionEstimateCalculator`
  is pure, and records `nutrition-estimate/1` on every result.
- **Exact until shown (CALC-001, CALC-006).** Every step is an exact `Quantity` fraction from the stored values.
  Each figure is rounded once, at output, ties to even, and returned beside its exact value to six places.
  Display precision by unit: kcal 0, g 1, mg 0, µg 1 — display only, not a statement of accuracy, and not
  regulatory label rounding. A non-zero value that rounds to zero is flagged `Trace`, never shown as 0. A total
  too large to state is refused (`nutrition.estimate.out_of_range`), not thrown.
- **Grams.** Mass units by their gram factor. Volumes only through the mapping's portion when it is a volume,
  via millilitres; counts only through a portion in the same count unit. A quantity with no unit ("2 eggs") is
  not taken to be "each". Qualitative and temperature units, and a missing portion, are `Unconvertible`.
- **Edible portion as written.** FDC's values are per 100 g edible portion; the creator's quantity is taken as
  exactly that. No trim, refuse, cooking-loss or nutrient-retention factor is applied, and the method says so.
- **Ranges** are carried low and high through every step.
- **Servings:** the caller's count, else the version's `ServingCount`, else `YieldQuantity ÷ ServingSize`, else
  none — never a per-100 g of finished dish, whose weight is unknown.
- **Missing data.** Each line is `Included`, `Unmapped`, `NeedsReview` (under review, a different source version
  than confirmed, or withdrawn), `Unconvertible`, `NotQuantified` or `Optional`. Optional and unquantified lines
  are not required and not counted. **Amended by [B-43](#b-43-food-domain-safety-audit) (14.7):** an
  *unquantified* line the creator did not mark optional is required after all, and appears in
  `unresolved.notQuantified`. Treating the two alike let an estimate report `Complete` while omitting the salt.
  An optional line is unchanged — it is the creator's "if you like". `Publishable` only when every required line is counted; otherwise the
  totals are returned labelled `Partial` — a known-incomplete lower bound — for 14.6d to show honestly.
  A nutrient a counted reference does not report is unknown for that line: the nutrient is marked incomplete and
  names the line, never counted as zero. A caller publishes nothing from an unpublishable estimate, and only
  complete nutrients from a publishable one.
- **Confidence** is the weakest quantity basis among counted lines: `Mass`, `ReferencePortion`, `CreatorPortion`.
- **Reads only.** An estimate never writes, not even to a mapping it finds out of date; a source that cannot be
  asked fails the estimate rather than shrink it. Any member may read one.

**Options considered:** withholding numbers until complete (rejected — a creator needs to see what is missing
and how much it matters); counting optional lines (rejected — they are the creator's "if you like"); treating a
unitless count as "each" when the portion is "each" (rejected as a guess).
**Consequences:** `NutritionReferenceServiceModel` now carries nutrients, citation and licence. The estimate
reads recipe versions through `INutritionRecipeVersionReader` over `IRecipeFacade.GetSnapshotAsync`, so a host
serving it registers Recipes. No endpoint (14.6a), no readiness rule and no JSON-LD nutrition yet.
**Rules:** `recipes.md`, `ai.md`.

### B-36 Dietary and allergen analysis

*Recorded 2026-10-20 by microprompt 14.4. Cites ING-007, whose text is not in the repository (B-12).*

- **A reading of records, never a verdict about food or a person.** `DietaryAllergenAnalyzer` is pure and records
  `dietary-allergen/1` and the date it read the records as of. No finding means safe, free from, certified or
  suitable, and a guard test holds that for every enum name and every sentence the analysis can produce.
- **What it reads:** one immutable recipe version (the 14.3 reader), the selected allergens and dietary profiles
  (checked against the vocabulary, which now names both), and the **approved** traits in effect on the evaluation
  date (UTC). Unreviewed, rejected, superseded and future-dated traits never drive a finding. A source's latest
  trait in effect supersedes that source's earlier ones and never another source's — and a later "consulted, does
  not say" is no word at all, so it never supersedes an earlier warning.
- **Only a vetted, active source may reassure.** "Not listed" and "compatible" count only from a source whose kind
  may assert absence (not community-contributed or AI-estimated) and that is not retired; from any other they are
  cited with `Counted = false` and the line stays *Unknown*. Any approved source may give a cautious word. For
  allergens the database already refuses an unvetted "not listed"; the analyzer refuses it again, and is the only
  guard for "compatible" and for retired sources.
- **Every line is analysed**, optional and unquantified ones included — a garnish or "to taste" can still bring
  an allergen — and a finding says when its line is optional.
- **Allergens, per line:** no record, a source that does not say, or a line that is not a recognised ingredient
  → *Unknown*; any `Present` → *Contains*; else any `PossiblePresence` → *Possible*; else all
  `NotListedBySource` → *Not listed*. **Per recipe**, the most cautious line: *Contains*, *Possible*, *Unknown*,
  then *Not listed in the cited records* — worded as "not a statement that the recipe is free of …".
- **Dietary profiles, per line:** *Unknown*, else any `Incompatible` → *Incompatible*, else any `DependsOnProduct`
  → *Depends on product*, else *Compatible*. **Per recipe:** *Doesn't fit*, *Depends on product*, *Unknown*, then
  *Fits, as recorded* — stated as "every ingredient is recorded as compatible with … in the cited records", never
  "fits Nut-Free", which for a profile named for what it leaves out would read as a free-from claim.
- **A finding drawn from some lines says how many others have no reviewed record.**
- **Conflicts** — a source listing an allergen (or a possibility) where another publishes a composition without it;
  a source calling an ingredient compatible where another does not or says it depends — keep the more cautious
  finding, are flagged, and cite both.
- **Hidden sources and cross-contact come only from what a source stated** (`PossiblePresence`,
  `DependsOnProduct`, with the source's required note quoted). Nothing is inferred from an ingredient's category,
  its neighbours, a line's own text or a model, and the gluten-free profile and the wheat allergen never answer
  for each other.
- **A fixed cross-contact disclaimer is on every analysis**, the best answer included.
- **Read-only, any member;** the workspace's own version only (the Recipes module answers another workspace's as
  not found). Any later explanation, a model's included, takes the findings as input and cannot change one.

**Options considered:** skipping optional lines as the estimate does (rejected — presence does not depend on
quantity); resolving conflicts by source kind (rejected — a disagreement is information a creator needs);
deriving gluten-free from the wheat allergen (rejected as an inference the records do not state).
**Consequences:** `VocabularyCatalog` gained `Allergen` and `DietaryProfile`; `IIngredientFacade` gained
`FindApprovedTraitsAsync`. Until reviewed traits are recorded or a source is approved (B-33 approves none), real
analyses read almost entirely as *Unknown* — the correct answer for records that do not exist. No endpoint (14.6),
no UI (14.6c).
**Rules:** `recipes.md`, `ai.md`.

### B-37 High-risk method warnings

*Recorded 2026-10-20 by microprompt 14.5. Cites AIREC-GR-005 and AI-009/010, whose text is not in the repository
(B-12).*

- **Every warning says verification is required, because nothing is approved to cite.** B-33 approves no
  food-safety source, so each warning carries `VerificationRequired` with its reason — `NoApprovedSource` on every
  host today, `NotConfigured`, `Unavailable` or `NoReference` when a source exists but cannot answer. FSIS, NCHFP
  and the like are not cited: approving one is a new decision in this record, after reading its terms. A source
  that becomes reachable is still reported `Unavailable`, never cited, until a reviewed reader exists for it.
- **Without a reference, nothing is compared.** A stated internal temperature is detected and recorded as written
  (value, scale, where — both ends of a range, "165 F" as well as "165°F"); it is never called too low, because no
  approved minimum exists to call it against. Only the figures the doneness cue is about are recorded — not the
  425 °F oven earlier in the sentence — and a cue whose figure is in the next sentence reads it from there. No
  message says what the hazard is or what a reader must do — each would be a claim of its own.
- **Ten categories:** canning, fermentation, curing or smoking, sous vide, raw or undercooked protein, internal
  temperature, pregnancy or medical diet, unsupported safety claim, and — because the technique vocabulary already
  flags them — drying and oil infusion. A technique flagged later that the list does not name is reported as
  `OtherFlaggedMethod` rather than dropped.
- **Structured choices first.** A primary or step technique with `RequiresSafetyCaution` is `Detected` on its own.
  `no-cook` is raw service only beside a meat, poultry, seafood or egg line. A retired technique is not in the
  active vocabulary and raises nothing structurally; its step's text is still read, and `UnrecognisedTechniques`
  counts it, so an empty list read from less than the whole record says so.
- **Text by how it is worded, never by a bare food word.** Canned tomatoes, fermented black beans, kimchi or
  prosciutto in a line, pink Himalayan salt, baby spinach, a cheesecake's water bath, bread dough's bulk
  fermentation and "add the raw shrimp" all raise nothing, and a test pins each — as do the misfires an
  ai-safety review traced in the first draft: freezer headspace, a blender jar, "no need to refrigerate the dough",
  "no longer safe to eat", beet carpaccio, a raw egg mixed into a meatloaf, "never serve raw chicken",
  "toddler-approved", air-dried cookies, a stir-fry oil infused and used at once, and "no sodium nitrite added".
  An ingredient line is something bought: only a curing agent, raw-egg or sushi-grade wording, and bare "pink salt"
  count from one. A creator's caution is not the thing it warns against.
- **Ambiguous wording is `Possible`** with a message saying what it might be: sealed jars for the pantry, food left
  out for days, a low water bath held for hours, bare "pink salt", rare or medium-rare meat and soft eggs, a
  thermometer test with no figure anywhere in the field, a pink or undercooked centre, a poke bowl, a dish often
  made with raw egg (hollandaise, mousse, tiramisu, carbonara, homemade mayonnaise) where the recipe has eggs, a
  mention of pregnancy or a condition, a keeping time where nothing in the recipe says how it is stored, a
  bought-sounding infused oil. A raw dish named where no line is a protein is `Possible`.
- **Protein categories scope two checks and infer nothing.** Raw-dish wording counts as `Detected` only when a line
  is meat, poultry, seafood or eggs. Internal temperatures count when a line is meat, poultry or seafood, or the
  sentence or its neighbour names a protein — eggs alone do not, so a cake's or a loaf's thermometer reading is not
  flagged. An ingredient's category never stands in for a method the text does not state.
- **Allergy assurances are claims.** "Allergy-safe" and "safe for people with nut allergies" are unsupported safety
  claims here; whether a recipe contains an allergen remains 14.4's analysis.
- **One warning per category**, its confidence the highest of its triggers, listing every trigger: the field, the
  line, step or equipment id, and the creator's matched words (at most 120 characters) or the technique code.
- **Read-only, any member, nothing leaves the workspace.** The version is read through the Recipes facade, so
  another workspace's is not found; categories and coverage are global reads by ingredient id. A fixed disclaimer
  on every result says finding nothing is not a statement that the recipe is safe, and a warning is not a ruling
  that it is unsafe. A guard test holds that no message or statement asserts safety.
- **Nothing blocks.** No readiness rule, endpoint or UI; `food-safety-warnings/1` is recorded on every result.

**Options considered:** citing FSIS minimum temperatures and the USDA canning guide now (rejected — an unapproved
source, B-33); flagging every step temperature beside a protein (rejected — every roast would carry a warning);
"raw chicken" as a trigger (rejected — it is how ordinary recipes say what goes into the pan); medium-rare as
`Detected` (rejected — it would mark most steak recipes; `Possible` leaves the call to the creator).
**Consequences:** `IIngredientFacade` gained `ListCategoriesAsync` (retired ingredients included) and
`DescribeEvidenceCoverage`. Until a food-safety source is approved, every warning reads "verification required" —
the correct answer for references that do not exist. Readiness, an endpoint and the AI review's reference checks may
consume these later; none does yet.
**Rules:** `recipes.md`, `ai.md`, `tenancy.md`.

### B-38 Dietary and allergen analysis endpoint

*Recorded 2026-10-20 by microprompt 14.6. Exposes B-36's analysis (ING-007, whose text is not in the repository —
B-12).*

- **`POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/{versionNumber}/analysis/dietary-allergen`.**
  POST because the selection — up to twenty allergens and twenty profiles — belongs in a body, not a URL; it
  writes nothing, takes no idempotency key and answers `no-store`, because approved traits change as they are
  reviewed. Any member, a Viewer included. The gateway's catch-all already forwards it.
- **A version by number**, as every public recipe route names one. The Recipes facade gained
  `GetSnapshotByNumberAsync`, and `RecipeSnapshotServiceModel` now carries the version's id, so the answer names
  the exact version it read whichever way it was asked.
- **Confidence describes the records, never the likelihood.** Per allergen and profile, `coverage` counts the
  lines read and those with a record that counts, and `confidence` labels it: `Recorded` (every line, no
  disagreement), `Partial` (some lines have none), `Disputed` (approved sources disagree) or `None`. It is derived
  from the line findings alone and cannot move one: `Contains` on `Partial` records still contains. A reassuring
  word that does not count (an unvetted or retired source) does not count as coverage either.
- **"Evaluated source version" is a source and a date.** Trait records have no version of their own, so `sources`
  lists every source the analysis read — counted or not — once, with its citation, licence (14.1) and the latest
  effective date among its records, beside `methodVersion` and `evaluatedOn`. `TraitSourceServiceModel` now
  carries the licence; no migration.
- **Refusals as the Recipes routes give them.** `400 nutrition.analysis.invalid` with field errors (a JSON `null`
  list now reads as empty rather than crashing validation), `422 nutrition.analysis.subject.unprocessable`,
  `404 recipes.recipe.not_found` for an unknown or another workspace's recipe, and `404 recipes.version.not_found`
  naming `versionNumber`, passed through from the Recipes module unchanged.

- **`no-store` on success only**, as the readiness route does; a refusal carries nothing worth caching either way.
  A missing or unparseable body is `400 request.malformed`, as on every route.
- **No two types may share a public schema name.** The first draft published this route's request body as its
  response (`DietaryAllergenAnalysisViewModel` and `…ServiceModel` were both `DietaryAllergenAnalysis` once the
  suffix was stripped) and its two line-finding enums as objects referring to themselves (each shared a name with
  the record that carries it), and the snapshot test could not see it, having been generated from the same
  collision. The request is now `DietaryAllergenSelectionViewModel` and the line records `…LineAnalysisServiceModel`;
  `OpenApiDocumentation` records every collision and `OpenApiContractTests` fails on any but the two it lists.
- **Two collisions predate this and are listed, not fixed:** `AiDraftAcceptance` (`POST .../recipe-draft-requests/
  {requestId}/acceptance`) and `AiProposalDisposition` (`POST .../ai-proposals/{aiProposalId}/disposition`) each
  document the request body with the response's schema, so the real request (`decision`, …) is unpublished.
  Correcting them renames a shipped schema and belongs in its own reviewed change.

**Options considered:** a version id in the route (rejected — no other public recipe route takes one); coverage
counts with no label (rejected — the prompt asks for confidence, and a closed label over counts adds no
judgement); a GET with ids in the query string (rejected — length, and caches that ignore `no-store`); throwing on
a schema-name collision (rejected — it turns the development document into a 500 with the reason hidden).
**Consequences:** `NutritionErrorCodes.EstimateRecipeNotFound` was `nutrition.estimate.recipe_not_found`, which
`ProblemResults.StatusFor` would have answered `400`; it is now `nutrition.estimate.recipe.not_found`, corrected
before any route returned it. The OpenAPI snapshot grew by 683 lines with nothing removed. No Angular model yet —
14.6c adds it with the screen.
**Rules:** `api-contract.md`, `tenancy.md`, `recipes.md`, `backend.md`.

### B-39 Nutrition estimate endpoint

*Recorded 2026-10-20 by microprompt 14.6a. Exposes B-35's estimate (ING-008, whose text is not in the repository —
B-12).*

- **`POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/{versionNumber}/analysis/nutrition`**, shaped
  as 14.6's route: read-only, no idempotency key, `no-store` on success, any member, version by number through
  the same reader, refusals passed through from Recipes. Its own controller, `NutritionEstimateController`.
- **The yield basis is `servings`, optional.** Omitted or null uses the version's own basis — serving count, else
  yield ÷ serving size, else none, when every `perServing` is null and no per-100 g of finished dish is invented. A
  number above 0, at most 10,000 and to four places divides by that instead; the check moved from the facade into
  `NutritionEstimateRequestViewModelValidator` unchanged. `{}` is a valid body; no body is `request.malformed`.
- **Labelled as an estimate**, as B-35 made it: `label`, `method`, `methodVersion`, exact and display values, `trace`.
- **Unresolved mappings stay explicit.** Lines keep their `outcome`, `reason` and `required`; a new `unresolved`
  groups the required lines not counted (`unmapped`, `needsReview`, `unconvertible`), derived from the lines so it
  cannot disagree with them. Optional and unquantified lines are never in it.
- **Publish gating is exposed, not wired.** `publishable`, `partial` and per-nutrient `complete` say what may be
  published, and B-35's rule stands — nothing from an unpublishable estimate, only complete nutrients from a
  publishable one. No readiness rule or publish path reads it yet; that is a separate change, not "expose only".
- **Codes that answer the status they mean.** `nutrition.estimate.source.unavailable` (503) when the source cannot be
  asked, with a message that says "not available on this installation" when it is not configured, since that does
  not clear by waiting; `nutrition.estimate.quantities.unprocessable` (422, was `nutrition.estimate.out_of_range`,
  which would have answered 400) for a total too large to state. Neither had been returned by a route before.

**Options considered:** filtering `lines` client-side instead of `unresolved` (rejected — every screen would
re-derive the same three lists); correcting the shared `nutrition.mapping.source_unavailable` now (rejected — the
mapping commands own it, and 14.6b corrects it with their route); a readiness rule (deferred, above).
**Consequences:** `NutritionEstimateServiceModel` gained `Unresolved`; the two estimate goldens gained it and nothing
else. The OpenAPI snapshot grew again with nothing removed, and the schema-name guard found no new collision.
`nutrition.mapping.source_unavailable` ended in `_unavailable` and would have answered 400; 14.6b corrected it (B-40).
**Rules:** `api-contract.md`, `recipes.md`, `tenancy.md`, `backend.md`.

### B-40 Nutrition mapping commands

*Recorded 2026-10-20 by microprompt 14.6b. Exposes B-34's decisions (ING-008, whose text is not in the repository —
B-12).*

- **Keyed by the platform ingredient**, because a mapping is one workspace's decision about one ingredient:
  `GET /api/v1/workspaces/{workspaceSlug}/nutrition-mappings/{ingredientId}` and `POST …/confirm`, `…/reject`,
  `…/remap` on it. Verb sub-resources, as the recipe routes do, each reusing 14.2's view model and validator.
  `NutritionMappingsController`; no gateway change.
- **One ingredient, one decision, never in bulk.** There is no list command, and confirming one mapping leaves every
  other candidate as it was. Nothing writes to a recipe: a test holds a recipe's lines and concurrency token
  unchanged across a confirmation.
- **The read is included** because the commands owe a token and nothing else hands one out — the estimate carries a
  mapping id, not its token. Any member may read; it is `no-store` so no cache serves a stale token. No mapping, and
  another workspace's, are both `404 nutrition.mapping.not_found`.
- **A token for every decision on a mapping that exists.** Confirm always required one; reject now does too (a
  missing one is `400` naming it — there is nothing to reject without a mapping the caller read); remap needs one
  when a mapping exists and none when it makes the first. A stale or missing one over an existing mapping is
  `409 nutrition.mapping.conflict`. A refresh that puts a newer source version under review changes the row
  version, so nobody confirms a version they did not see.
- **Editor**, by route policy and again in the facade for workers and plugins; audited, as 14.2 made them. `200`
  with the mapping for all three, a first remap included — it acts on the ingredient's one mapping, which has no
  address of its own for a `Location`. No idempotency key: the token makes a repeat a `409`, never a second write.
- **Codes corrected before any route returned them**, each of which would have answered 400: `stale` →
  `nutrition.mapping.conflict` (409), `state_conflict` → `nutrition.mapping.state.conflict` (409),
  `source_unavailable` → `nutrition.mapping.source.unavailable` (503), `not_saved` →
  `nutrition.mapping.save.unavailable` (503).

**Options considered:** one `POST …/decision` with a discriminator (rejected — three shapes, and three validators
that already exist); leaving the read to 14.6c (rejected — the commands would be unusable until then); `201` with a
`Location` for a first remap (rejected — no per-mapping address).
**Consequences:** the SQLite test host does not bump row versions on update, so a real token going stale is tested
on SQL Server (`NutritionMappingTests`) and the route tests use a token the mapping never had. The OpenAPI snapshot
moved some existing paths as the new controller sorted in; checked semantically against HEAD, nothing published was
removed or changed. Propose, refresh and mark-unresolved have no route.
**Rules:** `api-contract.md`, `tenancy.md`, `recipes.md`, `backend.md`.

### B-41 Dietary and allergen review UI

*Recorded 2026-10-20 by microprompt 14.6c. Shows B-36's analysis through B-38's route (ING-007 and AI-UI-002,
neither of whose text is in the repository — B-12).*

- **A tool on a recipe, not an area of it.** The eighth entry in the recipe editor's Tools drawer, beside Scale,
  Convert and Yield & display and before the AI requests: it reads the *saved* version, as every tool there does,
  and says so when the form beside it has edits that were never sent. Not a page — it has no address worth
  linking, the route answers `no-store`, and the selection is the question. Not a dialog — the recipe stays in
  view behind it. It prints no title of its own: the drawer already names the tool, so the region is named and
  its parts carry the headings.
- **The panel decides nothing and writes nothing.** Findings, confidences, coverage counts and the sentence about
  each finding are the server's; the panel groups lines, names findings in words and renders `statement` and
  `crossContactDisclaimer` verbatim. No finding is derived, re-worded or re-ranked here — subjects render in the
  order they were asked about, because a ranking the server did not give would be the panel's own judgement.
- **No finding is ever a success tone.** `NotListedInRecords` and `FitsAsRecorded` get the same neutral pill as
  `Unknown`, and a test holds those pairs equal: a tick beside "not listed" is exactly how a reading of records
  becomes read as a clearance. Nothing on screen carries a certification badge, and no nutrition number appears.
- **The copy is guarded like the server's.** Every sentence the panel writes itself lives in exported constants
  (`PANEL_COPY`, the label maps, `GROUP_HEADINGS`, `conflictSentence`, `notCountedReason`), and a spec asserts
  none of them says safe, unsafe, free from, free of, certified or suitable. The two sentences that *do* carry
  those words are the server's own denials, rendered untouched.
- **The disclaimer is on every answer**, in a warning notice above the findings, including the best one — never
  collapsed and never summarised.
- **Confidence is shown as what it is:** a pill about the records (`Records: some lines have none`,
  `Records: sources disagree`), the coverage counts in words, and, wherever the records are not complete and in
  agreement, one line saying this describes the records and not how likely the finding is.
- **Hidden sources, unknowns and conflicts each have their own group**, collapsible, cautious ones open: lines a
  source says contain it, lines a source says may contain it (with a line saying nothing was inferred from a
  category, a neighbour or the line's own words), lines with no reviewed record (each with its reason), and —
  shut by default — the lines whose records do not list it, stated as a fact about the records. A conflict is
  flagged on the line and counted under the finding, both saying the more cautious word was kept and both sources
  cited. Evidence opens per line and names the source, its kind, the date its statement took effect and its own
  note quoted; a word that does not count is shown as `Cited, not counted` with the reason — an unvetted kind,
  or a retired source.
- **The pick lists are the catalogue's**, read through two new `ReferenceService` catalogue reads
  (`/reference/allergens`, `/reference/dietary-profiles`, cached for the session like the rest). Each option
  carries its description, because what an entry covers decides the meaning of every record under it. Both lists
  failing is one state with nothing to pick; a reload drops any id the catalogue no longer offers.
- **The analysis is decoded whole or not at all.** Every finding, confidence, presence, compatibility, reason and
  source-kind enum is closed, and an answer missing its disclaimer is refused: a half-read analysis would drop a
  finding, and a dropped finding reads as "nothing was found about this". An unreadable answer is reported as
  unavailable rather than shown in part.
- **A refusal never erases an answer on screen.** The previous analysis names its own version and read-date, so
  it cannot be mistaken for the answer to the attempt that failed. `400` field errors land on the list they name;
  `422` offers to reload the lists; a newer version than the one read shows an `Out of date` pill and keeps the
  findings.
- **One live region, mounted empty from first render**, saying that a request is in flight and then what came
  back — a region created along with its text is not reliably announced. The Submit button is `disabled` only on
  what is true before anyone reaches it; while a request is in flight it stays focusable and says `aria-disabled`,
  because disabling a focused button drops focus to the body, and inside an open drawer that costs the creator
  their place.

**Options considered:** a routed page (rejected — no address worth linking, and the answer describes a moment);
sorting subjects most-cautious-first (rejected — a ranking the server did not give); a separate group for
conflicting lines (rejected — it would list the same lines twice; the flag sits on the line and the count under
the finding); tolerant decoding of an unknown finding (rejected — the only safe guess would be the most cautious
one, which would state something no source said).
**Consequences:** a collapsed group needs `[hidden] { display: none !important }` in the panel's own stylesheet:
every author `display` rule beats the UA rule behind the attribute, and `.lines[_ngcontent]` and
`[hidden][_ngcontent]` have the same specificity, so source order alone would decide it. Two specs pin the
computed display. **`RecipeReadinessComponent` has the same defect and is left alone here** — `.findings` is
`display: grid` and its Satisfied and NotApplicable groups are collapsed by default, so those lists show while
their buttons say "Show". The fix is that component's own line, or one rule in `global.css`; either is its own
change. `CpFormSectionComponent` hard-codes an `h2`, so the two pick lists are `h2`s beside the answer's — a
`headingLevel` input would let a panel demote them, and is not added here.
`DescribedReferenceEntry` and its decoder are new, as are
`ListDescribedReferenceEntriesOutcome`, `listAllergens` and `listDietaryProfiles`. Nothing was added to
`@creator-pantry/ui`; the panel is composed from `cp-form-section`, `cp-choice-group`, `cp-status-pill`,
`cp-badge`, `cp-notice` and `cp-button`. No nutrition UI (14.6d), and no readiness rule reads this.
**Rules:** `frontend.md`, `design-system.md`, `recipes.md`, `ai.md`.

### B-42 Nutrition mapping and estimate UI

*Recorded 2026-10-20 by microprompt 14.6d. Shows B-35's arithmetic through B-39's route, and decides B-34's
mappings through B-40's commands (ING-008, whose text is not in the repository — B-12).*

- **A tool on a recipe, like the dietary review beside it.** The ninth entry in the recipe editor's Tools
  drawer, over the *saved* version, printing no title of its own because the drawer names it. The estimate is
  asked for, never fetched on open: a creator who opens Tools has not asked for a figure yet.
- **No arithmetic in the browser.** Every figure is the server's own display value, rounded once server-side;
  `toFixed` is called only with the precision the server already rounded to, so it pads trailing zeros and
  cannot change a value, and a test pins that. Changing the serving basis re-asks the route rather than
  dividing. The exact values sit behind a disclosure and are never used to re-round.
- **Nothing is hidden.** A partial estimate shows its totals *and* says they are a lower bound; an incomplete
  nutrient shows its figure *and* names how many counted ingredients' sources are silent about it, "never
  zero"; a trace shows as `trace`; a range shows as a range; a `Low` confidence mapping shows its confidence.
  The three groups of uncounted lines are the server's own `unresolved`, rendered as given rather than
  filtered out of `lines` a second time, and the optional and unquantified lines are listed too, under "Left
  out on purpose" — a line in no list at all reads as one that was counted.
- **Publishing is stated, not enforced.** `publishable` and `partial` become one sentence: don't publish a
  partial estimate, and of a publishable one only the nutrients marked complete. It says in the same breath
  that nothing in CreatorPantry stops a publish on an estimate yet, because nothing does (B-39) — implying a
  gate would be worse than naming its absence.
- **Mapping review lives inside the estimate**, because a mapping is addressed by platform ingredient and
  nothing else says which ingredients matter: there is no list route and no mapping inbox. An estimate line
  carries a line id, so the panel joins it to the recipe **as saved** to find the ingredient id; a line matched
  to no platform ingredient says so and offers no control.
- **The review is offered on a counted line too**, not only an uncounted one. `NutritionLineEstimate` carries
  `basis`, `mappingId` and `source` and nothing else about the mapping, so a counted figure resting on a
  reference matched with `Low` confidence is invisible from the figures alone; opening its mapping is the only
  way to it, and leaving the control off those rows would hide exactly what this screen may not hide. Carrying
  the match confidence on the line is a server change, not made here.
- **A decision moves focus to the mapping it decided.** Confirming leaves nothing to decide, so the focused
  button is removed from the DOM — inside a drawer whose page is `inert`, focus would fall to the body. The
  region takes it, named by its line, so the new state is what gets read.
- **Confirm and turn-down only.** Each quotes the token of the mapping as read. A `409` — a stale token, or
  nothing left to decide — re-reads the mapping and never resends the decision; both `503`s say nothing was
  saved and leave the control there, because both are safe to send again. A refusal below Editor is reported
  as a role refusal rather than a lost session, and the control is not offered to a Contributor in the first
  place. **No remap**: it needs a reference the creator cannot search for, since no route exposes one, and a
  box for FDC record ids is not a feature. **No propose, no refresh, no mark-unresolved** — none has a route.
- **A turn-down is asked about first**, through `ConfirmService`, because it cannot be undone from here: the
  rejection is remembered for the whole workspace and withdrawing it needs the remap no screen can do.
- **A decision never rewrites the figures.** It marks the estimate "a mapping changed" and leaves the table
  alone; a creator reading numbers should not have them replaced underneath them, and the stale pill says what
  to do. A newer recipe version shows the same way.
- **An estimate is decoded whole or not at all**, and one that arrives without its `label` or its `method` is
  refused: those two are what mark the figures as an estimate. A source that cannot be asked is the server's
  own `503` sentence, which distinguishes "down" from "not available on this installation" — one clears by
  waiting and the other does not — and no partial estimate is shown in its place.

**From the design review**, fixed here: focus after a decision (above); the review on counted lines (above);
the parts demoted to `h3` under the drawer's own `h2`; the nutrient table in a focusable, named scroll box,
because three columns of figures do not fit a drawer that becomes a full-screen sheet at 40rem; the exact
values and the reference block moved onto `cp-facts`; the lower-bound sentence onto `cp-notice`; each review
control naming its line; the per-line decisions demoted to `secondary` beside the one primary; the
per-serving dash given a said alternative; an incomplete nutrient's note tied to its row by
`aria-describedby`; and a `displayPrecision` outside 0–20 refused in the decoder, since `toFixed` throws
outside 0–100. Two wordings were corrected because they claimed more than the data does: the confidence line
said "the least precise way any ingredient's weight was known" over a label that may read *Weighed amounts*
— it now says "weakest basis across the counted ingredients", and the word *confidence* is no longer shared
with a mapping's match confidence — and the publishable sentence said the complete nutrients "may be
published", which is the clause a creator reads and reads as permission; it now states the server's rule and
says who checks it.

**Options considered:** fetching an estimate when the tool opens (rejected — a figure nobody asked for);
auto-refetching after a mapping decision (rejected, above); deriving the uncounted groups from `lines`
(rejected — the server derives them so the two cannot disagree); a mappings page (impossible: no list route);
re-rounding the exact values for display (rejected — that is the arithmetic this screen may not do).
**Consequences:** `nutrition.models.ts` and `NutritionService` are new and cover both routes; the panel reuses
`roleAllows` from `recipe-readiness.models.ts`. The nutrient table is a hand-rolled `<table>` — the library has
no data table, and `cp-list-shell` frames a list without columns. No readiness rule, no publish path and no
JSON-LD read any of this yet.
**Rules:** `frontend.md`, `design-system.md`, `recipes.md`, `ai.md`, `tenancy.md`.

### B-43 Food-domain safety audit

*Recorded 2026-10-20 by microprompt 14.7. The findings, the matrix and what was deliberately left undone are in
[docs/food-domain-safety-audit.md](../food-domain-safety-audit.md); this records the decisions that changed
behaviour. Cites AIREC-GR-005, AI-009/010 and ING-007/008, none of whose text is in the repository (B-12).*

- **An assurance is refused; a subject is not.** `AiRecipeSafetyClaimScanner` refuses free text that says
  something is free of an allergen, safe to eat, suitable for a person, or guaranteed — in a substitution's
  prose, a review's finding, a proposed recipe value or a first draft. It is deliberately *not* the editorial
  claim scanner, which also matches "vegan", "fermentation" and "medium-rare": those are what these
  capabilities are about, and pointing that scanner at them would either break every answer or be switched off.
  Two tiers, because a warning is where a limitation legitimately names what a recipe *cannot* be made: an
  absence claim is refused in a value that becomes the recipe, an assurance about a person is refused everywhere.
  Correctable, so the model is asked again; the structured effects plus a caution remain the way to say it.
- **A prompt may not ask for a verdict the product cannot give.** `recipe.review-1.1.0` replaces 1.0.0's
  instruction to say a recipe "reads as complete, consistent, and safe as written", and CreatorPantry supplies
  the empty-answer sentence itself rather than printing the model's. A capability's `safetyClass` is a label
  until something enforces it; this is the first thing that does.
- **A dietary adaptation owes a caution, and may not do its own yield arithmetic.** The caution rule
  substitution and review already enforced now covers adaptation. Under the yield goal a proposed
  `yieldQuantity` must equal the deterministic figure, and `servingCount`/`servingSize` are refused outright —
  a model-chosen servings figure would silently become the divisor of every per-serving nutrition estimate.
- **A review's temperature and timing findings always need a reference check**, at any severity, because the
  model chooses the severity. Keying a safety floor off a model's own judgement let it decide whether its own
  food-safety figure needed checking.
- **An unquantified required line makes an estimate unpublishable. This amends B-35.** That decision put
  optional *and* unquantified lines outside the required set; for an optional line that is right — it is the
  creator's "if you like" — but for a line they did not mark optional it meant an estimate could report
  `Complete` and `publishable` while omitting the salt or the oil. Such a line is now required and appears in a
  new `unresolved.notQuantified` group, and publishability also requires that something was counted at all,
  which an empty or wholly unweighable recipe failed vacuously before. No arithmetic changed and no figure
  moved; what changed is the claim made about them.
- **"Nothing was checked" is never a pass.** Readiness answers `NotApplicable`, with its own sentence, when no
  ingredient line matched a platform ingredient — the same distinction the evaluator already drew for
  attribution. An empty gap list meant both "every record is complete" and "nobody looked".
- **An approved trait may cite only an approved source.** `TraitPolicy.MayBeApproved` states the rule the schema
  could not: the approved list lives in code because approving a source is a recorded decision, not a row.
  Nothing writes an approved trait today, which is why B-33 held — by absence rather than by a rule.
- **A model's own knowledge cannot carry high confidence for an allergen or dietary consequence.** Culinary
  guidance still can: how a swap tastes is what a model is for, and asking it to hedge everything would make the
  field meaningless.
- **14.5's warnings get a route and a screen** — `GET .../analysis/food-safety` and the editor's Safety checks
  tool. They were computed, disclaimed, guard-tested and seen by nobody. Still nothing blocks: no readiness rule
  reads them, as B-37 decided.
- **A refusal says which rule refused it.** `TraitCountedReason` is on every piece of cited evidence, so a
  screen no longer has to work out from a source's kind and its active flag why a reassuring word did not
  count. That derivation was a copy of `MayReassure` in the browser; the three reasons are kept apart because
  they are different — a source that does not say, a kind that may not reassure, and one that is retired.
- **The analyzer enforces its own effective date.** It took `evaluatedOn`, recorded it on the answer and
  compared nothing with it, so any second read path would have produced findings from records not yet in effect.
  Approval stays the repository's, since the service model carries no review status to re-check.

**Options considered:** pointing the existing editorial claim scanner at the recipe capabilities (rejected
above); rejecting an absence claim in a warning too (rejected — "cannot be made nut-free" is the honest
limitation, and refusing the word would teach the model to be vaguer); making `publishable` block approval
(rejected — a product decision nobody has taken, and B-39 left the gate exposed and unread); approving a
food-safety reference to close the verification-required wording (refused — the fabrication this task's
RESTRICTION forbids); `Calculator` as a forbidden cross-module suffix (rejected — it forbade Measurement's
deliberately shared conversion calculators, so the three Nutrition analyzers are named individually).
**Consequences:** `unresolved.notQuantified` and the food-safety route are additive OpenAPI changes; both
estimate goldens were regenerated and show only the reclassified line, the new group and USDA's refreshed
citation. The two tests that pinned the blockers now pin the refusals. `npm test` gained
`scripts/check-safety-copy.mjs`, which scans both halves of the three safety panels. Still open, with reasons,
in the audit document: `Manufacturer` asserting an allergen absence (needs a migration), and a mapping's match
confidence being absent from an estimate line (needs a server field).
**Rules:** `ai.md`, `recipes.md`, `tenancy.md`, `backend.md`, `api-contract.md`, `frontend.md`.

## Open items

### B-12 Unmapped DEC items — DECIDE

- **Question:** the microprompts reference twelve decisions (DEC-001–DEC-012), but only ten topics
  are named for this baseline and only DEC-010 has a known topic. At least one DEC is unrecorded.
- **Options:** (a) add the requirements document and reconcile; (b) the team restates the missing
  decisions directly in this record.
- **Impact:** a missing decision may affect any phase; the owning feature phase is unknown.
- **Default assumed by later prompts:** no additional decision beyond B-01–B-11. A prompt that meets an
  unrecorded product choice stops and asks instead of choosing.
