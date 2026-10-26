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
| D4 | Does content animate in? | One short fade-and-rise (`--cp-duration-enter`, 200ms, which AU.1 added; there is no `--cp-duration-base`) when a route's content or a result first renders, done in the library, and nothing under `prefers-reduced-motion`. No scroll reveal inside the app. |
| D5 | Illustrations for empty states? | A small set of drawn line illustrations as inline SVG in the library, one colour (`currentColor`, picking up the tone), decorative (`aria-hidden`). Not stock photos and not AI-generated pictures, so they stay crisp, theme-aware and weightless. |

*Settled by the owner, 2026-10-25: all five as recommended. D2 replaces parchment, so AU.2.2 runs in full.*

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

*Checked against the AU.0 capture (2026-10-25). Confirmed: page head, pictures, AI output, AI choices, figures,
and the parchment dark card. Corrected:*

- *Colour: `features/ai` and `shared` use the accents too, not only recipes and brand. The finding stands.*
- *Loading: about 62 non-spec files print about 50 different sentences, not about 21. Every page scored 0. Image
  Studio's recipe picker shows nothing at all while it loads, and five pages put a placeholder in the h1
  ("Asset", "Loading card…", "Workflow", "Kitchen view", "Edit recipe").*
- *Empty states: the dashboard has a "Get started" card and My Day has two plain-card states; the AI allowance
  page always has figures; Quick Recipe, the Content Pipeline and Long-form are flows that open on step 1. What
  is missing is a designed empty state where Image Studio, Social Studio and the flows' recipe step have only a
  one-line note ("You have no recipes yet, so there is nothing to link").*
- *Dark mode was worse than recorded: a selected `cp-choice-group` answer inside a card was 1.35:1. Fixed on its
  own after AU.0 (see its delivery note).*
- *Pages: 33 signed-in routes counting the `/app` gate; 28 were photographed.*

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

## AU.0 Baseline capture and scorecard (report only) - done

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

*Delivery note (2026-10-25): captured and scored; 224 pictures of 28 pages in `src/web/.captures/before/`
(`index.html` and four contact sheets), nothing committed.*

*The capture account (`robert@creatorpantry.com`) belongs only to `homespun-favorites`, which the owner confirmed
is sample data. It is added to `capture-app.mjs`'s `SAMPLE_WORKSPACES`, not to the tour's. Run it as
`CAPTURE_WORKSPACE=homespun-favorites`. The run's empty workspace is `capture-empty-before-2026-10-25-10-27`.
Workspaces cannot be deleted, so AU.8 reuses it with `CAPTURE_EMPTY_WORKSPACE` rather than making another.*

*Not photographed:*

- *The voice guide, its history and its test drive: no `CAPTURE_URL_BRAND_GUIDE` was given, and no list links to
  a guide.*
- *A saved prompt: the sample workspace has none, and it has no themed days either, so Prompt library and Themed
  days were only ever seen empty.*
- *The `/app` gate, which only redirects into a workspace.*

*Studio output was not on screen in the run. The AI Recipe Studio's "generated" score comes from the tour capture.*

*Scorecard: the averages are loading 0.00, dark 0.71, identity 0.79, empty 0.81, imagery 0.80, density 1.29 and
hierarchy 1.43. The three worst pages:*

1. *AI Recipe Studio, 10%.*
2. *The recipe editor, 17%.*
3. *Image Studio, 17%, standing for Social Studio and the Content Pipeline's first step, which share its form.*

*`design-review` checked the scores and changed three.*

*Two defects the AU list did not cover:*

1. *A selected `cp-choice-group` answer inside a dark card was 1.35:1, so on Workspace settings and Long-form the
   creator could not read what they had chosen. At the owner's request this was **fixed on its own** before AU.1:*
   - *`cp-card` re-declares `--cp-primary-soft` as a light mint in the dark theme.*
   - *Every part that wrote `--cp-primary` as text on that fill now writes `--cp-primary-text`: badge, status
     pill, chip, diff legend, the notice glyph, `ai-proposal-panel` and both version comparisons.*
   - *A picked tile is edged in `--cp-primary-text`.*
   - *`design-system.spec.ts` measures the painted pairs inside a card in both themes. It failed at 1.35:1 before
     the fix.*
   - *DESIGN-SYSTEM.md's focus-ring section records it.*

   *Light theme and the dark page are unchanged, because outside a dark card the two tokens are one colour. The
   danger family has the same cause and is left to AU.2.2.*
2. *The picture page is 550px wide at a 375px viewport. It is folded into AU.6.1.*

*The other findings are folded into AU.2.2, AU.2.3, AU.4.1, AU.4.2, AU.4.3, AU.5.1, AU.6.1, AU.6.3, AU.7 and
AU.8 as "From AU.0" sentences.*

*The showcase suite was intermittent in this session, on code this fix does not touch:*

- *A clean tree failed 5 ContentCardDetail editing specs.*
- *With the fix, different landing and recipe-editor scroll specs failed.*
- *Each passed when run alone.*

*The library suite is 326/326.*

## AU.1 Design decisions, rules and tokens - done

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

*Delivery note (2026-10-25): the owner chose all five recommendations. The decisions are written into
DESIGN-SYSTEM.md "Warmth inside the app", its "Layers" list, and `.claude/rules/design-system.md`. Line 27
stands, because D3 chose no underline.*

*Tokens:*

- *In `themes.css`, for light, dark and print:*
  - *`--cp-{pink,orange,purple,blue}-text`.*
  - *The four tone defaults, `--cp-tone`, `-soft`, `-text` and `-on`, which are the primary's.*
  - *`--cp-skeleton-base` and `-shine`.*
  - *Four `[data-cp-tone]` rules, last in the file so a tone wins over print.*
- *In `tokens.css`: `--cp-duration-enter` (200ms), `--cp-enter-distance`, `--cp-enter-stagger` and
  `--cp-duration-skeleton`. There was no `--cp-duration-base`, and D4 now says so.*

*What differs from the prompt:*

- *Orange cannot be its own text in the light theme. It is 4.25:1 on the raised surface and 4.37:1 on the subtle
  one, so `--cp-orange-text` is `#a84a0a`, at 4.87:1. That is why the accents gained `-text` tokens.*
- *The showcase had no token page. It gained a "Tool tones" section instead, showing each tone as a fill, a tint
  and text, plus the skeleton, in both themes.*

*Measured minimums across `--cp-bg`, the three surfaces, `--cp-field-bg` and the tone's own tint:*

| Theme | Worst `--cp-tone-text` | Worst `--cp-tone-on` on `--cp-tone` |
| --- | --- | --- |
| Light | 4.53:1 (no tone, on the raised surface) | 5.01:1 (orange) |
| Dark | 5.21:1 (pink, on the raised surface) | 7.25:1 (pink) |

*`design-system.spec.ts` measures the 10 theme × tone cases and the skeleton. Its orange case failed at 4.25:1
with the raw carrot, before the fix.*

*`design-review` raised these, now handled:*

- *The tone defaults are wrong inside a parchment card. They are now documented, and AU.2.2 must prove them.*
- *A drawer leaves the toned frame. AU.2.1 gains that.*
- *Reduced motion zeroes durations but not delays. AU.3.4 gains that.*
- *The tone block came before the print rules. It has moved after them.*
- *Wording: tone versus status, the featured card's tinted surface, "Loading more…", and pages D1 did not name.*

*One question is left for the owner, written into AU.3.1: should AI output be edged in purple on every tool, or
take no tone?*

*`npm run build` and `npm test` pass. The library is 338/338 and the page contract holds. The showcase was
6075/6075 on the final run. Earlier runs this session failed 5 to 30 specs in the known order-dependent
families: content card, landing credits, pricing and collage, and recipe-editor scroll. That is not caused by
this change, and it has not been fixed.*

## AU.2.1 Tone plumbing - done

```text
SCOPE: A route may declare data: { tone: 'purple' | 'pink' | 'blue' | 'orange' }. The shell's page frame reads
it and sets data-cp-tone on the frame element inside <main>, so every part on the page resolves --cp-tone*
from AU.1. Set the D1 tones on the routes for AI Recipe Studio, Image Studio, Social Studio, Long-form and the
Content Pipeline (and their child routes). Add a page-contract rule "tone": a feature stylesheet or template
neither sets data-cp-tone nor declares --cp-tone*; the route owns it. From AU.1's review: cp-drawer moves itself
to <body> while open, outside the frame, so a drawer opened on a toned page would draw untoned. When it opens it
carries the opener's nearest data-cp-tone onto its own host; test that too.
CONSTRAINT: APP-002; AU.1 tokens; frontend.md "The shell's page frame owns the one gutter"; the existing
pageWidth route-data handling in the shell, which this mirrors.
RESTRICTION: The nav, the sidebar's active-item style and the top bar do not change colour with the tone.
Nothing on a page uses the tone yet; parts adopt it in AU.2.5 onward. Do not run backend tests.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show where the frame applies the attribute and the routes that get a tone; wait for approval;
implement with specs (a toned route sets the attribute, an untoned one does not, navigating between them
clears it) and the contract rule with its own test; run npm run build and npm test from src/web.
```

*Delivery note (2026-10-25): shipped as planned.*

- *The shell reads `data: { tone }` beside `pageWidth`. The deepest route wins and an unknown value is dropped.
  The frame binds it as `[attr.data-cp-tone]`, so an untoned page has no attribute.*
- *The tones sit on the five tools' parent routes, so `context/:id`, the first-draft review, every pipeline step
  and every long-form address inherit them. On the live app the frame was the only toned element: Recipes,
  Quick Recipe and the Workflows hub carried none, and `--cp-tone` resolved to each accent.*
- *`cp-drawer`'s panel copies the nearest `data-cp-tone` as it moves to `<body>`, and clears it otherwise.*

*The `tone` page-contract rule covers more than the prompt asked. `design-review` found ways around the first
version, so it also reads inline style declarations and component code: `host` bindings, `setAttribute` and
`style.setProperty`. The shell's template is its one exemption. A self-test holds that template to the single
frame binding, so the exemption cannot cover a toned nav.*

*Specs:*

- *Shell: no tone, a parent's tone reaching the page, an unknown tone dropped, frame only, cleared and restored
  on navigation, and deepest wins.*
- *Drawer: carries the tone, takes none, and does not keep a tone across a reopen.*
- *`app.routes.spec.ts` resolves every lazy child and holds the table to D1 exactly. It failed on a deliberate
  `'purpel'`.*

*`npm run build` passes. The library is 341/341 and the page contract holds. The showcase failed 10 to 45 specs
per run, all in the known order-dependent families, none in the shell, routes or drawer, and each passes alone.
The new families seen this time (My Week moving, Content Pipeline brand defaults) point at specs leaving
SweetAlert confirmations in `document.body`. That is pre-existing and not fixed here.*

## AU.2.2 A true dark card (only if D2 chose to replace parchment) - done

```text
SCOPE: Remove the parchment re-declaration from cp-card under [data-cp-theme='dark'] (card.component.ts) and
give dark cards a raised dark surface instead: --cp-surface with a lighter hairline and the dark shadow, so a
card reads as lifted from the page without turning light. Remove the card-scoped --cp-primary-text, --cp-focus
and field overrides that existed only because of the parchment, and confirm fields, chips, pills, notices and
the focus ring inside a card against the dark page values. Update the DESIGN-SYSTEM.md focus-ring section that
describes the card's re-declared ring. From AU.0: also remove the card-scoped --cp-primary-soft that the AU.0
fix added. Then make the danger family read inside a dark card: a field's error text is --cp-danger on the
card at 2.22:1, and the cp-form-section problem banner and cp-validation-summary put ink on a --cp-danger-soft
mixed against the dark page. Do the same for the pink, orange, purple and blue tints. If D2 keeps parchment,
this prompt still runs, limited to the danger family and the other tints. From AU.1: the tone tokens are
computed where the theme declares them, so on parchment even an untoned --cp-tone-text is the dark page's mint
(1.45:1). With the card's re-declarations gone, add a spec that mounts a toned frame holding a card, plus an
untoned card, and measures --cp-tone-text on the card's surfaces and on --cp-tone-soft in both themes.
CONSTRAINT: APP-009; D2; LP.11A's measured ratios and its method; DESIGN-SYSTEM.md "The focus ring" and
"Native form controls and color-scheme"; cp-drawer, cp-dialog and cp-lightbox surfaces, which must match.
RESTRICTION: Every ring ≥3:1, every text ≥4.5:1, every control edge ≥3:1, inside a card, in dark. Light theme
does not change by a pixel. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review; playwright-cli for before/after screenshots of
three dense pages (recipe editor, Image Studio, workspace settings) in dark.
BEHAVIOR: Show the before/after ratio table and screenshots; wait for approval; implement, update the
showcase and docs, run npm run build and npm test from src/web, and report.
```

*Delivery note (2026-10-25): shipped, and wider than asked, at the owner's direction.*

- *A dark `cp-card` re-declares no token. It is `--cp-surface` edged in a new `--cp-card-edge`, which is
  `--cp-border` in light and `--cp-border-strong` in dark, so the component no longer sniffs the theme.
  `cp-dialog` and `cp-lightbox` share that edge.*

*Two owner decisions taken during the prompt:*

1. **The dark theme is charcoal.* *The owner judged the green-black odd. After a side-by-side preview, every dark
   neutral became a grey: page `#202020`, sheet `#2a2a2a`, subtle `#303030`, raised `#353535`, field `#1b1b1b`,
   ink `#f2f2f0`, muted `#b4b4b0`, faint `#a3a39f`, borders `#3a3a3a` and `#4d4d4d`, control edge `#82827e`.
   Accents and their tints did not change. The dark danger tint mixes 12% rather than 14%, because at 14% the
   danger text on it was 4.49:1. The theme change also reaches the nav, the top bar and the landing page's dark
   mode: they read the same theme tokens. The owner chose this knowing that.*
2. **Fix it in light too.** *`design-review` found that drawn control edges were 1.5–1.9:1 in both themes, and the
   toolbar search/select fields sat on a 1.2:1 hairline. `--cp-border-control` now edges the checkbox's box, the
   radio/check mark, the secondary button and the toolbar fields. Light `--cp-border-control` darkened from
   `#998e7f` to `#8a7f70`, because the old value was 2.72:1 on the raised surface and 2.81:1 on the subtle one.
   The ghost button keeps its hairline on purpose: its label identifies it, and low emphasis is its job.*

*So "light does not change by a pixel" was broken deliberately, for control edges only. Before that, with only
the card changed, light was proved pixel-identical on three pages, with a control showing the old card was the
one served.*

*Measured, dark, inside a card:*

| Pair | Ratio |
| --- | --- |
| Ink | 12.80:1 |
| Muted | 6.90:1 |
| Faint (worst, on raised) | 4.69:1 |
| Primary text | 7.67:1 |
| Ring on card / subtle / field | 7.67 / 7.05 / 9.20:1 |
| Control edge, worst (raised) | 3.18:1 |
| Error text | 5.63:1 |
| Ink on the danger tint | 10.57:1 |
| Danger on its tint | 4.65:1 |
| Accents as text | 5.49–6.96:1 |
| Card fill / edge against the page | 1.14 / 1.93:1 |

*Light: control edge 3.32:1 at worst. The worst ring on a tint is the light danger tint, at 4.24:1.*

*Specs in `design-system.spec.ts`:*

- *Every ink on all five surfaces, and every accent on its tint.*
- *The control edge on every surface, plus each drawn control's painted edge, on the page and in a card.*
- *"Inside a card": both themes, untoned and each tone, against the painted card.*
- *The dark card's edge against the page.*

*Each was shown failing on the old values first: parchment, the old checkbox edge, the 14% tint.*

*Before/after sheets at 375px and 1440px, in both themes, against the AU.0 baseline, are in the session
scratchpad.*

*`npm run build` passes. The library is 354/354, the page contract holds, and the showcase failed only the 4
known order-dependent landing-credits specs.*

*Out of date now: the landing page's tour screenshots (`public/images/tour/*`, LP.5) show the green-black dark
theme. Re-run `capture:tour` in the sample workspace when convenient. The AU.1 delivery note's dark figures were
measured on the green-black theme. The specs hold the charcoal ones.*

## AU.2.3 Skeleton loader and the list shell's loading state - done

```text
SCOPE: Add CpSkeletonComponent to @creator-pantry/ui: shapes line, block, circle and card (a media-card
silhouette), a count, and widths that vary so a block of lines does not read as a table. The shimmer moves by
background-position (the "drawer" rule rejects translateX) and is static under prefers-reduced-motion. The
host is aria-hidden; the region that is loading keeps aria-busy and one polite status sentence for screen
readers. Give cp-list-shell a loadingLayout input ('cards' | 'rows', default 'rows') that draws skeletons in
the shape of what is coming, replacing its visible "Loading…" text with a visually hidden one. From AU.0: an
empty cp-list-shell says its empty sentence twice, once in the projected state and again as small text at the
foot (Pictures, Recipes, Brand library and Prompt library); say it once.
CONSTRAINT: APP-003; AU.1 skeleton tokens; .claude/rules/design-system.md extension rules 1-9; list-shell's
existing loading/error/empty/ready contract and its specs.
RESTRICTION: No feature adopts it in this prompt except through cp-list-shell's default. A screen reader still
hears that the region is loading, exactly once. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review after implementation.
BEHAVIOR: Define the selector, inputs, ARIA and reduced-motion behaviour; wait for approval; implement with
specs, add it to public-api.ts, the showcase, README.md, DESIGN-SYSTEM.md and the public-API list in CLAUDE.md
and .claude/rules/design-system.md; run npm run build and npm test from src/web.
```

*Delivery note (2026-10-25): shipped. `CpSkeletonComponent` (`cp-skeleton`) has these properties:*

- *Shapes `line`, `block`, `circle` and `card`. `card` matches cp-card's padding and edge, has a 4:3 picture and a
  title line, and lays a `count` out as a card grid.*
- *`count` is capped at 24, and NaN draws one.*
- *A block of lines cycles its widths and ends at 60%.*
- *The host is `aria-hidden`.*
- *It sweeps by `background-position`. Under reduced motion it is flat (checked in a real browser), and under
  forced colours it is outlined.*

*`cp-list-shell` gained `loadingLayout` (`rows` by default, or `cards`). While loading it draws the matching
skeleton.*

*What differs from the prompt, all from `design-review`:*

1. *The shell's status region is always in the page, outside the `aria-busy` body, and only its words change. A
   region inserted with its text, or held inside a busy subtree, may not be read. This has not been checked with
   a real screen reader.*
2. *`cp-toolbar`, a library part, printed its own visible "Loading…" on the five libraries, which was a second
   announcement. Its `loading` now sets `aria-busy` and says nothing. Every toolbar using it sits above a list
   shell that announces.*
3. *The footer is hidden only while the list is **empty**, not while it is loading or in error, as first built.
   Hiding it while loading left test history's Previous/Next, and the histories' "Load older versions", as
   invisible focus stops. Hiding it only while empty fixes the duplicate sentence without changing any feature.*
4. *The `skeleton` contract rule catches any class beginning `skeleton` or `shimmer`, any use of a
   `--cp-skeleton-*` token or the library's sweep, and any keyframe that animates `background-position`. `bone`
   is left out on purpose: food copy says "bone-in".*

*No feature file changed. The libraries still load as rows until their own prompts pass `loadingLayout="cards"`:
AU.4.2, AU.6.1 and AU.6.3 now say so. AU.7 now names what is left: about thirty feature status sentences, and the
"Loading…" label on "Load older versions".*

*`npm run build` passes, the library is 366/366, and the page contract holds. The showcase's failures were only
in the known order-dependent families, and My Week and the content board pass alone (227/227).*

## AU.2.4 Empty states with illustrations - done

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

*Delivery note (2026-10-25): shipped. The owner approved the drawn set from a preview grid of all ten drawings ×
both themes × no tone and the four tones.*

- *They live in a new public part, `CpIllustrationComponent` (`cp-illustration`), rather than inside
  `cp-empty-state`, because AU.2.6 reuses them on cards with no picture.*
- *Each drawing is inline SVG via `@switch`, so no `innerHTML` and no sanitizer. It is `currentColor` on a faint wash
  of itself, `aria-hidden`, sized by `--cp-illustration-size` (7rem), and drops the wash and fills under forced
  colours.*
- *`CP_ILLUSTRATION_NAMES` is the single list the type derives from.*

*`cp-empty-state` gained `illustration`, which wins over `icon`. **A judgement call:** the roomy first-use layout
applies only when an illustration is given — art at 7rem in `--cp-tone-text`, the title at `--cp-font-size-xl`,
the sentence at `--cp-font-size-md`. Read literally, the SCOPE says the first-use variant becomes roomier, but 26
feature files use `cp-empty-state` and "pages adopt it in their own prompts", so an undrawn page keeps its glyph and
its old proportions. `design-review` agreed. A no-results state stays compact, with the art at 4.5rem.*

*The `illustration` contract rule fails:*

- *a stylesheet that styles `.illustration`, `.illustrations`, `.empty-art`, `.empty-illustration` or `.spot-art`;*
- *a template under `src/app/features/` that draws an inline `<svg>`. The shell's nav icons sit outside that folder.*

*No feature changed. The specs assert that the SVG namespace survives `@switch`, and that a consumer's
`--cp-illustration-size` is honoured. On the real showcase, in both themes, the pink drawing takes the Image
Studio tone while its button stays evergreen.*

*`npm run build` passes. The library is 382/382, the showcase 6083/6083, and the page contract holds.*

## AU.2.5 Page head accent and record picture - done

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

*Delivery note (2026-10-25): shipped. The owner approved the look from screenshots of each tone, with and without a
picture, at 375px and 1440px in both themes.*

*One approved deviation: the icon is a `[cpPageHeadIcon]` projection slot, not an input "passed by name". The nav's
icons are shell chrome, and the library must not know the product's sections. So the head draws a 3rem tile of
`--cp-tone`, and the page projects the nav's own `cp-shell-nav-icon` into it. AU.5.1 now moves that component to
`src/app/shared/` before a feature projects it.*

*What shipped:*

- *The rule under the head is new tokens, `--cp-tone-rule` and `--cp-tone-rule-width`: a 2px line in the tool's
  colour on a toned page, and the 1px hairline on an untoned one, never evergreen. This alone changed live pages:
  the five toned tools now show their line.*
- *`[cpPageHeadMedia]` takes the page's own `<img>`, or one wrapper around it, in a 4:3 frame. A container query on
  the head puts it beside the title from 34rem and above it below that. The first try used 40rem, which was too
  wide for a detail page's column.*
- *The tile meets the title's first line, and is outlined under forced colours.*
- *The `page-head` contract rule owns the new class names and fails a feature that styles a `[cpPageHead…]` slot.*

*`design-review` raised these, now handled:*

- *Tile alignment, the forced-colours outline, the container's block-level caveat, the README's slot count, a
  wrapped-picture spec, alt-text guidance in the docs and in AU.4.3, and contract self-tests that list their names
  out.*

*Two of its suggestions were left as approved, for the owner to revisit if wanted:*

- *An untoned page's tile is evergreen, not neutral.*
- *A phone shows the picture at up to 24rem wide.*

*`npm run build` passes. The library is 387/387, the showcase 6083/6083, and the page contract holds.*

## AU.2.6 Media card without a picture - done

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

*Delivery note (2026-10-25): shipped. The owner approved the look from screenshots of a mixed grid. The grid had
real photos beside each kind of empty panel, untoned and on a pink page, at 1440px and 375px, in both themes.*

*What shipped:*

- *The panel is a faint gingham, drawn in CSS from the panel's own ink at 4%. A first try at 6% was loud on a tint.
  It sits on two new tokens, `--cp-tone-panel` and `--cp-tone-panel-ink`.*
  - *On a toned page they are the tool's tint and text colour.*
  - *On an untoned page they are `--cp-surface-subtle` and `--cp-text-muted`, not evergreen. This follows
    `--cp-tone-rule`'s reasoning. All four libraries are untoned today, so their panels are neutral.*
  - *The drawing is the kind's `cp-illustration` at 5rem.*
- *A typed `illustration` input wins over `glyph`. `glyph` maps `▣ ⌘ ❝ ◆` to `picture`, `recipe`, `prompt` and
  `brand`, and shows any other glyph as text. The libraries keep passing their glyphs, so they draw their
  illustrations with no feature change.*
- *The projected picture leans to 1.04 on the title's hover or `:focus`, clipped by its rounded frame. It uses
  `:focus` rather than `:focus-visible`, the same as the title's ring, for a title focused by script after "Load
  more". Under reduced motion it does not move.*
- *The library now owns the picture's 4:3 frame. Before, each consumer had to give its picture the panel's shape,
  so a card with a bare `<img>` would sit unevenly in a row. A bare `<img>` is now cropped to fit, and a component
  such as DAM's thumbnail keeps its own display.*
- *The page contract's `illustration` rule fails a feature that draws its own no-picture panel. That covers the
  class names `no-picture`, `picture-panel` and `picture-placeholder`, and any read of `var(--cp-tone-panel`.*

*`design-review` raised these, now handled:*

- *The 4:3 frame belonged to each consumer; the library now owns it.*
- *The glyph map was a prototype-reachable object; it is now a `Map`.*
- *The panel pair was missing from "Warmth inside the app"'s list of neutral exceptions.*
- *The `glyph` JSDoc was stale, and the showcase had a dead `.showcase-picture` style.*
- *Two showcase titles and one alt text claimed more than their photos show.*

*One finding was wrong:* the review said the print block lacked the panel tokens. They are there, beside
`--cp-tone-rule`.

*Testing found a bug in the prototype:* the zoom selector was scoped by emulated encapsulation and never reached a
projected picture. It now uses `.picture ::ng-deep > *`, and a spec asserts the transform.

*`npm run build` passes. The library is 403/403, the showcase 6084/6084, and the page contract holds. One run of
the full suite had an intermittent red in RecipeEditorComponent's section-nav scroll spec. That is a known family,
and it passed when the file was rerun alone, with and without this change.*

## AU.3.1 One treatment for generated content - done

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
OPEN (from AU.1's review, for the owner before this prompt starts): edging AI output in purple on every page
puts a second tone on the four other toned tools — Image Studio's results would be purple on a pink page — and
makes purple read as "made by AI", which DESIGN-SYSTEM.md "Warmth inside the app" says no tone does. The
recommendation is a neutral edge carrying the glyph and the words "Drafted by AI", taking no tone.
BEHAVIOR: Present the audit table (feature, current marker, swap now or later); wait for approval; implement
with specs and the contract rule, update showcase and docs; run npm run build and npm test from src/web.
```

*Delivery note (2026-10-25): shipped. The owner approved the audit table and resolved the OPEN question as
recommended: the edge is neutral, not purple.*

*What shipped:*

- *`CpGeneratedLabelComponent` (`cp-generated-label`) is the mark: an `aria-hidden` ✦ and words picked by `kind`.
  `words` is "Drafted by AI", `picture` "Made by AI", `reading` "Read by AI". There is no free-text label;
  `CP_GENERATED_LABELS` is the one place the words live.*
  - *An optional `provenance` takes the feature's own text. A screen reader hears a pause before it.*
  - *The edge is a dashed `--cp-border-control`, with no tone.*
- *`CpGeneratedComponent` (`cp-generated`) is the surface. It holds the label and provenance, the output, then
  `[cpGeneratedActions]` last. It is a `role="group"` named by the label's words, with a dashed neutral edge and
  no fill. No feature adopts it yet: every like-for-like swap was a label already sitting inside an edge.*
- *Nine markers became `cp-generated-label`, each shown under the same condition as before:*
  - *channel-post-review, long-form-document-review and long-form-seo-suggestions ("Written for you");*
  - *long-form-plan-review ("AI suggested"), which `design-review` found;*
  - *generated-image-run ("AI generated" plus "Made by …", now `kind="picture"` with the maker as
    provenance);*
  - *brand-style-test-drive ("Written by AI");*
  - *save-to-library alt text and prompt-detail ("Generated");*
  - *quick-recipe ("Generated" on the picture, now `kind="picture"`, and "Generated draft" on the recipe), which
    the new rule and ai-safety-reviewer found.*
- *The page contract's `generated` rule fails a template element whose whole text, in any case, is one of the old
  or new labels.*
- *DESIGN-SYSTEM.md has a "Generated content" section. It says the words claim origin, not quality: any caution is
  the feature's sentence beside the label, and a reading always comes with a "check it against the original".*

*The reviews:*

- *ai-safety-reviewer found no blocking issue.*
- *Both reviews raised these, now handled: the second quick-recipe badge, the rule's case-sensitivity and missing
  phrases, the group's name now pointing at the words alone, a spoken separator before provenance, and the
  origin-not-quality guidance.*
- *The reviews disagreed on the alt text's `kind`. It stays "Drafted by AI": the output is words the creator
  accepts or edits, and the sentence beside it already says they came from a reading of the picture.*

*Left for each page's own prompt (the AU.3.1 leftovers):*

- *features/ai (AU.5.1):*
  - *ai-proposal-panel: its own `.panel` headed "AI suggestions", with no label on the suggestions and a private
    `.provenance` list that the `facts` rule does not catch.*
  - *ai-advisory-results: no label at all, provenance in a `<details>`.*
  - *recipe-first-draft-review: the page is the draft but has no label; "How this was made" is a hand-made facts
    card.*
  - *recipe-concept-studio: the ideas carry no label.*
- *content-pipeline (AU.5.4):*
  - *content-pipeline-prompt-panel: the "These words were written for you…" notice, and the private
    `p.generated` box holding the model's prompt.*
  - *content-pipeline-reference-panel: the AI reading of a picture carries no mark. It takes `kind="reading"`
    with a check-it-against-the-picture sentence. Its "A generated picture" name may also need a look.*
- *generated-image-run (AU.5.2): the label shows only when the server named a provider. A run with none shows no
  "Made by AI" at all; this was true before.*
- *Brand and Prompt Library (AU.6.3):*
  - *Brand setup's origin pill says "Written for you" from a TypeScript vocabulary that the rule cannot see.*
  - *A prompt's source is labelled "Written for you" in a purple kind badge (`prompt-library.models.ts`).*
  - *prompt-detail's card heading "What was written for you" now sits above "Drafted by AI".*
- *Kept on purpose: a record's kind badge in purple (DAM's "AI generated", including the showcase's page-head
  example), the recipe-history "AI-assisted ✦" event, and the dashboard's "AI proposals to review". ✦ also appears
  in two shell empty states; reserving it for the mark is a later choice.*

*`npm run build` passes. The library is 412/412, the showcase 6085/6085, the page contract holds, and its self-tests are 25/25.*

## AU.3.2 Option card for AI choices - done

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

*Delivery note (2026-10-25): shipped. The owner approved the API and the boundary as proposed.*

*What shipped:*

- *`CpOptionCardComponent` (`cp-option-card`) has these parts:*
  - *`heading` at `headingLevel`, in an `article` named by it.*
  - *`facts` (`CpOptionFact[]`): the first three are term-and-value tags, and the rest move into the disclosure,
    never dropped.*
  - *A one-sentence `summary`.*
  - *A default slot that is always shown, including bare text.*
  - *`[cpOptionCardDetail]` behind a native `<details>`. It is omitted when there is nothing behind it, and its
    summary is named with the option, like the button.*
  - *One secondary button, named "<chooseLabel> <heading>", that emits `chosen`.*
- *`selected` shows a 2px `--cp-primary-text` edge (border plus outline, so it survives forced colours), a ✓ and
  "Chosen". The button then reads "Chosen" at full strength. It stays focusable with `aria-disabled` and emits
  nothing. It takes no tone, and it is not a form control.*
- *DESIGN-SYSTEM.md's "Choosing between options" sets the boundary. A value a Save submits is `cp-choice-group`; an
  action that happens now is `cp-option-card`. The choice-group paragraph points back to it.*
- *The page contract's `option-card` rule owns `.option-card`, `.option-cards`, `.variant-card`, `.concept-card`,
  `.concept-title` and `.concept-dietary`. `recipe-concept-studio.component.css` is exempt until AU.5.1 adopts
  the card. The exemption covers that whole file for this rule.*
- *global.css: `.cp-disclosure > summary` is `list-item` again, with its 40px from padding. As `flex` it had dropped
  the browser's marker, so every `.cp-disclosure` in the app (13 templates) read as a label with no sign it
  opens. This contradicted the rule's own comment. No spec guards it: neither karma target loads global.css.*

*`design-review` raised these, now handled:*

- *Bare text in the always-shown slot was hidden; it is now `:empty`, with a spec.*
- *cpButton faded the aria-disabled "Chosen" and its focus ring to 52%; the card overrides it, with a spec.*
- *The disclosures had identical names.*
- *The edge token, wrapping of long fact text, and a stale comment.*
- *Four generic class names were dropped from the rule.*

*One suggestion was not taken:* setting the name in Fredoka. "Content hierarchy" sets item titles in sans semibold,
and an option in a grid is an item, as a media card's title is.

*`npm run build` passes. The library is 427/427 and the page contract holds (26 self-tests).
The showcase is 6085/6086. The one red is RecipeEditorComponent's section-nav scroll spec, the same intermittent
one as in AU.2.6, and it passed on a rerun of that file with the showcase spec.*

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

## AU.3.4 Entrance motion (only if D4 is yes) - done

```text
SCOPE: Add a library directive (cpEnter) that fades and lifts an element in by a few pixels the first time it
renders, with an optional stagger for list items, using the AU.1 duration. Apply it in the library where it is
always right: a cp-list-shell's ready content, a cp-generated result arriving, a cp-option-card group. Pages do
not apply it themselves in this document. Use AU.1's --cp-duration-enter, --cp-enter-distance and
--cp-enter-stagger. global.css's reduced-motion rule zeroes durations but not delays, so cpEnter sets
animation-delay: 0 under reduced motion, or a staggered item sits invisible for its turn; spec it.
CONSTRAINT: APP-010; D4; DESIGN-SYSTEM.md "Accessibility baseline" (reduced motion); LandingRevealDirective as
the reference for print and reduced-motion handling, not for scroll triggering.
RESTRICTION: Nothing moves under prefers-reduced-motion, nothing is hidden when script fails or under print,
focus never lands on an element that is still invisible, and nothing re-animates on change detection. No
scroll-triggered motion. Do not run backend tests.
UTILIZATION: creatorpantry-design-system skill; design-review.
BEHAVIOR: Show it with motion on and off; wait for approval; implement with specs, update the showcase and
docs; run npm run build and npm test from src/web.
```

*Delivery note (2026-10-25): shipped. The owner approved the contract as proposed, after seeing it with motion
on, with motion reduced, and with the stagger delay left in.*

*What shipped:*

- *`CpEnterDirective` (`[cpEnter]`), in `lib/components/enter.directive.ts`. It marks the element
  `data-cp-enter` and `global.css` holds the `cp-enter` keyframes keyed on that attribute, because a directive
  carries no styles. The marking is the only thing that hides anything, so an absent sheet, an absent script and
  a finished entrance all leave content that was never hidden — `LandingRevealDirective`'s direction, without
  its observer.*
- *The stagger is the arrival, not an index a page passes: everything created in one change-detection pass
  takes the next place, in creation order, capped at five steps. That is what reaches a `cp-option-card`, which
  sits inside its own `<li>` and so has no attributed DOM sibling for CSS or `previousElementSibling` to count.*
- *`global.css` gains four lines: the keyframes, `[data-cp-enter]`'s `animation … backwards`,
  `animation-delay: 0s !important` under `prefers-reduced-motion` (the prompt's point: the existing clamp zeroes
  durations and would leave a staggered item invisible for its turn), and `animation: none !important` under
  `print`, which replaces LandingReveal's `beforeprint` listener — paper renders the whole document at once and
  `backwards` would print the from-state.*
- *Three application sites. `cp-list-shell`'s ready `.scroll` is bound `[cpEnter]="!hasBeenReady()"`, so it
  arrives on its first ready and not when a filter reloads the same list; `cp-generated` and `cp-option-card`
  take it as a `hostDirectives`.*
- *The latch is an `afterRenderEffect`, not an `effect`. A component effect runs before its own view is
  refreshed, so latching there meant the first ready render already read `true` and nothing ever arrived — the
  spec caught it.*
- *Four restrictions, four mechanisms: reduced motion by the two CSS rules; no hiding without script or on paper
  by the direction above; focus by `focusin` ending the entrance in the same frame, before paint, which matters
  because `.scroll` is `tabindex="0"`; and no re-animation because a CSS animation declares once, the input is
  read in `ngOnInit` and never again, and the attribute comes off at `animationend` so a later DOM move cannot
  restart it.*
- *The page contract's `enter` rule owns the three entrance tokens, the `cp-enter` keyframe name and
  `data-cp-enter`, in a stylesheet, a template or code. It does not forbid `cpEnter` itself in a feature
  template, because a later document may let pages apply it and a check is never loosened. It also does not
  claim every fade-and-rise: `recipe-editor.component.css`'s `cp-panel-in` (a tab switch) and
  `landing-hero-collage.component.css`'s `settle` (the public hero) are motion about something else and are
  left alone — flagged here rather than exempted.*
- *`angular.json`: the library's karma target now loads `global.css` beside the tokens, which closes the gap
  AU.3.2 noted. The reduced-motion and print rules are read from the parsed stylesheet, because Chrome cannot
  be told to prefer reduced motion from a spec; what a marked element draws is measured.*

*`design-review` raised these, now handled:*

- *Focus settled a change detection late. A signal written in a `focusin` handler reaches the DOM on the next
  pass, so the creator could have met a focus ring on something still transparent — exactly what the prompt
  forbids. `global.css` now also carries `[data-cp-enter]:focus-within { animation: none }`, which a style
  recalculation applies synchronously, and the directive's handler stays as the cleanup that takes the mark off
  for good. A spec calls a real `.focus()` and reads the computed animation with no change detection between.*
- *An entrance could replay. Hiding an element mid-arrival — a `[hidden]` tab panel that stays mounted, a closed
  `<details>` — cancels the animation without ending it, and the mark left behind would run it again when the
  element was shown. `animationcancel` now settles it too, with the same target and name filter.*
- *The docs and the rule's own text claimed more than the rule checks. `ENTRANCE` catches the three tokens, the
  `cp-enter` keyframes and `data-cp-enter`; a feature's own `@keyframes rise` passes, deliberately. All four
  statements now say that, and say why `[cpEnter]` in a feature template is allowed: a later document can hand
  pages the entrance without a check being loosened.*
- *The stagger promise did not match what shipped. "List items `--cp-enter-stagger` apart" is AU.1's wording, and
  `cp-list-shell` marks one container, so a library's rows rise as one block. DESIGN-SYSTEM.md now says a group
  of options staggers and a list's rows do not, and records that the list shell is the only one of the three on a
  page today — `cp-generated` and `cp-option-card` are placed from AU.4.1 onwards.*
- *Two smaller ones: the list shell's latch is per shell instance, not per content, which is now written down;
  and the "fades and lifts" spec asserted only the duration and the fill, so it now reads the `@keyframes` block
  and asserts the `from` opacity and translate.*

*Two were not taken.* The reviewer read NG0956 in the showcase run as this section's `@for`; the warnings are for
a collection of size 2, this one holds one run, and the showcase spec alone logs none — they are someone else's
and were there before. The replay is a two-pass destroy-and-build anyway, which is honest about re-creating the
view. And `CP_ENTER_ANIMATION` and `CP_ENTER_MAX_STEPS` stay exported, as `CP_GENERATED_LABELS` and
`CP_OPTION_CARD_TAG_LIMIT` are.

*`npm run build` passes. The page contract holds (28 self-tests, 32 routed pages, 627 files, 11 exemptions, none
added) and the safety-copy check passes. The library is 465/465. The showcase was 6060/6060 on one full run and
6058/6060 on two others, the two reds being `RecipeEditorComponent`'s section-nav scroll specs — the same
order-dependent family as in AU.2.6 and AU.3.2, passing 229/229 on their own. That file uses none of the three
parts this prompt touched.*

## AU.4.1 Dashboard - done

```text
SCOPE: Rework the dashboard with the new parts: the head with its lede, a row of figure tiles from the read
model's existing counts, the most urgent topic as the one featured card, the remaining topic cards in
ul[cpCardGrid], skeletons while it loads (replacing "Loading your dashboard…"), an illustrated first-use state
in "Get started", and an illustrated quiet state in any topic with nothing in it. From AU.0: "Carry on where
you left off" lists three runs each linked as "Create a recipe"; name each by the thing it is about, so
neither the eye nor a screen reader meets the same link three times.
CONSTRAINT: APP-003, APP-004, APP-008; frontend.md's dashboard rule (DashboardComponent is the reference: most
urgent first, counts from the read model, an unreadable topic keeps its card with a notice, each item links to
where it is dealt with); dashboard.models.ts.
RESTRICTION: No new counts and no new endpoint. If a figure tile would need a number the read model does not
send, leave it out and list it. Do not run backend tests.
UTILIZATION: design-review; test-gap-analyzer on the states; playwright-cli screenshots before/after.
BEHAVIOR: Show the layout with the AU.0 capture beside it; wait for approval; implement with specs for every
state (loading, empty, degraded, error, success); run npm run build and npm test from src/web.
```

*Delivery note (2026-10-25): shipped. The owner approved the layout from a written sketch — the AU.0 captures
are gone (`.captures/` is ignored and that session's pictures went with it), and the sample workspace
`homespun-favorites` no longer exists on this machine, so the before/after pair was shot fresh in the owner's
own `test` workspace. Four decisions were put and all four taken as recommended.*

*What shipped:*

- *A row of four `cp-stat-tile`s, the "what needs you" set: `workPlan.overdueCount`,
  `publishing.needsAttentionCount`, `testFollowUps.open` and `aiProposals.awaitingDecision`. Each prints one
  field as it came; `context` lists further fields the server sent (today's count, the blocking/major/minor
  split). Only the late-tasks tile has a link, because only My Day is one place to deal with a figure; the
  other three are dealt with item by item in the card below. The row is not drawn at all in a first use.*
- *Left out for want of a number the read model does not send, as the restriction asks: **recipes in progress
  in all** (a count per status, no total — summing is arithmetic), **blocked recipes** (a list, with no count
  of its own) and **the size of the picture library** (the recent few, with no total). Available and not
  chosen, because they stay in their own cards: the workflow counts, the week's six figures, `unplannedCount`,
  `upcoming.cardCount`/`postCount` and `board.total`.*
- *One featured card above the grid, chosen by the first of the server's counts above zero down the page's own
  order of urgency — late, failed to publish, blocked, blocking a test, stalled, waiting on a decision, and
  last what the creator was in the middle of. Nothing is derived: each rung reads one count. A workspace where
  nothing needs anyone features nothing; one with nothing in it features "Get started".*
- *Each topic is an `ng-template` the page points at from the featured slot or the grid, so one topic is one
  card and never two. The grid's order is `DASHBOARD_TOPIC_KEYS`, not the order a view query happens to answer
  in.*
- *Loading draws the layout that is coming — four tile silhouettes, a wide featured card, six topics — with
  one offscreen `role="status"` outside the `aria-busy` region. "Loading your dashboard…" is gone from the
  page.*
- *Drawn quiet states where empty means "nothing here yet" (Carry on, Your day, Recipes in progress, Content
  board, Recently added pictures), and the plain reassuring sentence kept where it means "all clear"
  (Workflows that need a look, Publishing, blocked recipes, test follow-ups, AI proposals). The owner chose
  this over drawing every empty topic: on a healthy dashboard the literal reading is eleven drawings at once.*
- *"Get started" is the featured card, holding `cp-empty-state`'s roomy first-use layout with the `recipe`
  drawing.*
- *AU.0's three runs named "Create a recipe": confirmed at the source — `WorkflowRun.Title` falls back to
  `definition.Goal` (`IWorkflowRunBusiness.cs:126`). The read model carries no linked record, so the recipe a
  run is about cannot be named without a field the server does not send. A run whose title is its goal is now
  named by the step it is on ("Create a recipe — Check your method"), and every run link's accessible name
  carries the day it was last touched, so two unnamed runs on one step still differ. "Carry on with" where the
  step is the reader's, "Open" where it is somebody else's, which is what `YouCanDoIt` is for.*

*One library change, at the owner's request mid-prompt:* **`cpCardGrid` gained `flow="masonry"`**. *A grid's
rows are as tall as their tallest card, which left every short topic standing in a gap — visible in the before
capture and worse once the featured card was lifted out. Masonry lays the same list out in columns that pack.
The DOM order is unchanged, so the Tab key and a screen reader still read the order of urgency; the eye reads
down a column before across, which is what masonry means and is written down in DESIGN-SYSTEM.md. The topic
grid uses it; the figure row stays a grid, because a row of figures should line up. It took the page 870px
shorter. This replaced the alternative — a library fix to make `cp-topic-card` fill its cell — which
`design-review` had confirmed as the right shape for the ragged rows and which is now not needed.*

*`design-review` raised ten findings; four were taken:*

- *A retry's accessible name did not contain its visible "Try again" (SC 2.5.3). Now "Try again to read the
  board", "Try again to load your dashboard".*
- *A figure's notice and its topic card's notice announced the same unreadable part twice. The tile's notice
  lost its `role`; the card still announces.*
- *Focus was dropped when the featured topic moved. A retry that succeeds is exactly what moves a card, so the
  creator who pressed it would have been left on the document body; focus now goes to that topic's heading,
  and only where the read actually dropped it.*
- *The grid's order now comes from the declared key list rather than from query timing.*

*Not taken, with reasons: the degraded tile keeps its label, which is what DESIGN-SYSTEM.md's "Meters and
figures" prescribes; the zone tile does not repeat the Brand settings link its topic card already carries; the
other three figures stay linkless for want of one place to send anyone; and the loading state always draws the
featured silhouette, which is right for all but a workspace where nothing needs anyone.*

*`test-gap-analyzer` found a real defect, pre-existing and made worse by this prompt.* **`firstUse` judged by
five parts of ten**, *so a workspace whose recipes were all approved — none in progress, nothing on the board,
nothing uploaded — greeted a creator with a year's work behind them as though they had just arrived, and after
this prompt it also hid their four figures. It now requires every part to agree, with one documented
exception: the plan and what is coming up are unavailable until the workspace has a time zone, and that is no
evidence either way only while no zone is set. The spec's own `emptyWorkspace()` fixture had the same hole and
asserted the contradiction; it is now genuinely empty, and six specs prove each part alone denies a first use.*

*Also from that analysis: a count above zero with an empty list no longer reads "Nothing under way" or "No
recipes in progress" (it says none has changed recently); `runName` treats a blank step title as no step
rather than ending on a dash; the urgency table gained its missing AI-proposals rung and now pins the whole
order rather than its first heading; and the page gained the two-workspace test tenancy.md requires, which it
did not have.*

*Left for later, from the same analysis: out-of-order responses between a refresh and the quick-capture re-read
(no request sequence number), a refresh from ready to not_found, an unknown time-zone id falling back to UTC
silently, and malformed dates rendering as an empty string. None is new to this prompt.*

*`npm run build` passes. `npm test` is green in full: the page contract holds (32 routed pages, 628 files, 11
exemptions, none added), safety copy passes, the library is 469/469 (+4 masonry specs) and the showcase
6097/6097 (+21: nine on the dashboard, twelve in a new `dashboard-presentation.spec.ts`). No intermittents in
this session's runs.*

## AU.4.2 Recipe library shows each recipe's picture

```text
SCOPE: Add an optional heroAsset to the recipe list's ServiceModel (the media asset id and version of the
recipe's Hero asset link, or null) through the full seam, projected in the repository's list query rather than
loaded per row. In the web, add the field to RecipeSummary and its decoder, and project the picture into each
cp-media-card through the authorised thumbnail loader the DAM already uses (promote it to src/app/shared if it
lives in features/dam). Give the library cp-list-shell's 'cards' skeleton and AU.2.4's "recipe" first-use and
"search" no-results states. From AU.0: the status filter chips read "InDevelopment" and "ReadyForReview",
the wire names; label them in words. That is a label correction, not a copy rewrite. A card also stacks a
"Draft" pill above a "v4 Draft" badge, which says Draft twice.
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

## AU.4.3 Recipe detail as the editorial face of the app - done

```text
SCOPE: Give the recipe detail page (and the kitchen view's on-screen head, not its print layout) the AU.2.5
head with the recipe's Hero picture in the media slot, loaded from the asset link the detail response already
carries; times and yield as a dl[cpFacts] inline strip under the head; ingredients and method side by side at
wide widths and stacked at narrow; skeletons while loading. The recipe's own words are shown exactly as stored.
From AU.0: the editor stacks three navigation tiers before its first field (Edit/Media/History/…, then
General/Ingredients/Instructions, then the section nav), which wraps to three rows at 375px. Propose how the
head and the tiers combine. frontend.md's per-form dirty-state and field-error-selects-its-tab rules stand.
From AU.2.5: the Hero goes in [cpPageHeadMedia] as the page's own <img>; its alt is the asset's stored alt text
(media.md: never invented from the filename or the recipe), or alt="" only if the title says all it shows. The
recipes icon goes in [cpPageHeadIcon].
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

*Delivery note (2026-10-26): shipped. The "recipe detail page" is the editor at `/recipes/:id`; there is no other.
The owner approved the layout as a written sketch and settled three questions:*

- *The editor's three form tabs stay. Merging Ingredients and Instructions into one side-by-side tab was offered
  and declined, so side by side stays where it already was, in the kitchen view. The AU.0 navigation tiers at
  375px are unchanged.*
- *With no stored alt text, the picture's alt is the libraries' own sentence ("Picture titled “…”. No
  description has been written for it."), not `alt=""`.*
- *The picture and the icon tile come off paper by a print rule in `cp-page-head`, not by widening the kitchen
  view's `::ng-deep` print rule. The kitchen stylesheet's print block and all four of its exemptions are
  untouched.*

*What shipped:*

- *Both pages: the recipes nav icon in `[cpPageHeadIcon]`, in every state. Also, the Hero picture in
  `[cpPageHeadMedia]` as the page's own `<img>`: the first `Hero` link by order, the asset read for its title
  and alt text, then its `web` copy (or the pinned version's) as an object URL that is revoked when it changes
  or the page goes (`RecipeHeroPicture`, `features/recipes/recipe-head.ts`). A skeleton holds the slot while
  it loads. A gone or unreadable picture leaves the slot empty.*
- *The editor: a `dl[cpFacts]` strip under the head, filled from `recipeFacts()`, which the kitchen view now
  shares. It shows Makes (the yield text verbatim), Serves (its own fact), Prep, Cook, Rest and the stored
  Total. It is as saved: it moves when a saved recipe arrives (a save, a restore, an apply, a media or status
  change), never on a keystroke.*
- *Skeletons on both pages while the recipe loads, and one visually hidden status sentence that is always in
  the page.*
- *Library: `dl[cpFacts]` has a third layout, `across` (facts side by side, a small term over a bold value,
  wrapping). Neither `stacked` nor `inline` made a strip, and decision 4 puts the look in the library. Also,
  `cp-page-head` leaves the picture and the tile off paper and gives the title back the full width. Docs,
  showcase and the design-system rule are updated.*
- *`shell-nav-icon.component.ts` moved to `src/app/shared/nav-icon/` (AU.5.1 had it; this prompt projected it
  first). The nav's own rendering is unchanged.*

*Print proof: the kitchen view under print media was rendered for Matzo Ball Soup (which has a Hero) as
shipped, and again with the nodes this prompt adds taken out of the DOM, leaving the markup it had before. The
two were pixel-identical. On paper the picture and the tile are `display: none` and the masthead is one column.*

*`design-review` raised these:*

- *Fixed: the loading status line was a live region created already holding its text. It is now always in the
  page.*
- *Fixed: the strip's comment and DESIGN-SYSTEM.md disagreed about when it moves.*
- *Fixed: a re-export left behind for one spec.*
- *Left for the owner, as AU.2.5 recorded: at 375px the picture is 341×255px above the title, and the head of a
  recipe with one ends about 250px lower than one without.*
- *Left for the owner: "Makes" over a yield that is itself a serving sentence ("Serves 6"). The kitchen view
  already showed it so.*
- *Left for the owner: the kitchen view keeps its exempt `.facts`, so the same figures have two
  implementations until its print is reworked.*
- *Passed to AU.5.1: the first-draft review's across-reading values and `across`.*

*`npm run build` passes. The library is 471/471 and the page contract holds. The showcase was 6168/6168
before the review fixes. After them, one run had 10 reds, all in the known intermittent families (landing
credits, pricing and collage, and the editor's step-layout spec), and the editor and landing specs then
passed alone (415/415). Two `CpMediaCardComponent` focus specs failed once in an earlier run and passed alone.*

## AU.5.1 AI Recipe Studio - done

```text
SCOPE: Present the studio's concepts as cp-option-card in a comparison grid: the dish name as the title, time
budget, skill fit and dietary notes as the key facts, the assumptions behind the disclosure, "Choose this
concept" as the card's one action. Wrap generated results in cp-generated. Show "What the model flagged" as a
cp-notice in its own place, not a card of prose. Give every step a skeleton and an illustrated empty state.
The route wears the purple tone and its page head the studio's icon. From AU.0: the template joins lists of
whole sentences with ", " (suggestedIngredients, dietaryNotes, assumptions in
recipe-concept-studio.component.html), which prints "components., Dietary goals". Show a list of sentences
as a list, and keep comma-joining for lists of words. From AU.2.5: the page head's icon is a [cpPageHeadIcon]
slot that takes the nav's own cp-shell-nav-icon, which AU.4.3 moved to src/app/shared/nav-icon/ so features do
not depend on the shell; import it from there, as AU.5.2–AU.5.4 do with their own icons. From AU.4.3:
dl[cpFacts] now has an `across` layout. Settle whether recipe-first-draft-review's .draft-fields-grid (short
values, each with its own edit control) moves to it, and drop its card-grid exemption if it does.
CONSTRAINT: APP-006, APP-007; ai.md (proposals, not applied); recipes.md "Safety and claims" (dietary notes
stay labelled descriptive, never a guarantee); the AU.3.1 audit's leftovers for features/ai.
RESTRICTION: No change to prompts, generation, what is saved or the order of steps. The "descriptive only, not
a guarantee" wording stays with every dietary note. Do not run backend tests.
UTILIZATION: design-review; ai-safety-reviewer on the dietary and assumption presentation.
BEHAVIOR: Show the concept grid against the tour capture; wait for approval; implement with specs; run npm run
build and npm test from src/web.
```

*Delivery note (2026-10-26): shipped. The owner approved the concept grid from a written sketch against the tour
capture, and chose to keep dietary notes out of the tags (option A).*

*The concept studio:*

- *Each concept is a `cp-option-card` in `ul[cpCardGrid]`, inside `cp-generated` in the list shell's ready content.*
  - *Time budget and skill fit are the tags.*
  - *Dietary notes are always shown, as a `dl[cpFacts]`. Its term is "Dietary notes — descriptive only, not a
    guarantee", so the caveat is the page's own words, and the model's sentences are a list under it. A tag holds one
    string, so a list of sentences there would have needed a library change.*
  - *A concept's own warnings are `cp-notice`s, always shown.*
  - *The rationale, the suggested ingredients (still comma-joined: they are words) and the assumptions (now a list:
    they are sentences) are behind the disclosure. Its summary reads "…and what it assumes" when there are any.*
- *"What the model flagged" is a section with an `h3` and one `cp-notice` per flag, after the list shell. It shows
  even when no concepts came back, since a flag is often why.*
- *"Try a new brief" is the surface's `[cpGeneratedActions]`.*
- *Provenance is "model (provider)". It is left out in a guided step, as 13A.19 does for pictures.*
- *Page head: `cp-shell-nav-icon name="ai"` from `shared/nav-icon/`. The purple tone was already on the route.*
- *Loading sentences became `cp-skeleton` with one always-present offscreen status line, which also says "Asking
  for concepts…" while a brief is sent.*
- *Empty states are illustrated, and a first-use state ("Your concepts will appear here") stands where the concepts
  will land.*
- *The option-card exemption is gone.*

*The first-draft review:*

- *The page head icon, a skeleton, and illustrated idle and gone states.*
- *A `cp-generated-label` before the anchor nav.*
- *"How this was made" is `dl[cpFacts] layout="across"`.*
- ***Settled:** yield and timing keep `.draft-fields-grid` and its exemption. Each value opens an editor in place,
  and `across` sizes a fact to its content and wraps, so an opening editor would reflow its neighbours. The reason in
  the script and in DESIGN-SYSTEM.md now says so.*

*The rest of the AU.3.1 leftovers for features/ai:*

- *`ai-proposal-panel`:*
  - *`cp-generated-label` in its header.*
  - *Provenance as `dl[cpFacts]`, still in its disclosure.*
  - *A skeleton under an always-present status line.*
  - *An illustrated gone state.*
- *`ai-advisory-results`: `cp-generated` round the items, and provenance as `dl[cpFacts]`.*

*The reviews:*

- *ai-safety-reviewer found nothing blocking. Taken: the disclosure names the assumptions, and a spec pins the caveat
  outside the model's sentences.*
- *Left for the owner:*
  - *A concept with no dietary notes says nothing about diet. It did before.*
  - *The panel and the advisory list name the model in a guided step too. They have no host to ask.*
- *design-review:*
  - *Fixed: the panel's loading line was a live region created already holding its text (blocking).*
  - *Fixed: "submitting" was not announced.*
  - *Fixed: a leftover class.*
- *Left for the owner:*
  - *`cp-generated` round a grid of option cards, and round the advisory list's bordered items. This prompt asks for
    the surface, but "Generated content" says a box round a card is a box in a box and takes the label alone. The
    swap is one line in each template if the label alone is preferred.*
  - *`cp-empty-state` always prints an `h3`, which skips a level under the draft review's `h1`. It needs a heading
    level input in the library.*

*`npm run build` passes. The page contract holds (28 self-tests), safety copy holds, the library is 471/471 and the
showcase is 6191/6191.*

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
empty column. A board has no featured card. From AU.0: a picture's page is 550px wide at a 375px viewport,
because its versions table (610px) is not in a scroll region of its own; the page must not scroll sideways
at any width. The library's search placeholder is clipped to "Search by title or des".
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
SCOPE: Each page adopts the parts it is missing: skeletons in place of its loading sentences (Brand library and
Prompt Library are grids of cp-media-card, so their cp-list-shell takes loadingLayout="cards"), an illustrated
empty state from the set ("brand", "prompt", "board", "recipe"), cp-generated on any generated text (brand
style test-drive, generated prompts), and Quick Recipe's first-use state. From AU.0: Workflows lists four
"Continue" buttons and three runs titled "Create a recipe"; give each action an accessible name that says
which run it continues, and tell same-kind runs apart by what they are about.
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
exemption is a listed path with a reason. From AU.0: while a detail page loads, its h1 reads "Asset",
"Loading card…", "Workflow", "Kitchen view" or "Edit recipe". The title slot holds the route's title or a
skeleton line, never a loading sentence. From AU.2.3: cp-toolbar's own "Loading…" is gone (it is aria-busy now)
and cp-list-shell's is offscreen, so what remains is in src/app: about thirty feature status sentences (e.g.
dashboard.component.html, recipe-editor.component.html, content-pipeline-shell.component.html) and the
"Loading…" label on "Load older versions" in recipe-history and brand-guide-history, which is not the
"Loading more…" the exemption allows. Keep one status region per loading area, present before it loads.
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
rubric. Run it with CAPTURE_WORKSPACE=homespun-favorites and
CAPTURE_EMPTY_WORKSPACE=capture-empty-before-2026-10-25-10-27, plus CAPTURE_URL_BRAND_GUIDE and a saved prompt
in the sample workspace, so the four pages AU.0 could not reach have a before of their own (capture them in
the old state first, or say they have no before). (6) Walk the app with playwright-cli: both themes, 375/768/1440 px, 200% zoom, keyboard only, reduced
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
