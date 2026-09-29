// Wire models for AIREC-004
// (POST/GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/substitution-requests).
//
// A substitution has its own request route because it names an ingredient, which the generic proposal
// contract has nowhere to put. What comes back is never a diff — it is advice a creator reads and acts on
// themselves — so it is read through this route's own GET, not the generic ai-proposals one (which
// deliberately refuses an advisory operation).

/** The body of `POST .../substitution-requests` — mirrors `RequestIngredientSubstitutionViewModel`. */
export interface RequestIngredientSubstitutionRequest {
  /** The exact version the ingredient was selected in, which must be the recipe's current version. */
  readonly sourceVersionId: string;
  /** The ingredient line to find alternatives for, by its id in that version. */
  readonly ingredientId: string;
  /** Why the creator is asking, in their own words. Optional. */
  readonly reason: string;
}

export function encodeRequestIngredientSubstitutionRequest(
  request: RequestIngredientSubstitutionRequest,
): Record<string, unknown> {
  const reason = request.reason.trim();

  return {
    sourceVersionId: request.sourceVersionId,
    ingredientId: request.ingredientId,
    // Omitted rather than sent blank, for the reason a revision's goal is: an absent reason and an empty one
    // mean the same "not specified" to the server.
    ...(reason.length > 0 ? { reason } : {}),
  };
}

/** Mirrors `AiPolicy`'s bound on a substitution's free-text reason. A client-side hint, not the check. */
export const SUBSTITUTION_REASON_MAX_LENGTH = 500;

/** The field names an alternative's `Set` rows use — mirrors `AiSubstitutionFields`. */
export const SUBSTITUTION_FIELD_LABELS: Readonly<Record<string, string>> = {
  functionalRole: 'What it does in this recipe',
  quantityGuidance: 'How much to use',
  techniqueImpact: 'Effect on technique',
  flavorImpact: 'Effect on flavor',
  textureImpact: 'Effect on texture',
  dietaryEffects: 'Dietary effects',
  allergenEffects: 'Allergen effects',
  confidence: 'Confidence',
  evidenceBasis: 'Basis',
  evidenceNote: 'Evidence',
  testRecommendation: 'Test before trusting it',
};

/**
 * Fields whose value is a raw enum member name on the wire (`"ModelKnowledge"`), not a phrase the model or the
 * server composed — every other field's value is already words and must not be re-split and re-cased.
 */
export const SUBSTITUTION_ENUM_VALUE_FIELDS: ReadonlySet<string> = new Set(['confidence', 'evidenceBasis']);
