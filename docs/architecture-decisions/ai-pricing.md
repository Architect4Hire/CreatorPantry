# AI Pricing Decisions

*Status: Accepted — 2026-10-26, except B-51, which is **proposed, not decided**. Recorded by AI-pricing prompt
AP.1.1.*

This record fixes the product decisions behind CreatorPantry's AI pricing — closing the image cost leak, then
plans, prepaid credit packs and Stripe billing on top of the allowance Phase 9A built — so the AP prompts build
on a stated baseline instead of an assumption. It does not implement code, and it records nothing the AP prompt
document or the product owner has not stated.

Identifiers start at **B-44**, one past the highest in use (B-43, in [`baseline.md`](baseline.md)). The sequence
has already forked: [`ai-fluency.md`](ai-fluency.md) (B-27–B-31) and [`long-form.md`](long-form.md) (B-32–B-38)
reuse numbers `baseline.md` gives to other decisions, so "the next number after long-form" (B-39) is taken too.
Within those two records some numbers also appear twice, as a decision and again as its open item.
Cite a decision in this record as `ai-pricing.md` B-nn. Do not renumber; later documents may reference them.

## Sources

- `docs/prompts/1.creatorpantry-ai-pricing-scrub-prompts.md` — the AP prompts, the PRICE requirement table, and
  the decisions recorded here.
- The pricing analysis `creatorpantry-ai-pricing.md` (project files, 2026-10-24). **Not in the repository**; the
  parts the AP prompts need are copied below.
- Product-owner request of 2026-10-24: "a pricing structure and overage AI structure so I don't lose money on
  image and/or content creation".
- Product-owner ruling of 2026-10-24, given during LP.8
  (`docs/prompts/creatorpantry-landing-pricing-scrub-prompts-extensions.md`): packs do not expire.
- Product-owner answers given 2026-10-26 to AP.1.1's open questions — B-50, B-52, B-53 and B-54 (downgrades),
  and the deferral of sales tax.
- `CLAUDE.md` and `.claude/rules/` — architectural source of truth. Nothing here redefines them; B-44 amends
  `CLAUDE.md`'s out-of-bounds list on the owner's explicit request.

## Requirements

| ID | Requirement | AP phase |
| --- | --- | --- |
| PRICE-001 | Every provider call that costs money, text or image, is charged against the account's allowance before it runs. | 2 |
| PRICE-002 | Credits are a pinned unit: 1 credit = $0.001 of provider list cost × 1.25. Rates are configuration, validated at startup. | 2 |
| PRICE-003 | Image requests name their quality and resolution explicitly; nothing is left to a provider default. | 2 |
| PRICE-004 | A creator sees what an image request will cost, in credits, before submitting it. | 2 |
| PRICE-005 | A platform-wide daily spend ceiling stops new image admissions and alerts the operator. | 2 |
| PRICE-006 | Images come in quality classes (draft, standard, high, high-2K, high-4K), each mapped to a configured model and setting. | 3 |
| PRICE-007 | An account has exactly one plan at a time; the plan sets the allowance, daily image cap, image concurrency and allowed quality classes. | 4 |
| PRICE-008 | Prepaid credit packs add to the allowance, are spent after it, and **do not expire** (amended by B-50; the AP document first said 12 months). | 5 |
| PRICE-009 | There is no post-paid overage. An account at zero is refused at admission, as USAGE-006 already does. | 5 |
| PRICE-010 | Subscriptions and packs are bought through Stripe; webhooks are verified and idempotent. | 6 |
| PRICE-011 | Auto top-up is opt-in, buys the smallest pack, and is capped per month by the creator. | 6 |
| PRICE-012 | Trial accounts cannot generate unbounded images. | 7 |

## Decision table

| ID | Topic | Requirement | Status | Decision |
| --- | --- | --- | --- | --- |
| B-44 | Billing, plans and metering are in scope | PRICE-001–012 | Decided | Requested by the owner 2026-10-24. Prepaid plans, credit packs and payment through Stripe are in; per-seat pricing, usage-based invoicing and PantryPress billing are not. |
| B-45 | Images are the cost, and close first | PRICE-001, 003, 005 | Decided | A text action costs under $0.01, a picture at today's settings $0.07. The image leak closes before any billing exists, and needs none. |
| B-46 | Prepaid only | PRICE-008, 009 | Decided | No metered overage invoiced after the fact. An account at zero is refused at admission. |
| B-47 | The allowance and the plan belong to the account | PRICE-007, USAGE-001 | Decided | Per Identity account, across every workspace it works in. A plan never attaches to a workspace. |
| B-48 | The trial is 14 days | PRICE-012 | Decided | Owner, 2026-10-24, answering LP.2. Its gate is B-52. |
| B-49 | The credit is a pinned unit | PRICE-002 | Decided | 1 credit = $0.001 of (provider list cost × 1.25), so credits = list $ × 1,250. Every rate is configuration, validated at startup; no price is a C# literal. |
| B-50 | Packs do not expire | PRICE-008 | Decided | Owner, 2026-10-24 (LP.8), confirmed 2026-10-26. Bought credits keep until spent. Amends PRICE-008's 12 months. |
| B-51 | Plan, pack, rate and model figures | PRICE-006, 007, 008 | **Proposed** | The pricing analysis's table, held in configuration so the owner can change it without a code change. No prompt may hard-code a figure. |
| B-52 | The trial gate is a lifetime picture cap | PRICE-012 | Decided | No card on file. A trial account may generate at most 10 pictures in all, per Identity account, beside the daily cap. |
| B-53 | Pictures stay on Venice | PRICE-006 | Decided | Images do not move to Azure Foundry. AP.3.4 (FLUX.2-pro on Foundry) is skipped. |
| B-54 | Downgrades take effect at period end | PRICE-007, 010 | Decided | A downgrade, a cancellation and a trial running out all take effect when the running period ends. How an upgrade lands is open. |

## Decisions

### B-44 Billing, plans and metering are in scope

- **`CLAUDE.md` listed billing, plans and metering as out of bounds "unless explicitly requested"; the owner
  requested them on 2026-10-24.** Its out-of-bounds line now points here.
- **In:** plans, the plan catalogue, prepaid credit packs, picture and text metering against the allowance,
  payment through Stripe, opt-in auto top-up. Which Stripe products (Checkout, Billing, Customer Portal) is
  AP.6.1's decision, made after reading Stripe's current documentation (`external.md`).
- **Out:** per-seat pricing and team billing (a plan is per account; membership is unchanged), usage-based
  invoicing of any kind (B-46), PantryPress billing (it has its own Stripe integration and no AI), and moving
  chat or embeddings off Foundry.

**Consequences:** the Billing module LP.6 started (`Modules/Billing`, the catalogue only) is the home for plans,
grants and the Stripe integration records. AP.4.1's "new Billing module" now means extending that one. Setting a
plan or granting credits from ops (AP.4.4, AP.5.1) is a machine operation on an `ops` route (B-14) and an action
outside any workspace, so it writes `PlatformAuditLog` with a required reason (B-22), never `AuditLog`.
**Rules:** `CLAUDE.md` (Scope), `backend.md`, `external.md`, `auth.md`.

### B-45 Images are the cost, and close first

- **A text action on `gpt-4o-mini` costs under $0.01; one picture at today's settings costs $0.07.** Venice's
  `gpt-image-2-5-flare` runs at its `high`, 1K default because the request names neither quality nor
  resolution.
- **Phase 2 is ordered so the image leak closes first, and needs no billing:** explicit quality and resolution
  (AP.2.3), image credit rates (AP.2.4), admission and settlement against the existing allowance (AP.2.5), the
  cost shown before submit (AP.2.6), and a platform spend ceiling (AP.2.7).
- **The worst case today is about $14 a workspace a day** — and an account can own several workspaces — bounded
  only by `ImageVariantsPerWorkspacePerDay` (200), a token bucket, `ImageRunningPerWorkspace` (2) and
  `ImageInFlightPerWorkspace` (3). Those bounds stay as runaway limits after admission
  exists (AP.2.5).

**Consequences:** 12.10l's "Not done" item — no allowance check on image generation — is delivered by AP.2.4–
AP.2.6, narrowed for one kind of picture by DL.7 (`2.creatorpantry-daily-limits-scrub-prompts.md`).
**Rules:** `ai.md` (quota enforcement is deterministic code), `media.md`, `backend.md` (Limits).

### B-46 Prepaid only

- **No post-paid overage.** Nothing is metered and invoiced after the fact.
- **At zero, work waits.** An account that cannot afford a request is refused at admission (USAGE-006), before
  anything is queued or sent to a provider.
- **More credits come from a pack or an upgrade**, bought in advance; auto top-up buys a pack, opt-in and capped
  by the creator (PRICE-011).

**Options considered:** metered overage billed at period end — rejected: it is the surprise bill the landing
page promises never to send, and it moves the provider-cost risk from the platform to a dispute.
**Rules:** `ai.md`.

### B-47 The allowance and the plan belong to the account

- **Per Identity account (USAGE-001), across every workspace the account works in.** The existing
  `AccountAiQuota*` tables are already account-scoped and platform-level, with no `WorkspaceId`.
- **A plan attaches to the account, not to a workspace.** Workspace membership and roles are unchanged.
- **Caps that follow the plan are the account's.** Two workspaces the same person works in share one daily
  picture cap; the per-workspace cap stays beside it as a runaway bound (DL decision 3).

**Consequences:** counting an account's pictures across workspaces cannot use `IgnoreQueryFilters()` outside the
carve-outs in `tenancy.md`, and `backend.md`'s Limits rule counts daily budgets "from the rows the work writes,
inside the workspace filter". An account-wide cap therefore needs its own account-scoped record of what was
admitted (as `AccountAiUsageEntry` already is), not a filter bypass. DL.4 designs that count and amends the
Limits rule in the same change if it has to; until then the two read as in tension, not as a licence.
**Rules:** `tenancy.md`, `auth.md`, `backend.md`.

### B-48 The trial is 14 days

- **Owner, 2026-10-24, answering LP.2.** The catalogue states it as `TrialDays` on the trial plan.
- **Trial credits are granted once, not per period.** The trial is not a monthly allowance, which is why the
  landing estimator never offers it as an answer (LP.7).
- **Its gate is B-52; when it ends is B-54.**

### B-49 The credit is a pinned unit

- **1 credit = $0.001 of (provider list cost × 1.25)** — that is, $0.0008 of provider list cost. So credits =
  list price in dollars × 1,250: credits per 1M tokens = list $ per 1M tokens × 1,250, and credits per picture
  = list $ per picture × 1,250. A $0.07 picture is 87.5 credits.
- **The 1.25 is the margin over provider list cost**, so a credit spent is always more than its provider cost.
- **Every rate is configuration**, validated at startup: text in `Ai:Quota:ModelRates` and `Ai:Cost:Models`
  (both exist, both empty today), pictures in `Ai:Quota:ImageRates` and the matching `Ai:Cost` prices, which
  AP.2.4 adds. No price is a C# literal.
- **A missing rate is never free.** Text with no rate keeps settling at `DefaultTaskEstimate`; a picture with no
  rate is refused at admission (AP.2.4).

**Consequences:** today both text rate dictionaries are empty in every `appsettings*.json`, so every call
settles at the flat 10-credit estimate and cost reports read null. AP.2.1 fills them.
`AiQuotaOptions.TokensPerCredit` (1,000) is a different thing — the bridge for a period denominated in tokens
rather than credits — and is not a price; a credit-denominated period never reads it.
**Rules:** `ai.md`, `aspire.md`.

### B-50 Packs do not expire

- **A pack is credits already paid for, so they keep until they are spent.** Owner, 2026-10-24, ruling during
  LP.8; confirmed 2026-10-26 in AP.1.1.
- **This amends PRICE-008**, which said packs expire 12 months after purchase.
- **Order of spending is unchanged:** the period allowance first, then packs.
- **A refund or dispute still claws back the unspent part of the grant it came from** (AP.6.3); claw-back never
  makes a balance negative.

**Consequences:** `CreditPackOption` has no expiry field, on purpose (LP.8 removed `ExpiryMonths`); the landing
page and its FAQ already say packs keep. The AP document's PRICE-008 row and AP.5.1, AP.5.2, AP.5.3, AP.6.3 and
the AP.5.3 checkpoint were rewritten in AP.1.1 to drop expiry. With no expiry to order by, which pack is spent
first is AP.5.2's to propose (oldest first is the suggestion there); it is not decided here.
**Rules:** `external.md`.

### B-51 Plan, pack, rate and model figures — PROPOSED

**These are the pricing analysis's recommendations. They are not decided.** They live in configuration so the
owner can change them without a code change, and no prompt may hard-code them.

| Item | Proposed value |
| --- | --- |
| Plans (monthly / annual per month) | Trial free 14 days · Creator $19 / $15 · Pro $49 / $39 · Studio $99 / $79 |
| Credits per period | Trial 1,000 once · Creator 6,000 · Pro 18,000 · Studio 40,000 |
| Pictures per UTC day | 5 · 25 · 75 · 150 |
| Pictures running at once | 1 · 1 · 2 · 3 |
| Quality classes | Trial: draft, standard · Creator: + high · Pro: + high-2K · Studio: + high-4K |
| Picture credits | draft 25 · standard 50 · high 90 · high-2K 190 · high-4K 290 |
| Packs | 2,500 for $10 · 7,500 for $25 · 20,000 for $60 |
| Carry-over of plan credits | None (`AiQuotaOptions.DefaultCarryOver`, the current default) |
| Draft model | `z-image-turbo` or `qwen-image-2-1-turbo` (Venice) |
| Standard/high model | `gpt-image-2-5-flare` medium/high until the AP.3.3 bake-off picks one |

- **A local run publishes them; no deployment does.** LP.6 seeded `Billing:Plans`, `Billing:Packs` and
  `Billing:Rates` in `appsettings.Development.json` with these figures so the landing page could be built, and
  the AppHost's `Landing:Pricing` (on) sets `Billing:PublishPlans` for `aspire run` only. The API's own
  `appsettings.json` keeps it `false`, so no deployment shows a proposed price.
- **The B-52 trial picture cap (10) is decided, not proposed**, though it is configuration like the rest.
- **The picture credits are consistent with B-49** at the analysis's list prices: a $0.07 picture is 87.5 credits,
  published as 90.

**Consequences:** a figure becomes decided only by the owner, recorded here as a change to this section. Turning
`Billing:PublishPlans` on outside development waits on that and on the legal gate LP.6 states.

### B-52 The trial gate is a lifetime picture cap

- **No card on file.** A trial starts without a payment method; the landing page's "no card needed to look
  around" stays true.
- **A trial account may generate at most 10 pictures in all**, counted per Identity account and surviving
  workspace creation and deletion (AP.7.1). It sits beside the trial's daily cap; a request must pass both.
- **The cap is configuration on the trial plan** (`Billing:Plans:<trial>:TrialImageCap`), published in the
  catalogue so the landing page states the number the product enforces. It is refused at startup on a plan
  with no trial, and required on a published plan that has one.

**Options considered:** a card on file before trial pictures — rejected: it needs Stripe (Phase 6) before a
trial can make a single picture, and the landing page promises a trial with no card.
**Consequences:** AP.7.1 builds the count and the refusal. Until it ships, the catalogue states a cap nothing
enforces, so the figure is shown only where the catalogue is published — development — and AP.7.1 is a release
gate for `Billing:PublishPlans`, as AP.2.4/2.5 already are for the picture rates (LP.7).
**Rules:** `tenancy.md`, `ai.md` (limits are code).

### B-53 Pictures stay on Venice

- **Images are generated by Venice.ai over plain HTTP** (`VeniceImageGenerator`), as B-15's amendment recorded.
  They do not move to Azure Foundry. (`baseline.md`'s B-15 summary row still names an `images` Foundry
  deployment; its amendment, not the row, is current.)
- **AP.3.4 (a Foundry FLUX.2-pro generator) is skipped.** FLUX.2-pro stays a candidate in the AP.3.3 bake-off
  only where Venice offers it.
- **Chat and embeddings stay on Foundry**, unchanged.

**Consequences:** the AP document marks AP.3.4 `- skip` and keeps its text. The Foundry FLUX.2-pro deployment
and quota under "Credentials and external prerequisites" are no longer needed.
**Rules:** `CLAUDE.md` (Constraints: AI), `external.md`, `aspire.md`.

### B-54 Downgrades take effect at period end

- **A downgrade, a cancellation and a trial running out each take effect when the running period ends.** The
  creator keeps what they paid for until then.
- **The running period's allowance is never rewritten by a downgrade** (AP.4.2); the lower plan's allowance
  arrives at the next period roll.
- **How an upgrade lands is not decided here** — see Open items.
- **A trial ends at the end of its 14 days**, not when its credits or its picture cap run out; running out
  refuses work, it does not end the trial.

**Consequences:** AP.6.2 applies Stripe downgrades and cancellations at period end, and AP.7.1 ends a trial on
its date.
**Rules:** `external.md`.

## Relationship to the prompt libraries

| Prompt | Effect of this record |
| --- | --- |
| master 12.10l "Not done": image allowance charge | **Delivered by AP.2.4–AP.2.6** (B-45). A one-line pointer sits under the note. |
| master 9A.* allowance prompts | Unchanged. AP extends their ledger and admission. |
| `CLAUDE.md` out-of-bounds "Billing, plans, or metering" | **Amended** to point here (B-44). |
| LP.6, LP.7 | Shipped the catalogue half of AP.4.1 in `Modules/Billing`, seeded with B-51's proposals. |
| LP.8, LP.9 | Already state B-50; B-52's trial cap joins the trial card and comparison table. |
| DL.1–DL.10 | Narrow AP.2.4, AP.2.5, AP.4.1, AP.4.2 and AP.4.4 to one kind of picture and no Stripe. DL.1 adds its own decisions to this record. |
| AP.3.4 | **Skipped** (B-53). |
| AP.5.1, AP.5.2, AP.5.3, AP.6.3 | **Rewritten** to drop pack expiry (B-50). |
| AP.6.2 | Downgrade and cancellation at period end (B-54); upgrade timing open. |
| AP.7.1 | Lifetime picture cap of 10, no card (B-52). |

## Open items

### Upgrade timing — open, needed by AP.4.2

- **Question:** when a creator upgrades mid-period, what changes at once?
- **Options:** (a) everything at once, with the new allowance granted now (pro-rated or whole); (b) the caps and
  quality classes at once and the allowance at the next period roll, as AP.4.2 and the AP.4.4 checkpoint read;
  (c) everything at the next roll.
- **Impact:** what a creator who has just paid more can do today, and what Stripe proration AP.6.2 configures.
- **Default assumed by later prompts:** (b), because it is what AP.4.2 and the AP.4.4 checkpoint already say.
  AP.4.2 presents it to the owner before implementing, and the answer is recorded here.

### Sales tax and VAT — deferred to AP.6.1

- **Question:** does billing use Stripe Tax?
- **Options:** (a) Stripe Tax; (b) no automatic tax collection.
- **Impact:** Checkout configuration, the price shown before tax, and which jurisdictions can buy.
- **Default assumed by later prompts:** none. AP.6.1 reads Stripe's current docs, presents the answer, and
  records it here as a decision before any Checkout code is written. No prompt before AP.6.1 needs it.
