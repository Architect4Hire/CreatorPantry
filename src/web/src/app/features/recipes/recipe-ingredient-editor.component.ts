import { ChangeDetectionStrategy, Component, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpDialogComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { IngredientScaling, RecipeIngredientGroup } from '../../models/recipe.models';
import { IngredientMatchCandidate, ParsedIngredientLine, UnitMatchCandidate } from '../../models/ingredient-parse.models';
import { IngredientLinesAccepted, IngredientPasteReviewComponent } from './ingredient-paste-review.component';

/** Where a row's ingredient/unit stand relative to the shared catalogue — a UI-only summary, never sent as-is. */
export type IngredientRowMatchState = 'matched' | 'ambiguous' | 'unmatched' | 'unreviewed';

/**
 * The editor's own working copy of one ingredient line — a `key` stable across reorders for `@for`
 * tracking (the server `id`, when there is one, doubles as it; a brand-new or just-accepted row has
 * none yet).
 *
 * `displayText` is the creator's own words, preserved verbatim (recipes.md) — nothing here ever rewrites
 * it. `quantityText` is likewise edited as free text rather than as a numeric/fraction widget: the
 * tokenizer's exact parsed value is shown alongside as a read-only annotation, not replaced by an editor
 * that would have to reproduce its own fraction arithmetic.
 */
export interface EditableIngredientRow {
  readonly key: string;
  id: string | null;
  displayText: string;
  ingredientNameText: string;
  quantityText: string;
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
    ingredientNameText: '',
    quantityText: '',
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
    ingredientNameText: ingredientResolved?.canonicalName ?? line.tokens.ingredientText?.text ?? '',
    quantityText: quantity?.span.text ?? '',
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

function toEditableGroups(groups: readonly RecipeIngredientGroup[]): EditableIngredientGroup[] {
  return groups.map((group) => ({
    key: group.id,
    id: group.id,
    title: group.title ?? '',
    ingredients: group.ingredients.map((ingredient) => ({
      key: ingredient.id,
      id: ingredient.id,
      displayText: ingredient.displayText,
      ingredientNameText: ingredient.ingredientNameText ?? '',
      quantityText: ingredient.quantity === null ? '' : String(ingredient.quantity),
      detectedQuantityText: null,
      unitId: ingredient.measurementUnitId,
      unitLabel: '',
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
 * accepting a parsed line turns it into an editable row here, but nothing is sent anywhere: the recipe
 * create/update endpoints have no ingredient fields yet, so this component's state is local only, the
 * same boundary `RecipeEditorComponent.ingredientGroups` already draws for the read-only rendering this
 * replaces.
 */
@Component({
  selector: 'cp-recipe-ingredient-editor',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpDialogComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpStatusPillComponent,
    IngredientPasteReviewComponent,
  ],
  templateUrl: './recipe-ingredient-editor.component.html',
  styleUrl: './recipe-ingredient-editor.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeIngredientEditorComponent {
  readonly workspaceSlug = input.required<string>();
  readonly initialGroups = input<readonly RecipeIngredientGroup[]>([]);
  readonly groupsChanged = output<readonly EditableIngredientGroup[]>();

  readonly groups = signal<EditableIngredientGroup[]>([]);
  readonly pasteDialogOpen = signal(false);

  /** Announces a reorder for screen-reader users — the up/down buttons themselves are silent otherwise. */
  readonly moveAnnouncement = signal('');

  protected readonly scalingOptions = SCALING_BEHAVIOR_OPTIONS;

  constructor() {
    effect(() => this.groups.set(toEditableGroups(this.initialGroups())));
  }

  toneFor(state: IngredientRowMatchState): CpStatusPillTone {
    return MATCH_STATE_TONE[state];
  }

  labelFor(state: IngredientRowMatchState): string {
    return MATCH_STATE_LABEL[state];
  }

  openPasteDialog(): void {
    this.pasteDialogOpen.set(true);
  }

  closePasteDialog(): void {
    this.pasteDialogOpen.set(false);
  }

  onLinesAccepted(event: IngredientLinesAccepted): void {
    const targetGroupKey = this.groups()[0]?.key ?? this.addGroupInternal();
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

  updateRow(groupKey: string, rowKey: string, patch: Partial<Omit<EditableIngredientRow, 'key' | 'id'>>): void {
    this.groups.update((groups) =>
      groups.map((group) =>
        group.key === groupKey
          ? { ...group, ingredients: group.ingredients.map((row) => (row.key === rowKey ? { ...row, ...patch } : row)) }
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

  addGroup(): void {
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

  addRow(groupKey: string): void {
    this.groups.update((groups) =>
      groups.map((group) => (group.key === groupKey ? { ...group, ingredients: [...group.ingredients, blankRow()] } : group)),
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
