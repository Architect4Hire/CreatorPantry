// Wire models for the recipe asset-link commands (RCPUB-005):
// POST and DELETE /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/asset-links[/{linkId}].
// Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/RecipeAssetLinkModels.cs — do not add fields the
// backend does not read, and keep this file in step with that one.

import { RecipeAssetLink, RecipeAssetRole, RecipeDetail } from './recipe.models';

/** `RecipePolicy.CaptionMaxLength`. */
export const RECIPE_ASSET_CAPTION_MAX_LENGTH = 500;

/** The roles in the order a creator meets them: the one picture first, then the ones tied to the method. */
export const RECIPE_ASSET_ROLES: readonly RecipeAssetRole[] = ['Hero', 'Step', 'Gallery', 'Process', 'Social'];

/** What each role is called on screen. The wire names are the server's and are never shown. */
export const RECIPE_ASSET_ROLE_LABELS: Readonly<Record<RecipeAssetRole, string>> = {
  Hero: 'Lead picture',
  Step: 'Step picture',
  Gallery: 'Gallery',
  Process: 'In progress',
  Social: 'Social',
};

/** One line under each choice, saying what picking it means. */
export const RECIPE_ASSET_ROLE_HINTS: Readonly<Record<RecipeAssetRole, string>> = {
  Hero: 'The one picture that stands for the recipe.',
  Step: 'Shows one step of the method.',
  Gallery: 'A finished-dish picture alongside the lead.',
  Process: 'The recipe part-way through, not tied to one step.',
  Social: 'Cut or framed for a social post.',
};

/** The heading each group of linked pictures sits under. */
export const RECIPE_ASSET_ROLE_HEADINGS: Readonly<Record<RecipeAssetRole, string>> = {
  Hero: 'Lead picture',
  Step: 'Step pictures',
  Gallery: 'Gallery',
  Process: 'In progress',
  Social: 'Social',
};

/** `LinkRecipeAssetViewModel`. */
export interface LinkRecipeAssetRequest {
  readonly mediaAssetId: string;
  readonly role: RecipeAssetRole;
  /** Set exactly when `role` is `Step`. */
  readonly instructionStepId: string | null;
  /** The version to keep using, or null to follow whichever is current. */
  readonly versionNumber: number | null;
  readonly caption: string | null;
  readonly expectedConcurrencyToken: string;
}

/**
 * The body the route reads. Absent and null mean the same to the server, so what does not apply is left out
 * rather than sent as null — and a step is never sent with a role that cannot have one, whatever the caller
 * was still holding from an earlier choice.
 */
export function encodeLinkRecipeAssetRequest(request: LinkRecipeAssetRequest): Record<string, unknown> {
  const body: Record<string, unknown> = {
    mediaAssetId: request.mediaAssetId,
    role: request.role,
    expectedConcurrencyToken: request.expectedConcurrencyToken,
  };

  if (request.role === 'Step' && request.instructionStepId !== null) body['instructionStepId'] = request.instructionStepId;
  if (request.versionNumber !== null) body['versionNumber'] = request.versionNumber;

  const caption = request.caption?.trim() ?? '';
  if (caption.length > 0) body['caption'] = caption;

  return body;
}

/** The fields of a link request the server can name in a refusal, and this screen has a control for. */
export type RecipeAssetLinkField = 'mediaAssetId' | 'role' | 'instructionStepId' | 'versionNumber' | 'caption';

export type RecipeAssetLinkFieldErrors = Readonly<Record<string, readonly string[]>>;

export type LinkRecipeAssetOutcome =
  | { readonly status: 'linked'; readonly recipe: RecipeDetail; readonly replayed: boolean }
  /** Shape: something the form let through that the server will not read. */
  | { readonly status: 'validation_failed'; readonly fieldErrors: RecipeAssetLinkFieldErrors }
  /**
   * The picture, its version or the step is not there to link. An unknown asset, another workspace's and one
   * removed from the library are one answer on purpose, so this says only which field.
   */
  | { readonly status: 'target_refused'; readonly fieldErrors: RecipeAssetLinkFieldErrors }
  | { readonly status: 'hero_exists' }
  | { readonly status: 'already_linked' }
  /** The recipe changed since it was read. Nothing was written; re-reading is the remedy. */
  | { readonly status: 'conflict' }
  | { readonly status: 'archived_conflict' }
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type UnlinkRecipeAssetOutcome =
  | { readonly status: 'unlinked'; readonly recipe: RecipeDetail; readonly replayed: boolean }
  /** The link is already gone — someone else unlinked it. Re-reading shows the recipe as it stands. */
  | { readonly status: 'link_not_found' }
  | { readonly status: 'conflict' }
  | { readonly status: 'archived_conflict' }
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** A step of the saved recipe, as a step picture's picker and a linked row both name it. */
export interface RecipeMediaStep {
  readonly id: string;
  /** One-based, counted through the whole method across its groups. */
  readonly number: number;
  readonly text: string;
}

const STEP_LABEL_TEXT_LENGTH = 60;

/** "Step 2: Fold in the flour…" — the number, and enough of the creator's own words to recognise it by. */
export function recipeMediaStepLabel(step: RecipeMediaStep): string {
  const text = step.text.trim().replace(/\s+/g, ' ');
  const shown = text.length > STEP_LABEL_TEXT_LENGTH ? `${text.slice(0, STEP_LABEL_TEXT_LENGTH).trimEnd()}…` : text;

  return shown.length > 0 ? `Step ${step.number}: ${shown}` : `Step ${step.number}`;
}

export interface RecipeAssetLinkGroup {
  readonly role: RecipeAssetRole;
  readonly heading: string;
  readonly links: readonly RecipeAssetLink[];
}

/**
 * The recipe's links under their roles, in the order roles are offered, each group in the recipe's own order.
 * A role with no pictures has no group: an empty heading is a claim that something belongs there.
 */
export function groupRecipeAssetLinks(links: readonly RecipeAssetLink[]): readonly RecipeAssetLinkGroup[] {
  return RECIPE_ASSET_ROLES.map((role) => ({
    role,
    heading: RECIPE_ASSET_ROLE_HEADINGS[role],
    links: links.filter((link) => link.role === role).sort((a, b) => a.sortOrder - b.sortOrder),
  })).filter((group) => group.links.length > 0);
}

/**
 * How a link's version is described: following the current one, or held at a number — and, when held, whether
 * the library has since moved past it. `currentVersionNumber` is null when the asset could not be read, and
 * then nothing is claimed about what is newer.
 */
export function recipeAssetPinText(pinned: number | null, currentVersionNumber: number | null): string {
  if (pinned === null) {
    return currentVersionNumber === null
      ? 'Follows the current version'
      : `Follows the current version (version ${currentVersionNumber})`;
  }

  return currentVersionNumber !== null && currentVersionNumber > pinned
    ? `Kept at version ${pinned} · version ${currentVersionNumber} is newer`
    : `Kept at version ${pinned}`;
}
