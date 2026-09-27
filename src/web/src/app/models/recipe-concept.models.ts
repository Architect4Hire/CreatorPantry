// Wire and view models for AIREC-001 (POST/GET /api/v1/workspaces/{workspaceSlug}/recipe-concept-requests).
// The wire shape is the same AiProposalStatusServiceModel the Phase 8 proposal lifecycle already decodes in
// ai-proposal.models.ts — this file adds nothing to that decoder. It adds the request body this route
// declares (RequestRecipeConceptsViewModel: eleven optional brief fields, nothing else) and a pure,
// presentation-only view that turns the generic Add/Set change rows a concept-generation answer produces back
// into a `RecipeConcept` a form can render as a card.
//
// RecipeConceptsAiTaskHandler.Translate is the server side of this shape: one `Add` row per concept (its
// title, at `proposedPosition`), followed by `Set` rows sharing that row's `targetId` for `summary`,
// `distinctnessRationale`, `assumptions`, `suggestedIngredients`, `dietaryNotes`, `timeBudgetNote`, and
// `skillLevelFit` — list-shaped fields joined with "; ", and a field the model left empty has no `Set` row at
// all. `conceptsFromProposal` below is exactly that translation run backwards.

import { AiProposalDetail, AiProposalWarning, AiProposedChange } from './ai-proposal.models';

// ---------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------

/**
 * The body of `POST .../recipe-concept-requests` — mirrors `RequestRecipeConceptsViewModel`.
 *
 * **What is absent is the contract**, the same way it is for `RequestAiProposalRequest`: no prompt, no model,
 * no provider, no recipe id, and no free-text field beyond these eleven declared ones. This route names its
 * own task server-side; there is nowhere here to name a different one.
 */
export interface RequestRecipeConceptsRequest {
  readonly audience: string | null;
  readonly course: string | null;
  readonly cuisine: string | null;
  readonly dietaryGoals: string | null;
  readonly availableIngredients: string | null;
  readonly exclusions: string | null;
  readonly equipment: string | null;
  readonly skill: string | null;
  readonly season: string | null;
  readonly timeBudget: string | null;
  readonly creatorStyle: string | null;
}

export const EMPTY_RECIPE_CONCEPTS_REQUEST: RequestRecipeConceptsRequest = {
  audience: null,
  course: null,
  cuisine: null,
  dietaryGoals: null,
  availableIngredients: null,
  exclusions: null,
  equipment: null,
  skill: null,
  season: null,
  timeBudget: null,
  creatorStyle: null,
};

/** Every field is optional on the wire, so a blank one is omitted rather than sent as an empty string. */
export function encodeRequestRecipeConceptsRequest(request: RequestRecipeConceptsRequest): Record<string, unknown> {
  const body: Record<string, unknown> = {};

  const set = (key: keyof RequestRecipeConceptsRequest): void => {
    const value = (request[key] ?? '').trim();
    if (value.length > 0) body[key] = value;
  };

  set('audience');
  set('course');
  set('cuisine');
  set('dietaryGoals');
  set('availableIngredients');
  set('exclusions');
  set('equipment');
  set('skill');
  set('season');
  set('timeBudget');
  set('creatorStyle');

  return body;
}

// ---------------------------------------------------------------------------
// Result view
// ---------------------------------------------------------------------------

/** One recipe concept, read back from the generic diff rows it was translated into. Never itself a recipe. */
export interface RecipeConcept {
  /** The server-minted id every one of this concept's change rows shares. Stable across a refresh. */
  readonly targetId: string;
  /** The `Add` row's own id — what a concept-scoped warning's `changeId` names. */
  readonly changeId: string;
  readonly title: string;
  readonly summary: string | null;
  readonly distinctnessRationale: string | null;
  readonly assumptions: readonly string[];
  readonly suggestedIngredients: readonly string[];
  readonly dietaryNotes: readonly string[];
  readonly timeBudgetNote: string | null;
  readonly skillLevelFit: string | null;
  readonly warnings: readonly AiProposalWarning[];
}

/** The server's own separator for a concept's list-shaped fields (RecipeConceptsAiTaskHandler.Join). */
const LIST_FIELD_SEPARATOR = '; ';

function splitList(value: string | null): readonly string[] {
  if (value === null) return [];
  return value
    .split(LIST_FIELD_SEPARATOR)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

/**
 * Turns a proposal's generic change rows back into the concepts they describe.
 *
 * A presentation helper, not a wire decoder: an unrecognised `fieldName` on a `Set` row is skipped rather than
 * failing the whole group, because dropping one field a reader can live without is a better failure than
 * blanking every concept over it. Ordered by `proposedPosition`, the same order the model proposed them in.
 */
export function conceptsFromProposal(detail: AiProposalDetail | null): readonly RecipeConcept[] {
  if (detail === null) return [];

  const addRows = detail.changes
    .filter((change) => change.changeKind === 'Add' && change.targetKind === 'RecipeConcept')
    .sort((a, b) => (a.proposedPosition ?? 0) - (b.proposedPosition ?? 0));

  return addRows.map((addRow) => buildConcept(addRow, detail));
}

function buildConcept(addRow: AiProposedChange, detail: AiProposalDetail): RecipeConcept {
  const setRows = detail.changes.filter(
    (change) =>
      change.changeKind === 'Set' && change.targetKind === 'RecipeConcept' && change.targetId === addRow.targetId,
  );

  const field = (name: string): string | null => setRows.find((row) => row.fieldName === name)?.afterValue ?? null;

  return {
    targetId: addRow.targetId ?? addRow.changeId,
    changeId: addRow.changeId,
    title: addRow.afterValue ?? '',
    summary: field('summary'),
    distinctnessRationale: field('distinctnessRationale'),
    assumptions: splitList(field('assumptions')),
    suggestedIngredients: splitList(field('suggestedIngredients')),
    dietaryNotes: splitList(field('dietaryNotes')),
    timeBudgetNote: field('timeBudgetNote'),
    skillLevelFit: field('skillLevelFit'),
    warnings: detail.warnings.filter((warning) => warning.changeId === addRow.changeId),
  };
}

/** Warnings about the answer as a whole rather than about one concept — `AiConceptOutputWarning` with no index. */
export function generalConceptWarnings(detail: AiProposalDetail | null): readonly AiProposalWarning[] {
  if (detail === null) return [];
  return detail.warnings.filter((warning) => warning.changeId === null);
}
