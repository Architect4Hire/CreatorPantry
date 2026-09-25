// Wire models for POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/scale (ING-003,
// 7.11). Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/ScaleRecipeManagers.cs and
// RecipeScalingManagers.cs exactly — do not add fields the backend doesn't send, and keep this file in sync
// when those ServiceModels change.
//
// Read-only: nothing this file describes is persisted, and a recipe is unchanged by having been scaled.
// Every enum is serialized with the same global JsonStringEnumConverter as recipe.models.ts — PascalCase
// member names on the wire, decoded by membership check, never a number lookup.

import { Quantity, decodeQuantity } from './ingredient-parse.models';
import { isNumberOrNull, isRecord } from './recipe.models';

export type { Quantity } from './ingredient-parse.models';

/** Mirrors RecipeScalingWarningCode. Recipe-level and line-level codes share one enum, as they do on the wire. */
export type RecipeScalingWarningCode =
  | 'FixedQuantityNotScaled'
  | 'ReviewRequiredQualitative'
  | 'ReviewRequiredDiscreteCount'
  | 'ReviewRequiredRange'
  | 'ReviewRequiredOther'
  | 'NoQuantityToScale'
  | 'ScaledToNegligibleDisplay'
  | 'RecipeYieldNotStructured'
  | 'ExtremeScaleFactor';

/**
 * A warning code the server sent and this build does not recognise.
 *
 * Decoded to this rather than dropped, and never a reason to reject the payload. A caution the server chose
 * to raise about a quantity is exactly the thing recipes.md says must not disappear quietly — a build that
 * silently swallowed a warning added after it shipped would show a scaled amount as unqualified when it is
 * not. The UI renders it as a generic "check this line" instead.
 */
export const UNKNOWN_SCALING_WARNING = 'Unknown';

export type ScalingWarning = RecipeScalingWarningCode | typeof UNKNOWN_SCALING_WARNING;

const SCALING_WARNING_VALUES: ReadonlySet<string> = new Set<RecipeScalingWarningCode>([
  'FixedQuantityNotScaled',
  'ReviewRequiredQualitative',
  'ReviewRequiredDiscreteCount',
  'ReviewRequiredRange',
  'ReviewRequiredOther',
  'NoQuantityToScale',
  'ScaledToNegligibleDisplay',
  'RecipeYieldNotStructured',
  'ExtremeScaleFactor',
]);

/** Any string is a warning; an unrecognised one keeps its place as {@link UNKNOWN_SCALING_WARNING}. */
function decodeWarning(value: unknown): ScalingWarning | null {
  if (typeof value !== 'string') return null;
  return SCALING_WARNING_VALUES.has(value) ? (value as RecipeScalingWarningCode) : UNKNOWN_SCALING_WARNING;
}

function decodeWarnings(value: unknown): readonly ScalingWarning[] | null {
  if (!Array.isArray(value)) return null;

  const warnings: ScalingWarning[] = [];
  for (const entry of value) {
    const warning = decodeWarning(entry);
    if (warning === null) return null;
    warnings.push(warning);
  }
  return warnings;
}

/**
 * A nullable quantity field. `null` is the server saying there is none; `undefined` is this decoder saying
 * the value was there and unreadable — the two must not collapse into one, or a malformed payload would
 * render as a line with no amount rather than being refused.
 */
function decodeNullableQuantity(value: unknown): Quantity | null | undefined {
  if (value === null) return null;
  return decodeQuantity(value) ?? undefined;
}

/** Mirrors RecipeIngredientScalingPreviewLine. */
export interface RecipeScalingPreviewLine {
  readonly id: string;
  /** The creator's own text for the line, carried through unchanged (recipes.md). */
  readonly displayText: string;
  /** False for Fixed and ReviewRequired lines — the quantity below is then the original, not a product. */
  readonly wasScaled: boolean;
  /** The exact result, unrounded. Null only when the line had no quantity to begin with. */
  readonly scaledQuantity: Quantity | null;
  readonly scaledQuantityUpper: Quantity | null;
  /** {@link scaledQuantity} rounded for display, derived from the exact value and never from a prior rounding. */
  readonly scaledDisplayQuantity: number | null;
  readonly scaledDisplayQuantityUpper: number | null;
  readonly warnings: readonly ScalingWarning[];
}

function decodeLine(value: unknown): RecipeScalingPreviewLine | null {
  if (!isRecord(value)) return null;
  const {
    id,
    displayText,
    wasScaled,
    scaledQuantity: rawScaledQuantity,
    scaledQuantityUpper: rawScaledQuantityUpper,
    scaledDisplayQuantity,
    scaledDisplayQuantityUpper,
    warnings: rawWarnings,
  } = value;

  const scaledQuantity = decodeNullableQuantity(rawScaledQuantity);
  const scaledQuantityUpper = decodeNullableQuantity(rawScaledQuantityUpper);
  const warnings = decodeWarnings(rawWarnings);

  if (
    typeof id !== 'string' ||
    typeof displayText !== 'string' ||
    typeof wasScaled !== 'boolean' ||
    scaledQuantity === undefined ||
    scaledQuantityUpper === undefined ||
    !isNumberOrNull(scaledDisplayQuantity) ||
    !isNumberOrNull(scaledDisplayQuantityUpper) ||
    warnings === null
  ) {
    return null;
  }

  return {
    id,
    displayText,
    wasScaled,
    scaledQuantity,
    scaledQuantityUpper,
    scaledDisplayQuantity,
    scaledDisplayQuantityUpper,
    warnings,
  };
}

/** Mirrors RecipeScalingPreview. A proposal only — nothing here is persisted (recipes.md). */
export interface RecipeScalingPreview {
  /** The multiplier actually applied: the request's own, or the one derived from a target yield. */
  readonly factor: Quantity;
  readonly requestedTargetYieldQuantity: Quantity | null;
  /** The recipe's yield scaled by {@link factor}, or null when the recipe has no structured yield. */
  readonly scaledYieldQuantity: Quantity | null;
  readonly scaledYieldDisplayQuantity: number | null;
  readonly lines: readonly RecipeScalingPreviewLine[];
  /** Warnings about the request as a whole rather than about any one line. */
  readonly recipeWarnings: readonly ScalingWarning[];
}

function decodePreview(value: unknown): RecipeScalingPreview | null {
  if (!isRecord(value)) return null;
  const {
    factor: rawFactor,
    requestedTargetYieldQuantity: rawRequestedTargetYieldQuantity,
    scaledYieldQuantity: rawScaledYieldQuantity,
    scaledYieldDisplayQuantity,
    lines: rawLines,
    recipeWarnings: rawRecipeWarnings,
  } = value;

  const factor = decodeQuantity(rawFactor);
  const requestedTargetYieldQuantity = decodeNullableQuantity(rawRequestedTargetYieldQuantity);
  const scaledYieldQuantity = decodeNullableQuantity(rawScaledYieldQuantity);
  const recipeWarnings = decodeWarnings(rawRecipeWarnings);

  if (
    factor === null ||
    requestedTargetYieldQuantity === undefined ||
    scaledYieldQuantity === undefined ||
    !isNumberOrNull(scaledYieldDisplayQuantity) ||
    !Array.isArray(rawLines) ||
    recipeWarnings === null
  ) {
    return null;
  }

  const lines: RecipeScalingPreviewLine[] = [];
  for (const rawLine of rawLines) {
    const line = decodeLine(rawLine);
    if (line === null) return null;
    lines.push(line);
  }

  return {
    factor,
    requestedTargetYieldQuantity,
    scaledYieldQuantity,
    scaledYieldDisplayQuantity,
    lines,
    recipeWarnings,
  };
}

/** Mirrors RecipeScalingResultServiceModel. */
export interface RecipeScalingResult {
  /** The version the preview was computed from, echoed back as provenance. */
  readonly sourceVersionNumber: number;
  readonly preview: RecipeScalingPreview;
}

export function decodeRecipeScalingResult(value: unknown): RecipeScalingResult | null {
  if (!isRecord(value)) return null;
  const { sourceVersionNumber, preview: rawPreview } = value;

  const preview = decodePreview(rawPreview);
  if (typeof sourceVersionNumber !== 'number' || preview === null) return null;

  return { sourceVersionNumber, preview };
}

/**
 * Mirrors ScaleRecipeViewModel. Exactly one of a multiplier or a target yield is ever named — the server
 * enforces the same exclusive-or, and modelling it as a union means a request carrying both cannot be
 * constructed here at all rather than being refused after a round trip.
 */
export type ScaleRecipeRequest =
  | { readonly sourceVersionNumber: number; readonly multiplier: number }
  | { readonly sourceVersionNumber: number; readonly targetYieldQuantity: number };

export function encodeScaleRecipeRequest(request: ScaleRecipeRequest): Record<string, unknown> {
  return 'multiplier' in request
    ? { sourceVersionNumber: request.sourceVersionNumber, multiplier: request.multiplier }
    : { sourceVersionNumber: request.sourceVersionNumber, targetYieldQuantity: request.targetYieldQuantity };
}
