import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpFieldComponent,
  CpListShellComponent,
  CpListShellState,
  CpStatusPillComponent,
} from '@creator-pantry/ui';

import {
  Quantity,
  RecipeScalingPreviewLine,
  RecipeScalingResult,
  ScaleRecipeRequest,
  ScalingWarning,
  UNKNOWN_SCALING_WARNING,
} from '../../models/recipe-scaling.models';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RecipeCalculationService } from '../../services/recipe-calculation.service';

/** Which of the two mutually exclusive inputs the creator is scaling by. Mirrors the server's own exclusive-or. */
export type ScalingMode = 'factor' | 'yield';

/**
 * The request's own outcome, held apart from {@link RecipeScalingPreviewComponent.preview} so that a refused
 * attempt never erases a preview the creator is still reading. The preview carries its own factor and source
 * version on screen, so the two cannot be confused for one another.
 */
type RequestState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  | { readonly status: 'done' }
  /** The factor or target yield is one the server will not resolve; the message is its own wording. */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/** One ingredient line as the table renders it: what it was, what it becomes, and why it may not have moved. */
interface ScalingRow {
  readonly key: string;
  readonly displayText: string;
  /** The saved amount, from the recipe already on screen — never recomputed here. `null` renders as "—". */
  readonly before: string | null;
  readonly after: string | null;
  /**
   * The exact unrounded result, shown beside the rounded one when they can differ. A fraction is the server's
   * canonical value; the decimal above it is only how it reads.
   */
  readonly exact: string | null;
  readonly wasScaled: boolean;
  readonly notes: readonly string[];
}

/**
 * How each warning the server can raise reads to a creator.
 *
 * One sentence each, and every one says what to do rather than naming the rule that fired. None of them
 * claims the scaled amount is wrong — a scaling preview is arithmetic, and these are the places where
 * arithmetic alone is not the whole answer (recipes.md).
 */
const WARNING_TEXT: Readonly<Record<ScalingWarning, string>> = {
  FixedQuantityNotScaled: 'Kept as written — this line is marked fixed.',
  ReviewRequiredQualitative: 'No amount to scale — judge this one by taste.',
  ReviewRequiredDiscreteCount: 'Whole items — pick a sensible count yourself.',
  ReviewRequiredRange: 'This is a range; check both ends.',
  ReviewRequiredOther: "Flagged for review — scaling this one isn't linear.",
  NoQuantityToScale: 'No amount recorded, so nothing was multiplied.',
  ScaledToNegligibleDisplay: 'Rounds to zero at this precision — the exact amount is shown.',
  RecipeYieldNotStructured: "This recipe has no numeric yield, so a scaled yield can't be shown.",
  ExtremeScaleFactor:
    "That's an order-of-magnitude change. Leavening, pan size and cook times don't scale linearly — check them.",
  [UNKNOWN_SCALING_WARNING]: 'Flagged for review — check this line.',
};

/** Renders a canonical fraction as text. String composition only: nothing here divides, rounds or compares. */
function formatQuantity(quantity: Quantity): string {
  return quantity.denominator === '1' ? quantity.numerator : `${quantity.numerator}/${quantity.denominator}`;
}

/**
 * A quantity worth showing exactly — one the decimal beside it cannot state without rounding.
 *
 * A range is judged on both bounds, not only the lower one. Scaling "9–10 cups" by a third gives an exact
 * lower bound of 3 and an upper of 10/3: testing only the lower one dropped the exact column for the whole
 * row, hiding precisely the value it exists to show.
 */
function formatExact(lower: Quantity | null, upper: Quantity | null): string | null {
  if (lower === null) return null;

  const isExactlyRepresentable = lower.denominator === '1' && (upper === null || upper.denominator === '1');
  if (isExactlyRepresentable) return null;

  return upper === null ? formatQuantity(lower) : `${formatQuantity(lower)} to ${formatQuantity(upper)}`;
}

/** A pair of decimals as one cell. "to" rather than a dash, which a screen reader reads as a subtraction. */
function formatRange(lower: number | null, upper: number | null): string | null {
  if (lower === null) return null;
  return upper === null ? String(lower) : `${lower} to ${upper}`;
}

/** Makes each panel's heading and control ids unique, so two panels on one page cannot collide. */
let nextInstance = 0;

/**
 * The recipe editor's scaling preview (EDITOR-UI-005, over the read-only `calculations/scale` route).
 *
 * **Nothing here is saved, and nothing here is calculated.** Every number the table shows either came from
 * the server's preview or was already on screen as part of the recipe: the "after" column is the server's
 * result, and the "before" column is the saved line the result was computed from, matched by id. No
 * multiplication, rounding or unit reasoning happens in TypeScript — doing any of it here would be a second,
 * quietly divergent implementation of the domain's own arithmetic.
 *
 * The preview is always of the recipe's **current saved version**, which is what makes the "before" column
 * trustworthy: that is the content the editor loaded and the content the server scaled. Unsaved edits on the
 * page are therefore not in it, and the panel says so rather than letting the numbers imply otherwise.
 */
@Component({
  selector: 'cp-recipe-scaling-preview',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpFieldComponent, CpListShellComponent, CpStatusPillComponent],
  templateUrl: './recipe-scaling-preview.component.html',
  styleUrl: './recipe-scaling-preview.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeScalingPreviewComponent {
  private readonly calculationService = inject(RecipeCalculationService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The version a preview is computed from — the recipe's current one. Null while the recipe is still
   * loading, or for a recipe with no version at all, and the panel refuses to scale rather than guessing 1.
   */
  readonly sourceVersionNumber = input<number | null>(null);

  /** The recipe's saved ingredient lines, and the only source of the "before" column. */
  readonly savedIngredientGroups = input<readonly RecipeIngredientGroup[]>([]);

  /** The recipe's saved structured yield, when it has one. Without it there is nothing to scale toward. */
  readonly recipeYieldQuantity = input<number | null>(null);

  /** The recipe's own words for its yield, shown as a hint so a target is entered in the right terms. */
  readonly recipeYieldText = input<string | null>(null);

  /** Whether the form beside this panel has edits a preview cannot include, because they were never sent. */
  readonly editorIsDirty = input(false);

  private readonly instance = nextInstance++;
  readonly headingId = `scaling-heading-${this.instance}`;
  readonly modeLegendId = `scaling-mode-${this.instance}`;
  readonly factorFieldId = `scaling-factor-${this.instance}`;
  readonly targetYieldFieldId = `scaling-target-yield-${this.instance}`;

  readonly mode = signal<ScalingMode>('factor');

  // Numbers, not text: both boxes are `input type="number"`, so `ngModel` hands back a number or null (null
  // being an empty or unparseable box) and nothing here ever has to parse a string into one.
  readonly factorValue = signal<number | null>(null);
  readonly targetYieldValue = signal<number | null>(null);

  /** A local shape error — the box is empty, or holds something that is not a number at all. */
  private readonly localErrorSignal = signal<string | null>(null);
  readonly localError = this.localErrorSignal.asReadonly();

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly previewSignal = signal<RecipeScalingResult | null>(null);
  readonly preview = this.previewSignal.asReadonly();

  readonly isWorking = computed(() => this.requestSignal().status === 'working');

  /** A recipe with no numeric yield has nothing to scale toward, so that mode is offered but not usable. */
  readonly canScaleToYield = computed(() => this.recipeYieldQuantity() !== null);

  /**
   * What the recipe currently makes, in its own words where it has them. The saved yield, never the form's:
   * a target entered against an unsaved yield edit would be a target for a recipe that does not exist.
   */
  readonly targetYieldHint = computed(() => {
    const quantity = this.recipeYieldQuantity();
    if (quantity === null) return '';

    const text = this.recipeYieldText();
    return text ? `This recipe makes ${quantity} — ${text}.` : `This recipe makes ${quantity}.`;
  });

  readonly canSubmit = computed(() => !this.isWorking() && this.sourceVersionNumber() !== null);

  /**
   * Whether the recipe has moved past the version this preview was computed from — a save or a restore while
   * it was on screen. The numbers stay visible, because they were true of the version they name; what changes
   * is that they are no longer about the recipe as it now stands.
   */
  readonly isStale = computed(() => {
    const preview = this.previewSignal();
    const current = this.sourceVersionNumber();
    return preview !== null && current !== null && preview.sourceVersionNumber !== current;
  });

  /**
   * A re-scale keeps the previous table on screen rather than dropping back to the shell's loading state:
   * the numbers already there are still true of the factor and version printed above them, and replacing a
   * read table with "Loading…" on every attempt costs the creator their place for no gain. Only the first
   * preview of a session has nothing to keep, and that one does show the loading state.
   */
  readonly listState = computed<CpListShellState>(() => {
    if (this.previewSignal() !== null) return 'ready';
    return this.isWorking() ? 'loading' : 'empty';
  });

  /** The server's own message for a field, when it refused the request. */
  fieldError(field: string): string | null {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;
    return state.fieldErrors[field]?.[0] ?? null;
  }

  readonly factorError = computed(() =>
    this.mode() === 'factor' ? (this.localErrorSignal() ?? this.fieldError('multiplier')) : null,
  );

  readonly targetYieldError = computed(() =>
    this.mode() === 'yield' ? (this.localErrorSignal() ?? this.fieldError('targetYieldQuantity')) : null,
  );

  /** The refusals that are about the recipe rather than about the numbers typed into the form. */
  /**
   * A refusal naming a field this panel has no control for — in practice only `sourceVersionNumber`, which
   * no creator can set. Surfaced as the server worded it, because the alternative is to show nothing at all
   * and leave the button looking as though it did not work.
   */
  private readonly unboundFieldError = computed<string | null>(() => {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;

    const bound: readonly string[] = ['multiplier', 'targetYieldQuantity'];
    for (const [field, messages] of Object.entries(state.fieldErrors)) {
      if (!bound.includes(field) && messages.length > 0) return messages[0];
    }
    return null;
  });

  readonly requestProblem = computed<string | null>(() => {
    switch (this.requestSignal().status) {
      case 'invalid_request':
        return this.unboundFieldError();
      case 'version_not_found':
        return 'That version no longer exists — reload the recipe and scale it again.';
      case 'not_found':
        return "This recipe couldn't be found. It may have been removed.";
      case 'unauthorized':
        return 'You are no longer signed in, or no longer have access to this recipe. Sign in again to continue.';
      case 'unavailable':
        return "Couldn't work out the scaled amounts. Check your connection and try again.";
      default:
        return null;
    }
  });

  /** Recipe-level warnings, which are about the request as a whole rather than about any one line. */
  readonly recipeWarnings = computed<readonly string[]>(
    () => this.previewSignal()?.preview.recipeWarnings.map((warning) => WARNING_TEXT[warning]) ?? [],
  );

  /** The one-line summary a screen reader hears when a preview lands. */
  readonly summary = computed<string | null>(() => {
    const result = this.previewSignal();
    if (result === null) return null;

    const factor = `Scaled by ×${formatQuantity(result.preview.factor)}, from version ${result.sourceVersionNumber}`;
    const scaledYield = result.preview.scaledYieldDisplayQuantity;
    const savedYield = this.recipeYieldQuantity();
    if (scaledYield === null) return `${factor}.`;

    return savedYield === null
      ? `${factor}. Yield becomes ${scaledYield}.`
      : `${factor}. Yield ${savedYield} becomes ${scaledYield}.`;
  });

  /**
   * The saved amount for each line id.
   *
   * These are the recipe's own numbers as loaded, not the ingredient editor's working copy: the working copy
   * is never submitted this phase, so it is not what the server scaled and showing it as "before" would
   * describe a calculation that did not happen.
   */
  private readonly savedAmounts = computed<ReadonlyMap<string, string | null>>(() => {
    const amounts = new Map<string, string | null>();
    for (const group of this.savedIngredientGroups()) {
      for (const ingredient of group.ingredients) {
        amounts.set(ingredient.id, formatRange(ingredient.quantity, ingredient.quantityUpper));
      }
    }
    return amounts;
  });

  readonly rows = computed<readonly ScalingRow[]>(() => {
    const result = this.previewSignal();
    if (result === null) return [];

    const saved = this.savedAmounts();
    return result.preview.lines.map((line) => this.toRow(line, saved));
  });

  private toRow(line: RecipeScalingPreviewLine, saved: ReadonlyMap<string, string | null>): ScalingRow {
    return {
      key: line.id,
      displayText: line.displayText,
      // A line the saved recipe does not carry renders "—" rather than a number borrowed from somewhere
      // else: it means this preview is of content the page is not showing, which is worth noticing.
      before: saved.get(line.id) ?? null,
      after: formatRange(line.scaledDisplayQuantity, line.scaledDisplayQuantityUpper),
      exact: formatExact(line.scaledQuantity, line.scaledQuantityUpper),
      wasScaled: line.wasScaled,
      notes: line.warnings.map((warning) => WARNING_TEXT[warning]),
    };
  }

  setMode(mode: ScalingMode): void {
    if (this.mode() === mode) return;

    this.mode.set(mode);
    // The other box is cleared rather than left holding a value the request will not carry: the server
    // accepts exactly one of the two, and a filled field that is silently ignored is a lie about what was
    // asked for.
    if (mode === 'factor') {
      this.targetYieldValue.set(null);
    } else {
      this.factorValue.set(null);
    }
    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'idle' });
  }

  /** Fills the factor box from a preset. It sets a value and nothing else — no amount is worked out here. */
  setFactor(value: number): void {
    this.mode.set('factor');
    this.factorValue.set(value);
    this.targetYieldValue.set(null);
    this.localErrorSignal.set(null);
  }

  async scale(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    if (versionNumber === null || this.isWorking()) return;

    const mode = this.mode();
    const value = mode === 'factor' ? this.factorValue() : this.targetYieldValue();

    // Only the shape is checked here — that there is a number at all. Whether it is one this recipe can
    // actually be scaled by (zero, negative, or a target yield the recipe has no structured yield to derive
    // a factor from) is the server's own rule, and it says so in its own words rather than in a second copy
    // of them kept in sync by hand.
    if (value === null || !Number.isFinite(value)) {
      this.localErrorSignal.set('Enter a number.');
      return;
    }

    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'working' });

    const request: ScaleRecipeRequest =
      mode === 'factor'
        ? { sourceVersionNumber: versionNumber, multiplier: value }
        : { sourceVersionNumber: versionNumber, targetYieldQuantity: value };

    const outcome = await this.calculationService.scaleRecipe(this.workspaceSlug(), this.recipeId(), request);

    if (outcome.status === 'scaled') {
      this.previewSignal.set(outcome.result);
      this.requestSignal.set({ status: 'done' });
      return;
    }

    // The previous preview is deliberately left alone. It states its own factor and source version on
    // screen, so it cannot be mistaken for the answer to the attempt that just failed, and discarding it
    // would take away something the creator was reading because of a typo in the box above it.
    this.requestSignal.set(
      outcome.status === 'invalid_request'
        ? { status: 'invalid_request', fieldErrors: outcome.fieldErrors }
        : { status: outcome.status },
    );
  }
}
