// Wire models for POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/recalculate-yield
// (ING-005, 7.11c). Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/RecalculateYieldRecipeManagers.cs
// over YieldReconciliationManagers.cs exactly.
//
// Read-only: nothing here is persisted, and nothing about the recipe changes because its yield was
// reconciled. Every input is independently optional — which subset is present is what decides whether
// anything can be computed at all, and too few is an answer rather than a refusal.

import { Quantity, decodeQuantity } from './ingredient-parse.models';
import { decodeEnum, isNumberOrNull, isRecord, isStringOrNull } from './recipe.models';

export type { Quantity } from './ingredient-parse.models';

/** Mirrors YieldReconciliationStatus — what the server concluded about the numbers it was given. */
export type YieldReconciliationStatus = 'Reconciled' | 'Solved' | 'Contradictory' | 'InsufficientInput';

/** Mirrors YieldReconciliationField — which of the three a `Solved` result computed. */
export type YieldReconciliationField = 'BatchYield' | 'ServingCount' | 'ServingSize';

const YIELD_STATUS_VALUES: ReadonlySet<string> = new Set<YieldReconciliationStatus>([
  'Reconciled',
  'Solved',
  'Contradictory',
  'InsufficientInput',
]);

const YIELD_FIELD_VALUES: ReadonlySet<string> = new Set<YieldReconciliationField>([
  'BatchYield',
  'ServingCount',
  'ServingSize',
]);

/**
 * Mirrors RecalculateYieldViewModel.
 *
 * Every value is independently optional, and none of them is backed by the recipe's own stored numbers: what
 * is sent here is everything the calculation reasons about (ING-005 — explicit inputs, and recipes.md's
 * refusal to invent a missing serving definition).
 */
export interface RecalculateYieldRequest {
  readonly sourceVersionNumber: number;
  readonly batchYield: number | null;
  readonly servingCount: number | null;
  readonly servingSize: number | null;
  readonly panVolume: number | null;
  readonly displayPrecision: number | null;
}

/**
 * Omits every value the creator left blank rather than sending it as null.
 *
 * The two are equivalent to the server, but a request that names only what was actually entered is the one
 * that matches what the panel says it does — nothing unstated is submitted on the creator's behalf.
 */
export function encodeRecalculateYieldRequest(request: RecalculateYieldRequest): Record<string, unknown> {
  const body: Record<string, unknown> = { sourceVersionNumber: request.sourceVersionNumber };

  if (request.batchYield !== null) body['batchYield'] = request.batchYield;
  if (request.servingCount !== null) body['servingCount'] = request.servingCount;
  if (request.servingSize !== null) body['servingSize'] = request.servingSize;
  if (request.panVolume !== null) body['panVolume'] = request.panVolume;
  if (request.displayPrecision !== null) body['displayPrecision'] = request.displayPrecision;

  return body;
}

/** Mirrors YieldReconciliationPreview. A proposal only; nothing here is persisted (recipes.md). */
export interface YieldReconciliationPreview {
  readonly status: YieldReconciliationStatus;
  /** The given value, or the solved one when {@link solvedField} names it. */
  readonly batchYield: number | null;
  readonly servingCount: number | null;
  readonly servingSize: number | null;
  /** Which value a `Solved` result computed; null for every other status. */
  readonly solvedField: YieldReconciliationField | null;
  /** A short, deterministic description of the arithmetic performed; null for `InsufficientInput`. */
  readonly formula: string | null;
  /** The given pan or vessel capacity, echoed; null when none was given. */
  readonly panVolume: number | null;
  /**
   * The known batch yield divided by {@link panVolume}, exact. Purely descriptive — never a fits or
   * does-not-fit verdict. Set only when {@link panComparable} is true.
   */
  readonly panFillRatio: Quantity | null;
  /**
   * Null when no pan capacity was given at all; false when one was given but could not be compared (the
   * recipe's yield is not a volume, or no batch yield is known); true when a ratio was computed.
   */
  readonly panComparable: boolean | null;
}

/**
 * A nullable quantity field. `null` is the server saying there is none; `undefined` is this decoder saying
 * the value was there and unreadable — collapsing the two would render a malformed ratio as no ratio.
 */
function decodeNullableQuantity(value: unknown): Quantity | null | undefined {
  if (value === null) return null;
  return decodeQuantity(value) ?? undefined;
}

function decodeNullableBoolean(value: unknown): boolean | null | undefined {
  if (value === null) return null;
  return typeof value === 'boolean' ? value : undefined;
}

function decodePreview(value: unknown): YieldReconciliationPreview | null {
  if (!isRecord(value)) return null;
  const {
    status: rawStatus,
    batchYield,
    servingCount,
    servingSize,
    solvedField: rawSolvedField,
    formula,
    panVolume,
    panFillRatio: rawPanFillRatio,
    panComparable: rawPanComparable,
  } = value;

  const status = decodeEnum<YieldReconciliationStatus>(YIELD_STATUS_VALUES, rawStatus);
  // Null is the ordinary case for every status but `Solved`, so an absent field is not a failure — only an
  // unrecognised name is.
  const solvedField =
    rawSolvedField === null ? null : decodeEnum<YieldReconciliationField>(YIELD_FIELD_VALUES, rawSolvedField);
  const panFillRatio = decodeNullableQuantity(rawPanFillRatio);
  const panComparable = decodeNullableBoolean(rawPanComparable);

  if (
    status === null ||
    !isNumberOrNull(batchYield) ||
    !isNumberOrNull(servingCount) ||
    !isNumberOrNull(servingSize) ||
    (rawSolvedField !== null && solvedField === null) ||
    !isStringOrNull(formula) ||
    !isNumberOrNull(panVolume) ||
    panFillRatio === undefined ||
    panComparable === undefined
  ) {
    return null;
  }

  return {
    status,
    batchYield,
    servingCount,
    servingSize,
    solvedField,
    formula,
    panVolume,
    panFillRatio,
    panComparable,
  };
}

/** Mirrors RecipeYieldReconciliationResultServiceModel. */
export interface RecipeYieldReconciliationResult {
  readonly sourceVersionNumber: number;
  readonly preview: YieldReconciliationPreview;
}

export function decodeRecipeYieldReconciliationResult(value: unknown): RecipeYieldReconciliationResult | null {
  if (!isRecord(value)) return null;
  const { sourceVersionNumber, preview: rawPreview } = value;

  const preview = decodePreview(rawPreview);
  if (typeof sourceVersionNumber !== 'number' || preview === null) return null;

  return { sourceVersionNumber, preview };
}
