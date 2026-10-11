# CreatorPantry — UI Cohesion SCRUB Prompts

Eight prompts that make every signed-in page share one frame and one set of parts, for use with Claude Code
and the CreatorPantry `.claude/` toolkit.

This document supplements `docs/prompts/creatorpantry-scrub-microprompts.md`. It is scoped to the Angular
workspace at `src/web/` and does not redefine anything already decided in `CLAUDE.md` or `.claude/rules/`.
It changes no API, no ServiceModel and no backend code.

## Reusable SCRUB skeleton

```text
SCOPE:        one observable change and the exact repository seam it touches
CONSTRAINT:   stack, rule files, skills, and prerequisites
RESTRICTION:  explicit exclusions and invariants that must not be weakened
UTILIZATION:  skills, read-only reviewers, and tools to invoke
BEHAVIOR:     inspect, plan, wait for approval, implement, test, and report
```

## The direction

**DAM is the guiding lane.** `src/web/src/app/features/dam/` is the page the rest of the product is brought
into line with, sitting inside a gutter the shell owns. Two shapes come out of it:

- **The library** (`dam-library.component.html`): a quiet page head — display title, one muted sentence, the
  primary action — then a toolbar, then a responsive grid of picture-led cards (image, title, description,
  badges, a short facts list), then a result count and "Load more".
- **The detail page** (`dam-asset-detail.component.html`): a breadcrumb, the title with its badges, one fixed
  place for outcome and problem messages, then a stack of single-topic cards — each a display heading, a
  prose-width body and its actions at the bottom. Editing opens a focused dialog; the page itself is for
  reading.

## How this list was built

A read of every routed page's template and stylesheet, measured rather than eyeballed. The palette is not the
problem: there are no literal colours in feature CSS. The drift is structural.

- **No page gutter exists.** The shell's `<main>` has no padding (`app-shell.component.css:136`), so each
  routed page decides for itself. Twelve pad their own `:host` with `--cp-space-8` (the recipe library, editor
  and kitchen view, the concept studio, first-draft review, the Brand library and source detail, both DAM
  pages, both Prompt Library pages, and the placeholder). About ten do not, and their content runs to the
  sidebar and the window edge: Image Studio, the content pipeline, the Workflows hub, Brand settings, Brand
  setup, the Brand guide editor and history, the style test drive, Workspace settings, Themed days and the AI
  allowance page.
- **The title and the content do not share an edge.** The shell's page title is padded `--cp-space-5`
  (`app-shell.component.css:141`), 12px left of the content on the pages that pad themselves.
- **Three pages cap their own width, each differently:** `90rem` (recipe editor, concept studio) and `84rem`
  (kitchen view). Everything else stretches to the window.
- **There is no page head.** The shell prints an `<h1>` from route data; DAM, Prompt Library, Image Studio and
  others print a second one. Titles are set at three different sizes, 22 stylesheets declare their own header
  or title rules, and "Back to…" has twelve wordings.
- **The same parts are redrawn privately.** `.row` is declared in 27 feature stylesheets, `.title` in 19,
  `.facts` in 19, `.notice` in 17, `.back` in 16, `.actions` in 14, `.muted` in 13, `.search-field` in 12 —
  several of them beside a library equivalent that already exists.
- **The other three libraries are text rows.** Recipes, Prompt Library and Brand library list single-column
  cards with no picture, where DAM shows a grid.

In total 94 feature stylesheets carry about 10,400 lines, against 166 lines of tokens, themes and global
styles. This was read from the code, not from the running app, so UI.1 opens by capturing screenshots.

## Decisions already taken

Change these here before running the prompts if they are wrong; the prompts assume them.

- **The recipe editor stays an editor.** DAM's "read first, edit in a dialog" does not fit a form with dozens
  of fields that one Save writes. The editor takes the shared page head and frame in UI.6 and keeps its tabs,
  dirty state and validation exactly as they are. A DAM-style read page for a recipe is new feature work and
  is not in this document.
- **A drawer is added, for one job.** A panel that slides in from the right holds a single task the creator
  does while the page stays in view: a small edit to the record on screen, a preview from a library, a
  recipe tool. It is not a way to tidy a busy page, and it never holds a form that the page's own Save
  writes, a confirmation, navigation, or a multi-step flow. UI.3 builds it and writes that rule down.
- **The drawer is modal.** The page behind stays visible and readable but is inert while the drawer is open,
  so nothing can change underneath a task in progress. Under a narrow width it is a full-screen sheet.
- **Recipe tools move into the drawer, in a prompt of their own.** UI.7 takes the Tools area — seven panels
  nested inside the editor's tabs — out of the tab set and opens each tool in a drawer beside the recipe. It
  is the one prompt here that changes how a page is navigated, not only how it looks, so it stands alone
  and can be skipped: UI.8 does not depend on it.
- **No new previews.** The Prompt Library's existing preview moves from a dialog to the drawer. The other
  libraries keep opening a card's detail page; a peek for them is a new feature and is not built here.
- **A card with no picture gets a tinted panel and a glyph** in the image's place, so a grid of prompts or
  brand examples keeps even rows.
- **Grid only.** No grid/rows toggle is added.
- **One title size everywhere,** chosen in UI.2 and stated in `DESIGN-SYSTEM.md`.
- **Unbuilt sections are untouched.** Dashboard, My Day, My Week, Social Studio and Content Board stay
  placeholders; they simply inherit the frame.

## Keeping the design system current

Every prompt here changes what the design system is, so every prompt updates it in the same change. A part
that ships without its documentation is not finished, and a page migrated onto a part the docs do not
describe will be redrawn privately by the next person who needs it. Each prompt names its own updates; the
places they land are always the same:

| What changed | Where it is recorded |
| --- | --- |
| A token | `tokens.css` or `themes.css` (both themes), and the Layers section of `src/web/DESIGN-SYSTEM.md` |
| A library part | Its spec, `public-api.ts`, the showcase at `src/web/src/app/design-system-showcase/`, `src/web/README.md`, and a paragraph in `DESIGN-SYSTEM.md` saying when to use it and when not to |
| The list of exports | "Implemented public API" in `.claude/rules/design-system.md`, and the export list in the root `CLAUDE.md` |
| A rule a page must follow | `DESIGN-SYSTEM.md` for the reasoning, `.claude/rules/frontend.md` for the one-line rule |
| A part that replaces a documented reference | Every sentence in the docs that names the old reference |

## Application-level adherence

The design system today describes parts and forms. It does not describe a *page*, which is why 94
stylesheets each answered that question alone. This pass adds the missing layer, in three places that
reinforce each other:

1. **A written page contract.** A new "Composing a page" section in `src/web/DESIGN-SYSTEM.md` states the
   frame and the three page shapes — library, detail, flow — with DAM as the named reference for the first
   two. UI.1 opens the section; UI.2 through UI.6 each add what they establish.
2. **Rules loaded every session.** `.claude/rules/frontend.md` gains the short form: a routed page is one of
   the three shapes, sits in the shell's frame, and draws no header, gutter, grid or facts list of its own.
   The `new-component` skill and the `design-review` agent are updated to ask which shape a page is before
   anything else. This is what makes the next feature arrive already conforming.
3. **Checks that fail the build.** UI.1 adds the first and UI.8 the rest, so the contract does not depend on
   anyone remembering it.

No separate governing document is added. A fourth source of truth beside `DESIGN-SYSTEM.md`, the rules and
the showcase would be one more thing to drift.

## Prerequisites already satisfied

Reuse these; do not rebuild them:

- `CpToolbarComponent`, `CpListShellComponent`, `CpEmptyStateComponent`, `CpCardComponent`,
  `CpBadgeComponent`, `CpStatusPillComponent` and `CpNoticeComponent` already carry DAM's toolbar, list frame,
  empty states, surfaces and messages. The new parts compose them; none is replaced.
- `global.css` already has `.cp-muted`, `.cp-actions` and `.cp-disclosure`. A feature stylesheet that
  redeclares one of them is deleting work, not adding it.
- The shell already reads `title` from route `data` (`app-shell.component.ts`, `readRouteState`), so a second
  route-data key costs one line per route.
- The shell already zeroes `<main>` and hides its own chrome under `@media print`, and
  `RecipeKitchenViewComponent` is the reference for a page that prints.
- `CpFormSectionComponent` and `CpFieldRowComponent` already own the shape of a form. Nothing here changes
  that; a form sits inside a topic card the same way it sits inside a card today.

## Credentials and external prerequisites

None. Every prompt runs against the existing local stack (`aspire run`) and seeded workspace data.

## Checkpoint

| After | Demonstrable result |
| --- | --- |
| **UI.1** | Every signed-in page has the same gutter on every side, its title lines up with its content, and no routed page sets its own padding or width. |
| **UI.2** | DAM looks the same as before and is built from library parts; its two stylesheets have lost the rules those parts now own. |
| **UI.3** | The showcase has a drawer that traps and returns focus, closes on Escape, and becomes a full-screen sheet when narrow; `DESIGN-SYSTEM.md` says when to use a drawer, a dialog or a page. |
| **UI.4** | Recipes, Prompt Library and Brand library are grids of the same card DAM uses, under the same toolbar and footer; a prompt previews in a drawer over the grid. |
| **UI.5** | Every detail and settings page reads top to bottom as breadcrumb, head, messages, topic cards, and a small edit opens beside the record it changes. |
| **UI.6** | The flows and the recipe editor carry the same head and cards; no routed page draws its own. |
| **UI.7** | A recipe tool opens in a drawer beside the recipe, and the editor has one fewer level of tabs. |
| **UI.8** | `design-review` reports no blocker-level findings, a check fails the build if a page drifts back, and `npm run build` and `npm test` pass from `src/web/`. |

---

## UI.1 The shell owns the page frame

```text
SCOPE: Give every signed-in page one gutter and one width, owned by the shell. Add two tokens —
--cp-page-gutter (--cp-space-8, dropping to --cp-space-4 on a narrow screen) and a --cp-page-width in three
steps, reading, standard and wide — and have AppShellComponent wrap its page title and its router outlet in
one container that applies both, so the title and the content share a left edge. A page chooses its width
through route data beside the `title` it already declares; standard is the default, and the recipe editor,
the kitchen view, the concept studio and Image Studio take wide. Then delete `padding` and `max-width` from
the :host rule of every routed page that sets them.
CONSTRAINT: .claude/rules/design-system.md and frontend.md; the creatorpantry-design-system skill;
src/web/DESIGN-SYSTEM.md. The seam is src/web/src/app/shell/app-shell.component.{html,css,ts},
src/web/projects/creator-pantry-ui/src/lib/styles/tokens.css, and the route `data` in
src/web/src/app/app.routes.ts and the per-feature *.routes.ts files. The twelve :host rules to clear are
listed under "How this list was built"; re-derive that list from the code rather than trusting it.
RESTRICTION: Layout only — no page's content, markup order, component or behaviour changes. Keep every
`container:` declaration a :host carries: the editor, the kitchen view and the concept studio size
themselves by container query and their tests assert it. The frame must zero itself under @media print the
way <main> already does, and the kitchen view must print exactly as it does today. Sign-in, sign-up,
confirm-email and the landing page sit outside the shell and are not touched. Do not add a page head
component here; that is UI.2.
UTILIZATION: the playwright-cli skill to capture every signed-in route in both themes at a desktop and a
narrow width before the change and again after — the "before" set is the baseline UI.8 compares against;
design-review (read-only) after implementation.
BEHAVIOR: Capture the baseline screenshots, show the two tokens with their values, the frame's markup and
CSS, the route-data key and which routes take which width, and the list of :host rules to be cleared; wait
for approval; implement with a test that the shell applies the gutter and the route's width, and a check
that fails when a routed component's :host sets padding or max-width; record the two tokens in the Layers
section of DESIGN-SYSTEM.md, open its new "Composing a page" section with the frame — who owns the gutter,
the three widths and how a route picks one — and add the one-line rule to .claude/rules/frontend.md; run
npm run build and npm test from src/web/; report with before/after screenshots of one page that had a
gutter and one that did not.
```

## UI.2 Lift DAM's parts into the library

```text
SCOPE: Turn the parts DAM draws privately into @creator-pantry/ui exports, and rebuild DAM's two pages from
them with no visible change. The parts, each lifted from DAM as built rather than redesigned:
  1. a page head — breadcrumb, title, a slot for badges, a lede, a slot for the primary action — from
     .page-head and .back;
  2. a card grid — as many columns as fit, one at a narrow width — from dam-library's .grid;
  3. a media card — picture, linked title, description, badges, facts — from dam-library's .asset, with a
     tinted panel and a glyph where there is no picture;
  4. a topic card — display heading, prose-width body with a wide option, actions at the bottom — from
     dam-asset-detail's .block inside cp-card;
  5. a facts list — term and value pairs, stacked or inline — from both pages' .facts;
  6. the toolbar's search field and select field, from .search-field and .select-field.
With the page head in the library, the shell stops printing its own <h1>: the page head owns the one <h1>
on a page, and DAM's pages no longer have two.
CONSTRAINT: UI.1; .claude/rules/design-system.md, whose extension rules apply in full — classify each part
as token, primitive, component or pattern before writing it, search the public API first, and prefer
composing CpCardComponent, CpBadgeComponent and CpToolbarComponent over adding variants to them; the
new-component and creatorpantry-design-system skills; src/web/DESIGN-SYSTEM.md. Sources are
src/web/src/app/features/dam/dam-library.component.{html,css} and dam-asset-detail.component.{html,css}.
RESTRICTION: Every part is domain-neutral: no part knows what an asset, a recipe or a prompt is, fetches
anything, or names a route. The media card takes its picture by projection, because DAM's thumbnail is a
feature component that authorizes and loads the bytes itself, and that must not move into the library.
Keep DAM's focus behaviour — the title link focused after "Load more", the heading focused after a
removal — and its 40px targets. Every part works in both themes, at 200% zoom and at 320px wide, and uses
only --cp-* tokens. A page that still prints its own <h1> after the shell's is removed must not be left
with none: any routed page that relied on the shell's title gets the page head in this prompt, with its
existing route title and nothing else changed.
UTILIZATION: design-review (read-only) after each part; the playwright-cli skill to compare DAM against
the UI.1 screenshots.
BEHAVIOR: Show each part's classification, selector, typed inputs and outputs, slots, ARIA and states, and
the one title size chosen for every page; wait for approval; implement one part per turn with its spec,
its showcase entry, its public-api.ts export, its README.md entry and its DESIGN-SYSTEM.md paragraph,
moving DAM onto it and deleting the DAM rules it replaces in the same turn; once all six are in, add them
to "Implemented public API" in .claude/rules/design-system.md and to the export list in the root CLAUDE.md,
state the title size under "Content hierarchy", and write the library and detail shapes into "Composing a
page" with DAM named as the reference for both; finish with npm run build and npm test from src/web/ and a
line count of DAM's two stylesheets before and after.
```

## UI.3 A drawer in the library

```text
SCOPE: Add one drawer to @creator-pantry/ui: a panel that slides in from the right edge over a scrim and
holds a single task while the page behind stays in view. It has a title, an optional description, a
projected body that scrolls on its own, a footer slot for its actions, a close control and a close event,
and two widths — a standard one for a short form and a wide one for a comparison or a preview. Under a
narrow width it is a full-screen sheet. Write the rule for choosing between a drawer, a dialog and a page
into DESIGN-SYSTEM.md. No feature uses it yet; UI.4, UI.5 and UI.7 do.
CONSTRAINT: UI.1–UI.2; .claude/rules/design-system.md and its extension rules; the new-component and
creatorpantry-design-system skills; src/web/DESIGN-SYSTEM.md, "Confirmations" and the accessibility
baseline. CpDialogComponent is the nearest existing part: read it first, share what the two genuinely have
in common, and keep them two components — a drawer is not a dialog variant.
RESTRICTION: Modal: focus moves into the drawer when it opens, is trapped while it is open, and returns to
the control that opened it when it closes; the page behind is inert, not merely covered. Escape and the
scrim ask to close, they do not close — the drawer emits a request and the consumer decides, because a
drawer holding unsaved edits must be able to confirm a discard through ConfirmService first. The drawer
has no knowledge of forms, dirty state, routes or data. It uses --cp-z-overlay and --cp-z-modal as they
are defined, adds no z-index of its own, and sits above the shell's sidebar and top bar. Motion uses the
existing duration and easing tokens and is removed under prefers-reduced-motion. Works in both themes, at
200% zoom and at 320px wide, with every target at 40px. Body scroll is locked while it is open and
restored exactly when it closes.
UTILIZATION: design-review (read-only) after implementation; the playwright-cli skill to check focus trap
and return, Escape, and the narrow sheet in a real browser, since a unit test cannot prove any of them.
BEHAVIOR: Show the selector, typed inputs and outputs, slots, ARIA, the two widths as tokens, the
breakpoint for the sheet, and the drawer/dialog/page rule in full; wait for approval; implement with a
spec, a showcase entry that includes a drawer holding a form and one holding long content, the
public-api.ts export, the README.md entry, the export lists in .claude/rules/design-system.md and the root
CLAUDE.md, and the rule in DESIGN-SYSTEM.md and .claude/rules/frontend.md; run npm run build and npm test
from src/web/; report.
```

## UI.4 Bring the libraries onto the grid

```text
SCOPE: Rebuild the recipe library, the Prompt Library and the Brand library in DAM's library shape: the
page head, the toolbar with the shared search and select fields, the card grid of media cards, and the
footer with a result count and "Load more". Each keeps the filters, statuses, badges and actions it has
today, moved into the matching slot. The Prompt Library's preview, a dialog today, opens in the drawer so
the grid, its filters and the creator's place in it stay in view.
CONSTRAINT: UI.1–UI.3; .claude/rules/frontend.md and design-system.md. Pages:
src/web/src/app/features/recipes/recipe-library.component.*,
src/web/src/app/features/prompt-library/prompt-library.component.* and
src/web/src/app/features/brand/brand-library.component.*. The recipe and brand libraries filter by status
with pressed-state chips where DAM uses selects; keep the chips, and if both libraries draw them the same
way lift that one chip into the library rather than leaving two copies.
RESTRICTION: No API, model or service change. A recipe card shows a picture only if the list response
already carries one; if it does not, the card takes the no-picture panel and that gap is reported, not
closed here. Prompts and brand examples always take the no-picture panel. Nothing a row shows today is
dropped — every status pill, readiness flag, version number and tag has a place on the card. Keep every
loading, empty, no-results, degraded and error state each page has, and each page's existing role-gated
actions hidden rather than shown and refused. The prompt preview shows what it shows today and offers the
actions it offers today; closing it returns focus to the card that opened it. Do not add a preview to the
recipe or Brand library.
UTILIZATION: design-review (read-only) after each page; the playwright-cli skill for before/after
screenshots at a desktop and a narrow width.
BEHAVIOR: For each page show where every current element lands on the card and in the toolbar, and what
the no-picture panel shows; wait for approval; implement one page per turn, updating its spec and deleting
the stylesheet rules the shared parts replace; extend the library shape in DESIGN-SYSTEM.md with what the
three pages settled — what belongs in each slot of a media card, when a filter is a chip and when it is a
select — and give a lifted chip the full treatment a library part gets under "Keeping the design system
current"; report each page's stylesheet line count before and after.
```

## UI.5 Bring the detail and settings pages onto topic cards

```text
SCOPE: Rebuild every detail and settings page in DAM's detail shape: breadcrumb, page head with the
record's badges, one fixed place for outcome and problem messages, then a stack of topic cards, each with
its facts in the shared facts list and its actions at the bottom. Pages: Prompt detail, Brand source
detail, Brand settings, the Brand guide editor and history, the style test drive, Workspace settings,
Themed days and the AI allowance page. On these pages and on DAM's asset detail, a dialog that edits the
record on screen moves to the drawer, so the creator sees what they are changing.
CONSTRAINT: UI.1–UI.3; .claude/rules/frontend.md and design-system.md; src/web/DESIGN-SYSTEM.md, "The shape
of a form" — a form inside a topic card is still a legend and CpFormSectionComponent sections, and a topic
card does not replace a form section. Breadcrumb wording follows one rule, set in this prompt and written
into DESIGN-SYSTEM.md, in place of today's twelve. About seventeen components host a CpDialogComponent
today; the ones on these pages are in src/web/src/app/features/dam/ (edit, log a use, upload a version) and
src/web/src/app/features/brand/ (replace a source, edit its text, logos, activate a guide, upload).
RESTRICTION: Every form keeps its dirty state, its validation, its navigation guard and its Save exactly as
they are — Themed days has a canDeactivate guard and the Brand guide editor preserves unsaved edits through
recoverable errors, and both must still do so. An inline form stays inline: the drawer takes over existing
dialogs only, and only where UI.3's rule says a drawer — a dialog that asks a short question or creates
something from nowhere stays a dialog, and a confirmation stays a ConfirmService modal. A dialog that moves
keeps its fields, its validation, its wording and its outcome; a drawer holding unsaved edits confirms
before it discards them. Do not merge, split or reorder what a page says without showing it first.
Generated content stays visibly identified and editable before acceptance.
UTILIZATION: design-review (read-only) after each page; test-gap-analyzer (read-only) once at the end,
against the forms that moved.
BEHAVIOR: Show the breadcrumb rule, for each page its topic cards by heading with what each holds, and a
table of every dialog on these pages with its verdict — drawer or stays a dialog — and the reason; wait
for approval; implement one page per turn, updating its spec and deleting the stylesheet rules the
shared parts replace; extend the detail shape in DESIGN-SYSTEM.md with the breadcrumb rule, how a topic
card and a form section sit together, and when an edit is inline and when it is a dialog; report each
page's stylesheet line count before and after.
```

## UI.6 Bring the flows and the recipe editor into the frame

```text
SCOPE: Give the remaining pages the same head and cards. The flows — the content pipeline, Brand setup,
Image Studio, the recipe concept studio and first-draft review — take the page head, and each step's body
is built from topic cards. The content pipeline and Brand setup both draw a step list privately; lift it
into the library as one stepper and use it in both. The recipe editor and the kitchen view take the page
head in place of their own headers, with the editor's status pills and Save in the head's action slot.
CONSTRAINT: UI.1–UI.2 and UI.5; .claude/rules/frontend.md, design-system.md and recipes.md;
src/web/DESIGN-SYSTEM.md, "Tabs, anchor navs, and one Save". Pages:
src/web/src/app/features/content-pipeline/content-pipeline-shell.component.*,
src/web/src/app/features/brand/setup/brand-setup-shell.component.*,
src/web/src/app/features/image-studio/image-studio.component.*,
src/web/src/app/features/ai/recipe-concept-studio.component.* and recipe-first-draft-review.component.*,
src/web/src/app/features/recipes/recipe-editor.component.* and recipe-kitchen-view.component.*.
RESTRICTION: The recipe editor keeps its tabs, its per-form dirty state, revealFirstFieldError, its
navigation guard and every field exactly as they are; this prompt changes its header and nothing below it.
The kitchen view prints exactly as it does today. A flow keeps its steps, their order, what is saved as
the creator goes, and its resume behaviour. The legend of a multi-step form stays in the shell of the
flow, not in each step. The stepper is domain-neutral and says which step is current, done and not yet
reachable in words as well as colour.
UTILIZATION: the new-component skill for the stepper; design-review (read-only) after each page;
test-gap-analyzer (read-only) against the recipe editor once its header has moved.
BEHAVIOR: Show the stepper's selector, inputs, ARIA and states, and for each page what moves into the page
head and how each step divides into topic cards; wait for approval; implement the stepper first, with the
full treatment a library part gets under "Keeping the design system current", then one page per turn with
its spec updated; write the flow shape into "Composing a page", and correct every sentence in
DESIGN-SYSTEM.md and .claude/rules/frontend.md that names RecipeEditorComponent or BrandSetupShellComponent
as a reference, so each still describes what the component does; report each page's stylesheet line count
before and after.
```

## UI.7 Recipe tools in a drawer

```text
SCOPE: Take the Tools area out of the recipe editor's tabs. Today it is a tab holding a second tab set of
seven panels — scaling, converting, yield and display, and the four AI requests: revision, adaptation,
substitution and review. Replace it with one "Tools" control in the editor's page head that lists the
seven, and open the chosen tool in the wide drawer beside the recipe. The editor loses a level of tabs and
a creator reads a scaled recipe or an AI proposal next to the recipe it was made from.
CONSTRAINT: UI.3 and UI.6; .claude/rules/recipes.md, ai.md, frontend.md and design-system.md;
src/web/DESIGN-SYSTEM.md, "Tabs, anchor navs, and one Save"; the add-recipe-feature skill. The seam is the
`tools` tab panel and `toolTabs` in src/web/src/app/features/recipes/recipe-editor.component.{html,ts}; the
tool components themselves are in src/web/src/app/features/recipes/ and src/web/src/app/features/ai/.
This prompt is optional: skip it and UI.8 still holds.
RESTRICTION: Each tool component moves as it is — same inputs, same outputs, same service calls, same
states; this prompt changes where a tool is hosted, not what it does. A tool still reads a saved recipe:
whatever locks the Tools tab while the recipe has unsaved changes or has never been saved locks the
control the same way, with the same explanation in words. A scaled or converted recipe is still a proposal
until the creator accepts it or saves it as a version, and an AI change is still a diff the creator
reviews before anything canonical is replaced — the drawer adds no shortcut to either. Closing the drawer
never accepts, never discards an accepted change, and confirms before abandoning a request that is still
running or a proposal not yet answered; a running AI operation is cancelled or left tracked exactly as it
is today, never silently orphaned. Accepting a proposal reaches the editor's form through the path it uses
now, so dirty state and the navigation guard still see it. Media, History, Tests, Readiness and Publish
stay tabs. No API, model or service change.
UTILIZATION: design-review (read-only) and ai-safety-reviewer (read-only) after implementation;
test-gap-analyzer (read-only) on the accept, cancel and close paths; the playwright-cli skill to run one
deterministic tool and one AI tool end to end in a real browser.
BEHAVIOR: Show the Tools control, how each of the seven opens, what locks it and what it says when locked,
and for each tool what closing the drawer does in every state it can be in; wait for approval; implement
with the editor's and each tool's spec updated, including a test that an accepted proposal still marks the
form dirty and a test that the control is locked whenever the tab was; correct "Tabs, anchor navs, and one
Save" in DESIGN-SYSTEM.md, which names Tools as a tab; run npm run build and npm test from src/web/;
report.
```

## UI.8 Guardrails and verification

```text
SCOPE: Stop the drift from coming back, and prove the whole pass holds together. Add checks that fail the
build when a routed page sets its own gutter or width, renders an <h1> or a page header outside the shared
page head, or redeclares a part the library now owns — a grid of cards, a facts list, a search or select
field, a step list, a hand-built sliding panel, or the .cp-muted and .cp-actions utilities. Then walk
every signed-in route in both themes at a desktop and a narrow width and compare against the UI.1
baseline. Finally, make the page
contract something the toolkit asks for unprompted: the new-component skill and the design-review agent
each ask which of the three shapes a page is before anything else, and the design system's documents are
reconciled with what shipped.
CONSTRAINT: UI.1–UI.6, and UI.7 if it was run; the verification checklist in
.claude/rules/design-system.md and the Verification section of .claude/rules/frontend.md. The UI.1 check on :host padding and max-width already exists; extend
it rather than writing a second one beside it. The toolkit files are .claude/skills/new-component/SKILL.md,
.claude/skills/creatorpantry-design-system/SKILL.md, .claude/agents/design-review.md and src/web/CLAUDE.md.
One disagreement predates this pass and is settled here: .claude/rules/design-system.md names DM Serif
Display as the display face, while tokens.css and DESIGN-SYSTEM.md ship Fredoka.
RESTRICTION: A check names the file and the rule it objects to, and every exemption is a listed path with
a reason, so granting one costs a reviewable line. Do not weaken a check to make a page pass; fix the page
or list it. Fix only findings inside this document's scope and report the rest.
UTILIZATION: design-review (read-only) across every route; test-gap-analyzer (read-only) on the new library
parts; the playwright-cli skill for the comparison screenshots.
BEHAVIOR: Show each check, what it matches and its exemption list, and the wording added to the skill, the
agent and the rules; wait for approval; implement, then read DESIGN-SYSTEM.md, README.md, the showcase,
.claude/rules/design-system.md, .claude/rules/frontend.md and the export list in the root CLAUDE.md against
public-api.ts and the shipped pages, and correct anything that no longer matches; run design-review and
test-gap-analyzer, fix in-scope findings, run npm run build and npm test from src/web/, and report: a
pass/fail line for each of UI.1–UI.7 (or "skipped"), before/after screenshots of one page per shape,
total feature stylesheet lines before and after, every exemption granted, every documentation correction made, and every dialog
still in the app with one line on why it is a dialog and not a drawer.
```
