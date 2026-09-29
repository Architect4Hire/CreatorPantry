// Wire and view models for AIREC-005
// (POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/adaptation-requests).
//
// An adaptation has its own request route because it carries a goal, which the generic proposal contract has
// nowhere to put — the same reason a revision does. What comes back is an ordinary proposal against the
// pinned version, so it is read and decided through the existing ai-proposals routes — there is no
// adaptation-specific review model here.

/** Mirrors `AiAdaptationGoal`. The one goal an adaptation may declare. */
export type AiAdaptationGoal = 'Dietary' | 'Equipment' | 'Yield' | 'SkillLevel';

/** The body of `POST .../adaptation-requests` — mirrors `RequestRecipeAdaptationViewModel`. */
export interface RequestRecipeAdaptationRequest {
  /** The exact version to adapt, which must be the recipe's current version. */
  readonly sourceVersionId: string;
  readonly goal: AiAdaptationGoal;
  /** What the goal means for this recipe. Required for every goal but `Yield`. */
  readonly goalDetail: string;
  /** Meaningful only when `goal` is `Yield`, and exactly one of this or `targetYieldQuantity` is named then. */
  readonly targetMultiplier: number | null;
  readonly targetYieldQuantity: number | null;
}

export function encodeRequestRecipeAdaptationRequest(
  request: RequestRecipeAdaptationRequest,
): Record<string, unknown> {
  const goalDetail = request.goalDetail.trim();

  return {
    sourceVersionId: request.sourceVersionId,
    goal: request.goal,
    // Omitted rather than sent blank, for the reason a revision's goal is: an absent detail and an empty one
    // mean the same "not specified" to the server.
    ...(goalDetail.length > 0 ? { goalDetail } : {}),
    ...(request.targetMultiplier !== null ? { targetMultiplier: request.targetMultiplier } : {}),
    ...(request.targetYieldQuantity !== null ? { targetYieldQuantity: request.targetYieldQuantity } : {}),
  };
}

/** One goal a creator can adapt a recipe toward. */
export interface RecipeAdaptationGoalOption {
  readonly goal: AiAdaptationGoal;
  readonly label: string;
  readonly detail: string;
  /** Whether this goal takes free-text detail rather than (or alongside) the yield target fields. */
  readonly takesDetail: boolean;
}

/** Mirrors the four `AiAdaptationGoal` members in declaration order. */
export const RECIPE_ADAPTATION_GOALS: readonly RecipeAdaptationGoalOption[] = [
  {
    goal: 'Dietary',
    label: 'A diet or restriction',
    detail: 'Say which one — "gluten-free", "vegan", "low sodium".',
    takesDetail: true,
  },
  {
    goal: 'Equipment',
    label: 'Equipment you have, or do not have',
    detail: 'Say which — "no stand mixer", "only a slow cooker".',
    takesDetail: true,
  },
  {
    goal: 'Yield',
    label: 'A different batch size',
    detail: 'Scale by a factor or toward a target yield. Ingredient quantities are computed, never guessed.',
    takesDetail: false,
  },
  {
    goal: 'SkillLevel',
    label: "The cook's skill level",
    detail: 'Say what to adapt for — "a nervous first-timer", "an experienced baker".',
    takesDetail: true,
  },
];

/** Mirrors `AiPolicy.AdaptationGoalDetailMaxLength`. A client-side hint, not the check. */
export const ADAPTATION_GOAL_DETAIL_MAX_LENGTH = 500;
