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
| LND-007 | The page feels premium and alive (bold type, colour, depth, motion) and stays fast and accessible. | LP.4, LP.10, LP.11 |

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
| SEO/meta | Nothing landing-specific found. | No description, Open Graph or social card. |

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

## LP.1 Landing page review (report only)

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
catalogue fields, never typed into the copy. Keep the warm, direct voice of the current page.
BEHAVIOR: Present the outline, mark every claim with its source, wait for approval. No components.
```

## LP.3 Bring value props and how-it-works up to date

```text
SCOPE: Update DEFAULT_VALUE_PROPS and DEFAULT_HOW_IT_WORKS_STEPS to the approved copy: shipped capabilities
get their category badge and tone, the how-it-works note goes, and each card can link to its tour panel
(LP.5). Add a spec that maps every non-"in the works" card to a real, non-placeholder route, so the page
fails a test before it can overclaim.
CONSTRAINT: LND-001; LP.2 copy; the existing L.4/L.5 components and specs.
RESTRICTION: Status is still carried by badge text, not colour alone. Do not remove the AI-as-proposal and
explicit-publish wording.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the copy-to-route table; wait for approval; implement and update specs.
```

## LP.4 Hero, rebuilt to stop the scroll

```text
SCOPE: Rebuild LandingHeroComponent's first screen: a display headline set much larger (fluid clamp from
the type scale), one phrase highlighted with a hand-drawn underline or a colour block in a fruit accent,
and a layered food-photo collage in the media slot: three or four photos at slight rotations, overlapping,
with soft shadows and gentle parallax drift on pointer move. Add a floating "proposal card" overlay
showing what an AI draft looks like (a real, static capture). Keep the logo and both CTAs above the fold
at 375 px.
CONSTRAINT: LND-002, LND-007; DESIGN-SYSTEM.md tokens and motion; new-component and
creatorpantry-design-system skills.
RESTRICTION: Tokens only, no literal colours. The LCP image is fixed and preloaded, not random. No layout
shift from the collage (reserved aspect ratios). Parallax and drift stop under prefers-reduced-motion and
on coarse pointers. Decorative images get empty alt; the overlay is aria-hidden if it repeats nothing new.
UTILIZATION: design-review after implementation; playwright-cli screenshots at three widths, both themes.
BEHAVIOR: Show a static mock (screenshots) and the motion spec; wait for approval; implement with
component and a11y specs.
```

## LP.5 Product tour with real screenshots

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

## LP.6 Pricing section from the plan catalogue

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
UTILIZATION: api-contract-checker and design-review after implementation.
BEHAVIOR: Show the route, the ViewModel and the card/table layout; wait for approval; implement with
endpoint and component tests and an OpenAPI snapshot diff.
```

## LP.7 "What do credits buy?" explainer and plan estimator

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

## LP.8 Draft vs final images, and the no-surprise-bill promise

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

## LP.9 FAQ

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

## LP.10 Sticky nav, section anchors and /pricing

```text
SCOPE: Add a slim sticky landing nav: logo, anchors (Tour, Pricing, FAQ), Sign in, and a Start free
button that appears once the hero CTA scrolls away. It goes translucent with a backdrop blur and hairline
on scroll, and the current section is highlighted. Add a public /pricing route that renders the landing
page scrolled to pricing, and give the landing page a title, meta description and Open Graph/Twitter card
image.
CONSTRAINT: LND-007; frontend.md, gateway.md; L.1 routing rules.
RESTRICTION: Anchors are real links with smooth scroll only when motion is allowed. Skip link to main
content. The nav never hides content at 200% zoom. /pricing stays anonymous and redirects nothing for
signed-in users beyond what L.1 does for ''.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the nav states and route change; wait for approval; implement with routing and component
specs.
```

## LP.11 Motion, depth and image performance pass

```text
SCOPE: Page-wide polish. Scroll-reveal for sections (IntersectionObserver, fade and rise by a few pixels,
staggered children), hover lift on cards using --cp-shadow-md, colour-blocked section backgrounds
alternating paper and soft fruit tints, and a marquee of food photos replacing the random image trios.
Replace the 16 MB photo set with responsive AVIF/WebP/JPEG renditions (sizes for 480, 960, 1600 px), with
width/height and lazy loading below the fold, and make photo choice deterministic.
CONSTRAINT: LND-007; DESIGN-SYSTEM.md motion and shadow tokens; frontend.md.
RESTRICTION: Budget on mobile Lighthouse: LCP under 2.5 s, CLS under 0.1, total image bytes on first load
under 600 KB. All motion off under prefers-reduced-motion; the marquee also pauses on hover and focus and
has a pause control. No animation library unless the plan justifies it.
UTILIZATION: design-review after implementation; playwright-cli for Lighthouse before and after.
BEHAVIOR: Show the motion inventory and the image pipeline; wait for approval; implement; report the
before/after Lighthouse numbers.
```

## LP.12 Composition and verification

```text
SCOPE: Compose the final landing page in the LP.2 order, wire the Landing:Pricing flag, and verify:
anonymous and authenticated behaviour at '' and /pricing, every CTA target, the catalogue-down state
(pricing hides cleanly, the rest renders), both themes, 375/768/1440 px, 200% zoom, keyboard-only,
reduced motion, and the LP.11 budget.
CONSTRAINT: L.9's checklist; frontend.md Verification; design-system.md.
RESTRICTION: Test through real routing and guards. Fix only findings within this document's scope.
UTILIZATION: design-review, api-contract-checker and test-gap-analyzer; playwright-cli for a full journey
and screenshots.
BEHAVIOR: Run everything, fix in-scope findings, run npm build and npm test from src/web and the API
tests, and report proof for each item with screenshots attached.
```
