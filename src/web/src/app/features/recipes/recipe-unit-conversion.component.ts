import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent } from '@creator-pantry/ui';

import {
  MIDPOINT_ROUNDING_LABELS,
  Quantity,
  RecipeUnitConversionResult,
  UnitConversionMethod,
} from '../../models/recipe-conversion.models';
import { MEASUREMENT_DIMENSION_LABELS, MeasurementDimension, MeasurementUnit } from '../../models/reference.models';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RecipeCalculationService } from '../../services/recipe-calculation.service';
import { ReferenceService } from '../../services/reference.service';

/** Where the shared unit catalogue stands. Without it there is nothing to pick between, so the form waits. */
type CatalogueState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly units: readonly MeasurementUnit[] }
  | { readonly status: 'unavailable' };

type RequestState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  | { readonly status: 'done' }
  /**
   * Every pair of units that cannot be bridged lands here — different dimensions, a Mass/Volume pair with no
   * approved density, or a temperature unit. The server's own sentence says which.
   */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/** One saved ingredient line offered as a starting point, and why it may not be usable as one. */
export interface IngredientLineOption {
  readonly id: string;
  readonly label: string;
  /** The reason this line cannot fill the form, when it cannot. Null means it can. */
  readonly unresolvedReason: string | null;
  readonly quantity: number | null;
  readonly unitId: string | null;
}

/** Units grouped for a picker, so a creator scans weights and volumes rather than one flat list. */
interface UnitGroup {
  readonly label: string;
  readonly units: readonly MeasurementUnit[];
}

/** The order dimensions appear in the pickers — the two that actually convert, first. */
const DIMENSION_ORDER: readonly MeasurementDimension[] = ['Mass', 'Volume', 'Count', 'Temperature', 'Qualitative'];

/**
 * How each method reads to a creator. Never only the enum name: the whole point of this panel is that the
 * creator can see how a number was arrived at.
 */
const METHOD_LABELS: Readonly<Record<UnitConversionMethod, string>> = {
  SameDimensionFactor: 'Same-dimension factor',
  IngredientDensity: 'Ingredient density',
};

const METHOD_EXPLANATIONS: Readonly<Record<UnitConversionMethod, string>> = {
  SameDimensionFactor: "Multiplied by the ratio of the two units' own base factors.",
  IngredientDensity: 'Bridged between weight and volume using the cited density reference below.',
};

/** Renders a canonical fraction as text. String composition only: nothing here divides, rounds or compares. */
function formatQuantity(quantity: Quantity): string {
  return quantity.denominator === '1' ? quantity.numerator : `${quantity.numerator}/${quantity.denominator}`;
}

let nextInstance = 0;

/**
 * The recipe editor's unit-conversion preview (EDITOR-UI-005, over the read-only `calculations/convert-units`
 * route).
 *
 * **Nothing here is saved, and nothing here is calculated.** The converted amount, the formula, the precision
 * and the rounding mode are all the server's; this component picks the inputs and renders the answer.
 *
 * **An unsupported conversion is never shown as a success.** Units of different dimensions, a weight/volume
 * pair with no approved density, and temperature units are all refused by the server, and a refusal clears
 * any previous result rather than sitting beside it — a converted amount left on screen next to a refusal
 * about different units reads as a footnote on a working answer.
 */
@Component({
  selector: 'cp-recipe-unit-conversion',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpFieldComponent],
  templateUrl: './recipe-unit-conversion.component.html',
  styleUrl: './recipe-unit-conversion.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeUnitConversionComponent {
  private readonly calculationService = inject(RecipeCalculationService);
  private readonly referenceService = inject(ReferenceService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The version a preview is computed in the context of. Null while loading; the form refuses to convert. */
  readonly sourceVersionNumber = input<number | null>(null);

  /** The recipe's saved ingredient lines, offered as starting points. Never edited, never submitted. */
  readonly savedIngredientGroups = input<readonly RecipeIngredientGroup[]>([]);

  private readonly instance = nextInstance++;
  readonly headingId = `unit-conversion-heading-${this.instance}`;
  readonly quantityFieldId = `unit-conversion-quantity-${this.instance}`;
  readonly fromFieldId = `unit-conversion-from-${this.instance}`;
  readonly toFieldId = `unit-conversion-to-${this.instance}`;
  readonly prefillFieldId = `unit-conversion-prefill-${this.instance}`;

  private readonly catalogueSignal = signal<CatalogueState>({ status: 'loading' });
  readonly catalogue = this.catalogueSignal.asReadonly();

  readonly quantityValue = signal<number | null>(null);
  readonly fromUnitId = signal('');
  readonly toUnitId = signal('');

  private readonly localErrorSignal = signal<string | null>(null);
  readonly localError = this.localErrorSignal.asReadonly();

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly resultSignal = signal<RecipeUnitConversionResult | null>(null);
  readonly result = this.resultSignal.asReadonly();

  readonly isWorking = computed(() => this.requestSignal().status === 'working');

  readonly canSubmit = computed(
    () =>
      !this.isWorking() &&
      this.sourceVersionNumber() !== null &&
      this.catalogueSignal().status === 'ready',
  );

  constructor() {
    void this.loadCatalogue();

    // A recipe replaced under the panel — a save, a restore — invalidates a result computed from the version
    // it named. Cleared rather than stamped stale: unlike a scaling table, this is one number about two units
    // the creator may well have changed since, and there is nothing on the card worth keeping.
    effect(() => {
      const current = this.sourceVersionNumber();
      const result = this.resultSignal();
      if (result !== null && current !== null && result.sourceVersionNumber !== current) {
        this.resultSignal.set(null);
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

  /** The pickers' contents, grouped by dimension and ordered so weights and volumes come first. */
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
   * A line that cannot fill the form stays in the list carrying its reason rather than being filtered out:
   * an ingredient that quietly is not offered reads as one the recipe does not have, when what it actually
   * has is a line with no recorded amount or no recorded unit.
   */
  readonly ingredientLineOptions = computed<readonly IngredientLineOption[]>(() =>
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
        quantity: ingredient.quantity,
        unitId: ingredient.measurementUnitId,
      })),
    ),
  );

  readonly hasIngredientLines = computed(() => this.ingredientLineOptions().length > 0);

  /** The server's own message for a field, when it refused the request. */
  fieldError(field: string): string | null {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;
    return state.fieldErrors[field]?.[0] ?? null;
  }

  readonly quantityError = computed(() => this.localErrorSignal() ?? this.fieldError('quantity'));
  readonly fromUnitError = computed(() => this.fieldError('fromUnitId'));

  /**
   * Where every "these cannot be converted" refusal lands — the server puts all of them on `toUnitId`, each
   * with its own sentence, so the reason is the server's and not a guess made from a status code.
   */
  readonly toUnitError = computed(() => this.fieldError('toUnitId'));

  /**
   * A refusal naming a field this panel has no control for — in practice only `sourceVersionNumber`,
   * which no creator can set. Surfaced as the server worded it, because the alternative was what this
   * used to do: clear the card, show nothing, and leave the button looking as though it did not work.
   */
  private readonly unboundFieldError = computed<string | null>(() => {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;

    const bound: readonly string[] = ['quantity', 'fromUnitId', 'toUnitId'];
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

  readonly methodLabel = computed(() => {
    const result = this.resultSignal();
    return result === null ? null : METHOD_LABELS[result.result.method];
  });

  readonly methodExplanation = computed(() => {
    const result = this.resultSignal();
    return result === null ? null : METHOD_EXPLANATIONS[result.result.method];
  });

  readonly roundingLabel = computed(() => {
    const result = this.resultSignal();
    return result === null ? null : MIDPOINT_ROUNDING_LABELS[result.result.rounding];
  });

  /**
   * The unit the result on screen was actually converted to, captured when the request was sent.
   *
   * Held apart from the picker, which the creator can keep changing after a result lands. Labelling the
   * server's number with whatever unit is currently selected let a gram figure read as "1 oz" — the same
   * substitution the display panel's `renderedCanonical` exists to prevent.
   */
  private readonly convertedToUnitId = signal<string | null>(null);

  /** The converted amount with the unit it is in — the target unit the request named, not the one on screen. */
  readonly convertedLabel = computed<string | null>(() => {
    const result = this.resultSignal();
    const unitId = this.convertedToUnitId();
    if (result === null || unitId === null) return null;

    const unit = this.unitsById().get(unitId);
    const amount = result.result.convertedDisplayQuantity;
    return unit ? `${amount} ${unit.abbreviation}` : String(amount);
  });

  /** The exact result, shown whenever the rounded decimal beside it cannot state it without rounding. */
  readonly exactLabel = computed<string | null>(() => {
    const result = this.resultSignal();
    if (result === null || result.result.convertedQuantity.denominator === '1') return null;
    return formatQuantity(result.result.convertedQuantity);
  });

  /** Fills the form from a saved line. A line with an unresolved reason fills nothing. */
  prefillFrom(lineId: string): void {
    const option = this.ingredientLineOptions().find((line) => line.id === lineId);
    if (!option || option.unresolvedReason !== null) return;

    this.quantityValue.set(option.quantity);
    this.fromUnitId.set(option.unitId ?? '');
    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'idle' });
    this.resultSignal.set(null);
  }

  async convert(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    if (versionNumber === null || this.isWorking()) return;

    const quantity = this.quantityValue();
    const fromUnitId = this.fromUnitId();
    const toUnitId = this.toUnitId();

    // Only the shape is checked here — that there is a number and two units at all. Whether the number is one
    // the server accepts, and whether the two units can be bridged, are its rules and it says so in its own
    // words rather than in a second copy of them kept in sync by hand.
    if (quantity === null || !Number.isFinite(quantity)) {
      this.localErrorSignal.set('Enter an amount.');
      return;
    }

    if (fromUnitId === '' || toUnitId === '') {
      this.localErrorSignal.set('Choose both units.');
      return;
    }

    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'working' });

    const outcome = await this.calculationService.convertUnits(this.workspaceSlug(), this.recipeId(), {
      sourceVersionNumber: versionNumber,
      quantity,
      fromUnitId,
      toUnitId,
    });

    if (outcome.status === 'converted') {
      this.resultSignal.set(outcome.result);
      this.convertedToUnitId.set(toUnitId);
      this.requestSignal.set({ status: 'done' });
      return;
    }

    this.convertedToUnitId.set(null);

    // The result is cleared on every refusal, deliberately. A conversion is one number about one pair of
    // units: leaving the last successful one on screen beside "these units cannot be converted between each
    // other" would let a refused conversion read as a qualified success, which ING-004 forbids outright.
    this.resultSignal.set(null);
    this.requestSignal.set(
      outcome.status === 'invalid_request'
        ? { status: 'invalid_request', fieldErrors: outcome.fieldErrors }
        : { status: outcome.status },
    );
  }
}
