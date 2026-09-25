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
| B-17 | AI proposal boundaries | TBD | Decided | Proposal is write-once; per-change disposition on the change row; one execution row per provider attempt, owned by the operation; structured change targets, not path strings. |
| B-18 | Model output contract | TBD | Decided | The C# document type is the schema, exported for the prompt with `JsonSchemaExporter`; validated in five stages; no repair; an empty proposal is a valid answer. |
| B-19 | Prompt context envelope | TBD | Decided | Instructions in the system role, data in the user role; nonce-fenced segments; trust fixed by segment kind; every non-instruction segment stamped with its workspace and checked. |
| B-20 | Model execution wrapper | TBD | Decided | Resilience pipeline in ServiceDefaults with the transience predicate supplied by the host; classification behind a provider-implemented interface; one attempt record per call; exactly one corrective re-ask for a schema failure. |
| B-21 | Proposal disposition and atomic acceptance | TBD | Decided | A proposal may only offer a change the recipe patch can apply; accepted changes travel the recipe module's own merge path; the confirmation names the accepted change ids; one explicit transaction in the AI data layer spans both modules; replay compares the decision rather than a key. |

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
`AiDiffFields` and `AiChangeApplicability` for when `UpdateRecipeViewModel` grows an ingredients field and
`AiStructuredChange` can carry a child payload.
**Rules:** `ai.md`, `recipes.md`, `backend.md`, `api-contract.md`.

## Open items

### B-12 Unmapped DEC items — DECIDE

- **Question:** the microprompts reference twelve decisions (DEC-001–DEC-012), but only ten topics
  are named for this baseline and only DEC-010 has a known topic. At least one DEC is unrecorded.
- **Options:** (a) add the requirements document and reconcile; (b) the team restates the missing
  decisions directly in this record.
- **Impact:** a missing decision may affect any phase; the owning feature phase is unknown.
- **Default assumed by later prompts:** no additional decision beyond B-01–B-11. A prompt that meets an
  unrecorded product choice stops and asks instead of choosing.
