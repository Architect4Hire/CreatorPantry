// Wire models for the nutrition estimate and the ingredient mappings it rests on (ING-008; baselines B-34
// mappings, B-35 arithmetic, B-39 the estimate route, B-40 the mapping commands):
//   POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/{versionNumber}/analysis/nutrition
//   GET  /api/v1/workspaces/{workspaceSlug}/nutrition-mappings/{ingredientId}
//   POST /api/v1/workspaces/{workspaceSlug}/nutrition-mappings/{ingredientId}/confirm
//   POST /api/v1/workspaces/{workspaceSlug}/nutrition-mappings/{ingredientId}/reject
//
// Mirrors CreatorPantry.Domain/Modules/Nutrition/Managers/{NutritionEstimateModels,NutritionMappingModels,
// NutritionMappingEnums}.cs exactly. Keep in sync when those change.
//
// **An estimate, labelled as one.** `label` is always "Estimate" and `method` says how it was made. Nothing
// here is a measured, certified or regulatory value, and no surface may present it as one (recipes.md).
//
// **Nothing here calculates.** Every figure arrives exact and already rounded for display; a client that
// summed, divided or re-rounded any of it would be a second implementation of the domain's arithmetic
// disagreeing with the one that produced the numbers beside it (ai.md, "deterministic work stays
// deterministic"). This file decodes and nothing else.

import { MIDPOINT_ROUNDING_VALUES, MidpointRounding } from './reference.models';
import { isRecord } from './recipe.models';

// ---------------------------------------------------------------------------
// Vocabulary
// ---------------------------------------------------------------------------
//
// Every enum is decoded **closed**, for the reason `dietary-allergen-analysis.models.ts` gives: a word this
// build has not heard of cannot be shown without guessing what it means, and the guesses available here all
// read as "counted" or "complete" when they might mean the opposite.

/** Whether every required ingredient was counted. Mirrors NutritionEstimateStatus. */
export type NutritionEstimateStatus = 'Complete' | 'Incomplete';

const ESTIMATE_STATUSES: ReadonlySet<string> = new Set<NutritionEstimateStatus>(['Complete', 'Incomplete']);

/** How a counted line's grams were known — the weakest of them is the estimate's confidence. */
export type NutritionQuantityBasis = 'Mass' | 'ReferencePortion' | 'CreatorPortion';

const QUANTITY_BASES: ReadonlySet<string> = new Set<NutritionQuantityBasis>([
  'Mass',
  'ReferencePortion',
  'CreatorPortion',
]);

/** Where the serving count came from. `None` means per-serving figures cannot be given at all. */
export type NutritionServingsSource = 'Requested' | 'ServingCount' | 'YieldOverServingSize' | 'None';

const SERVINGS_SOURCES: ReadonlySet<string> = new Set<NutritionServingsSource>([
  'Requested',
  'ServingCount',
  'YieldOverServingSize',
  'None',
]);

/** What became of one recipe line. Mirrors NutritionLineOutcome. */
export type NutritionLineOutcome =
  | 'Included'
  | 'Unmapped'
  | 'NeedsReview'
  | 'Unconvertible'
  | 'NotQuantified'
  | 'Optional';

const LINE_OUTCOMES: ReadonlySet<string> = new Set<NutritionLineOutcome>([
  'Included',
  'Unmapped',
  'NeedsReview',
  'Unconvertible',
  'NotQuantified',
  'Optional',
]);

/** Why a line ended as it did. Mirrors NutritionLineReason. */
export type NutritionLineReason =
  | 'None'
  | 'NoIngredient'
  | 'NoMapping'
  | 'MappingNotConfirmed'
  | 'MappingUnderReview'
  | 'SourceVersionChanged'
  | 'ReferenceWithdrawn'
  | 'NoUnit'
  | 'UnitUnknown'
  | 'UnitNotMeasurable'
  | 'NoPortion'
  | 'PortionDoesNotMatch';

const LINE_REASONS: ReadonlySet<string> = new Set<NutritionLineReason>([
  'None',
  'NoIngredient',
  'NoMapping',
  'MappingNotConfirmed',
  'MappingUnderReview',
  'SourceVersionChanged',
  'ReferenceWithdrawn',
  'NoUnit',
  'UnitUnknown',
  'UnitNotMeasurable',
  'NoPortion',
  'PortionDoesNotMatch',
]);

/** The nutrients an estimate can report. Mirrors NutrientCode. */
export type NutrientCode =
  | 'Energy'
  | 'Protein'
  | 'TotalFat'
  | 'SaturatedFat'
  | 'TransFat'
  | 'Cholesterol'
  | 'Carbohydrate'
  | 'DietaryFiber'
  | 'TotalSugars'
  | 'AddedSugars'
  | 'Sodium'
  | 'Potassium'
  | 'Calcium'
  | 'Iron'
  | 'VitaminD';

const NUTRIENT_CODES: ReadonlySet<string> = new Set<NutrientCode>([
  'Energy',
  'Protein',
  'TotalFat',
  'SaturatedFat',
  'TransFat',
  'Cholesterol',
  'Carbohydrate',
  'DietaryFiber',
  'TotalSugars',
  'AddedSugars',
  'Sodium',
  'Potassium',
  'Calcium',
  'Iron',
  'VitaminD',
]);

export type NutrientUnit = 'Kilocalorie' | 'Gram' | 'Milligram' | 'Microgram';

const NUTRIENT_UNITS: ReadonlySet<string> = new Set<NutrientUnit>([
  'Kilocalorie',
  'Gram',
  'Milligram',
  'Microgram',
]);

/** Where a mapping stands. Only `Confirmed` is used by an estimate. Mirrors NutritionMappingStatus. */
export type NutritionMappingStatus = 'Candidate' | 'Confirmed' | 'NeedsReview' | 'Unresolved';

const MAPPING_STATUSES: ReadonlySet<string> = new Set<NutritionMappingStatus>([
  'Candidate',
  'Confirmed',
  'NeedsReview',
  'Unresolved',
]);

/** Recorded, never scored: `High` for the only open hit or an exact name, `Low` otherwise (B-34). */
export type NutritionMatchConfidence = 'Low' | 'High';

const MATCH_CONFIDENCES: ReadonlySet<string> = new Set<NutritionMatchConfidence>(['Low', 'High']);

export type NutritionMatchMethod = 'AutomaticSearch' | 'CreatorSelected';

const MATCH_METHODS: ReadonlySet<string> = new Set<NutritionMatchMethod>(['AutomaticSearch', 'CreatorSelected']);

/** Why a mapping has no reference. Mirrors NutritionUnresolvedReason. */
export type NutritionUnresolvedReason =
  | 'NoCandidates'
  | 'CreatorDeclined'
  | 'ReferenceRejected'
  | 'ReferenceWithdrawn';

const UNRESOLVED_REASONS: ReadonlySet<string> = new Set<NutritionUnresolvedReason>([
  'NoCandidates',
  'CreatorDeclined',
  'ReferenceRejected',
  'ReferenceWithdrawn',
]);

/** Whether a portion is one the source measured or this workspace's own figure. */
export type NutritionPortionOrigin = 'Reference' | 'Creator';

const PORTION_ORIGINS: ReadonlySet<string> = new Set<NutritionPortionOrigin>(['Reference', 'Creator']);

/** `NutritionEstimatePolicy.MaxRequestedServings`. Above it the server refuses the request. */
export const MAX_REQUESTED_SERVINGS = 10_000;

/** The places a requested serving count may carry (`NutritionEstimateRequestViewModelValidator`). */
export const MAX_SERVINGS_DECIMALS = 4;

// ---------------------------------------------------------------------------
// The estimate
// ---------------------------------------------------------------------------

/**
 * One figure, exact and as it should be shown. Mirrors NutritionAmountServiceModel.
 *
 * `displayLow`/`displayHigh` are the server's own rounding, done once; `low`/`high` are the exact values to
 * six places. A client shows the display values and never rounds the exact ones itself.
 */
export interface NutritionAmount {
  readonly low: number;
  readonly high: number;
  readonly displayLow: number;
  readonly displayHigh: number;
  readonly displayPrecision: number;
  readonly rounding: MidpointRounding;
  /** True when a counted line carried a range, so the figure is a range too. */
  readonly isRange: boolean;
  /** Not zero, but rounds to zero at this precision. Show "trace"; never 0. */
  readonly trace: boolean;
}

/** One nutrient, whole-recipe and per serving. Mirrors NutritionNutrientEstimateServiceModel. */
export interface NutritionNutrientEstimate {
  readonly code: NutrientCode;
  readonly unit: NutrientUnit;
  readonly wholeRecipe: NutritionAmount;
  /** Null when there is no serving basis at all. */
  readonly perServing: NutritionAmount | null;
  /** False when a counted line's source does not report this nutrient; it is unknown there, never zero. */
  readonly complete: boolean;
  readonly linesMissingIt: readonly string[];
}

/** The source record behind one counted line. Mirrors NutritionEstimateSourceServiceModel. */
export interface NutritionEstimateSource {
  readonly sourceCode: string;
  readonly externalRecordId: string;
  readonly sourceVersion: string;
}

/** One cited source with its licence. Mirrors NutritionCitationServiceModel. */
export interface NutritionCitation {
  readonly sourceCode: string;
  readonly citation: string;
  readonly licenseCode: string;
  readonly licenseUrl: string;
}

/** One recipe line as the estimate read it. Mirrors NutritionLineEstimateServiceModel. */
export interface NutritionLineEstimate {
  readonly lineId: string;
  /** The creator's own words for the line. Never rewritten. */
  readonly displayText: string;
  readonly outcome: NutritionLineOutcome;
  readonly reason: NutritionLineReason;
  /** False for an optional or unquantified line: not required, and not counted. */
  readonly required: boolean;
  readonly gramsLow: number | null;
  readonly gramsHigh: number | null;
  readonly basis: NutritionQuantityBasis | null;
  readonly mappingId: string | null;
  readonly source: NutritionEstimateSource | null;
}

/**
 * The required lines that were not counted, grouped. Mirrors NutritionUnresolvedLinesServiceModel.
 *
 * Derived server-side from the lines so the two cannot disagree (B-39). Every screen shows these groups as
 * given rather than filtering `lines` again.
 */
export interface NutritionUnresolvedLines {
  readonly unmapped: readonly string[];
  readonly needsReview: readonly string[];
  readonly unconvertible: readonly string[];
  /**
   * Required lines the recipe states no amount for — "butter, softened".
   *
   * Added by 14.7's H-4, which amended B-35: an unquantified line the creator did not mark optional used to
   * sit outside the required set, so an estimate could call itself `Complete` while omitting the salt.
   */
  readonly notQuantified: readonly string[];
}

/** Mirrors NutritionEstimateServiceModel. */
export interface NutritionEstimate {
  readonly recipeId: string;
  readonly recipeVersionId: string;
  readonly versionNumber: number;
  /** Always "Estimate". Shown as given; never replaced with a word that claims more. */
  readonly label: string;
  readonly methodVersion: string;
  /** How it was made, in the server's own words. Shown verbatim. */
  readonly method: string;
  readonly status: NutritionEstimateStatus;
  /** True only when every required line was counted. */
  readonly publishable: boolean;
  /** True when the totals are a known-incomplete lower bound. */
  readonly partial: boolean;
  /** The weakest basis among counted lines. Null when nothing was counted. */
  readonly confidence: NutritionQuantityBasis | null;
  readonly servings: number | null;
  readonly servingsSource: NutritionServingsSource;
  readonly nutrients: readonly NutritionNutrientEstimate[];
  readonly lines: readonly NutritionLineEstimate[];
  readonly sources: readonly NutritionCitation[];
  readonly unresolved: NutritionUnresolvedLines;
}

/** Mirrors NutritionEstimateRequestViewModel. `servings` omitted uses the version's own basis. */
export interface NutritionEstimateRequest {
  readonly servings: number | null;
}

export function encodeNutritionEstimateRequest(request: NutritionEstimateRequest): Record<string, unknown> {
  // `{}` is a valid body and means "the version's own basis"; `null` says the same thing explicitly.
  return { servings: request.servings };
}

// ---------------------------------------------------------------------------
// The mapping
// ---------------------------------------------------------------------------

/** The reference a mapping points at. Mirrors NutritionMappingReferenceServiceModel. */
export interface NutritionMappingReference {
  readonly sourceCode: string;
  readonly externalRecordId: string;
  /** The source's own description of the record. Shown as written. */
  readonly description: string;
  readonly sourceVersion: string;
}

/** The portion that turns a volume or a count into grams. Mirrors NutritionMappingPortionServiceModel. */
export interface NutritionMappingPortion {
  readonly measurementUnitId: string;
  readonly amount: number;
  readonly grams: number;
  readonly label: string | null;
  readonly origin: NutritionPortionOrigin;
}

/** Mirrors NutritionMappingServiceModel: one workspace's decision about one platform ingredient. */
export interface NutritionMapping {
  readonly id: string;
  readonly ingredientId: string;
  readonly status: NutritionMappingStatus;
  readonly matchMethod: NutritionMatchMethod | null;
  readonly confidence: NutritionMatchConfidence | null;
  readonly reference: NutritionMappingReference | null;
  /** The newer source version waiting to be accepted, while the mapping is under review. */
  readonly reviewSourceVersion: string | null;
  readonly unresolvedReason: NutritionUnresolvedReason | null;
  readonly portion: NutritionMappingPortion | null;
  readonly rejectedReferenceCount: number;
  readonly confirmedAt: string | null;
  readonly updatedAt: string;
  /** Quoted back on a decision, so nobody decides a mapping they did not read. */
  readonly concurrencyToken: string;
}

/**
 * A decision on the mapping as read. Mirrors NutritionMappingDecisionViewModel, and
 * ConfirmNutritionMappingViewModel without the optional portion — nothing in this app sends one yet.
 */
export interface NutritionMappingDecision {
  readonly expectedConcurrencyToken: string;
}

export function encodeNutritionMappingDecision(decision: NutritionMappingDecision): Record<string, unknown> {
  return { expectedConcurrencyToken: decision.expectedConcurrencyToken };
}

// ---------------------------------------------------------------------------
// Decoding
// ---------------------------------------------------------------------------

function decodeClosedEnum<T extends string>(values: ReadonlySet<string>, value: unknown): T | null {
  return typeof value === 'string' && values.has(value) ? (value as T) : null;
}

function decodeNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function decodeInteger(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) ? value : null;
}

function decodeIdList(value: unknown): readonly string[] | null {
  if (!Array.isArray(value)) return null;
  return value.every((item) => typeof item === 'string') ? (value as string[]) : null;
}

function decodeList<T>(value: unknown, decodeItem: (item: unknown) => T | null): readonly T[] | null {
  if (!Array.isArray(value)) return null;

  const items: T[] = [];
  for (const raw of value) {
    const item = decodeItem(raw);
    if (item === null) return null;
    items.push(item);
  }
  return items;
}

function decodeAmount(value: unknown): NutritionAmount | null {
  if (!isRecord(value)) return null;

  const low = decodeNumber(value['low']);
  const high = decodeNumber(value['high']);
  const displayLow = decodeNumber(value['displayLow']);
  const displayHigh = decodeNumber(value['displayHigh']);
  const displayPrecision = decodeInteger(value['displayPrecision']);
  const rounding = decodeClosedEnum<MidpointRounding>(MIDPOINT_ROUNDING_VALUES, value['rounding']);
  const { isRange, trace } = value;

  if (low === null || high === null || displayLow === null || displayHigh === null) return null;
  if (displayPrecision === null || rounding === null) return null;
  // A precision a formatter cannot use is not a figure this client can show: `toFixed` throws outside 0–100,
  // and nothing in this domain needs more than a handful of places. Refused rather than caught at render.
  if (displayPrecision < 0 || displayPrecision > 20) return null;
  if (typeof isRange !== 'boolean' || typeof trace !== 'boolean') return null;

  return { low, high, displayLow, displayHigh, displayPrecision, rounding, isRange, trace };
}

function decodeNutrient(value: unknown): NutritionNutrientEstimate | null {
  if (!isRecord(value)) return null;

  const code = decodeClosedEnum<NutrientCode>(NUTRIENT_CODES, value['code']);
  const unit = decodeClosedEnum<NutrientUnit>(NUTRIENT_UNITS, value['unit']);
  const wholeRecipe = decodeAmount(value['wholeRecipe']);
  const perServing = value['perServing'] === null ? null : decodeAmount(value['perServing']);
  const linesMissingIt = decodeIdList(value['linesMissingIt']);
  const { complete } = value;

  if (code === null || unit === null || wholeRecipe === null || linesMissingIt === null) return null;
  if (typeof complete !== 'boolean') return null;
  // Present but unreadable is not the same as absent: only an explicit null means "no serving basis".
  if (value['perServing'] !== null && perServing === null) return null;

  return { code, unit, wholeRecipe, perServing, complete, linesMissingIt };
}

function decodeEstimateSource(value: unknown): NutritionEstimateSource | null {
  if (!isRecord(value)) return null;

  const { sourceCode, externalRecordId, sourceVersion } = value;
  if (typeof sourceCode !== 'string' || typeof externalRecordId !== 'string') return null;
  if (typeof sourceVersion !== 'string') return null;

  return { sourceCode, externalRecordId, sourceVersion };
}

function decodeCitation(value: unknown): NutritionCitation | null {
  if (!isRecord(value)) return null;

  const { sourceCode, citation, licenseCode, licenseUrl } = value;
  if (typeof sourceCode !== 'string' || typeof citation !== 'string') return null;
  if (typeof licenseCode !== 'string' || typeof licenseUrl !== 'string') return null;

  return { sourceCode, citation, licenseCode, licenseUrl };
}

function decodeLine(value: unknown): NutritionLineEstimate | null {
  if (!isRecord(value)) return null;

  const { lineId, displayText, required, mappingId } = value;
  const outcome = decodeClosedEnum<NutritionLineOutcome>(LINE_OUTCOMES, value['outcome']);
  const reason = decodeClosedEnum<NutritionLineReason>(LINE_REASONS, value['reason']);
  const gramsLow = value['gramsLow'] === null ? null : decodeNumber(value['gramsLow']);
  const gramsHigh = value['gramsHigh'] === null ? null : decodeNumber(value['gramsHigh']);
  const basis =
    value['basis'] === null ? null : decodeClosedEnum<NutritionQuantityBasis>(QUANTITY_BASES, value['basis']);
  const source = value['source'] === null ? null : decodeEstimateSource(value['source']);

  if (typeof lineId !== 'string' || typeof displayText !== 'string') return null;
  if (typeof required !== 'boolean') return null;
  if (mappingId !== null && typeof mappingId !== 'string') return null;
  if (outcome === null || reason === null) return null;
  if (value['gramsLow'] !== null && gramsLow === null) return null;
  if (value['gramsHigh'] !== null && gramsHigh === null) return null;
  if (value['basis'] !== null && basis === null) return null;
  if (value['source'] !== null && source === null) return null;

  return { lineId, displayText, outcome, reason, required, gramsLow, gramsHigh, basis, mappingId, source };
}

function decodeUnresolved(value: unknown): NutritionUnresolvedLines | null {
  if (!isRecord(value)) return null;

  const unmapped = decodeIdList(value['unmapped']);
  const needsReview = decodeIdList(value['needsReview']);
  const unconvertible = decodeIdList(value['unconvertible']);
  const notQuantified = decodeIdList(value['notQuantified']);
  if (unmapped === null || needsReview === null || unconvertible === null) return null;
  if (notQuantified === null) return null;

  return { unmapped, needsReview, unconvertible, notQuantified };
}

/**
 * The estimate, or null when any part of it could not be read.
 *
 * Whole or nothing, as the dietary analysis is: a half-decoded estimate would drop a nutrient or a line, and
 * a dropped line reads as one that was counted. An estimate that cannot be read is reported as unavailable
 * rather than shown with a gap nobody can see.
 */
export function decodeNutritionEstimate(value: unknown): NutritionEstimate | null {
  if (!isRecord(value)) return null;

  const { recipeId, recipeVersionId, label, methodVersion, method, publishable, partial } = value;
  const versionNumber = decodeInteger(value['versionNumber']);
  const status = decodeClosedEnum<NutritionEstimateStatus>(ESTIMATE_STATUSES, value['status']);
  const confidence =
    value['confidence'] === null ? null : decodeClosedEnum<NutritionQuantityBasis>(QUANTITY_BASES, value['confidence']);
  const servings = value['servings'] === null ? null : decodeNumber(value['servings']);
  const servingsSource = decodeClosedEnum<NutritionServingsSource>(SERVINGS_SOURCES, value['servingsSource']);
  const nutrients = decodeList(value['nutrients'], decodeNutrient);
  const lines = decodeList(value['lines'], decodeLine);
  const sources = decodeList(value['sources'], decodeCitation);
  const unresolved = decodeUnresolved(value['unresolved']);

  if (typeof recipeId !== 'string' || typeof recipeVersionId !== 'string') return null;
  if (typeof label !== 'string' || typeof methodVersion !== 'string' || typeof method !== 'string') return null;
  if (typeof publishable !== 'boolean' || typeof partial !== 'boolean') return null;
  if (versionNumber === null || status === null || servingsSource === null) return null;
  if (value['confidence'] !== null && confidence === null) return null;
  if (value['servings'] !== null && servings === null) return null;
  if (nutrients === null || lines === null || sources === null || unresolved === null) return null;

  // The label and the method are what mark these figures as an estimate. An answer that arrives without
  // either is not a plainer answer to show quietly; it is one this client will not show.
  if (label.trim().length === 0 || method.trim().length === 0) return null;

  return {
    recipeId,
    recipeVersionId,
    versionNumber,
    label,
    methodVersion,
    method,
    status,
    publishable,
    partial,
    confidence,
    servings,
    servingsSource,
    nutrients,
    lines,
    sources,
    unresolved,
  };
}

function decodeReference(value: unknown): NutritionMappingReference | null {
  if (!isRecord(value)) return null;

  const { sourceCode, externalRecordId, description, sourceVersion } = value;
  if (typeof sourceCode !== 'string' || typeof externalRecordId !== 'string') return null;
  if (typeof description !== 'string' || typeof sourceVersion !== 'string') return null;

  return { sourceCode, externalRecordId, description, sourceVersion };
}

function decodePortion(value: unknown): NutritionMappingPortion | null {
  if (!isRecord(value)) return null;

  const { measurementUnitId, label } = value;
  const amount = decodeNumber(value['amount']);
  const grams = decodeNumber(value['grams']);
  const origin = decodeClosedEnum<NutritionPortionOrigin>(PORTION_ORIGINS, value['origin']);

  if (typeof measurementUnitId !== 'string' || amount === null || grams === null || origin === null) return null;
  if (label !== null && typeof label !== 'string') return null;

  return { measurementUnitId, amount, grams, label, origin };
}

export function decodeNutritionMapping(value: unknown): NutritionMapping | null {
  if (!isRecord(value)) return null;

  const { id, ingredientId, reviewSourceVersion, confirmedAt, updatedAt, concurrencyToken } = value;
  const status = decodeClosedEnum<NutritionMappingStatus>(MAPPING_STATUSES, value['status']);
  const matchMethod =
    value['matchMethod'] === null ? null : decodeClosedEnum<NutritionMatchMethod>(MATCH_METHODS, value['matchMethod']);
  const confidence =
    value['confidence'] === null
      ? null
      : decodeClosedEnum<NutritionMatchConfidence>(MATCH_CONFIDENCES, value['confidence']);
  const unresolvedReason =
    value['unresolvedReason'] === null
      ? null
      : decodeClosedEnum<NutritionUnresolvedReason>(UNRESOLVED_REASONS, value['unresolvedReason']);
  const reference = value['reference'] === null ? null : decodeReference(value['reference']);
  const portion = value['portion'] === null ? null : decodePortion(value['portion']);
  const rejectedReferenceCount = decodeInteger(value['rejectedReferenceCount']);

  if (typeof id !== 'string' || typeof ingredientId !== 'string') return null;
  if (typeof updatedAt !== 'string' || typeof concurrencyToken !== 'string') return null;
  if (reviewSourceVersion !== null && typeof reviewSourceVersion !== 'string') return null;
  if (confirmedAt !== null && typeof confirmedAt !== 'string') return null;
  if (status === null || rejectedReferenceCount === null) return null;
  if (value['matchMethod'] !== null && matchMethod === null) return null;
  if (value['confidence'] !== null && confidence === null) return null;
  if (value['unresolvedReason'] !== null && unresolvedReason === null) return null;
  if (value['reference'] !== null && reference === null) return null;
  if (value['portion'] !== null && portion === null) return null;

  // Nothing is decided without a token to quote back, so an answer without one is unusable rather than
  // read-only: a decision built on it would be refused as a conflict anyway.
  if (concurrencyToken.length === 0) return null;

  return {
    id,
    ingredientId,
    status,
    matchMethod,
    confidence,
    reference,
    reviewSourceVersion,
    unresolvedReason,
    portion,
    rejectedReferenceCount,
    confirmedAt,
    updatedAt,
    concurrencyToken,
  };
}
