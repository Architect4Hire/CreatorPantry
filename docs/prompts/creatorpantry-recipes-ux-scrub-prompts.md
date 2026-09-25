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

## R.1 Recipe ingredient groups on the create/update contract

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

## R.2 Wire the ingredient editor into save, dirty tracking, and navigation guards

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

## R.3 Recipe editor layout and information architecture — plan

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

## R.4 Recipe editor layout and information architecture — implementation

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

## R.5 Ingredient paste-review: group targeting, bulk accept, and hint accessibility

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

## R.6 Disabled-section explanation in create mode

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

## R.7 Keep "New recipe" available during a library error state

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

## R.8 Recipes UX verification

```text
SCOPE: Re-verify the full recipe journey end to end: browse/search/filter in the library, create a
recipe on a wide viewport and a narrow one, edit ingredients and instructions in the new layout, save,
reload and confirm ingredients persisted, trigger a validation error on a field outside the current
viewport, view history, compare two versions, restore an old version, duplicate a recipe — confirming
each of R.1-R.8's fixes holds together rather than only in isolation.
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
