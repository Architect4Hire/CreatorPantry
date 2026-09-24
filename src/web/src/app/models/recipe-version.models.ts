// Wire models for the recipe version-history and version-comparison reads
// (GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions and .../versions/compare).
// Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/{RecipeVersionHistoryServiceModels,
// RecipeVersionComparisonModels,RecipeComparisonModels}.cs — do not add fields the backend doesn't
// send, and keep this file in sync when those ServiceModels change.
//
// The comparison is the server's and is the only one. Nothing here derives a difference: these types
// decode an answer, and the component that renders them re-orders nothing and re-computes nothing.
//
// Two decoding stances live side by side here, on purpose:
//
//   Tolerant — `field` and `section` are plain strings. Adding a RecipeComparisonField or
//   RecipeComparisonSection member is a compatible server change (api-contract.md), and the renderer
//   humanises a name it has never seen, so a new field costs a prettier label and nothing else.
//
//   Strict — `source`, `readiness` and `presence` are closed unions, and an unrecognised value fails the
//   whole decode. `presence` genuinely cannot grow: an item is on one side, the other, or both. `source` and
//   `readiness` could, and the cost of that is high — decodeArray rejects a whole array on one bad element,
//   so a single new RecipeVersionSource member would blank the history list rather than mislabel one row.
//   They are held closed anyway, because every other decoder in this app decodes enums closed and one file
//   deciding otherwise would be its own inconsistency. If a new source member is ever added server-side,
//   widen these two here first.

// The shared value sets and primitive guards are imported rather than redeclared: two spellings of one
// server enum is exactly the drift the header above warns about.
import {
  RECIPE_VERSION_READINESS_VALUES,
  RECIPE_VERSION_SOURCE_VALUES,
  RecipeVersionReadiness,
  RecipeVersionSource,
  decodeEnum,
  isNumberOrNull,
  isRecord,
  isStringOrNull,
} from './recipe.models';

/** Mirrors RecipeItemPresence. A closed three-value concept: an item is on one side, the other, or both. */
export type RecipeItemPresence = 'Retained' | 'Added' | 'Removed';

const RECIPE_ITEM_PRESENCE_VALUES: ReadonlySet<string> = new Set<RecipeItemPresence>(['Retained', 'Added', 'Removed']);

/**
 * Decodes an array, rejecting the whole thing if any element fails. A partially decoded comparison would
 * understate what changed, which is the one mistake a diff must not make.
 */
function decodeArray<T>(value: unknown, decode: (element: unknown) => T | null): readonly T[] | null {
  if (!Array.isArray(value)) return null;

  const decoded: T[] = [];
  for (const element of value) {
    const item = decode(element);
    if (item === null) return null;
    decoded.push(item);
  }
  return decoded;
}

// ---------------------------------------------------------------------------
// Version history
// ---------------------------------------------------------------------------

/** Mirrors RecipeVersionHistoryServiceModel. */
export interface RecipeVersionHistoryEntry {
  readonly id: string;
  readonly versionNumber: number;
  readonly source: RecipeVersionSource;
  readonly readiness: RecipeVersionReadiness;
  readonly reason: string | null;
  readonly createdAt: string;
  /**
   * The display name of whoever wrote this version, or null when the membership behind it can no longer be
   * named — authorship is recorded so it survives a member leaving, so this will happen.
   */
  readonly createdByName: string | null;
  readonly parentVersionId: string | null;
  /** The version this one's content was copied from, when `source` is `Restore`. */
  readonly restoredFromVersionId: string | null;
  readonly aiProposalId: string | null;
}

/** Mirrors CursorPageServiceModel<RecipeVersionHistoryServiceModel>. */
export interface RecipeVersionHistoryPage {
  readonly items: readonly RecipeVersionHistoryEntry[];
  readonly nextCursor: string | null;
}

function decodeRecipeVersionHistoryEntry(value: unknown): RecipeVersionHistoryEntry | null {
  if (!isRecord(value)) return null;
  const { id, versionNumber, reason, createdAt, createdByName, parentVersionId, restoredFromVersionId, aiProposalId } =
    value;
  const source = decodeEnum<RecipeVersionSource>(RECIPE_VERSION_SOURCE_VALUES, value['source']);
  const readiness = decodeEnum<RecipeVersionReadiness>(RECIPE_VERSION_READINESS_VALUES, value['readiness']);

  if (
    typeof id !== 'string' ||
    typeof versionNumber !== 'number' ||
    source === null ||
    readiness === null ||
    !isStringOrNull(reason) ||
    typeof createdAt !== 'string' ||
    !isStringOrNull(createdByName) ||
    !isStringOrNull(parentVersionId) ||
    !isStringOrNull(restoredFromVersionId) ||
    !isStringOrNull(aiProposalId)
  ) {
    return null;
  }

  return {
    id,
    versionNumber,
    source,
    readiness,
    reason,
    createdAt,
    createdByName,
    parentVersionId,
    restoredFromVersionId,
    aiProposalId,
  };
}

export function decodeRecipeVersionHistoryPage(value: unknown): RecipeVersionHistoryPage | null {
  if (!isRecord(value)) return null;
  const items = decodeArray(value['items'], decodeRecipeVersionHistoryEntry);
  const nextCursor = value['nextCursor'];

  if (items === null || !isStringOrNull(nextCursor)) return null;

  return { items, nextCursor };
}

// ---------------------------------------------------------------------------
// Version comparison
// ---------------------------------------------------------------------------

/**
 * Mirrors RecipeFieldChange.
 *
 * `field` is a plain string rather than a union of the ~45 RecipeComparisonField members, deliberately.
 * Adding a field to the snapshot adds a member, and api-contract.md treats that as a compatible change —
 * a client that refused to decode an unrecognised one would turn every such addition into a broken
 * comparison. The renderer derives a human label from the name instead, so a new field reads sensibly
 * the day it ships.
 *
 * `from` and `to` are the server's rendering of the two values, already invariant-culture strings. `null`
 * means the field held nothing on that side — empty, or, on an added or removed item, absent entirely.
 */
export interface RecipeFieldChange {
  readonly field: string;
  readonly from: string | null;
  readonly to: string | null;
}

/**
 * Mirrors RecipeItemChange.
 *
 * `presence` and `moved` are independent facts: an item that was reworded and dragged reports both, and
 * one that only moved reports `moved` with no field changes at all. Ranks are positions among siblings,
 * not stored sort orders — the server already decided what counts as a move, and nothing here second-
 * guesses it.
 */
export interface RecipeItemChange {
  readonly id: string;
  readonly presence: RecipeItemPresence;
  readonly moved: boolean;
  readonly fromParentId: string | null;
  readonly toParentId: string | null;
  readonly fromRank: number | null;
  readonly toRank: number | null;
  readonly fieldChanges: readonly RecipeFieldChange[];
}

/**
 * Mirrors RecipeComparisonSectionResult.
 *
 * `section` is a plain string for the same reason `field` is, and because nothing here needs to know the
 * names: the server publishes every section in a fixed order whether or not it changed, so the renderer
 * walks the array it was given rather than imposing an order of its own.
 */
export interface RecipeComparisonSectionResult {
  readonly section: string;
  readonly fieldChanges: readonly RecipeFieldChange[];
  readonly itemChanges: readonly RecipeItemChange[];
  readonly hasChanges: boolean;
}

/** Mirrors RecipeComparison. */
export interface RecipeComparison {
  readonly sections: readonly RecipeComparisonSectionResult[];
  readonly hasChanges: boolean;
}

/** Mirrors RecipeVersionComparisonSideServiceModel. */
export interface RecipeVersionComparisonSide {
  readonly versionId: string;
  readonly versionNumber: number;
  readonly source: RecipeVersionSource;
  readonly readiness: RecipeVersionReadiness;
  readonly createdAt: string;
}

/** Mirrors RecipeVersionComparisonServiceModel. */
export interface RecipeVersionComparison {
  readonly from: RecipeVersionComparisonSide;
  readonly to: RecipeVersionComparisonSide;
  readonly comparison: RecipeComparison;
}

function decodeRecipeFieldChange(value: unknown): RecipeFieldChange | null {
  if (!isRecord(value)) return null;
  const { field, from, to } = value;

  if (typeof field !== 'string' || !isStringOrNull(from) || !isStringOrNull(to)) return null;

  return { field, from, to };
}

function decodeRecipeItemChange(value: unknown): RecipeItemChange | null {
  if (!isRecord(value)) return null;
  const { id, moved, fromParentId, toParentId, fromRank, toRank } = value;
  const presence = decodeEnum<RecipeItemPresence>(RECIPE_ITEM_PRESENCE_VALUES, value['presence']);
  const fieldChanges = decodeArray(value['fieldChanges'], decodeRecipeFieldChange);

  if (
    typeof id !== 'string' ||
    presence === null ||
    typeof moved !== 'boolean' ||
    !isStringOrNull(fromParentId) ||
    !isStringOrNull(toParentId) ||
    !isNumberOrNull(fromRank) ||
    !isNumberOrNull(toRank) ||
    fieldChanges === null
  ) {
    return null;
  }

  return { id, presence, moved, fromParentId, toParentId, fromRank, toRank, fieldChanges };
}

/**
 * `hasChanges` is a computed C# property, so the published schema does not list it as required even though
 * the server always sends it. Hard-failing on a field the contract marks optional would blank the whole
 * panel, so an absent one falls back to the same derivation the server makes — reading the answer it already
 * gave, not deciding anything about it. An explicitly wrong type still fails.
 */
function decodeHasChanges(value: unknown, derived: boolean): boolean | null {
  if (value === undefined) return derived;
  return typeof value === 'boolean' ? value : null;
}

function decodeRecipeComparisonSection(value: unknown): RecipeComparisonSectionResult | null {
  if (!isRecord(value)) return null;
  const { section } = value;
  const fieldChanges = decodeArray(value['fieldChanges'], decodeRecipeFieldChange);
  const itemChanges = decodeArray(value['itemChanges'], decodeRecipeItemChange);

  if (typeof section !== 'string' || fieldChanges === null || itemChanges === null) return null;

  const hasChanges = decodeHasChanges(value['hasChanges'], fieldChanges.length > 0 || itemChanges.length > 0);
  if (hasChanges === null) return null;

  return { section, fieldChanges, itemChanges, hasChanges };
}

function decodeRecipeVersionComparisonSide(value: unknown): RecipeVersionComparisonSide | null {
  if (!isRecord(value)) return null;
  const { versionId, versionNumber, createdAt } = value;
  const source = decodeEnum<RecipeVersionSource>(RECIPE_VERSION_SOURCE_VALUES, value['source']);
  const readiness = decodeEnum<RecipeVersionReadiness>(RECIPE_VERSION_READINESS_VALUES, value['readiness']);

  if (
    typeof versionId !== 'string' ||
    typeof versionNumber !== 'number' ||
    source === null ||
    readiness === null ||
    typeof createdAt !== 'string'
  ) {
    return null;
  }

  return { versionId, versionNumber, source, readiness, createdAt };
}

export function decodeRecipeVersionComparison(value: unknown): RecipeVersionComparison | null {
  if (!isRecord(value)) return null;

  const from = decodeRecipeVersionComparisonSide(value['from']);
  const to = decodeRecipeVersionComparisonSide(value['to']);
  const rawComparison = value['comparison'];

  if (from === null || to === null || !isRecord(rawComparison)) return null;

  const sections = decodeArray(rawComparison['sections'], decodeRecipeComparisonSection);
  if (sections === null) return null;

  const hasChanges = decodeHasChanges(
    rawComparison['hasChanges'],
    sections.some((section) => section.hasChanges),
  );
  if (hasChanges === null) return null;

  return { from, to, comparison: { sections, hasChanges } };
}

// ---------------------------------------------------------------------------
// Restore
// ---------------------------------------------------------------------------

/**
 * The body of POST .../recipes/{recipeId}/versions/{versionNumber}/restore — mirrors
 * `RestoreRecipeVersionViewModel`.
 *
 * Which version to restore is deliberately not here: it is the route's own segment, as it is server-side.
 * Nor is any recipe content — everything restored comes from the archive, which is what makes "a restore
 * cannot smuggle in an edit" structural rather than a rule this client is trusted to keep.
 */
export interface RestoreRecipeVersionRequest {
  /** The `concurrencyToken` from the read this restore was decided against. Required by the server. */
  readonly expectedConcurrencyToken: string;

  /** The creator's own words, recorded on the version the restore writes. Optional. */
  readonly reason: string | null;
}

/**
 * Trims the reason and omits it entirely when it is empty, so a creator who tabbed through the box without
 * typing does not store a blank string where the history expects either words or nothing.
 */
export function encodeRestoreRecipeVersionRequest(request: RestoreRecipeVersionRequest): Record<string, unknown> {
  const reason = (request.reason ?? '').trim();

  return reason.length === 0
    ? { expectedConcurrencyToken: request.expectedConcurrencyToken }
    : { expectedConcurrencyToken: request.expectedConcurrencyToken, reason };
}
