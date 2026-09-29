// Wire models for AIREC-006
// (POST/GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/review-requests).
//
// A review has its own request route for the scope alone: the generic proposal contract lets the client
// choose a scope, and a review's scope is the server's to fix to Advisory. What comes back is never a diff —
// it is findings a creator judges themselves — so it is read through this route's own GET, not the generic
// ai-proposals one (which deliberately refuses an advisory operation).

/** The body of `POST .../review-requests` — mirrors `RequestRecipeReviewViewModel`. Nothing but the version. */
export interface RequestRecipeReviewRequest {
  /** The exact version to review, which must be the recipe's current version. */
  readonly sourceVersionId: string;
}

export function encodeRequestRecipeReviewRequest(
  request: RequestRecipeReviewRequest,
): Record<string, unknown> {
  return { sourceVersionId: request.sourceVersionId };
}

/** The field names a finding's `Set` rows use — mirrors `AiRecipeReviewFields`. */
export const REVIEW_FIELD_LABELS: Readonly<Record<string, string>> = {
  category: 'Category',
  severity: 'Severity',
  fieldKind: 'About',
  allergen: 'Allergen',
  allergenEffect: 'Allergen effect',
  diet: 'Diet',
  dietaryEffect: 'Dietary effect',
  evidenceBasis: 'Basis',
  evidenceNote: 'Evidence',
  confidence: 'Confidence',
  requiresReferenceCheck: 'Needs checking against a reference',
  unknownFactors: 'What is unknown',
};

/**
 * Fields whose value is a raw enum member name on the wire (`"AllergenConflict"`), not a phrase the model or
 * the server composed — every other field's value is already words and must not be re-split and re-cased.
 */
export const REVIEW_ENUM_VALUE_FIELDS: ReadonlySet<string> = new Set([
  'category',
  'severity',
  'fieldKind',
  'allergenEffect',
  'dietaryEffect',
  'evidenceBasis',
  'confidence',
]);

/**
 * A raw internal id with no creator-facing meaning here — this app has no lookup from it back to the
 * ingredient line or step it names. Shown nowhere; `fieldKind` alone ("Ingredient line", "Step") already says
 * what kind of thing a finding is about.
 */
export const REVIEW_HIDDEN_FIELDS: ReadonlySet<string> = new Set(['fieldEntityId']);
