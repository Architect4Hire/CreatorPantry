import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import {
  MIDPOINT_ROUNDING_LABELS,
  Quantity,
  RecipeTemperatureConversionResult,
  TEMPERATURE_SCALE_LABELS,
  TEMPERATURE_SCALE_SYMBOLS,
  TemperatureScale,
} from '../../models/recipe-conversion.models';
import { MAX_DISPLAY_PRECISION, MeasurementUnit } from '../../models/reference.models';
import { RecipeInstructionGroup } from '../../models/recipe.models';

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

/** One saved instruction step offered as a starting point, and why it may not be usable as one. */
export interface StepTemperatureOption {
  readonly id: string;
  readonly label: string;
  /** The reason this step cannot fill the form at all, when it cannot. Null means it can fill something. */
  readonly unresolvedReason: string | null;
  readonly value: number | null;
  /** The scale the step recorded, when its unit is one this calculation knows. Null is not a failure. */
  readonly scale: TemperatureScale | null;
  /** Said when a step has a number but nothing that names which scale it is in. */
  readonly scaleUnresolvedNote: string | null;
}

/**
 * The seeded unit codes the two scales correspond to.
 *
 * Matched by code and nothing else. A Temperature unit that is neither is left unresolved rather than mapped
 * by guesswork — picking a scale wrongly would turn 180 °C into 180 °F without anything on screen saying so.
 */
const SCALE_BY_UNIT_CODE: Readonly<Record<string, TemperatureScale>> = {
  celsius: 'Celsius',
  fahrenheit: 'Fahrenheit',
};

export const TEMPERATURE_SCALES: readonly TemperatureScale[] = ['Celsius', 'Fahrenheit'];

/** The seeded unit code each scale is written back as. The inverse of {@link SCALE_BY_UNIT_CODE}. */
const UNIT_CODE_BY_SCALE: Readonly<Record<TemperatureScale, string>> = {
  Celsius: 'celsius',
  Fahrenheit: 'fahrenheit',
};

/**
 * One converted temperature, ready to be written back to the step it came from.
 *
 * Raised rather than saved here: the recipe, its concurrency token and the seam that writes a version all
 * belong to the editor, and a calculation panel that wrote to the recipe itself would be a second way to
 * save one (REC-004).
 */
export interface TemperatureApplication {
  readonly stepId: string;
  readonly stepLabel: string;
  readonly temperatureValue: number;
  readonly temperatureUnitId: string;
  /** How the change reads, for the diff and the confirmation. */
  readonly beforeLabel: string;
  readonly afterLabel: string;
}

/** The step a conversion came from, and the inputs it was started with. */
interface PrefilledStep {
  readonly id: string;
  readonly label: string;
  readonly value: number;
  readonly scale: TemperatureScale;
}

/**
 * How each formula reads in words.
 *
 * Keyed by the server's own formula string, which is one of exactly three this calculator emits. An
 * unrecognised one still renders — the symbols are shown either way — it simply gets no gloss beneath it.
 */
const FORMULA_EXPLANATIONS: Readonly<Record<string, string>> = {
  '°F = °C × 9/5 + 32': 'Multiplied by nine fifths, then 32 added.',
  '°C = (°F − 32) × 5/9': 'Reduced by 32, then multiplied by five ninths.',
  'identity — source and target scale are the same': 'Unchanged: the two scales are the same.',
};

/** Renders a canonical fraction as text. String composition only: nothing here divides, rounds or compares. */
function formatQuantity(quantity: Quantity): string {
  return quantity.denominator === '1' ? quantity.numerator : `${quantity.numerator}/${quantity.denominator}`;
}

let nextInstance = 0;

/**
 * The recipe editor's temperature-conversion preview (EDITOR-UI-005, over the read-only
 * `calculations/convert-temperature` route).
 *
 * **Nothing here is saved, and nothing here is calculated.** The converted value, the formula, the precision
 * and the rounding mode are all the server's.
 *
 * **A temperature is only ever converted from a number.** recipes.md forbids inferring one from vague heat
 * language, so a step described only as "over medium-high" is listed as unresolved and cannot fill this form
 * — there is no path here that turns prose into a temperature.
 *
 * **Echoing a safety note back is not confirming it.** The oven-mode context and safety note are carried
 * through the calculation untouched, and the result says so in as many words: the system has not checked
 * either, and nothing here makes a claim that any temperature is safe.
 */
@Component({
  selector: 'cp-recipe-temperature-conversion',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpFieldComponent, CpStatusPillComponent],
  templateUrl: './recipe-temperature-conversion.component.html',
  styleUrl: './recipe-temperature-conversion.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeTemperatureConversionComponent {
  private readonly calculationService = inject(RecipeCalculationService);
  private readonly referenceService = inject(ReferenceService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  readonly sourceVersionNumber = input<number | null>(null);

  /** The recipe's saved steps, offered as starting points. Never edited, never submitted. */
  readonly savedInstructionGroups = input<readonly RecipeInstructionGroup[]>([]);

  /** Whether the form beside this panel has edits that a write to the recipe would have to reckon with. */
  readonly editorIsDirty = input(false);

  /**
   * A converted temperature the creator has confirmed they want written back.
   *
   * The panel raises it; the editor performs it through the ordinary update seam. Nothing here writes.
   */
  readonly applyRequested = output<TemperatureApplication>();

  /**
   * The unit catalogue, read only to tell which scale a step's recorded temperature unit names, and to name
   * the unit a converted temperature is written back in.
   *
   * A failure to load is a fact about the fetch, not about the recipe, and {@link catalogueFailed} keeps the
   * two apart: without the catalogue a step still fills its number, and the reason its scale is unresolved is
   * stated as a failed lookup rather than as something the recipe does or does not record.
   */
  private readonly unitCatalogue = signal<readonly MeasurementUnit[]>([]);

  private readonly catalogueFailed = signal(false);

  private readonly instance = nextInstance++;
  readonly headingId = `temperature-conversion-heading-${this.instance}`;
  readonly valueFieldId = `temperature-value-${this.instance}`;
  readonly precisionFieldId = `temperature-precision-${this.instance}`;
  readonly ovenModeFieldId = `temperature-oven-mode-${this.instance}`;
  readonly safetyNoteFieldId = `temperature-safety-note-${this.instance}`;
  readonly prefillFieldId = `temperature-prefill-${this.instance}`;
  readonly fromLegendId = `temperature-from-${this.instance}`;
  readonly toLegendId = `temperature-to-${this.instance}`;

  readonly maxPrecision = MAX_DISPLAY_PRECISION;
  readonly scales = TEMPERATURE_SCALES;
  readonly scaleLabels = TEMPERATURE_SCALE_LABELS;
  readonly scaleSymbols = TEMPERATURE_SCALE_SYMBOLS;

  readonly temperatureValue = signal<number | null>(null);
  readonly fromScale = signal<TemperatureScale | null>(null);
  readonly toScale = signal<TemperatureScale>('Fahrenheit');
  readonly precisionValue = signal<number | null>(0);
  readonly ovenModeContext = signal('');
  readonly safetyNote = signal('');

  /** Said beside the scale chooser when a step filled a value but recorded no scale to go with it. */
  private readonly scaleNoteSignal = signal<string | null>(null);
  readonly scaleNote = this.scaleNoteSignal.asReadonly();

  /**
   * The step this conversion was started from, when it was started from one.
   *
   * Recorded so a converted number is only ever written back to the step it actually came from. A
   * hand-typed temperature has no step to belong to, and guessing one would put a number into a recipe that
   * nothing in the recipe said.
   */
  private readonly prefilledStep = signal<PrefilledStep | null>(null);

  /**
   * The inputs the result on screen was actually computed from, captured when the request was sent.
   *
   * Held separately from the form, which the creator can keep editing after a result lands. Pairing a result
   * with the form's current contents rather than with its own inputs is how a converted number could be
   * attributed to a value it was never computed from.
   */
  private readonly convertedFrom = signal<{ readonly value: number; readonly scale: TemperatureScale } | null>(null);

  private readonly localErrorSignal = signal<string | null>(null);
  readonly localError = this.localErrorSignal.asReadonly();

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly resultSignal = signal<RecipeTemperatureConversionResult | null>(null);
  readonly result = this.resultSignal.asReadonly();

  readonly isWorking = computed(() => this.requestSignal().status === 'working');

  readonly canSubmit = computed(() => !this.isWorking() && this.sourceVersionNumber() !== null);

  constructor() {
    void this.loadUnits();

    // A recipe replaced under the panel invalidates a result computed from the version it named. Cleared for
    // the reason the unit section gives: one number about one input, with nothing on the card worth keeping.
    effect(() => {
      const current = this.sourceVersionNumber();
      const result = this.resultSignal();
      if (result !== null && current !== null && result.sourceVersionNumber !== current) {
        this.resultSignal.set(null);
        // The link to the step goes with it: the step list this was chosen from belongs to the version the
        // recipe has moved past.
        this.prefilledStep.set(null);
        this.requestSignal.set({ status: 'idle' });
      }
    });
  }

  async loadUnits(): Promise<void> {
    this.catalogueFailed.set(false);
    const outcome = await this.referenceService.listUnits();

    if (outcome.status === 'found') {
      this.unitCatalogue.set(outcome.units);
      return;
    }

    // Recorded rather than swallowed. Dropping it silently made every step report "records a temperature but
    // not which scale it is in" — a claim about the creator's recipe, when what actually happened is that a
    // reference lookup failed.
    this.catalogueFailed.set(true);
  }

  /** Said once, at the top, when the catalogue could not be read — so no step has to explain it itself. */
  readonly catalogueProblem = computed<string | null>(() =>
    this.catalogueFailed()
      ? "The unit catalogue couldn't be loaded, so a step's recorded scale can't be read and a converted temperature can't be saved back yet."
      : null,
  );

  private readonly unitsById = computed<ReadonlyMap<string, MeasurementUnit>>(
    () => new Map(this.unitCatalogue().map((unit) => [unit.id, unit])),
  );

  /**
   * Every saved step, including the ones that cannot fill this form.
   *
   * A step whose heat is described only in words is the case recipes.md is most explicit about: it is listed,
   * disabled, and says why. Leaving it out would read as a step with no heat in it at all, and quietly
   * offering it would invite a number that nothing in the recipe actually records.
   */
  readonly stepOptions = computed<readonly StepTemperatureOption[]>(() => {
    const unitsById = this.unitsById();

    return this.savedInstructionGroups().flatMap((group) =>
      group.steps.map((step) => {
        const summary = step.text.length > 60 ? `${step.text.slice(0, 60)}…` : step.text;
        const label = `Step ${step.sortOrder + 1} — ${summary}`;

        if (step.temperatureValue === null) {
          return {
            id: step.id,
            label,
            unresolvedReason: 'no temperature recorded',
            value: null,
            scale: null,
            scaleUnresolvedNote: null,
          };
        }

        const unit = step.temperatureUnitId === null ? undefined : unitsById.get(step.temperatureUnitId);
        const scale = unit ? (SCALE_BY_UNIT_CODE[unit.code] ?? null) : null;

        return {
          id: step.id,
          label,
          unresolvedReason: null,
          value: step.temperatureValue,
          scale,
          scaleUnresolvedNote:
            scale !== null
              ? null
              : this.catalogueFailed()
                ? "The unit catalogue couldn't be read, so this step's scale is unknown here — choose one below."
                : 'This step records a temperature but not which scale it is in — choose one below.',
        };
      }),
    );
  });

  readonly hasSteps = computed(() => this.stepOptions().length > 0);

  fieldError(field: string): string | null {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;
    return state.fieldErrors[field]?.[0] ?? null;
  }

  readonly valueError = computed(() => this.localErrorSignal() ?? this.fieldError('value'));
  readonly precisionError = computed(() => this.fieldError('precision'));
  readonly scaleError = computed(() => this.fieldError('fromScale') ?? this.fieldError('toScale'));

  /**
   * A refusal naming a field this panel has no control for — in practice only `sourceVersionNumber`,
   * which no creator can set. Surfaced as the server worded it, because the alternative was what this
   * used to do: clear the card, show nothing, and leave the button looking as though it did not work.
   */
  private readonly unboundFieldError = computed<string | null>(() => {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;

    const bound: readonly string[] = ['value', 'precision', 'fromScale', 'toScale'];
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
    return result === null ? '' : 'Converted: ' + (this.convertedLabel() ?? '');
  });

  readonly requestProblem = computed<string | null>(() => {
    switch (this.requestSignal().status) {
      case 'version_not_found':
        return 'That version no longer exists — reload the recipe and convert again.';
      case 'not_found':
        return "This recipe couldn't be found. It may have been removed.";
      case 'unauthorized':
        return 'You are no longer signed in, or no longer have access to this recipe. Sign in again to continue.';
      case 'unavailable':
        return "Couldn't convert that. Check your connection and try again.";
      case 'invalid_request':
        // Every field the server can name is bound to a control except sourceVersionNumber, which no panel
        // lets a creator set. Without this arm that refusal cleared the card and said nothing at all, so the
        // button appeared simply not to work.
        return this.unboundFieldError();

      default:
        return null;
    }
  });

  readonly convertedLabel = computed<string | null>(() => {
    const result = this.resultSignal();
    if (result === null) return null;
    return `${result.result.convertedDisplayValue} ${TEMPERATURE_SCALE_SYMBOLS[result.result.convertedScale]}`;
  });

  readonly exactLabel = computed<string | null>(() => {
    const result = this.resultSignal();
    if (result === null || result.result.convertedValue.denominator === '1') return null;
    return formatQuantity(result.result.convertedValue);
  });

  /** The formula in words, when it is one this build recognises. Null simply means no gloss is shown. */
  readonly formulaExplanation = computed<string | null>(() => {
    const result = this.resultSignal();
    return result === null ? null : (FORMULA_EXPLANATIONS[result.result.formula] ?? null);
  });

  readonly roundingLabel = computed(() => {
    const result = this.resultSignal();
    return result === null ? null : MIDPOINT_ROUNDING_LABELS[result.result.rounding];
  });

  /** Whether anything was carried through, and so whether the block saying so is worth showing. */
  readonly hasCarriedContext = computed(() => {
    const result = this.resultSignal();
    return result !== null && (result.result.ovenModeContext !== null || result.result.safetyNote !== null);
  });

  /**
   * The step a result could be written back to — the one it was started from, when the form still holds
   * exactly what was sent.
   *
   * Editing the value or the scale after starting from a step breaks the link deliberately: the number on
   * screen is then no longer the step's, and writing it back would record something the recipe never said.
   */
  private readonly applicableStep = computed<PrefilledStep | null>(() => {
    const prefilled = this.prefilledStep();
    const converted = this.convertedFrom();
    if (prefilled === null || converted === null) return null;

    // Three things have to agree, not two. Checking the form against the step proves the creator has not
    // moved on; checking the *result's own inputs* against the step is what proves the number on the card
    // was computed from that step. Without the second check, converting 200 and then typing 180 back into
    // the box made a 392 °F result applicable to a 180 °C step — a value the recipe never held, under a
    // confirmation reading "180 °C → 392 °F".
    if (this.temperatureValue() !== prefilled.value || this.fromScale() !== prefilled.scale) return null;
    if (converted.value !== prefilled.value || converted.scale !== prefilled.scale) return null;

    return prefilled;
  });

  /** The catalogue's unit for the scale a result converted to; null when the catalogue cannot name it. */
  private readonly convertedUnitId = computed<string | null>(() => {
    const result = this.resultSignal();
    if (result === null) return null;

    const code = UNIT_CODE_BY_SCALE[result.result.convertedScale];
    return this.unitCatalogue().find((unit) => unit.code === code)?.id ?? null;
  });

  /**
   * Why this result cannot be written back, when it cannot. Null means it can.
   *
   * Every reason is shown as text beside a disabled control rather than as an absent one: a button that is
   * simply not there says nothing about what would bring it back.
   */
  readonly applyBlockedReason = computed<string | null>(() => {
    if (this.resultSignal() === null) return null;

    if (this.applicableStep() === null) {
      return 'This converted a number you typed rather than one the recipe records. Start from a step to save a conversion back to it.';
    }

    if (this.sourceVersionNumber() === null) {
      return 'This recipe has no saved version to write to.';
    }

    if (this.editorIsDirty()) {
      return 'Save or discard your changes first. Applying writes to the recipe as it was last saved.';
    }

    if (this.convertedUnitId() === null) {
      return "The unit catalogue couldn't name the converted scale, so this can't be saved back yet.";
    }

    // A Celsius→Celsius conversion of a step already recording 180 °C changes nothing. The server correctly
    // writes no version for it, but it still answers success — so without this the creator confirmed a change
    // and was told the recipe had moved to a version that was never written.
    const step = this.applicableStep();
    const result = this.resultSignal();
    if (
      step !== null &&
      result !== null &&
      result.result.convertedDisplayValue === step.value &&
      result.result.convertedScale === step.scale
    ) {
      return 'This step already records that temperature, so there is nothing to change.';
    }

    return null;
  });

  readonly canApply = computed(
    () => this.resultSignal() !== null && !this.isWorking() && this.applyBlockedReason() === null,
  );

  /** How the change reads, for the diff above the button and for the confirmation. */
  readonly applyDiff = computed<{ readonly label: string; readonly before: string; readonly after: string } | null>(
    () => {
      const step = this.applicableStep();
      const result = this.resultSignal();
      if (step === null || result === null) return null;

      return {
        label: step.label,
        before: `${step.value} ${TEMPERATURE_SCALE_SYMBOLS[step.scale]}`,
        after: `${result.result.convertedDisplayValue} ${TEMPERATURE_SCALE_SYMBOLS[result.result.convertedScale]}`,
      };
    },
  );

  /**
   * Asks the editor to write this conversion back.
   *
   * Every precondition is re-checked here rather than trusted from the disabled state, because a disabled
   * button is a courtesy and this is the actual gate: the version the preview was computed against must
   * still be the recipe's own, or the change would be composed against content that has moved on.
   */
  requestApply(): void {
    const step = this.applicableStep();
    const result = this.resultSignal();
    const unitId = this.convertedUnitId();
    const diff = this.applyDiff();

    if (step === null || result === null || unitId === null || diff === null) return;
    if (this.editorIsDirty() || result.sourceVersionNumber !== this.sourceVersionNumber()) return;

    this.applyRequested.emit({
      stepId: step.id,
      stepLabel: step.label,
      temperatureValue: result.result.convertedDisplayValue,
      temperatureUnitId: unitId,
      beforeLabel: diff.before,
      afterLabel: diff.after,
    });
  }

  setFromScale(scale: TemperatureScale): void {
    this.fromScale.set(scale);
    this.scaleNoteSignal.set(null);
    this.localErrorSignal.set(null);
  }

  setToScale(scale: TemperatureScale): void {
    this.toScale.set(scale);
  }

  /** Fills the form from a saved step. A step with no recorded temperature fills nothing. */
  prefillFrom(stepId: string): void {
    const option = this.stepOptions().find((step) => step.id === stepId);
    if (!option || option.unresolvedReason !== null) return;

    // Only a step that records both a value and a scale can be written back to: without a scale there is
    // nothing to say what the stored number means, and a conversion of it would be a conversion of a guess.
    this.prefilledStep.set(
      option.value !== null && option.scale !== null
        ? { id: option.id, label: option.label, value: option.value, scale: option.scale }
        : null,
    );

    this.temperatureValue.set(option.value);
    // A step whose scale is unrecorded leaves the chooser as it was and says so, rather than defaulting to
    // one: reading 180 as Fahrenheit when the recipe meant Celsius is the error this avoids.
    this.fromScale.set(option.scale ?? this.fromScale());
    this.scaleNoteSignal.set(option.scaleUnresolvedNote);
    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'idle' });
    this.resultSignal.set(null);
  }

  async convert(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    if (versionNumber === null || this.isWorking()) return;

    const value = this.temperatureValue();
    const fromScale = this.fromScale();
    const precision = this.precisionValue();

    // Only the shape is checked here. A temperature may be negative, so nothing is rejected for its sign —
    // only for not being a number at all, or for no scale having been chosen to read it in.
    if (value === null || !Number.isFinite(value)) {
      this.localErrorSignal.set('Enter a temperature.');
      return;
    }

    if (fromScale === null) {
      this.localErrorSignal.set('Choose which scale this temperature is in.');
      return;
    }

    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'working' });

    const outcome = await this.calculationService.convertTemperature(this.workspaceSlug(), this.recipeId(), {
      sourceVersionNumber: versionNumber,
      value,
      fromScale,
      toScale: this.toScale(),
      // Negative precision is the server's own refusal; an empty box is this component's.
      precision: precision ?? 0,
      ovenModeContext: this.ovenModeContext().trim() || null,
      safetyNote: this.safetyNote().trim() || null,
    });

    if (outcome.status === 'converted') {
      this.resultSignal.set(outcome.result);
      this.convertedFrom.set({ value, scale: fromScale });
      this.requestSignal.set({ status: 'done' });
      return;
    }

    this.convertedFrom.set(null);
    this.resultSignal.set(null);
    this.requestSignal.set(
      outcome.status === 'invalid_request'
        ? { status: 'invalid_request', fieldErrors: outcome.fieldErrors }
        : { status: outcome.status },
    );
  }
}
