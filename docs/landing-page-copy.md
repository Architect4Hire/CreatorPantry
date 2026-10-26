# CreatorPantry landing page — outline and copy (LP.2)

**Status: draft, awaiting owner approval.** Written 2026-10-24 for LP.2 of
`docs/prompts/creatorpantry-landing-pricing-scrub-prompts-extensions.md`, with the owner's answers of the same
day folded in (see "Owner answers" at the end). LP.3 to LP.10 build from the approved version of this file.
Nothing here is a component.

## How to read the sources

Every claim carries one of four tags. A claim tagged anything but **Shipped** cannot render until its tag
changes.

| Tag | Means | Can render? |
| --- | --- | --- |
| **Shipped** | Code and tests on `dev` today. The path is the evidence. | Yes |
| **Decided** | An owner decision written in the AI pricing prompts ("Decisions already made"), not yet in a decision record. AP.1.1 turns it into one. | After AP.1.1 |
| **Catalogue** | A value read from the plan catalogue at runtime (AP.4.1). Copy names the field, never the number. | After AP.4.1, behind `Landing:Pricing` |
| **Not built** | Needs a named prompt first. | No |

Paths are abbreviated: `web/` is `src/web/src/app/`, `dom/` is `src/CreatorPantry.Domain/`, `AP` is
`docs/prompts/1.creatorpantry-ai-pricing-scrub-prompts.md`.

## Three things the copy has to respect

1. **AI is off until the Owner switches it on.** `Ai:Tasks:Enabled` is empty outside Development, so a new
   workspace has no AI until its Owner turns features on in Workspace settings
   (`dom/Modules/Ai/Business/IAiTaskGate.cs`, `web/features/workspace-settings/`). The owner confirmed this is
   the intended product behaviour (answer 2), so the copy presents it as control, not as a limitation, and
   never says AI is running the moment you sign up.
2. **Publishing is not self-serve yet.** Buffer is the one provider. It is configured per deployment for
   exactly one workspace and is off by default (`.claude/rules/aspire.md`, `web/services/publishing.service.ts`).
   A new sign-up cannot schedule a post, so publishing carries the badge **Coming to every account**
   (answer 1).
3. **Images are not charged against credits yet.** Today credits cover text AI only; images are capped at 200
   a workspace a day and cost no credits (`dom/Managers/Limits/LimitsOptions.cs`; master library 12.10l
   "Not done"). Every pricing promise about images waits for AP.2.5.

Two lines on today's page overclaim and are not carried forward: "Save **named** versions" (versions are
numbered, with an optional reason: `dom/Modules/Recipes/Data/Entities/RecipeVersion.cs`) and "equipment stay
exactly as you write them" (the editor has no equipment field).

---

## 1. Sticky nav

**Items:** CreatorPantry logo (home) · Tour · Pricing · FAQ · Sign in · **Start free** (appears once the
hero's CTA has scrolled away).

| Claim | Source |
| --- | --- |
| Tour, FAQ anchors | Sections 4 and 10 of this page |
| Pricing anchor | Only while `Landing:Pricing` is on (LP.6); otherwise the item is absent, not disabled |
| Sign in → `/sign-in`, Start free → `/sign-up` | **Shipped** `web/app.routes.ts` |

**Motion:** goes from transparent to paper with a hairline and backdrop blur after the hero; the current
section's link is underlined. No movement under reduced motion; the state change is instant.

---

## 2. Hero

**Headline:** The workspace where your recipes become *everything else.*
(Keep today's line. "everything else" gets the fruit-accent underline.)

**Subheadline:** Write and version your recipes, then turn them into pictures, social posts, blog articles
and newsletters. AI drafts what you ask for, and nothing changes until you accept it.

**CTAs:** Start free · Sign in. Once the trial is enforced (AP.7.1), the primary becomes **Start your
`{trial.days}`-day free trial**.

**Note under the CTAs:** AI is there when you want it. Your workspace's Owner switches on the features you use.

**Proof card (overlay):** a static capture of a real AI proposal from the AI Recipe Studio, with its
"Create recipe from this draft" step visible.

| Claim | Source |
| --- | --- |
| Write and version recipes | **Shipped** `web/features/recipes/recipe-editor.component.ts`; every save is a numbered, unchangeable version (`RecipeVersion.cs`) |
| Pictures | **Shipped** Image Studio, `web/features/image-studio/` |
| Social posts | **Shipped** Social Studio, `web/features/social-studio/` |
| Blog articles and newsletters | **Shipped** long-form, `web/models/long-form-pipeline.models.ts` (`BlogArticle`, `NewsletterIssue`) |
| Nothing changes until you accept it | **Shipped** accept steps: `AiProposalsController` disposition, `RecipeDraftRequestsController` acceptance, `LongFormDraftsController`, `dam-assets/from-generated-image`; `.claude/rules/ai.md` |
| "Start free" | **Shipped** today: no billing exists and no account expires |
| "`{trial.days}`-day free trial" | **Decided** 14 days (owner answer 3); **Catalogue** field, rendered only after AP.7.1 enforces it. Until then the page says no trial length, because nothing ends one |
| Owner switches AI on | **Shipped** `IAiTaskGate.cs`, Workspace settings AI switches (Owner only); owner answer 2 |

**Motion:** the photo collage drifts a few pixels with the pointer; the proof card settles in once. Both stop
under reduced motion and on coarse pointers.

---

## 3. What's real today (value props)

**Section headline:** Everything here works today

**Intro:** No roadmap slides. Each of these is in the product now, and each card opens its part of the tour.

| Badge | Card headline | Body | Source |
| --- | --- | --- | --- |
| Recipes | Your recipe, in your words | Ingredients, steps, times and yields stay exactly as you write them. Every save is a numbered version you can compare, restore or copy into a new draft. | **Shipped** `recipe-editor.component.ts`, `RecipeVersion.cs`; recipes.md "Preserve creator-entered…" |
| AI Recipe Studio | From an idea to a first draft | Describe the dish and get a handful of concepts. Pick one for a full draft, and it becomes a recipe only when you say so. | **Shipped** `web/features/ai/recipe-concept-studio.component.ts`, `recipe-first-draft-review.component.ts` |
| Image Studio | Food pictures on demand | Plan a look, write or generate the prompt, and make up to four pictures a run. Keep the ones you like in your picture library. | **Shipped** `image-studio.component.ts`; `MediaPolicy.MaxVariantsPerOperation = 4` |
| Social posts | A post for every channel | One post each for Instagram, TikTok, Pinterest, Facebook, X and Threads, plus a blog intro and a newsletter blurb, each held to that channel's length and written in your brand voice. | **Shipped** `social-studio.component.ts`; `dom/Managers/Reference/ContentChannelProfiles.cs`; `BrandContextAssembler.cs` |
| Long-form | Blog posts and newsletters | Approve an outline, draft it section by section, edit anything, and download it as Markdown or HTML. | **Shipped** `web/features/long-form/`, `LongFormExportsController` |
| Brand voice | Sounds like you | Set up your brand once: goals, style and examples. Your voice guide is versioned, and "Test my style" shows a draft with and without it. | **Shipped** `web/models/brand-setup.models.ts`, `brand/style-guide/:id/test-drive` |
| Planning | See what's next | A content board from To do to Approved, plus My Day and My Week for your own tasks. Move cards by drag or from the keyboard. | **Shipped** `content-board.component.ts`, `my-day.component.ts`, `my-week.component.ts` |
| Exports | Take it anywhere | Download a recipe as PDF, Markdown or JSON-LD for your blog's recipe card. | **Shipped** `IRecipeExportFacade.cs`; approved or ready versions only |
| Coming to every account | Schedule to your social channels | Schedule finished posts through Buffer, each with an explicit confirmation and a time you choose. | **Shipped** but deployment-configured for one workspace (`publishing.service.ts`). Badge per owner answer 1 |

**Motion:** cards rise into place in a short stagger as the section enters; hover lifts a card to
`--cp-shadow-md`. Under reduced motion they are simply there.

---

## 4. Product tour (LP.5)

**Section headline:** Take the tour

**Intro (added at LP.5):** Every picture here is a screen of the product, taken with a sample workspace of our
own. Source: **Shipped** `scripts/capture-tour.mjs`, which photographs the running product, refuses any workspace
but the sample one, and refuses a screen with nothing on it.

One panel per tab; each has a promise, three bullets and a real screenshot of seeded sample data.

**AI Recipe Studio** — *Start with an idea, finish with a recipe.*
- Brief it like a test kitchen: course, cuisine, season, audience, time, what's on hand and what to leave out.
- Compare a few concepts before you commit to one.
- Revise, adapt or substitute later, accepting each change one at a time.

Source: **Shipped** `recipe-concept-studio.component.ts`, `recipe-first-draft-review.component.ts`; accept or
decline per change via `AiProposalsController`.

**Image Studio** — *Pictures that look like your blog.*
- Plan a look or a photography concept first.
- Let AI write the prompt, or write your own.
- Keep the best of up to four per run; the rest expire on their own.

Source: **Shipped** `image-studio.component.ts`; unsaved pictures expire after 14 days.

**Content Pipeline** — *One recipe, a whole week of content.*
- Pick a recipe or dish and the day's theme.
- Choose a picture idea and make the pictures.
- Accept a post for each channel and drop a card on your board.

Source: **Shipped** `web/models/content-pipeline.models.ts` steps `about`, `picture`, `images`, `finish`.

**Long-form** — *The long read, without the blank page.*
- A blog article or a newsletter issue.
- An outline you rename, reorder and approve before a word is drafted.
- Download it as Markdown or HTML when it's ready.

Source: **Shipped** `long-form-pipeline.models.ts`, `long-form-document.service.ts`. SEO suggestions are left
out: off by default (`content.seo-package`).

**Brand guide** — *Your voice, written down.*
- A guided setup: goals, style, examples, review.
- A brand library for the documents and pictures you already have.
- A versioned guide; nothing changes until you activate a version.

Source: **Shipped** `brand-setup.models.ts`, `brand/library`, style-guide history and activation confirmation.

**Publishing** — *Out the door, on your say-so.*
- Every post is confirmed by you, with a time you choose.
- See each one go from scheduled to published, or why it needs attention.
- Cancel anything that hasn't gone out yet.

Source: **Shipped** `IPublishingRecordFacade.cs` statuses and cancel. The panel carries the **Coming to every
account** badge (owner answer 1).

**Motion:** the screenshot crossfades between panels; instant under reduced motion. Arrow keys move between
tabs.

---

## 5. "One recipe, everything" flow

**Headline:** One recipe in. Everything else out.

**Body:** Start from a recipe you already have. CreatorPantry plans the picture, makes it, writes a post for
each channel in your voice, and puts the result on your board for review. Nothing goes out until you
schedule it or download it.

**Steps (a horizontal ribbon):** Recipe → Picture idea → Pictures → Posts per channel → Board card →
Download or schedule

| Claim | Source |
| --- | --- |
| Pipeline steps in that order | **Shipped** `content-pipeline.models.ts`, `content-pipeline-shell.component.ts` |
| Nothing goes out from the pipeline | **Shipped** the pipeline's own copy, "Nothing goes out from here." |
| In your voice | **Shipped** `BrandContextAssembler.cs` (social posts and image prompts read the active guide) |
| Schedule | **Shipped**, deployment-configured; "Coming to every account" (owner answer 1) |

**Motion:** a line draws from step to step as the ribbon scrolls into view. Under reduced motion all steps
show at once.

---

## 6. Draft vs final images — **Removed**

Taken off the page (2026-10-25). The product makes one kind of picture: `VeniceImageGenerator` sends the one
configured model and no size, quality or resolution, so each model's own default applies. Draft, standard,
high, 2K and 4K are an AP.3.1 roadmap item that has not been built, and the page had begun to describe them
as a choice a creator could make — on the plan cards, in the comparison table and in the credit menu.

No page copy names a picture quality until AP.3.1 ships and a creator can choose one in the image studio.
The catalogue's `qualityClasses`, `rates.imageDraft` and `rates.imageHigh` are still in the API contract and
the development configuration, but the page reads none of them.

---

## 7. Pricing — **Catalogue**, behind `Landing:Pricing`

**Headline:** Plans that grow with what you make

**Body:** Every plan includes the whole workspace. What changes is how much AI work a month includes.

**Per plan card:** `{plan.name}` · `{plan.monthlyPrice}` a month, or `{plan.annualMonthlyPrice}` billed
yearly (`{plan.annualSaving}` saved) · `{plan.credits}` AI credits a month · "about `{plan.exampleMix}`" ·
up to `{plan.dailyImageCap}` pictures a day (on the trial, "`{plan.trialImageCap}` in all" too) · **Start with `{plan.name}`** → `/sign-up?plan={plan.key}`.
No picture quality, on a card or in the comparison table (section 6).

"Recommended for most food bloggers" appears on the plan the catalogue flags (`{plan.recommended}`).

| Claim | Source |
| --- | --- |
| Every plan includes the whole workspace | **Not built** — AP.4 only proposes credit, cap and quality differences. Holds only if AP.1.1 decides no feature gating |
| Every figure | **Catalogue** AP.4.1; the proposed values in AP "Proposed, awaiting the owner" are never typed here |
| Recommended badge | Landing decision 2 |
| Plan per account, across workspaces | **Decided** AP decision 4 |

**Motion:** the monthly/annual radio group counts the prices to their new value; instant under reduced
motion.

---

## 8. What do credits buy? and the plan estimator — **Catalogue**

**Headline:** What a credit buys

**Body:** Credits are how CreatorPantry measures AI work, so every plan's allowance means the same thing
whichever model does the job. Your credits belong to you, not to a workspace, and they're shared across every
workspace you work in.

**Menu board:** a text draft `{rates.textDraft}` · a long-form section `{rates.longFormSection}` · a picture
`{rates.imageStandard}`. One picture line, because there is one kind of picture (section 6).

**Estimator:** "Pictures a month" and "AI writing a month" sliders. "Your **estimate** fits `{plan.name}`, with
about `{headroom}` credits to spare." Beyond the top plan: "More than `{topPlan.name}` includes. Add a pack,
or write to robert@creatorpantry.com and we'll work something out." (Contact per owner answer 6.)

| Claim | Source |
| --- | --- |
| Credits measure AI work the same whichever model | **Shipped** `AiQuotaUnit.Credits`, `dom/Modules/AiUsage/Managers/AiQuotaOptions.cs` |
| Belong to you, shared across workspaces | **Shipped** USAGE-001, `AccountAiQuota` |
| Picture rates | **Not built** AP.2.4 (images cost no credits today) |
| Every figure | **Catalogue** AP.4.1 plus per-action rates (LP.7) |

**Motion:** the menu board's prices tick in once; the estimator's result counts up. Instant under reduced
motion.

---

## 9. Packs and the no-surprise-bill promise

**Headline:** No surprise bills. Ever.

| Tile | Copy | Source | Renders after |
| --- | --- | --- | --- |
| Prepaid | You pay for credits before you use them. Nothing is billed afterwards. | **Decided** AP decision 3; PRICE-009 | AP.1.1 and AP.6 |
| At zero, AI waits | When your credits run out, AI requests stop before they start. Your recipes, pictures and drafts stay yours to edit. | **Shipped** for text (USAGE-006, `IAiRequestQuotaGate`; usage page: "You can keep writing and editing recipes as usual."). **Not built** for pictures (AP.2.5) | AP.2.5 |
| Top-up is yours to switch on | Automatic top-up is off unless you turn it on, and you set its monthly limit. | **Not built** AP.6.4 | AP.6.4 |

**Packs:** `{pack.credits}` credits for `{pack.price}`, used after your plan's credits. **Catalogue** AP.5.
Under the list, once: "A pack is credits you have bought, so they keep until you spend them. They never
expire." **Decided** (owner, 2026-10-24) — a pack has no term, and the catalogue carries no expiry field to
set one with.

**Motion:** tiles lift on hover only.

---

## 10. FAQ

Questions answerable today render now. Questions marked **Catalogue** or **Not built** wait for their
prompt, and LP.9's rule stands: anything undecided is left out.

| # | Question | Answer | Source |
| --- | --- | --- | --- |
| 0 | Is AI switched on when I sign up? | Not until you want it. Your workspace's Owner chooses which AI features to switch on, one at a time, in Workspace settings. Everything else works without them. | **Shipped** `IAiTaskGate.cs`, Workspace settings; owner answer 2 |
| 1 | Does AI change my recipes on its own? | No. AI drafts proposals. A draft becomes a recipe only when you create it, and a suggested change applies only when you accept it — one change at a time. "Review with AI" only ever reports findings. | **Shipped** accept flows listed in section 2; `recipe-review-request.component.ts` |
| 2 | Is scaling done by AI? | No. Scaling and unit conversion are plain arithmetic in our own code, so the numbers are the same every time. A scaled recipe is a preview you can check before you use it. | **Shipped** `recipe-scaling-preview.component.html`; `.claude/rules/ai.md` "Deterministic work stays deterministic" |
| 3 | Who can see my recipes? | Only the people in your workspace. Your recipes, drafts and pictures are never used to inform AI work in anyone else's workspace. | **Shipped** `tenancy.md`; `BrandContextAssemblerTests`, `RecipeConceptsWorkerIsolationTests` |
| 4 | Do you keep the prompts I write? | Your prompts and drafts are stored in your workspace so you can come back to them. We don't write their contents to our logs. | **Shipped** `PromptEnvelope.Describe`, `PromptEnvelopeTests` |
| 5 | Are AI pictures filtered? | Yes. Safe mode is always on, and a picture the provider flags is never shown to you. | **Shipped** `VeniceImageGenerator.cs` (`safe_mode: true`), `VeniceImageGeneratorTests` |
| 6 | Can CreatorPantry tell me a dish is allergen-free or safe? | No, and it won't pretend to. Dietary and allergen notes describe what your recipe says; they're never a guarantee. Always check every label. | **Shipped** `DietaryAllergenAnalyzer.CrossContactDisclaimer`; B-36; `check-safety-copy.mjs` wording limits |
| 7 | What is a credit? | Section 8's body. | **Shipped** + **Catalogue** |
| 8 | What happens when I run out? | Tile 2 of section 9. | **Shipped** for text; pictures after AP.2.5 |
| 9 | Do unused credits roll over? | Plan credits don't carry over; each month starts fresh. | **Shipped** `DefaultCarryOver = None`; restate from AP.1.1 |
| 10 | Do packs expire? | No. A pack is credits you have bought, so they keep until you spend them. | **Decided** (owner, 2026-10-24) |
| 11 | Can I cancel any time? | **Not built** (AP.6.2, downgrade timing still open). Omitted. | — |
| 12 | Who owns what AI makes for me? | You do, as far as the law allows. Your recipes, pictures and drafts are yours, and so is what CreatorPantry generates for you. We only use them to run the service for you. The Terms of Service say exactly how. | Draft ToS §5 (`docs/legal/terms-of-service.md`). Renders only once the reviewed Terms are published, and links to them |
| 13 | What does the trial include? | The whole workspace for `{trial.days}` days. | **Decided** 14 days (owner answer 3); renders after AP.7.1. **Decided** what a trial may generate: up to `{trial.trialImageCap}` pictures in all, no card on file (`ai-pricing.md` B-52); enforced by AP.7.1 |

Seven answers render today (0–6). 7–10 and 13 render with pricing, and 12 with the published Terms. LP.9 asks
for 8 to 10 answers; the page reaches that once pricing is on, so LP.9 picks the ten that are live.

**Motion:** native `details` disclosure; the marker rotates, nothing else moves.

---

## 11. Trust

**Headline:** Built for creators, not clicks (keep)

**Body:** CreatorPantry exists to give food creators a real production workspace, not another feed to perform
for. It's built for people who develop recipes and turn them into everything else, and who want to keep
control of both.

**Stage line:** CreatorPantry is early in development, and we're building it in the open. We're launching
by the end of 2026.

**Proof note:** We don't have customer stories to share yet. When we do, they'll be real ones.

| Claim | Source |
| --- | --- |
| Mission and audience | CLAUDE.md Scope |
| Early development, building in the open, launching by end of 2026 | Owner answer 4, 2026-10-24. The launch line comes off the page once it is no longer future tense |
| No customer stories | Owner-supplied in L.6 |

**Motion:** none. This section stays still on purpose.

---

## 12. Final CTA

**Headline:** Your next recipe could be everything else, too.

**CTAs:** Start free · Sign in

**Motion:** the evergreen band's background shifts once between two fruit tints on entry; still under
reduced motion.

---

## 13. Footer

**Links in `<nav aria-label="Footer">`:** Tour · FAQ · Pricing (when on) · Sign in.
**Legal:** Terms of Service · Acceptable Use Policy · Privacy Policy. Each stays plain text with "(coming
soon)" until its reviewed page is published. All three are drafted as mock-ups in `docs/legal/`. Pricing
does not go live without all three (LP.6).
**Contact:** robert@creatorpantry.com (owner answer 6).
**Line:** © {year} CreatorPantry.

---

## Owner answers (2026-10-24)

| # | Question | Answer | Where it landed |
| --- | --- | --- | --- |
| 1 | Publishing for new sign-ups | Badge it **Coming to every account**. | Sections 3, 4, 5 |
| 2 | AI on by default? | No. AI is off by default and the Owner turns features on as needed. | "Three things" 1; hero note; FAQ 0 |
| 3 | "Start free" after billing | A **14-day trial**. | Hero CTA after AP.7.1; FAQ 13. Record it in AP.1.1 as decided, not proposed |
| 4 | Stage | Early in development, building in the open, launching by the end of the year. | Section 11 |
| 5 | Ownership of AI output | Mock up a Terms of Service and an Acceptable Use Policy. | `docs/legal/terms-of-service.md`, `docs/legal/acceptable-use-policy.md`; FAQ 12 |
| 6 | Contact | robert@creatorpantry.com | Section 8 estimator; footer |

## Still open

- **Legal review.** The Terms of Service, Acceptable Use Policy and Privacy Policy are mock-ups in
  `docs/legal/`. None is published, and FAQ 12 and the footer links wait until a lawyer has reviewed them and
  they exist as pages.
- **Google Fonts.** The Privacy Policy has to disclose that visitors' IP addresses go to Google for fonts,
  unless the fonts are self-hosted (LP.11 can do that as part of the performance pass).
- **Cancellation and refunds.** Downgrades and cancellations take effect at period end (`ai-pricing.md` B-54),
  but a refund policy is undecided, so FAQ 11 and the matching ToS clauses stay bracketed. Pack expiry is no longer among the open questions: packs do not expire
  (owner, 2026-10-24).
- ~~**What a trial may generate.**~~ Decided 2026-10-26: a lifetime cap of 10 trial pictures, no card on file
  (`ai-pricing.md` B-52). The pricing card and comparison table state it from the catalogue; AP.7.1 enforces it.
