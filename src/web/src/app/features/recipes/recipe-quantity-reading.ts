import { FractionPresentation } from '../../models/recipe-display.models';
import { MidpointRounding } from '../../models/reference.models';
import { RecipeCalculationService } from '../../services/recipe-calculation.service';

/** One amount to read: a number, an optional upper bound for a range, and the unit it is in. */
export interface QuantityToRead {
  /** What the caller finds the reading by. */
  readonly key: string;
  readonly value: number;
  readonly upperValue: number | null;
  readonly unitId: string;
}

/** How an amount reads — "1½ cups" — and whether that is a kitchen fraction, a whole number or a decimal. */
export interface QuantityReading {
  readonly text: string;
  readonly presentation: FractionPresentation;
}

export interface ReadingOptions {
  readonly precision: number;
  readonly useAbbreviation: boolean;
  readonly rounding: MidpointRounding;
}

/** Two decimal places, unit names in full, a halfway value to the even digit: the server's own defaults. */
export const DEFAULT_READING: ReadingOptions = { precision: 2, useAbbreviation: false, rounding: 'ToEven' };

/** At most this many asked at once, so a long recipe is a steady trickle rather than a burst. */
const AT_ONCE = 4;

/**
 * Reads many amounts through the server's `normalize-display` route, which reads one at a time.
 *
 * Presentation only: a reading is how an amount reads, never what it is, and nothing here is fed back into a
 * quantity. Nothing is worked out on the page either — the text is the server's. An amount the server could not
 * read is left out of the map and counted, so a caller shows the number it already has instead.
 */
export async function readQuantities(
  service: RecipeCalculationService,
  workspaceSlug: string,
  recipeId: string,
  sourceVersionNumber: number,
  quantities: readonly QuantityToRead[],
  options: ReadingOptions = DEFAULT_READING,
): Promise<{ readonly readings: ReadonlyMap<string, QuantityReading>; readonly failed: number }> {
  const readings = new Map<string, QuantityReading>();
  let failed = 0;
  let next = 0;

  const worker = async (): Promise<void> => {
    while (next < quantities.length) {
      const quantity = quantities[next++];
      const outcome = await service.normalizeDisplay(workspaceSlug, recipeId, {
        sourceVersionNumber,
        value: quantity.value,
        upperValue: quantity.upperValue,
        unitId: quantity.unitId,
        ...options,
      });
      if (outcome.status === 'rendered') {
        readings.set(quantity.key, {
          text: outcome.result.result.text,
          presentation: outcome.result.result.presentation,
        });
      } else {
        failed += 1;
      }
    }
  };

  await Promise.all(Array.from({ length: Math.min(AT_ONCE, quantities.length) }, worker));
  return { readings, failed };
}
