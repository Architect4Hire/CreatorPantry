# CreatorPantry — Recipes UI/UX SCRUB Prompts

Atomic prompts for fixing the confusing parts of the recipe library and recipe editor experience, for use
with Claude Code and the CreatorPantry `.claude/` toolkit.

This document supplements `docs/prompts/creatorpantry-scrub-microprompts.md`. It is scoped to the recipe
UI at `src/web/src/app/features/recipes/` and does not redefine anything already decided in `CLAUDE.md` or
`.claude/rules/`.

## Reusable SCRUB skeleton

```text
SCOPE:        one observable change and the exact repository seam it touches
CONSTRAINT:   stack, rule files, skills, and prerequisites
RESTRICTION:  explicit exclusions and invariants that must not be weakened
UTILIZATION:  skills, read-only reviewers, and tools to invoke
BEHAVIOR:     inspect, plan, wait for approval, implement, test, and report
```

## How this list was built

A `design-review` audit walked the real creator journeys — browse, create, edit ingredients, view history,
compare versions, restore, duplicate — against `src/web/DESIGN-SYSTEM.md`, `.claude/rules/design-system.md`,
`.claude/rules/frontend.md`, and `.claude/rules/recipes.md`.

The honest finding: most of this feature is *not* rough. `@creator-pantry/ui` primitives are used
consistently, there are no deep imports or literal colors, loading/empty/error/success states are present
almost everywhere, and the history/comparison/restore/duplicate flows are unusually careful about not
implying that history can be rewritten. The functional confusion is concentrated in one place —
**ingredient editing silently disconnected from Save** — plus a handful of smaller, separable rough edges.

Separately, the editor's *layout* is genuinely cramped: `recipe-editor.component.css` caps the whole
editor at `max-width: 48rem` (768px) regardless of viewport, and the recipe is split across seven tabs
(`metadata`, `ingredients`, `instructions`, `notes`, `timing`, `media`, `history`) that must be clicked
through one at a time instead of being worked on as a page. R.3-R.4 below widen the editor and restructure
that navigation into fewer/no tabs, by request.

Prompts are ordered so the one real data-loss bug is fixed first (R.1-R.2), then the layout restructuring
(R.3-R.4, since it changes where later validation/error surfacing lives), then the remaining independent
polish items.

## Prerequisites already satisfied

Reuse these; do not rebuild them:

- `RecipeEditorComponent` already has a working save/dirty pipeline for every other field: a
  `RecipeFormSnapshot`-based `isDirty()`, a page-level "Unsaved changes" pill, `confirmDiscardIfDirty()`,
  and a `beforeunload` guard (`src/web/src/app/features/recipes/recipe-editor.component.ts`).
- `instructions` already round-trips end to end through `CreateRecipeRequest`/`UpdateRecipeRequest`
  (`src/web/src/app/models/recipe.models.ts:118`, `:188`) via `InstructionGroupInput[]` — this is the
  pattern ingredients should follow, not a new pattern to invent.
- `RecipeIngredientEditorComponent` and `IngredientPasteReviewComponent` already do the hard parts:
  grouping, reordering, paste-and-parse, and ingredient-vocabulary matching. The gap is wiring, not
  missing functionality.
- `recipe-restore.component.html` and `recipe-duplicate.component.html` already show the correct pattern
  for associating a hint with a control via `[attr.aria-describedby]` where `CpFieldComponent` doesn't do
  it automatically — reuse that pattern rather than inventing a new one.
- Role-gated actions (Restore = Editor, Duplicate = Contributor) are already hidden rather than
  shown-then-403'd. Keep that pattern for any new gated action.

## Credentials and external prerequisites

None. Every prompt here runs against the existing local stack (`aspire run`) and seeded workspace data.

## Checkpoint

| After | Demonstrable result |
| --- | --- |
| **R.2** | A creator who edits ingredients and clicks Save has those edits actually persisted; leaving the tab with unsaved ingredient edits triggers the same discard warning as every other tab. |
| **R.4** | The recipe editor uses the available viewport instead of a fixed 768px column, and a creator works through the recipe with fewer clicks between sections than today's seven separate tabs. |
| **R.7** | Every list/detail/validation surface in the recipe editor and library gives the creator an unambiguous next action, even in an error or partial-error state. |
| **R.8** | `design-review` re-audit of the same journeys reports no blocker-level findings; `npm test` and `npm run build` pass from `src/web/`. |

---

## R.1 Recipe ingredient groups on the create/update contract - done

```text
SCOPE: Extend the recipe create/update seam so ingredient groups round-trip the same way instructions
already do: CreateRecipeViewModel/UpdateRecipeViewModel gain an ingredientGroups input (mirroring
InstructionGroupInput's replace semantics — named group/line updates in place, unnamed is new, omitted
is removed), and Facade → Business → DataLayer → Repository persist it as part of the same save.
CONSTRAINT: .claude/rules/recipes.md and backend.md; the add-recipe-feature skill; the existing
instructions field on CreateRecipeViewModel/UpdateRecipeViewModel as the pattern to mirror
(src/CreatorPantry.Domain/Modules/Recipes, mirrored at src/web/src/app/models/recipe.models.ts:118,188).
RESTRICTION: Preserve creator-entered ingredient display text exactly — a recognized Ingredient reference
enriches a line, it never replaces the entered text. Do not let this endpoint silently change quantities
or scaling behavior of ingredients it didn't touch. Explicit/named RecipeVersion records stay immutable;
this only affects the current draft/edit surface. Do not touch instructions, timing, or yield handling,
which already work.
BEHAVIOR: Inspect the current instructions round-trip as the reference implementation, show the
ingredientGroups contract and persistence plan, wait for approval, implement with unit/integration tests
proving a create and an update both persist ingredient groups exactly as submitted, then report.
```

## R.2 Wire the ingredient editor into save, dirty tracking, and navigation guards - done

```text
SCOPE: Make RecipeEditorComponent actually send ingredient groups on save using the R.1 contract, and
fold ingredient-tab edits into the same unsaved-state machinery every other tab already has: isDirty(),
the "Unsaved changes" pill, confirmDiscardIfDirty(), and the beforeunload guard.
CONSTRAINT: .claude/rules/frontend.md ("Preserve unsaved creator edits through recoverable errors and
navigation warnings"); the existing RecipeFormSnapshot/isDirty machinery in
src/web/src/app/features/recipes/recipe-editor.component.ts (see isDirty() and the RecipeFormSnapshot
comment describing why ingredient edits were previously excluded, and buildCreateRequest/
buildUpdateRequest which currently drop groupsChanged on the floor).
RESTRICTION: Do not build a second, parallel dirty-tracking mechanism for ingredients — extend the
existing RecipeFormSnapshot/isDirty() so there is exactly one source of truth for "this recipe has
unsaved changes." Remove or correct the "Nothing here is saved yet" body copy on the Ingredients tab once
it is no longer true; do not leave stale copy that contradicts the new behavior.
BEHAVIOR: Show the wiring plan (what buildCreateRequest/buildUpdateRequest send, what isDirty() now
checks), wait for approval, implement with component tests proving: (1) ingredient edits survive a save
and a reload, (2) navigating away with only unsaved ingredient edits triggers the discard-confirmation
guard, (3) the "Unsaved changes" pill lights up for ingredient-only edits. Report proof for all three.
```

## R.3 Recipe editor layout and information architecture — plan - done

```text
SCOPE: Decide and document a wider, less tab-fragmented layout for RecipeEditorComponent. Inspect the
current seven-tab structure (metadata, ingredients, instructions, notes, timing, media, history —
recipe-editor.component.ts:193-200) and each tab's actual field/content volume, then propose 1-2 concrete
layout options that (a) remove or substantially raise the current max-width: 48rem cap
(recipe-editor.component.css:4) so the editor uses available viewport width on desktop, and (b) reduce
the number of separate tab clicks needed to work through a recipe — e.g. collapsing metadata/notes/timing
into one scrollable "Recipe details" section, placing ingredients and instructions side by side on wide
viewports instead of on separate tabs, and/or replacing tabs with in-page anchored sections plus a sticky
section nav. History likely stays separate since it is a distinct list/detail journey, not a form field.
CONSTRAINT: .claude/rules/design-system.md and frontend.md; src/web/DESIGN-SYSTEM.md's responsive/
accessibility requirements (200% zoom, narrow layout, keyboard operation); the existing ARIA tabs pattern
in CpTabsComponent as a baseline to compare against, not a constraint to preserve if the plan drops it.
RESTRICTION: The plan must keep the editor fully usable at narrow/mobile widths (a wide-viewport layout is
additive, not a replacement for a working narrow layout). It must not change what data is
collected/validated or how save/dirty/discard works (R.1-R.2) — this prompt is presentation and IA only.
Do not implement anything in this prompt.
BEHAVIOR: Show the field/content inventory per current tab, propose the layout option(s) with rough
wireframe-in-prose (what's visible together, what's still a separate section, what happens at narrow
widths), wait for approval, then stop — implementation happens in R.4.
```

## R.4 Recipe editor layout and information architecture — implementation - done

```text
SCOPE: Implement the layout approved in R.3: widen RecipeEditorComponent beyond the current
max-width: 48rem cap and restructure its navigation into the approved fewer/no-tabs structure.
CONSTRAINT: the approved R.3 plan; .claude/rules/design-system.md and frontend.md; the save/dirty/discard
pipeline from R.1-R.2, which must keep working unchanged against the new layout; existing per-field
validation (fieldError()) and the generic "Some fields need attention" save-error banner.
RESTRICTION: Do not weaken or duplicate the save/dirty tracking built in R.2 to accommodate the new
layout — the same single isDirty()/RecipeFormSnapshot source of truth must drive it regardless of how
sections are presented. Whatever the approved plan does with tabs, a validation error on a field that
isn't currently in view must still be reachable: scroll the relevant section into view and give the field
visible focus/highlight, not just the generic banner. No literal colors — tokens only. Must remain legible
and operable at 200% zoom and at narrow/mobile widths per the R.3 plan's narrow-width behavior.
BEHAVIOR: Show the component/template restructuring plan against the approved R.3 layout, wait for
approval, implement with component and accessibility tests (including a validation-error-on-an-offscreen-
field test and a narrow-viewport test), and report before/after screenshots or a description of the
viewport-width behavior.
```

## R.5 Ingredient paste-review: group targeting, bulk accept, and hint accessibility - done

```text
SCOPE: In the paste-and-parse flow (ingredient-paste-review.component.*, invoked from
RecipeIngredientEditorComponent's onLinesAccepted), let the creator choose which ingredient group
receives newly pasted lines instead of always appending to the first group; add a "Add all matched" bulk
action alongside the existing per-line "Add to recipe" action; and wire the paste textarea's hint to the
control via [attr.aria-describedby], matching the pattern already used in recipe-restore.component.html
and recipe-duplicate.component.html.
CONSTRAINT: .claude/rules/design-system.md and frontend.md; existing per-line accept/reject affordances
and CpFieldComponent's hint id convention ({forId}-hint).
RESTRICTION: A bulk accept only applies to lines the parser already matched with confidence — do not
silently accept ambiguous or unmatched lines in bulk; those still require the existing per-line review.
Do not change ingredient-vocabulary matching logic itself, only the review/accept UI around it.
BEHAVIOR: Show the group-selector and bulk-accept interaction design plus the aria-describedby fix, wait
for approval, implement with component and accessibility tests (including a case with 15+ pasted lines
across multiple existing groups), and report.
```

## R.6 Disabled-section explanation in create mode - done

```text
SCOPE: Whatever the R.3/R.4 layout does with the current Media/History tabs (kept as reduced tabs, or
turned into disabled/absent sections), give a first-time creator in create mode a visible reason why
Media and History aren't available yet ("Save the recipe first") instead of an unexplained disabled
control or a silently missing section.
CONSTRAINT: the layout approved and implemented in R.3-R.4; recipe-editor.component.ts:199-200's existing
isCreateMode-driven disabling as the behavior to make legible, not change.
RESTRICTION: Do not change when Media/History actually become available (still first-save-required) —
this is purely about explaining the existing rule to the creator.
BEHAVIOR: Show where the explanatory text/affordance goes in the R.4 layout, wait for approval, implement
with a test asserting the reason is visible/announced in create mode, and report.
```

## R.7 Keep "New recipe" available during a library error state - done

```text
SCOPE: In recipe-library.component.html, show the "New recipe" action alongside "Try again" when
state().status === 'error', instead of hiding it. A transient search/filter failure should not block
creating a new recipe.
CONSTRAINT: .claude/rules/design-system.md; existing cpListShellActions slot structure.
RESTRICTION: Do not change retry/error-recovery behavior for the list itself — only restore the
unrelated create action's availability.
BEHAVIOR: Show the small template change, wait for approval, implement with a component test asserting
both actions render in the error state, and report.
```

## Robert Rework 
* Split the sub menu in the recipe box into three tabs General, Ingredients and Instructions and have them span the div 100%
* Have ingredients in own vertical tab group
* Have instructions in own vertical tab group
* Make vertical tab group a navigation menu
* Don't require ingredients group should be optional some recipes will not require ingredient/insturction groups
* the optional checkbox in the ingredients isn't working like it should it is a default black box - should be an empty check box
* Unit and Ingredients should have a drop down list that filters type ahead against the reference data
* Instructions shouldn't have duration or if it does it should be optional and clear that it is
* Total Time should be the sum of all times and automatically calculate
* Yield should be a drop down box with a look up to reference data
* The tools are confusing they provide value but because they don't update the recipe I don't know what value they provide
* The History UI is unsable and unreadable

*Captured verbatim above as the source record. Written up as R.9–R.20 below, in dependency order: the
layout split first, then the group/optionality change the two navigation prompts depend on, then the
design-system primitives the two picker prompts depend on, then the standalone fixes and the two
redesigns. Decisions already taken: the three tabs **replace** the R.3/R.4 anchored-section rail rather
than sitting inside it.*

## Robert Rework #2
* When i go into a saved recipe the ingredients/instructions don't appear to reload so i can edit like i would expect

*Triaged, and the cause found — written up as R.8 below, which must land first.*

*Original triage note, kept because the reasoning still applies to the next report like it:*

*Not triaged and no prompt written yet. Worth knowing before it is: R.2 has a passing test that ingredient
edits survive a save and a reload, and R.4 changed the mounting this depends on (the Ingredients panel used
to be lazily mounted on first tab click and is now always in the DOM). So this may already be fixed, may be
a different path from the one under test, or may be a real gap the R.2 test misses — establish which before
writing a fix.*

## R.8 Hand-added ingredient lines are dropped on save - done

```text
SCOPE: A line added by hand in the ingredient editor is silently dropped on save, never reaches the server,
and is therefore absent on reload — reproduced on recipe a954bf05-b270-46f1-8c4e-92b3f7932a65. The chain:
addRow() appends blankRow(), which sets displayText: '' (recipe-ingredient-editor.component.ts:70 and
370-375); no control in the ingredient UI edits displayText — the row's four inputs are Quantity, Unit,
Ingredient (bound to ingredientNameText) and Preparation note, and displayText appears only as read-only
output guarded by @if (row.displayText) (recipe-ingredient-editor.component.html:55-56); and
buildIngredientGroups() filters rows by exactly that field (recipe-editor.component.ts:1263). So a creator
fills every visible field, displayText stays empty, and the row is removed from the request while the save
reports success and writes a version. Pasted lines survive because rowFromParsedLine sets displayText from
the pasted text (recipe-ingredient-editor.component.ts:124), which is why the failure looks intermittent
rather than total. Fix it so a line typed into the visible fields persists. Land this before R.9 — the
layout work rearranges the surface this bug lives on.
CONSTRAINT: .claude/rules/recipes.md — "Preserve the creator's entered ingredient and instruction text",
and "a recognized Ingredient reference enriches a RecipeIngredient; it never replaces the entered display
text". The R.1 wire contract (IngredientGroupInput/IngredientInput) and the R.2 dirty/save machinery both
stay as they are; this is about what the editor puts into a row, not about how rows are submitted. Three
candidate fixes, each with a real cost — pick one and justify it: (a) make displayText the row's primary
input, with quantity/unit/ingredient shown beside it as the parsed reading, which is closest to how the
paste flow already works; (b) compose displayText from the structured fields only while the creator has not
entered a line of their own, and never afterwards; (c) relax the filter to keep any row carrying any
content, and accept a stored line whose displayText is empty.
RESTRICTION: Do not just delete the blank-row filter without deciding what displayText then holds — a
persisted line with an empty displayText renders blank on every surface that shows it: the row itself, the
scaling preview's "before" column, unit conversion, display normalization, and version comparison. Do not
let the app rewrite a displayText the creator did type, whether typed or pasted; it stays verbatim. Do not
lose the filter's original purpose either — a genuinely empty row that a creator added and abandoned must
still not be saved. Backend persistence is not at fault and must not be changed: ReconcileIngredientGroups
and BuildIngredientGroup both work, the detail query Includes both collections, and applyDetail populates
them correctly.
BEHAVIOR: State which fix and why, wait for approval, then implement — and add the test whose absence let
this through. Every existing R.2 test builds rows with editableRow({ displayText: '...' }), explicitly
setting the one field the real UI never sets, which is exactly why they pass against a broken feature:
audit them, and make at least one drive the real path — click "Add ingredient", type into the row's actual
inputs, save, and assert the line is present in the request body and survives a reload. Report proof for
the hand-entered path, and proof that the pasted path still works.
```

## R.9 Split the Edit surface into three full-width tabs - done

```text
SCOPE: Replace the Edit area's single scrolling surface and section rail with three tabs — General,
Ingredients, Instructions — spanning the full width of the editor card. General holds what the Details,
Notes and Timing & yield sections hold today. The outer Edit/Tools/Media/History areas are unchanged; this
splits what is inside Edit.
CONSTRAINT: .claude/rules/design-system.md and frontend.md; CpTabsComponent as the implementation (it
wraps and meets the 40px target as of R.4); the 90rem cap and container queries R.4 added to
recipe-editor.component.css, which stay; EDIT_SECTIONS, .edit-layout and .section-rail in
recipe-editor.component.{ts,html,css} are what this replaces.
RESTRICTION: R.4's offscreen-error reveal must keep working across the split — revealFirstFieldError() and
FIELD_LOCATIONS map a field to a sectionId on one scroll surface today, and a validation error on a field
in a tab that is not showing must still select that tab, scroll to the field and focus it. Do not weaken
isDirty()/RecipeFormSnapshot: one Save still writes all three tabs, so the "Unsaved changes" pill must
light for an edit in any of them. Delete the rail and its IntersectionObserver rather than leaving dead
code. Tabs span 100% of their container and stay operable at 200% zoom and at narrow widths.
BEHAVIOR: Show the tab/field mapping (which fields land in General, what FIELD_LOCATIONS becomes), wait
for approval, implement with tests proving: a validation error on a field in a non-visible tab selects that
tab and focuses the field; the dirty pill lights for an edit in each of the three tabs; the tablist spans
its container; and the narrow layout still has no horizontal overflow. Report proof for each.
```

## R.10 Make ingredient and instruction groups optional - done

```text
SCOPE: Let a creator add ingredients and instruction steps without creating a group first. Today
RecipeIngredientEditorComponent.addRow(groupKey) needs a group, and the empty state tells the creator to
"add a group to start typing them in" (recipe-ingredient-editor.component.html:9-10); instructions are the
same shape in recipe-editor.component.html. Make ungrouped the default and a named group an opt-in for
recipes that genuinely have sections.
CONSTRAINT: .claude/rules/recipes.md. The server already permits this — neither
CreateRecipeViewModelValidator nor UpdateRecipeViewModelValidator requires a non-empty IngredientGroups or
Instructions, and an empty group title is valid (only GroupTitleMaxLength applies). This is a frontend
change; do not add a backend rule to match.
RESTRICTION: Do not change the wire contract — lines still submit inside an IngredientGroupInput and steps
inside an InstructionGroupInput, because that is the shape the API takes (R.1). An implicit untitled group
is a presentation decision, not a new request shape. A recipe that does have titled groups must still open
showing them, not flattened.
BEHAVIOR: Show how an ungrouped recipe is presented and how it submits, wait for approval, implement with
tests proving: a new recipe takes an ingredient and a step with no group created first and saves correctly;
a recipe with titled groups round-trips unchanged; and adding a group to an ungrouped recipe moves its
existing lines somewhere predictable rather than losing them. Report proof.
```

## R.11 Vertical group navigation inside Ingredients - done

```text
SCOPE: Inside R.9's Ingredients tab, give a recipe's ingredient groups a vertical navigation menu down the
side, so a creator moves between groups without scrolling past all of them. A navigation menu — a list of
links or buttons naming each group — not a second ARIA tablist.
CONSTRAINT: .claude/rules/design-system.md; the .section-rail pattern R.4 built and R.9 removes is the
precedent for "ordinary navigation, no roving tabindex, every item tabbable"; R.10's outcome, since a recipe
may legitimately have no groups at all.
RESTRICTION: With no groups the nav must not render as an empty shell — an ungrouped recipe shows the flat
list and no nav. Do not introduce lazy mounting for groups: an unsaved edit in a group the creator has
navigated away from must still count as dirty and must still submit. Must collapse to a usable
single-column layout at narrow widths and stay operable at 200% zoom.
BEHAVIOR: Show the markup, keyboard behavior and narrow-width fallback, wait for approval, implement with
tests proving: every group is reachable from the nav; an edit in a group not currently in view still lights
the dirty pill and still submits; and the ungrouped case renders no nav. Report proof.
```

## R.12 Vertical group navigation inside Instructions - done

```text
SCOPE: The same change as R.11, for R.9's Instructions tab and its instruction groups.
CONSTRAINT: whatever R.11 established — this must reuse that navigation, not build a second one that looks
similar. If R.11's nav is not already domain-neutral enough to serve both, factor it out as part of this
prompt rather than duplicating it.
RESTRICTION: As R.11: no empty nav for an ungrouped recipe, no lazy mounting, no loss of dirty state or
submitted content for a group out of view. Do not change instruction step ordering, the move/remove
controls, or the technique/temperature fields the editor carries but does not edit.
BEHAVIOR: Show what is shared with R.11 and what is specific here, wait for approval, implement with the
equivalent tests plus one proving the shared navigation is a single implementation, and report.
```

## R.13 Fix the "Optional" checkbox, and decide where checkboxes live - done

```text
SCOPE: The "Optional" checkbox in the ingredient editor renders as a filled dark box rather than an empty
checkbox (recipe-ingredient-editor.component.html:143-151). It is a bare native <input type="checkbox">
with no styling of its own — the only related rule, .checkbox-label, styles the label. Most likely cause is
themes.css's `color-scheme: dark` making the native control dark-filled; confirm in both themes before
choosing a fix.
CONSTRAINT: .claude/rules/design-system.md. @creator-pantry/ui has no checkbox primitive, and the app has
two hand-rolled checkboxes — this one and recipe-display-normalization.component.html:141 — which is the
repeat that extension rule 5 treats as the threshold for a reusable primitive.
RESTRICTION: Checked and unchecked must be distinguishable without relying on colour alone, keep a 40px
target, work in both themes, and show a visible focus ring. Do not fix it only in this feature's CSS and
leave the display-normalization checkbox looking different.
BEHAVIOR: Confirm the cause in both themes and say what it was; show whether you are adding a
CpCheckboxComponent or styling the native control in the library, and why; wait for approval; implement
with tests covering both themes, both states, disabled, focus and the 40px target; update the showcase,
public-api.ts and DESIGN-SYSTEM.md if a component is added; and report.
```

## R.14 A type-ahead combobox in the design system - done

```text
SCOPE: Add a type-ahead combobox to @creator-pantry/ui — a text input that filters a supplied option list
as the creator types, with keyboard selection. Domain-neutral: it takes options and emits a selection. It
does no fetching. Foundation for R.15 and R.16, which is why it is its own prompt.
CONSTRAINT: .claude/rules/design-system.md and its extension rules; the WAI-ARIA combobox pattern
(aria-expanded, aria-controls, aria-activedescendant over a listbox; Escape, Home/End, Enter, arrow keys);
CpFieldComponent for label/hint/error, which this composes with rather than replaces.
RESTRICTION: No HttpClient, no service-coupled RxJS, and no recipe or ingredient vocabulary anywhere in the
library — frontend.md forbids domain logic there. Support both "must pick from the list" and "free text
allowed" without splitting into two components. Usable by keyboard alone, and the filtered result count is
announced.
BEHAVIOR: Define the contract first — selector, signal inputs/outputs, projected regions, ARIA, states,
narrow-width behavior — show it, wait for approval, implement with tests for keyboard operation, filtering,
empty results, disabled, free-text versus restricted mode and screen-reader naming, add a showcase example
with realistic CreatorPantry content, update public-api.ts, README and DESIGN-SYSTEM.md, and report.
```

## R.15 Unit and Ingredient pickers against reference data - done

```text
SCOPE: Replace the free-text Unit and Ingredient inputs in the ingredient editor (the row-unit-* and
row-ingredient-* fields in recipe-ingredient-editor.component.html) with R.14's combobox filtering against
reference data. Units come from ReferenceService.listUnits(), which already caches the whole catalogue for
the session. Ingredients need a new ReferenceService method against /api/v1/reference/ingredients, which
accepts a Search query parameter — the ingredient catalogue is too large to hold client-side.
CONSTRAINT: .claude/rules/recipes.md — "a recognized Ingredient reference enriches a RecipeIngredient; it
never replaces the entered display text"; .claude/rules/tenancy.md — reference routes are global and must
never carry a workspace identifier; frontend.md — typed services own HTTP, components never inject
HttpClient.
RESTRICTION: Picking from the list sets ingredientId/measurementUnitId and must not overwrite the creator's
displayText. An unmatched entry stays enterable as free text rather than being rejected. Do not change the
match model — combinedMatchState, confirmIngredientMatch and the matchState pills stay as they are; this
replaces an input control, not the confirmation machinery. Debounce the ingredient search and cancel
in-flight requests; never one request per keystroke.
BEHAVIOR: Show the behavior for matched, unmatched and reference-data-unavailable cases, wait for approval,
implement with tests proving creator text survives a pick, an unmatched value still saves, and the search
is debounced and cancelled, and report.
```

## R.16 A yield unit picker - done

```text
SCOPE: Give Yield a unit picker backed by reference data. yieldUnitId is already on the create/update
contract and the detail model (recipe.models.ts:138, 206, 585) but the editor has no control for it — which
is what RecipeEditorComponent's own comment means by "no vocabulary picker exists in this editor for any
reference field ... or yield unit". Add one using R.14's combobox over ReferenceService.listUnits().
CONSTRAINT: .claude/rules/recipes.md — yield, serving unit and equipment modelled explicitly; the Timing &
yield fields' placement, which R.9 moves into the General tab.
RESTRICTION: Do not touch the free-text yieldText field. "About 4 generous bowls" is the creator's own
words and is not the same fact as a unit id; both exist deliberately. Do not make the unit required.
recipe-yield-reconciliation reads savedYieldUnitId — do not change what it reads.
BEHAVIOR: Show where the control goes and how it coexists with yieldText, wait for approval, implement with
tests covering set, cleared and reference-data-unavailable cases, and report.
```

## R.17 Make a step's optional duration read as optional - done

```text
SCOPE: A step's duration already is optional — the 'Duration (minutes)' cp-field in
recipe-editor.component.html carries no [required] — but nothing says so, and a numeric field on every
step reads as something a creator is expected to fill in. Either label it optional or drop it from the
default step form and reveal it on request.
CONSTRAINT: .claude/rules/design-system.md; CpFieldComponent's existing label, hint and required
affordances, which already distinguish required from optional visually — use them rather than inventing a
new marker.
RESTRICTION: Do not remove durationMinutes from the wire contract and do not stop submitting it: existing
recipes have step durations and must keep them. Whichever presentation is chosen, a step that already has a
duration must show it without the creator having to go looking for it.
BEHAVIOR: Show the chosen presentation, wait for approval, implement with tests proving a step with no
duration saves cleanly and a step with an existing duration still displays and still submits it, and
report.
```

## R.18 Calculate Total time instead of asking for it - done

```text
SCOPE: Derive Total time from prep + cook + rest rather than asking the creator to type it, in the timing
fields R.9 moves into the General tab.
CONSTRAINT: CLAUDE.md and .claude/rules/ai.md both require deterministic domain code for time arithmetic —
a sum in code, never a model's estimate. .claude/rules/recipes.md requires prep, cook, rest and total
modelled explicitly. totalTimeMinutes is a settable field on the create/update contract
(recipe.models.ts:135, 203), so state and justify whether the value is derived in the browser and
submitted, or computed server-side.
RESTRICTION: Decide explicitly what happens when a creator has already typed a total that disagrees with
the sum, and when some components are blank — a recipe with only a cook time must not silently report a
total of 0. If the field becomes derived, show what it was derived from rather than presenting an
unexplained number. An existing recipe's manually entered total must not be overwritten on load.
BEHAVIOR: Show the calculation rule, the override behavior and the blank-component behavior, wait for
approval, implement with tests covering all-blank, partial, exact-sum and conflicting-manual-total cases,
and report.
```

## R.19 Make the Tools area's value explicit - done

```text
SCOPE: Two of the Tools calculations already write back to the recipe — recipe-temperature-conversion and
recipe-yield-reconciliation each emit applyRequested, which RecipeEditorComponent applies — while
recipe-scaling-preview, recipe-unit-conversion and recipe-display-normalization are read-only with no way
to act on what they show. Close that gap: give the read-only three an apply path, or say plainly in each
panel that it is a reference calculation the creator applies by hand. No panel should be a dead end of
unexplained numbers.
CONSTRAINT: .claude/rules/recipes.md — "a scaled recipe is a proposal until accepted or saved as a
version"; the existing apply pipeline in RecipeEditorComponent (onTemperatureApplyRequested,
onYieldApplyRequested) with its confirmation, idempotency key and version reporting, which any new apply
path reuses rather than reimplements; the editorIsDirty contract that makes a calculation refuse to run
against unsaved edits.
RESTRICTION: No calculation writes to the recipe without an explicit confirmation. Do not change what the
server computes — these panels render server-computed previews and must keep doing so; this is about what a
creator can do with the result. Named RecipeVersion records stay immutable.
BEHAVIOR: Show, per panel, whether it gains an apply path or an explanation, and why, wait for approval,
implement with tests proving each apply goes through the existing confirmation and idempotency path and
that a declined confirmation writes nothing, and report.
```

## R.20 Make History readable - done

```text
SCOPE: Rework the History area's presentation. Audit recipe-history.component and the comparison and
restore surfaces it hosts, then decide what a version row must say, how two versions are compared, and how
a restore is confirmed.
CONSTRAINT: .claude/rules/design-system.md; CpListShellComponent for the version list frame and
CpDiffLegendComponent for the comparison legend — both exist for exactly this and should be used rather
than hand-rolled; the accessibility checklist in DESIGN-SYSTEM.md, since a dense diff is where colour-only
status and unreadable density creep in.
RESTRICTION: Do not change what restore, compare or duplicate do. The restore pipeline — onVersionRestored,
its concurrency-token handling and its "no new version was written" case — and the immutability of named
versions stay exactly as they are. Do not drop information from the list to make it look tidier: state what
each row must carry and keep it.
BEHAVIOR: Show the current information inventory and the reworked layout in prose, wait for approval,
implement with component and accessibility tests including a long-history case and a two-version
comparison, and report before/after descriptions of the density and reading order.
```

## R.21 Recipes UX verification - done

```text
SCOPE: Re-verify the full recipe journey end to end: browse/search/filter in the library, create a
recipe on a wide viewport and a narrow one, edit ingredients and instructions in the new layout, save,
reload and confirm ingredients persisted, trigger a validation error on a field outside the current
viewport, view history, compare two versions, restore an old version, duplicate a recipe — confirming
each of R.1-R.20's fixes holds together rather than only in isolation.
CONSTRAINT: the verification checklist in .claude/rules/design-system.md and the Verification section of
.claude/rules/frontend.md; .claude/rules/recipes.md invariants (immutable versions, preserved entered
text, scaling/AI edits as proposals) must still hold after this pass.
UTILIZATION: design-review agent (read-only) re-run against the same journeys the original audit covered;
test-gap-analyzer agent (read-only) against the new ingredient-save and layout/validation-visibility
behavior.
RESTRICTION: Assert behavior through real routing/guards and the real save pipeline, not by bypassing
them with direct component/state injection.
BEHAVIOR: Run design-review and test-gap-analyzer, fix only findings within this document's scope, run
npm build and npm test from src/web/, and report before/after proof for the data-loss fix in R.1-R.2, the
layout change in R.3-R.4, and a pass/fail line for every other prompt in this document.
```

## R.22 Name the ingredient line or step a refusal is about

```text
SCOPE: Make a refused save say which line. Both recipe validators state every ingredient rule and every
instruction rule as one chained RuleFor with CascadeMode.Stop and OverridePropertyName to the list itself,
so a refusal arrives as a single sentence under the flat key `ingredientGroups` or `instructions` with no
position in it — "A quantity must be greater than zero." about a recipe with forty lines. Split each chain
into the rules that are genuinely about the whole list (totals, repeated ids) and the rules that are about
one group or one line, and have the second kind emit an indexed key the client can resolve to a row. Then
show the sentence on that row in the ingredient and instruction editors, beside the section-level message
R.21 added rather than instead of it.
CONSTRAINT: .claude/rules/api-contract.md — a ValidationProblemDetails `errors` dictionary is free-form, so
adding keys is additive, but the existing flat key must keep arriving for list-level problems rather than
being replaced; .claude/rules/backend.md, which keeps shape validation at the edge in FluentValidation and
domain invariants in Business; the four rule blocks are
CreateRecipeViewModelValidator.cs:138 and its Instructions block, and
UpdateRecipeViewModelValidator.cs:146 and :183.
Two traps, both already paid for once: OperationResult.FieldErrors lowercases only the first character of a
key, so an emitted name must already be camelCase past its first segment to read as
`ingredientGroups[0].ingredients[2].quantity` on the wire. And the validators' own AllLines/Lines/Groups
helpers filter with OfType<T>(), which discards the submitted positions an index has to refer to — a
per-line pass has to walk the arrays by index instead.
RESTRICTION: Do not change any validation rule's meaning, its wording, or which requests are refused; this
is about where a refusal is reported, not what is refused. Keep the flat key for the rules that have no one
line to blame, and keep RecipePolicy as the only home for the limits. Do not reimplement any rule in the
browser — the client resolves a position the server sent and never decides for itself which line is at
fault. The client also filters blank lines out of the request, so submitted position and working-copy row
are not the same thing: record the mapping when the request is built rather than assuming they match.
UTILIZATION: the add-recipe-feature skill for the domain seam; api-contract-checker (read-only) on the
error-shape change.
BEHAVIOR: Show which rules move to an indexed key and which stay flat, the exact key format, and how the
client maps a position back to a row, wait for approval, implement with backend tests proving the new keys
for a per-line rule and the unchanged flat key for a list-level one, and frontend tests proving a refusal
about one line marks that row and no other, then report.
```
