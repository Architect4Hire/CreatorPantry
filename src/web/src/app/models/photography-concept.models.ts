// IMG-001: photography concepts for a channel, as `POST/GET .../photography-concept-requests` sends them.
// Mirrors CreatorPantry.Domain/Modules/Ai/Managers/AiPhotographyConceptRequestModels.cs,
// AiPhotographyConceptOutputDocument.cs, AiPhotographyShotKind.cs and PhotographyConceptAiTaskHandler.cs.
//
// A concept is **planning material**: nothing here can be applied to a recipe, and there is no disposition
// route — the creator looks at one to three concepts and picks one. Every field is descriptive; a concept has
// nowhere to put a quantity, a time or a temperature, which is why none appears below.

import {
  AiProposalDetail,
  AiProposalWarning,
  AiProposedChange,
} from './ai-proposal.models';
import { decodeEnum } from './recipe.models';

/** Mirrors AiPhotographyShotKind. What one planned frame of a concept is for. */
export type PhotographyShotKind = 'Hero' | 'ProcessStep' | 'IngredientLayout' | 'StyledScene' | 'DetailShot';

export const PHOTOGRAPHY_SHOT_KINDS: readonly PhotographyShotKind[] = [
  'Hero',
  'ProcessStep',
  'IngredientLayout',
  'StyledScene',
  'DetailShot',
];

export const PHOTOGRAPHY_SHOT_KIND_VALUES: ReadonlySet<string> = new Set<PhotographyShotKind>(
  PHOTOGRAPHY_SHOT_KINDS,
);

/** How each shot role reads to a creator. Spelled out, because "IngredientLayout" names nothing to anyone. */
export const PHOTOGRAPHY_SHOT_KIND_LABELS: Readonly<Record<PhotographyShotKind, string>> = {
  Hero: 'The main shot',
  ProcessStep: 'A step in the method',
  IngredientLayout: 'The ingredients laid out',
  StyledScene: 'A styled scene',
  DetailShot: 'A close detail',
};

/** One frame to shoot. */
export interface PhotographyShot {
  readonly kind: PhotographyShotKind;
  readonly framing: string | null;
  readonly lighting: string | null;
  readonly surface: string | null;
  /**
   * How the food is arranged for the camera. Presentation only — it may not introduce an ingredient the recipe
   * does not have, nor assert a doneness or texture as fact (.claude/rules/recipes.md).
   */
  readonly styling: string | null;
  readonly props: readonly string[];
}

/** One photography concept: a look, and the short list of shots that realise it. */
export interface PhotographyConcept {
  /** The server-minted id this concept's rows share. What IMG-002 is given to compose from. */
  readonly conceptId: string;
  readonly changeId: string;
  readonly label: string;
  readonly mood: string | null;
  readonly palette: string | null;
  readonly rationale: string | null;
  /**
   * How the set serves the channel it was planned for. Null when no channel was named, and never an invented
   * reach or engagement figure.
   */
  readonly channelFit: string | null;
  readonly shots: readonly PhotographyShot[];
  /** Warnings about this concept in particular. Those about the answer as a whole stay separate. */
  readonly warnings: readonly AiProposalWarning[];
}

/**
 * How a list field arrives in a proposal row.
 *
 * The handler joins with `"; "`, and this splits on the semicolon alone and trims — so a row joined without the
 * space still reads correctly rather than arriving as one long item.
 */
const LIST_SEPARATOR = ';';

function splitList(value: string | null): readonly string[] {
  if (value === null) return [];

  return value
    .split(LIST_SEPARATOR)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

/**
 * The concepts in one proposal, in the order the model offered them.
 *
 * Read back out of flat rows rather than a nested document, because that is how every proposal is stored: one
 * `Add` row per concept carrying its label under a server-minted `targetId`, then `Set` rows for the rest of the
 * look and for each shot, field-named `shot.{Kind}.{property}`.
 */
export function decodePhotographyConcepts(detail: AiProposalDetail | null): readonly PhotographyConcept[] {
  if (detail === null) return [];

  return detail.changes
    .filter((change) => change.changeKind === 'Add' && change.targetKind === 'PhotographyConcept')
    .sort((left, right) => (left.proposedPosition ?? 0) - (right.proposedPosition ?? 0))
    .map((addRow) => buildConcept(addRow, detail));
}

function buildConcept(addRow: AiProposedChange, detail: AiProposalDetail): PhotographyConcept {
  const setRows = detail.changes.filter(
    (change) =>
      change.changeKind === 'Set' &&
      change.targetKind === 'PhotographyConcept' &&
      change.targetId === addRow.targetId,
  );

  const field = (name: string): string | null => setRows.find((row) => row.fieldName === name)?.afterValue ?? null;

  // A shot exists in the answer if any of its properties came back. Built from what is there rather than from
  // the five possible roles, so a concept that planned two shots shows two.
  const shots = PHOTOGRAPHY_SHOT_KINDS.map((kind) => {
    const property = (name: string): string | null => field(`shot.${kind}.${name}`);
    const framing = property('framing');
    const lighting = property('lighting');
    const surface = property('surface');
    const styling = property('styling');
    const props = splitList(property('props'));

    const planned =
      framing !== null || lighting !== null || surface !== null || styling !== null || props.length > 0;

    return planned ? { kind, framing, lighting, surface, styling, props } : null;
  }).filter((shot): shot is PhotographyShot => shot !== null);

  return {
    // `targetId` is what IMG-002 is given, so falling back to the change id would name something that route
    // cannot resolve. A row without one is a shape this client does not expect, and an empty id is refused
    // there rather than guessed at here.
    conceptId: addRow.targetId ?? '',
    changeId: addRow.changeId,
    label: addRow.afterValue ?? '',
    mood: field('mood'),
    palette: field('palette'),
    rationale: field('rationale'),
    channelFit: field('channelFit'),
    shots,
    warnings: detail.warnings.filter((warning) => warning.changeId === addRow.changeId),
  };
}

/** Warnings about the answer as a whole rather than about one concept. */
export function generalPhotographyWarnings(detail: AiProposalDetail | null): readonly AiProposalWarning[] {
  return detail === null ? [] : detail.warnings.filter((warning) => warning.changeId === null);
}

/** What a client sends to ask for concepts. Every field optional, which is this capability's shape. */
export interface RequestPhotographyConceptsRequest {
  readonly channelKey: string | null;
  readonly creatorConcept: string;
  readonly sceneOverrides: readonly string[];
  readonly styleOverrides: readonly string[];
}

/**
 * The request body, with everything blank left out.
 *
 * Absent and empty mean the same thing to the server (`PhotographyConceptInputs` drops blanks on the way in), so
 * sending an empty list would only make a request that says it asked for something it did not.
 */
export function encodeRequestPhotographyConcepts(
  request: RequestPhotographyConceptsRequest,
): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  if (request.channelKey) body['channelKey'] = request.channelKey;
  if (request.creatorConcept.trim()) body['creatorConcept'] = request.creatorConcept.trim();
  if (request.sceneOverrides.length > 0) body['sceneOverrides'] = [...request.sceneOverrides];
  if (request.styleOverrides.length > 0) body['styleOverrides'] = [...request.styleOverrides];

  return body;
}

/** `AiPhotographyConceptRequestErrors.TaskNotEnabled`. */
export const PHOTOGRAPHY_CONCEPT_NOT_ENABLED_CODE = 'ai.photographyConcept.not_enabled';

/** `AiPhotographyConceptRequestErrors.RecipeNotFound`. */
export const PHOTOGRAPHY_CONCEPT_RECIPE_NOT_FOUND_CODE = 'ai.photographyConceptRecipe.not_found';

export function decodePhotographyShotKind(value: unknown): PhotographyShotKind | null {
  return decodeEnum<PhotographyShotKind>(PHOTOGRAPHY_SHOT_KIND_VALUES, value);
}
