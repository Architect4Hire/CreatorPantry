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

  /**
   * Whether the note field is showing, on the same terms as {@link showDuration}: offered rather than presented,
   * because most steps have no note and an empty box on every one reads as something to fill in. A note the
   * recipe already records is shown; once on, it stays on for the life of the form.
   */
  showNote: boolean;

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
export function moveByKey<T extends { readonly key: string }>(
  items: readonly T[],
  key: string,
  direction: -1 | 1,
): T[] {
  const index = items.findIndex((item) => item.key === key);
  const target = index + direction;
  if (index === -1 || target < 0 || target >= items.length) return [...items];

  const next = [...items];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

/** A place in a grouped list: the group, and the position in it the item ends up at. */
export interface GroupedPlace {
  readonly groupKey: string;
  readonly index: number;
}

/**
 * Where a key pressed on a line's move handle asks to take it, or null for any other key. Up and Down move it one
 * place; Home and End to the top or the bottom of its group. A key with a modifier is left to the browser.
 */
export type HandleMove = 'up' | 'down' | 'first' | 'last';

export function handleMoveFor(event: KeyboardEvent): HandleMove | null {
  if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return null;
  switch (event.key) {
    case 'ArrowUp':
      return 'up';
    case 'ArrowDown':
      return 'down';
    case 'Home':
      return 'first';
    case 'End':
      return 'last';
    default:
      return null;
  }
}

/**
 * Where a handle's key takes the item named by `itemKey`, or null when it is already there. Up from the top of a group
 * goes to the end of the group above, and Down from the bottom to the start of the group below, when `acrossGroups`
 * — a recipe the creator split into sections, where moving a line between them is a move like any other. Home and
 * End stay in the item's own group.
 */
export function handleTarget<G extends { readonly key: string }>(
  groups: readonly G[],
  itemsOf: (group: G) => readonly { readonly key: string }[],
  itemKey: string,
  move: HandleMove,
  acrossGroups: boolean,
): GroupedPlace | null {
  const groupIndex = groups.findIndex((group) => itemsOf(group).some((item) => item.key === itemKey));
  if (groupIndex === -1) return null;
  const group = groups[groupIndex];
  const items = itemsOf(group);
  const index = items.findIndex((item) => item.key === itemKey);
  const last = items.length - 1;

  switch (move) {
    case 'first':
      return index === 0 ? null : { groupKey: group.key, index: 0 };
    case 'last':
      return index === last ? null : { groupKey: group.key, index: last };
    case 'up':
      if (index > 0) return { groupKey: group.key, index: index - 1 };
      if (!acrossGroups || groupIndex === 0) return null;
      return { groupKey: groups[groupIndex - 1].key, index: itemsOf(groups[groupIndex - 1]).length };
    case 'down':
      if (index < last) return { groupKey: group.key, index: index + 1 };
      if (!acrossGroups || groupIndex === groups.length - 1) return null;
      return { groupKey: groups[groupIndex + 1].key, index: 0 };
  }
}

/**
 * Takes the item named by `itemKey` out of whichever group holds it and puts it at `to` — the position it holds once
 * moved, which is what both a drop and a handle's key give. The groups and every other item keep their order; an item
 * or a group that is not there leaves the list as it was.
 */
export function moveToPlace<G extends { readonly key: string }, I extends { readonly key: string }>(
  groups: readonly G[],
  itemsOf: (group: G) => readonly I[],
  withItems: (group: G, items: I[]) => G,
  itemKey: string,
  to: GroupedPlace,
): G[] {
  const from = groups.find((group) => itemsOf(group).some((item) => item.key === itemKey));
  const item = from && itemsOf(from).find((candidate) => candidate.key === itemKey);
  if (!from || !item || !groups.some((group) => group.key === to.groupKey)) return [...groups];

  const without = groups.map((group) =>
    group === from
      ? withItems(
          group,
          itemsOf(group).filter((candidate) => candidate.key !== itemKey),
        )
      : group,
  );
  return without.map((group) => {
    if (group.key !== to.groupKey) return group;
    const items = [...itemsOf(group)];
    items.splice(Math.max(0, Math.min(to.index, items.length)), 0, item);
    return withItems(group, items);
  });
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
