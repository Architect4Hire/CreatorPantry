// Wire models for the dietary and allergen analysis (ING-007; baseline B-36 for the analysis, B-38 for the route):
//   POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/{versionNumber}/analysis/dietary-allergen
//
// Mirrors CreatorPantry.Domain/Modules/Nutrition/Managers/DietaryAllergenAnalysisModels.cs exactly. Keep in
// sync when that file changes.
//
// **A reading of records, never a verdict about food or a person.** There is no member here meaning safe, free
// from, certified or suitable, and none may be added (recipes.md, ai.md) — the server holds a guard test to
// that effect and this file is the same contract on this side of the wire.
//
// Nothing here decides anything. Every finding, every sentence and every count comes from the server, which is
// the only thing that may produce one: a client that derived a finding from the evidence it was shown could
// disagree with the analysis it is displaying, and the direction it would disagree in is the one that matters.
//
// Every enum is serialized with the same global JsonStringEnumConverter as the rest of the API — PascalCase
// member names on the wire, decoded by membership check, never a number lookup.

import { isRecord } from './recipe.models';

// ---------------------------------------------------------------------------
// Vocabulary
// ---------------------------------------------------------------------------

/**
 * Every finding enum here is decoded **closed**: an unrecognised value makes the whole analysis unreadable
 * rather than falling through to a member of this union.
 *
 * The opposite stance to `recipe-readiness.models.ts`'s tolerant evidence kinds, and for the same underlying
 * reason — what the failure would cost. A kind this app has not heard of can be humanised from its own name
 * and shown; a *finding* it has not heard of cannot be shown at all without guessing how cautious it is, and
 * the only safe guess would be the most cautious one, which would state something no source said. Refusing
 * the answer is the honest failure, and it is the one direction this must never fail in.
 */
export type AllergenLineFinding = 'Unknown' | 'Contains' | 'Possible' | 'NotListed';

const ALLERGEN_LINE_FINDINGS: ReadonlySet<string> = new Set<AllergenLineFinding>([
  'Unknown',
  'Contains',
  'Possible',
  'NotListed',
]);

export type DietaryLineFinding = 'Unknown' | 'Incompatible' | 'DependsOnProduct' | 'Compatible';

const DIETARY_LINE_FINDINGS: ReadonlySet<string> = new Set<DietaryLineFinding>([
  'Unknown',
  'Incompatible',
  'DependsOnProduct',
  'Compatible',
]);

/** One allergen across the whole recipe: the most cautious of its lines. */
export type RecipeAllergenFinding = 'Contains' | 'Possible' | 'Unknown' | 'NotListedInRecords';

const RECIPE_ALLERGEN_FINDINGS: ReadonlySet<string> = new Set<RecipeAllergenFinding>([
  'Contains',
  'Possible',
  'Unknown',
  'NotListedInRecords',
]);

/** One dietary profile across the whole recipe: the most cautious of its lines. */
export type RecipeDietaryFinding = 'DoesNotFit' | 'DependsOnProduct' | 'Unknown' | 'FitsAsRecorded';

const RECIPE_DIETARY_FINDINGS: ReadonlySet<string> = new Set<RecipeDietaryFinding>([
  'DoesNotFit',
  'DependsOnProduct',
  'Unknown',
  'FitsAsRecorded',
]);

/** Why a line's finding is what it is when there is no evidence to point at. */
export type TraitFindingReason = 'None' | 'NoRecord' | 'NotARecognisedIngredient';

const TRAIT_FINDING_REASONS: ReadonlySet<string> = new Set<TraitFindingReason>([
  'None',
  'NoRecord',
  'NotARecognisedIngredient',
]);

/**
 * How complete the records behind one finding are.
 *
 * A statement about the records, not about how likely the finding is: `Contains` on `Partial` records still
 * contains, and no surface may present this as a probability or a strength of evidence for the finding itself.
 */
export type AnalysisConfidence = 'None' | 'Partial' | 'Disputed' | 'Recorded';

const ANALYSIS_CONFIDENCES: ReadonlySet<string> = new Set<AnalysisConfidence>([
  'None',
  'Partial',
  'Disputed',
  'Recorded',
]);

/** What one source said about an allergen in one ingredient. */
export type AllergenPresence = 'Unknown' | 'Present' | 'PossiblePresence' | 'NotListedBySource';

const ALLERGEN_PRESENCES: ReadonlySet<string> = new Set<AllergenPresence>([
  'Unknown',
  'Present',
  'PossiblePresence',
  'NotListedBySource',
]);

/** What one source said about an ingredient against a dietary profile. */
export type DietaryCompatibility = 'Unknown' | 'Compatible' | 'Incompatible' | 'DependsOnProduct';

const DIETARY_COMPATIBILITIES: ReadonlySet<string> = new Set<DietaryCompatibility>([
  'Unknown',
  'Compatible',
  'Incompatible',
  'DependsOnProduct',
]);

/**
 * What kind of thing a cited source is. Mirrors ReferenceSourceKind.
 *
 * Carried through to the reader because it is what decides whether a reassuring word counts: only a vetted,
 * active source may state that something is absent (B-36).
 */
export type ReferenceSourceKind =
  | 'OfficialDatabase'
  | 'Publisher'
  | 'Manufacturer'
  | 'LabMeasured'
  | 'CommunityContributed'
  | 'AiEstimated';

const REFERENCE_SOURCE_KINDS: ReadonlySet<string> = new Set<ReferenceSourceKind>([
  'OfficialDatabase',
  'Publisher',
  'Manufacturer',
  'LabMeasured',
  'CommunityContributed',
  'AiEstimated',
]);

/** The kinds that may not state absence, so a reassuring word from one is cited and never counted. */
export const UNVETTED_SOURCE_KINDS: readonly ReferenceSourceKind[] = ['CommunityContributed', 'AiEstimated'];

// ---------------------------------------------------------------------------
// Shapes
// ---------------------------------------------------------------------------

/** Mirrors AnalysisCoverageServiceModel: how many lines were read, and how many of them have a record that counts. */
export interface AnalysisCoverage {
  readonly linesRead: number;
  readonly linesWithCountedRecords: number;
}

/** Mirrors AnalysisSourceServiceModel: one source the analysis read, and the latest of its records it saw. */
export interface AnalysisSource {
  readonly code: string;
  readonly name: string;
  readonly kind: ReferenceSourceKind;
  readonly citation: string;
  readonly licenseCode: string | null;
  readonly licenseUrl: string | null;
  /** False for a retired source: cited so it can be seen, never counted as reassurance. */
  readonly isActive: boolean;
  /** ISO 8601 date (`YYYY-MM-DD`). The newest effective date among this source's records that were read. */
  readonly latestEffectiveFrom: string;
}

/** Mirrors AllergenTraitEvidenceServiceModel: one source's approved word about an allergen in an ingredient. */
export interface AllergenTraitEvidence {
  readonly presence: AllergenPresence;
  readonly sourceCode: string;
  readonly sourceName: string;
  readonly sourceKind: ReferenceSourceKind;
  readonly citation: string;
  /** ISO 8601 date (`YYYY-MM-DD`): when this source's statement took effect. */
  readonly effectiveFrom: string;
  /** The source's own required note. Shown as written; never paraphrased. */
  readonly evidenceNote: string;
  /** False for a reassuring word from a source that may not give one. Cited, and never allowed to move a finding. */
  readonly counted: boolean;
}

/** Mirrors DietaryTraitEvidenceServiceModel. */
export interface DietaryTraitEvidence {
  readonly compatibility: DietaryCompatibility;
  readonly sourceCode: string;
  readonly sourceName: string;
  readonly sourceKind: ReferenceSourceKind;
  readonly citation: string;
  readonly effectiveFrom: string;
  readonly evidenceNote: string;
  readonly counted: boolean;
}

/** Mirrors AllergenLineAnalysisServiceModel: one recipe line, read for one allergen. */
export interface AllergenLineAnalysis {
  readonly lineId: string;
  /** The creator's own words for the line. Never rewritten, here or anywhere. */
  readonly displayText: string;
  readonly isOptional: boolean;
  /** Null where the line is not matched to a platform ingredient, so no record could be looked up. */
  readonly ingredientId: string | null;
  readonly finding: AllergenLineFinding;
  readonly reason: TraitFindingReason;
  /** Approved sources disagree about this line; the more cautious word was kept and both are cited. */
  readonly conflict: boolean;
  readonly evidence: readonly AllergenTraitEvidence[];
}

/** Mirrors DietaryLineAnalysisServiceModel. */
export interface DietaryLineAnalysis {
  readonly lineId: string;
  readonly displayText: string;
  readonly isOptional: boolean;
  readonly ingredientId: string | null;
  readonly finding: DietaryLineFinding;
  readonly reason: TraitFindingReason;
  readonly conflict: boolean;
  readonly evidence: readonly DietaryTraitEvidence[];
}

/** Mirrors AllergenAnalysisServiceModel: one selected allergen across the recipe. */
export interface AllergenAnalysis {
  readonly allergenId: string;
  readonly allergenName: string;
  readonly finding: RecipeAllergenFinding;
  readonly confidence: AnalysisConfidence;
  readonly coverage: AnalysisCoverage;
  /** The finding in one fixed sentence, composed server-side. Shown verbatim; nothing here parses or rewords it. */
  readonly statement: string;
  readonly containingLines: readonly string[];
  /** Lines a source says may contain it: the hidden sources and cross-contact the records know of. */
  readonly possibleSources: readonly string[];
  readonly unknownLines: readonly string[];
  readonly conflictingLines: readonly string[];
  readonly lines: readonly AllergenLineAnalysis[];
}

/** Mirrors DietaryAnalysisServiceModel: one selected dietary profile across the recipe. */
export interface DietaryAnalysis {
  readonly dietaryProfileId: string;
  readonly dietaryProfileName: string;
  readonly finding: RecipeDietaryFinding;
  readonly confidence: AnalysisConfidence;
  readonly coverage: AnalysisCoverage;
  readonly statement: string;
  readonly incompatibleLines: readonly string[];
  /** Lines whose fit varies by product: the hidden sources the records know of. */
  readonly dependsOnProductLines: readonly string[];
  readonly unknownLines: readonly string[];
  readonly conflictingLines: readonly string[];
  readonly lines: readonly DietaryLineAnalysis[];
}

/** Mirrors DietaryAllergenAnalysisServiceModel. */
export interface DietaryAllergenAnalysis {
  readonly recipeId: string;
  readonly recipeVersionId: string;
  readonly versionNumber: number;
  /** The decision table that produced this, e.g. `dietary-allergen/1`. */
  readonly methodVersion: string;
  /** ISO 8601 date (`YYYY-MM-DD`): the date the records were read as of, in UTC. */
  readonly evaluatedOn: string;
  /** On every analysis, whatever it found — the best answer included. Shown verbatim, never collapsed away. */
  readonly crossContactDisclaimer: string;
  readonly allergens: readonly AllergenAnalysis[];
  readonly dietary: readonly DietaryAnalysis[];
  readonly sources: readonly AnalysisSource[];
}

/** Mirrors DietaryAllergenSelectionViewModel: what to read the records against. */
export interface DietaryAllergenSelection {
  readonly allergenIds: readonly string[];
  readonly dietaryProfileIds: readonly string[];
}

/** `DietaryAllergenAnalysisPolicy.MaxSelected` — the most of either kind one analysis may be asked about. */
export const MAX_ANALYSIS_SUBJECTS = 20;

export function encodeDietaryAllergenSelection(selection: DietaryAllergenSelection): Record<string, unknown> {
  return {
    allergenIds: [...selection.allergenIds],
    dietaryProfileIds: [...selection.dietaryProfileIds],
  };
}

// ---------------------------------------------------------------------------
// Decoding
// ---------------------------------------------------------------------------

function decodeClosedEnum<T extends string>(values: ReadonlySet<string>, value: unknown): T | null {
  return typeof value === 'string' && values.has(value) ? (value as T) : null;
}

function decodeIdList(value: unknown): readonly string[] | null {
  if (!Array.isArray(value)) return null;
  return value.every((item) => typeof item === 'string') ? (value as string[]) : null;
}

/** An ISO date as the server writes it. Kept as text: nothing here does date arithmetic. */
function decodeDate(value: unknown): string | null {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) ? value : null;
}

function decodeInteger(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) ? value : null;
}

function decodeCoverage(value: unknown): AnalysisCoverage | null {
  if (!isRecord(value)) return null;

  const linesRead = decodeInteger(value['linesRead']);
  const linesWithCountedRecords = decodeInteger(value['linesWithCountedRecords']);
  if (linesRead === null || linesWithCountedRecords === null) return null;

  return { linesRead, linesWithCountedRecords };
}

export function decodeAnalysisSource(value: unknown): AnalysisSource | null {
  if (!isRecord(value)) return null;

  const { code, name, citation, licenseCode, licenseUrl, isActive } = value;
  const kind = decodeClosedEnum<ReferenceSourceKind>(REFERENCE_SOURCE_KINDS, value['kind']);
  const latestEffectiveFrom = decodeDate(value['latestEffectiveFrom']);

  if (typeof code !== 'string' || typeof name !== 'string' || typeof citation !== 'string') return null;
  if (licenseCode !== null && typeof licenseCode !== 'string') return null;
  if (licenseUrl !== null && typeof licenseUrl !== 'string') return null;
  if (typeof isActive !== 'boolean' || kind === null || latestEffectiveFrom === null) return null;

  return { code, name, kind, citation, licenseCode, licenseUrl, isActive, latestEffectiveFrom };
}

/** The parts every piece of evidence shares, whichever word it carries. */
function decodeEvidenceCommon(value: unknown): Omit<AllergenTraitEvidence, 'presence'> | null {
  if (!isRecord(value)) return null;

  const { sourceCode, sourceName, citation, evidenceNote, counted } = value;
  const sourceKind = decodeClosedEnum<ReferenceSourceKind>(REFERENCE_SOURCE_KINDS, value['sourceKind']);
  const effectiveFrom = decodeDate(value['effectiveFrom']);

  if (typeof sourceCode !== 'string' || typeof sourceName !== 'string') return null;
  if (typeof citation !== 'string' || typeof evidenceNote !== 'string') return null;
  if (typeof counted !== 'boolean' || sourceKind === null || effectiveFrom === null) return null;

  return { sourceCode, sourceName, sourceKind, citation, effectiveFrom, evidenceNote, counted };
}

function decodeAllergenEvidence(value: unknown): AllergenTraitEvidence | null {
  const common = decodeEvidenceCommon(value);
  if (common === null || !isRecord(value)) return null;

  const presence = decodeClosedEnum<AllergenPresence>(ALLERGEN_PRESENCES, value['presence']);
  return presence === null ? null : { presence, ...common };
}

function decodeDietaryEvidence(value: unknown): DietaryTraitEvidence | null {
  const common = decodeEvidenceCommon(value);
  if (common === null || !isRecord(value)) return null;

  const compatibility = decodeClosedEnum<DietaryCompatibility>(DIETARY_COMPATIBILITIES, value['compatibility']);
  return compatibility === null ? null : { compatibility, ...common };
}

/** The parts every line analysis shares. The finding itself is decoded by the caller, against its own set. */
function decodeLineCommon(value: unknown): Omit<AllergenLineAnalysis, 'finding' | 'evidence'> | null {
  if (!isRecord(value)) return null;

  const { lineId, displayText, isOptional, ingredientId, conflict } = value;
  const reason = decodeClosedEnum<TraitFindingReason>(TRAIT_FINDING_REASONS, value['reason']);

  if (typeof lineId !== 'string' || typeof displayText !== 'string') return null;
  if (typeof isOptional !== 'boolean' || typeof conflict !== 'boolean') return null;
  if (ingredientId !== null && typeof ingredientId !== 'string') return null;
  if (reason === null) return null;

  return { lineId, displayText, isOptional, ingredientId, reason, conflict };
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

function decodeAllergenLine(value: unknown): AllergenLineAnalysis | null {
  const common = decodeLineCommon(value);
  if (common === null || !isRecord(value)) return null;

  const finding = decodeClosedEnum<AllergenLineFinding>(ALLERGEN_LINE_FINDINGS, value['finding']);
  const evidence = decodeList(value['evidence'], decodeAllergenEvidence);
  if (finding === null || evidence === null) return null;

  return { ...common, finding, evidence };
}

function decodeDietaryLine(value: unknown): DietaryLineAnalysis | null {
  const common = decodeLineCommon(value);
  if (common === null || !isRecord(value)) return null;

  const finding = decodeClosedEnum<DietaryLineFinding>(DIETARY_LINE_FINDINGS, value['finding']);
  const evidence = decodeList(value['evidence'], decodeDietaryEvidence);
  if (finding === null || evidence === null) return null;

  return { ...common, finding, evidence };
}

function decodeAllergenAnalysis(value: unknown): AllergenAnalysis | null {
  if (!isRecord(value)) return null;

  const { allergenId, allergenName, statement } = value;
  const finding = decodeClosedEnum<RecipeAllergenFinding>(RECIPE_ALLERGEN_FINDINGS, value['finding']);
  const confidence = decodeClosedEnum<AnalysisConfidence>(ANALYSIS_CONFIDENCES, value['confidence']);
  const coverage = decodeCoverage(value['coverage']);
  const containingLines = decodeIdList(value['containingLines']);
  const possibleSources = decodeIdList(value['possibleSources']);
  const unknownLines = decodeIdList(value['unknownLines']);
  const conflictingLines = decodeIdList(value['conflictingLines']);
  const lines = decodeList(value['lines'], decodeAllergenLine);

  if (typeof allergenId !== 'string' || typeof allergenName !== 'string' || typeof statement !== 'string') return null;
  if (finding === null || confidence === null || coverage === null || lines === null) return null;
  if (containingLines === null || possibleSources === null || unknownLines === null || conflictingLines === null) {
    return null;
  }

  return {
    allergenId,
    allergenName,
    finding,
    confidence,
    coverage,
    statement,
    containingLines,
    possibleSources,
    unknownLines,
    conflictingLines,
    lines,
  };
}

function decodeDietaryAnalysis(value: unknown): DietaryAnalysis | null {
  if (!isRecord(value)) return null;

  const { dietaryProfileId, dietaryProfileName, statement } = value;
  const finding = decodeClosedEnum<RecipeDietaryFinding>(RECIPE_DIETARY_FINDINGS, value['finding']);
  const confidence = decodeClosedEnum<AnalysisConfidence>(ANALYSIS_CONFIDENCES, value['confidence']);
  const coverage = decodeCoverage(value['coverage']);
  const incompatibleLines = decodeIdList(value['incompatibleLines']);
  const dependsOnProductLines = decodeIdList(value['dependsOnProductLines']);
  const unknownLines = decodeIdList(value['unknownLines']);
  const conflictingLines = decodeIdList(value['conflictingLines']);
  const lines = decodeList(value['lines'], decodeDietaryLine);

  if (typeof dietaryProfileId !== 'string' || typeof dietaryProfileName !== 'string') return null;
  if (typeof statement !== 'string') return null;
  if (finding === null || confidence === null || coverage === null || lines === null) return null;
  if (incompatibleLines === null || dependsOnProductLines === null) return null;
  if (unknownLines === null || conflictingLines === null) return null;

  return {
    dietaryProfileId,
    dietaryProfileName,
    finding,
    confidence,
    coverage,
    statement,
    incompatibleLines,
    dependsOnProductLines,
    unknownLines,
    conflictingLines,
    lines,
  };
}

/**
 * The analysis, or null when any part of it could not be read.
 *
 * Null for the whole answer rather than for the part that failed: a half-decoded analysis would drop a
 * finding, and a dropped finding reads as "nothing was found about this" — the one meaning this data can
 * never be allowed to acquire by accident.
 */
export function decodeDietaryAllergenAnalysis(value: unknown): DietaryAllergenAnalysis | null {
  if (!isRecord(value)) return null;

  const { recipeId, recipeVersionId, methodVersion, crossContactDisclaimer } = value;
  const versionNumber = decodeInteger(value['versionNumber']);
  const evaluatedOn = decodeDate(value['evaluatedOn']);
  const allergens = decodeList(value['allergens'], decodeAllergenAnalysis);
  const dietary = decodeList(value['dietary'], decodeDietaryAnalysis);
  const sources = decodeList(value['sources'], decodeAnalysisSource);

  if (typeof recipeId !== 'string' || typeof recipeVersionId !== 'string') return null;
  if (typeof methodVersion !== 'string' || typeof crossContactDisclaimer !== 'string') return null;
  if (versionNumber === null || evaluatedOn === null) return null;
  if (allergens === null || dietary === null || sources === null) return null;

  // The disclaimer is required on every analysis (B-36). An answer that arrives without one is not a weaker
  // answer to show quietly — it is an answer this client cannot show at all.
  if (crossContactDisclaimer.trim().length === 0) return null;

  return {
    recipeId,
    recipeVersionId,
    versionNumber,
    methodVersion,
    evaluatedOn,
    crossContactDisclaimer,
    allergens,
    dietary,
    sources,
  };
}
