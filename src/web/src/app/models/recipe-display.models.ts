// Wire models for POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/normalize-display
// (ING-006, 7.11d). Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/NormalizeDisplayRecipeManagers.cs
// over QuantityDisplayManagers.cs exactly.
//
// **Presentation only.** Nothing here changes a canonical quantity or a recipe version: this renders a value
// the caller sends, once, and returns text. The rendered text never replaces or feeds back into the exact
// quantity it was built from (recipes.md, ING-006).

import { decodeEnum, isRecord } from './recipe.models';
import { MIDPOINT_ROUNDING_VALUES, MidpointRounding } from './reference.models';

export type { MidpointRounding } from './reference.models';
export { MIDPOINT_ROUNDING_LABELS } from './reference.models';

/** Mirrors FractionPresentation — how the server rendered one number. */
export type FractionPresentation = 'Whole' | 'Fraction' | 'Decimal';

const FRACTION_PRESENTATION_VALUES: ReadonlySet<string> = new Set<FractionPresentation>([
  'Whole',
  'Fraction',
  'Decimal',
]);

/** What each presentation means, in the creator's terms rather than the enum's. */
export const FRACTION_PRESENTATION_LABELS: Readonly<Record<FractionPresentation, string>> = {
  Whole: 'Whole number',
  Fraction: 'Kitchen fraction',
  Decimal: 'Rounded decimal',
};

export const FRACTION_PRESENTATION_EXPLANATIONS: Readonly<Record<FractionPresentation, string>> = {
  Whole: 'The exact value has no fractional remainder, so it reads as a bare number.',
  Fraction: 'The remainder matched a common kitchen fraction exactly, so it reads as one.',
  Decimal: 'No common kitchen fraction matched, so it reads as a decimal rounded to the precision below.',
};

/** Mirrors NormalizeDisplayViewModel. */
export interface NormalizeDisplayRequest {
  readonly sourceVersionNumber: number;
  readonly value: number;
  /** The upper bound of a range, when there is one. Must be greater than {@link value}. */
  readonly upperValue: number | null;
  readonly unitId: string;
  readonly precision: number;
  readonly useAbbreviation: boolean;
  readonly rounding: MidpointRounding;
}

export function encodeNormalizeDisplayRequest(request: NormalizeDisplayRequest): Record<string, unknown> {
  const body: Record<string, unknown> = {
    sourceVersionNumber: request.sourceVersionNumber,
    value: request.value,
    unitId: request.unitId,
    precision: request.precision,
    useAbbreviation: request.useAbbreviation,
    rounding: request.rounding,
  };

  // Omitted rather than sent as null when there is no range: the validator only compares an upper bound it
  // was actually given, and a request that names a field it does not mean reads wrong.
  if (request.upperValue !== null) body['upperValue'] = request.upperValue;

  return body;
}

/**
 * Mirrors QuantityDisplayResult.
 *
 * Presentation only. {@link text} is how a quantity reads, never what it is — the exact value this was built
 * from remains canonical, and nothing here replaces it.
 */
export interface QuantityDisplayResult {
  /** The complete rendered text, including the unit — "1½ cups", "2–3 tbsp", "0.02 g". */
  readonly text: string;
  /** How the single value, or a range's lower bound, was rendered. */
  readonly presentation: FractionPresentation;
  /** How a range's upper bound was rendered; null when this was a single value. */
  readonly upperPresentation: FractionPresentation | null;
  /** The precision used for the decimal fallback, echoed rather than left implicit. */
  readonly precision: number;
  readonly rounding: MidpointRounding;
}

function decodeResult(value: unknown): QuantityDisplayResult | null {
  if (!isRecord(value)) return null;
  const {
    text,
    presentation: rawPresentation,
    upperPresentation: rawUpperPresentation,
    precision,
    rounding: rawRounding,
  } = value;

  const presentation = decodeEnum<FractionPresentation>(FRACTION_PRESENTATION_VALUES, rawPresentation);
  const upperPresentation =
    rawUpperPresentation === null
      ? null
      : decodeEnum<FractionPresentation>(FRACTION_PRESENTATION_VALUES, rawUpperPresentation);
  const rounding = decodeEnum<MidpointRounding>(MIDPOINT_ROUNDING_VALUES, rawRounding);

  if (
    typeof text !== 'string' ||
    presentation === null ||
    (rawUpperPresentation !== null && upperPresentation === null) ||
    typeof precision !== 'number' ||
    rounding === null
  ) {
    return null;
  }

  return { text, presentation, upperPresentation, precision, rounding };
}

/** Mirrors RecipeQuantityDisplayResultServiceModel. */
export interface RecipeQuantityDisplayResult {
  readonly sourceVersionNumber: number;
  readonly result: QuantityDisplayResult;
}

export function decodeRecipeQuantityDisplayResult(value: unknown): RecipeQuantityDisplayResult | null {
  if (!isRecord(value)) return null;
  const { sourceVersionNumber, result: rawResult } = value;

  const result = decodeResult(rawResult);
  if (typeof sourceVersionNumber !== 'number' || result === null) return null;

  return { sourceVersionNumber, result };
}
