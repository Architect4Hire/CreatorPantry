// Wire and view models for AIREC-003
// (POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/revision-requests).
//
// A revision has its own request route because it carries a goal, which the generic proposal contract has
// nowhere to put. What comes back is an ordinary proposal against the pinned version, so it is read and
// decided through the existing ai-proposals routes — there is no revision-specific review model here.

import { AiOperationScope } from './ai-proposal.models';

/** The body of `POST .../revision-requests` — mirrors `RequestRecipeRevisionViewModel`. */
export interface RequestRecipeRevisionRequest {
  /** The one section the revision may change. */
  readonly scope: AiOperationScope;
  /** The exact version to revise, which must be the recipe's current version. */
  readonly sourceVersionId: string;
  /** What the creator wants out of it, in their own words. Optional. */
  readonly goal: string;
}

export function encodeRequestRecipeRevisionRequest(
  request: RequestRecipeRevisionRequest,
): Record<string, unknown> {
  const goal = request.goal.trim();

  return {
    scope: request.scope,
    sourceVersionId: request.sourceVersionId,
    // Omitted rather than sent blank: an absent goal and an empty one mean the same "not specified" to the
    // server, and sending one of them as the other is how two spellings of one request come about.
    ...(goal.length > 0 ? { goal } : {}),
  };
}

/** One section a creator can scope a revision to. */
export interface RecipeRevisionSection {
  readonly scope: AiOperationScope;
  readonly label: string;
  /** What that section covers, so the choice does not rest on the reader knowing the vocabulary. */
  readonly detail: string;
}

/**
 * The sections a revision can be asked for.
 *
 * **Mirrors `AiRevisionScopes.Revisable`, which is derived server-side from the applicability table**, so a
 * section becomes revisable there the moment the recipe seam can apply something in it. This list cannot
 * derive itself and is therefore the one that can fall behind — but it fails safe in both directions: a
 * section missing here is merely not offered, and one that stopped being revisable is refused by the
 * request validator with a message naming the sections that work.
 *
 * `Media` is absent because no change to an asset link can be applied, and the server will not accept it.
 */
export const RECIPE_REVISION_SECTIONS: readonly RecipeRevisionSection[] = [
  {
    scope: 'Metadata',
    label: 'Title and framing',
    detail: 'The title, description, headnote, times and yield — not the method or the ingredients.',
  },
  {
    scope: 'Ingredients',
    label: 'Ingredients',
    detail: 'The ingredient lines and their groups.',
  },
  {
    scope: 'Instructions',
    label: 'Method',
    detail: 'The steps and their groups.',
  },
  {
    scope: 'WholeRecipe',
    label: 'The whole recipe',
    detail: 'Anything above. The widest choice, and the one that needs the closest review.',
  },
];

/** Mirrors `AiPolicy.RevisionGoalMaxLength`. A client-side hint, not the check. */
export const REVISION_GOAL_MAX_LENGTH = 500;
