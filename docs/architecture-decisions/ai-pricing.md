# AI Pricing Decisions

*Status: Accepted — 2026-10-26, except B-51, which is **proposed, not decided**. Recorded by AI-pricing prompt
AP.1.1. B-55 added 2026-10-27 at the owner's request; B-56 added 2026-10-27 by AP.4.2 with the owner's answers;
B-57 and B-58 added 2026-10-28 by AP.6.1 with the owner's approval; B-59 added 2026-10-28 with the owner's answer;
B-60 added 2026-10-28 by AP.6.2 with the owner's approval.*

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
- Stripe's first-party documentation, read 2026-10-28 for AP.6.1 (B-57, B-58): Checkout fulfillment, Billing
  subscriptions and their webhooks, the customer portal (integration, configuration, configuration API, deep
  links), webhooks, prorations, Stripe Tax with Checkout, Managed Payments (overview, how it works, eligibility,
  set-up), idempotent requests, errors and low-level error handling, API upgrades, and the Stripe.net changelog.
- Product-owner approval of 2026-10-28 of AP.6.1's review: "Managed Payment is ok - approved"; and the answer
  to its auto top-up item the same day: "autotop off will need to use the notice and have them buy a pack --
  approved" (B-59).
- Product-owner approval of 2026-10-29 of AP.7.1's rule, with its two recommendations: a trial is dated from the
  account's creation, and an ended trial admits no AI work, pack credits included (B-61).
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
| PRICE-011 | When an account's credits run low the creator is told and offered a pack; **nothing is ever bought for them** (amended by B-59; the AP document first said opt-in auto top-up). | 6 |
| PRICE-012 | Trial accounts cannot generate unbounded images. | 7 |
| PRICE-013 | Every published picture price — quote, picker, landing page, billing page — is the rate that request is charged at, per quality class, read from one table (added 2026-10-27, B-55). | 3, 4 |

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
| B-54 | Downgrades take effect at period end | PRICE-007, 010 | Decided | A downgrade, a cancellation and a trial running out all take effect when the running period ends. How an upgrade lands is B-56. |
| B-55 | A picture's price is the rate it is charged at | PRICE-004, 006, 013 | Decided | Owner, 2026-10-27. A quality class's credits are read from `Ai:Quota:ImageRates` for the model, quality and resolution the class sends. Every page that states a picture price reads that table; there is no second copy. |
| B-56 | The plan sets the allowance and the picture caps | PRICE-007, 012, USAGE-003, 009 | Decided | Owner, 2026-10-27, answering AP.4.2. An ops-set allowance wins; otherwise the plan in force at the period's start. An upgrade adds the difference at once; a trial's credits are granted once. The plan's daily and running picture caps are the account's, beside the workspace's flat ones. |
| B-57 | Stripe: Checkout to buy, the portal to manage, webhooks to decide | PRICE-010, B-54, B-56 | Decided | Owner, 2026-10-28, approving AP.6.1. Hosted Checkout for a first subscription and every pack; the portal for payment methods, invoices and cancelling at period end, with plan switching off; plan changes from CreatorPantry's own page; webhooks decide every change. API pinned to `2026-09-30.endive`; test mode until the owner says otherwise. |
| B-58 | Tax: Stripe Managed Payments is the merchant of record | PRICE-010, PRICE-011 | Decided | Owner, 2026-10-28, answering AP.6.1. Stripe, through Link, calculates, collects and remits sales tax, VAT and GST, for 3.5% on top of card fees. AP.6.4 (auto top-up) cannot work as written under it; B-59 replaces it. |
| B-59 | A low-balance notice instead of auto top-up | PRICE-011 | Decided | Owner, 2026-10-28, answering B-58's open item. No pack is ever bought for a creator. When credits run low the account is told once a period and offered a pack, which the creator buys through Checkout. Amends PRICE-011. |
| B-60 | The plan follows the subscription, re-read | PRICE-007, 010, B-54, B-56, B-57 | Decided | Owner, 2026-10-28, approving AP.6.2. Every event re-reads its subscription; up at once, down at the end of a paid period wherever it was made, past due holds, ended lapses to the default now. Only the provider's own scheduled rows are withdrawn. |
| B-61 | A trial is dated from the account, and its end stops all AI work | PRICE-012, B-48, B-52, B-54 | Decided | Owner, 2026-10-29, approving AP.7.1. A trial runs `TrialDays` from the Identity account's creation. After it, while still on the trial, no AI work is admitted — text or pictures, pack credits included — until a plan is chosen. Running out of credits or pictures before then refuses that work only. |

## Decisions

### B-44 Billing, plans and metering are in scope

- **`CLAUDE.md` listed billing, plans and metering as out of bounds "unless explicitly requested"; the owner
  requested them on 2026-10-24.** Its out-of-bounds line now points here.
- **In:** plans, the plan catalogue, prepaid credit packs, picture and text metering against the allowance,
  payment through Stripe, a low-balance notice (B-59; first opt-in auto top-up). Which Stripe products (Checkout, Billing, Customer Portal) is
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
- **More credits come from a pack or an upgrade**, bought in advance and always by the creator; running low brings
  a notice offering a pack, never a charge (PRICE-011 as amended by B-59).

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
  (AP.2.1), pictures in `Ai:Quota:ImageRates` and `Ai:Cost:Images` (AP.2.4). No price is a C# literal.
- **A missing rate is never free.** Text with no rate keeps settling at `DefaultTaskEstimate`; a picture with no
  rate is refused at admission (AP.2.4).

**Consequences:** *this paragraph first said both text rate dictionaries were empty, so every call settled at
the flat 10-credit estimate with its cost reading null. Filled the same day it was recorded: AP.2.1 wrote the
text tables and AP.2.4 the picture ones, into the API's and the Worker's `appsettings.json` alike, so the two
agree about what one call costs.* Startup now refuses a deployed host whose chat model has no rate or price
(AP.2.1), and any host — in every environment — that can request a picture it has no rate for (AP.2.4). A text
call on a model nobody rated still settles at the flat estimate with a null cost, which is the original state
narrowed to one case rather than removed. `AiQuotaOptions.TokensPerCredit` (1,000) is a different thing — the
bridge for a period denominated in tokens rather than credits — and is not a price; a credit-denominated period
never reads it.
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
*Decided in AP.5.2 (owner, 2026-10-28): grants are spent oldest first whatever their source — purchase, ops or
promo — the id breaking a tie. A settled charge keeps from the allowance first and grants next, each up to what
it gave, and the rest returns to the grant it came from (`AccountCreditGrantDraw`). A text run that costs more
than its hold lands the overshoot on the allowance, never on a grant. Credits a hold had open when its grant was
clawed back stay spent if the run spent them; the rest join the claw-back, never the balance.*
*Built in AP.6.3 (owner, 2026-10-28, approving its mapping): a pack's checkout grants once, keyed by the checkout
session; a refund or a dispute claws back the **whole unspent balance** of that grant, once, because a partial refund
cannot fairly unspend credits already spent, and because a grant's claw-back is final by design. A dispute claws back
when it is opened, and **winning one does not re-grant** — `charge.dispute.closed` is not subscribed (B-57) — so an
operator re-grants through AP.5.1's ops route. The payment's record is a new integration row, `BillingPackPurchase`,
which is what lets a reversal find the credits it must take back: a refund names a payment, never a session.*
**Rules:** `external.md`.

### B-51 Plan, pack, rate and model figures — PROPOSED

**These are the pricing analysis's recommendations. They are not decided.** They live in configuration so the
owner can change them without a code change, and no prompt may hard-code them.

| Item | Proposed value |
| --- | --- |
| Plans (monthly / annual per month) | Trial free 14 days · Creator $12 / $9 · Pro $49 / $39 · Studio $99 / $79. First proposed as Creator $19 / $15. |
| Credits per period | Trial 1,000 once · Creator 4,500 · Pro 18,000 · Studio 36,000, the same on either billing cycle. First proposed as Creator 6,000 and Studio 40,000. |
| Pictures per UTC day | 5 · 25 · 75 · 150 |
| Pictures running at once | 1 · 1 · 2 · 3 |
| Quality classes | Trial: draft, standard · Creator: + high · Pro: + high-2K · Studio: + high-4K |
| Picture credits | Not a figure of their own since B-55: each class costs its rate. At today's AP.2.4 table and B-51's models: draft 12.5 (`z-image-turbo`) or 25 (`qwen-image-2-1-turbo`) · standard 50 · high 87.5 · high-2K 187.5 · high-4K 287.5. First proposed as 25 · 50 · 90 · 190 · 290. |
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
  first published as 90. B-55 (2026-10-27) removes the rounding: what a class costs is now what its model is
  charged at, so the owner sets a class's price by choosing its model, quality and resolution (AP.3.3), not by
  typing a figure.
- **Owner, 2026-10-28: the plans were reworked from the annual price down.** Annual Creator $9 with 4,500, Pro
  $39 and Studio $79 with 36,000; monthly is the same credits at about 20% more ($12, $49, $99). Pro was first
  given 12,000, which priced its credit at $3.25 per thousand, above Creator ($2.00), Studio ($2.19) and every
  pack, so Creator plus the 7,500 pack bought Pro's credits for $34. Pro keeps 18,000 ($2.17 per thousand)
  instead. At B-49's $0.0008 of provider cost a credit, a plan whose every credit is spent still clears about 57%
  (Creator annual) to 77% (Pro monthly) after card fees. Every pack ($3.00–$4.00 per thousand) still costs more
  a credit than any plan ($2.00–$2.75), so a heavy user is pointed at an upgrade rather than a pack. Above
  Creator the plans sell on quality classes and caps, not a cheaper credit: Studio is a hair dearer a credit than
  Pro ($2.19 against $2.17 annual, $2.75 against $2.72 monthly). The packs are unchanged.

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
gate for `Billing:PublishPlans`, as AP.2.4/2.5 already are for the picture rates (LP.7). *Delivered 2026-10-29
(AP.7.1): the cap is counted from the account's own picture holds, which carry no workspace, inside the same hold
as the daily cap and before it, and refused as `403 ai.quota.trial_images.forbidden`. That gate is passed; the
others LP.6 names still stand.*
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
its date — the date B-61 fixes, computed rather than scheduled, so nothing has to run at day fourteen.
**Rules:** `external.md`.

### B-55 A picture's price is the rate it is charged at

- **A quality class has no price of its own.** Its credits per picture are `Ai:Quota:ImageRates` read for the
  model, quality and resolution `Ai:Images:Classes` maps it to (AP.3.1), through `AiImageQuotaConverter`. Change
  the class's settings and its price follows; nothing else needs editing.
- **Every surface that states a picture price reads the same answer:** the quote (AP.2.6, per class from
  AP.3.1), the quality picker (AP.3.1A), the published plan catalogue and so the landing page (AP.4.5), and the
  billing page (AP.6.5). The browser formats the server's figure and does no pricing arithmetic.
- **`Billing:Rates` loses its picture entries** (`ImageDraft`, `ImageStandard`, `ImageHigh`), which were a
  hand-typed copy of the proposal (LP.7) and had already drifted from what is charged (high: 90 published, 87.5
  charged; no high-2K or high-4K). It keeps the text entries, which are typical figures and say "about".
- **A picture's price is exact, not typical.** Text is charged by the tokens a call reports and the menu says
  "about"; a picture is charged per picture at its class's rate, which is known before the request, so the menu
  states it without "about".
- **Owner, 2026-10-27:** "if we are giving the user the ability to create images of a given quality we also
  need to price them out … not at a static value and need to update the landing page."

**Options considered:** keep `Billing:Rates` per class and assert it equals the charging table — rejected: two
figures that a test holds equal is still two places to edit, and the per-class menu would need five more keys
that only restate `Ai:Quota:ImageRates`.
**Consequences:** the figures in B-51's "Picture credits" row become a reading of the table, not a proposal of
their own. DL.7's "one rate source … `Billing:Rates:ImageStandard`" and DL.10's "quality classes stay off the
page" are superseded once AP.3.1 ships: AP.2.5 already charges from `Ai:Quota:ImageRates`, and AP.4.5 puts the
classes on the page. Until AP.3.1, the one class the deployment sends is standard's row.
**Rules:** `ai.md` (rates are configuration), `frontend.md` (the browser renders the server's figures),
`api-contract.md`.

### B-56 The plan sets the allowance and the picture caps

- **Precedence (USAGE-009).** An `AccountAiQuota` row an administrator set with an allowance is the account's
  allowance, whatever its plan. A row with no allowance — one that only suspends or sets a zone — leaves the
  number to the plan. With no row, the plan decides. With no plans configured, `Ai:Quota:DefaultAllowance`, as
  before plans existed.
- **A period opens with the plan in force at its own start**, not at the moment its row is written, so an
  account that is idle across the roll is treated as one that used AI on the first. The opening `Allowance` is
  never rewritten.
- **An upgrade adds the difference at once.** Owner, 2026-10-27: "if they upgrade they get the difference not
  the total". The period's grant is raised to the new plan's credits — Creator's 4,500 to Pro's 18,000 adds
  13,500 — recorded apart from the opening allowance (`AccountAiQuotaPeriod.UpgradeGranted`). The grant is only
  ever raised: up, down and up again in one period adds the difference once. Caps and quality classes change at
  once too, because they are read at the request. This settles the open item "Upgrade timing" as option (a),
  with the allowance granted as the difference rather than pro-rated.
- **A downgrade waits for the roll** (B-54), including a downgrade made before an idle account's period row was
  written.
- **A trial's credits are granted once in an account's life.** Owner, 2026-10-27: "trial credits are only for
  the trial". A plan whose `Period` is `Once` grants its credits to the first period opened under it and nothing
  to any later one; the period records the plan it opened under (`PlanKey`). This holds before AP.7.1 ends
  trials: an account left on a trial default has its thousand credits once, not every month.
- **The daily picture cap is the account's, per UTC day, across every workspace it works in** (B-47). It is
  judged inside the account's picture hold, under the period's row version, so two requests cannot both take
  the last picture of the day; counted from the holds, never by reading another workspace's operations. While a
  hold is held, every picture asked for counts; once settled, those charged; released or expired, none.
- **The running-pictures cap is the account's too**, taken from the plan at the request onto the operation and
  enforced by the queue claim with the same strength as the workspace's running share.
- **Beside the workspace's limits, never instead of them.** `Limits:ImageVariantsPerWorkspacePerDay`,
  `ImageRunningPerWorkspace` and `ImageInFlightPerWorkspace` remain a bound on each workspace whoever asks. A
  request passes both, in this order: workspace in-flight, workspace day, account day, credits. Startup refuses a
  plan whose caps exceed the workspace's, which a creator in one workspace could never reach;
  `ImageRunningPerWorkspace` rose from 2 to 3 so Studio's 3 can be reached. Both kinds switch off with
  `Limits:Enabled`.
- **Development defaults to Studio** (owner, 2026-10-27), so local work is not held to the trial's figures
  before AP.4.4 can move an account. Which plan a deployment defaults to is still the daily-limits document's
  open item.

**Options considered:** pro-rating an upgrade by days left — rejected by the owner; the whole new allowance on
top — rejected ("the difference not the total"); a separate per-account day-hold table (DL.4) — not needed, since
the account's picture holds already exist, are counts only, and are serialized per account.
**Consequences:** AP.6.2 configures Stripe so an upgrade is charged and granted at once and a downgrade at
period end. AP.7.1 decides how a trial ends; its credits are already once-only. Account erasure (9A.3) clears
`GeneratedImageOperation.RequestedByAccountId` with the account's quota and plan rows.
**Rules:** `backend.md` (Limits), `tenancy.md`.

### B-57 Stripe: Checkout to buy, the portal to manage, webhooks to decide

- **Owner, 2026-10-28, approving AP.6.1's review** of Stripe's current documentation (Sources).
- **Hosted Checkout for a first purchase only:** an account on the trial or with nothing running buys a plan
  (`mode=subscription`), and any account buys a pack (`mode=payment`). The account's Stripe customer is made first
  (`BillingCustomer`) and passed in, and the account id travels as `client_reference_id` and as metadata on the
  session, on the subscription it makes and on a pack's payment, so every later event names the account. No
  Stripe trial is ever set: the trial is CreatorPantry's own, with no card (B-48, B-52).
- **One Stripe product per paid plan, with a monthly and an annual price; one product per pack, with one one-time
  price.** Stripe's own advice for tiers. The price ids are configuration (`Billing:Stripe:Prices`, set per machine
  and never committed), checked at startup: every paid plan priced on both cycles, every pack priced, no trial
  priced, no unknown key, no id twice. `Billing:Plans` stays the published figure (B-51); AP.6.5 checks Stripe's
  amounts against it.
- **The customer portal for payment methods, invoices, billing details and cancelling at period end**
  (`subscription_cancel.mode=at_period_end`, no proration). **Plan switching is off in it.** The portal can defer
  a switch to period end only between prices of one product, so a Pro → Creator switch there would land at once
  and break B-54.
- **A plan change on a running subscription starts from CreatorPantry's billing page.** An upgrade opens a portal
  `subscription_update_confirm` deep link, under a portal configuration that invoices the proration at once
  (`always_invoice`), so Stripe handles card authentication and a failed payment; the B-56 difference is granted
  when the webhook says it was paid. A downgrade, and annual → monthly, is a subscription schedule whose next phase
  starts at the current period's end (B-54). Both are AP.6.2's to build.
- **Events handled** (AP.6.2, AP.6.3). Nothing else is subscribed to.

  | Event | Why |
  | --- | --- |
  | `checkout.session.completed`, `.async_payment_succeeded`, `.async_payment_failed` | Link a subscription to its account; grant a pack (B-50) |
  | `customer.subscription.created`, `.updated`, `.deleted` | Plan, period, a scheduled change, cancellation at period end |
  | `invoice.paid` | Renewal, and a paid upgrade — the subscription re-fetched and `active` before acting |
  | `invoice.payment_failed`, `invoice.payment_action_required` | Tell the creator; the plan follows the subscription's status |
  | `charge.refunded`, `charge.dispute.created` | Claw back the unspent part of the pack's grant (B-50) |

  Not subscribed: entitlements, `customer.subscription.trial_will_end` (no Stripe trial), `customer.updated` (no
  billing detail is stored here) and `invoice.finalization_failed` (only Stripe Tax causes it; B-58).
- **How an event is taken:** the signature verified on the raw body, with Stripe's five-minute tolerance, before
  anything is parsed; the event id stored, so a replay changes nothing; 2xx returned at once and the work done from
  the outbox; and order never assumed — Stripe does not guarantee it, so the subscription or invoice is re-fetched
  before a state changes. Full-object ("snapshot") payloads, the endpoint created at the pinned version.
- **The API is pinned to `2026-09-30.endive`** (`Stripe-Version` on every request), through a plain-HTTP gateway
  (`Modules/Billing/Integration/Stripe`) behind the application-owned `IBillingPaymentGateway`. Classic
  `Customer` objects, not Accounts v2, which outside Connect is still a public preview needing a preview version.
- **Test mode only until the owner says otherwise.** A live key stops the API unless `Billing:Stripe:AllowLiveMode`
  is set on it.

**Options considered:** Stripe.net (53.0.0, which pins `endive`) — not used: the four calls need no SDK, the other
providers here are plain HTTP, a package release moves the pin and has removed parameters between majors, and
with no SDK no Stripe type exists outside the adapter to leak. Plan switching in the portal — rejected (B-54, above).
A Stripe trial — rejected (B-52: no card). Thin event payloads, which Stripe now recommends — not chosen: it could
not be confirmed that their catalogue carries these v1 subscription and invoice events.
**Consequences:** AP.6.2 adds the two plan-change operations to the gateway, and the webhook route; AP.6.3 finds a
refunded or disputed pack through its payment's metadata; AP.6.5's "upgrade via Checkout" means a first
subscription only, and its return page shows "pending" from the session id Stripe writes into the success URL.
Account erasure (9A.3) must name `BillingCustomers` beside the plan and quota tables before it ships; the Stripe
customer itself is Stripe's record as merchant of record (B-58), and a deletion request made through Link is
Stripe's to carry out.
**Rules:** `external.md`, `aspire.md`, `gateway.md`, `backend.md`.

### B-58 Tax: Stripe Managed Payments is the merchant of record

- **Owner, 2026-10-28, answering AP.6.1: "Managed Payment is ok - approved".** Every Checkout session sets
  `managed_payments[enabled]=true`. Stripe, through Link, is the merchant of record: it calculates, collects,
  files and remits sales tax, VAT and GST in more than 80 countries, and handles fraud, disputes and
  transaction-level support. Nothing here sets `automatic_tax` or collects an address for tax.
- **What it costs:** 3.5% of each transaction on top of card processing (2.9% + 30¢, US cards) and, for
  subscriptions, Billing's 0.7% — against Stripe Tax Basic's 0.5% where registered. Every plan still covers its AI
  at full credit burn: Creator annual, the thinnest, goes from about 2.4× to about 2.3× its provider cost ($108 a
  year less about $8 of fees, against $43.20 of AI at B-49's $0.0008 a credit). Every pack still costs more a
  credit than every plan.
- **What it changes for a creator:** Checkout, receipts and the card statement say Link (`LINK.COM* …`); Stripe
  sends the receipts and some subscription emails itself; customers can also manage orders at link.com; Stripe may
  refund within 60 days without asking; Checkout cannot be on a custom domain.
- **What it requires:** Stripe's eligibility review, a business in one of its supported countries, and an eligible
  product tax code on every product — `txcd_10105002` (AI as a service, cloud, business use) is proposed, the
  owner's to confirm with an accountant. Subscriptions are created only through Checkout, which is already the
  design (B-57).
- **AP.6.4 (auto top-up) cannot work as written.** A Managed Payments purchase authorises only Managed Payments
  to charge the card, and an off-session charge is an integration it does not support. B-59 replaces it.

**Options considered:** Stripe Tax Basic — rejected: the owner would register and file wherever tax is owed —
generally VAT from the first sale to a consumer abroad, and US states as their thresholds are passed — and it
collects nothing until a registration exists. No automatic tax — rejected. This record is not tax advice.
**Consequences:** the open item "Sales tax and VAT" is settled; two follow from it (below), and the auto top-up one
is answered by B-59.
**Rules:** `external.md`.

### B-59 A low-balance notice instead of auto top-up

- **Owner, 2026-10-28, answering "Auto top-up under Managed Payments": "autotop off will need to use the notice
  and have them buy a pack -- approved".** Option (b) of that item.
- **Nothing is ever bought for a creator.** No saved card is charged and no payment is taken off-session: every
  pack is a Checkout the creator completes (B-57), which is also the only kind of charge Managed Payments makes
  (B-58). The landing page's "no surprise bills" now holds without a setting to keep it true.
- **Running low brings a notice.** When admission leaves an account's spendable credits — the period allowance
  left plus every pack's balance — **at or below** a threshold (the same inclusive boundary
  `NearlySpentFraction` draws, and what AP.6.4 built), the account is told, once for that period, and offered a
  pack: the notice links to the billing page's pack purchase (AP.6.5). Reaching zero is unchanged: work is refused
  at admission (USAGE-006) and the exhausted state offers a pack and an upgrade.
- **Settled by AP.6.4 (owner, 2026-10-28, "approved"), as proposed and with one answer the repository gave:**
  the threshold is 10% of the period's spendable allowance, in `Ai:Quota:LowCreditNoticeFraction`; the notice is
  **in-app only**, because there is no notification module in the repository and no email sender of any kind, so
  "email if that module already delivers it" resolves to no email; it is on by default and the creator may turn
  it off. A notice is not a charge, so it needs no opt-in. The threshold is deliberately a second line below
  `Ai:Quota:NearlySpentFraction` (0.2) rather than the same one: nearly-spent counts the period's allowance alone
  and repeats, this counts the allowance **and** the packs and is said once, so an account holding packs is not
  running low at nought allowance.
- **This amends PRICE-011**, which said "auto top-up is opt-in, buys the smallest pack, and is capped per month by
  the creator".

**Options considered:** dropping auto top-up with nothing in its place — rejected: a creator who runs out mid-task
learns it only from a refusal. Charging off-session outside Managed Payments — rejected: CreatorPantry would be
merchant of record and owe the tax on those charges, and it needs consent Managed Payments' checkout does not give.
**Consequences:** AP.6.4 is rewritten to build the notice, and **built it on 2026-10-28** — the mark on
`AccountAiQuotaPeriod`, the account's setting, the two `/api/v1/me/ai-usage/low-credit-notice` routes and the
shell's notice. AP.6.5's billing page drops the auto top-up setting and shows the notice's on/off instead, and
repoints `PACK_PURCHASE_LINK` (which AP.6.4 left aimed at the allowance page) at its pack purchase. The landing
tile that promised auto top-up was replaced the same day, on the owner's approval — now "Running low? You hear
first", **and its "Coming with billing" label came off when AP.6.4 shipped** — with a spec that no promise names
an automatic purchase (`landing-promises.component.ts`, `docs/landing-page-copy.md`). The Terms of Service and Privacy Policy drafts were updated the same day for B-48–B-59
— the trial, packs that do not expire, plan-change timing, Link as merchant of record and the low-balance notice —
each with a revision note for counsel, whose review LP.6 still requires before either is published.
**Rules:** `ai.md` (quota enforcement is deterministic), `frontend.md`.

### B-60 The plan follows the subscription, re-read

- **Owner, 2026-10-28, approving AP.6.2's design** ("approved"), including its five recommendations below.
- **The route.** `POST /api/v1/webhooks/stripe`, through the gateway on a route of its own (`stripe-webhook`): no
  antiforgery token asked, no browser session carried (no internal token is attached even when a cookie arrives),
  `Stripe-Signature` forwarded on it alone, a 512 KiB body limit on the route and the action, and 600 deliveries a
  minute per address. The signature is verified over the raw body before it is parsed — `v1` only, any of several
  during a secret roll, constant time, five minutes either way. 200 for a stored or already-stored event; 400
  `billing.webhook.signature_invalid` (or `.unreadable`); 503 `billing.webhook.unavailable` on a host with no signing
  secret, which Stripe retries.
- **Stored, then worked.** One `BillingProviderEvent` per event id (unique: the replay guard), with no payload, and
  its outbox message in the same save. The Worker's handler reads the subscription again (`expand[]=schedule`),
  records it on `BillingSubscription`, and decides from **every** subscription the account has, so a cancelled one's
  late event cannot lapse an account another is paying for. Order is never assumed: an older event processed late
  reads the same subscription and writes nothing.
- **Whose account.** The subscription's customer must be a `BillingCustomer` in the same mode, and its
  `creatorpantry_account_id` metadata must be that customer's account. Anything else changes nothing and is
  `NeedsAttention`.
- **The transitions.**

  | The subscription, as re-read | The account's plan chain (source `Stripe`) |
  | --- | --- |
  | `incomplete` | Nothing until the first payment is taken |
  | `active`, a higher plan than the one in force, its latest invoice paid | That plan, now. B-56's difference follows at the next admission |
  | `active`, a higher plan, its latest invoice still open | Nothing yet: an upgrade's proration is unpaid until `invoice.paid` |
  | `active`, a lower plan, while a plan the creator paid for through Stripe is in force | That plan from the item's `current_period_end` — wherever it was changed, link.com included (B-54) |
  | `active`, a lower plan, while a trial, ops or default plan is in force | That plan, now: nothing paid for is cut short |
  | A schedule's next phase, a plan from T | That plan from T. A phase that only changes the cycle writes nothing |
  | Cancelling at period end (`cancel_at` T) | The default from T; withdrawn, the scheduled row is taken off |
  | `past_due` | Nothing, in either direction: an unpaid upgrade never lands |
  | `unpaid`, `canceled`, `incomplete_expired`, `paused` | The default, now — if the plan in force is one a subscription put there; an ops grant or trial made since stays |

- **Ops is not overwritten** (recommendation 2). The reconciler withdraws only scheduled rows it wrote itself; an
  ops-scheduled row in the way is `NeedsAttention` and the chain is left as it was. An in-force ops plan is superseded
  normally; an ops-set allowance still wins through `AccountAiQuota` (B-56).
- **Lapsing is to the configured default** (recommendation 3): `PlanKey` null, which follows `Billing:DefaultPlan`.
  There is no free or lapsed plan in the catalogue; adding one is a catalogue change and the owner's. Development's
  default is Studio, so a cancelled local subscriber reads as Studio.
- **A failed payment is recorded, not yet told** (recommendation 4): the creator's notice is AP.6.4/AP.6.5's.
- **Plan changes on a running subscription** (B-57's two operations), on `IBillingPaymentGateway` without a route
  yet (AP.6.5 adds it): an upgrade is a portal `subscription_update_confirm` page under a second portal configuration,
  `Billing:Stripe:UpgradePortalConfiguration` — plan switching on, which Stripe's confirmation flow appears to need,
  and `always_invoice`; a deep link hides the rest of the portal, so its switcher is never shown (recommendation 5,
  the owner to create it in the Dashboard). A downgrade, or annual to monthly, is a schedule made `from_subscription`
  and set to two phases — the current one as Stripe reported it, then the new price from its end, no proration,
  `end_behavior=release`. A schedule is released before an upgrade, since the portal will not change a scheduled
  subscription.
- **The Worker reads subscriptions, and verifies nothing.** The AppHost gives it the key and the price ids, never the
  signing secret, and it builds no verifier even from a whole connection string that carries one.
- **Two Workers, one account** (AP.6.2 review). A subscription's reading is written only if its record has not moved
  since before the provider was asked, and a reconcile stands only if no record of the account moved while it decided;
  the loser retries and reads the provider again.
- **Nothing acknowledged is lost.** The route answers 200, so Stripe will not redeliver; an event whose outbox message
  gave up while Stripe could not be read stays `Received`, and the Worker's sweep queues it again every quarter hour
  for a day, then leaves it `NeedsAttention` (`provider_unavailable`).

**Options considered:** acting on the event's own object — rejected (B-57: order is not guaranteed). Applying a lower
plan the moment Stripe shows it — rejected: a change made at link.com would land at once and break B-54. Letting the
reconciler overwrite an ops schedule — rejected: an operator's decision is not the provider's to undo.
**Consequences:** the allowance period does not follow Stripe's billing period, which leaks credits on a
cancellation or downgrade (Open items, "Allowance period and the billing period"); AP.6.2a closes it before live
mode. The upgrade and schedule calls are written to Stripe's documented parameters but have not been run against a
Stripe account; the first test-mode run checks them (Open items, link.com).
**Rules:** `external.md`, `gateway.md`, `backend.md`, `tenancy.md` (platform-scoped; no workspace).

### B-61 A trial is dated from the account, and its end stops all AI work

- **Owner, 2026-10-29, approving AP.7.1's rule** and both of its recommendations.
- **A trial starts when the Identity account is created** and ends `TrialDays` later (B-48's 14). One date per
  account, read through Auth's `IAccountCreationFacade` and never stored or scheduled: a plan bought before then
  replaces the trial, and an account that paid and lapsed back to a trial default has no second trial.
- **After it, while the account is still on the trial, no AI work is admitted** — writing and pictures alike, at the
  request and again when the Worker claims queued work, and **pack credits included**. A pack is spent under a plan's
  caps and quality classes, and an ended trial is no plan to spend under. The refusal is
  `403 ai.quota.trial_ended.forbidden` with `trialEndedAt`: no balance and no reset, because choosing a plan is the
  remedy. Everything else in CreatorPantry — recipes, content, media, export — works as before.
- **Running out before then refuses only that work** (B-54): a trial whose pictures are spent still writes, and one
  whose credits are spent is exhausted, not ended.
- **An ops-set allowance is not refused for the trial's end** (USAGE-009): an administrator who gives an account a
  number has decided what it may spend.
- **An account Identity has no record of has no trial end.** It has no session to ask from; work it queued before is
  bounded by the trial's once-only credits and lifetime picture cap.

**Options considered:** dating the trial from its first AI use — rejected: the landing page and the Terms promise new
accounts 14 days, and an idle sign-up would otherwise keep its trial indefinitely. Letting an ended trial spend pack
credits at the trial's caps — rejected by the owner: it would sell credits to an account on no plan.
**Consequences:** a pack Checkout should refuse an account whose trial has ended and which has no plan, pointing it at
a plan first (B-57 lets any account buy a pack); that is a change to AP.6's Checkout, not yet made. The landing page's
trial-length CTA and FAQ 13, held back until a trial could end, are unblocked.
**Rules:** `tenancy.md`, `ai.md` (limits are code), `backend.md` (facade to facade).

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
| AP.7.1 | Lifetime picture cap of 10, no card (B-52); the trial's date and end (B-61). Delivered 2026-10-29. |
| AP.3.1, AP.3.1A, AP.4.5, AP.6.5 | **Amended or added** 2026-10-27 so the quote, the picker, the landing page and the billing page read each class's price from the charging table (B-55). |
| AP.4.2, DL.4, DL.5, DL.8 | **Delivered by AP.4.2** (B-56): the plan's allowance and both picture caps, through the account's picture holds rather than DL.4's day-hold table. |
| DL.7, DL.10 | **Superseded in part** by B-55: picture prices come from `Ai:Quota:ImageRates`, not `Billing:Rates:ImageStandard`, and AP.4.5 puts quality classes on the page. |
| AP.6.1 | **Delivered** with B-57 and B-58: the gateway, `BillingCustomer`, the price map and the Aspire parameters. |
| AP.6.2 | **Extended** by B-57: the upgrade deep link and the period-end schedule, and the event table above. |
| AP.6.2 | **Delivered** with B-60: the webhook route, `BillingProviderEvent` and `BillingSubscription`, the transitions, and B-57's plan-change operations on the gateway. |
| AP.6.2a | **Added** by B-60: the allowance period follows a Stripe-billed account's subscription period. A gate before live mode. |
| AP.6.4 | **Rewritten** by B-59 and then **delivered**: the low-credit notice, its threshold, its setting and its two routes. |
| AP.6.5 | **Narrowed** by B-57: Checkout for a first subscription; a running one changes plan through AP.6.2's operations. |

## Open items

### Upgrade timing — decided 2026-10-27, B-56

- **Answer:** everything at once, with the allowance raised by the difference between the plans.
- **Question:** when a creator upgrades mid-period, what changes at once?
- **Options:** (a) everything at once, with the new allowance granted now (pro-rated or whole); (b) the caps and
  quality classes at once and the allowance at the next period roll, as AP.4.2 and the AP.4.4 checkpoint read;
  (c) everything at the next roll.
- **Impact:** what a creator who has just paid more can do today, and what Stripe proration AP.6.2 configures.
- **Default assumed by later prompts:** (b), because it is what AP.4.2 and the AP.4.4 checkpoint already say.
  AP.4.2 presents it to the owner before implementing, and the answer is recorded here.

### Sales tax and VAT — decided 2026-10-28, B-58

- **Answer:** neither option as written: Stripe Managed Payments, with Stripe as merchant of record.
- **Question:** does billing use Stripe Tax?
- **Options:** (a) Stripe Tax; (b) no automatic tax collection.
- **Impact:** Checkout configuration, the price shown before tax, and which jurisdictions can buy.
- **Default assumed by later prompts:** none. AP.6.1 reads Stripe's current docs, presents the answer, and
  records it here as a decision before any Checkout code is written. No prompt before AP.6.1 needs it.

### Prices shown with tax or before it — open, raised by AP.6.1

- **Question:** does a plan's or a pack's published price include tax, or is tax added at Checkout?
- **Options:** (a) added at Checkout — Stripe's default (`tax_behavior` exclusive), so the landing page's $12 is
  $12 plus whatever tax applies where the creator is; (b) included — the price is what is paid everywhere, and the
  net after tax varies by country.
- **Impact:** what the landing page and the billing page say beside a price ("plus applicable tax"), consumer
  price-display rules in some countries, and the margin per country under (b).
- **Default assumed until answered:** (a), because it is a Stripe Dashboard and price setting, not code, and a
  test-mode price is easily remade. Not to go live undecided.

### Auto top-up under Managed Payments — decided 2026-10-28, B-59

- **Answer:** (b): a low-balance notice, and the creator buys a pack. Nothing is bought for them.
- **Question:** what does PRICE-011's opt-in auto top-up become, now that a Managed Payments purchase authorises
  only Managed Payments to charge the card and an off-session charge is not supported under it (B-58)?
- **Options:** (a) drop auto top-up, and amend PRICE-011; (b) replace it with a low-balance notice and a one-click
  pack Checkout, which is a purchase the creator makes rather than a charge; (c) charge off-session outside
  Managed Payments, with CreatorPantry merchant of record for those charges and the tax on them — not recommended.
- **Impact:** PRICE-011, AP.6.4's scope, and AP.6.5's auto top-up setting.
- **Default assumed until answered:** none. AP.6.4 does not start until the owner answers.

### Subscription changes made at link.com — checked in design only, AP.6.2

- **Answer so far:** not verified in test mode — no Stripe account was available to AP.6.2, as to AP.6.1. B-54 no
  longer depends on it: a lower price Stripe shows while a paid higher plan is in force lands at that period's end,
  wherever the change was made (B-60). What a creator is charged or refunded for a switch made there stays Stripe's.
- **Question:** Managed Payments gives every customer order management at link.com, including "cancel or update
  subscriptions". Can a creator switch plans there, and if so does a downgrade land at once, around the portal's
  configuration and B-54?
- **To do:** the first test-mode run makes a switch at link.com and checks what the subscription says afterwards, and
  confirms the upgrade portal configuration and the schedule update against a real account.

### Allowance period and the billing period — open, raised by AP.6.2; a gate before live mode

- **Question:** the allowance period runs on its own monthly anchor and time zone (`AccountAiQuota`), not on Stripe's
  billing anchor. A Creator subscriber whose paid month ends on the 2nd has an allowance period that opened on the
  1st under Creator; cancelled, the plan lapses on the 2nd, and B-56 never rewrites a period's opening allowance — so
  a month of credits is kept for one paid day. A downgrade leaks the same way. That breaks "never lose money on AI".
- **Recommended, approved 2026-10-28 as a separate prompt (AP.6.2a):** a Stripe-billed account's allowance period
  follows its subscription's period, its credits granted on the renewal's `invoice.paid`.
- **Default assumed until done:** test mode only. Not to go live before AP.6.2a ships.
