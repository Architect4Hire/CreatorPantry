// Wire models for POST /api/v1/workspaces/{workspaceSlug}/ingredient-tools/parse. Mirrors
// CreatorPantry.Domain/Modules/Recipes/Managers/IngredientParsingManagers.cs, IngredientLineTokens.cs,
// QuantityToken.cs, PackageQuantityToken.cs, IngredientLineAmbiguity.cs and the ingredient/unit match
// models in CreatorPantry.Domain/Modules/{Ingredients,Measurement}/Managers exactly — do not add fields
// the backend doesn't send, and keep this file in sync when those ServiceModels change.
//
// Every enum here is serialized with the same global JsonStringEnumConverter as recipe.models.ts —
// PascalCase member names on the wire, decoded by membership check, never a number lookup.

import { decodeEnum, isRecord } from './recipe.models';

export type IngredientMatchKind = 'CanonicalName' | 'Alias';
export type UnitMatchKind = 'Code' | 'DisplayName' | 'Abbreviation' | 'PluralName' | 'Alias';
export type IngredientLineAmbiguityKind = 'NoQuantityDetected' | 'InvalidQuantity';

const INGREDIENT_MATCH_KIND_VALUES: ReadonlySet<string> = new Set<IngredientMatchKind>(['CanonicalName', 'Alias']);
const UNIT_MATCH_KIND_VALUES: ReadonlySet<string> = new Set<UnitMatchKind>([
  'Code',
  'DisplayName',
  'Abbreviation',
  'PluralName',
  'Alias',
]);
const AMBIGUITY_KIND_VALUES: ReadonlySet<string> = new Set<IngredientLineAmbiguityKind>([
  'NoQuantityDetected',
  'InvalidQuantity',
]);

/** Mirrors CreatorPantry.Domain.Modules.Recipes.Managers.IngredientLineSpan. */
export interface IngredientLineSpan {
  readonly start: number;
  readonly length: number;
  readonly text: string;
}

function decodeIngredientLineSpan(value: unknown): IngredientLineSpan | null {
  if (!isRecord(value)) return null;
  const { start, length, text } = value;
  if (typeof start !== 'number' || typeof length !== 'number' || typeof text !== 'string') return null;
  return { start, length, text };
}

/** Mirrors CreatorPantry.Domain.Managers.Quantities.Quantity's JSON contract — an exact fraction, never a float. */
export interface Quantity {
  readonly numerator: string;
  readonly denominator: string;
}

function decodeQuantity(value: unknown): Quantity | null {
  if (!isRecord(value)) return null;
  const { numerator, denominator } = value;
  if (typeof numerator !== 'string' || typeof denominator !== 'string') return null;
  return { numerator, denominator };
}

/** Mirrors CreatorPantry.Domain.Managers.Quantities.QuantityRange's JSON contract. */
export interface QuantityRange {
  readonly lower: Quantity;
  readonly upper: Quantity;
}

function decodeQuantityRange(value: unknown): QuantityRange | null {
  if (!isRecord(value)) return null;
  const { lower, upper } = value;
  const decodedLower = decodeQuantity(lower);
  const decodedUpper = decodeQuantity(upper);
  if (decodedLower === null || decodedUpper === null) return null;
  return { lower: decodedLower, upper: decodedUpper };
}

/** Mirrors QuantityToken. Exactly one of `value`/`range` is set, unless `isInvalid` is true, in which case neither is. */
export interface QuantityToken {
  readonly span: IngredientLineSpan;
  readonly value: Quantity | null;
  readonly range: QuantityRange | null;
  readonly isInvalid: boolean;
}

function decodeQuantityToken(value: unknown): QuantityToken | null {
  if (!isRecord(value)) return null;
  const { span, value: rawValue, range: rawRange, isInvalid } = value;
  const decodedSpan = decodeIngredientLineSpan(span);
  if (decodedSpan === null || typeof isInvalid !== 'boolean') return null;

  if (rawValue !== null && !isRecord(rawValue)) return null;
  const decodedValue = rawValue === null ? null : decodeQuantity(rawValue);
  if (rawValue !== null && decodedValue === null) return null;

  if (rawRange !== null && !isRecord(rawRange)) return null;
  const decodedRange = rawRange === null ? null : decodeQuantityRange(rawRange);
  if (rawRange !== null && decodedRange === null) return null;

  return { span: decodedSpan, value: decodedValue, range: decodedRange, isInvalid };
}

/** Mirrors PackageQuantityToken — the "(14.5 oz)" aside in "1 (14.5 oz) can diced tomatoes". */
export interface PackageQuantityToken {
  readonly span: IngredientLineSpan;
  readonly quantity: QuantityToken;
  readonly unit: IngredientLineSpan;
}

function decodePackageQuantityToken(value: unknown): PackageQuantityToken | null {
  if (!isRecord(value)) return null;
  const { span, quantity, unit } = value;
  const decodedSpan = decodeIngredientLineSpan(span);
  const decodedQuantity = decodeQuantityToken(quantity);
  const decodedUnit = decodeIngredientLineSpan(unit);
  if (decodedSpan === null || decodedQuantity === null || decodedUnit === null) return null;
  return { span: decodedSpan, quantity: decodedQuantity, unit: decodedUnit };
}

/** Mirrors IngredientLineAmbiguity. */
export interface IngredientLineAmbiguity {
  readonly kind: IngredientLineAmbiguityKind;
  readonly span: IngredientLineSpan;
  readonly message: string;
}

function decodeIngredientLineAmbiguity(value: unknown): IngredientLineAmbiguity | null {
  if (!isRecord(value)) return null;
  const { kind: rawKind, span, message } = value;
  const kind = decodeEnum<IngredientLineAmbiguityKind>(AMBIGUITY_KIND_VALUES, rawKind);
  const decodedSpan = decodeIngredientLineSpan(span);
  if (kind === null || decodedSpan === null || typeof message !== 'string') return null;
  return { kind, span: decodedSpan, message };
}

/** Mirrors IngredientLineTokens — the tokenizer's segmentation of one free-form ingredient line. */
export interface IngredientLineTokens {
  readonly originalText: string;
  readonly isGroupMarker: boolean;
  readonly quantity: QuantityToken | null;
  readonly packageQuantity: PackageQuantityToken | null;
  readonly unitCandidate: IngredientLineSpan | null;
  readonly ingredientText: IngredientLineSpan | null;
  readonly preparationText: readonly IngredientLineSpan[];
  readonly isOptional: boolean;
  readonly ambiguities: readonly IngredientLineAmbiguity[];
}

function decodeIngredientLineTokens(value: unknown): IngredientLineTokens | null {
  if (!isRecord(value)) return null;
  const {
    originalText,
    isGroupMarker,
    quantity: rawQuantity,
    packageQuantity: rawPackageQuantity,
    unitCandidate: rawUnitCandidate,
    ingredientText: rawIngredientText,
    preparationText,
    isOptional,
    ambiguities,
  } = value;

  if (
    typeof originalText !== 'string' ||
    typeof isGroupMarker !== 'boolean' ||
    typeof isOptional !== 'boolean' ||
    !Array.isArray(preparationText) ||
    !Array.isArray(ambiguities)
  ) {
    return null;
  }

  if (rawQuantity !== null && !isRecord(rawQuantity)) return null;
  const quantity = rawQuantity === null ? null : decodeQuantityToken(rawQuantity);
  if (rawQuantity !== null && quantity === null) return null;

  if (rawPackageQuantity !== null && !isRecord(rawPackageQuantity)) return null;
  const packageQuantity = rawPackageQuantity === null ? null : decodePackageQuantityToken(rawPackageQuantity);
  if (rawPackageQuantity !== null && packageQuantity === null) return null;

  if (rawUnitCandidate !== null && !isRecord(rawUnitCandidate)) return null;
  const unitCandidate = rawUnitCandidate === null ? null : decodeIngredientLineSpan(rawUnitCandidate);
  if (rawUnitCandidate !== null && unitCandidate === null) return null;

  if (rawIngredientText !== null && !isRecord(rawIngredientText)) return null;
  const ingredientText = rawIngredientText === null ? null : decodeIngredientLineSpan(rawIngredientText);
  if (rawIngredientText !== null && ingredientText === null) return null;

  const decodedPreparationText = preparationText.map(decodeIngredientLineSpan);
  if (decodedPreparationText.some((span) => span === null)) return null;

  const decodedAmbiguities = ambiguities.map(decodeIngredientLineAmbiguity);
  if (decodedAmbiguities.some((ambiguity) => ambiguity === null)) return null;

  return {
    originalText,
    isGroupMarker,
    quantity,
    packageQuantity,
    unitCandidate,
    ingredientText,
    preparationText: decodedPreparationText as IngredientLineSpan[],
    isOptional,
    ambiguities: decodedAmbiguities as IngredientLineAmbiguity[],
  };
}

/** Mirrors CreatorPantry.Domain.Modules.Ingredients.Managers.IngredientMatchCandidate. */
export interface IngredientMatchCandidate {
  readonly ingredientId: string;
  readonly canonicalName: string;
  readonly kind: IngredientMatchKind;
}

function decodeIngredientMatchCandidate(value: unknown): IngredientMatchCandidate | null {
  if (!isRecord(value)) return null;
  const { ingredientId, canonicalName, kind: rawKind } = value;
  const kind = decodeEnum<IngredientMatchKind>(INGREDIENT_MATCH_KIND_VALUES, rawKind);
  if (typeof ingredientId !== 'string' || typeof canonicalName !== 'string' || kind === null) return null;
  return { ingredientId, canonicalName, kind };
}

/** Mirrors IngredientMatchResult. */
export interface IngredientMatchResult {
  readonly inputText: string;
  readonly resolved: IngredientMatchCandidate | null;
  readonly alternates: readonly IngredientMatchCandidate[];
  readonly isAmbiguous: boolean;
}

function decodeIngredientMatchResult(value: unknown): IngredientMatchResult | null {
  if (!isRecord(value)) return null;
  const { inputText, resolved: rawResolved, alternates, isAmbiguous } = value;
  if (typeof inputText !== 'string' || typeof isAmbiguous !== 'boolean' || !Array.isArray(alternates)) return null;

  if (rawResolved !== null && !isRecord(rawResolved)) return null;
  const resolved = rawResolved === null ? null : decodeIngredientMatchCandidate(rawResolved);
  if (rawResolved !== null && resolved === null) return null;

  const decodedAlternates = alternates.map(decodeIngredientMatchCandidate);
  if (decodedAlternates.some((candidate) => candidate === null)) return null;

  return { inputText, resolved, alternates: decodedAlternates as IngredientMatchCandidate[], isAmbiguous };
}

/** Mirrors CreatorPantry.Domain.Modules.Measurement.Managers.UnitMatchCandidate. */
export interface UnitMatchCandidate {
  readonly measurementUnitId: string;
  readonly displayName: string;
  readonly kind: UnitMatchKind;
}

function decodeUnitMatchCandidate(value: unknown): UnitMatchCandidate | null {
  if (!isRecord(value)) return null;
  const { measurementUnitId, displayName, kind: rawKind } = value;
  const kind = decodeEnum<UnitMatchKind>(UNIT_MATCH_KIND_VALUES, rawKind);
  if (typeof measurementUnitId !== 'string' || typeof displayName !== 'string' || kind === null) return null;
  return { measurementUnitId, displayName, kind };
}

/** Mirrors UnitMatchResult. */
export interface UnitMatchResult {
  readonly inputText: string;
  readonly resolved: UnitMatchCandidate | null;
  readonly alternates: readonly UnitMatchCandidate[];
  readonly isAmbiguous: boolean;
}

function decodeUnitMatchResult(value: unknown): UnitMatchResult | null {
  if (!isRecord(value)) return null;
  const { inputText, resolved: rawResolved, alternates, isAmbiguous } = value;
  if (typeof inputText !== 'string' || typeof isAmbiguous !== 'boolean' || !Array.isArray(alternates)) return null;

  if (rawResolved !== null && !isRecord(rawResolved)) return null;
  const resolved = rawResolved === null ? null : decodeUnitMatchCandidate(rawResolved);
  if (rawResolved !== null && resolved === null) return null;

  const decodedAlternates = alternates.map(decodeUnitMatchCandidate);
  if (decodedAlternates.some((candidate) => candidate === null)) return null;

  return { inputText, resolved, alternates: decodedAlternates as UnitMatchCandidate[], isAmbiguous };
}

/** Mirrors ParsedIngredientLine. A proposal only — nothing here is a recipe ingredient until confirmed. */
export interface ParsedIngredientLine {
  readonly tokens: IngredientLineTokens;
  readonly ingredientMatch: IngredientMatchResult | null;
  readonly unitMatch: UnitMatchResult | null;
}

function decodeParsedIngredientLine(value: unknown): ParsedIngredientLine | null {
  if (!isRecord(value)) return null;
  const { tokens, ingredientMatch: rawIngredientMatch, unitMatch: rawUnitMatch } = value;

  const decodedTokens = decodeIngredientLineTokens(tokens);
  if (decodedTokens === null) return null;

  if (rawIngredientMatch !== null && !isRecord(rawIngredientMatch)) return null;
  const ingredientMatch = rawIngredientMatch === null ? null : decodeIngredientMatchResult(rawIngredientMatch);
  if (rawIngredientMatch !== null && ingredientMatch === null) return null;

  if (rawUnitMatch !== null && !isRecord(rawUnitMatch)) return null;
  const unitMatch = rawUnitMatch === null ? null : decodeUnitMatchResult(rawUnitMatch);
  if (rawUnitMatch !== null && unitMatch === null) return null;

  return { tokens: decodedTokens, ingredientMatch, unitMatch };
}

/** Mirrors ParseIngredientLinesResult — the POST .../ingredient-tools/parse response body. */
export interface ParseIngredientLinesResponse {
  readonly lines: readonly ParsedIngredientLine[];
}

export function decodeParseIngredientLinesResponse(value: unknown): ParseIngredientLinesResponse | null {
  if (!isRecord(value)) return null;
  const { lines } = value;
  if (!Array.isArray(lines)) return null;

  const decoded = lines.map(decodeParsedIngredientLine);
  if (decoded.some((line) => line === null)) return null;

  return { lines: decoded as ParsedIngredientLine[] };
}

/** Mirrors ParseIngredientLinesViewModel — the request body. */
export interface ParseIngredientLinesRequest {
  readonly lines: readonly string[];
}

export function encodeParseIngredientLinesRequest(request: ParseIngredientLinesRequest): Record<string, unknown> {
  return { lines: request.lines };
}
