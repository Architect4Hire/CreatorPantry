// Wire model for the export summary (RCPUB-002/003/004):
//   GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/exports/summary
//
// Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/RecipeExportSummaryModels.cs. Keep in sync.
//
// Nothing here decides whether a recipe may be exported or whether copy is current. Both come from the
// server, which is the only thing that applies the gate an export is actually refused by — a client that
// recomputed either could disagree with it.

import { isRecord, isStringOrNull } from './recipe.models';

/** Mirrors RecipeExportAcceptedCopyServiceModel. */
export interface RecipeExportAcceptedCopy {
  readonly revisionNumber: number;
  /** False when the copy was accepted for an older version or has been marked for review. */
  readonly isCurrent: boolean;
}

/** Mirrors RecipeExportSummaryServiceModel. */
export interface RecipeExportSummary {
  readonly versionNumber: number;
  readonly exportable: boolean;
  /** A stable code, present exactly when `exportable` is false. */
  readonly notExportableReason: string | null;
  /** Null when nothing is accepted. */
  readonly editorial: RecipeExportAcceptedCopy | null;
  readonly seo: RecipeExportAcceptedCopy | null;
}

/** The layout choices the export routes accept. Values are the query strings the server parses. */
export type RecipeExportTemplate = 'standard' | 'compact';
export type RecipeExportUnits = 'asWritten' | 'metric' | 'usCustomary';
export type RecipeExportPageSize = 'a4' | 'letter';

export interface RecipeExportChoices {
  readonly template: RecipeExportTemplate;
  readonly units: RecipeExportUnits;
  readonly pageSize: RecipeExportPageSize;
}

export const DEFAULT_EXPORT_CHOICES: RecipeExportChoices = {
  template: 'standard',
  units: 'asWritten',
  pageSize: 'a4',
};

export const EXPORT_TEMPLATE_OPTIONS: readonly { readonly value: RecipeExportTemplate; readonly label: string }[] = [
  { value: 'standard', label: 'Standard — description, equipment and accepted copy' },
  { value: 'compact', label: 'Compact — ingredients, steps and notes only' },
];

export const EXPORT_UNITS_OPTIONS: readonly { readonly value: RecipeExportUnits; readonly label: string }[] = [
  { value: 'asWritten', label: 'As written' },
  { value: 'metric', label: 'Metric, added beside your amounts' },
  { value: 'usCustomary', label: 'US customary, added beside your amounts' },
];

export const EXPORT_PAGE_SIZE_OPTIONS: readonly { readonly value: RecipeExportPageSize; readonly label: string }[] = [
  { value: 'a4', label: 'A4' },
  { value: 'letter', label: 'US Letter' },
];

/** The one reason code the server sends today. Others are shown as a generic sentence, never as the code. */
export const NOT_EXPORTABLE_REASON_NOT_APPROVED = 'recipe_export_not_approved';

function isPositiveInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1;
}

function decodeAcceptedCopy(value: unknown): RecipeExportAcceptedCopy | null | undefined {
  // `undefined` is "malformed", distinct from `null` meaning "nothing accepted".
  if (value === null) return null;
  if (!isRecord(value)) return undefined;

  const { revisionNumber, isCurrent } = value;
  return isPositiveInteger(revisionNumber) && typeof isCurrent === 'boolean'
    ? { revisionNumber, isCurrent }
    : undefined;
}

/**
 * Decodes the summary, or returns null when it is not the shape the server sends.
 *
 * Closed on the booleans and on both copies: an answer this client cannot read must never be rendered as
 * "exportable" or "current", so a malformed one is unavailable rather than guessed at.
 */
export function decodeRecipeExportSummary(raw: unknown): RecipeExportSummary | null {
  if (!isRecord(raw)) return null;

  const { versionNumber, exportable, notExportableReason } = raw;
  if (!isPositiveInteger(versionNumber) || typeof exportable !== 'boolean' || !isStringOrNull(notExportableReason)) {
    return null;
  }

  // The reason is present exactly when the version is not exportable.
  if (exportable === (notExportableReason !== null)) return null;

  const editorial = decodeAcceptedCopy(raw['editorial']);
  const seo = decodeAcceptedCopy(raw['seo']);
  if (editorial === undefined || seo === undefined) return null;

  return { versionNumber, exportable, notExportableReason, editorial, seo };
}
