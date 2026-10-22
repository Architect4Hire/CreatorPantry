// Types and helpers shared by the recipe editor, its form state and the section components that render it.

/**
 * The editor's own working copy of one instruction step — a `key` stable across reorders for
 * `@for` tracking (the server `id`, when there is one, doubles as it; a brand-new step has none yet).
 * Technique and temperature are read-only fields elsewhere in the app today (this editor has a vocabulary
 * picker for the yield unit, but none for a step's technique, nor for cuisine or course),
 * so this form edits only what a plain text/number field can: the step's prose, its note, and how
 * long it takes.
 */
export interface EditableInstructionStep {
  readonly key: string;
  id: string | null;
  text: string;
  note: string;
  durationMinutes: number | null;

  /**
   * Whether this step's duration field is on show. UI only — it is never submitted and never part of the
   * dirty-state snapshot, both of which are built from `buildInstructions()`, so revealing a field a creator
   * then leaves empty is not an edit to the recipe.
   *
   * Most steps have no duration, and a number box on every one of a dozen reads as something the creator is
   * expected to fill in. So a step starts without it and asks for it, except where the recipe already records
   * one: a stored duration is shown without the creator having to go looking for it.
   *
   * Once on, it stays on for the life of the form even if the creator empties the box — a field vanishing out
   * from under the cursor mid-edit is worse than an empty one. It is recomputed from the recipe on the next
   * load, save or restore, all of which go through `applyDetail`.
   */
  showDuration: boolean;

  // Carried, never edited. `Instructions` is a full replace and the server applies a submitted step
  // wholesale, so a field this form omits is a field the next save sets to null — which silently destroyed
  // every step's technique and recorded temperature. These are round-tripped so that editing a step's prose
  // leaves the rest of what the recipe knows about it alone.
  techniqueId: string | null;
  temperatureValue: number | null;
  temperatureUnitId: string | null;
}

export interface EditableInstructionGroup {
  readonly key: string;
  id: string | null;
  title: string;
  steps: EditableInstructionStep[];
}

/** Swaps the item named by `key` with its neighbour in `direction`; a no-op at either end of the list. */
export function moveByKey<T extends { readonly key: string }>(items: readonly T[], key: string, direction: -1 | 1): T[] {
  const index = items.findIndex((item) => item.key === key);
  const target = index + direction;
  if (index === -1 || target < 0 || target >= items.length) return [...items];

  const next = [...items];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

/**
 * `RecipePolicy.MaxTimeMinutes` — one year, the most the server accepts for any one time field.
 *
 * Mirrored because three times each inside the limit can add to a sum outside it, and the total is derived:
 * a sum past the limit has to be reported rather than recorded, because it would come straight back as a
 * validation error on a field the creator has no way to correct.
 */
export const MAX_TIME_MINUTES = 525_600;

/**
 * What prep, cook and rest add up to, and which of them were counted.
 *
 * `parts` exists because the number alone explains nothing: a recipe with only a cook time would otherwise
 * show a total of 45 that appears to come from nowhere. Naming what went into it is what the sum is offered
 * *with*, never instead of.
 *
 * A time of 0 counts — the creator recorded it. Only an absent one is left out, which is why all three blank
 * produces no sum at all rather than a total of 0.
 */
export interface TimingSum {
  readonly minutes: number;
  readonly parts: readonly string[];
}

/**
 * The saved/loaded baseline the form is diffed against for dirty-state. Covers every field that
 * actually gets submitted, `ingredientGroups` included — `newTagText` (the not-yet-committed tag
 * input) stays out deliberately: diffing it against a baseline would let a post-save baseline reset
 * silently swallow real unsaved text that was never part of the request in the first place. It's
 * checked live in `isDirty` instead.
 */
export interface RecipeFormSnapshot {
  readonly title: string;
  readonly description: string;
  readonly headnote: string;
  readonly attributionText: string;
  readonly sourceUrl: string;
  readonly notes: string;
  readonly storageNotes: string;
  readonly prepTimeMinutes: number | null;
  readonly cookTimeMinutes: number | null;
  readonly restTimeMinutes: number | null;
  readonly yieldText: string;
  readonly yieldQuantity: number | null;
  readonly yieldUnitId: string | null;
  readonly servingCount: number | null;
  readonly servingSize: number | null;
  readonly tags: readonly string[];
  readonly instructionsJson: string;
  readonly ingredientGroupsJson: string;
}
