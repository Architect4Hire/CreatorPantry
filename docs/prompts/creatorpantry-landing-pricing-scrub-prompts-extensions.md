# CreatorPantry — Landing Page Review, Showcase and Pricing SCRUB Prompts

Atomic prompts that review the public landing page, bring its claims up to what CreatorPantry actually
ships today, and add a feature showcase, plans and pricing, a credits explainer and an FAQ. The page gets
noticeably bolder and more alive, in its existing style. For use with Claude Code and the CreatorPantry
`.claude/` toolkit. Intended path in the repo: `docs/prompts/creatorpantry-landing-pricing-scrub-prompts.md`.

This document follows `docs/prompts/creatorpantry-landing-page-scrub-prompts.md` (L.1–L.9, all done) and
depends on `docs/prompts/creatorpantry-ai-pricing-scrub-prompts.md` (AP.*) for the plan catalogue. It does
not redefine anything in `CLAUDE.md`, `.claude/rules/` or `src/web/DESIGN-SYSTEM.md`.

## Reusable SCRUB skeleton

```text
SCOPE:        one observable change and the exact repository seam it touches
CONSTRAINT:   stack, rule files, skills, requirement IDs, and prerequisites
RESTRICTION:  explicit exclusions and invariants that must not be weakened
UTILIZATION:  skills, read-only reviewers, and tools to invoke
BEHAVIOR:     inspect, plan, wait for approval, implement, test, and report
```

Run one prompt at a time, in order. Mark a prompt `- done` only after its verification passes, and add a
delivery note under it when what shipped differs from what was asked.

## The ask

Owner, 2026-10-24: "make SCRUB Prompts to review the landing page and build out this extra detail
following the style of the page and make it extra snazzy and sexy." "This extra detail" means the plans,
AI credits, prepaid packs and the draft/final image modes from the AI pricing work.

| ID | Requirement | Prompt |
| --- | --- | --- |
| LND-001 | Every claim on the page matches what the product ships today. No stale "in the works" labels, no overstated ones. | LP.1, LP.3 |
| LND-002 | The page shows the real product: the AI Recipe Studio, Image Studio, Content Pipeline, long-form, brand voice and publishing, using real screenshots. | LP.4, LP.5 |
| LND-003 | A pricing section shows every plan, monthly and annual, with what its AI credits buy in plain terms. | LP.6 |
| LND-004 | Visitors understand credits, packs and the no-surprise-bill promise without reading fine print. | LP.7, LP.8 |
| LND-005 | Visitors can estimate which plan fits their output. | LP.7 |
| LND-006 | An honest FAQ answers cost, cancellation, AI ownership and safety questions. | LP.9 |
| LND-007 | The page feels premium and alive (bold type, colour, depth, motion) and stays fast and accessible. | LP.4, LP.10, LP.11, LP.11A |

## Decisions already made

1. **The page's existing style is the brief.** Fredoka display headings set big, DM Sans for everything
   read, paper-and-warm-ink surfaces with hairline edges and layered soft shadows, evergreen for actions,
   and tomato pink, carrot orange, eggplant purple and blueberry blue as accents. The logo and the food
   photography bring the colour. "Snazzy" means pushing that further: larger type, richer layering,
   colour blocking in the fruit accents, and purposeful motion. It does not mean a new visual language,
   dark-glass SaaS gradients or stock illustrations.
2. **Honesty rules from L.2 and L.6 stand.** No invented testimonials, customer counts, ratings, press
   logos or "most popular" badges. A plan may be marked "Recommended for most food bloggers". That is a
   recommendation, not a statistic.
3. **Prices are never hard-coded in the web.** They come from the server's plan catalogue (AP.4.1). Until
   AP.4.1 ships, the pricing sections stay behind a feature flag and don't render.
4. **Motion is progressive enhancement.** Everything reads and works with motion off, and nothing moves
   under `prefers-reduced-motion`.

## How this list was built

A read of `main` at `a9c3d34` on 2026-10-24. Re-find files before trusting a line number.

| Area | What exists | Finding |
| --- | --- | --- |
| Composition | `landing.component.html`: hero, value props, image trio, how-it-works, mirrored image trio, trust, final CTA, footer. | Sound structure; no navigation, pricing or FAQ. |
| Value props | `DEFAULT_VALUE_PROPS` marks content projects, AI drafts and publishing **"In the works"**. | **Stale.** AI Recipe Studio, Image Studio, the Content Pipeline, long-form, the brand guide and Buffer publishing now ship. The page undersells the product. |
| How it works | A `note` input says "AI drafting and publishing integrations are in active development." | **Stale**, same reason. |
| Photography | 12 JPEGs in `public/images/food`, **0.5–3.7 MB each, about 16 MB in total**, picked at random by `FoodImageService`. | Biggest performance risk on the page. No responsive sizes, no modern formats, and random order makes LCP and screenshots non-deterministic. |
| Hero | Headline, subheadline, two CTAs, logo, an empty media slot. | Room for a far stronger first screen. |
| SEO/meta | One site-wide description in `index.html`; its static `<title>` is "CreatorPantry Design System". The `''` route has no title, so the tab reads "CreatorPantry" once the app runs. | No canonical, Open Graph, social card or `robots.txt`, and no prerender, so a scraper sees an empty `<cp-root>`. |

## Prerequisites already satisfied

- L.1–L.9: the public `''` route, the anonymous/authenticated split, the section components and their specs.
- `@creator-pantry/ui`: `CpButtonComponent`, `CpCardComponent`, `CpBadgeComponent`, tokens for space, type,
  radius, shadow (`-sm`, `-md`, `-lg`), motion (`--cp-duration-*`, `--cp-ease`) and z-index.
- `global.css` honours `prefers-reduced-motion: reduce`.
- `playwright-cli` skill for screenshots and journeys; the `design-review` agent.

## Out of bounds for this document

- Checkout. The pricing CTAs go to `/sign-up` with the chosen plan as a query parameter; billing itself is
  AP.6.
- A blog, docs site, or separate marketing app.
- Third-party analytics or tracking scripts. Adding one needs its own decision.
- Fabricated proof of any kind (see decision 2).

## Checkpoints

| After | Demonstrable result |
| --- | --- |
| **LP.3** | Every capability claim on the page is true today, and a spec fails if a claimed route becomes a placeholder. |
| **LP.5** | A visitor can tour the real product from the landing page. |
| **LP.9** | Pricing, credits, packs and the FAQ render from the server catalogue behind the flag. |
| **LP.12** | The page meets its performance budget and WCAG 2.2 AA, in both themes, and design-review passes. |

---

## LP.1 Landing page review (report only) - done

```text
SCOPE: Review the current landing page end to end and return a findings report: copy claims against
shipped routes in app.routes.ts and the features folder; visual hierarchy and how far it falls short of
the DESIGN-SYSTEM.md voice; accessibility (landmarks, heading order, focus, contrast in both themes, 200%
zoom, narrow widths); performance (Lighthouse mobile run: LCP, CLS, total bytes, image weight); and SEO
meta. Include screenshots at 375, 768 and 1440 px in light and dark.
CONSTRAINT: LND-001, LND-007; .claude/rules/frontend.md and design-system.md; src/web/DESIGN-SYSTEM.md;
playwright-cli skill.
RESTRICTION: Report first. Change nothing in this prompt.
UTILIZATION: design-review and public-facing a11y checks; playwright-cli for screenshots and Lighthouse.
BEHAVIOR: Run the checks, present findings ranked by severity with a fix mapped to a later LP prompt, and
wait for approval before LP.2.
```

*Delivery note (2026-10-24): reported, nothing changed. The Web host served no bundle in that session, so
Lighthouse 12.8.2 (mobile, simulated) ran twice against a production build served gzipped from a scratch
folder. The session check failed CORS from that origin, so the anonymous guard let the page through at once;
on a real host it waits a round trip, and real LCP is somewhat worse. Baseline for LP.11: Performance 71 and
53, LCP 12.5 s and 12.0 s, CLS 0 and 0.379, TBT 0 ms, initial JS about 97 KB gzipped; Accessibility 100.
Lighthouse's byte total includes photos its full-page screenshot scrolls into view, so first-load image bytes
were measured separately at 412 px with no scroll: 4.4 MB light, 6.3 MB dark. Findings, most severe first, with
the prompt that now carries each:*

1. *Photos at up to 4672×7008 shown about 370 px wide (110 px at 375), random each load → LP.11.*
2. *LCP is the hero logo PNG (340–400 KB, 1312×1199, shown at ≤544 px), discovered only once the lazy landing
   chunk runs and then held behind the photos → LP.4, LP.11.*
3. *CLS 0.379 in one run: the logo `<img>` has no width or height, plus the font swap → LP.4.*
4. *Every "In the works" label and the how-it-works note are stale: each Content Pipeline and Brand setup step
   has a real component, and the placeholder components are unreachable fallbacks → LP.2, LP.3.*
5. *Overclaims: "import a recipe" (only ingredient lines can be pasted), "connect the channels you use"
   (publishing is Buffer only, configured per deployment for one workspace, off by default), and scaling
   listed as something AI proposes (it is deterministic code, `ai.md`). "Building it in the open" is
   unverified → LP.2, LP.3.*
6. *Focus ring contrast 1.72:1 light and 2.71:1 dark against the 3:1 of WCAG 1.4.11: `--cp-focus` mixes the
   primary at 38–42% (`themes.css`). Library-wide; Lighthouse does not catch it → LP.11A.*
7. *No prerender, static `<title>` is "CreatorPantry Design System", no canonical, Open Graph, Twitter card or
   `robots.txt` → LP.10.*
8. *No banner, nav or skip link; the footer renders an empty `<nav aria-label="Footer">` → LP.10.*
9. *Hierarchy: h1 34 px at 375 and 768 and 56 px at 1440; card body 13–15 px, proof note 11 px; one accent
   (evergreen) and shadow on the photos only; empty hero media slot; at 768 the first screen is mostly logo;
   in dark the paper cards are the brightest thing on screen. Headings ask for weight 400, but Fredoka loads
   500–700 → LP.4, LP.11.*
10. *The value-prop grid is a private `ul.grid`; literal rem widths and `::ng-deep` in the hero. The page sits in
   `OUTSIDE_SHELL` in `check-page-contract.mjs`, and DESIGN-SYSTEM.md has no shape for a public page, so
   nothing enforces either → LP.3, LP.12.*
11. *The legal links are "(coming soon)" placeholders. A paid plan's sign-up needs real Privacy and Terms
   pages → LP.6.*
12. *`CpLogoComponent` is not OnPush, breaks the `.component.ts` naming, and takes a raw inline-style string
   → LP.4.*

*Passing: AA text contrast in both themes (lowest 4.56:1), no horizontal scroll at 375, 768, 1440, 200% zoom
or 320 px, h1 → h2 → h3, targets at least 40 px, nothing animates, decorative photos have empty alt and
reserved sizes. Not adopted from design-review: holding the h1 to `--cp-font-size-title`, which caps it below
what LP.4 asks for.*

## LP.2 Narrative and section plan

```text
SCOPE: Write the updated page outline and copy, section by section: sticky nav; hero; "what's real today"
value props; product tour; "one recipe, everything" flow; draft vs final images; pricing; credits
explainer and plan estimator; packs and the no-surprise-bill promise; FAQ; trust; final CTA; footer. For
each, give the headline, body copy, the capability or catalogue field each claim traces to, and the
motion intent.
CONSTRAINT: LND-001..006; CLAUDE.md Scope; .claude/rules/ai.md (proposals, never auto-applied) and
publishing.md (explicit confirmation); the AP.1.1 decision record for plan facts.
RESTRICTION: No claim without a source in the codebase or the decision record. Prices are referenced as
catalogue fields, never typed into the copy. Keep the warm, direct voice of the current page. LP.1 found
three claims the product does not back, and none of them comes back in new words: recipe import (only
ingredient lines can be pasted), creators connecting their own channels (publishing is Buffer, configured
for the deployment), and AI proposing a scaled recipe (scaling is deterministic code). "Building it in the
open" stays only if the owner confirms it.
BEHAVIOR: Present the outline, mark every claim with its source, wait for approval. No components.
```

## LP.3 Bring value props and how-it-works up to date - done

```text
SCOPE: Update DEFAULT_VALUE_PROPS and DEFAULT_HOW_IT_WORKS_STEPS to the approved copy: shipped capabilities
get their category badge and tone, the how-it-works note goes, and each card can link to its tour panel
(LP.5). Rewrite how-it-works steps 1, 2 and 4 so they make none of the claims LP.1 found unbacked. Add a
spec that maps every non-"in the works" card to a real, non-placeholder route, so the page fails a test
before it can overclaim. Render the cards in `ul[cpCardGrid]` and delete the private `.grid` rule.
CONSTRAINT: LND-001; LP.2 copy; LP.1 findings 4, 5 and 10; the existing L.4/L.5 components and specs.
RESTRICTION: Status is still carried by badge text, not colour alone. Do not remove the AI-as-proposal and
explicit-publish wording. A placeholder route means one whose step falls through to a
`*-step-placeholder` component, not one that merely imports it.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the copy-to-route table; wait for approval; implement and update specs.
```

*Delivery note (2026-10-24): the nine LP.2 section 3 cards ship as written, except the section heading: "Everything
here works today" was false beside the "Coming to every account" card, so it reads "What's in CreatorPantry
today" and the intro says scheduling is on its way. LP.2 had no how-it-works copy; steps 1, 2 and 4 were drafted
and approved here, and step 3's "named version" became "numbered". Tour links exist behind `tourLinks` (off)
until LP.5 gives the panels `#tour-*` ids. `landing-capability-routes.spec.ts` navigates every card's pages
through `app.routes.ts` and, for a step shell, reads its template (`with { loader: 'text' }`, typed by
`src/template-text.d.ts`, which `tsconfig.app.json` excludes) to require a `@case` for the step. The publishing
card is checked too, not exempted.*

## LP.4 Hero, rebuilt to stop the scroll

```text
SCOPE: Rebuild LandingHeroComponent's first screen: a display headline set much larger (fluid clamp from
the type scale), one phrase highlighted with a hand-drawn underline or a colour block in a fruit accent,
and a layered food-photo collage in the media slot: three or four photos at slight rotations, overlapping,
with soft shadows and gentle parallax drift on pointer move. Add a floating "proposal card" overlay
showing what an AI draft looks like (a real, static capture). Keep the logo and both CTAs above the fold
at 375 px; today the primary CTA ends at 791 px of an 812 px screen, so the logo shrinks rather than the CTAs
moving down. Give the logo intrinsic width and height and a resized, theme-specific rendition near its shown
size, and make CpLogoComponent OnPush with no raw style-string input. Headings use a Fredoka weight that
index.html loads.
CONSTRAINT: LND-002, LND-007; LP.1 findings 2, 3, 9 and 12; DESIGN-SYSTEM.md tokens and motion;
new-component and creatorpantry-design-system skills.
RESTRICTION: Tokens only, no literal colours. The LCP image is fixed and preloaded, not random, and is not
discovered only after the lazy landing chunk runs. No layout shift from the collage or the logo (reserved
aspect ratios, explicit image sizes). Parallax and drift stop under prefers-reduced-motion and
on coarse pointers. Decorative images get empty alt; the overlay is aria-hidden if it repeats nothing new.
UTILIZATION: design-review after implementation; playwright-cli screenshots at three widths, both themes.
BEHAVIOR: Show a static mock (screenshots) and the motion spec; wait for approval; implement with
component and a11y specs.
```

## LP.5 Product tour with real screenshots - done

```text
SCOPE: Add LandingTourComponent: a tabbed (or scroll-linked on wide screens) tour of AI Recipe Studio,
Image Studio, Content Pipeline, long-form, brand guide and publishing. Each panel has a one-line promise,
three benefit bullets, and a real screenshot in a browser-frame card that crossfades between panels.
Screenshots are captured by a playwright-cli script against a seeded development workspace, committed as
optimised AVIF/WebP with JPEG fallback at 1x and 2x.
CONSTRAINT: LND-002; LP.2 copy; frontend.md; playwright-cli skill.
RESTRICTION: Screenshots show only seeded sample data, never a real creator's content. No mocked UI that
the product doesn't have. Tabs follow the WAI-ARIA tabs pattern with arrow-key support. Crossfade is
instant under reduced motion. The capture script is committed so screenshots can be regenerated.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the panel list, the seed data and the capture plan; wait for approval; implement the script,
the component and specs.
```

*Delivery note (2026-10-24): the component, the capture script and the specs ship; the screenshots do not, because
the five AI panels need a configured provider and real credits, and the publishing panel needs Buffer configured
for the sample workspace. So the tour is built to be empty until it is photographed:
`landing-tour.captures.ts` is generated and starts as an empty list, `LandingTourComponent` renders nothing while
it is, and `landing.component.ts` leaves the value props' `tourLinks` off for the same reason. `npm run capture:tour`
(`scripts/capture-tour.mjs`, Playwright and sharp, both new devDependencies) signs in to a running app, photographs
each panel in both themes at 1280×800 and deviceScaleFactor 2, writes AVIF and WebP at 1x and 2x into
`public/images/tour/`, and rewrites the generated file — so the page lights up with no hand edit.*

*Three decisions, approved before implementation: no JPEG tier (every browser that runs Angular 22 reads WebP and
AVIF, so the fallback would have been 24 files nothing fetches), tabs at every width rather than scroll-linking on
wide screens, and both themes. Two guards keep the restriction about creators' content honest: the script refuses
any workspace but the sample one named in `SAMPLE_WORKSPACES`, and it refuses to photograph a screen showing a
`cp-empty-state` or holding no cards, failing that panel by name instead of shipping a picture of an empty product.
It also reads `TOUR_STOPS` back out of the component and fails if the two lists of panels have drifted.*

*The copy is LP.2 section 4 verbatim, including the publishing panel's **Coming to every account** badge. The
screenshot frame sits beside the panels rather than inside them — the pictures are decorative, every word in them
is in the promise and the bullets — which is also what lets one fade into the next; a picture is mounted the first
time its tab is chosen, so the page loads one screenshot and not six.*

*design-review raised four things, all fixed. A second click on a link a visitor had already followed changed no
address and so raised no `hashchange`, leaving the tour scrolled into view on the wrong panel — the tour now
watches clicks on `a[href^="#tour-"]` as well, and writes the selected panel back with `replaceState` once (and
only once) the address already names one. A hash-driven selection was silent and left focus on the link, so it now
moves focus to the tab; `CpTabsComponent.focusTab` became public for it, documented in `DESIGN-SYSTEM.md` beside
`selectTab`. The capture script stamped today's date on a panel it had failed to re-photograph, so it now keeps an
earlier run's own date for any panel this run did not take in both themes. And the comment claiming the hero's
largest paint is the preloaded logo was wrong — the front photo is drawn larger — so it says plainly instead that
the dealt photo may be the LCP element and that LP.11 is where that gets measured. Accepted rather than changed:
the frame sits below the bullets at narrow widths, and the feature keeps its own browser-frame styling until a
second consumer earns a library part.*

*Captured on 2026-10-24: **two of the six panels**, AI Recipe Studio and Image Studio, both in light and dark.
The other four have nothing to photograph — the sample workspace has no content pipeline run, no long-form draft,
no activated voice guide, and Buffer is not configured — so the script refused them by name and the tour ships
with the two. A value-prop card links to its panel only when the tour has it (`tourPanels`, from
`CAPTURED_TOUR_PANELS`), so the four waiting cards carry no link rather than pointing at a stop that is not there.
Re-running the capture with the other four addresses adds them with no code change.*

*Two guards earned their place during that run. A panel's `proof` — the words that show the work is on screen —
has to be **visible**, not merely in the DOM: the first run photographed an empty brief form while the concepts
sat below the fold and the pictures sat in an unselected tab, and both passed. The proof is now scrolled into
view before the shot, and the Image Studio panel clicks through to its pictures first (`prepare`). The blanket
"no `cp-empty-state`" rule went: a screen full of real work still carries small empty notes beside it. The second
guard is `requiresUrl`: the pipeline, long-form, brand-guide and publishing screens exist only at an id of their
own, so their hub page is never photographed in their place.*

*The capture account and its sample workspace live in the developer's own database: `maple-thyme`, owned by
`tour-capture@creatorpantry.local`, holding one set of AI concepts and two generated pictures saved to the
library. Nothing of a real creator's is on the page.*

*And, also at the owner's request: the hero collage's three photos are dealt at random
from the page's `FoodImageService` rather than fixed (`dealHeroCollage`), so the first screen differs on every
load. LP.4's reason for fixing them does not apply — the page's largest paint is the preloaded logo, not a photo,
and the collage still reserves its aspect ratio and each image its intrinsic size. The trios below still repeat
none of the hero's, because all three now deal from the same pool.*

*A second owner request landed with it: the landing page's type was too varied to read easily. Body copy across
every section is now one size — `--cp-font-size-lg`, the hero's lede — with headings stepping up from it rather
than down to meet it (`--cp-font-size-title` for a section, `--cp-font-size-xl` for a card, step or panel). That
also answers LP.1 finding 9's 13–15px card bodies and 11px proof note. The rule is written down in
`DESIGN-SYSTEM.md` under the type scale, as a public-page rule only.*

## LP.6 Pricing section from the plan catalogue - done

```text
SCOPE: Add an anonymous, read-only GET for the public plan catalogue through the gateway (plan key, name,
monthly and annual price, credits, example mix, daily image cap, quality classes, pack list), cacheable,
and a LandingPricingComponent: four plan cards with a monthly/annual toggle that animates the price, the
annual saving shown as a figure from the server, "Recommended for most food bloggers" on the plan the
catalogue flags, and a feature-comparison table that expands below. Each CTA goes to
/sign-up?plan=<key>. The section renders only when the Landing:Pricing flag is on.
CONSTRAINT: LND-003; AP.4.1 (catalogue); .claude/rules/api-contract.md, gateway.md, frontend.md;
add-endpoint, new-component and creatorpantry-design-system skills.
RESTRICTION: No price or credit number in the web bundle. The route exposes catalogue data only, no account
data, and gets an edge rate limit. The toggle is a real radio group with a visible label. The recommended
card is distinguished by more than colour (badge text, elevation). The comparison table is a real table.
Landing:Pricing is not turned on outside development until reviewed Terms of Service, Acceptable Use
Policy and Privacy Policy pages replace the footer's "(coming soon)" placeholders. Mock-ups of all three
are in docs/legal/ (2026-10-24). Legal review is outside this document; the gate is not.
UTILIZATION: api-contract-checker and design-review after implementation.
BEHAVIOR: Show the route, the ViewModel and the card/table layout; wait for approval; implement with
endpoint and component tests and an OpenAPI snapshot diff.
```

*Delivery note (2026-10-24): shipped, including the dependency it names. **AP.4.1 had not been written**, so
there was no catalogue to read: this change builds its configuration half — `Billing:Plans` and `Billing:Packs`
bound and validated at startup in a new `Modules/Billing` (Facade, Business, Managers; no Data area, so it
joins `ProjectionModules` with its reason) — and leaves the `AccountPlan` table and everything that reads a plan
to AP.4.1 proper. The owner approved that split, and approved seeding development with the pricing analysis's
"Proposed, awaiting the owner" figures while the real ones wait.*

*`GET /api/v1/plans` is anonymous — the only product route outside `auth` that is — because the catalogue is
configuration a deployment chose to publish, with no account, workspace or usage in it. It carries
`Cache-Control: public, max-age=300`, set after the answer rather than before, and the gateway gives it its own
YARP route with a `public-catalogue` rate limit of 60 a minute per address. `Billing:PublishPlans` is off in
`appsettings.json` and on in `appsettings.Development.json` alone; with it off the route answers 200 with empty
lists, because an unpublished catalogue is a state rather than a missing resource, and the section then renders
nothing. The OpenAPI snapshot was regenerated and its diff is purely additive: one path, three schemas, a tag.*

*One existing assertion was tightened rather than worked around: `OpenApiContractTests` forbade any key beginning
`"example`, which the catalogue's `exampleMix` field tripped. It now forbids `"example":` and `"examples":`
exactly — OpenAPI's own sample keywords, which is what that check was protecting — and says so.*

*The component carries no figure of its own: prices, credits, caps and the yearly saving all arrive at runtime.
The toggle is a real radio group (`CpChoiceGroupComponent`) named by the question printed above it, the
recommended plan is marked in words and by a 2px edge rather than a colour swap, and the comparison is a real
`<table>` in a named, focusable scroller behind an `aria-expanded` disclosure. The price counts between the two
figures and never counts under reduced motion.*

*api-contract-checker and design-review both ran. Fixed from their findings: two animation loops could run at
once and clear each other mid-count (one generation wins, and a count continues from the figure on screen);
`li { display: flex }` was unscoped and stripped the list semantics from each card's own list; the card showed a
monthly figure and a saving but never what a year costs (it now says "Billed yearly at ..."); the CTAs used a
bare `href`, reloading the whole app from a public page, and now use `routerLink` with the plan as a query
parameter; `IOptionsMonitor` could serve a configuration reload no validator had seen, so the business reads
`IOptions`; the decoder turned a present-but-wrong `trialDays` into "no trial", and now refuses the plan; the
disclosure label said the state that `aria-expanded` already says; the table's scroller had no name or tab stop;
`withCredentials` was set on an anonymous route; and startup now also refuses a lower-case currency, a plan key
that is not url-safe, a published plan with no example mix and a pack that expires in nought months.*

*Accepted rather than fixed, and why: the section still pops in after the page paints, because a resolver would
hold the first paint of a marketing page for a network call and the section sits well below the fold; the
service still answers every failure with an empty catalogue and no log, which is the visitor's experience by
design (now written down in `DESIGN-SYSTEM.md`) but leaves an operator unable to tell an outage from an
unpublished catalogue; the gateway's 429 carries no `retry` field, which is the pre-existing shape the login
limiter already has and belongs to a separate change; and `CpChoiceGroupComponent` still names itself with
`aria-label`, so the question is written twice — a `labelledBy` input would fix that in the library.*

*Still blocked by what LP.6 itself says: the figures are proposals, and the flag may not be turned on outside
development until reviewed Terms, Acceptable Use and Privacy pages replace the footer's placeholders.*


## LP.7 "What do credits buy?" explainer and plan estimator - done

```text
SCOPE: Add LandingCreditsComponent: a visual "credit menu" (draft image, standard image, high image, text
draft, long-form section, each with its credit cost from the catalogue, styled like a menu board) and an
estimator with two sliders, images per month and AI writing actions per month, that highlights the
smallest plan that fits and shows how much headroom remains, with a count-up animation.
CONSTRAINT: LND-004, LND-005; LP.6 catalogue (add credit rates per action class to it); frontend.md.
RESTRICTION: Rates come from the server. The estimate is labelled "estimate" and uses the catalogue's
typical per-action figures; the allowance itself is enforced server-side. Sliders are native range inputs
with visible values and keyboard support. Count-up is instant under reduced motion.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the menu, the estimator logic and its edge cases (zero, beyond the top plan); wait for
approval; implement with specs for each edge case.
```

*Delivery note (2026-10-24): shipped. `Billing:Rates` joins the catalogue — a text draft, a long-form section,
and a draft, standard and high picture — published on the same anonymous route and refused at startup if a
published catalogue leaves any of them at nought. `LandingCreditsComponent` draws the menu board from those
figures and an estimator over two native range sliders, and the count-up came out of
`LandingPricingComponent` into `LandingCount`, which both now share.*

*Edge cases, each with a spec: nothing chosen asks for a slider rather than naming a plan; an estimate that
exactly fills a plan says "with none to spare"; past the largest plan it gives a way out; the trial is never an
answer, because its credits are a one-off rather than a monthly allowance; and a plan whose daily picture cap
could not carry the month is passed over even when its credits would cover it.*

*Two decisions worth recording. **The rates are published before anything charges them**: pictures cost no
credits today (AP.2.4 and AP.2.5), and text settles against `AiQuotaOptions.DefaultTaskEstimate` rather than
these figures. The owner approved shipping the menu behind the same development-only flag the prices sit
behind, so no visitor sees the claim before it is true — the dependency is AP.2.4/2.5, and it is a release
gate, not a suggestion. **The catalogue's rates are optional on the wire**: a server that states none, or
states them badly, leaves the credit menu off the page and the plans above it untouched. Requiring them took
the whole pricing section down against a server a few minutes older, which is a poor trade for a field only
one section reads.*

*design-review found eight things; six are fixed. The counted figure was inside the live region, so a drag
announced about twenty times a second — the figure is now `aria-hidden` and the settled estimate is said once
beside the verdict, with `role="status"` alone and the redundant `aria-live` gone. Each slider now carries
`aria-valuetext`, because a range otherwise announces a bare number. The shared counter wrote nothing until its
first frame, so a figure jumped to its destination and counted back from the start — it now writes where it is
starting from synchronously, and `landing-count.spec.ts` covers that and the rest of the helper. The writing
slider's hint promised long-form sections the estimate did not price. "Add a pack" sent a visitor looking for
something no section renders yet (LP.8), so it is gone until there is one. And a field's hint stepped below the
public page's one body size.*

*Two were not changed, with reasons. The reviewer read the intro's "your credits belong to you, not to a
workspace" as unbacked after looking in the Billing module; it is backed, in `Modules/AiUsage` — the allowance
is account-scoped (USAGE-001, `AccountAiQuota`), which is why `/app/ai-usage` sits outside a workspace route.
And "about N credits to spare" keeps its "about": LP.2's approved copy says it, and a headroom derived from an
estimate is itself an estimate.*

## LP.8 Draft vs final images, and the no-surprise-bill promise - done

```text
SCOPE: Two bands. (1) "Explore fast, finish beautifully": a side-by-side of a draft and a final image of
the same seeded prompt with a drag-to-compare slider, and their credit costs. (2) "No surprise bills":
three promise tiles (prepaid credits only, generation stops at zero, auto top-up is opt-in with your own
monthly limit) and the pack prices from the catalogue.
CONSTRAINT: LND-004; AP.3.1 (quality classes), AP.5, AP.6.4 decisions; LP.6 catalogue.
RESTRICTION: The two images must be real outputs from the configured draft and final models, generated
once and committed. The compare slider is keyboard operable and has a text alternative. Promises must
match shipped behaviour; any part not shipped is labelled as coming.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the copy and assets; wait for approval; implement with specs.
```

*Delivery note (2026-10-24): both bands built; one of them renders.*

*"No surprise bills. Ever." ships whole. Three tiles, and the two describing billing that is not built carry
**Coming with billing** in words rather than a tone — `cp-badge` paints without a glyph, so a status told by
colour is a status a reader can miss. The middle tile has no label because the product already behaves that
way: an allowance at nought refuses the work before it starts. The packs come from the catalogue LP.6
publishes. **Packs do not expire** (owner, 2026-10-24): a pack is credits already paid for, so they keep until
they are spent. That answered the last open question about them, and the catalogue's `ExpiryMonths` went with
it rather than staying as a field that had to be left empty — one deployment setting it would have put a term
on the public page that the owner has ruled out. The band says so once, under the list, as the promise it is. With packs on the page
at last, the estimator's beyond-the-top-plan line says "Add a pack" again — LP.7 had removed it precisely
because nothing rendered one.*

*"Explore fast. Finish beautifully." is **built and waiting on its pictures.** The band, the divider and the
specs are all here; `COMPARE_ASSETS` is null, so nothing renders. The restriction asks for real outputs of the
configured draft and final models, and there are none to ask: quality classes are not built (AP.3.1), the
Venice request names neither quality nor resolution (AP.2.3), and the model that would make the final picture
has not been chosen (AP.3.3). Two pictures from two different models with one labelled "draft" would be a
picture of a feature rather than of the product. The owner asked for the band to be built anyway, so it is,
gated the way the product tour is: generate one prompt at each quality once those ship, commit the pair, fill
in `landing-compare.assets.ts`, and the band lights up with no further change. A spec holds the shipped state
to that null, so it cannot be left half-on.*

*The divider is a native `input[type=range]`, which carries the arrow keys, Home and End, the focus ring and
an announced value for nothing; both pictures are in the page with their own alt text, so the comparison reads
without seeing either, and the prompt they share is printed beneath them.*

*design-review found one blocker and six smaller things, all fixed. **The divider announced the opposite of
what it showed**: the finished picture was clipped *from* the left by `reveal`, so "55% of the finished
picture" showed 45% of it — and the spec encoded the same inversion, so it passed. The clip is now bound in
the template rather than written in the stylesheet, precisely so a spec can read which side it insets from.
The band also gained a third gate: committing two pictures is not enough to claim two tiers, so it renders
only where the catalogue offers both a draft and a finished quality. The packs now carry the same **Coming
with billing** label the tiles do, because a pack is exactly as unbuyable as the billing is; the compare costs
say "about", as the credit menu does; each picture's alt says only which of the two it is, with what they
both show said once in the caption; "export" became "download", which is what every picture actually has; the
money formatter is one shared helper rather than a third copy; and the pack list says `role="list"`, which
Safari takes away from a list with no markers.*

*One claim the owner settled rather than the code: the "At zero" tile says a creator's pictures stay theirs
while AI waits, and pictures draw on no allowance at all today (AP.2.5). The owner's instruction was that
pictures will count by the time this page is published. It therefore ships behind the same development-only
flag as every other figure in this part of the page, and AP.2.5 is a release gate for it.*

## LP.9 FAQ - done

```text
SCOPE: Add LandingFaqComponent: an accordion of 8 to 10 answers covering what a credit is, what happens at
zero, rollover, cancelling, pack expiry, who owns AI output, whether AI changes recipes on its own, safety
mode on images, and data privacy. Answers come from the decision records.
CONSTRAINT: LND-006; AP.1.1 record; ai.md; L.6 honesty rule.
RESTRICTION: Native details/summary or the disclosure pattern, one heading level below the section. No
legal or medical promise. Anything undecided is left out, not guessed.
UTILIZATION: design-review after implementation.
BEHAVIOR: Present the questions and answers with sources; wait for approval; implement with specs.
```

*Delivery note (2026-10-24): ten questions, in native `details` so the keyboard, the marker's state and what a
screen reader says are the browser's. Each `summary` holds the question as an `h3`, one level under the
section's own heading, and every one starts closed.*

*Seven of the ten are true of any deployment. The three about credits, rollover and packs render only where
the catalogue publishes prices — a deployment that sells nothing should not answer questions about what it
charges. So the accordion is ten questions with pricing on and seven without, which is the honest shape rather
than LP.9's "8 to 10" exactly.*

*Three questions LP.2 lists are deliberately absent, and the restriction is why: cancelling waits on AP.6.2's
downgrade timing, who owns what AI makes waits on reviewed Terms that exist as a page to link to, and what a
trial includes waits on AP.7.1. Undecided is left out, not guessed. A fourth, "what is a credit?", is left out
for a different reason: the credit menu directly above answers it in full, and an accordion that repeats the
section above it is noise. Pack expiry became answerable on the day — the owner settled it (packs are bought
credits; they keep until spent), so it is in.*

*One guard was extended rather than written. `scripts/check-safety-copy.mjs` watched the three recipe screens
that report records; it now watches the FAQ too, because the allergen question is the one thing on a public
page a reader's health could turn on, and a refusal is one edit away from an assurance. Its answer's "never a
guarantee" is listed as a denial, by hand, the way the script's other six are. The script's own test asserted
that it watched three templates and three components by counting to three; it now names the stems and requires
both halves of each, which is the rule it was trying to express and survives a fourth screen.*

*design-review raised no blocker. Fixed from it: the guard's own comments still said "three panels" after it
grew a fourth; the section had no `id` for a nav to link to (`#faq`, ready for LP.10); the question list lost
its semantics in Safari without `role="list"`; and the summary had no radius, so its focus ring cut the corners
of the box it opens. Accepted rather than changed: the three catalogue questions appear after the catalogue
loads, which is the same trade the pricing section makes; and `h3` inside `summary` is valid and keeps the
native disclosure, but how reliably heading navigation reaches it varies by screen reader and wants checking in
VoiceOver and NVDA before this page is public. **The focus ring's contrast — 1.74:1 light, 2.68:1 dark against
WCAG 1.4.11's 3:1 — is real, library-wide and already LP.1 finding 6, assigned to LP.11A.** It is a token, not
a thing for this component to patch. The reviewer could not verify three claims; "we don't write their
contents to our logs" is backed by `PromptEnvelope.Describe`, which reports a media type and a length and
nothing else, citing `ai.md`.*

*Two flaky specs were fixed along the way, both the same fault and neither mine: `LandingHeroCollageComponent`
aimed its pointer at the window's bottom-right corner and asserted a positive drift, which held only while the
fixture happened to sit left of that corner — something the specs running before it decide. They now aim past
the collage's own centre. The `RecipeEditorComponent` anchor-nav spec remains intermittent under a loaded
suite; it predates this work, passes in isolation, and was reproduced failing on a clean tree before any of
these prompts were implemented.*

## LP.10 Sticky nav, section anchors and /pricing - done, without the nav

```text
SCOPE: Add a slim sticky landing nav: logo, anchors (Tour, Pricing, FAQ), Sign in, and a Start free
button that appears once the hero CTA scrolls away. It goes translucent with a backdrop blur and hairline
on scroll, and the current section is highlighted. Add a public /pricing route that renders the landing
page scrolled to pricing, and give the landing page a title, meta description, canonical URL and Open
Graph/Twitter card image, in HTML a scraper receives without running JavaScript: prerender '' and /pricing,
or put static tags in index.html, whichever the plan justifies. Replace index.html's "CreatorPantry Design
System" title. Add a robots.txt. The nav is the page's banner; the footer either puts its links in its
`<nav aria-label="Footer">` or drops the empty landmark.
CONSTRAINT: LND-007; LP.1 findings 7 and 8; frontend.md, gateway.md; L.1 routing rules.
RESTRICTION: Anchors are real links with smooth scroll only when motion is allowed. Skip link to main
content. The nav never hides content at 200% zoom. Tags set only through Angular's Meta service at runtime
do not meet this prompt, because social scrapers never run the app. A prerendered page renders no
workspace or session data. /pricing stays anonymous and redirects nothing for
signed-in users beyond what L.1 does for ''.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the nav states and route change; wait for approval; implement with routing and component
specs.
```

*Delivery note (2026-10-24): **the nav is not in the page, and that is the owner's decision rather than a
shortfall.** It was built as asked — a `cp-landing-nav` of `CpAnchorNavComponent` anchors, sticky, translucent
with a hairline once scrolled, current section marked, "Start free" appearing as the hero's CTA left the
screen — and the owner's answer on seeing it was that a bar over the hero "destroys the value of the hero".
Moving it inline under the hero (which costs the page its `banner` landmark, since a banner may not sit inside
`<main>`) did not fix it either: "very jarring for the page layout". So the bar, its spec, its anchors on the
tour section and the id on the hero's CTA row are all out, and the page is read by scrolling, as it was.
Anything LP.11 or a later prompt wants to reconsider here starts from nothing, not from a disabled component.*

*What did ship, all of it still wanted:*

- `/pricing`: the landing page at a second address, guarded exactly as `''` is (`anonymousOnlyGuard`, so a
  signed-in visitor goes to the app), titled "Pricing", and opened at the pricing section through
  `data: { scrollTo }`. It waits for the section rather than scrolling at once, because the catalogue answers
  after the first render — and on a deployment that publishes none it does nothing at all and the address stays
  an ordinary landing page. Arriving also moves the keyboard's place to the section. Two things that cost a
  while: `afterRenderEffect` never runs under `RouterTestingHarness`, so this is `effect` + `afterNextRender`
  as everything else in this repo is; and removing a borrowed `tabindex="-1"` straight after focusing blurs the
  element in Chrome, so it stays (`-1` adds no tab stop).
- `LandingPricingComponent.available` is public, so the page can tell whether that section rendered without
  asking the catalogue a sixth time. `#pricing` is its anchor, with `scroll-margin-top` so a jump leaves air
  above the heading.
- The document head, static in `index.html` rather than prerendered: prerendering would need `@angular/ssr`
  and would bake a plan catalogue into the HTML, which landing decision 3 forbids outright. Title (no longer
  "CreatorPantry Design System"), canonical, Open Graph and Twitter card, all at `https://creatorpantry.com/`
  — the owner's domain, given on request. One document answers both addresses, so it names one canonical.
  `landing-document-head.spec.ts` guards it by reading `index.html` as text, because a tag moved into Angular's
  `Meta` service looks right in every manual check and is invisible to every scraper.
- `public/images/og-card.png`, 1200×630, built by `npm run make:og-card`: Playwright over the real tokens and
  typefaces, the logo the page loads, and the hero's own headline read out of the component so the card cannot
  drift from the page. It photographs no product screen and no creator's work.
- `public/robots.txt`: `Disallow: /`, matching the `X-Robots-Tag: noindex, nofollow` that
  `staticwebapp.config.json` already sends. The owner chose to stay unindexed until launch; the file names
  both lines to change then. The SEO tags are therefore inert in that deployment on purpose.
- The footer's empty `<nav aria-label="Footer">` is gone (LP.1 finding 8), with its dead styles and an unused
  `RouterLink` import.
- **Beyond the prompt, at the owner's request:** no workspace may be given an address the browser serves
  itself. `WorkspacePolicy.ReservedSlugs` holds `app`, `confirm-email`, `design-system`, `pricing`, `sign-in`
  and `sign-up`, and `WorkspaceBusiness` treats a reserved candidate as taken — so a workspace named "Pricing"
  is created as `pricing-2` and keeps its name, exactly as one whose name someone reached first already does.
  No round trip, and no race. `ReservedSlugMirrorTests` reads `app.routes.ts` and fails until a new literal
  top-level route is reserved, since that drift is invisible from the server's side.*

*Not done: the skip link, which went out with the nav — with no navigation before `<main>` it would skip
nothing. design-review was not run, there being no new visual surface left for it to review.*

## LP.11 Motion, depth and image performance pass - done

```text
SCOPE: Page-wide polish. Scroll-reveal for sections (IntersectionObserver, fade and rise by a few pixels,
staggered children), hover lift on cards using --cp-shadow-md, colour-blocked section backgrounds
alternating paper and soft fruit tints, and a marquee of food photos replacing the random image trios.
Replace the 16 MB photo set with responsive AVIF/WebP/JPEG renditions (sizes for 480, 960, 1600 px), with
width/height and lazy loading below the fold, and make photo choice deterministic. Decide whether the
landing page still waits on the session check before it renders: anonymousOnlyGuard awaits a round trip to
the gateway, and that time is spent before the LCP image can even be requested.
CONSTRAINT: LND-007; LP.1 findings 1, 2 and 9 and its baseline; DESIGN-SYSTEM.md motion and shadow tokens;
frontend.md.
RESTRICTION: Budget on mobile Lighthouse: LCP under 2.5 s, CLS under 0.1, total image bytes on first load
under 600 KB. Measure as LP.1 did: a production build, not the dev server, and first-load image bytes taken
without scrolling, since Lighthouse's byte total counts photos its full-page screenshot scrolls into view.
A signed-in visitor is still forwarded into the app, and never after marketing content has painted. All motion off under prefers-reduced-motion; the marquee also pauses on hover and focus and
has a pause control. No animation library unless the plan justifies it.
UTILIZATION: design-review after implementation; playwright-cli for Lighthouse before and after.
BEHAVIOR: Show the motion inventory and the image pipeline; wait for approval; implement; report the
before/after Lighthouse numbers.
```

*Delivery note (2026-10-24): delivered, with one named piece of scope not built and the budget met. **LP.1's
baseline was stale and its method understated the page**: LP.4 had already replaced the 16 MB photo set, and
both LP.1's run and the first run here let the anonymous guard through without a round trip — LP.1 blamed CORS,
but the cause is that a static folder answers `/runtime-config.json` with the SPA's own document, so
`gatewayUrl` never decodes and `ApiBaseService` refuses the call. The harness now serves that file and
`/bff/session`, so the guard is really paid for. Measured on `08da041` by that corrected method, the page was
Performance 84, LCP 4.3 s, CLS 0.013, 414 KB of first-load images in light and 443 KB in dark — not the 12.5 s
and 4.4/6.3 MB LP.1 recorded.*

| | Before (`08da041`) | After | Budget |
| --- | --- | --- | --- |
| Performance | 84 | 98 | — |
| LCP | 4.3 s | **2.2 s** | < 2.5 s |
| CLS | 0.013 | 0.013 | < 0.1 |
| First-load images (412 px, no scroll) | 414 KB light / 443 KB dark, varying per load | **140 KB / 167 KB, the same every load** | < 600 KB |
| Accessibility | 100 | 100 | — |

*Mobile, simulated, Lighthouse 12.8.2 against a production build served gzipped from a scratch folder, image
bytes counted separately at 412 px with no scrolling.*

*The LCP element is the hero's `h1` — text — and it was never an image problem. The page cost **three
serialized round trips after the bundle parsed** before anything could paint: the awaited `runtime-config.json`
initializer, the guard's session check, and the lazy landing chunk. Each was measured on its own rather than
reasoned about:*

- *`runtime-config.json`, now asked for by a `<link rel="preload" as="fetch">` in `index.html` so it travels
  with `main.js` instead of after it: **−1.1 s**, and it is a round trip off the first paint of every page in
  the product, not just this one.*
- *The session check: **0 s.** The guard was temporarily made non-blocking and the build re-measured — LCP did
  not move (render delay 2735 ms against 2712 ms). Lighthouse's simulation overlaps that small request with the
  script graph. So the answer to this prompt's question is that **the landing page still waits on the session
  check, and the wait is free**: `anonymousOnlyGuard` stays on `''` and `/pricing`, a signed-in visitor is still
  forwarded before any marketing paints, and no hint cookie or `localStorage` flag was needed. Worth recording
  because the obvious fix for it would have cost correctness for nothing — and the deployed document is served
  by Azure Static Web Apps, so a gateway-side redirect was never available either.*
- *The lazy landing chunk: **−1.0 s**, and the only thing left between the page and its budget. The router
  cannot start fetching a lazy component until the route's guards resolve, so the chunk sat strictly behind the
  session check rather than beside it. **The landing route is now the one route in `app.routes.ts` that is not
  lazy.** It costs 22.6 kB gzipped on the initial bundle (87.7 → 110.3 kB), which a signed-in visitor downloads
  once and then has cached; script bytes on the wire went *down*, 128 → 122 KB, because a dozen tiny chunks'
  overhead went with it. The owner declined this when it was offered as a list item and approved it once it was
  the measured difference between 3.2 s and 2.2 s.*

*The image pipeline. `npm run make:food-images` (`scripts/make-food-images.mjs`) builds AVIF + WebP at 480 and
960 px, plus one 480 px JPEG as the `<picture>` fallback, and deletes the unsuffixed files LP.4 left. **The
camera originals are not in the tree and are not being put back**: twelve files at up to 5191×7787 come to
16.8 MB, so the script reads them out of git at `bdcb6f1` by default — reproducible on any clone, with a commit
rather than someone's desktop as the provenance — or from `FOOD_SOURCE_DIR`. **No 1600 px rendition**, against
the prompt's letter: the largest box a food photo occupies on this page is the hero's front photo at about
250 CSS px, ~660 device px on a phone, so 960 is already the 2× margin and 1600 would add ~2 MB to the
repository and never be selected. One line in `WIDTHS` to add it back.*

*Photo choice is deterministic, which **reverses the owner's request of earlier the same day** for the random
scatter — reversed by the owner here, on the grounds that first-load bytes cannot be measured against a budget
while they vary by ~40 KB per load, and a photo whose name is not known until the lazy chunk runs cannot be
preloaded. In exchange the page shows all twelve photographs rather than nine of twelve: `HERO_COLLAGE_PHOTOS`
fixes the hero's three (chosen off a contact sheet, not picked by id) and `MARQUEE_ORDER` runs all twelve
through each strip, the second backwards. `index.html` now preloads the hero's front photo by name, AVIF only —
a second preload for the WebP would be fetched as well by every browser that reads both —
and `landing-document-head.spec.ts` fails if that stops naming the photo the collage draws.*

*Motion, all of it off under `prefers-reduced-motion` and verified in a real browser rather than asserted:
section scroll-reveal (`LandingRevealDirective`, fade and an 8 px rise, children staggered 60 ms apart, capped
at six); the photo marquee replacing both image trios; and LP.4's hero parallax and proposal settle, unchanged.
**The reveal directive writes inline styles and only once it has decided to**, rather than CSS hiding
everything and script revealing it: under reduced motion, and in a browser with no `IntersectionObserver`,
nothing is ever hidden. It also reveals everything on `beforeprint`, so a print or a full-page capture does not
come out as blank bands. **The hero is deliberately not revealed** — it holds the LCP element.*

*The marquee pauses on hover, on focus, and on a rendered pause control (WCAG 2.2.2); under reduced motion it
does not animate and the control is **not rendered**, rather than hidden in CSS, which would leave a button
that does nothing in the accessibility tree. The track is `aria-hidden` with empty alt throughout, and every
photo in it is `loading="lazy"`.*

*Colour blocking, **built as asked and then taken out at the owner's direction**. Five full-bleed bands in the
soft fruit tokens (blue, orange, purple, pink), alternating with paper, went in; on seeing them the owner's
answer was that "the color scheme doesn't work with the logo — we need to stick with white and HR, keep it a
bit cleaner and less pastel". So the page is paper throughout and its sections are told apart by a 1px
`--cp-border` hairline, drawn once by `landing.component.css` rather than by each section. `:has(*)` is what
keeps a rule off a section that drew nothing: the catalogue sections render no content at all where a
deployment publishes no plans, and an empty host with a top border is a stray line across the page.
`DESIGN-SYSTEM.md` records the hairline as the rule and the bands as the thing that was tried.*

*The contrast work that went with the bands is kept there rather than thrown away, because it is the expensive
part if they are ever reconsidered: measured against all four soft tokens in both themes, `--cp-text` clears
11.08:1 and `--cp-text-muted` 5.57:1, while **`--cp-text-faint` falls to 3.37–4.10:1 on every dark tint and
fails AA**.*

***Not delivered: the hover lift on cards.*** *`cp-card` already lifts with `--cp-shadow-md`, but only behind
`interactive`, which also sets `cursor: pointer`. No card on this page is a whole-card link — a value-prop card
holds an optional tour link, a plan card holds its CTA — so `interactive` would have put a pointer cursor on
cards that do not respond to a click, and a grid where some cards lift and others do not reads as a bug. The
one honest route is a `lift` input on `CpCardComponent` (depth without the false affordance), which the owner
declined. Ten minutes whenever that is reconsidered; nothing here blocks it.*

*Beyond the prompt, at the owner's choice: the `runtime-config.json` preload above. **Offered and declined:**
caching the plan catalogue — `PlanCatalogueService.catalogue()` makes a fresh GET per subscriber, so five
landing sections fire **five identical `/api/v1/plans` requests on every load**; a lazily-created
`shareReplay(1)` fixes it. Recorded here because it is still true and still cheap.*

*design-review found no blockers. Fixed from its report: the reveal's "no JavaScript" rationale was wrong (this
is an SPA — with no JS there is no page to leave unhidden) and now says what it actually buys; `translate` is
taken off once a section has settled, so no section is left a stacking context; `food-photos.ts` moved to
`src/app/shared/`, because the sign-in frame uses it too and a shared component reaching into a feature folder
was the wrong way round; the marquee's pause control took the library's `2.5rem` control height and
`--cp-border-control`, and sits in a row of its own rather than over the bottom 16 px of the moving photos; the
hero's `settle` keyframe and the reveal now rise by the same token; and a stale comment in the build script
went. Two of its findings were overtaken: it asked for the bands to be documented or dropped, and they were
dropped; and its claim that a paper card on a band sinks in dark mode was **wrong** — it used `--cp-surface`
for the card, but `CpCardComponent` redefines its own surface to parchment under
`:host-context([data-cp-theme='dark'])`, so a card on a dark band was the brightest thing in it. Left for later
and recorded rather than done: three copies of the
reduced-motion media query across the reveal, the marquee and the collage, which wants one shared helper, and
promoting `cpReveal` into `@creator-pantry/ui`, which has one consumer today.*

*Verification: `npm run build` and `npm test` from `src/web` (6041 specs, the page contract and the safety-copy
check). **Two pre-existing failures are not from this work** — `RecipeEditorComponent`'s two scroll-geometry
specs; a clean `git worktree` at `08da041` fails the same two, plus a third, in this environment. No `aspire
run` restart is needed for any of this: it is all web assets and Angular source, served by the Angular dev
server, and the only backend-adjacent file touched is `index.html`.*

## LP.11A Focus indicator contrast

```text
SCOPE: Raise the visible focus indicator to at least 3:1 against every surface it can sit on, in both
themes. Today --cp-focus mixes --cp-primary at 38% (light) and 42% (dark), which measures 1.72:1 on the
light page and 2.71:1 on the dark one. Change the token, or the :focus-visible rule in global.css, and add a
spec that computes the ring's contrast against --cp-bg and --cp-surface in both themes.
CONSTRAINT: LND-007; LP.1 finding 6; WCAG 2.2 1.4.11; .claude/rules/design-system.md;
creatorpantry-design-system skill.
RESTRICTION: Library-wide: one token or global rule changes, never a landing-only override. Focus stays
visible on primary buttons, on paper cards in dark mode, and on fruit-tinted section backgrounds from LP.11.
Do not trade the ring for a colour change alone.
UTILIZATION: design-review after implementation; playwright-cli for focus screenshots on the landing page and
two signed-in pages.
BEHAVIOR: Show the measured ratios before and after for each surface; wait for approval; implement, update
DESIGN-SYSTEM.md and the showcase, and run npm test.
```

## LP.12 Composition and verification

```text
SCOPE: Compose the final landing page in the LP.2 order, wire the Landing:Pricing flag, and verify:
anonymous and authenticated behaviour at '' and /pricing, every CTA target, the catalogue-down state
(pricing hides cleanly, the rest renders), both themes, 375/768/1440 px, 200% zoom, keyboard-only,
reduced motion, the LP.11 budget and the LP.11A focus ratios. Add a "public page" shape to DESIGN-SYSTEM.md
that names what the landing page may draw itself (hero, sections, gutter) and what it must take from the
library. Give check-page-contract.mjs rules for that shape, so the landing page is no longer passed only
because OUTSIDE_SHELL skips it.
CONSTRAINT: L.9's checklist; LP.1 finding 10; frontend.md Verification; design-system.md.
RESTRICTION: Test through real routing and guards. Fix only findings within this document's scope.
UTILIZATION: design-review, api-contract-checker and test-gap-analyzer; playwright-cli for a full journey
and screenshots.
BEHAVIOR: Run everything, fix in-scope findings, run npm build and npm test from src/web and the API
tests, and report proof for each item with screenshots attached.
```

*Delivery note (2026-10-25): built and verified, **with the LP.11 budget left unconfirmed** — so not yet marked
done. The composition is LP.2's order less the two parts the owner took out (the nav, LP.10; the draft-and-final
band, removed with picture quality, AP.3.1): hero, value props, tour, how-it-works, pricing, credits, packs and
promises, FAQ, trust, final CTA, footer. The legal pages (`/privacy`, `/terms`, `/acceptable-use`) landed during
this prompt, so the footer's links are real and the LP.6 gate is now about review of their text, not their
existence.*

- ***The flag.*** *`Landing:Pricing` was a name only the docs used; the switch was the API's
  `Billing:PublishPlans`. The AppHost now owns the name (`appsettings.json`, `true`) and sets
  `Billing__PublishPlans` from it **in run mode only**, so `aspire publish` carries nothing and a deployment keeps
  the API's own `false`. `.claude/rules/aspire.md` says how to turn it off locally.*
- ***The public page shape.*** *`DESIGN-SYSTEM.md` "The public page": what a public page draws itself (the hero,
  its sections, one headline per page, its frame from five new `--cp-public-*` tokens) and what it takes from the
  library (every part rule, unchanged). `check-page-contract.mjs` now judges `features/landing/` rather than
  skipping it — every part rule plus `public-h1`, `public-frame` and `public-control`, with 4 new tests. LP.1
  finding 10 is closed: every section frame is a token, and the two class names the part rules ban were renamed
  for what they are (`.board` → `.menu`, `.steps` → `.sequence`). The legal page's literal gutter was the one
  thing the new rules caught.*
- ***Fixed from the journey:*** */pricing focused the whole section, so the ring framed the screen; it now focuses
  the section's heading, sized to its words, with the ring cp-page-head's title has. It also landed anywhere from
  −38px to +16px from where it was put, because a late font re-wrapped the sections above; it now waits for
  `document.fonts.ready` and lands at the same place every time. The legal title hid its ring; it now shows it, as
  the library's focused headings do. The legal parser rode in the first-screen bundle with the footer's links;
  the links are now `legal-links.ts` and the parser stays in the legal chunk (−0.9 kB).*
- ***Verified in a real browser*** *through real routing and guards against `aspire run` (21/21): the order, one
  h1/main/footer and no skipped level; /pricing opens at pricing with the heading focused; all 11 distinct link
  targets clicked and reached (both CTAs, four plan CTAs with `?plan=`, two tour links, three legal pages); with
  `/api/v1/plans` failing, pricing, credits and packs render nothing, the FAQ drops from ten to seven, no stray
  hairline, and /pricing stays at the top; light and dark at 375/768/1440 with no sideways scroll; 200% zoom on
  `/` and `/privacy`; reduced motion with no running animation and nothing left hidden; a keyboard walk of 24
  stops in each theme, every ring at ≥3:1 against what it is drawn over (lowest 4.56:1 light, 5.82:1 dark).
  Signed-in behaviour is proven by `app.routes.integration.spec.ts` with the real guards; a real signed-in
  browser needs a confirmed address, and the confirmation sink is in-memory.*
- ***The budget, not reproduced.*** *Images 140 KB light / 167 KB dark and CLS 0.02 — inside it. LCP 3.2–3.3 s in
  three Lighthouse 12.8.2 mobile runs, against 2.5 s. `HEAD` before this prompt measures 3.3–3.4 s in the same
  harness, so this prompt does not move it; the difference from LP.11's 2.2 s is the harness, which LP.11 never
  committed and this one recreates (production build gzipped, `runtime-config.json` and an anonymous
  `/bff/session` answered at the same origin). 86% of LCP is render delay. Initial script is 119.9 kB gzipped,
  about 10 kB over LP.11's 110.3 kB, and that growth predates this prompt. Committing a harness is what would
  settle it.*
- ***Reviewers.*** *design-review: no blocker; its two warnings are fixed (the doc now says `public-frame`
  judges capped widths, not the size of a part; the legal title's ring). api-contract-checker: the wiring is
  correct; open and outside this prompt — nothing tests the AppHost switch, no gateway test pins
  `/api/v1/plans`'s limiter, a workspace already named `privacy`, `terms` or `acceptable-use` would be shadowed
  (reserved slugs apply at creation only), and a rate-limited visitor sees no pricing and no notice, which is
  the documented choice. test-gap-analyzer: `/sign-up?plan=` is linked but sign-up does not read the plan
  (checkout is AP.6).*
