// IMG-002: one editable image prompt for one shot of one approved concept, as
// `POST/GET .../image-prompt-requests` sends it. Mirrors
// CreatorPantry.Domain/Modules/Ai/Managers/AiImagePromptRequestModels.cs, AiImagePromptOutputDocument.cs and
// ImagePromptAiTaskHandler.cs.
//
// **The creator's edit is authoritative.** What comes back is a draft; the prompt that counts is whatever they
// have in the box. This file decodes the draft and encodes the request, and takes no view on which is final —
// that is the pipeline draft's job.
//
// There is no rendering parameter anywhere in the contract: no provider, model, seed, sampler or dimension.
// IMG-002 composes text and renders nothing.

import { AiProposalDetail, AiProposalWarning } from './ai-proposal.models';
import { PhotographyShotKind } from './photography-concept.models';

/** What the model composed, before the creator touched it. */
export interface ComposedImagePrompt {
  /** The prompt, as one block of text a creator edits and an image model reads. */
  readonly prompt: string;
  /**
   * What the image model should avoid, as short phrases.
   *
   * Its own list rather than a sentence inside the prompt, because image models take negative guidance as a
   * separate input and because a workspace's visual guide carries negative direction the creator wrote.
   */
  readonly avoid: readonly string[];
}

const LIST_SEPARATOR = ';';

function splitList(value: string | null): readonly string[] {
  if (value === null) return [];

  return value
    .split(LIST_SEPARATOR)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

/**
 * The composed prompt in one proposal, or null when there is none.
 *
 * One `Add`/`ImagePrompt` row carries the prompt; a `Set` row field-named `avoid` carries the negative guidance.
 */
export function decodeComposedImagePrompt(detail: AiProposalDetail | null): ComposedImagePrompt | null {
  if (detail === null) return null;

  const addRow = detail.changes.find(
    (change) => change.changeKind === 'Add' && change.targetKind === 'ImagePrompt',
  );
  if (addRow === undefined || addRow.afterValue === null) return null;

  const avoidRow = detail.changes.find(
    (change) =>
      change.changeKind === 'Set' && change.targetKind === 'ImagePrompt' && change.fieldName === 'avoid',
  );

  return { prompt: addRow.afterValue, avoid: splitList(avoidRow?.afterValue ?? null) };
}

/**
 * What the creator should know about this composition.
 *
 * **Read separately from the prompt, and that is the point.** A proposal can carry a `Limitation` saying a brief
 * could not be read, or a `SafetyCaution`, *and* no usable prompt — and warnings hung off the prompt would be
 * dropped in exactly the case the creator most needs them (.claude/rules/ai.md).
 */
export function composedPromptWarnings(detail: AiProposalDetail | null): readonly AiProposalWarning[] {
  return detail === null ? [] : detail.warnings;
}

/** What a client sends to ask for a composed prompt. The concept and its shot are required. */
export interface RequestImagePromptRequest {
  /** The IMG-001 request whose proposal holds the concept. */
  readonly conceptRequestId: string;
  /** The concept within that proposal, as its rows identify it. Server-minted; a client only ever echoes one. */
  readonly conceptId: string;
  readonly shotKind: PhotographyShotKind;
  readonly channelKey: string | null;
  /** The recipe being photographed, where the creator linked one. Null sends no recipe at all. */
  readonly recipeId?: string | null;
  /** The version pinned when it was linked. Never sent without {@link recipeId}. */
  readonly recipeVersionId?: string | null;
  /**
   * What the creator calls the dish, for work with no recipe linked. Null or blank sends none.
   *
   * Never sent beside {@link recipeId}: the route refuses both, and `contentSubjectOf` is where that
   * precedence is decided.
   */
  readonly dishName?: string | null;
  /** A brief the creator uploaded, as a brand source document of theirs. Its text is untrusted. */
  readonly briefDocumentId: string | null;
  readonly sceneOverrides: readonly string[];
  readonly styleOverrides: readonly string[];
}

export function encodeRequestImagePrompt(request: RequestImagePromptRequest): Record<string, unknown> {
  const body: Record<string, unknown> = {
    conceptRequestId: request.conceptRequestId,
    conceptId: request.conceptId,
    shotKind: request.shotKind,
  };

  if (request.channelKey) body['channelKey'] = request.channelKey;
  if (request.recipeId) {
    body['recipeId'] = request.recipeId;
    // Inside the recipe's own branch: the route refuses a version that arrives without its recipe.
    if (request.recipeVersionId) body['recipeVersionId'] = request.recipeVersionId;
  }
  // Outside the recipe's branch, and only when there is no recipe: the route refuses both together.
  else if (request.dishName?.trim()) body['dishName'] = request.dishName.trim();
  if (request.briefDocumentId) body['briefDocumentId'] = request.briefDocumentId;
  if (request.sceneOverrides.length > 0) body['sceneOverrides'] = [...request.sceneOverrides];
  if (request.styleOverrides.length > 0) body['styleOverrides'] = [...request.styleOverrides];

  return body;
}

/** `AiImagePromptRequestErrors.TaskNotEnabled`. */
export const IMAGE_PROMPT_NOT_ENABLED_CODE = 'ai.imagePrompt.not_enabled';

/**
 * `AiImagePromptRequestErrors.ConceptNotFound`.
 *
 * One answer for every cause — no such request, another workspace's, a concept that proposal does not hold, a
 * shot it did not plan — because a creator picks from what they were shown, so each is a client naming something
 * it was not shown.
 */
export const IMAGE_PROMPT_CONCEPT_NOT_FOUND_CODE = 'ai.imagePromptConcept.not_found';

/** `AiImagePromptRequestErrors.BriefNotFound`. */
export const IMAGE_PROMPT_BRIEF_NOT_FOUND_CODE = 'ai.imagePromptBrief.not_found';
