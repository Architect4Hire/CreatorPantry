# CreatorPantry — AI Pricing, Allowance and Billing SCRUB Prompts

Atomic prompts that close CreatorPantry's AI cost exposure, then add plans, prepaid credit packs and Stripe
billing on top of the allowance system Phase 9A already built. For use with Claude Code and the
CreatorPantry `.claude/` toolkit. Intended path in the repo: `docs/prompts/creatorpantry-ai-pricing-scrub-prompts.md`.

This document supplements `docs/prompts/creatorpantry-scrub-microprompts.md` (the "master library"). It does
not redefine anything decided in `CLAUDE.md` or `.claude/rules/`, except where AP.1.1 amends `CLAUDE.md`'s
out-of-bounds list on the owner's explicit request. The pricing analysis behind it is
`creatorpantry-ai-pricing.md` (project files, 2026-10-24); AP.1.1 copies the parts it needs into the repo.

## Reusable SCRUB skeleton

```text
SCOPE:        one observable change and the exact repository seam it touches
CONSTRAINT:   stack, rule files, skills, requirement IDs, and prerequisites
RESTRICTION:  explicit exclusions and invariants that must not be weakened
UTILIZATION:  skills, read-only reviewers, and tools to invoke
BEHAVIOR:     inspect, plan, wait for approval, implement, test, and report
```

Run one prompt at a time, in order, in a clean context where practical. Mark a prompt `- done` only after
its stated verification passes, and add a delivery note under it when what shipped differs from what was
asked.

## What this answers

The owner asked on 2026-10-24 for "a pricing structure and overage AI structure so I don't lose money on
image and/or content creation", and for a faster image model.

| ID | Requirement | Phase |
| --- | --- | --- |
| PRICE-001 | Every provider call that costs money, text or image, is charged against the account's allowance before it runs. | 2 |
| PRICE-002 | Credits are a pinned unit: 1 credit = $0.001 of provider list cost × 1.25. Rates are configuration, validated at startup. | 2 |
| PRICE-003 | Image requests name their quality and resolution explicitly; nothing is left to a provider default. | 2 |
| PRICE-004 | A creator sees what an image request will cost, in credits, before submitting it. | 2 |
| PRICE-005 | A platform-wide daily spend ceiling stops new image admissions and alerts the operator. | 2 |
| PRICE-006 | Images come in quality classes (draft, standard, high, high-2K, high-4K), each mapped to a configured model and setting. | 3 |
| PRICE-007 | An account has exactly one plan at a time; the plan sets the allowance, daily image cap, image concurrency and allowed quality classes. | 4 |
| PRICE-008 | Prepaid credit packs add to the allowance, are spent after it, and expire 12 months after purchase. | 5 |
| PRICE-009 | There is no post-paid overage. An account at zero is refused at admission, as USAGE-006 already does. | 5 |
| PRICE-010 | Subscriptions and packs are bought through Stripe; webhooks are verified and idempotent. | 6 |
| PRICE-011 | Auto top-up is opt-in, buys the smallest pack, and is capped per month by the creator. | 6 |
| PRICE-012 | Trial accounts cannot generate unbounded images. | 7 |

## Decisions already made

1. **Billing, plans and metering are now in scope.** `CLAUDE.md` lists them as out of bounds "unless
   explicitly requested"; the owner requested them on 2026-10-24. AP.1.1 records that.
2. **Images are the cost, text is not.** A text action on `gpt-4o-mini` costs under $0.01; one image at
   today's settings costs $0.07. Phase 2 is ordered so the image leak closes first and needs no billing.
3. **Prepaid only.** No metered overage invoiced after the fact.
4. **Allowance stays per Identity account** (USAGE-001), across every workspace the account works in. A plan
   attaches to the account, not to a workspace.
5. **The trial is 14 days.** Owner, 2026-10-24, answering LP.2. The trial gate (card on file or a picture cap)
   is still open below.

## Proposed, awaiting the owner

These are the analysis's recommendations. AP.1.1 writes them as **proposed**; no prompt after AP.1.1 may
hard-code them. They live in configuration so the owner can change them without a code change.

| Item | Proposed value |
| --- | --- |
| Plans (monthly / annual per month) | Trial free 14 days · Creator $19 / $15 · Pro $49 / $39 · Studio $99 / $79 |
| Credits per period | Trial 1,000 once · Creator 6,000 · Pro 18,000 · Studio 40,000 |
| Images per UTC day | 5 · 25 · 75 · 150 |
| Images running at once | 1 · 1 · 2 · 3 |
| Quality classes | Trial: draft, standard · Creator: + high · Pro: + high-2K · Studio: + high-4K |
| Image credits | draft 25 · standard 50 · high 90 · high-2K 190 · high-4K 290 |
| Packs | 2,500 for $10 · 7,500 for $25 · 20,000 for $60 |
| Carry-over of plan credits | None (the current default) |
| Draft model | `z-image-turbo` or `qwen-image-2-1-turbo` (Venice) |
| Standard/high model | `gpt-image-2-5-flare` medium/high until the AP.3.3 bake-off picks one |

Still open, to be answered in AP.1.1 or before the prompt that needs it:

- Sales tax / VAT: Stripe Tax or not (AP.6.1).
- Trial gate: card on file, or a lifetime cap of 10 trial images (AP.7.1).
- Whether images move to Azure Foundry (FLUX.2-pro), which makes AP.3.4 run or skip.
- Downgrade timing: at period end (proposed) or immediately (AP.6.2).

## How this list was built

A code map taken on 2026-10-24 (`main`, after `a9c3d34`). Re-find files before relying on a line number.

| Area | What exists | The gap |
| --- | --- | --- |
| Allowance | `Modules/AiUsage`: quota, periods, reservations, settlement, carry-over, ops routes (USAGE-001..010), `features/ai-usage` in the web. | Built around `AiOperation`. Nothing outside the Ai module is admitted against it. |
| Text rates | `AiQuotaOptions.ModelRates`, `AiCostOptions.Models`, `AiQuotaUnitConverter`. | Both dictionaries are empty in every `appsettings*.json`, so every call settles at the flat 10-credit estimate and cost reports read null. |
| Output bound | `AiGatewayOptions.MaxSchemaCorrections = 1`. | No `MaxOutputTokens` on any chat call. |
| Images | `GeneratedImageOperation` (Media), `IGeneratedImageGenerationBusiness`, `GeneratedImageProviderGateway`, `VeniceImageGenerator`. | **No allowance check** (12.10l "Not done"). Bounded only by `ImageVariantsPerWorkspacePerDay = 200`, a token bucket and `ImageRunningPerWorkspace = 2`. Worst case about $14 a workspace a day. |
| Venice request | `GenerateRequest`: model, prompt, png, no watermark, safe mode. | No `quality` or `resolution`, so `gpt-image-2-5-flare` runs at its `high` 1K default ($0.07). |
| Plans and billing | Nothing. | No plan, pack, Stripe, or webhook code. |

## Prerequisites already satisfied

Reuse these; do not rebuild them:

- `IAiQuotaAdmissionFacade` (`AdmitAsync`, `PeekAsync`, `StageSettlementAsync`, `PostAsync`, `RenewAsync`),
  `AccountAiQuota`, `AccountAiQuotaPeriod`, `AccountAiQuotaReservation`, `AccountAiUsageEntry`, and the
  quota maintenance and reconciliation hosted services.
- The quota problem family (`QuotaProblemResultsTests`) and the USAGE-010 states in `features/ai-usage`.
- `LimitsOptions`, `LimitedOperation`, `LimitRefusals` and the Redis token buckets.
- `IdempotentCommandExecutor`, the outbox, `IPlatformAuditWriter`.
- `ImageGenerationOptions.ModelId`, which already overrides the configured image model per request.
- The Live check pattern in `src/CreatorPantry.Tests/Ai/Live/`.

## Relationship to the master library

| Master prompt | Effect of this document |
| --- | --- |
| 12.10l "Not done": image allowance charge | **Delivered by AP.2.4–AP.2.6.** |
| 9A.* allowance prompts | Unchanged. This document extends their ledger and admission; it does not replace them. |
| `CLAUDE.md` out-of-bounds "Billing, plans, or metering" | **Amended by AP.1.1.** |

## Out of bounds for this document

- Per-seat pricing and team billing. A plan is per account; workspace membership is unchanged.
- Usage-based invoicing of any kind.
- PantryPress billing. PantryPress has its own Stripe integration and no AI.
- Moving chat or embeddings off Foundry.

## Credentials and external prerequisites

Stripe test-mode keys and a webhook signing secret (Phase 6), stored as Aspire secret parameters. A Venice
key with the draft model enabled for AP.3.3. For AP.3.4 only, a Foundry resource with a FLUX.2-pro deployment
and an approved quota.

## Checkpoints

| After | Demonstrable result |
| --- | --- |
| **AP.2.6** | An image request is refused when the account cannot afford it, shows its cost before submit, and lands in the usage ledger and the creator's usage page. No provider call happens without a reservation. |
| **AP.3.3** | A draft image arrives in seconds for a fraction of a standard one, and a bake-off report names the standard/high model with measured latency and cost. |
| **AP.4.4** | Changing an account's plan from ops changes its allowance at the next period, its daily image cap and its allowed quality classes immediately. |
| **AP.5.3** | A granted pack is spent after the period allowance and before refusal, and expires on time. |
| **AP.6.5** | A creator subscribes, buys a pack and manages the subscription in Stripe test mode end to end, and a replayed webhook changes nothing. |
| **AP.7.2** | The audit passes with no open blocker. |

---

# Phase 1 — Record the decision

## AP.1.1 Pricing decision record, CLAUDE.md scope and master-library pointer

```text
SCOPE: Documentation only. Add docs/architecture-decisions/ai-pricing.md recording "Decisions already
made", the PRICE requirement table, the pinned credit unit and the "Proposed, awaiting the owner" table
marked proposed, using the next free B-number(s). In CLAUDE.md, change "Billing, plans, or metering" under
"Out of bounds unless explicitly requested" to point at the new record. In the master library, add a
one-line pointer under 12.10l's "Not done" note to AP.2.4–AP.2.6, and move the "pick up here" marker.
Add this document under docs/prompts/.
CONSTRAINT: docs/architecture-decisions/baseline.md for record format and numbering; .claude/rules/ai.md.
RESTRICTION: Do not record a proposed value as decided. Do not delete master prompts. No code.
UTILIZATION: architecture-reviewer (read-only) on the record.
BEHAVIOR: Show the record outline and every edit to CLAUDE.md and the master library, ask the owner the
"Still open" questions, wait for approval, write them, and report any conflict with an existing decision.
```

---

# Phase 2 — Close the leak (no billing needed)

## AP.2.1 Text model rates in configuration, validated

```text
SCOPE: Configure Ai:Quota:ModelRates and Ai:Cost:Models for every chat and embedding model the deployment
can name (gpt-4o-mini, gpt-4.1-mini, gpt-5-mini, text-embedding-3-small, and the Foundry Local dev models),
using the pinned unit (credits per 1M tokens = list $ × 1,250). Add options validation that fails startup
outside Development when the configured chat or embedding deployment's model has no rate.
CONSTRAINT: PRICE-002, USAGE-004/005; AiQuotaOptions, AiCostOptions, AiQuotaUnitConverter;
.claude/rules/ai.md and aspire.md. API and Worker carry identical values (they must agree, per the
appsettings comments).
RESTRICTION: No price literal in C#. A missing rate keeps settling at the estimate, never at zero. Do not
change the DefaultTaskEstimate semantics.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the rate table with its source and date, and the validation rule; wait for approval;
implement; test conversion for each model, the unknown-model path, and the startup failure; report the
before/after settlement of one recorded operation.
```

## AP.2.2 Output-token ceiling per AI task

```text
SCOPE: Add a per-task MaxOutputTokens (AiTaskOptions or AiGatewayOptions, whichever already owns per-task
settings) and pass it on every IChatClient call through the completion gateway. Defaults: 4,000; long-form
outline and section 8,000. A response cut off by the ceiling is a schema failure handled by the existing
correction path, not a success.
CONSTRAINT: PRICE-001; AiCompletionGateway; .claude/rules/ai.md; add-ai-capability skill.
RESTRICTION: No per-call override from the request body. The quota estimate per call must stay at or above
the ceiling's worst-case credits; add a startup check for that.
UTILIZATION: ai-safety-reviewer after implementation.
BEHAVIOR: Show where the value lives and the estimate check; wait for approval; implement; run the
evaluation sets for long-form and channel posts and report any case the ceiling now truncates.
```

## AP.2.3 Venice requests name quality and resolution

```text
SCOPE: VeniceImageGenerator sends quality and resolution (and aspect_ratio, or width/height for pixel-sized
models) from ImageGenerationOptions instead of leaving the model's defaults in force. Add a per-model sizing
profile in configuration (Venice:Models:<id>: sizing kind, allowed qualities, allowed resolutions, prompt
character limit) so a field a model rejects is never sent. Default for the configured model: medium, 1K.
CONSTRAINT: PRICE-003; docs/architecture-decisions/ai-fluency.md §(a) and baseline.md's Venice amendment;
.claude/rules/external.md and media.md; add-external-integration skill.
RESTRICTION: Stays PNG, no watermark, safe mode on. Provenance records the model, quality and resolution
actually sent. A model with no profile is refused at startup, not guessed. Do not log the prompt.
UTILIZATION: integration-reviewer after implementation.
BEHAVIOR: Show the profile shape and the request mapping for each sizing kind; wait for approval; implement
with VeniceImageGeneratorTests for each kind, an unknown model, and an over-limit prompt; amend the ADR.
```

## AP.2.4 Image credit rates

```text
SCOPE: Add Ai:Quota:ImageRates — credits per image keyed by model, quality and resolution — and the matching
Ai:Cost image prices, with a pure converter beside AiQuotaUnitConverter. Validate at startup that every
quality class the deployment can request has a rate.
CONSTRAINT: PRICE-001/002; AP.2.3; AiQuotaOptions; .claude/rules/ai.md (quota enforcement is code).
RESTRICTION: Rates are configuration. An image with no rate is refused at admission, never free.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the table and validation; wait for approval; implement with tests for each key and the
missing-rate refusal.
```

## AP.2.5 Admit and settle image generation against the allowance

```text
SCOPE: Before a GeneratedImageOperation is queued, reserve variantCount × the image rate through
IAiQuotaAdmissionFacade for the requesting account; refuse with the existing quota problem family when it
cannot be afforded. The image worker settles per variant actually sent to the provider, charges a variant
Venice flagged or blurred (Venice bills it), and releases the hold for a variant whose provider call failed
before generation (provider-unavailable, rate-limited, 5xx). Extend AccountAiUsageEntry so a ledger row can
name an image operation as well as an AiOperation, with a migration.
CONSTRAINT: PRICE-001, USAGE-001/002/004/006/007; IGeneratedImageGenerationBusiness,
GeneratedImageProviderGateway, the image worker; .claude/rules/tenancy.md, media.md, backend.md;
add-media-feature skill. Memory: check the migration against a SQL Server fixture for multiple cascade paths.
RESTRICTION: Media reaches AiUsage only through its facade. The ledger stays counts-only with no prompt or
image content (USAGE-002). The reservation and the operation commit together or not at all. The existing
daily cap, token bucket and running limits stay as an additional bound. A resumed pass never charges a
variant twice.
UTILIZATION: workspace-isolation-auditor, test-gap-analyzer and integration-reviewer after implementation.
BEHAVIOR: Show the ledger change, the reservation/settlement sequence for success, partial success, flagged,
provider failure and resume; wait for approval; implement; extend ImagesJourneyEndToEndTests and
CreativePipelineEndToEndTests with an over-allowance refusal, a partial pass, and two-account isolation.
```

## AP.2.6 Image cost in the web and the usage page

```text
SCOPE: Image Studio and the Content Pipeline image step show the credit cost of the request (variants ×
class rate, from a new read-only quote endpoint) and the remaining allowance before submit, with the
USAGE-010 warning and exhausted states. The usage page breaks out images from text.
CONSTRAINT: PRICE-004, USAGE-008/010; features/ai-usage, cp-generated-image-run; .claude/rules/frontend.md,
design-system.md, api-contract.md; add-endpoint, new-component and creatorpantry-design-system skills.
RESTRICTION: The browser does no pricing arithmetic; it renders the server's quote. An exhausted state
never discards the filled-in request. Components do not inject HttpClient.
UTILIZATION: design-review and api-contract-checker after implementation.
BEHAVIOR: Show the quote route, ViewModels and the three states; wait for approval; implement with specs for
each state and keyboard operation; regenerate the OpenAPI snapshot and read the diff.
```

## AP.2.7 Platform image spend circuit breaker

```text
SCOPE: A platform-wide daily ceiling on estimated image spend (Ai:Quota:PlatformImageSpendPerDay, in
dollars). When admissions would pass it, new image requests are refused with a distinct, non-disclosing
problem code, a platform audit event is written, and a structured log/metric fires for alerting. Ops can
read the day's total and lift the breaker for the day.
CONSTRAINT: PRICE-005; OpsAiUsageController, IPlatformAuditWriter; .claude/rules/auth.md; add-endpoint skill.
RESTRICTION: The counter is atomic across API and Worker instances (Redis or SQL, not memory). It never
reveals another account's usage to a creator.
UTILIZATION: architecture-reviewer and test-gap-analyzer after implementation.
BEHAVIOR: Show the counter, the refusal and the ops routes; wait for approval; implement with a concurrency
test that two instances cannot both pass the ceiling.
```

---

# Phase 3 — Draft and final image engines

## AP.3.1 Quality classes on the image request

```text
SCOPE: Add a quality class to the image request (draft, standard, high, high-2K, high-4K). Configuration maps
each class to a model id, quality and resolution (Ai:Images:Classes). The class flows through the Media
seam to ImageGenerationOptions, so draft and standard can use different models.
CONSTRAINT: PRICE-006; AP.2.3, AP.2.4; .claude/rules/media.md, api-contract.md; add-media-feature skill.
RESTRICTION: The request names a class, never a model id. Provenance records the model actually used. The
idempotency key covers the class (12.10l rule: a key names one request).
UTILIZATION: api-contract-checker after implementation.
BEHAVIOR: Show the class mapping and wire change; wait for approval; implement with tests per class and a
reused-key-different-class 422.
```

## AP.3.2 Compact prompt for short-limit models

```text
SCOPE: When a class's model has a prompt character limit below the composed prompt, the image.prompt task
produces a compact variant within the limit (a new prompt template version with its evaluation set), and
the request uses it. A prompt that still does not fit is refused with a clear error, never truncated.
CONSTRAINT: AP.2.3 profiles; image.prompt-1.2.0; .claude/rules/ai.md; add-ai-capability skill.
RESTRICTION: Truncation is never silent. The creator's avoid list survives compaction or the request is
refused. Untrusted-content delimiting is unchanged.
UTILIZATION: ai-safety-reviewer after implementation.
BEHAVIOR: Show the template change and fit rule; wait for approval; implement; run the evaluation set and
report how often compaction drops creator detail.
```

## AP.3.3 Image model bake-off (live check)

```text
SCOPE: A Live check like LivePhotographyConceptCheck that runs 20 fixed CreatorPantry food prompts through
each candidate model (z-image-turbo, qwen-image-2-1-turbo, seedream-v5-lite, flux-2-pro,
gpt-image-2-5-flare medium and high) and writes a report: latency p50/p95, cost per image, refusals, and the
images for side-by-side review. Not part of CI.
CONSTRAINT: PRICE-006; src/CreatorPantry.Tests/Ai/Live/; Venice key.
RESTRICTION: Costs real money: print the estimated total and require an explicit opt-in flag before
running. Images go to a local folder, never to storage.
UTILIZATION: none.
BEHAVIOR: Show the prompt set and estimated cost; wait for approval; run; present the report and wait for
the owner to pick each class's model; then set Ai:Images:Classes accordingly.
```

## AP.3.4 Azure Foundry FLUX.2-pro image generator - conditional

*Run only if the owner decides to move images to Azure (see "Still open"). Skip otherwise.*

```text
SCOPE: A second IImageGenerator for Foundry FLUX.2-pro over the Black Forest Labs provider route
(<resource>/providers/blackforestlabs/v1/flux-2-pro), selectable per quality class, with failure mapping
matching VeniceImageGenerator's (429 rate-limited, other 4xx provider-refused, 408/5xx provider-unavailable).
CONSTRAINT: baseline.md's images amendments; .claude/rules/external.md and aspire.md;
add-external-integration and add-aspire-resource skills.
RESTRICTION: Behind IImageGenerator; no Media change. Respect Retry-After. The deployment's RPM quota is
configuration and feeds ImageRunningPerWorkspace limits. No SDK type leaves CreatorPantry.AiProvider.
UTILIZATION: integration-reviewer after implementation.
BEHAVIOR: Read the current Foundry docs first and report the request shape, limits and price; wait for
approval; implement with generator tests; amend the ADR.
```

---

# Phase 4 — Plans

## AP.4.1 Plan catalogue and AccountPlan

```text
SCOPE: A plan catalogue in configuration (Billing:Plans: key, display name, allowance, period, daily image
cap, running images, allowed quality classes, trial flag and length) and a platform-scoped AccountPlan
entity (account id, plan key, source: ops | stripe | trial, effective from/to, row version), in a new
Billing module, with a migration.
CONSTRAINT: PRICE-007; .claude/rules/tenancy.md (platform-scoped, like the quota tables), backend.md;
add-workspace-entity skill for conventions only.
RESTRICTION: No Stripe field on AccountPlan; provider references live in integration records (Phase 6).
One effective plan per account at a time, enforced by the schema. Accounts with no row read as the default
plan from configuration.
UTILIZATION: architecture-reviewer and workspace-isolation-auditor after implementation.
BEHAVIOR: Show the module, entity, constraints and catalogue shape; wait for approval; implement; test
overlap refusal, default resolution and concurrency.
```

## AP.4.2 Plan drives the allowance and image limits

```text
SCOPE: The quota period resolver takes the allowance from the account's plan when no explicit
AccountAiQuota override exists; a plan change takes effect at the next period roll. Daily image cap and
running images become plan-dependent, replacing the flat LimitsOptions values for accounts with a plan.
CONSTRAINT: PRICE-007, USAGE-003; IAiQuotaPeriodResolver, LimitsOptions; Billing reached by facade only.
RESTRICTION: An ops-set AccountAiQuota still wins (USAGE-009). The running period's allowance is never
rewritten mid-period. Limits are per account across workspaces where the daily cap is concerned; document
how that interacts with the existing per-workspace cap.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show precedence rules and the period-roll behaviour; wait for approval; implement with tests for
override precedence, mid-period change, and each limit.
```

## AP.4.3 Quality classes gated by plan

```text
SCOPE: Admission refuses a quality class the account's plan does not allow, with a stable problem code
that names the plan that would allow it. The web shows disallowed classes as locked with that reason.
CONSTRAINT: PRICE-006/007; AP.3.1, AP.4.1; .claude/rules/api-contract.md, design-system.md.
RESTRICTION: The check is server-side; hiding in the browser is presentation only.
UTILIZATION: api-contract-checker and design-review after implementation.
BEHAVIOR: Show the problem code and locked state; wait for approval; implement with tests per plan.
```

## AP.4.4 Ops: set an account's plan

```text
SCOPE: Extend the ops AI-usage routes so a platform administrator can read and set an account's plan, with
an audit event per change, and list accounts by plan.
CONSTRAINT: PRICE-007, USAGE-009; baseline B-13/B-14; OpsAiUsageController, IPlatformAuditWriter.
RESTRICTION: Ops scopes only; a creator cannot set their own plan through this route.
UTILIZATION: api-contract-checker after implementation.
BEHAVIOR: Show routes and audit shape; wait for approval; implement with authorization tests.
```

---

# Phase 5 — Prepaid credit packs

## AP.5.1 Credit grant ledger

```text
SCOPE: A platform-scoped AccountCreditGrant (account id, credits granted, credits remaining, source:
purchase | ops | promo, external reference id, granted at, expires at, row version) with a migration, and
an ops route to grant credits with an audit event.
CONSTRAINT: PRICE-008; Billing module; .claude/rules/tenancy.md.
RESTRICTION: Remaining never goes below zero (CHECK constraint). A grant is never deleted; expiry and
claw-back are recorded state changes.
UTILIZATION: architecture-reviewer after implementation.
BEHAVIOR: Show entity and constraints; wait for approval; implement; test expiry and concurrency.
```

## AP.5.2 Admission spends allowance, then grants

```text
SCOPE: Admission reserves from the period allowance first, then from unexpired grants ordered by soonest
expiry; settlement and release return credits to where they came from. Refusal (USAGE-006/007) reports
allowance and pack balance separately.
CONSTRAINT: PRICE-008/009; IAiQuotaAdmissionFacade, AccountAiQuotaReservation; AiUsage reaches Billing
through a facade.
RESTRICTION: A reservation split across allowance and grants commits atomically. Reconciliation of abandoned
reservations releases to the right source. No negative balance anywhere.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the split and return rules; wait for approval; implement with tests for a split reservation,
expiry during a hold, release after abandonment, and two concurrent admissions on the last credits.
```

## AP.5.3 Plan and pack balance in the usage page

```text
SCOPE: /me/ai-usage and the usage page show the plan, period allowance used/remaining, pack balance with
the soonest expiry, and the reset date.
CONSTRAINT: PRICE-007/008, USAGE-008/010; MeController; features/ai-usage.
RESTRICTION: Server-computed figures only. Plain creator language, no token counts.
UTILIZATION: design-review and api-contract-checker after implementation.
BEHAVIOR: Show the ServiceModel change and layout; wait for approval; implement with specs.
```

---

# Phase 6 — Stripe

## AP.6.1 Stripe integration decision and gateway

```text
SCOPE: Read Stripe's current first-party docs (Checkout, Billing, Customer Portal, webhooks, Stripe Tax)
and record the integration decision in ai-pricing.md: Checkout for subscriptions and packs, the Customer
Portal for management, the event list handled, and the tax answer. Then add the typed Stripe gateway
behind an application-owned interface, a BillingCustomer integration record (account id ↔ Stripe customer
id), the configuration mapping Stripe price ids to plan and pack keys, and the Aspire secret parameters.
CONSTRAINT: PRICE-010; .claude/rules/external.md (gateway, secrets, webhooks), aspire.md;
add-external-integration and add-aspire-resource skills.
RESTRICTION: No Stripe type outside the gateway. Keys are secrets, never logged or returned. Test mode only
until the owner says otherwise.
UTILIZATION: integration-reviewer after implementation.
BEHAVIOR: Present the doc review and decision; wait for approval; implement the gateway with tests against
recorded fixtures.
```

## AP.6.2 Webhook ingress and subscription lifecycle

```text
SCOPE: A public webhook route through CreatorPantry.Gateway to the API that verifies the Stripe signature
before parsing, stores the event id, and processes idempotently through the outbox. Handle subscription
created / updated / deleted and invoice paid / payment_failed: set AccountPlan (source stripe), apply
upgrades immediately and downgrades at period end (unless AP.1.1 decided otherwise), and drop to the free
or lapsed plan on cancellation.
CONSTRAINT: PRICE-010; .claude/rules/gateway.md and external.md; add-endpoint and add-background-job skills.
RESTRICTION: The webhook route takes no browser session and accepts nothing unsigned; it gets its own size
limit and rate limit at the edge. A replayed or out-of-order event changes nothing it should not.
UTILIZATION: integration-reviewer, architecture-reviewer and test-gap-analyzer after implementation.
BEHAVIOR: Show the route, the event table and the state transitions; wait for approval; implement with
tests for bad signature, replay, out-of-order delivery, upgrade, downgrade and failed payment.
```

## AP.6.3 Pack purchase, refund and chargeback

```text
SCOPE: checkout.session.completed for a pack creates an AccountCreditGrant (expiry 12 months). A refund or
dispute claws back the unspent part of that grant and records it; spent credits are not recovered from
the allowance.
CONSTRAINT: PRICE-008/010; AP.5.1, AP.6.2.
RESTRICTION: One grant per checkout session, enforced by the external reference id. Claw-back never makes a
balance negative.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the mapping; wait for approval; implement with replay and partial-spend refund tests.
```

## AP.6.4 Opt-in auto top-up

```text
SCOPE: An account setting for auto top-up (off by default, monthly maximum chosen by the creator, default
2). When admission leaves the combined balance under 10% of the period allowance, a background job
charges the saved payment method for the smallest pack through Stripe, at most the monthly maximum times.
CONSTRAINT: PRICE-011; AP.6.3; add-background-job skill.
RESTRICTION: Never charges without the setting on and a saved payment method. A failed charge turns the
setting off and notifies the creator. Admission never waits on a charge.
UTILIZATION: integration-reviewer after implementation.
BEHAVIOR: Show the trigger and limits; wait for approval; implement with tests for the cap and failure path.
```

## AP.6.5 Billing pages in the web

```text
SCOPE: A Billing page under account settings: current plan, plan comparison from the catalogue, upgrade via
Checkout, buy packs, auto top-up setting, and a button to the Stripe Customer Portal. A creator at zero
sees "buy credits" and "upgrade" in the exhausted state.
CONSTRAINT: PRICE-007..011, USAGE-010; .claude/rules/frontend.md, design-system.md; new-component and
creatorpantry-design-system skills.
RESTRICTION: Prices shown come from the server's catalogue, which reads Stripe price ids; nothing is
hard-coded in the web. Returning from Checkout shows a pending state until the webhook lands.
UTILIZATION: design-review after implementation; playwright-cli for an end-to-end run in test mode.
BEHAVIOR: Show the layout and states; wait for approval; implement with specs and one Playwright journey.
```

---

# Phase 7 — Trial and audit

## AP.7.1 Trial limits

```text
SCOPE: Apply the trial gate the owner chose in AP.1.1: either a card on file before trial images, or a
lifetime cap on trial images per account (proposed 10). Trial allowance is granted once, not per period.
CONSTRAINT: PRICE-012; AP.4.1.
RESTRICTION: The cap is per Identity account and survives workspace creation and deletion.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the rule; wait for approval; implement with tests including a second workspace.
```

## AP.7.2 Pricing and billing audit

```text
SCOPE: Audit everything this document delivered: no provider call without a reservation, every rate
configured, no negative balances, webhook safety, and isolation between accounts and workspaces.
CONSTRAINT: PRICE-001..012, USAGE-001..010.
RESTRICTION: Report first; no unrelated redesign.
UTILIZATION: architecture-reviewer, api-contract-checker, workspace-isolation-auditor, integration-reviewer,
ai-safety-reviewer, design-review and test-gap-analyzer.
BEHAVIOR: Return findings, wait for approval, fix blockers, rerun focused and end-to-end tests, and add a
delivery note here.
```
