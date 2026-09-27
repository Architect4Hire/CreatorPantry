import { ChangeDetectionStrategy, Component, computed, inject, input, model, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpStatusPillComponent, CpStatusPillTone } from '@creator-pantry/ui';

import { IngredientParseService } from '../../services/ingredient-parse.service';
import { IngredientMatchCandidate, ParsedIngredientLine, UnitMatchCandidate } from '../../models/ingredient-parse.models';
import { EditableIngredientRow, rowFromParsedLine } from './recipe-ingredient-editor.component';

/** One group a pasted batch can be sent to. The label is the host's, which is what numbers groups. */
export interface PasteTargetGroup {
  readonly key: string;
  readonly label: string;
}

export interface IngredientLinesAccepted {
  readonly rows: readonly EditableIngredientRow[];

  /**
   * The group the creator chose, or `null` for "a new group at the end". A `null` target is the host's to
   * resolve, and it must write the key it created back through `targetGroupKey` so the rest of the batch —
   * and any later accept in the same dialog — joins that group instead of making another empty one.
   */
  readonly targetGroupKey: string | null;
}

/** The option value standing for "a new group at the end"; `''` is what a `<select>` gives for it. */
const NEW_GROUP_VALUE = '';

type ParseState =
  | { readonly status: 'idle' }
  | { readonly status: 'parsing' }
  | { readonly status: 'parsed'; readonly lines: readonly ParsedIngredientLine[] }
  | { readonly status: 'error'; readonly message: string };

const UNRESOLVED_INGREDIENT_LABEL = '— choose or leave unmatched —';
const UNRESOLVED_UNIT_LABEL = '— choose or leave unmatched —';

/**
 * Paste free-form ingredient lines, review the server's tokenize-and-match proposal for each, and accept
 * one at a time into the structured editor. Nothing here is confirmed until "Add to recipe" is clicked —
 * closing without accepting discards the whole pasted batch (ING-001, ING-002, 7.5).
 */
@Component({
  selector: 'cp-ingredient-paste-review',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpFieldComponent, CpStatusPillComponent],
  templateUrl: './ingredient-paste-review.component.html',
  styleUrl: './ingredient-paste-review.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IngredientPasteReviewComponent {
  readonly workspaceSlug = input.required<string>();

  /** The groups a batch can be sent to, in the host's own order. */
  readonly groups = input<readonly PasteTargetGroup[]>([]);

  /**
   * Which group accepted lines join — `null` meaning "a new group at the end". A two-way model rather than a
   * plain input so the host can answer a `null` target with the key of the group it created; see
   * {@link IngredientLinesAccepted.targetGroupKey}.
   */
  readonly targetGroupKey = model<string | null>(null);

  readonly linesAccepted = output<IngredientLinesAccepted>();

  readonly pastedText = signal('');
  readonly state = signal<ParseState>({ status: 'idle' });

  /** Ids kept in one place so `forId`, the control's `id` and cp-field's `{forId}-hint` cannot drift apart. */
  readonly pasteFieldId = 'paste-ingredients-text';
  readonly targetGroupFieldId = 'paste-target-group';

  readonly newGroupValue = NEW_GROUP_VALUE;

  /** Announces a bulk accept; the per-line buttons say "Added" on their own, which is visible feedback. */
  readonly bulkAnnouncement = signal('');

  /** Per-line index, the candidate the creator picked before accepting — see {@link rowFromParsedLine}'s overrides. */
  private readonly ingredientChoices = signal<ReadonlyMap<number, IngredientMatchCandidate | null>>(new Map());
  private readonly unitChoices = signal<ReadonlyMap<number, UnitMatchCandidate | null>>(new Map());
  private readonly acceptedIndexes = signal<ReadonlySet<number>>(new Set());

  readonly lines = computed(() => (this.state().status === 'parsed' ? (this.state() as { lines: readonly ParsedIngredientLine[] }).lines : []));

  private readonly parseService = inject(IngredientParseService);

  isAccepted(index: number): boolean {
    return this.acceptedIndexes().has(index);
  }

  toneFor(resolved: boolean, ambiguous: boolean): CpStatusPillTone {
    if (ambiguous) return 'warning';
    return resolved ? 'success' : 'warning';
  }

  ingredientChoiceFor(index: number, line: ParsedIngredientLine): IngredientMatchCandidate | null | undefined {
    const choices = this.ingredientChoices();
    return choices.has(index) ? choices.get(index) : line.ingredientMatch?.resolved;
  }

  unitChoiceFor(index: number, line: ParsedIngredientLine): UnitMatchCandidate | null | undefined {
    const choices = this.unitChoices();
    return choices.has(index) ? choices.get(index) : line.unitMatch?.resolved;
  }

  readonly unresolvedIngredientLabel = UNRESOLVED_INGREDIENT_LABEL;
  readonly unresolvedUnitLabel = UNRESOLVED_UNIT_LABEL;

  onIngredientChoiceChange(index: number, line: ParsedIngredientLine, ingredientId: string): void {
    const candidate = (line.ingredientMatch?.alternates ?? []).find((option) => option.ingredientId === ingredientId) ?? null;
    this.ingredientChoices.update((choices) => new Map(choices).set(index, candidate));
  }

  onUnitChoiceChange(index: number, line: ParsedIngredientLine, unitId: string): void {
    const candidate = (line.unitMatch?.alternates ?? []).find((option) => option.measurementUnitId === unitId) ?? null;
    this.unitChoices.update((choices) => new Map(choices).set(index, candidate));
  }

  async parse(): Promise<void> {
    const lines = this.pastedText()
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.length > 0);

    if (lines.length === 0) return;

    this.state.set({ status: 'parsing' });
    this.acceptedIndexes.set(new Set());
    this.ingredientChoices.set(new Map());
    this.unitChoices.set(new Map());

    const outcome = await this.parseService.parseIngredientLines(this.workspaceSlug(), lines);

    if (outcome.status === 'parsed') {
      this.state.set({ status: 'parsed', lines: outcome.lines });
      return;
    }

    this.state.set({
      status: 'error',
      message:
        outcome.status === 'validation_failed'
          ? (Object.values(outcome.fieldErrors)[0]?.[0] ?? 'Those lines could not be parsed as submitted.')
          : "Couldn't reach the server to parse these lines. Try again in a moment.",
    });
  }

  /**
   * The indexes "Add all matched" would accept, and the single source for its count, its disabled state and
   * what it actually sends.
   *
   * A line qualifies only when the parser resolved everything it found on it, or the creator has since
   * resolved it by hand. Specifically excluded:
   *
   * - group markers, which are section headings rather than ingredients;
   * - lines already accepted;
   * - lines the matcher never had anything to say about (`unreviewed`, not "matched");
   * - anything still waiting on a choice, including a line where the creator chose "leave unmatched" — the
   *   button says *matched*, and that line's own button is still there;
   * - anything the matcher itself called ambiguous, even where it offered a resolved candidate, because the
   *   warning pill on that line is the parser saying it is not sure;
   * - `InvalidQuantity`, which is a quantity the tokenizer could not read at all.
   *
   * `NoQuantityDetected` does **not** disqualify a line. It fires on ordinary correct lines — "kosher salt,
   * to taste", "zest of 1 lemon" — where the ingredient and unit matched confidently and there is simply no
   * number to find. The creator's text is carried through verbatim either way, and the missing quantity is
   * visible and editable in the row afterwards. Nothing here is silent: the count of what was skipped sits
   * under the button, and {@link bulkAnnouncement} says what happened.
   */
  readonly bulkAcceptableIndexes = computed<readonly number[]>(() => {
    const accepted = this.acceptedIndexes();
    return this.lines()
      .map((line, index) => ({ line, index }))
      .filter(({ line, index }) => !accepted.has(index) && this.isBulkAcceptable(index, line))
      .map(({ index }) => index);
  });

  readonly bulkAcceptableCount = computed(() => this.bulkAcceptableIndexes().length);

  /** Lines that are ingredients, are not accepted, and are not eligible — the ones still owed a look. */
  readonly needsReviewCount = computed(() => {
    const accepted = this.acceptedIndexes();
    const eligible = new Set(this.bulkAcceptableIndexes());
    return this.lines().filter((line, index) => !line.tokens.isGroupMarker && !accepted.has(index) && !eligible.has(index))
      .length;
  });

  readonly groupMarkerCount = computed(() => this.lines().filter((line) => line.tokens.isGroupMarker).length);

  private isBulkAcceptable(index: number, line: ParsedIngredientLine): boolean {
    if (line.tokens.isGroupMarker) return false;

    const ingredientMatch = line.ingredientMatch;
    const unitMatch = line.unitMatch;
    if (ingredientMatch === null && unitMatch === null) return false;

    if (line.tokens.ambiguities.some((ambiguity) => ambiguity.kind === 'InvalidQuantity')) return false;

    // The parser's own `isAmbiguous` blocks a line only until the creator answers it. Checking it before
    // their choice — rather than after — would make a hand-resolved line permanently ineligible, which is
    // the opposite of the rule: a choice *is* review, and the point of the growing count is that working
    // through the pickers moves lines into the batch.
    if (!this.isMatchSettled(ingredientMatch, this.ingredientChoices().has(index), this.ingredientChoiceFor(index, line))) {
      return false;
    }
    if (!this.isMatchSettled(unitMatch, this.unitChoices().has(index), this.unitChoiceFor(index, line))) {
      return false;
    }

    return true;
  }

  /**
   * Whether one of the two matches on a line is settled enough for a bulk accept: absent entirely, or
   * resolved to something, and not still carrying the parser's own doubt unanswered.
   */
  private isMatchSettled(
    match: { readonly isAmbiguous: boolean } | null,
    creatorDecided: boolean,
    effectiveChoice: { readonly kind: string } | null | undefined,
  ): boolean {
    if (match === null) return true;
    if (!effectiveChoice) return false;
    return creatorDecided || !match.isAmbiguous;
  }

  private rowFor(index: number, line: ParsedIngredientLine): EditableIngredientRow {
    return rowFromParsedLine(line, {
      ingredient: this.ingredientChoices().has(index) ? this.ingredientChoices().get(index) : undefined,
      unit: this.unitChoices().has(index) ? this.unitChoices().get(index) : undefined,
    });
  }

  private targetGroupLabel(): string {
    const key = this.targetGroupKey();
    return this.groups().find((group) => group.key === key)?.label ?? 'a new group';
  }

  accept(index: number, line: ParsedIngredientLine): void {
    this.linesAccepted.emit({ rows: [this.rowFor(index, line)], targetGroupKey: this.targetGroupKey() });
    this.acceptedIndexes.update((accepted) => new Set(accepted).add(index));
  }

  /**
   * Accepts every currently eligible line as one batch.
   *
   * One emission rather than one per line: the host turns each into a `groups.update` and a `groupsChanged`,
   * so twelve emissions would be twelve dirty transitions for what the creator did once — and, with a `null`
   * target, twelve chances to create a group before the host's key has been written back.
   */
  acceptAllMatched(): void {
    const indexes = this.bulkAcceptableIndexes();
    if (indexes.length === 0) return;

    const lines = this.lines();
    const rows = indexes.map((index) => this.rowFor(index, lines[index]));
    const groupLabel = this.targetGroupLabel();

    this.linesAccepted.emit({ rows, targetGroupKey: this.targetGroupKey() });
    this.acceptedIndexes.update((accepted) => {
      const next = new Set(accepted);
      for (const index of indexes) next.add(index);
      return next;
    });

    const remaining = this.needsReviewCount();
    this.bulkAnnouncement.set(
      `${rows.length} ${rows.length === 1 ? 'ingredient' : 'ingredients'} added to ${groupLabel}.` +
        (remaining > 0 ? ` ${remaining} ${remaining === 1 ? 'line' : 'lines'} still need review.` : ''),
    );
  }

  onTargetGroupChange(value: string): void {
    this.targetGroupKey.set(value === NEW_GROUP_VALUE ? null : value);
  }

  reset(): void {
    this.pastedText.set('');
    this.state.set({ status: 'idle' });
    this.acceptedIndexes.set(new Set());
    this.ingredientChoices.set(new Map());
    this.unitChoices.set(new Map());
    this.bulkAnnouncement.set('');
  }
}
