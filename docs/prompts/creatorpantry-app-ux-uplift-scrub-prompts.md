# CreatorPantry — App UX Uplift SCRUB Prompts

Atomic prompts that bring the signed-in app up to the standard of the public landing page: warmer, more
visual, clearer hierarchy, real loading and empty states, and AI output that looks like what it is. For use
with Claude Code and the CreatorPantry `.claude/` toolkit. Path in the repo:
`docs/prompts/creatorpantry-app-ux-uplift-scrub-prompts.md`.

This document follows `docs/prompts/creatorpantry-ui-cohesion-scrub-prompts.md` (UI.*, which gave every
signed-in page one frame and one set of parts) and `docs/prompts/creatorpantry-landing-pricing-scrub-prompts-extensions.md`
(LP.*, which rebuilt the landing page). It does not redefine anything in `CLAUDE.md`, `.claude/rules/` or
`src/web/DESIGN-SYSTEM.md`; where a prompt needs a rule changed, AU.1 changes it first and says so.

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

## Test policy for this document

To save agent time, **the backend test suite runs once, in AU.8, and in no earlier prompt.**

- Every prompt runs `npm run build` and `npm test` from `src/web/`. Frontend checks are fast and catch the
  page contract, so they stay per prompt.
- No prompt before AU.8 runs `dotnet run --project src/CreatorPantry.Tests`, a filtered subset of it, or
  `dotnet test`.
- A prompt that changes backend code (only AU.4.2 is expected to) still **writes or updates** its backend
  tests and proves the solution compiles with `dotnet build` (redirect output with
  `-p:BaseOutputPath=<scratch>/` while `aspire run` is live). It does not run them. It does not regenerate the
  OpenAPI snapshot either, because that means running `OpenApiContractTests`. Mark such a prompt
  `- built (backend tests pending AU.8)`. AU.8 changes the mark to `- done` once the suite is green.
- If a later prompt has to touch backend code it did not expect to, it follows the same rule and adds itself
  to AU.8's list.

## The ask

Owner, 2026-10-25: "after looking at the fantastic UI/UX of the landing page — I think the UI/UX of the app
itself is lacking — come up with a plan to bring it up to par — do not touch the nav menu as that is already
strong." Then: "create the prompts … where they don't run the backend unit tests until the last prompt."

| ID | Requirement | Prompt |
| --- | --- | --- |
| APP-001 | Every signed-in page has a measured before-and-after, in both themes, at phone and desktop widths. | AU.0, AU.8 |
| APP-002 | Each creative tool has a recognisable identity from one fruit accent, used consistently and sparingly. | AU.1, AU.2.1 |
| APP-003 | No data surface shows a bare "Loading…" sentence; loading looks like the layout that is about to arrive. | AU.2.3, AU.7 |
| APP-004 | Empty states invite the first action and look designed, not like a missing part. | AU.2.4 |
| APP-005 | The creator's own pictures appear wherever the thing has one: recipe cards, recipe heads, results. | AU.2.5, AU.2.6, AU.4.2, AU.4.3 |
| APP-006 | AI output is identified the same way everywhere, and editable before acceptance. | AU.3.1 |
| APP-007 | AI choices (concepts, options, variants) are scannable: the key facts first, the details on request. | AU.3.2, AU.5.1 |
| APP-008 | Figures (counts, allowance, progress) read at a glance on the dashboard and the AI allowance page. | AU.3.3, AU.4.1, AU.6.4 |
| APP-009 | Dark mode is a dark mode: the page doesn't turn into light islands. | AU.1 (decision), AU.2.2 |
| APP-010 | Everything stays WCAG 2.2 AA, keyboard-operable and still under reduced motion, and passes the page contract. | every prompt, AU.8 |

## Decisions already made

1. **The nav is out of bounds.** The sidebar, its groups and items, the workspace switcher and the top bar
   (`src/app/shell/`) are not restyled, re-ordered or re-labelled. The one shell change allowed is the page
   frame applying a route's tone (AU.2.1), which happens inside `<main>`, not in the nav.
2. **The landing page is out of bounds.** It is the reference, not a target. Nothing under
   `features/landing/` changes, except an import path if a part it uses moves into the library.
3. **Bring the warmth over, not the marketing.** The app takes the landing page's accents, imagery, hierarchy
   and finish. It does not take scroll-reveal sections, the hero collage, colour-blocked bands or decorative
   stock photography. The app stays a tool that scans fast.
4. **Library first.** Visual change lands in `@creator-pantry/ui` (or in tokens) and pages adopt it. A page
   prompt that wants a look the library cannot give stops and asks for a library prompt. It does not draw the
   look itself.
5. **Page shapes stand.** Library, detail and flow (and the board, which is a library) are unchanged. Every
   new part gets a `check-page-contract.mjs` rule in the prompt that adds it, so pages cannot hand-draw it.
6. **Backend tests run once, in AU.8** (see Test policy).

## Decisions AU.1 settles

| # | Question | Recommendation |
| --- | --- | --- |
| D1 | Which accent belongs to which tool? | AI Recipe Studio purple, Image Studio pink, Social Studio blue, Long-form and Content Pipeline orange. Recipes, Pictures, Content Board, Brand, Prompt Library, My Day, My Week and the dashboard stay evergreen (no tone). A tool keeps one tone everywhere it appears. |
| D2 | Does `cp-card` stay parchment in dark mode? | **Replace it with a true dark raised surface.** Today a dark page is a set of cream islands (`card.component.ts:11-40`); LP.1 finding 9 flagged the same thing on the landing page. It is the largest single visual change in this document and needs its own prompt (AU.2.2), with every LP.11A focus ratio re-measured. Keeping parchment is a valid choice; if kept, AU.2.2 is skipped. |
| D3 | What does a page head's accent look like? | A tone-filled icon tile beside the title, a tone hairline under the head instead of the neutral one, and an optional thumbnail slot for a detail page's own picture. The brush underline stays a public-hero device. On a dense tool page it reads as marketing. |
| D4 | Does content animate in? | One short fade-and-rise (`--cp-duration-base` or less) when a route's content or a result first renders, done in the library, and nothing under `prefers-reduced-motion`. No scroll reveal inside the app. |
| D5 | Illustrations for empty states? | A small set of drawn line illustrations as inline SVG in the library, one colour (`currentColor`, picking up the tone), decorative (`aria-hidden`). Not stock photos and not AI-generated pictures, so they stay crisp, theme-aware and weightless. |

## How this list was built

A code audit of `dev` on 2026-10-25 (32 routed pages, all passing `npm run check:page-contract`), the landing
page in a browser at 1440 px, and the two signed-in screenshots the landing tour ships
(`public/images/tour/*`, captured 2026-10-24). A live signed-in walk was not possible in that session, which
is why AU.0 exists. Re-find files before trusting a line number.

| Area | What exists | Finding |
| --- | --- | --- |
| Colour | Fruit accents exist in both themes (`themes.css`), but only `features/recipes` and `features/brand` use them. | Every other page is ink, paper and evergreen. Tools are indistinguishable at a glance. |
| Page head | `cp-page-head`: title at `--cp-font-size-title`, a lede, a neutral hairline (`page-head.component.ts`). | Correct and quiet, but nothing says which tool you are in. `DESIGN-SYSTEM.md:27` reserves the display size and underline for the public hero. |
| Loading | `cp-list-shell` renders the sentence "Loading…" (`list-shell.component.ts:9`). About 21 non-spec files print their own loading sentence ("Loading your dashboard…", "Loading the unit list…"). | No skeleton anywhere. Every page arrives as a sentence, then jumps. |
| Empty states | `cp-empty-state`: a text glyph (`✦` by default) in a 3.25 rem disc (`empty-state.component.ts:12-17`). | Copy is the only expressive channel. Several pages (dashboard, My Day, AI allowance, Image Studio, Social Studio, Quick Recipe, Content Pipeline) have no empty state at all, per a grep (treat as approximate). |
| Pictures | `cp-media-card` with no picture draws a tint and a glyph (`▣` default). The recipe library passes `⌘` for every card because `RecipeSummary` carries no picture (`recipe-library.component.html:74-76`, `recipe.models.ts:804`). | A library of recipes looks like a grid of placeholders even when every recipe has a Hero picture (`RECIPE_ASSET_ROLES`, `recipe-asset-link.models.ts:12`). |
| Dark mode | `cp-card` re-declares its neutrals to a parchment palette under `[data-cp-theme='dark']` (`card.component.ts:11-40`). | The Image Studio dark capture is a cream form on a near-black page. See D2. |
| AI output | Generated content is marked per feature: `ai-proposal-panel`, `channel-post-review`, `generated-image-run`, `ai-advisory-results`, the content-pipeline panels. | No shared treatment, despite the rule that generated content is "visibly identified". |
| AI choices | AI Recipe Studio concepts are three identical cards of bold-label paragraphs (time budget, skill fit, dietary notes, assumptions), each with the same "Choose this concept" button (tour capture). | A wall of text. The differences between concepts are buried. |
| Motion | The interactive `cp-card` lifts 2px on hover. | Nothing else moves. Results appear with no transition. |
| Figures | `DESIGN-SYSTEM.md` "Meters and figures"; `cp-progress`. | No figure tile; the dashboard is lists of items in topic cards. |

## Prerequisites already satisfied

- UI.1–UI.8: one frame, the three page shapes, `check-page-contract.mjs`.
- LP.11A: the focus ring at ≥3:1 everywhere, including inside a dark `cp-card`.
- `@creator-pantry/ui` parts listed in `.claude/rules/design-system.md`; `--cp-duration-*`, `--cp-ease`,
  `--cp-shadow-*` tokens; `global.css` honours `prefers-reduced-motion`.
- `scripts/capture-tour.mjs` signs in as a real account against `aspire run` (`CAPTURE_EMAIL`,
  `CAPTURE_PASSWORD`, `CAPTURE_BASE_URL`) and refuses any workspace but the sample one.
- `playwright-cli` skill; `design-review`, `test-gap-analyzer` and `api-contract-checker` agents.

## Out of bounds for this document

- The nav, the top bar and the landing page (decisions 1 and 2).
- New product capability. No page gains a feature; it gains a better presentation of what it already does.
- Backend changes, except the one additive field in AU.4.2.
- Copy rewrites beyond the new empty states, the loading sweep and labels a new part needs.
- Stock photography, AI-generated decoration, and any picture that is not the thing itself
  (`DESIGN-SYSTEM.md:144` stands).

## Checkpoints

| After | Demonstrable result |
| --- | --- |
| **AU.1** | The rules and tokens say what the app may now do, and the owner has settled D1–D5. |
| **AU.3.4** | Every new and upgraded part is in the showcase, in both themes, with its contract rule. No page has changed yet. |
| **AU.5.4** | The dashboard, recipes and every creative studio wear the new parts. |
| **AU.8** | Every page re-captured against the AU.0 baseline; frontend and **backend** suites green; reviewers pass. |

---

## AU.0 Baseline capture and scorecard (report only)

```text
SCOPE: Add scripts/capture-app.mjs and an npm script "capture:app" that signs in the way capture-tour.mjs does
and photographs every signed-in routed page in app.routes.ts and the feature route files, in light and dark,
at 375 and 1440 px: once in the sample workspace with work in it, and once in an empty workspace the script
creates for the run. A page that needs an id it cannot guess is read from CAPTURE_URL_<PAGE>, as capture-tour
does. Write the images to src/web/.captures/<label>/ (add it to src/web/.gitignore), where <label> is "before"
by default. Then score every page against this rubric, 0-2 each: hierarchy (can you tell what matters first),
identity (can you tell which tool this is), imagery (is the creator's own picture shown where one exists),
loading (skeleton vs sentence), empty (designed vs missing), generated (AI output identified), dark mode (reads
as dark), density (scannable vs a wall of text). Confirm or correct every finding in "How this list was built".
CONSTRAINT: APP-001; .claude/rules/frontend.md; src/web/DESIGN-SYSTEM.md; scripts/capture-tour.mjs for the
sign-in, the sample-workspace guard and the viewport handling.
RESTRICTION: Report first. Change nothing but the new script, its npm entry and the .gitignore line. Never
commit a capture. Never photograph a workspace other than the sample one and the run's own empty workspace.
Do not touch the nav. Do not run backend tests (Test policy).
UTILIZATION: playwright-cli; design-review on the captures and the scorecard.
BEHAVIOR: Run the capture, present the scorecard as a table (page × rubric) with the three worst pages called
out, attach a contact sheet of the captures, map each finding to the AU prompt that fixes it, and wait for
approval before AU.1.
```

## AU.1 Design decisions, rules and tokens

```text
SCOPE: Settle D1-D5 with the owner, then write them down and add the tokens they need. In DESIGN-SYSTEM.md:
add a section "Warmth inside the app" under "Composing a page" that states the tool tones (D1), the page-head
accent (D3), entrance motion (D4), empty-state illustrations (D5) and the skeleton loader, each with what a
page may and may not do; amend line 27 only if D3 chooses the underline; record D2. In themes.css, both
themes: a tone scale per accent (--cp-tone, --cp-tone-soft, --cp-tone-text, --cp-tone-on) selected by
[data-cp-tone="purple|pink|blue|orange"], defaulting to the primary when no tone is set; skeleton tokens
(--cp-skeleton-base, --cp-skeleton-shine); an entrance duration if D4 is yes. Update the token list in
.claude/rules/design-system.md and the showcase's token page.
CONSTRAINT: APP-002, APP-009; .claude/rules/design-system.md (semantic tokens only, both themes); DESIGN-SYSTEM
.md "Content hierarchy", "The page head", "Accessibility baseline", "The focus ring"; the AU.0 scorecard.
RESTRICTION: Docs and tokens only: no component changes. Every --cp-tone-text clears 4.5:1 and every
--cp-tone-on clears 4.5:1 on --cp-tone, against every surface it can sit on, in both themes; show the ratios.
No literal colour outside themes.css. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review after writing.
BEHAVIOR: Present D1-D5 with the recommendations above and the AU.0 evidence, wait for the owner's answers,
then write the rules and tokens, show the contrast table, run npm run build and npm test from src/web, and
report.
```

## AU.2.1 Tone plumbing

```text
SCOPE: A route may declare data: { tone: 'purple' | 'pink' | 'blue' | 'orange' }. The shell's page frame reads
it and sets data-cp-tone on the frame element inside <main>, so every part on the page resolves --cp-tone*
from AU.1. Set the D1 tones on the routes for AI Recipe Studio, Image Studio, Social Studio, Long-form and the
Content Pipeline (and their child routes). Add a page-contract rule "tone": a feature stylesheet or template
neither sets data-cp-tone nor declares --cp-tone*; the route owns it.
CONSTRAINT: APP-002; AU.1 tokens; frontend.md "The shell's page frame owns the one gutter"; the existing
pageWidth route-data handling in the shell, which this mirrors.
RESTRICTION: The nav, the sidebar's active-item style and the top bar do not change colour with the tone.
Nothing on a page uses the tone yet; parts adopt it in AU.2.5 onward. Do not run backend tests.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show where the frame applies the attribute and the routes that get a tone; wait for approval;
implement with specs (a toned route sets the attribute, an untoned one does not, navigating between them
clears it) and the contract rule with its own test; run npm run build and npm test from src/web.
```

## AU.2.2 A true dark card (only if D2 chose to replace parchment)

```text
SCOPE: Remove the parchment re-declaration from cp-card under [data-cp-theme='dark'] (card.component.ts) and
give dark cards a raised dark surface instead: --cp-surface with a lighter hairline and the dark shadow, so a
card reads as lifted from the page without turning light. Remove the card-scoped --cp-primary-text, --cp-focus
and field overrides that existed only because of the parchment, and confirm fields, chips, pills, notices and
the focus ring inside a card against the dark page values. Update the DESIGN-SYSTEM.md focus-ring section that
describes the card's re-declared ring.
CONSTRAINT: APP-009; D2; LP.11A's measured ratios and its method; DESIGN-SYSTEM.md "The focus ring" and
"Native form controls and color-scheme"; cp-drawer, cp-dialog and cp-lightbox surfaces, which must match.
RESTRICTION: Every ring ≥3:1, every text ≥4.5:1, every control edge ≥3:1, inside a card, in dark. Light theme
does not change by a pixel. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review; playwright-cli for before/after screenshots of
three dense pages (recipe editor, Image Studio, workspace settings) in dark.
BEHAVIOR: Show the before/after ratio table and screenshots; wait for approval; implement, update the
showcase and docs, run npm run build and npm test from src/web, and report.
```

## AU.2.3 Skeleton loader and the list shell's loading state

```text
SCOPE: Add CpSkeletonComponent to @creator-pantry/ui: shapes line, block, circle and card (a media-card
silhouette), a count, and widths that vary so a block of lines does not read as a table. The shimmer moves by
background-position (the "drawer" rule rejects translateX) and is static under prefers-reduced-motion. The
host is aria-hidden; the region that is loading keeps aria-busy and one polite status sentence for screen
readers. Give cp-list-shell a loadingLayout input ('cards' | 'rows', default 'rows') that draws skeletons in
the shape of what is coming, replacing its visible "Loading…" text with a visually hidden one.
CONSTRAINT: APP-003; AU.1 skeleton tokens; .claude/rules/design-system.md extension rules 1-9; list-shell's
existing loading/error/empty/ready contract and its specs.
RESTRICTION: No feature adopts it in this prompt except through cp-list-shell's default. A screen reader still
hears that the region is loading, exactly once. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review after implementation.
BEHAVIOR: Define the selector, inputs, ARIA and reduced-motion behaviour; wait for approval; implement with
specs, add it to public-api.ts, the showcase, README.md, DESIGN-SYSTEM.md and the public-API list in CLAUDE.md
and .claude/rules/design-system.md; run npm run build and npm test from src/web.
```

## AU.2.4 Empty states with illustrations

```text
SCOPE: Give cp-empty-state an illustration input naming one of a small drawn set: recipe, picture, post,
article, calendar, board, prompt, brand, search, sparkle. Each is inline SVG line art in currentColor, about
7rem, coloured --cp-tone-text, aria-hidden. The first-use variant becomes a roomier layout: illustration,
title at --cp-font-size-xl, one or two sentences at --cp-font-size-md, and the projected primary action. The
no-results variant stays compact and uses "search". The icon input stays for compatibility; illustration wins
when both are given.
CONSTRAINT: APP-004; D5; DESIGN-SYSTEM.md "A card for one thing in a library" (empty-state glyph pairing) and
"What the product says about the thing on screen"; the existing empty-state specs.
RESTRICTION: Illustrations are drawn for this library, not traced from stock or generated by a model. No
picture of food carries a claim (no "healthy", no "gluten-free" on a drawing). Pages adopt it in their own
prompts. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review.
BEHAVIOR: Show the illustration set in both themes and every tone; wait for approval; implement with specs,
update the showcase and docs, run npm run build and npm test from src/web.
```

## AU.2.5 Page head accent and record picture

```text
SCOPE: Give cp-page-head what D3 chose. As recommended: an optional icon input drawn in a tone-filled tile
beside the title (the same icon set the nav uses, passed by name so the library names no route), the head's
hairline in --cp-tone when a tone is set, and a [cpPageHeadMedia] slot for a detail page's own picture (a
fixed-ratio thumbnail beside the title on wide screens, above it on narrow). The title stays
--cp-font-size-title and the one h1.
CONSTRAINT: APP-002, APP-005; D3; DESIGN-SYSTEM.md "The page head" and line 27; the page-head specs and
focusHeading(); the "page-head" contract rule.
RESTRICTION: The media slot never takes a URL: the feature authorises and loads its own bytes, as cp-media-card
does. Empty slots take no room. The icon is decorative; the heading stays the name. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review.
BEHAVIOR: Show the head in each tone and with/without media at 375 and 1440 px; wait for approval; implement
with specs, update the showcase and docs; run npm run build and npm test from src/web.
```

## AU.2.6 Media card without a picture

```text
SCOPE: Replace cp-media-card's flat tint and text glyph with a designed panel: a soft --cp-tone-soft (or
neutral) field with a faint repeating pattern drawn in CSS, and the kind's illustration from AU.2.4 at a
small size in place of the glyph. Keep the glyph input working by mapping the documented glyphs (▣ ⌘ ❝ ◆) to
their illustrations. Give the projected picture a subtle zoom on hover/focus of the card's link, off under
reduced motion.
CONSTRAINT: APP-005; DESIGN-SYSTEM.md "A card for one thing in a library" and line 144; AU.2.4's set.
RESTRICTION: Still never borrows a picture from something related. A card with and without a picture keep the
same height in a row. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review.
BEHAVIOR: Show a mixed grid (with and without pictures) in both themes; wait for approval; implement with
specs, update showcase and docs; run npm run build and npm test from src/web.
```

## AU.3.1 One treatment for generated content

```text
SCOPE: Audit how every feature marks AI output today (start with ai-proposal-panel, ai-advisory-results,
channel-post-review, generated-image-run, the content-pipeline prompt and reference panels, brand style
test-drive and recipe-revision-request) and add CpGeneratedComponent to the library: a surface edged in the
purple tone with a "Drafted by AI" label (glyph plus words, never colour alone), an optional provenance line
(the feature passes model and time as text), the projected content, and a [cpGeneratedActions] row in a fixed
place for accept / edit / discard. Add a contract rule that a feature does not hand-draw an AI label. Adopt it
in the audited features in this prompt only where the swap is one-for-one; list the rest for their page
prompt.
CONSTRAINT: APP-006; CLAUDE.md "Generated content is visibly identified and remains editable before
acceptance"; .claude/rules/ai.md "Output lifecycle"; content.md derivatives; DESIGN-SYSTEM.md notices.
RESTRICTION: Presentation only: no change to when output is saved, accepted or discarded. Do not log or
display prompt bodies. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review; ai-safety-reviewer on the label copy and the
provenance line.
BEHAVIOR: Present the audit table (feature, current marker, swap now or later); wait for approval; implement
with specs and the contract rule, update showcase and docs; run npm run build and npm test from src/web.
```

## AU.3.2 Option card for AI choices

```text
SCOPE: Add CpOptionCardComponent: one choice among several. A title, up to three short key facts as tags (the
feature chooses which), a one-sentence summary, the remaining detail behind a native disclosure, and a single
selected state shown by an edge, a glyph and text. The choose action is the card's one button; in a group the
cards sit in ul[cpCardGrid] and are compared side by side. Selecting is the feature's output event.
CONSTRAINT: APP-007; DESIGN-SYSTEM.md "A grid of cards", "Facts"; CpChoiceGroupComponent, which stays the
answer for a form field with tiles (document when to use which).
RESTRICTION: Not a form control and not a radio: no value accessor. Detail is never truncated without a way to
read it. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review.
BEHAVIOR: Define the API and the CpChoiceGroup boundary; wait for approval; implement with specs, update
showcase and docs; run npm run build and npm test from src/web.
```

## AU.3.3 Figure tile and featured card

```text
SCOPE: Add CpStatTileComponent: a figure, its label, an optional sentence of context and an optional link,
with the figure set in Fredoka at --cp-font-size-2xl and the label in words beside it (never a bare number).
Add a featured input to cp-card: a --cp-tone-soft (or primary-soft) surface and a stronger edge, for the one
topic on a page that matters most. Add a contract note in DESIGN-SYSTEM.md: at most one featured card per page.
CONSTRAINT: APP-008; DESIGN-SYSTEM.md "Meters and figures" (no invented precision, units in words); the
dashboard rule in frontend.md (the read model's counts, derived nowhere in the page).
RESTRICTION: No trend arrows or deltas unless the server sends the comparison. No charts in this prompt. Do
not run backend tests.
UTILIZATION: creatorpantry-design-system skill; dataviz skill for the figure styling; design-review.
BEHAVIOR: Show both parts in each tone and theme; wait for approval; implement with specs, update showcase and
docs; run npm run build and npm test from src/web.
```

## AU.3.4 Entrance motion (only if D4 is yes)

```text
SCOPE: Add a library directive (cpEnter) that fades and lifts an element in by a few pixels the first time it
renders, with an optional stagger for list items, using the AU.1 duration. Apply it in the library where it is
always right: a cp-list-shell's ready content, a cp-generated result arriving, a cp-option-card group. Pages do
not apply it themselves in this document.
CONSTRAINT: APP-010; D4; DESIGN-SYSTEM.md "Accessibility baseline" (reduced motion); LandingRevealDirective as
the reference for print and reduced-motion handling, not for scroll triggering.
RESTRICTION: Nothing moves under prefers-reduced-motion, nothing is hidden when script fails or under print,
focus never lands on an element that is still invisible, and nothing re-animates on change detection. No
scroll-triggered motion. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review.
BEHAVIOR: Show it with motion on and off; wait for approval; implement with specs, update the showcase and
docs; run npm run build and npm test from src/web.
```

## AU.4.1 Dashboard

```text
SCOPE: Rework the dashboard with the new parts: the head with its lede, a row of figure tiles from the read
model's existing counts, the most urgent topic as the one featured card, the remaining topic cards in
ul[cpCardGrid], skeletons while it loads (replacing "Loading your dashboard…"), an illustrated first-use state
in "Get started", and an illustrated quiet state in any topic with nothing in it.
CONSTRAINT: APP-003, APP-004, APP-008; frontend.md's dashboard rule (DashboardComponent is the reference: most
urgent first, counts from the read model, an unreadable topic keeps its card with a notice, each item links to
where it is dealt with); dashboard.models.ts.
RESTRICTION: No new counts and no new endpoint. If a figure tile would need a number the read model does not
send, leave it out and list it. Do not run backend tests.
UTILIZATION: design-review; test-gap-analyzer on the states; playwright-cli screenshots before/after.
BEHAVIOR: Show the layout with the AU.0 capture beside it; wait for approval; implement with specs for every
state (loading, empty, degraded, error, success); run npm run build and npm test from src/web.
```

## AU.4.2 Recipe library shows each recipe's picture

```text
SCOPE: Add an optional heroAsset to the recipe list's ServiceModel (the media asset id and version of the
recipe's Hero asset link, or null) through the full seam, projected in the repository's list query rather than
loaded per row. In the web, add the field to RecipeSummary and its decoder, and project the picture into each
cp-media-card through the authorised thumbnail loader the DAM already uses (promote it to src/app/shared if it
lives in features/dam). Give the library cp-list-shell's 'cards' skeleton and AU.2.4's "recipe" first-use and
"search" no-results states.
CONSTRAINT: APP-005; add-endpoint skill; backend.md seam; tenancy.md (the hero asset is read inside the
workspace filter, and an asset the caller cannot read is null, never a 404 or an error); api-contract.md
(additive optional field); recipes.md; media.md (renditions, not originals, are what pages show).
RESTRICTION: Additive only: no rename, no change to ordering or paging. A deleted, hidden or unreadable asset
yields null, and the card falls back to AU.2.6's panel. No IgnoreQueryFilters. **Test policy:** write the
backend tests (the projection, a recipe with no hero, a hero whose asset is gone, and the two-workspace
isolation test) and the web specs; prove the solution compiles with dotnet build (redirected output); run npm
test; **do not run the backend suite and do not regenerate the OpenAPI snapshot.** Both happen in AU.8. Mark
this prompt "built (backend tests pending AU.8)".
UTILIZATION: add-endpoint skill; architecture-reviewer, workspace-isolation-auditor and api-contract-checker
after implementation.
BEHAVIOR: Show the query change, the ServiceModel diff and the card in both themes; wait for approval;
implement; run dotnet build, npm run build and npm test; report what AU.8 must run for this prompt.
```

## AU.4.3 Recipe detail as the editorial face of the app

```text
SCOPE: Give the recipe detail page (and the kitchen view's on-screen head, not its print layout) the AU.2.5
head with the recipe's Hero picture in the media slot, loaded from the asset link the detail response already
carries; times and yield as a dl[cpFacts] inline strip under the head; ingredients and method side by side at
wide widths and stacked at narrow; skeletons while loading. The recipe's own words are shown exactly as stored.
CONSTRAINT: APP-005; recipes.md (creator text is verbatim; yield's three facts stay separate; total time is
shown as stored); frontend.md detail-page rule; DESIGN-SYSTEM.md "The two shapes"; the recipe-kitchen-view
exemptions in check-page-contract.mjs, which stay as they are.
RESTRICTION: No change to the editor's form, its Save, or what it writes. Print output of the kitchen view does
not change (UI.6). No dietary or allergen wording is added. Do not run backend tests.
UTILIZATION: design-review; playwright-cli screenshots at 375/1440 in both themes, and a print preview of the
kitchen view to prove it is unchanged.
BEHAVIOR: Show the layout; wait for approval; implement with specs; run npm run build and npm test from
src/web.
```

## AU.5.1 AI Recipe Studio

```text
SCOPE: Present the studio's concepts as cp-option-card in a comparison grid: the dish name as the title, time
budget, skill fit and dietary notes as the key facts, the assumptions behind the disclosure, "Choose this
concept" as the card's one action. Wrap generated results in cp-generated. Show "What the model flagged" as a
cp-notice in its own place, not a card of prose. Give every step a skeleton and an illustrated empty state.
The route wears the purple tone and its page head the studio's icon.
CONSTRAINT: APP-006, APP-007; ai.md (proposals, not applied); recipes.md "Safety and claims" (dietary notes
stay labelled descriptive, never a guarantee); the AU.3.1 audit's leftovers for features/ai.
RESTRICTION: No change to prompts, generation, what is saved or the order of steps. The "descriptive only, not
a guarantee" wording stays with every dietary note. Do not run backend tests.
UTILIZATION: design-review; ai-safety-reviewer on the dietary and assumption presentation.
BEHAVIOR: Show the concept grid against the tour capture; wait for approval; implement with specs; run npm run
build and npm test from src/web.
```

## AU.5.2 Image Studio

```text
SCOPE: Pink tone and icon. Results as a picture-led grid in cp-generated, each with "View larger" into
cp-lightbox; skeleton tiles at the result's aspect while a render runs; illustrated empty states for "no
recipes to link" and "no pictures yet"; the in-page cp-anchor-nav and form sections unchanged in structure.
CONSTRAINT: APP-005, APP-006; media.md (renditions; alt text describes only known content); DESIGN-SYSTEM.md
lightbox rules; the hosted-in-pipeline mode of the component, which must look right in both places.
RESTRICTION: No change to generation, credits, or what is stored. Do not run backend tests.
UTILIZATION: design-review; playwright-cli screenshots standalone and hosted in the Content Pipeline.
BEHAVIOR: Show both modes; wait for approval; implement with specs; run npm run build and npm test from src/web.
```

## AU.5.3 Social Studio

```text
SCOPE: Blue tone and icon. Channel posts in cp-generated with the channel named in words and a short preview
of how the post sits (length against the channel's limit with cp-progress); variants compared with
cp-option-card where the studio offers a choice; skeletons and illustrated empty states.
CONSTRAINT: APP-006, APP-007; content.md (channel constraints live in channel profiles, not page text);
publishing.md (nothing implies a post is published); shared/channel-posts.
RESTRICTION: No change to drafting, saving or publishing; the canDeactivate guard keeps working. No provider
branding. Do not run backend tests.
UTILIZATION: design-review.
BEHAVIOR: Show the layout; wait for approval; implement with specs; run npm run build and npm test from src/web.
```

## AU.5.4 Content Pipeline and Long-form

```text
SCOPE: Orange tone on both flows. Each step's card takes cp-generated for generated output, skeletons for its
loading, and an illustrated empty state where a step can start empty. The flow's cp-progress and cp-stepper
pick up the tone. The resume card, if present, becomes a featured card.
CONSTRAINT: APP-006; frontend.md flow rule (ContentPipelineShellComponent and BrandSetupShellComponent are the
references); the AU.3.1 leftovers for content-pipeline and long-form.
RESTRICTION: No change to steps, their order, saving or focus movement between steps. If the change set
passes about 25 files, stop after the Content Pipeline and split Long-form into its own prompt. Do not run
backend tests.
UTILIZATION: design-review.
BEHAVIOR: Show one step of each flow before/after; wait for approval; implement with specs; run npm run build
and npm test from src/web.
```

## AU.6.1 Pictures and the Content Board

```text
SCOPE: Pictures (DAM): 'cards' skeletons, the AU.2.6 panel for assets still processing, illustrated "picture"
first-use and "search" no-results states, cp-generated's label on AI-generated assets in place of any private
marker. Content Board: skeleton cards per column, an illustrated "board" first-use state, and a quiet line in an
empty column. A board has no featured card.
CONSTRAINT: APP-003, APP-004; frontend.md library and board rules (DAM is the reference library;
ContentBoardComponent the reference board); media.md.
RESTRICTION: No change to upload, moves, ordering or the keyboard move control. Do not run backend tests.
UTILIZATION: design-review.
BEHAVIOR: Show both pages; wait for approval; implement with specs; run npm run build and npm test from src/web.
```

## AU.6.2 My Day and My Week

```text
SCOPE: My Day gains an illustrated "calendar" empty state (it has none) and today's top three as the featured
card. My Week gains skeleton rows and day columns that show a quiet line, not a blank, when a day is free.
CONSTRAINT: APP-004; the reading and wide page widths these routes already ask for; shared/work-tasks and
shared/day-notes.
RESTRICTION: No change to task behaviour, quick capture or the "Top three" rule. Do not run backend tests.
UTILIZATION: design-review.
BEHAVIOR: Show both pages; wait for approval; implement with specs; run npm run build and npm test from src/web.
```

## AU.6.3 Brand, Prompt Library, Workflows and Quick Recipe

```text
SCOPE: Each page adopts the parts it is missing: skeletons in place of its loading sentences, an illustrated
empty state from the set ("brand", "prompt", "board", "recipe"), cp-generated on any generated text (brand
style test-drive, generated prompts), and Quick Recipe's first-use state.
CONSTRAINT: APP-003, APP-004, APP-006; the existing page shapes of each; the AU.3.1 leftovers.
RESTRICTION: No behaviour change. Split per page if the change set passes about 25 files. Do not run backend
tests.
UTILIZATION: design-review.
BEHAVIOR: Show each page; wait for approval; implement with specs; run npm run build and npm test from src/web.
```

## AU.6.4 AI allowance and Workspace settings

```text
SCOPE: AI allowance: the allowance and its use as cp-stat-tile figures over the existing breakdown lists,
skeletons while loading. Workspace settings and Themed days: a lede for settings, skeletons, illustrated empty
states where a list can be empty.
CONSTRAINT: APP-003, APP-008; DESIGN-SYSTEM.md "Meters and figures"; the ai-usage card-grid exemption in
check-page-contract.mjs, which stays.
RESTRICTION: Figures come from what the server sends; no client arithmetic beyond formatting. No billing UI.
Do not run backend tests.
UTILIZATION: design-review.
BEHAVIOR: Show both pages; wait for approval; implement with specs; run npm run build and npm test from src/web.
```

## AU.7 Loading sentence sweep and its rule

```text
SCOPE: Find every remaining visible loading sentence in src/app (grep for "Loading" in templates and inline
templates, excluding specs) and replace each with skeletons in the shape of what is coming, keeping one
visually hidden status sentence for screen readers. Add a page-contract rule "loading": a routed page's or
shared component's template shows no visible "Loading…" sentence outside cp-skeleton's hidden status; an
exemption is a listed path with a reason.
CONSTRAINT: APP-003; AU.2.3; check-page-contract.mjs's rule and exemption format and its self-tests.
RESTRICTION: "Loading more…" on a pager button may stay as button text if the button is the only thing
changing; list each such case. Do not run backend tests.
UTILIZATION: design-review; test-gap-analyzer on loading states.
BEHAVIOR: Present the list of sites and what replaces each; wait for approval; implement with the rule and its
tests; run npm run build and npm test from src/web.
```

## AU.8 Full verification, backend suite and re-capture

```text
SCOPE: Verify the whole document. (1) Run the full backend suite, once:
dotnet run --project src/CreatorPantry.Tests -p:BaseOutputPath=<scratch>/ (redirected while aspire run is
live; about 15 minutes; the baseline is green except the named load-sensitive tests in memory). (2) Regenerate
the OpenAPI snapshot with UPDATE_OPENAPI_SNAPSHOT=1 for the AU.4.2 field, read the diff, and confirm it is the
one additive optional property and nothing else. (3) Fix every backend failure caused by this document; a
failure that predates it is reported, not fixed. (4) From src/web: npm ci, npm run build, npm test. (5) Run
npm run capture:app with label "after" and present before/after pairs for every page, then re-score the AU.0
rubric. (6) Walk the app with playwright-cli: both themes, 375/768/1440 px, 200% zoom, keyboard only, reduced
motion, every toned route, and the nav unchanged against its AU.0 capture. (7) Mark every "built" prompt done
once its backend tests pass.
CONSTRAINT: APP-001..010; CLAUDE.md Usage (test command, BaseOutputPath, Docker for the SQL Server fixture);
api-contract.md snapshot rule; frontend.md Verification; design-system.md extension rule 9.
RESTRICTION: Fix only findings within this document's scope. Do not loosen a contract rule or a test to pass.
The nav and the landing page must be pixel-identical to AU.0 apart from shared token changes approved in AU.1
and AU.2.2; show any difference.
UTILIZATION: design-review, test-gap-analyzer, api-contract-checker, workspace-isolation-auditor (for AU.4.2);
playwright-cli for the walk and screenshots.
BEHAVIOR: Run everything, fix in-scope findings, re-run what a fix touched, and report proof for each item:
suite totals, the snapshot diff, the scorecard before/after, and screenshots attached.
```
