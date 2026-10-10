// A dish name read into a cuisine, a dish type and a method, as `POST/GET .../dish-facet-requests` sends it.
// Mirrors CreatorPantry.Domain/Modules/Ai/Managers/AiDishFacetsRequestModels.cs, AiDishFacet.cs,
// AiDishFacetFields.cs and DishFacetSuggestionAiTaskHandler.cs.
//
// A reading is a **suggestion**: catalogue codes a creator's own controls are filled from and which they can
// change. The request carries the name and nothing else — no candidate list, and none of the creator's own
// answers — so every reading covers all three facets and keeping one off a control they have answered is the
// caller's job (`RequestDishFacetsViewModel`).

import { AiProposalDetail, AiProposalWarning } from './ai-proposal.models';

/** Mirrors AiDishFacet, less `Unspecified`, under the names the pipeline's `keep` map already uses. */
export type DishFacetName = 'cuisine' | 'dishType' | 'method';

export const DISH_FACET_NAMES: readonly DishFacetName[] = ['cuisine', 'dishType', 'method'];

/** The enum member each facet is field-named by on the wire: `facet.{Member}`. */
const WIRE_NAMES: Readonly<Record<DishFacetName, string>> = {
  cuisine: 'Cuisine',
  dishType: 'DishType',
  method: 'Method',
};

/** Mirrors AiDishFacetConfidence, less `Unspecified`. */
export type DishFacetConfidence = 'Likely' | 'Possible';

/** One facet of one reading. */
export interface DishFacetReading {
  /** The catalogue code the name was read as, or null where the reading declined. */
  readonly code: string | null;
  /** Null exactly when {@link code} is, and for a confidence this client has not heard of. */
  readonly confidence: DishFacetConfidence | null;
  /** Which words of the name led here, or why nothing did. */
  readonly rationale: string | null;
}

export interface DishFacetsReading {
  /** A facet the answer said nothing about is absent rather than a decline with no reason. */
  readonly facets: { readonly [K in DishFacetName]?: DishFacetReading };
  readonly warnings: readonly AiProposalWarning[];
}

/**
 * The reading in one proposal, read back out of its flat rows.
 *
 * One `Add` row carries the name that was read; `Set` rows under the same target carry `facet.{Facet}`,
 * `facet.{Facet}.confidence` and `facet.{Facet}.rationale`, each present only where the answer supplied it.
 */
export function decodeDishFacetsReading(detail: AiProposalDetail | null): DishFacetsReading | null {
  if (detail === null) return null;

  const rows = detail.changes.filter(
    (change) => change.changeKind === 'Set' && change.targetKind === 'DishFacetSuggestion',
  );
  const field = (name: string): string | null => rows.find((row) => row.fieldName === name)?.afterValue ?? null;

  const facets: { -readonly [K in DishFacetName]?: DishFacetReading } = {};
  for (const name of DISH_FACET_NAMES) {
    const wire = `facet.${WIRE_NAMES[name]}`;
    const code = field(wire);
    const confidence = field(`${wire}.confidence`);
    const rationale = field(`${wire}.rationale`);
    if (code === null && rationale === null) continue;

    facets[name] = {
      code,
      confidence: code !== null && (confidence === 'Likely' || confidence === 'Possible') ? confidence : null,
      rationale,
    };
  }

  return { facets, warnings: detail.warnings };
}

/** `AiPolicy.DishFacetNameMaxLength`. */
export const DISH_FACET_NAME_MAX_LENGTH = 200;

/** The request body: the name, and nothing else. */
export function encodeRequestDishFacets(dishName: string): Record<string, unknown> {
  return { dishName: dishName.trim() };
}

/** `AiDishFacetsRequestErrors.TaskNotEnabled`. */
export const DISH_FACETS_NOT_ENABLED_CODE = 'ai.dishFacets.not_enabled';
