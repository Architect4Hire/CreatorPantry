// Wire models for the high-risk cooking-method warnings (AIREC-GR-005 / AI-009/010; baseline B-37, exposed by
// 14.7's H-3):
//   GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/{versionNumber}/analysis/food-safety
//
// Mirrors CreatorPantry.Domain/Modules/Nutrition/Managers/FoodSafetyWarningModels.cs exactly.
//
// **Not a verdict, in either direction.** Finding nothing is not a statement that a recipe is safe, and a
// warning is not a ruling that it is unsafe. No message says what the hazard is or what a reader must do —
// each would be a claim no approved reference backs (B-33 approves no food-safety source), which is why every
// warning carries `verificationRequired`.
//
// Nothing here detects anything. The detector is deterministic domain code reading the recipe's own words and
// structured choices; this file decodes what it said.

import { isRecord } from './recipe.models';

// ---------------------------------------------------------------------------
// Vocabulary
// ---------------------------------------------------------------------------
//
// Closed, like the other safety enums: a category or a confidence this build has not heard of cannot be shown
// without guessing how serious it is, and the safe guess would invent a warning the detector did not give.

export type FoodSafetyCategory =
  | 'Canning'
  | 'Fermentation'
  | 'CuringOrSmoking'
  | 'SousVide'
  | 'RawOrUndercookedProtein'
  | 'InternalTemperature'
  | 'PregnancyOrMedicalDiet'
  | 'UnsupportedSafetyClaim'
  | 'Drying'
  | 'OilInfusion'
  | 'OtherFlaggedMethod';

const CATEGORIES: ReadonlySet<string> = new Set<FoodSafetyCategory>([
  'Canning',
  'Fermentation',
  'CuringOrSmoking',
  'SousVide',
  'RawOrUndercookedProtein',
  'InternalTemperature',
  'PregnancyOrMedicalDiet',
  'UnsupportedSafetyClaim',
  'Drying',
  'OilInfusion',
  'OtherFlaggedMethod',
]);

/** How firmly the recipe's own text shows the method — never how dangerous it is. */
export type FoodSafetyConfidence = 'Possible' | 'Detected';

const CONFIDENCES: ReadonlySet<string> = new Set<FoodSafetyConfidence>(['Possible', 'Detected']);

/** Where in the version a trigger was found. */
export type FoodSafetyField =
  | 'Title'
  | 'Description'
  | 'Headnote'
  | 'Notes'
  | 'StorageNotes'
  | 'PrimaryTechnique'
  | 'IngredientLine'
  | 'IngredientPreparation'
  | 'Step'
  | 'StepNote'
  | 'StepTechnique'
  | 'Equipment'
  | 'EquipmentNote';

const FIELDS: ReadonlySet<string> = new Set<FoodSafetyField>([
  'Title',
  'Description',
  'Headnote',
  'Notes',
  'StorageNotes',
  'PrimaryTechnique',
  'IngredientLine',
  'IngredientPreparation',
  'Step',
  'StepNote',
  'StepTechnique',
  'Equipment',
  'EquipmentNote',
]);

/** What raised the trigger: a structured choice, the creator's words, or an ingredient's category. */
export type FoodSafetyTriggerKind = 'Technique' | 'Text' | 'IngredientCategory';

const TRIGGER_KINDS: ReadonlySet<string> = new Set<FoodSafetyTriggerKind>([
  'Technique',
  'Text',
  'IngredientCategory',
]);

/**
 * Whether a warning could be checked against an approved reference, and why not.
 *
 * `Cited` is unreachable today: B-33 approves no food-safety source, so every warning arrives
 * `NoApprovedSource`. The member exists so a future citation needs no wire change, and so a screen written
 * now cannot assume the absence is permanent.
 */
export type FoodSafetyEvidenceStatus =
  | 'Cited'
  | 'NoApprovedSource'
  | 'NotConfigured'
  | 'Unavailable'
  | 'NoReference';

const EVIDENCE_STATUSES: ReadonlySet<string> = new Set<FoodSafetyEvidenceStatus>([
  'Cited',
  'NoApprovedSource',
  'NotConfigured',
  'Unavailable',
  'NoReference',
]);

export type FoodSafetyTemperatureScale = 'Fahrenheit' | 'Celsius';

const SCALES: ReadonlySet<string> = new Set<FoodSafetyTemperatureScale>(['Fahrenheit', 'Celsius']);

// ---------------------------------------------------------------------------
// Shapes
// ---------------------------------------------------------------------------

/** One thing in the version that raised a warning, with the creator's own matched words. */
export interface FoodSafetyTrigger {
  readonly field: FoodSafetyField;
  /** The line, step or equipment row, where the field names one. */
  readonly itemId: string | null;
  readonly kind: FoodSafetyTriggerKind;
  /** The creator's words, at most 120 characters of them, or a technique code. Never rewritten. */
  readonly matched: string;
}

/** An approved reference behind a warning. Null today, for every warning (B-33). */
export interface FoodSafetyCitation {
  readonly sourceCode: string;
  readonly sourceName: string;
  readonly sourceVersion: string;
  readonly citation: string;
  readonly licenseCode: string | null;
  readonly url: string | null;
  readonly retrievedAt: string;
}

export interface FoodSafetyEvidence {
  readonly status: FoodSafetyEvidenceStatus;
  readonly citation: FoodSafetyCitation | null;
  /** What the evidence is, in words, composed server-side. Shown verbatim. */
  readonly statement: string;
  /** True for every status but `Cited` — so, today, for every warning there is. */
  readonly verificationRequired: boolean;
}

/** An internal temperature the recipe states, recorded as written and never compared. */
export interface FoodSafetyStatedTemperature {
  readonly value: number;
  readonly scale: FoodSafetyTemperatureScale;
  readonly field: FoodSafetyField;
  readonly itemId: string | null;
}

export interface FoodSafetyWarning {
  readonly category: FoodSafetyCategory;
  readonly confidence: FoodSafetyConfidence;
  /** The server's sentence. Says what the record shows; never what the hazard is or what to do. */
  readonly message: string;
  readonly triggers: readonly FoodSafetyTrigger[];
  readonly evidence: FoodSafetyEvidence;
  /** Only on `InternalTemperature`; empty elsewhere. */
  readonly statedTemperatures: readonly FoodSafetyStatedTemperature[];
}

export interface FoodSafetyWarnings {
  readonly recipeId: string;
  readonly recipeVersionId: string;
  readonly versionNumber: number;
  readonly methodVersion: string;
  readonly warnings: readonly FoodSafetyWarning[];
  /**
   * Techniques the version names that are not in the active vocabulary, so their flags could not be read. A
   * non-zero count means an empty warning list was read from less than the whole record.
   */
  readonly unrecognisedTechniques: number;
  /** On every result, an empty one most of all. Shown verbatim, never collapsed. */
  readonly disclaimer: string;
}

// ---------------------------------------------------------------------------
// Decoding
// ---------------------------------------------------------------------------

function decodeClosedEnum<T extends string>(values: ReadonlySet<string>, value: unknown): T | null {
  return typeof value === 'string' && values.has(value) ? (value as T) : null;
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

function decodeTrigger(value: unknown): FoodSafetyTrigger | null {
  if (!isRecord(value)) return null;

  const field = decodeClosedEnum<FoodSafetyField>(FIELDS, value['field']);
  const kind = decodeClosedEnum<FoodSafetyTriggerKind>(TRIGGER_KINDS, value['kind']);
  const { itemId, matched } = value;

  if (field === null || kind === null || typeof matched !== 'string') return null;
  if (itemId !== null && typeof itemId !== 'string') return null;

  return { field, itemId, kind, matched };
}

function decodeCitation(value: unknown): FoodSafetyCitation | null {
  if (!isRecord(value)) return null;

  const { sourceCode, sourceName, sourceVersion, citation, licenseCode, url, retrievedAt } = value;

  if (typeof sourceCode !== 'string' || typeof sourceName !== 'string') return null;
  if (typeof sourceVersion !== 'string' || typeof citation !== 'string') return null;
  if (typeof retrievedAt !== 'string') return null;
  if (licenseCode !== null && typeof licenseCode !== 'string') return null;
  if (url !== null && typeof url !== 'string') return null;

  return { sourceCode, sourceName, sourceVersion, citation, licenseCode, url, retrievedAt };
}

function decodeEvidence(value: unknown): FoodSafetyEvidence | null {
  if (!isRecord(value)) return null;

  const status = decodeClosedEnum<FoodSafetyEvidenceStatus>(EVIDENCE_STATUSES, value['status']);
  const citation = value['citation'] === null ? null : decodeCitation(value['citation']);
  const { statement, verificationRequired } = value;

  if (status === null || typeof statement !== 'string') return null;
  if (value['citation'] !== null && citation === null) return null;

  // Computed server-side from the status; derived here only if an older host omits it, never guessed as false.
  const required = typeof verificationRequired === 'boolean' ? verificationRequired : status !== 'Cited';

  return { status, citation, statement, verificationRequired: required };
}

function decodeTemperature(value: unknown): FoodSafetyStatedTemperature | null {
  if (!isRecord(value)) return null;

  const scale = decodeClosedEnum<FoodSafetyTemperatureScale>(SCALES, value['scale']);
  const field = decodeClosedEnum<FoodSafetyField>(FIELDS, value['field']);
  const { value: amount, itemId } = value;

  if (scale === null || field === null) return null;
  if (typeof amount !== 'number' || !Number.isFinite(amount)) return null;
  if (itemId !== null && typeof itemId !== 'string') return null;

  return { value: amount, scale, field, itemId };
}

function decodeWarning(value: unknown): FoodSafetyWarning | null {
  if (!isRecord(value)) return null;

  const category = decodeClosedEnum<FoodSafetyCategory>(CATEGORIES, value['category']);
  const confidence = decodeClosedEnum<FoodSafetyConfidence>(CONFIDENCES, value['confidence']);
  const triggers = decodeList(value['triggers'], decodeTrigger);
  const evidence = decodeEvidence(value['evidence']);
  const statedTemperatures = decodeList(value['statedTemperatures'], decodeTemperature);
  const { message } = value;

  if (category === null || confidence === null || typeof message !== 'string') return null;
  if (triggers === null || evidence === null || statedTemperatures === null) return null;

  return { category, confidence, message, triggers, evidence, statedTemperatures };
}

/**
 * The warnings, or null when any part could not be read.
 *
 * Whole or nothing, as the sibling analyses are: a dropped warning is indistinguishable from a recipe that
 * raised none, and this is the one screen where that difference is the whole point. An answer without its
 * disclaimer is refused for the same reason — the disclaimer is what says an empty list is not a clearance.
 */
export function decodeFoodSafetyWarnings(value: unknown): FoodSafetyWarnings | null {
  if (!isRecord(value)) return null;

  const { recipeId, recipeVersionId, methodVersion, disclaimer, versionNumber, unrecognisedTechniques } = value;
  const warnings = decodeList(value['warnings'], decodeWarning);

  if (typeof recipeId !== 'string' || typeof recipeVersionId !== 'string') return null;
  if (typeof methodVersion !== 'string' || typeof disclaimer !== 'string') return null;
  if (typeof versionNumber !== 'number' || !Number.isInteger(versionNumber)) return null;
  if (typeof unrecognisedTechniques !== 'number' || !Number.isInteger(unrecognisedTechniques)) return null;
  if (warnings === null) return null;
  if (disclaimer.trim().length === 0) return null;

  return {
    recipeId,
    recipeVersionId,
    versionNumber,
    methodVersion,
    warnings,
    unrecognisedTechniques,
    disclaimer,
  };
}
