import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpStatusPillComponent, CpStatusPillTone } from '@creator-pantry/ui';

import { Quantity } from '../../models/ingredient-parse.models';
import {
  RecipeYieldReconciliationResult,
  YieldReconciliationField,
  YieldReconciliationStatus,
} from '../../models/recipe-yield.models';
import { MAX_DISPLAY_PRECISION, MeasurementUnit } from '../../models/reference.models';
import { RecipeCalculationService } from '../../services/recipe-calculation.service';
import { ReferenceService } from '../../services/reference.service';

type RequestState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  | { readonly status: 'done' }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/** One of the three reconciled values, as the card renders it. */
interface ReconciledRow {
  readonly field: YieldReconciliationField;
  readonly label: string;
  readonly value: number | null;
  /** "computed" or "as you entered" — stated in words, never by a colour or a weight. */
  readonly provenance: string;
}

/**
 * What each status means, in the creator's terms.
 *
 * None of the four is a failure. Two of them — nothing computable, and three numbers that disagree — are
 * conclusions about the recipe's own figures, and ING-005 asks for them to be shown as answers rather than
 * as a request that went wrong.
 */
const STATUS_HEADLINES: Readonly<Record<YieldReconciliationStatus, string>> = {
  Reconciled: 'These agree',
  Solved: 'Worked out the missing one',
  Contradictory: "These don't agree",
  InsufficientInput: 'Not enough to work from',
};

const STATUS_EXPLANATIONS: Readonly<Record<YieldReconciliationStatus, string>> = {
  Reconciled:
    'All three were given, and the servings multiplied by the serving size match the batch yield at the precision below.',
  Solved: 'Two were given, so the third could be worked out exactly. Nothing was rounded into agreement.',
  Contradictory:
    "All three were given, and they don't agree even after rounding. Nothing was changed, and none of the " +
    'three is assumed to be the wrong one — that is yours to decide.',
  InsufficientInput:
    'Enter at least two of batch yield, servings and serving size. Nothing was assumed in place of the ' +
    'missing ones.',
};

const STATUS_TONES: Readonly<Record<YieldReconciliationStatus, CpStatusPillTone>> = {
  Reconciled: 'success',
  Solved: 'success',
  Contradictory: 'warning',
  InsufficientInput: 'neutral',
};

const FIELD_LABELS: Readonly<Record<YieldReconciliationField, string>> = {
  BatchYield: 'Batch yield',
  ServingCount: 'Servings',
  ServingSize: 'Serving size',
};

/**
 * How each formula the server emits reads in words. An unrecognised one still shows; it simply has no gloss.
 *
 * Keyed by the calculator's exact strings, all five of them. The first version of this map invented three
 * keys that the server never emits, so the gloss silently never rendered — and two of them were transposed
 * as well (`batchYield ÷ servingSize` solves the serving *count*, not the size), so guessing at the keys
 * would have printed the wrong explanation rather than none.
 */
const FORMULA_EXPLANATIONS: Readonly<Record<string, string>> = {
  'servingCount × servingSize = batchYield':
    'Servings multiplied by serving size, which matches the batch yield you gave.',
  'servingCount × servingSize ≠ batchYield as given':
    'Servings multiplied by serving size, which does not match the batch yield you gave.',
  'batchYield = servingCount × servingSize': 'Servings multiplied by serving size.',
  'servingSize = batchYield ÷ servingCount': 'Batch yield divided by the number of servings.',
  'servingCount = batchYield ÷ servingSize': 'Batch yield divided by the serving size.',
};

/**
 * A reconciled batch yield, ready to be recorded as the recipe's own yield quantity.
 *
 * Raised rather than saved here: the recipe, its concurrency token and the seam that writes a version all
 * belong to the editor (REC-004).
 */
export interface YieldApplication {
  readonly yieldQuantity: number;
  /** How the change reads, for the diff and the confirmation. */
  readonly beforeLabel: string;
  readonly afterLabel: string;
}

/**
 * The statuses whose batch yield is a conclusion worth recording.
 *
 * `Contradictory` is deliberately absent: the whole point of that answer is that the server decided nothing
 * and none of the three numbers is assumed to be the right one. Offering to save one of them would make the
 * choice the calculation refused to make. `InsufficientInput` has nothing to save at all.
 */
const APPLICABLE_STATUSES: readonly YieldReconciliationStatus[] = ['Reconciled', 'Solved'];

/** Renders a canonical fraction as text. String composition only: nothing here divides, rounds or compares. */
function formatQuantity(quantity: Quantity): string {
  return quantity.denominator === '1' ? quantity.numerator : `${quantity.numerator}/${quantity.denominator}`;
}

let nextInstance = 0;

/**
 * The recipe editor's yield and portion preview (EDITOR-UI-005, over the read-only
 * `calculations/recalculate-yield` route).
 *
 * **Nothing here is saved, and nothing here is calculated.** Every number on the card is the server's, or is
 * echoed back from what the creator typed. No division, rounding or agreement check happens in TypeScript.
 *
 * **Nothing is assumed on the creator's behalf.** Two of the four answers this can give — "not enough to work
 * from" and "these don't agree" — exist precisely because recipes.md forbids inventing a serving definition
 * or picking which of three stated numbers is the wrong one. Both render as conclusions, with the figures
 * exactly as entered.
 *
 * **The unit is the recipe's, not the form's.** Batch yield, serving size and pan capacity are all read in
 * the recipe's own yield unit, resolved server-side from the version being reconciled. The panel states which
 * unit that is, and says so plainly when the recipe records none — that case also silently rules out a pan
 * comparison, which needs a volume.
 */
@Component({
  selector: 'cp-recipe-yield-reconciliation',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpFieldComponent, CpStatusPillComponent],
  templateUrl: './recipe-yield-reconciliation.component.html',
  styleUrl: './recipe-yield-reconciliation.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeYieldReconciliationComponent {
  private readonly calculationService = inject(RecipeCalculationService);
  private readonly referenceService = inject(ReferenceService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  readonly sourceVersionNumber = input<number | null>(null);

  /**
   * The recipe's saved yield unit, read only to say which unit the amounts below are in. The server resolves
   * the dimension itself from the same version; this is the panel telling the creator what that resolved to.
   */
  readonly yieldUnitId = input<string | null>(null);

  /** The recipe's saved yield quantity, so the diff can say what it is changing from. */
  readonly savedYieldQuantity = input<number | null>(null);

  /** Whether the form beside this panel has edits that a write to the recipe would have to reckon with. */
  readonly editorIsDirty = input(false);

  /**
   * A reconciled batch yield the creator has confirmed they want recorded.
   *
   * The panel raises it; the editor performs it through the ordinary update seam. Nothing here writes.
   */
  readonly applyRequested = output<YieldApplication>();

  private readonly unitCatalogue = signal<readonly MeasurementUnit[]>([]);

  readonly maxPrecision = MAX_DISPLAY_PRECISION;

  private readonly instance = nextInstance++;
  readonly headingId = `yield-heading-${this.instance}`;
  readonly batchYieldFieldId = `yield-batch-${this.instance}`;
  readonly servingCountFieldId = `yield-servings-${this.instance}`;
  readonly servingSizeFieldId = `yield-serving-size-${this.instance}`;
  readonly panVolumeFieldId = `yield-pan-${this.instance}`;
  readonly precisionFieldId = `yield-precision-${this.instance}`;

  readonly batchYield = signal<number | null>(null);
  readonly servingCount = signal<number | null>(null);
  readonly servingSize = signal<number | null>(null);
  readonly panVolume = signal<number | null>(null);
  readonly displayPrecision = signal<number | null>(2);

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly resultSignal = signal<RecipeYieldReconciliationResult | null>(null);
  readonly result = this.resultSignal.asReadonly();

  readonly isWorking = computed(() => this.requestSignal().status === 'working');
  readonly canSubmit = computed(() => !this.isWorking() && this.sourceVersionNumber() !== null);

  constructor() {
    void this.loadUnits();

    // A recipe replaced under the panel invalidates a reconciliation computed against the version it named.
    effect(() => {
      const current = this.sourceVersionNumber();
      const result = this.resultSignal();
      if (result !== null && current !== null && result.sourceVersionNumber !== current) {
        this.resultSignal.set(null);
        this.requestSignal.set({ status: 'idle' });
      }
    });
  }

  private async loadUnits(): Promise<void> {
    const outcome = await this.referenceService.listUnits();
    if (outcome.status === 'found') this.unitCatalogue.set(outcome.units);
  }

  /** The recipe's yield unit, when it has one and the catalogue could name it. */
  private readonly yieldUnit = computed<MeasurementUnit | null>(() => {
    const unitId = this.yieldUnitId();
    if (unitId === null) return null;
    return this.unitCatalogue().find((unit) => unit.id === unitId) ?? null;
  });

  /**
   * What the amounts below are read in — the assumption the form cannot show on its own.
   *
   * A recipe with no yield unit is read as a plain count by the server, which is also what makes a pan
   * comparison impossible. Both halves of that are said here rather than left to be discovered when the pan
   * block comes back uncomparable.
   */
  readonly unitAssumption = computed<string>(() => {
    const unit = this.yieldUnit();
    if (unit !== null) {
      return `Batch yield, serving size and pan capacity are all read in ${unit.pluralName} — this recipe's yield unit.`;
    }

    return this.yieldUnitId() === null
      ? 'This recipe records no yield unit, so these are read as plain counts. A pan comparison needs a volume yield and will not be attempted.'
      : 'This recipe records a yield unit the catalogue could not name, so the unit below cannot be shown.';
  });

  fieldError(field: string): string | null {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;
    return state.fieldErrors[field]?.[0] ?? null;
  }

  readonly batchYieldError = computed(() => this.fieldError('batchYield'));
  readonly servingCountError = computed(() => this.fieldError('servingCount'));
  readonly servingSizeError = computed(() => this.fieldError('servingSize'));
  readonly panVolumeError = computed(() => this.fieldError('panVolume'));
  readonly precisionError = computed(() => this.fieldError('displayPrecision'));

  /**
   * A refusal naming a field this panel has no control for — in practice only `sourceVersionNumber`,
   * which no creator can set. Surfaced as the server worded it, because the alternative was what this
   * used to do: clear the card, show nothing, and leave the button looking as though it did not work.
   */
  private readonly unboundFieldError = computed<string | null>(() => {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;

    const bound: readonly string[] = ['batchYield', 'servingCount', 'servingSize', 'panVolume', 'displayPrecision'];
    for (const [field, messages] of Object.entries(state.fieldErrors)) {
      if (!bound.includes(field) && messages.length > 0) return messages[0];
    }
    return null;
  });

  /**
   * What a screen reader hears when a result lands.
   *
   * A persistent region whose text changes, rather than a role="status" on the card itself: a live region
   * inserted into the DOM already holding its content is not reliably announced, and where it was, it read
   * the entire card including the apply button.
   */
  readonly announcement = computed<string>(() => {
    const result = this.resultSignal();
    return result === null ? '' : (this.statusHeadline() ?? '');
  });

  readonly requestProblem = computed<string | null>(() => {
    switch (this.requestSignal().status) {
      case 'version_not_found':
        return 'That version no longer exists — reload the recipe and try again.';
      case 'not_found':
        return "This recipe couldn't be found. It may have been removed.";
      case 'unauthorized':
        return 'You are no longer signed in, or no longer have access to this recipe. Sign in again to continue.';
      case 'unavailable':
        return "Couldn't work that out. Check your connection and try again.";
      case 'invalid_request':
        // Every field the server can name is bound to a control except sourceVersionNumber, which no panel
        // lets a creator set. Without this arm that refusal cleared the card and said nothing at all, so the
        // button appeared simply not to work.
        return this.unboundFieldError();

      default:
        return null;
    }
  });

  readonly statusHeadline = computed<string | null>(() => {
    const result = this.resultSignal();
    return result === null ? null : STATUS_HEADLINES[result.preview.status];
  });

  readonly statusExplanation = computed<string | null>(() => {
    const result = this.resultSignal();
    return result === null ? null : STATUS_EXPLANATIONS[result.preview.status];
  });

  readonly statusTone = computed<CpStatusPillTone>(() => {
    const result = this.resultSignal();
    return result === null ? 'neutral' : STATUS_TONES[result.preview.status];
  });

  /**
   * The three values, each saying where it came from.
   *
   * Rows are rendered for every status, including `InsufficientInput`, where the ones that were never given
   * read as blank — showing the creator what the calculation had, rather than an empty card.
   */
  readonly rows = computed<readonly ReconciledRow[]>(() => {
    const result = this.resultSignal();
    if (result === null) return [];

    const { preview } = result;
    const solved = preview.solvedField;
    const provenanceOf = (field: YieldReconciliationField, value: number | null): string => {
      if (value === null) return 'not given';
      return field === solved ? 'computed' : 'as you entered';
    };

    return [
      {
        field: 'BatchYield' as const,
        label: FIELD_LABELS.BatchYield,
        value: preview.batchYield,
        provenance: provenanceOf('BatchYield', preview.batchYield),
      },
      {
        field: 'ServingCount' as const,
        label: FIELD_LABELS.ServingCount,
        value: preview.servingCount,
        provenance: provenanceOf('ServingCount', preview.servingCount),
      },
      {
        field: 'ServingSize' as const,
        label: FIELD_LABELS.ServingSize,
        value: preview.servingSize,
        provenance: provenanceOf('ServingSize', preview.servingSize),
      },
    ];
  });

  readonly formulaExplanation = computed<string | null>(() => {
    const formula = this.resultSignal()?.preview.formula;
    return formula ? (FORMULA_EXPLANATIONS[formula] ?? null) : null;
  });

  /** Whether a pan capacity was given at all, and so whether the comparison block has anything to say. */
  readonly hasPan = computed(() => this.resultSignal()?.preview.panComparable !== null);

  readonly panComparable = computed(() => this.resultSignal()?.preview.panComparable === true);

  /** The fill ratio as the server's own exact fraction. Nothing here divides. */
  readonly panFillRatioLabel = computed<string | null>(() => {
    const ratio = this.resultSignal()?.preview.panFillRatio;
    return ratio ? formatQuantity(ratio) : null;
  });

  /** The batch yield this result would record, when it reached one worth recording. */
  private readonly applicableYield = computed<number | null>(() => {
    const preview = this.resultSignal()?.preview;
    if (preview === undefined) return null;
    if (!APPLICABLE_STATUSES.includes(preview.status)) return null;

    return preview.batchYield;
  });

  /**
   * Why this result cannot be recorded, when it cannot. Null means it can.
   *
   * Shown as text beside a disabled control rather than as an absent one — particularly for a contradiction,
   * where the absence of a button is the whole answer and needs saying.
   */
  readonly applyBlockedReason = computed<string | null>(() => {
    const preview = this.resultSignal()?.preview;
    if (preview === undefined) return null;

    if (preview.status === 'Contradictory') {
      return 'These three numbers disagree, and nothing here decided which one is right. Settle that first — there is nothing to record yet.';
    }

    if (preview.status === 'InsufficientInput') {
      return 'There is no reconciled yield to record yet.';
    }

    if (this.applicableYield() === null) {
      return 'This reconciliation produced no batch yield to record.';
    }

    if (this.sourceVersionNumber() === null) {
      return 'This recipe has no saved version to write to.';
    }

    if (this.editorIsDirty()) {
      return 'Save or discard your changes first. Applying writes to the recipe as it was last saved.';
    }

    // Recording a number the recipe already holds would write a version in which nothing changed.
    if (this.applicableYield() === this.savedYieldQuantity()) {
      return "This is already the recipe's yield quantity, so there is nothing to change.";
    }

    return null;
  });

  readonly canApply = computed(
    () => this.resultSignal() !== null && !this.isWorking() && this.applyBlockedReason() === null,
  );

  readonly applyDiff = computed<{ readonly before: string; readonly after: string } | null>(() => {
    const applicable = this.applicableYield();
    if (applicable === null) return null;

    const saved = this.savedYieldQuantity();
    return { before: saved === null ? 'not recorded' : String(saved), after: String(applicable) };
  });

  /**
   * Asks the editor to record this batch yield.
   *
   * Preconditions are re-checked here rather than trusted from the disabled state: the version the
   * reconciliation was computed against must still be the recipe's own, or the change would be composed
   * against content that has moved on.
   */
  requestApply(): void {
    const applicable = this.applicableYield();
    const result = this.resultSignal();
    const diff = this.applyDiff();

    if (applicable === null || result === null || diff === null) return;
    if (this.applyBlockedReason() !== null) return;
    if (result.sourceVersionNumber !== this.sourceVersionNumber()) return;

    this.applyRequested.emit({
      yieldQuantity: applicable,
      beforeLabel: diff.before,
      afterLabel: diff.after,
    });
  }

  async reconcile(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    if (versionNumber === null || this.isWorking()) return;

    this.requestSignal.set({ status: 'working' });

    // Nothing is checked here beyond what the boxes hold. Whether there is enough to work from is the
    // calculation's own conclusion, not a precondition — refusing to send a one-value request would hide the
    // answer ING-005 asks for.
    const outcome = await this.calculationService.recalculateYield(this.workspaceSlug(), this.recipeId(), {
      sourceVersionNumber: versionNumber,
      batchYield: this.batchYield(),
      servingCount: this.servingCount(),
      servingSize: this.servingSize(),
      panVolume: this.panVolume(),
      displayPrecision: this.displayPrecision(),
    });

    if (outcome.status === 'reconciled') {
      this.resultSignal.set(outcome.result);
      this.requestSignal.set({ status: 'done' });
      return;
    }

    // Cleared on a refusal, as the conversion sections are: the card describes one set of numbers, and
    // leaving it beside a refusal about different ones invites reading the refusal as a footnote.
    this.resultSignal.set(null);
    this.requestSignal.set(
      outcome.status === 'invalid_request'
        ? { status: 'invalid_request', fieldErrors: outcome.fieldErrors }
        : { status: outcome.status },
    );
  }
}
