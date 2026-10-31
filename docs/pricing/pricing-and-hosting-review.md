# CreatorPantry pricing recommendation (draft for B-51, 2026-10-28)

*Status: **Proposed, not decided.** A review of the plan, pack and rate figures in
[`ai-pricing.md`](../architecture-decisions/ai-pricing.md) B-51 against the hosting proposal in
[`azure-hosting.md`](../infrastructure/azure-hosting.md). It records no decision and changes no configuration.*

## Recommended prices

**Keep the owner's 2026-10-28 figures.** Every tier still makes money when a customer spends every credit.

| | Trial | Creator | **Pro** | **Studio** |
| --- | --- | --- | --- | --- |
| Billed annually | free, 14 days | $9/mo ($108/yr) | **$39/mo ($468/yr)** | **$79/mo ($948/yr)** |
| Billed monthly | — | $12/mo | **$49/mo** | **$99/mo** |
| AI credits | 1,000 once | 4,500/mo | 18,000/mo | 36,000/mo |
| Pictures per day / running | 5 / 1 (10 in all) | 25 / 1 | 75 / 2 | 150 / 3 |
| Best picture quality | standard | high | high-2K | high-4K |
| Packs | 2,500 for $10 · 7,500 for $25 · 20,000 for $60, never expire | | | |

There is no overage (B-46). The provider cost of a credit is pinned at $0.0008 (B-49), so the most AI any plan
can cost is its credits × $0.0008: $3.60, $14.40 and $28.80 a month. Before launch, change one setting rather
than any price: the platform's daily picture ceiling (see "Other recommendations").

## Reading of the hosting proposal

`azure-hosting.md` sizes the deployment but gives no dollar figures. Priced from the Azure Retail Prices API
(East US, list price, 730 hours a month):

| Item | Prod | Dev | How it is priced |
| --- | --- | --- | --- |
| Container Apps (gateway, web, api, worker) | $160–350 | ~$55 | $0.000024 per vCPU-second active, $0.000003 idle, $0.000003 per GiB-second; free grant of 180k vCPU-s and 360k GiB-s per subscription |
| Azure SQL General Purpose | ~$370 (2 vCore provisioned, license included) | ~$35 (serverless, auto-pause) | about $0.504/hr for 2 vCore; serverless $0.52 per vCore-hour while running; storage $0.115/GB |
| Azure Managed Redis | ~$23 (B1) | ~$12 (B0) | $0.032 and $0.016 per hour, from the retail meter |
| Log Analytics | ~$50 with sampling | ~$5 | $2.30/GB ingested after 5 GB free |
| Blob storage (Hot ZRS / LRS) | ~$5 | ~$1 | about $0.026/GB a month, growing with pictures |
| Container Registry (Basic), Key Vault, egress | ~$10 | shared | — |
| **Total** | **~$620–810** | **~$110** | **planning figure: about $830/mo for both** |

Notes on the range:

- **Container Apps.** The top of the range assumes every minimum replica bills as active. The bottom assumes only
  the worker does, since it polls SQL and never idles, while the others bill at the idle rate between requests. At
  launch traffic expect the bottom. Prod's minimums are 4.5 vCPU and 9 GiB, and the gateway's two replicas exist
  for session continuity (hosting change 2), so they are not the place to save.
- **SQL is the largest fixed line.** Two provisioned vCores cost about $370 a month whether or not anyone is
  signed in. Serverless General Purpose with a 0.5-vCore floor and no auto-pause costs about $190 a month at
  light load and grows with use. That suits the first hundred accounts better.
- **Nothing in the total is AI.** Chat and embeddings on Foundry and pictures on Venice are billed per call, so
  they are the per-account cost below, not a fixed cost. Stripe has no monthly fee. USDA and Buffer are off by
  default.
- **Log Analytics is the line that surprises.** Four services exporting OpenTelemetry traces with no sampling can
  ingest several GB a day, which is $150 or more a month. Cap it.
- **Bandwidth is small.** CreatorPantry is a tool, not a published blog, so only the creator views their own
  pictures. The first 100 GB of internet egress each month is free. Front Door is not in the design; adding it
  would cost $35 a month plus traffic.

**Lean launch:** serverless SQL in prod, `web` at a minimum of one replica, and capped logs bring the total to
about **$560 a month**. Move to the sized design when load calls for it, not on day one.

## Per-subscriber unit economics (modelled)

Assumptions:

- **Stripe:** 2.9% + $0.30 per charge, plus 0.7% Billing on subscriptions. An annual plan pays this once a year.
  Add 0.5% if Stripe Tax is turned on (deferred to AP.6.1).
- **AI:** credits spent × $0.0008. *Typical* assumes half the credits are spent; *full burn* assumes all of them.
- **Storage, egress and telemetry per account:** $0.15 for Creator, $0.40 for Pro, $0.80 for Studio.
- **Support:** valued at $60/hr. Creator takes 2 minutes per account a month, Pro 5 and Studio 8.

| Plan | Price | Stripe | AI typical | Storage etc. | Support | Contribution | At full burn |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Creator annual | $9.00 | $0.35 | $1.80 | $0.15 | $2.00 | $4.70 (52%) | $2.90 (32%) |
| Creator monthly | $12.00 | $0.73 | $1.80 | $0.15 | $2.00 | $7.32 (61%) | $5.52 (46%) |
| Pro annual | $39.00 | $1.43 | $7.20 | $0.40 | $5.00 | $24.97 (64%) | $17.77 (46%) |
| Pro monthly | $49.00 | $2.06 | $7.20 | $0.40 | $5.00 | $34.34 (70%) | $27.14 (55%) |
| Studio annual | $79.00 | $2.87 | $14.40 | $0.80 | $8.00 | $52.93 (67%) | $38.53 (49%) |
| Studio monthly | $99.00 | $3.86 | $14.40 | $0.80 | $8.00 | $71.94 (73%) | $57.54 (58%) |

**Coverage at full burn** is revenue after Stripe divided by the most the AI can cost. It is 2.40× for Creator
annual, 3.13× for Creator monthly, 2.61× and 3.26× for Pro, and 2.64× and 3.30× for Studio. Creator annual is the
thinnest: it covers its AI cost, but one extra support ticket a month uses up most of what is left. Do not cut
its price or add to its credits.

| Pack | Price | Stripe (no Billing fee) | AI at full burn | Contribution |
| --- | --- | --- | --- | --- |
| 2,500 credits | $10 | $0.59 | $2.00 | $7.41 (74%) |
| 7,500 credits | $25 | $1.03 | $6.00 | $17.98 (72%) |
| 20,000 credits | $60 | $2.04 | $16.00 | $41.96 (70%) |

Every pack costs more per credit ($3.00–4.00 per thousand) than any plan ($2.00–2.75), so heavy users are pushed
toward an upgrade. Creator plus packs only reaches Pro's 18,000 credits at $69 a month, against Pro's $39.

**The trial is bounded.** It gets 1,000 credits once, at most $0.80 of AI, and 10 pictures at draft or standard
quality, at most $0.40 of that. At a 10% conversion rate, trials cost about $8 of AI per paying account.

## Break-even and scale

Assumed mix: 60% Creator, 30% Pro, 10% Studio, half on annual billing. At typical burn that comes to about
**$28 of revenue and $19 of contribution per account per month**, or $14 at full burn.

| Paying accounts | MRR | Fixed infra | Profit/mo |
| --- | --- | --- | --- |
| 25 | ~$710 | $830 | ~–$360 |
| 50 | ~$1,400 | $830 | ~$110 |
| 100 | ~$2,800 | $830 | ~$1,050 |
| 250 | ~$7,100 | ~$900 | ~$3,800 |
| 500 | ~$14,200 | ~$1,350 (SQL to 4 vCore) | ~$8,000 |
| 1,000 | ~$28,400 | ~$1,900 (Redis B3, more replicas) | ~$16,800 |

- **Hosting break-even: about 44 paying accounts.** That is about 30 on the lean launch, and 59 if every account
  spends every credit.
- **Mix matters more than any other assumption.** At 80% Creator, 15% Pro and 5% Studio, contribution falls to
  about $12 per account and break-even rises to about 67 accounts.
- **$5k/mo profit at about 320 accounts. $10k/mo at about 610.**
- **Support is the real limit.** At 1,000 accounts the minutes above add up to about 58 hours a month.
- **The burn rate is a guess.** There is no production usage yet. Read the real burn from `AccountAiUsageEntry`
  after the first month of paying accounts and redo this table.

## Competitors (checked 2026-10-28)

| Product | Price |
| --- | --- |
| ChatGPT | Go $8; Plus $20; Pro $100 or $200 |
| Canva Pro | about $15 monthly or $120/yr; about 500 AI credits a month, no top-ups |
| Midjourney | Basic $10, Standard $30, Pro $60, Mega $120; about 20% less billed annually |
| Jasper | Pro about $69 monthly or $59 annually per seat; whether Creator ($49 / $39) still exists is unconfirmed |

A food creator's usual AI stack is ChatGPT Plus for the writing, Canva Pro for the graphics, and often Midjourney
for the pictures. That costs about $35–65 a month across three tools, none of which knows what a recipe is.
Creator at $9–12 sits below any one of them. Pro at $39–49 matches the stack and replaces it with one recipe-aware
workspace. Studio at $79–99 costs more than Jasper Pro, so it has to sell on 4K pictures, three running at once
and 150 a day. Those are what a creator producing at volume needs.

## What separates the plans

Recommendation: **decide "no feature gating."** Every plan gets the whole workspace, and plans differ only in
credits, picture caps and quality classes, as they do today. The landing copy already promises "Every plan
includes the whole workspace" but marks it *Not built*, because it holds only if this is decided.

Gating features by plan would add "why is this greyed out?" tickets on the plan with the thinnest margin. It would
also mean B-54's period-end downgrades have to lock creators out of things they built in the middle of a project.
Quality classes already give a clean, metered reason to upgrade.

## Other recommendations

- **Raise `Ai:Quota:PlatformImageSpendPerDay` before any paid launch.** At $25 a day, a single Studio account can
  trip it: 125 high-4K pictures cost $28.75 at list price and fit inside both Studio's credits and its daily cap
  of 150. Two Pro accounts at their cap on high-2K come to $22.50. Once the ceiling trips, every creator's
  pictures are refused until the end of the UTC day. At the assumed mix, average picture spend is about $3.30 per
  account a month, so the ceiling equals an average day's spend at about 230 accounts. Set it from the number of
  paying accounts: at least $35 plus $0.25 per paying account a day. Review it monthly.
- **Cap Log Analytics before deploying.** Set a daily ingestion cap and trace sampling in ServiceDefaults.
  Unsampled telemetry is the one fixed cost that can double without anyone noticing.
- **Launch lean.** Use serverless SQL and one `web` replica until about 100 accounts. That saves about $270 a
  month while contribution is still smaller than fixed cost.
- **Show annual first, at the current discount.** Annual billing roughly halves Stripe's cut on Creator ($0.35
  against $0.73 a month) and lowers churn. Do not deepen the discount: Creator annual is already the floor.
- **Venice price changes are already safe.** A picture costs its rate (B-55), so a price rise means fewer
  pictures per plan, not a loss. `PictureClassRateTests` then fails any plan's published example counts that no
  longer fit its credits, and those counts must be revised in the same change. Re-read Venice's prices quarterly.
- **Optional founding offer:** Pro at $29 a month annual, locked for life, for the first 100 accounts. It still
  covers full burn (1.9×) and contributes about $15 at typical burn. Put the offer on Pro, not Creator: a
  discount on the thinnest tier has no room.
- **Keep embeddings unmetered for now.** At $0.02 per million tokens, embedding a 1,000-recipe workspace costs
  about two cents.

Sources: [Azure Retail Prices API](https://prices.azure.com/api/retail/prices) (Container Apps, SQL Database,
Managed Redis, Log Analytics, Storage; queried 2026-10-28), [Container Apps billing](https://learn.microsoft.com/en-us/azure/container-apps/billing),
[Azure SQL pricing explained](https://spendark.com/blog/azure-sql-pricing-explained/),
[Stripe Billing fee](https://usagebox.com/articles/stripe-billing-fees-2026-the-07-percent-math),
[ChatGPT](https://www.cometapi.com/chatgpt-pricing-2026-free-vs-go-vs-plus-vs-pro/),
[Canva](https://piktochart.com/blog/canva-pricing-2026-costs-raised/),
[Midjourney](https://eesel.ai/blog/midjourney-pricing),
[Jasper](https://www.layer3labs.io/guides/jasper-ai-pricing). Venice and Foundry model prices are taken from
`Ai:Cost` in `src/CreatorPantry.ApiService/appsettings.json` (read 2026-10-26).
