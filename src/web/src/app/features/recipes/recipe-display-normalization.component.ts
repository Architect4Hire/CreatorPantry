import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent } from '@creator-pantry/ui';

import {
  FRACTION_PRESENTATION_EXPLANATIONS,
  FRACTION_PRESENTATION_LABELS,
  FractionPresentation,
  RecipeQuantityDisplayResult,
} from '../../models/recipe-display.models';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import {
  MEASUREMENT_DIMENSION_LABELS,
  MAX_DISPLAY_PRECISION,
  MIDPOINT_ROUNDING_LABELS,
  MeasurementDimension,
  MeasurementUnit,
  MidpointRounding,
  SUPPORTED_ROUNDING_MODES,
} from '../../models/reference.models';
import { RecipeCalculationService } from '../../services/recipe-calculation.service';
import { ReferenceService } from '../../services/reference.service';

type CatalogueState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly units: readonly MeasurementUnit[] }
  | { readonly status: 'unavailable' };

type RequestState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  | { readonly status: 'done' }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/** One saved ingredient line offered as a starting point, and why it may not be usable as one. */
export interface DisplayLineOption {
  readonly id: string;
  readonly label: string;
  readonly unresolvedReason: string | null;
  readonly value: number | null;
  readonly upperValue: number | null;
  readonly unitId: string | null;
}

interface UnitGroup {
  readonly label: string;
  readonly units: readonly MeasurementUnit[];
}

const DIMENSION_ORDER: readonly MeasurementDimension[] = ['Mass', 'Volume', 'Count', 'Temperature', 'Qualitative'];

/**
 * The rounding modes the calculation actually supports.
 *
 * Two, not the five `MidpointRounding` defines: only half-to-even and half-away-from-zero are meaningful for
 * an exact rational, and the server refuses the rest. Offering all five meant three of the choices failed —
 * and failed *intermittently*, because a value matching a kitchen fraction never reaches the rounding step at
 * all, so the same mode appeared to work for one quantity and broke on the next.
 */
export const ROUNDING_MODES: readonly MidpointRounding[] = SUPPORTED_ROUNDING_MODES;

let nextInstance = 0;

/**
 * The recipe editor's display-normalization preview (EDITOR-UI-005, over the read-only
 * `calculations/normalize-display` route).
 *
 * **Presentation only, and the card says so twice.** The canonical value and the rendered text sit side by
 * side under their own labels, because the one thing this section exists to show is that they are not the
 * same fact: the exact quantity is what the recipe holds, and the text is only how it reads. Nothing here is
 * persisted, and no rendered string is ever fed back into a canonical quantity (recipes.md, ING-006).
 *
 * **Nothing here is calculated.** The text, the presentation each bound was rendered with, the precision and
 * the rounding mode are all the server's. The canonical value shown beside them is the number the creator
 * entered, echoed from the signal that sent it — never recomputed from the response.
 */
@Component({
  selector: 'cp-recipe-display-normalization',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpFieldComponent],
  templateUrl: './recipe-display-normalization.component.html',
  styleUrl: './recipe-display-normalization.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeDisplayNormalizationComponent {
  private readonly calculationService = inject(RecipeCalculationService);
  private readonly referenceService = inject(ReferenceService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  readonly sourceVersionNumber = input<number | null>(null);

  /** The recipe's saved ingredient lines, offered as starting points. Never edited, never submitted. */
  readonly savedIngredientGroups = input<readonly RecipeIngredientGroup[]>([]);

  private readonly instance = nextInstance++;
  readonly headingId = `display-heading-${this.instance}`;
  readonly valueFieldId = `display-value-${this.instance}`;
  readonly upperFieldId = `display-upper-${this.instance}`;
  readonly unitFieldId = `display-unit-${this.instance}`;
  readonly precisionFieldId = `display-precision-${this.instance}`;
  readonly abbreviationFieldId = `display-abbreviation-${this.instance}`;
  readonly roundingFieldId = `display-rounding-${this.instance}`;
  readonly prefillFieldId = `display-prefill-${this.instance}`;

  readonly roundingModes = ROUNDING_MODES;
  readonly maxPrecision = MAX_DISPLAY_PRECISION;
  readonly roundingLabels = MIDPOINT_ROUNDING_LABELS;

  private readonly catalogueSignal = signal<CatalogueState>({ status: 'loading' });
  readonly catalogue = this.catalogueSignal.asReadonly();

  readonly value = signal<number | null>(null);
  readonly upperValue = signal<number | null>(null);
  readonly unitId = signal('');
  readonly precision = signal<number | null>(2);
  readonly useAbbreviation = signal(false);
  readonly rounding = signal<MidpointRounding>('ToEven');

  private readonly localErrorSignal = signal<string | null>(null);
  readonly localError = this.localErrorSignal.asReadonly();

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly resultSignal = signal<RecipeQuantityDisplayResult | null>(null);
  readonly result = this.resultSignal.asReadonly();

  /**
   * The exact value the rendered text was built from, held as it was sent.
   *
   * Kept apart from the form fields so that editing a box after a render cannot silently restate what the
   * card's "canonical value" was — the pairing on screen has to stay a true pairing.
   */
  private readonly renderedCanonical = signal<{ value: number; upperValue: number | null; unitId: string } | null>(
    null,
  );

  readonly isWorking = computed(() => this.requestSignal().status === 'working');

  readonly canSubmit = computed(
    () => !this.isWorking() && this.sourceVersionNumber() !== null && this.catalogueSignal().status === 'ready',
  );

  constructor() {
    void this.loadCatalogue();

    effect(() => {
      const current = this.sourceVersionNumber();
      const result = this.resultSignal();
      if (result !== null && current !== null && result.sourceVersionNumber !== current) {
        this.resultSignal.set(null);
        this.renderedCanonical.set(null);
        this.requestSignal.set({ status: 'idle' });
      }
    });
  }

  async loadCatalogue(): Promise<void> {
    this.catalogueSignal.set({ status: 'loading' });
    const outcome = await this.referenceService.listUnits();
    this.catalogueSignal.set(
      outcome.status === 'found' ? { status: 'ready', units: outcome.units } : { status: 'unavailable' },
    );
  }

  readonly unitGroups = computed<readonly UnitGroup[]>(() => {
    const catalogue = this.catalogueSignal();
    if (catalogue.status !== 'ready') return [];

    return DIMENSION_ORDER.map((dimension) => ({
      label: MEASUREMENT_DIMENSION_LABELS[dimension],
      units: catalogue.units
        .filter((unit) => unit.dimension === dimension)
        .sort((left, right) => left.displayName.localeCompare(right.displayName)),
    })).filter((group) => group.units.length > 0);
  });

  private readonly unitsById = computed<ReadonlyMap<string, MeasurementUnit>>(() => {
    const catalogue = this.catalogueSignal();
    if (catalogue.status !== 'ready') return new Map();
    return new Map(catalogue.units.map((unit) => [unit.id, unit]));
  });

  /**
   * The saved lines, every one of them listed.
   *
   * Starting from a real line is what makes the distinction below concrete: the canonical value is then a
   * quantity the recipe actually holds, not a number typed into a box a moment ago. A line that cannot fill
   * the form stays listed with its reason rather than being filtered out.
   */
  readonly ingredientLineOptions = computed<readonly DisplayLineOption[]>(() =>
    this.savedIngredientGroups().flatMap((group) =>
      group.ingredients.map((ingredient) => ({
        id: ingredient.id,
        label: ingredient.displayText,
        unresolvedReason:
          ingredient.quantity === null
            ? 'no amount recorded'
            : ingredient.measurementUnitId === null
              ? 'no unit recorded'
              : null,
        value: ingredient.quantity,
        upperValue: ingredient.quantityUpper,
        unitId: ingredient.measurementUnitId,
      })),
    ),
  );

  readonly hasIngredientLines = computed(() => this.ingredientLineOptions().length > 0);

  fieldError(field: string): string | null {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;
    return state.fieldErrors[field]?.[0] ?? null;
  }

  readonly valueError = computed(() => this.localErrorSignal() ?? this.fieldError('value'));
  readonly upperValueError = computed(() => this.fieldError('upperValue'));
  readonly unitError = computed(() => this.fieldError('unitId'));
  readonly precisionError = computed(() => this.fieldError('precision'));
  readonly roundingError = computed(() => this.fieldError('rounding'));

  /**
   * A refusal naming a field this panel has no control for — in practice only `sourceVersionNumber`, which
   * no creator can set. Surfaced as the server worded it, because the alternative is to show nothing at all
   * and leave the button looking as though it did not work.
   */
  private readonly unboundFieldError = computed<string | null>(() => {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;

    const bound: readonly string[] = ['value', 'upperValue', 'unitId', 'precision', 'rounding'];
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
    return result === null ? '' : 'Reads as ' + result.result.text + '.';
  });

  readonly requestProblem = computed<string | null>(() => {
    switch (this.requestSignal().status) {
      case 'invalid_request':
        return this.unboundFieldError();
      case 'version_not_found':
        return 'That version no longer exists — reload the recipe and try again.';
      case 'not_found':
        return "This recipe couldn't be found. It may have been removed.";
      case 'unauthorized':
        return 'You are no longer signed in, or no longer have access to this recipe. Sign in again to continue.';
      case 'unavailable':
        return "Couldn't render that. Check your connection and try again.";
      default:
        return null;
    }
  });

  /**
   * The exact value the text was built from, with its unit — the canonical half of the pairing.
   *
   * Composed from what was sent, not from the response: the response carries only the rendering, and
   * rebuilding the canonical value from it would be exactly the substitution this section warns against.
   */
  readonly canonicalLabel = computed<string | null>(() => {
    const canonical = this.renderedCanonical();
    if (canonical === null) return null;

    const unit = this.unitsById().get(canonical.unitId);
    const amount =
      canonical.upperValue === null ? String(canonical.value) : `${canonical.value} to ${canonical.upperValue}`;

    return unit ? `${amount} ${unit.displayName}` : amount;
  });

  readonly presentationLabel = computed<string | null>(() => {
    const result = this.resultSignal();
    return result === null ? null : FRACTION_PRESENTATION_LABELS[result.result.presentation];
  });

  readonly presentationExplanation = computed<string | null>(() => {
    const result = this.resultSignal();
    return result === null ? null : FRACTION_PRESENTATION_EXPLANATIONS[result.result.presentation];
  });

  /** Set only for a range: the upper bound can be rendered differently from the lower one. */
  readonly upperPresentationLabel = computed<string | null>(() => {
    const upper = this.resultSignal()?.result.upperPresentation;
    return upper ? FRACTION_PRESENTATION_LABELS[upper as FractionPresentation] : null;
  });

  readonly roundingLabel = computed<string | null>(() => {
    const result = this.resultSignal();
    return result === null ? null : MIDPOINT_ROUNDING_LABELS[result.result.rounding];
  });

  /** Fills the form from a saved line. A line with an unresolved reason fills nothing. */
  prefillFrom(lineId: string): void {
    const option = this.ingredientLineOptions().find((line) => line.id === lineId);
    if (!option || option.unresolvedReason !== null) return;

    this.value.set(option.value);
    this.upperValue.set(option.upperValue);
    this.unitId.set(option.unitId ?? '');
    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'idle' });
    this.resultSignal.set(null);
    this.renderedCanonical.set(null);
  }

  setRounding(mode: string): void {
    if ((ROUNDING_MODES as readonly string[]).includes(mode)) this.rounding.set(mode as MidpointRounding);
  }

  async render(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    if (versionNumber === null || this.isWorking()) return;

    const value = this.value();
    const unitId = this.unitId();

    // Only the shape is checked here. A negative value, and an upper bound that is not above the lower one,
    // are the server's own rules and it states them in its own words.
    if (value === null || !Number.isFinite(value)) {
      this.localErrorSignal.set('Enter a value.');
      return;
    }

    if (unitId === '') {
      this.localErrorSignal.set('Choose a unit.');
      return;
    }

    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'working' });

    const upperValue = this.upperValue();
    const outcome = await this.calculationService.normalizeDisplay(this.workspaceSlug(), this.recipeId(), {
      sourceVersionNumber: versionNumber,
      value,
      upperValue,
      unitId,
      precision: this.precision() ?? 0,
      useAbbreviation: this.useAbbreviation(),
      rounding: this.rounding(),
    });

    if (outcome.status === 'rendered') {
      this.resultSignal.set(outcome.result);
      this.renderedCanonical.set({ value, upperValue, unitId });
      this.requestSignal.set({ status: 'done' });
      return;
    }

    this.resultSignal.set(null);
    this.renderedCanonical.set(null);
    this.requestSignal.set(
      outcome.status === 'invalid_request'
        ? { status: 'invalid_request', fieldErrors: outcome.fieldErrors }
        : { status: outcome.status },
    );
  }
}
