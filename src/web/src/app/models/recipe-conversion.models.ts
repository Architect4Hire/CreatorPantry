// Wire models for the two conversion routes on a recipe (ING-004 and CALC-003, 7.11a/7.11b):
//
//   POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/convert-units
//   POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/convert-temperature
//
// Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/ConvertUnitsRecipeManagers.cs and
// ConvertTemperatureRecipeManagers.cs, over the result types in
// CreatorPantry.Domain/Modules/Measurement/Managers/{UnitConversionManagers,TemperatureConversionManagers}.cs.
//
// Read-only: nothing here is persisted, and a recipe is unchanged by having a quantity or a temperature
// converted. Both routes name an exact source version so a preview's provenance is never ambiguous.

import { Quantity, decodeQuantity } from './ingredient-parse.models';
import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';
import { MIDPOINT_ROUNDING_VALUES, MidpointRounding } from './reference.models';

export type { Quantity } from './ingredient-parse.models';

// Rounding is a measurement fact rather than a conversion one, so it lives with the shared measurement
// vocabulary. Re-exported here because the conversion contracts publish it.
export type { MidpointRounding } from './reference.models';
export { MIDPOINT_ROUNDING_LABELS } from './reference.models';

/** Mirrors UnitConversionMethod — which arithmetic the server actually performed. */
export type UnitConversionMethod = 'SameDimensionFactor' | 'IngredientDensity';

/** Mirrors TemperatureScale. Exactly two, because an affine formula bridges exactly two. */
export type TemperatureScale = 'Celsius' | 'Fahrenheit';

const UNIT_CONVERSION_METHOD_VALUES: ReadonlySet<string> = new Set<UnitConversionMethod>([
  'SameDimensionFactor',
  'IngredientDensity',
]);

const TEMPERATURE_SCALE_VALUES: ReadonlySet<string> = new Set<TemperatureScale>(['Celsius', 'Fahrenheit']);

export const TEMPERATURE_SCALE_LABELS: Readonly<Record<TemperatureScale, string>> = {
  Celsius: 'Celsius',
  Fahrenheit: 'Fahrenheit',
};

export const TEMPERATURE_SCALE_SYMBOLS: Readonly<Record<TemperatureScale, string>> = {
  Celsius: '°C',
  Fahrenheit: '°F',
};

// ---- Unit conversion ----

/** Mirrors ConvertUnitsViewModel. */
export interface ConvertUnitsRequest {
  readonly sourceVersionNumber: number;
  readonly quantity: number;
  readonly fromUnitId: string;
  readonly toUnitId: string;
}

export function encodeConvertUnitsRequest(request: ConvertUnitsRequest): Record<string, unknown> {
  return {
    sourceVersionNumber: request.sourceVersionNumber,
    quantity: request.quantity,
    fromUnitId: request.fromUnitId,
    toUnitId: request.toUnitId,
  };
}

/** Mirrors UnitConversionResult. */
export interface UnitConversionResult {
  readonly method: UnitConversionMethod;
  /** The result as an exact fraction — computed from canonical inputs, never from a rounded display value. */
  readonly convertedQuantity: Quantity;
  /** {@link convertedQuantity} rounded exactly once, to {@link precision} using {@link rounding}. */
  readonly convertedDisplayQuantity: number;
  readonly precision: number;
  readonly rounding: MidpointRounding;
  /** A short, deterministic description of the operation actually performed. */
  readonly formula: string;
  /** The density fact's citation when {@link method} is `IngredientDensity`; otherwise null. */
  readonly source: string | null;
}

function decodeUnitConversionResult(value: unknown): UnitConversionResult | null {
  if (!isRecord(value)) return null;
  const {
    method: rawMethod,
    convertedQuantity: rawConvertedQuantity,
    convertedDisplayQuantity,
    precision,
    rounding: rawRounding,
    formula,
    source,
  } = value;

  const method = decodeEnum<UnitConversionMethod>(UNIT_CONVERSION_METHOD_VALUES, rawMethod);
  const convertedQuantity = decodeQuantity(rawConvertedQuantity);
  const rounding = decodeEnum<MidpointRounding>(MIDPOINT_ROUNDING_VALUES, rawRounding);

  if (
    method === null ||
    convertedQuantity === null ||
    typeof convertedDisplayQuantity !== 'number' ||
    typeof precision !== 'number' ||
    rounding === null ||
    typeof formula !== 'string' ||
    !isStringOrNull(source)
  ) {
    return null;
  }

  return { method, convertedQuantity, convertedDisplayQuantity, precision, rounding, formula, source };
}

/** Mirrors RecipeUnitConversionResultServiceModel. */
export interface RecipeUnitConversionResult {
  readonly sourceVersionNumber: number;
  readonly result: UnitConversionResult;
}

export function decodeRecipeUnitConversionResult(value: unknown): RecipeUnitConversionResult | null {
  if (!isRecord(value)) return null;
  const { sourceVersionNumber, result: rawResult } = value;

  const result = decodeUnitConversionResult(rawResult);
  if (typeof sourceVersionNumber !== 'number' || result === null) return null;

  return { sourceVersionNumber, result };
}

// ---- Temperature conversion ----

/**
 * Mirrors ConvertTemperatureViewModel.
 *
 * `value` must already be a structured number. recipes.md forbids inferring a temperature from vague heat
 * language, and there is no field here that could carry one — a step described only as "medium-high" has
 * nothing to send.
 */
export interface ConvertTemperatureRequest {
  readonly sourceVersionNumber: number;
  readonly value: number;
  readonly fromScale: TemperatureScale;
  readonly toScale: TemperatureScale;
  readonly precision: number;
  /** Carried through the calculation untouched and echoed back; never read or used to adjust anything. */
  readonly ovenModeContext: string | null;
  readonly safetyNote: string | null;
}

export function encodeConvertTemperatureRequest(request: ConvertTemperatureRequest): Record<string, unknown> {
  return {
    sourceVersionNumber: request.sourceVersionNumber,
    value: request.value,
    fromScale: request.fromScale,
    toScale: request.toScale,
    precision: request.precision,
    ovenModeContext: request.ovenModeContext,
    safetyNote: request.safetyNote,
  };
}

/** Mirrors TemperatureConversionResult. */
export interface TemperatureConversionResult {
  /** The value as given, exact — never itself rounded. */
  readonly sourceValue: Quantity;
  readonly sourceScale: TemperatureScale;
  readonly convertedValue: Quantity;
  readonly convertedDisplayValue: number;
  readonly convertedScale: TemperatureScale;
  readonly precision: number;
  readonly rounding: MidpointRounding;
  readonly formula: string;
  /** Echoed back exactly as sent — a null in is a null out, a value in is the same value out. */
  readonly ovenModeContext: string | null;
  /** Echoed back exactly as sent. Never read, parsed, or used to adjust the conversion (recipes.md). */
  readonly safetyNote: string | null;
}

function decodeTemperatureConversionResult(value: unknown): TemperatureConversionResult | null {
  if (!isRecord(value)) return null;
  const {
    sourceValue: rawSourceValue,
    sourceScale: rawSourceScale,
    convertedValue: rawConvertedValue,
    convertedDisplayValue,
    convertedScale: rawConvertedScale,
    precision,
    rounding: rawRounding,
    formula,
    ovenModeContext,
    safetyNote,
  } = value;

  const sourceValue = decodeQuantity(rawSourceValue);
  const sourceScale = decodeEnum<TemperatureScale>(TEMPERATURE_SCALE_VALUES, rawSourceScale);
  const convertedValue = decodeQuantity(rawConvertedValue);
  const convertedScale = decodeEnum<TemperatureScale>(TEMPERATURE_SCALE_VALUES, rawConvertedScale);
  const rounding = decodeEnum<MidpointRounding>(MIDPOINT_ROUNDING_VALUES, rawRounding);

  if (
    sourceValue === null ||
    sourceScale === null ||
    convertedValue === null ||
    typeof convertedDisplayValue !== 'number' ||
    convertedScale === null ||
    typeof precision !== 'number' ||
    rounding === null ||
    typeof formula !== 'string' ||
    !isStringOrNull(ovenModeContext) ||
    !isStringOrNull(safetyNote)
  ) {
    return null;
  }

  return {
    sourceValue,
    sourceScale,
    convertedValue,
    convertedDisplayValue,
    convertedScale,
    precision,
    rounding,
    formula,
    ovenModeContext,
    safetyNote,
  };
}

/** Mirrors RecipeTemperatureConversionResultServiceModel. */
export interface RecipeTemperatureConversionResult {
  readonly sourceVersionNumber: number;
  readonly result: TemperatureConversionResult;
}

export function decodeRecipeTemperatureConversionResult(
  value: unknown,
): RecipeTemperatureConversionResult | null {
  if (!isRecord(value)) return null;
  const { sourceVersionNumber, result: rawResult } = value;

  const result = decodeTemperatureConversionResult(rawResult);
  if (typeof sourceVersionNumber !== 'number' || result === null) return null;

  return { sourceVersionNumber, result };
}
