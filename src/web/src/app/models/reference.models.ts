// Wire models for the shared platform catalogue at /api/v1/reference/*. Mirrors
// CreatorPantry.Domain/Modules/Measurement/Managers/MeasurementManagers.cs, MeasurementSystem.cs,
// CreatorPantry.Domain/Managers/Reference/MeasurementDimension.cs,
// CreatorPantry.Domain/Modules/Ingredients/Managers/IngredientManagers.cs and
// CreatorPantry.Domain/Managers/Paging/CursorPageServiceModel.cs exactly.
//
// Reference data is global, not workspace-owned (tenancy.md): these routes sit outside
// /workspaces/{workspaceSlug}, no workspace is resolved while they run, and nothing here may ever carry a
// workspace identifier.
//
// Every enum is serialized with the same global JsonStringEnumConverter as recipe.models.ts — PascalCase
// member names on the wire, decoded by membership check, never a number lookup.

import { decodeEnum, isNumberOrNull, isRecord, isStringOrNull } from './recipe.models';

/** Mirrors CreatorPantry.Domain.Managers.Reference.MeasurementDimension. */
export type MeasurementDimension = 'Mass' | 'Volume' | 'Count' | 'Temperature' | 'Qualitative';

/** Mirrors CreatorPantry.Domain.Modules.Measurement.Managers.MeasurementSystem. */
export type MeasurementSystem = 'Neutral' | 'Metric' | 'UsCustomary' | 'Imperial';

const MEASUREMENT_DIMENSION_VALUES: ReadonlySet<string> = new Set<MeasurementDimension>([
  'Mass',
  'Volume',
  'Count',
  'Temperature',
  'Qualitative',
]);

const MEASUREMENT_SYSTEM_VALUES: ReadonlySet<string> = new Set<MeasurementSystem>([
  'Neutral',
  'Metric',
  'UsCustomary',
  'Imperial',
]);

/**
 * Mirrors System.MidpointRounding, as the global JsonStringEnumConverter writes it.
 *
 * Lives here rather than beside any one calculation: scaling, unit conversion, temperature conversion and
 * display normalization all publish the mode they rounded with, and it is a fact about measurement rather
 * than about any of them.
 */
export type MidpointRounding = 'ToEven' | 'AwayFromZero' | 'ToZero' | 'ToNegativeInfinity' | 'ToPositiveInfinity';

export const MIDPOINT_ROUNDING_VALUES: ReadonlySet<string> = new Set<MidpointRounding>([
  'ToEven',
  'AwayFromZero',
  'ToZero',
  'ToNegativeInfinity',
  'ToPositiveInfinity',
]);

/**
 * The only two modes a calculation will actually apply.
 *
 * `MidpointRounding` defines five, and the decoder above accepts all five because the server could echo any
 * of them. But only half-to-even and half-away-from-zero are meaningful for an exact rational, and the
 * calculators refuse the rest — so these are the two a picker may offer.
 */
export const SUPPORTED_ROUNDING_MODES: readonly MidpointRounding[] = ['ToEven', 'AwayFromZero'];

/**
 * Mirrors `QuantityFormat.MaxDisplayPrecision`. Beyond it the server refuses the request, so a precision
 * input caps here rather than letting a creator type a number that comes back as an error.
 */
export const MAX_DISPLAY_PRECISION = 6;

/**
 * How each rounding mode reads to a creator.
 *
 * Spelled out rather than shown as the enum name, because CALC-002 asks for rounding to be *documented* and
 * "ToEven" documents nothing to anyone who has not met the term.
 */
export const MIDPOINT_ROUNDING_LABELS: Readonly<Record<MidpointRounding, string>> = {
  ToEven: 'Half to even — a halfway value goes to the nearest even digit',
  AwayFromZero: 'Half away from zero — a halfway value rounds up',
  ToZero: 'Toward zero — the fraction is dropped',
  ToNegativeInfinity: 'Down — toward negative infinity',
  ToPositiveInfinity: 'Up — toward positive infinity',
};

/** How each dimension reads to a creator, for grouping a picker by something other than the enum name. */
export const MEASUREMENT_DIMENSION_LABELS: Readonly<Record<MeasurementDimension, string>> = {
  Mass: 'Weight',
  Volume: 'Volume',
  Count: 'Count',
  Temperature: 'Temperature',
  Qualitative: 'Descriptive',
};

/**
 * How each system reads to a creator. `Neutral` is blank because a clove belongs to no system and saying so
 * would be answering a question nobody asked.
 */
export const MEASUREMENT_SYSTEM_LABELS: Readonly<Record<MeasurementSystem, string>> = {
  Neutral: '',
  Metric: 'metric',
  UsCustomary: 'US customary',
  Imperial: 'imperial',
};

/**
 * The prefix the catalogue puts on a unit whose name would otherwise be ambiguous across systems — a US cup is
 * 236.6ml and an imperial one is 284ml, so `US cup` earns its words.
 *
 * Kept as data beside the labels so a surface that would rather say the system on a second line can take the
 * prefix off the name rather than string-matching for it. Null where no prefix is used.
 */
export const MEASUREMENT_SYSTEM_NAME_PREFIXES: Readonly<Record<MeasurementSystem, string | null>> = {
  Neutral: null,
  Metric: null,
  UsCustomary: 'US ',
  Imperial: 'Imperial ',
};

/** Mirrors MeasurementUnitServiceModel. */
export interface MeasurementUnit {
  readonly id: string;
  /** The catalogue's own stable key for the unit, e.g. `gram`, `celsius`. Never shown to a creator. */
  readonly code: string;
  readonly displayName: string;
  readonly pluralName: string;
  readonly abbreviation: string;
  readonly dimension: MeasurementDimension;
  readonly system: MeasurementSystem;
  /**
   * How many of this unit's dimension's base unit one of these is. Null for `Temperature`, whose scales are
   * affine rather than multiplicative, and for `Qualitative`, which has no numeric relationship at all.
   */
  readonly baseUnitFactor: number | null;
  readonly displayPrecision: number;
}

export function decodeMeasurementUnit(value: unknown): MeasurementUnit | null {
  if (!isRecord(value)) return null;
  const {
    id,
    code,
    displayName,
    pluralName,
    abbreviation,
    dimension: rawDimension,
    system: rawSystem,
    baseUnitFactor,
    displayPrecision,
  } = value;

  const dimension = decodeEnum<MeasurementDimension>(MEASUREMENT_DIMENSION_VALUES, rawDimension);
  const system = decodeEnum<MeasurementSystem>(MEASUREMENT_SYSTEM_VALUES, rawSystem);

  if (
    typeof id !== 'string' ||
    typeof code !== 'string' ||
    typeof displayName !== 'string' ||
    typeof pluralName !== 'string' ||
    typeof abbreviation !== 'string' ||
    dimension === null ||
    system === null ||
    !isNumberOrNull(baseUnitFactor) ||
    typeof displayPrecision !== 'number'
  ) {
    return null;
  }

  return { id, code, displayName, pluralName, abbreviation, dimension, system, baseUnitFactor, displayPrecision };
}

/**
 * One ingredient in the shared platform catalogue. Mirrors IngredientServiceModel.
 *
 * A reference, never a replacement: matching one of these enriches a recipe line while the creator's own
 * wording stays canonical (recipes.md).
 */
export interface Ingredient {
  readonly id: string;
  readonly canonicalName: string;
  readonly foodCategoryCode: string | null;
  /**
   * The unit to suggest for a bare number — "2 garlic" proposing cloves. A suggestion only; it never
   * overrides a unit the creator actually wrote.
   */
  readonly defaultCountUnitCode: string | null;
  /**
   * The aliases that may have caused a search to match. Carried so a picker can show *why* "scallion" found
   * green onion — without it, a correct match is indistinguishable from a bug.
   */
  readonly aliases: readonly string[];
}

export function decodeIngredient(value: unknown): Ingredient | null {
  if (!isRecord(value)) return null;
  const { id, canonicalName, foodCategoryCode, defaultCountUnitCode, aliases } = value;

  if (
    typeof id !== 'string' ||
    typeof canonicalName !== 'string' ||
    !isStringOrNull(foodCategoryCode) ||
    !isStringOrNull(defaultCountUnitCode) ||
    !Array.isArray(aliases) ||
    aliases.some((alias) => typeof alias !== 'string')
  ) {
    return null;
  }

  return { id, canonicalName, foodCategoryCode, defaultCountUnitCode, aliases: aliases as string[] };
}

/**
 * One entry in a plain controlled vocabulary — a cuisine, a course, an equipment type, a food category.
 * Mirrors ReferenceEntryServiceModel.
 */
export interface ReferenceEntry {
  readonly id: string;
  /** The catalogue's own stable key, e.g. `thai`, `main-course`. What a record stores; never shown. */
  readonly code: string;
  readonly displayName: string;
}

export function decodeReferenceEntry(value: unknown): ReferenceEntry | null {
  if (!isRecord(value)) return null;
  const { id, code, displayName } = value;

  if (typeof id !== 'string' || typeof code !== 'string' || typeof displayName !== 'string') return null;

  return { id, code, displayName };
}

/**
 * A cooking method. Mirrors CookingTechniqueServiceModel.
 *
 * **`requiresSafetyCaution: false` means no caution has been attached to this technique** — never that the
 * technique is safe, and no surface may render it as one (.claude/rules/ai.md).
 */
export interface CookingTechnique extends ReferenceEntry {
  readonly requiresSafetyCaution: boolean;
}

export function decodeCookingTechnique(value: unknown): CookingTechnique | null {
  const entry = decodeReferenceEntry(value);
  if (entry === null || !isRecord(value)) return null;

  const { requiresSafetyCaution } = value;
  if (typeof requiresSafetyCaution !== 'boolean') return null;

  return { ...entry, requiresSafetyCaution };
}

/** Mirrors CursorPageServiceModel<T>. */
export interface CursorPage<T> {
  readonly items: readonly T[];
  readonly nextCursor: string | null;
}

export function decodeCursorPage<T>(value: unknown, decodeItem: (item: unknown) => T | null): CursorPage<T> | null {
  if (!isRecord(value)) return null;
  const { items: rawItems, nextCursor } = value;
  if (!Array.isArray(rawItems) || !isStringOrNull(nextCursor)) return null;

  const items: T[] = [];
  for (const rawItem of rawItems) {
    const item = decodeItem(rawItem);
    if (item === null) return null;
    items.push(item);
  }

  return { items, nextCursor };
}
