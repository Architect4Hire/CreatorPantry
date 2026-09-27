import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Subject, debounceTime, map, switchMap } from 'rxjs';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpCheckboxComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpDialogComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { IngredientScaling, RecipeIngredientGroup } from '../../models/recipe.models';
import { IngredientMatchCandidate, ParsedIngredientLine, UnitMatchCandidate } from '../../models/ingredient-parse.models';
import { Ingredient } from '../../models/reference.models';
import { INGREDIENT_SEARCH_MIN_LENGTH, ReferenceService, SearchIngredientsOutcome } from '../../services/reference.service';
import { IngredientLinesAccepted, IngredientPasteReviewComponent, PasteTargetGroup } from './ingredient-paste-review.component';
import { IngredientUnitPickerComponent } from './ingredient-unit-picker.component';
import { UnitCatalogueState } from './recipe-unit-options';

/** Where a row's ingredient/unit stand relative to the shared catalogue — a UI-only summary, never sent as-is. */
export type IngredientRowMatchState = 'matched' | 'ambiguous' | 'unmatched' | 'unreviewed';

/**
 * The editor's own working copy of one ingredient line — a `key` stable across reorders for `@for`
 * tracking (the server `id`, when there is one, doubles as it; a brand-new or just-accepted row has
 * none yet).
 *
 * `displayText` is the creator's own words, preserved verbatim (recipes.md) — nothing here ever rewrites a
 * line the creator wrote. A row added by hand is the one case where nobody has written one yet: this editor
 * offers no control for it, so `displayTextIsComposed` marks the row as still assembling its line from the
 * fields it does offer. See `composeDisplayText`.
 *
 * `quantityText` is edited as free text rather than as a numeric/fraction widget: the tokenizer's exact
 * parsed value is shown alongside as a read-only annotation, not replaced by an editor that would have to
 * reproduce its own fraction arithmetic.
 */
export interface EditableIngredientRow {
  readonly key: string;
  id: string | null;
  displayText: string;
  /**
   * Whether `displayText` is still being assembled from this row's other fields rather than being text the
   * creator owns. True only for a row added by hand and not yet given a line of its own; a pasted row, a row
   * loaded from the server, and any row whose `displayText` is patched directly are all false, and their text
   * is never rewritten.
   */
  displayTextIsComposed: boolean;
  ingredientNameText: string;
  quantityText: string;

  /**
   * The upper bound of a recorded range, carried and never edited.
   *
   * There is no control for it: a range is something a recipe already holds ("2–3 cups"), and this editor
   * offers one quantity field. But `IngredientGroups` is a full replace and the server applies a submitted
   * line wholesale, so a row that does not carry this is a row whose next save clears it — a recipe losing its
   * range because the creator renamed the title. Cleared only when the quantity itself is retyped, because
   * "2–3" edited to "5" is not 5–3 (see `patchRow`).
   */
  quantityUpper: number | null;
  detectedQuantityText: string | null;
  unitId: string | null;
  unitLabel: string;
  ingredientId: string | null;
  matchState: IngredientRowMatchState;
  preparationNote: string;
  isOptional: boolean;
  scalingBehavior: IngredientScaling;
  /** Populated only right after a parse-accept that left the match unresolved/ambiguous, for the confirmation picker below. */
  ingredientCandidates: readonly IngredientMatchCandidate[];
  unitCandidates: readonly UnitMatchCandidate[];
}

export interface EditableIngredientGroup {
  readonly key: string;
  id: string | null;
  title: string;
  ingredients: EditableIngredientRow[];
}

/** The id prefix of a group's own anchor — what `CpAnchorNavComponent` jumps to, and what it comes back as. */
const GROUP_ANCHOR_PREFIX = 'ingredient-group-';

/**
 * How long the ingredient search waits for the typing to stop.
 *
 * The ingredient catalogue is far too large to hold client-side, so every keystroke is a potential request.
 * Long enough that an ordinary word costs one request instead of six; short enough that the list still feels
 * like it is answering the typing.
 */
export const INGREDIENT_SEARCH_DEBOUNCE_MS = 250;

/**
 * "Nothing to offer", as one shared value.
 *
 * A fresh `[]` per change detection would be a new `options` identity for every row's popup on every pass,
 * which rebuilds lists nobody touched.
 */
const NO_OPTIONS: readonly CpComboboxOption[] = [];

/**
 * The one ingredient search, and which row asked for it.
 *
 * One search rather than one per row, because one box has focus at a time. A row only ever sees results
 * tagged with its own key, so a list fetched for the line above can never be offered as this line's matches.
 */
export type IngredientSearchState =
  | { readonly status: 'idle'; readonly rowKey: null }
  | { readonly status: 'searching'; readonly rowKey: string }
  | { readonly status: 'tooShort'; readonly rowKey: string }
  | { readonly status: 'unavailable'; readonly rowKey: string }
  | {
      readonly status: 'found';
      readonly rowKey: string;
      readonly ingredients: readonly Ingredient[];
      readonly hasMore: boolean;
    };

/** The state one search outcome leaves the row that asked for it in. */
function ingredientSearchState(rowKey: string, outcome: SearchIngredientsOutcome): IngredientSearchState {
  switch (outcome.status) {
    case 'found':
      return { status: 'found', rowKey, ingredients: outcome.ingredients, hasMore: outcome.hasMore };
    case 'tooShort':
      return { status: 'tooShort', rowKey };
    default:
      return { status: 'unavailable', rowKey };
  }
}

/**
 * One catalogue ingredient as a combobox row.
 *
 * The aliases are shown because they are *why* the search matched: "scallion" answering with green onion is
 * otherwise indistinguishable from a bug.
 */
function ingredientOption(ingredient: Ingredient): CpComboboxOption {
  return {
    id: ingredient.id,
    label: ingredient.canonicalName,
    detail: ingredient.aliases.length === 0 ? undefined : `also ${ingredient.aliases.join(', ')}`,
  };
}

/**
 * What to call a group in a list of them: the creator's own heading, or its position when it has none.
 *
 * Shared by the group nav and the paste dialog's destination selector, so the two cannot come to call the same
 * group different things.
 */
function groupLabel(group: EditableIngredientGroup, index: number): string {
  return group.title.trim() || `Group ${index + 1} (untitled)`;
}

/**
 * Whether these groups are sections the creator actually has, rather than the one implicit list an ungrouped
 * recipe is presented as.
 *
 * More than one group counts even when both are untitled: a creator who asked for two lists has sections,
 * whatever they have called them so far.
 */
function hasNamedGroups(groups: readonly EditableIngredientGroup[]): boolean {
  return groups.length > 1 || groups.some((group) => group.title.trim().length > 0);
}

/** Swaps the item named by `key` with its neighbour in `direction`; a no-op at either end of the list. */
function moveByKey<T extends { readonly key: string }>(items: readonly T[], key: string, direction: -1 | 1): T[] {
  const index = items.findIndex((item) => item.key === key);
  const target = index + direction;
  if (index === -1 || target < 0 || target >= items.length) return [...items];

  const next = [...items];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

function blankRow(): EditableIngredientRow {
  return {
    key: crypto.randomUUID(),
    id: null,
    displayText: '',
    displayTextIsComposed: true,
    ingredientNameText: '',
    quantityText: '',
    quantityUpper: null,
    detectedQuantityText: null,
    unitId: null,
    unitLabel: '',
    ingredientId: null,
    matchState: 'unreviewed',
    preparationNote: '',
    isOptional: false,
    scalingBehavior: 'Proportional',
    ingredientCandidates: [],
    unitCandidates: [],
  };
}

/**
 * The line a hand-entered row will be saved as, assembled from the only fields this editor lets a creator
 * type into.
 *
 * A row added with "Add ingredient" has no `displayText` control of its own, so without this its line would
 * stay empty and `RecipeEditorComponent` would drop the row from the request — the whole line lost although
 * every visible field was filled in. It matters more than a fallback, too: `IngredientInput` carries no
 * free-text unit or ingredient name, so for a hand-entered line this text is the only place the creator's own
 * wording of either one survives at all.
 *
 * Assembled from the creator's own words and nothing else — it reads no vocabulary and invents no wording —
 * and only ever for a row that has none of its own (see `displayTextIsComposed`). A row with nothing filled
 * in composes to an empty string, which is what still keeps an abandoned blank row out of a save.
 */
export function composeDisplayText(
  row: Pick<EditableIngredientRow, 'quantityText' | 'unitLabel' | 'ingredientNameText' | 'preparationNote'>,
): string {
  const amount = [row.quantityText, row.unitLabel, row.ingredientNameText]
    .map((part) => part.trim())
    .filter((part) => part.length > 0)
    .join(' ');
  const preparation = row.preparationNote.trim();

  if (amount.length === 0) return preparation;

  return preparation.length === 0 ? amount : `${amount}, ${preparation}`;
}

/**
 * A pending choice for an ambiguous/unresolved match before the line is accepted. `undefined` means "use
 * whatever the parse resolved" (or leave pending review, if nothing did); `null` is the creator's explicit
 * "leave unmatched"; a candidate is the creator's explicit pick among the alternates.
 */
export interface IngredientRowMatchOverrides {
  readonly ingredient?: IngredientMatchCandidate | null;
  readonly unit?: UnitMatchCandidate | null;
}

/** Builds a proposal row from one parsed line — a value the creator can still edit or discard, never a fact. */
export function rowFromParsedLine(line: ParsedIngredientLine, overrides: IngredientRowMatchOverrides = {}): EditableIngredientRow {
  const quantity = line.tokens.quantity;
  const ingredientMatch = line.ingredientMatch;
  const unitMatch = line.unitMatch;

  const ingredientDecided = overrides.ingredient !== undefined;
  const ingredientResolved = ingredientDecided ? overrides.ingredient : (ingredientMatch?.resolved ?? null);
  const unitDecided = overrides.unit !== undefined;
  const unitResolved = unitDecided ? overrides.unit : (unitMatch?.resolved ?? null);

  const ingredientNeedsReview = !ingredientDecided && ingredientMatch !== null && ingredientResolved === null;
  const unitNeedsReview = !unitDecided && unitMatch !== null && unitResolved === null;

  let matchState: IngredientRowMatchState = 'unreviewed';
  if (ingredientMatch !== null || unitMatch !== null) {
    if (ingredientNeedsReview || unitNeedsReview) {
      matchState = 'ambiguous';
    } else if ((ingredientDecided && ingredientResolved === null) || (unitDecided && unitResolved === null)) {
      matchState = 'unmatched';
    } else {
      matchState = 'matched';
    }
  }

  return {
    key: crypto.randomUUID(),
    id: null,
    displayText: line.tokens.originalText,
    // The pasted line is the creator's own text; editing the parsed reading beside it never rewrites it.
    displayTextIsComposed: false,
    ingredientNameText: ingredientResolved?.canonicalName ?? line.tokens.ingredientText?.text ?? '',
    quantityText: quantity?.span.text ?? '',
    // The parser can report a range, but its bounds are exact fractions and this editor does no fraction
    // arithmetic (see `parseQuantity`). A pasted range keeps its wording in `displayText`; only the structured
    // upper bound goes unrecorded, exactly as the lower one is best-effort.
    quantityUpper: null,
    detectedQuantityText: quantity && !quantity.isInvalid ? quantity.span.text : null,
    unitId: unitResolved?.measurementUnitId ?? null,
    unitLabel: unitResolved?.displayName ?? line.tokens.unitCandidate?.text ?? '',
    ingredientId: ingredientResolved?.ingredientId ?? null,
    matchState,
    preparationNote: line.tokens.preparationText.map((span) => span.text).join(', '),
    isOptional: line.tokens.isOptional,
    scalingBehavior: 'Proportional',
    ingredientCandidates: ingredientNeedsReview ? (ingredientMatch?.alternates ?? []) : [],
    unitCandidates: unitNeedsReview ? (unitMatch?.alternates ?? []) : [],
  };
}

/** Applies one edit to a row, then recomposes its line if the row is still composing one. */
function patchRow(row: EditableIngredientRow, patch: Partial<Omit<EditableIngredientRow, 'key' | 'id'>>): EditableIngredientRow {
  const patched = { ...row, ...patch };

  // A retyped quantity is no longer the range the recipe recorded, and there is no control for the upper bound
  // to follow it with: "2–3" edited to "5" must not be submitted as 5–3. Dropped rather than guessed at.
  const ranged = 'quantityText' in patch && !('quantityUpper' in patch) ? { ...patched, quantityUpper: null } : patched;

  if ('displayText' in patch) return { ...ranged, displayTextIsComposed: false };

  return ranged.displayTextIsComposed ? { ...ranged, displayText: composeDisplayText(ranged) } : ranged;
}

/**
 * Converts server-shaped ingredient groups into this editor's working copy. Exported so
 * `RecipeEditorComponent` can build the same working copy directly (in `applyDetail`) rather than
 * waiting on this component's own `effect()` to resync from `initialGroups` — the parent needs its
 * mirror to be correct the instant a save/load applies, not on the next change-detection pass.
 */
export function toEditableGroups(groups: readonly RecipeIngredientGroup[]): EditableIngredientGroup[] {
  return groups.map((group) => ({
    key: group.id,
    id: group.id,
    title: group.title ?? '',
    ingredients: group.ingredients.map((ingredient) => ({
      key: ingredient.id,
      id: ingredient.id,
      displayText: ingredient.displayText,
      // Whether a stored line may be re-derived is the recipe's own record of it, not a guess made here: a
      // line that reads exactly like its own spans may still be one the creator wrote.
      displayTextIsComposed: ingredient.displayTextSource === 'Composed',
      ingredientNameText: ingredient.ingredientNameText ?? '',
      quantityText: ingredient.quantity === null ? '' : String(ingredient.quantity),
      quantityUpper: ingredient.quantityUpper,
      detectedQuantityText: null,
      unitId: ingredient.measurementUnitId,
      unitLabel: ingredient.unitText ?? '',
      ingredientId: ingredient.ingredientId,
      matchState: matchStateFromServer(ingredient.matchStatus),
      preparationNote: ingredient.preparationNote ?? '',
      isOptional: ingredient.isOptional,
      scalingBehavior: ingredient.scalingBehavior,
      ingredientCandidates: [],
      unitCandidates: [],
    })),
  }));
}

function matchStateFromServer(status: string): IngredientRowMatchState {
  switch (status) {
    case 'Matched':
      return 'matched';
    case 'NoMatch':
      return 'unmatched';
    case 'Ambiguous':
      return 'ambiguous';
    default:
      return 'unreviewed';
  }
}

export const SCALING_BEHAVIOR_OPTIONS: readonly { readonly value: IngredientScaling; readonly label: string }[] = [
  { value: 'Proportional', label: 'Scales with recipe' },
  { value: 'Fixed', label: 'Fixed amount (e.g. garnish)' },
  { value: 'ReviewRequired', label: 'Needs review when scaling' },
];

const MATCH_STATE_TONE: Record<IngredientRowMatchState, CpStatusPillTone> = {
  matched: 'success',
  ambiguous: 'warning',
  unmatched: 'warning',
  unreviewed: 'neutral',
};

const MATCH_STATE_LABEL: Record<IngredientRowMatchState, string> = {
  matched: 'Matched',
  ambiguous: 'Ambiguous — review needed',
  unmatched: 'No match — review needed',
  unreviewed: 'Not yet reviewed',
};

/**
 * The structured ingredient editor: groups and ingredient lines a creator can paste-and-parse, add, edit,
 * reorder and remove (ING-001, ING-002, 7.5). Parsed values are a proposal until explicitly confirmed —
 * accepting a parsed line turns it into an editable row here. This component owns editing only; it emits
 * `groupsChanged` on every edit and `RecipeEditorComponent` is what submits the result on Save, the same
 * way it already does for instructions.
 */
@Component({
  selector: 'cp-recipe-ingredient-editor',
  standalone: true,
  imports: [
    FormsModule,
    CpAnchorNavComponent,
    CpButtonComponent,
    CpCheckboxComponent,
    CpComboboxComponent,
    CpDialogComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpStatusPillComponent,
    IngredientPasteReviewComponent,
    IngredientUnitPickerComponent,
  ],
  templateUrl: './recipe-ingredient-editor.component.html',
  styleUrl: './recipe-ingredient-editor.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeIngredientEditorComponent {
  readonly workspaceSlug = input.required<string>();
  readonly initialGroups = input<readonly RecipeIngredientGroup[]>([]);
  readonly groupsChanged = output<readonly EditableIngredientGroup[]>();

  /**
   * The server's sentences for a refused line, keyed by the row's own key.
   *
   * Resolved by the editor that owns the save, because only it knows which rows the request actually sent — a
   * row with no text is filtered out of it, so a submitted position is not a position in this list. Passed in
   * rather than looked up here: nothing in this component decides what is wrong with a line.
   */
  readonly rowErrors = input<ReadonlyMap<string, readonly string[]>>(new Map());

  /** The same for a refused group heading. */
  readonly groupErrors = input<ReadonlyMap<string, readonly string[]>>(new Map());

  readonly groups = signal<EditableIngredientGroup[]>([]);

  /**
   * Whether group headings are shown at all.
   *
   * An ingredient list has no sections until a creator says it does, so this starts false and a new recipe
   * takes lines straight away — the group they go into exists because the wire contract needs one, not because
   * the creator asked for it (recipes.md keeps their list theirs; an invented "Group 1" heading is the app
   * talking over them). Turned on by a recipe that arrives with sections, and by asking for one. Never turned
   * off: hiding a heading someone typed would misrepresent their recipe.
   */
  readonly showGroups = signal(false);

  /**
   * The groups, named and counted for the nav down the side.
   *
   * Every group is always rendered — the nav scrolls, it does not mount — so an edit in a group scrolled out
   * of view is in `groups()` like any other, and dirty-state and the save see it.
   */
  readonly groupNav = computed<readonly CpAnchorNavItem[]>(() =>
    this.groups().map((group, index) => ({
      targetId: GROUP_ANCHOR_PREFIX + group.key,
      label: groupLabel(group, index),
      detail: `${group.ingredients.length} ${group.ingredients.length === 1 ? 'line' : 'lines'}`,
    })),
  );

  /**
   * Shown only for a recipe that has sections to move between. One group is not navigation, and an ungrouped
   * recipe has nothing to navigate at all — it is the one flat list R.10 presents it as.
   */
  readonly showGroupNav = computed(() => this.showGroups() && this.groups().length > 1);

  /**
   * The group last jumped to, which `aria-current` names.
   *
   * Set on activation rather than watched with an `IntersectionObserver`: groups are added, removed and
   * reordered, so an observer would have to be re-registered on every change — a moving part with a
   * stale-observer failure mode, for a marker that is orientation only. The cost is that scrolling by hand
   * leaves this behind until the next jump.
   */
  readonly activeGroupKey = signal<string | null>(null);

  /** The same group as `activeGroupKey`, in the shared nav's terms. */
  readonly activeGroupTargetId = computed(() => {
    const key = this.activeGroupKey();

    return key === null ? null : GROUP_ANCHOR_PREFIX + key;
  });

  readonly pasteDialogOpen = signal(false);

  /** Which group the open paste dialog is sending lines to; `null` means "a new group at the end". */
  readonly pasteTargetGroupKey = signal<string | null>(null);

  /**
   * The paste dialog's group options. The label is built here because this component is what numbers groups —
   * an untitled group is the ordinary state right after "Add ingredient group", so it needs a name a creator
   * can pick out of a list rather than a blank line.
   */
  readonly pasteTargets = computed<readonly PasteTargetGroup[]>(() => {
    const grouped = this.showGroups();

    return this.groups().map((group, index) => ({
      key: group.key,
      // While there are no sections there is nothing to number, so the one destination is named for what it
      // is. "A new group at the end" stays on offer beside it: choosing it is asking for a section, and the
      // headings then appear because the creator asked, not because a paste guessed.
      label: grouped ? groupLabel(group, index) : group.title.trim() || 'The ingredient list',
    }));
  });

  /** Announces a reorder for screen-reader users — the up/down buttons themselves are silent otherwise. */
  readonly moveAnnouncement = signal('');

  protected readonly scalingOptions = SCALING_BEHAVIOR_OPTIONS;

  private readonly referenceService = inject(ReferenceService);

  /** The shared unit catalogue behind every row's unit picker. Read once; see `loadUnits`. */
  readonly unitCatalogue = signal<UnitCatalogueState>({ status: 'loading' });

  /** The one ingredient search in flight, and which row it belongs to. */
  readonly ingredientSearch = signal<IngredientSearchState>({ status: 'idle', rowKey: null });

  /** Terms on their way to the search. Debounced and cancelled by the pipeline in the constructor. */
  private readonly ingredientQuery = new Subject<{ readonly rowKey: string; readonly text: string }>();

  /** The current matches, built once rather than once per row that might be asked to render them. */
  private readonly ingredientOptions = computed<readonly CpComboboxOption[]>(() => {
    const search = this.ingredientSearch();

    return search.status === 'found' ? search.ingredients.map(ingredientOption) : NO_OPTIONS;
  });

  /**
   * What the ingredient search announces, in place of the library's own count.
   *
   * A search asks for the first page only, so a truncated list has to say so: "20 results" reads as "there are
   * 20" when what it means is "there are at least 20, and typing more is how you see the rest".
   */
  protected readonly ingredientResultsLabel = computed<(count: number) => string>(() => {
    const search = this.ingredientSearch();
    const hasMore = search.status === 'found' && search.hasMore;

    return (count) => {
      if (count === 0) return 'No results';
      const results = `${count} result${count === 1 ? '' : 's'}`;

      return hasMore ? `${results}, more available — keep typing to narrow` : results;
    };
  });

  /**
   * The degraded shared-catalogue state, for the editor's existing live region.
   *
   * Announced from a region that is already in the DOM rather than from the visible notice: a live region that
   * appears at the same moment as its text is not reliably read, and the notice carries a Retry button, which
   * has no business being re-announced every time the region changes.
   */
  protected readonly referenceAnnouncement = computed(() =>
    this.unitCatalogue().status === 'unavailable'
      ? 'Unit list unavailable. Units you type are still saved as you wrote them.'
      : '',
  );

  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);

  constructor() {
    void this.loadUnits();

    // `debounceTime` is what makes this one request per pause rather than one per keystroke, and `switchMap` is
    // what cancels the request for a term the creator has already typed past — so a slow answer for "flo" can
    // never land on top of the answer for "flour".
    this.ingredientQuery
      .pipe(
        debounceTime(INGREDIENT_SEARCH_DEBOUNCE_MS),
        switchMap(({ rowKey, text }) =>
          this.referenceService.searchIngredients(text).pipe(map((outcome) => ({ rowKey, outcome }))),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ rowKey, outcome }) => this.ingredientSearch.set(ingredientSearchState(rowKey, outcome)));

    effect(() => {
      const groups = toEditableGroups(this.initialGroups());
      this.groups.set(groups);
      if (hasNamedGroups(groups)) this.showGroups.set(true);
    });
  }

  /**
   * Jumps to the group the nav named: marks it current, scrolls it into view, and moves focus to its heading
   * field.
   *
   * `CpAnchorNavComponent` deliberately does neither of the last two, because only this editor knows which of
   * its controls deserves the focus. Focus moves rather than only the viewport: a nav that scrolls and leaves
   * focus behind strands a keyboard user, whose next Tab goes to the next nav item instead of into the group
   * they just asked for. The heading field is the first control in the group and takes the ordinary visible
   * focus ring.
   */
  onGroupNavActivated(item: CpAnchorNavItem): void {
    const groupKey = item.targetId.slice(GROUP_ANCHOR_PREFIX.length);
    this.activeGroupKey.set(groupKey);

    const host = this.elementRef.nativeElement;
    host.querySelector(`#${item.targetId}`)?.scrollIntoView({ block: 'start' });
    host.querySelector<HTMLElement>(`#ingredient-group-title-${groupKey}`)?.focus();
  }

  /** One line's problems as a sentence, since a line can be wrong in more than one way at once. */
  rowProblem(rowKey: string): string {
    return this.rowErrors().get(rowKey)?.join(' ') ?? '';
  }

  groupProblem(groupKey: string): string {
    return this.groupErrors().get(groupKey)?.join(' ') ?? '';
  }

  toneFor(state: IngredientRowMatchState): CpStatusPillTone {
    return MATCH_STATE_TONE[state];
  }

  labelFor(state: IngredientRowMatchState): string {
    return MATCH_STATE_LABEL[state];
  }

  /**
   * Opens the paste dialog with the first existing group preselected — which is where a paste has always
   * landed, so a creator who ignores the selector sees no change. With no groups yet the target is `null`,
   * and accepting anything creates the one group it needs.
   */
  openPasteDialog(): void {
    this.pasteTargetGroupKey.set(this.groups()[0]?.key ?? null);
    this.pasteDialogOpen.set(true);
  }

  closePasteDialog(): void {
    this.pasteDialogOpen.set(false);
  }

  /**
   * Adds an accepted batch to the group the creator chose in the paste dialog.
   *
   * `targetGroupKey: null` means "a new group at the end", and the key of the group created for it is written
   * straight back into `pasteTargetGroupKey` — the dialog's selector is bound two-way to it. Without that
   * write-back, every later accept in the same dialog would see `null` again and make another empty group.
   *
   * A key naming a group that no longer exists (it was removed while the dialog was open) falls back to the
   * same "make one" path rather than dropping the rows on the floor.
   */
  onLinesAccepted(event: IngredientLinesAccepted): void {
    const requested = event.targetGroupKey;
    const existing = requested === null ? undefined : this.groups().find((group) => group.key === requested);
    const targetGroupKey = existing?.key ?? this.addGroupInternal();

    if (targetGroupKey !== requested) this.pasteTargetGroupKey.set(targetGroupKey);

    this.groups.update((groups) =>
      groups.map((group) =>
        group.key === targetGroupKey ? { ...group, ingredients: [...group.ingredients, ...event.rows] } : group,
      ),
    );
    this.emitChange();
  }

  updateGroupTitle(groupKey: string, title: string): void {
    this.groups.update((groups) => groups.map((group) => (group.key === groupKey ? { ...group, title } : group)));
    this.emitChange();
  }

  /**
   * Applies `patch` to one row, keeping a hand-entered row's line in step with the fields the creator can
   * actually see (`composeDisplayText`). A row whose text the creator owns is never touched, and patching
   * `displayText` itself is what hands ownership over — so a later layout that adds a line control needs no
   * change here.
   */
  updateRow(groupKey: string, rowKey: string, patch: Partial<Omit<EditableIngredientRow, 'key' | 'id'>>): void {
    this.groups.update((groups) =>
      groups.map((group) =>
        group.key === groupKey
          ? { ...group, ingredients: group.ingredients.map((row) => (row.key === rowKey ? patchRow(row, patch) : row)) }
          : group,
      ),
    );
    this.emitChange();
  }

  /** Explicit choice, never an automatic one: a null candidate means the creator chose "leave unmatched". */
  confirmIngredientMatch(groupKey: string, rowKey: string, candidate: IngredientMatchCandidate | null): void {
    this.updateRow(groupKey, rowKey, {
      ingredientId: candidate?.ingredientId ?? null,
      ingredientNameText: candidate?.canonicalName ?? this.rowIn(groupKey, rowKey)?.ingredientNameText ?? '',
      matchState: candidate ? this.combinedMatchState(groupKey, rowKey, 'ingredient', true) : 'unmatched',
      ingredientCandidates: [],
    });
  }

  confirmUnitMatch(groupKey: string, rowKey: string, candidate: UnitMatchCandidate | null): void {
    this.updateRow(groupKey, rowKey, {
      unitId: candidate?.measurementUnitId ?? null,
      unitLabel: candidate?.displayName ?? this.rowIn(groupKey, rowKey)?.unitLabel ?? '',
      matchState: candidate ? this.combinedMatchState(groupKey, rowKey, 'unit', true) : 'unmatched',
      unitCandidates: [],
    });
  }

  /** Template-facing wrapper: resolves the picked `<option>` value back to the candidate object it named. */
  onIngredientChoiceSelected(groupKey: string, rowKey: string, ingredientId: string): void {
    const candidates = this.rowIn(groupKey, rowKey)?.ingredientCandidates ?? [];
    this.confirmIngredientMatch(groupKey, rowKey, candidates.find((candidate) => candidate.ingredientId === ingredientId) ?? null);
  }

  onUnitChoiceSelected(groupKey: string, rowKey: string, unitId: string): void {
    const candidates = this.rowIn(groupKey, rowKey)?.unitCandidates ?? [];
    this.confirmUnitMatch(groupKey, rowKey, candidates.find((candidate) => candidate.measurementUnitId === unitId) ?? null);
  }

  /**
   * Opts this list into sections, and appends an empty one to fill.
   *
   * Lines already entered stay exactly where they are — in the first group, which now shows the empty heading
   * field it always had — so nothing moves and nothing is lost. The new group is the section the creator is
   * about to write.
   */
  addGroup(): void {
    this.showGroups.set(true);
    this.addGroupInternal();
    this.emitChange();
  }

  removeGroup(groupKey: string): void {
    this.groups.update((groups) => groups.filter((group) => group.key !== groupKey));
    this.emitChange();
  }

  moveGroup(groupKey: string, direction: -1 | 1): void {
    const groups = this.groups();
    const index = groups.findIndex((group) => group.key === groupKey);
    this.groups.set(moveByKey(groups, groupKey, direction));
    this.announceMove('ingredient group', index, direction, groups.length);
    this.emitChange();
  }

  /**
   * Adds a blank line. With no `groupKey` — the ungrouped case, and the empty state's own action — it goes
   * into the one list, which is created here if this is the first line of a new recipe. That group is a wire
   * requirement (R.1: lines travel inside an IngredientGroupInput) and stays untitled, so it submits and comes
   * back as the untitled group it is, and the creator is never shown a heading they did not ask for.
   */
  addRow(groupKey?: string): void {
    const targetKey = groupKey ?? this.groups()[0]?.key ?? this.addGroupInternal();

    this.groups.update((groups) =>
      groups.map((group) => (group.key === targetKey ? { ...group, ingredients: [...group.ingredients, blankRow()] } : group)),
    );
    this.emitChange();
  }

  removeRow(groupKey: string, rowKey: string): void {
    this.groups.update((groups) =>
      groups.map((group) =>
        group.key === groupKey ? { ...group, ingredients: group.ingredients.filter((row) => row.key !== rowKey) } : group,
      ),
    );
    this.emitChange();
  }

  moveRow(groupKey: string, rowKey: string, direction: -1 | 1): void {
    const group = this.groups().find((candidate) => candidate.key === groupKey);
    const index = group?.ingredients.findIndex((row) => row.key === rowKey) ?? -1;
    this.groups.update((groups) =>
      groups.map((candidate) =>
        candidate.key === groupKey ? { ...candidate, ingredients: moveByKey(candidate.ingredients, rowKey, direction) } : candidate,
      ),
    );
    if (group) this.announceMove('ingredient', index, direction, group.ingredients.length);
    this.emitChange();
  }

  /**
   * Reads the shared unit catalogue for every row's unit picker, and is the Retry the degraded notice offers.
   *
   * One read for the whole editor: `ReferenceService` holds the catalogue for the session, and reference data
   * is global, so there is nothing per-row or per-workspace to vary. A catalogue that cannot be read never
   * blocks typing — see `IngredientUnitPickerComponent`.
   */
  async loadUnits(): Promise<void> {
    this.unitCatalogue.set({ status: 'loading' });
    const outcome = await this.referenceService.listUnits();
    this.unitCatalogue.set(
      outcome.status === 'found' ? { status: 'ready', units: outcome.units } : { status: 'unavailable' },
    );
  }

  /** Only the row being searched sees matches. Every other row's list is empty, never the row above's answers. */
  protected ingredientOptionsFor(rowKey: string): readonly CpComboboxOption[] {
    const search = this.ingredientSearch();

    return search.status === 'found' && search.rowKey === rowKey ? this.ingredientOptions() : NO_OPTIONS;
  }

  protected ingredientSelectionFor(row: EditableIngredientRow): CpComboboxOption | null {
    if (row.ingredientId === null) return null;

    return this.ingredientOptionsFor(row.key).find((option) => option.id === row.ingredientId) ?? null;
  }

  /**
   * The same distinction the unit picker draws, for a list that is a search rather than a catalogue: "no such
   * ingredient", "still searching" and "the search failed" must not all read as the first one.
   */
  protected ingredientEmptyTextFor(rowKey: string): string {
    const search = this.ingredientSearch();
    const keepTyping = `Type at least ${INGREDIENT_SEARCH_MIN_LENGTH} characters to search ingredients.`;
    if (search.rowKey !== rowKey) return keepTyping;

    switch (search.status) {
      case 'searching':
        return 'Searching…';
      case 'tooShort':
        return keepTyping;
      case 'unavailable':
        return 'Ingredient search is unavailable — what you type is still saved as you wrote it.';
      default:
        return 'No ingredient by that name — it will be saved as you wrote it.';
    }
  }

  /**
   * The creator typed in a unit box.
   *
   * Text that no longer names the matched unit gives up the match, through the same `confirmUnitMatch(null)`
   * that "leave unmatched" has always used — a `measurementUnitId` still pointing at the unit the line used to
   * say is worse than no id at all. If what they typed does name a unit exactly, the combobox says so in the
   * `selectedChange` that follows and `onUnitPicked` puts the id back.
   *
   * Text identical to what the row already holds is not an edit. The combobox re-states its value when the
   * field is left, and acting on that would mark the recipe dirty without anyone having changed anything.
   */
  onUnitTextChanged(groupKey: string, rowKey: string, text: string): void {
    const row = this.rowIn(groupKey, rowKey);
    if (row === null || row.unitLabel === text) return;

    this.updateRow(groupKey, rowKey, { unitLabel: text });
    if (row.unitId !== null) this.confirmUnitMatch(groupKey, rowKey, null);
  }

  /**
   * A unit the picker reports the creator chose.
   *
   * It never reports "none": the combobox recomputes its selection whenever the field is left, so that would
   * say nothing about intent. Giving a match up follows changed text, in `onUnitTextChanged` above.
   *
   * The candidate's `displayName` is the label the creator was shown, which is what becomes their wording for
   * the line — see `IngredientUnitPickerComponent.unitPicked`.
   */
  onUnitPicked(groupKey: string, rowKey: string, candidate: UnitMatchCandidate): void {
    const row = this.rowIn(groupKey, rowKey);
    if (row === null || row.unitId === candidate.measurementUnitId) return;

    this.confirmUnitMatch(groupKey, rowKey, candidate);
  }

  /** The unit rules above, plus the search this text is the term for. */
  onIngredientTextChanged(groupKey: string, rowKey: string, text: string): void {
    const row = this.rowIn(groupKey, rowKey);
    if (row === null || row.ingredientNameText === text) return;

    this.updateRow(groupKey, rowKey, { ingredientNameText: text });
    if (row.ingredientId !== null) this.confirmIngredientMatch(groupKey, rowKey, null);
    this.queryIngredients(rowKey, text);
  }

  onIngredientOptionPicked(groupKey: string, rowKey: string, option: CpComboboxOption | null): void {
    const row = this.rowIn(groupKey, rowKey);
    if (option === null || row === null || row.ingredientId === option.id) return;

    this.confirmIngredientMatch(groupKey, rowKey, {
      ingredientId: option.id,
      canonicalName: option.label,
      kind: 'CanonicalName',
    });
  }

  /**
   * Asks for the ingredients matching `text`, on behalf of the row being typed into.
   *
   * Every term is pushed, including one too short to search: `debounceTime` keeps only the last value of a
   * quiet window, so pushing the short term is itself what abandons the request the longer one would have
   * made. The state is also set here, synchronously, so a popup stops offering matches for a term the creator
   * has already typed past while the debounce is still counting.
   */
  private queryIngredients(rowKey: string, text: string): void {
    const term = text.trim();
    this.ingredientSearch.set(
      term.length < INGREDIENT_SEARCH_MIN_LENGTH ? { status: 'tooShort', rowKey } : { status: 'searching', rowKey },
    );
    this.ingredientQuery.next({ rowKey, text: term });
  }

  private addGroupInternal(): string {
    const key = crypto.randomUUID();
    this.groups.update((groups) => [...groups, { key, id: null, title: '', ingredients: [] }]);
    return key;
  }

  private rowIn(groupKey: string, rowKey: string): EditableIngredientRow | null {
    return this.groups().find((group) => group.key === groupKey)?.ingredients.find((row) => row.key === rowKey) ?? null;
  }

  /** Whichever of ingredient/unit is still pending review after this confirmation keeps the row ambiguous. */
  private combinedMatchState(groupKey: string, rowKey: string, justConfirmed: 'ingredient' | 'unit', confirmedToMatch: boolean): IngredientRowMatchState {
    if (!confirmedToMatch) return 'unmatched';
    const row = this.rowIn(groupKey, rowKey);
    const otherStillPending = justConfirmed === 'ingredient' ? (row?.unitCandidates.length ?? 0) > 0 : (row?.ingredientCandidates.length ?? 0) > 0;
    return otherStillPending ? 'ambiguous' : 'matched';
  }

  private announceMove(subject: string, fromIndex: number, direction: -1 | 1, total: number): void {
    if (fromIndex < 0) return;
    const toPosition = fromIndex + direction + 1;
    this.moveAnnouncement.set(`Moved ${subject} to position ${toPosition} of ${total}.`);
  }

  private emitChange(): void {
    this.groupsChanged.emit(this.groups());
  }
}
