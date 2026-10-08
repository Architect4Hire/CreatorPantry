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
  disagree on how one is expressed and reject each other's fields.
- **A flagged picture is a refusal.** With safe mode on Venice answers 200 with a blurred image and a header;
  that is reported as `provider-refused` rather than staged.
- **This client opts out of the ServiceDefaults resilience handler.** Its ten-second attempt timeout is
  shorter than a generation and its retries would each be paid for; the gateway's timeout and the
  operation-level requeue are the only bounds.
- **Failure mapping is unchanged**, and now needs no translation: a non-success status is thrown as
  `HttpRequestException` with the status and none of Venice's text. 402 (no balance) is `provider-refused`.

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

## Open items

### B-12 Unmapped DEC items — DECIDE

- **Question:** the microprompts reference twelve decisions (DEC-001–DEC-012), but only ten topics
  are named for this baseline and only DEC-010 has a known topic. At least one DEC is unrecorded.
- **Options:** (a) add the requirements document and reconcile; (b) the team restates the missing
  decisions directly in this record.
- **Impact:** a missing decision may affect any phase; the owning feature phase is unknown.
- **Default assumed by later prompts:** no additional decision beyond B-01–B-11. A prompt that meets an
  unrecorded product choice stops and asks instead of choosing.
