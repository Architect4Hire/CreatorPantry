import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpCheckboxComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { RecipeIngredientGroup } from '../../models/recipe.models';
import {
  MAX_DISPLAY_PRECISION,
  MIDPOINT_ROUNDING_LABELS,
  MeasurementUnit,
  MidpointRounding,
  SUPPORTED_ROUNDING_MODES,
} from '../../models/reference.models';
import { RecipeCalculationService } from '../../services/recipe-calculation.service';
import { ReferenceService } from '../../services/reference.service';
import { QuantityReading, QuantityToRead, readQuantities } from './recipe-quantity-reading';

/**
 * The rounding modes the calculation actually supports.
 *
 * Two, not the five `MidpointRounding` defines: only half-to-even and half-away-from-zero are meaningful for
 * an exact rational, and the server refuses the rest.
 */
export const ROUNDING_MODES: readonly MidpointRounding[] = SUPPORTED_ROUNDING_MODES;

/** Where reading the recipe's amounts stands. */
type ReadState = 'idle' | 'reading' | 'ready' | 'unavailable';

/** One saved line as the list shows it: what the recipe records, and how that reads. */
export interface DisplayRow {
  readonly id: string;
  readonly displayText: string;
  /** The amount the recipe records, with its unit — "0.5 cup". Null for a line with no amount. */
  readonly saved: string | null;
  /** How it reads — "½ cup". Null until read, or for a line that cannot be. */
  readonly reading: string | null;
  /** Why a line cannot be read, in words; null for one that can. */
  readonly reason: string | null;
}

let nextInstance = 0;

/**
 * "How your amounts read": every saved ingredient line, read the way a cook writes it, with nothing to fill in.
 *
 * It reads the whole recipe as soon as it opens, and again when an option changes — short unit names, decimal
 * places, rounding — rather than asking for a value, a unit and five settings to answer one amount at a time.
 *
 * **Presentation only.** The text is the server's (`normalize-display`), read one amount at a time through
 * {@link readQuantities}; nothing is calculated here, nothing is saved, and no reading is ever fed back into a
 * quantity (recipes.md, ING-006). What the recipe records stays beside each reading, so the two are never taken
 * for one fact.
 */
@Component({
  selector: 'cp-recipe-display-normalization',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpCheckboxComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
  ],
  templateUrl: './recipe-display-normalization.component.html',
  styleUrl: './recipe-display-normalization.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeDisplayNormalizationComponent {
  private readonly calculationService = inject(RecipeCalculationService);
  private readonly referenceService = inject(ReferenceService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();
  readonly sourceVersionNumber = input<number | null>(null);
  /** The recipe's saved ingredient lines, read as they are. Never edited, never submitted. */
  readonly savedIngredientGroups = input<readonly RecipeIngredientGroup[]>([]);

  private readonly instance = nextInstance++;
  readonly headingId = `display-heading-${this.instance}`;
  readonly abbreviationFieldId = `display-abbreviation-${this.instance}`;
  readonly precisionFieldId = `display-precision-${this.instance}`;
  readonly roundingFieldId = `display-rounding-${this.instance}`;

  readonly roundingModes = ROUNDING_MODES;
  readonly roundingLabels = MIDPOINT_ROUNDING_LABELS;
  readonly precisionOptions = Array.from({ length: MAX_DISPLAY_PRECISION + 1 }, (_, index) => index);

  /** Short unit names — "c", "g", "tbsp". The one option most creators will want, so it is not folded away. */
  readonly useAbbreviation = signal(false);
  /** Decimal places for an amount no kitchen fraction fits, and how a halfway value rounds: folded away. */
  readonly precision = signal(2);
  readonly rounding = signal<MidpointRounding>('ToEven');

  private readonly units = signal<ReadonlyMap<string, MeasurementUnit>>(new Map());
  private readonly readings = signal<ReadonlyMap<string, QuantityReading>>(new Map());
  readonly state = signal<ReadState>('idle');
  /** How many amounts could not be read on the last pass, so a partial answer says so. */
  readonly failed = signal(0);
  private ticket = 0;

  constructor() {
    void this.loadUnits();

    // Read again whenever what is read, or how, changes: another version, other lines, another option.
    effect(() => {
      this.sourceVersionNumber();
      this.savedIngredientGroups();
      this.useAbbreviation();
      this.precision();
      this.rounding();
      untracked(() => void this.readAll());
    });
  }

  /** The unit catalogue, only to name the unit beside each recorded amount; the readings do not need it. */
  private async loadUnits(): Promise<void> {
    const outcome = await this.referenceService.listUnits();
    if (outcome.status === 'found') this.units.set(new Map(outcome.units.map((unit) => [unit.id, unit])));
  }

  /** The lines that can be read: an amount and a unit recorded. */
  private readonly readable = computed<readonly QuantityToRead[]>(() =>
    this.savedIngredientGroups().flatMap((group) =>
      group.ingredients.flatMap((line) =>
        line.quantity !== null && line.measurementUnitId !== null
          ? [{ key: line.id, value: line.quantity, upperValue: line.quantityUpper, unitId: line.measurementUnitId }]
          : [],
      ),
    ),
  );

  /** Reads every line that can be, a few at a time, and keeps only the answer to the latest ask. */
  async readAll(): Promise<void> {
    const version = this.sourceVersionNumber();
    const quantities = this.readable();
    if (version === null) {
      this.state.set('idle');
      return;
    }
    if (quantities.length === 0) {
      this.readings.set(new Map());
      this.failed.set(0);
      this.state.set('ready');
      return;
    }

    const ticket = ++this.ticket;
    this.state.set('reading');
    const { readings, failed } = await readQuantities(
      this.calculationService,
      this.workspaceSlug(),
      this.recipeId(),
      version,
      quantities,
      { precision: this.precision(), useAbbreviation: this.useAbbreviation(), rounding: this.rounding() },
    );
    if (ticket !== this.ticket) return;

    this.readings.set(readings);
    this.failed.set(failed);
    this.state.set(readings.size === 0 && failed > 0 ? 'unavailable' : 'ready');
  }

  readonly rows = computed<readonly DisplayRow[]>(() => {
    const readings = this.readings();
    const units = this.units();
    return this.savedIngredientGroups().flatMap((group) =>
      group.ingredients.map((line) => {
        const unit = line.measurementUnitId === null ? undefined : units.get(line.measurementUnitId);
        const amount =
          line.quantity === null
            ? null
            : line.quantityUpper === null
              ? String(line.quantity)
              : `${line.quantity} to ${line.quantityUpper}`;
        const unitName = unit
          ? line.quantity === 1 && line.quantityUpper === null
            ? unit.displayName
            : unit.pluralName
          : '';
        return {
          id: line.id,
          displayText: line.displayText,
          saved: amount === null ? null : unitName ? `${amount} ${unitName}` : amount,
          reading: readings.get(line.id)?.text ?? null,
          reason:
            line.quantity === null ? 'No amount recorded' : line.measurementUnitId === null ? 'No unit recorded' : null,
        };
      }),
    );
  });

  /** Whether any amount has been read yet: until one has, the list is drawn as its shape. */
  readonly hasAnyReading = computed(() => this.readings().size > 0);

  /** Said once, offscreen, while the amounts are read and when they land. */
  readonly statusWords = computed(() =>
    this.state() === 'reading' ? 'Reading your amounts…' : this.state() === 'ready' ? 'Amounts read.' : '',
  );

  setPrecision(value: string): void {
    const parsed = Number(value);
    if (Number.isInteger(parsed) && parsed >= 0 && parsed <= MAX_DISPLAY_PRECISION) this.precision.set(parsed);
  }

  setRounding(mode: string): void {
    if ((ROUNDING_MODES as readonly string[]).includes(mode)) this.rounding.set(mode as MidpointRounding);
  }
}
