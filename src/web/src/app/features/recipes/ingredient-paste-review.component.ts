import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpStatusPillComponent, CpStatusPillTone } from '@creator-pantry/ui';

import { IngredientParseService } from '../../services/ingredient-parse.service';
import { IngredientMatchCandidate, ParsedIngredientLine, UnitMatchCandidate } from '../../models/ingredient-parse.models';
import { EditableIngredientRow, rowFromParsedLine } from './recipe-ingredient-editor.component';

export interface IngredientLinesAccepted {
  readonly rows: readonly EditableIngredientRow[];
}

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
  readonly linesAccepted = output<IngredientLinesAccepted>();

  readonly pastedText = signal('');
  readonly state = signal<ParseState>({ status: 'idle' });

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

  accept(index: number, line: ParsedIngredientLine): void {
    const row = rowFromParsedLine(line, {
      ingredient: this.ingredientChoices().has(index) ? this.ingredientChoices().get(index) : undefined,
      unit: this.unitChoices().has(index) ? this.unitChoices().get(index) : undefined,
    });

    this.linesAccepted.emit({ rows: [row] });
    this.acceptedIndexes.update((accepted) => new Set(accepted).add(index));
  }

  reset(): void {
    this.pastedText.set('');
    this.state.set({ status: 'idle' });
    this.acceptedIndexes.set(new Set());
    this.ingredientChoices.set(new Map());
    this.unitChoices.set(new Map());
  }
}
