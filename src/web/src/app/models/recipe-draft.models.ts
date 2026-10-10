// View models for AIREC-002 (GET /api/v1/workspaces/{workspaceSlug}/recipe-draft-requests/{id}).
//
// A first draft is stored the way every other AI answer is: as generic `AiStructuredChange` rows. Nothing
// server-side ever holds it as a recipe, because it is not one yet — turning an accepted draft into a
// `Recipe` is a separate, explicit step with its own transaction. So this file is a presentation-only view
// that turns those rows back into the draft they describe.
//
// `RecipeFirstDraftAiTaskHandler.Translate` is the server side of this shape, and its own remarks are the
// specification for what follows: recipe-level `Set` rows with no target id; one `Add` row per group, line,
// step and equipment item carrying that row's own server-minted id; `Set` rows addressed to it for the rest
// of its fields. `draftFromProposal` below is exactly that translation run backwards.

import { AiProposalDetail, AiProposalWarning, AiProposedChange } from './ai-proposal.models';

// ---------------------------------------------------------------------------
// Draft view
// ---------------------------------------------------------------------------

/** One ingredient line, as proposed. Never a `RecipeIngredient` — nothing here has been saved. */
export interface RecipeDraftIngredient {
  /** The `Add` row's own change id: what an edit names, and what makes this row addressable. */
  readonly changeId: string;
  /** The server-minted id this line's `Set` rows share. */
  readonly targetId: string | null;
  /** The line exactly as proposed: "2 tbsp doubanjiang". */
  readonly displayText: string;
  readonly ingredientNameText: string | null;
  readonly unitText: string | null;
  readonly quantity: string | null;
  readonly quantityUpper: string | null;
  readonly preparationNote: string | null;
  readonly isOptional: boolean;
}

/** One instruction step, as proposed. */
export interface RecipeDraftStep {
  readonly changeId: string;
  readonly targetId: string | null;
  readonly text: string;
  readonly note: string | null;
  readonly durationMinutes: string | null;
}

/** One piece of equipment, as proposed. */
export interface RecipeDraftEquipment {
  readonly changeId: string;
  readonly targetId: string | null;
  readonly displayText: string;
  readonly note: string | null;
  readonly isOptional: boolean;
}

/**
 * A group of ingredient lines or instruction steps.
 *
 * `title` is nullable and that is not an oversight: recipes.md makes grouping opt-in, and a recipe has one
 * ingredient list and one method until the creator asks for sections. An untitled group is the ordinary case
 * and must render without a heading rather than with an invented one.
 */
export interface RecipeDraftGroup<TItem> {
  readonly changeId: string;
  readonly targetId: string | null;
  readonly title: string | null;
  readonly isOptional: boolean;
  readonly items: readonly TItem[];
}

/** What the model proposed about how much the recipe makes. Three separable facts (recipes.md). */
export interface RecipeDraftYield {
  /** The creator-facing wording: "Makes 12 muffins". */
  readonly yieldText: string | null;
  readonly yieldQuantity: string | null;
  /** Free text — "loaves", "cups" — never a measurement unit id. A model may not name one. */
  readonly yieldUnitText: string | null;
  readonly servingCount: string | null;
  readonly servingSize: string | null;
}

export interface RecipeDraftTiming {
  readonly prepTimeMinutes: string | null;
  readonly cookTimeMinutes: string | null;
  readonly restTimeMinutes: string | null;
  /**
   * Stored independently of the other three and never summed from them (recipes.md). Shown as proposed,
   * even where it disagrees with prep + cook + rest — reconciling that is the recipe editor's job at
   * acceptance, and silently correcting it here would be this view inventing a number.
   */
  readonly totalTimeMinutes: string | null;
}

/** One complete proposed first draft. A proposal to read, never a recipe. */
export interface RecipeDraft {
  readonly title: string | null;
  readonly description: string | null;
  readonly notes: string | null;
  readonly yield: RecipeDraftYield;
  readonly timing: RecipeDraftTiming;
  readonly ingredientGroups: readonly RecipeDraftGroup<RecipeDraftIngredient>[];
  readonly instructionGroups: readonly RecipeDraftGroup<RecipeDraftStep>[];
  readonly equipment: readonly RecipeDraftEquipment[];
  /** What the model declined to guess at. Kept apart from {@link warnings} — see `unresolvedQuestionsOf`. */
  readonly unresolvedQuestions: readonly AiProposalWarning[];
  /** Everything else the creator should be told: assumptions, cautions, unverified claims. */
  readonly warnings: readonly AiProposalWarning[];
  /** The `Set` rows this view could not place, so a field added server-side is visible before it is styled. */
  readonly unrecognisedFields: readonly AiProposedChange[];
}

/** A recipe-level field name this view knows how to place. */
const RECIPE_FIELDS = new Set([
  'title',
  'description',
  'notes',
  'prepTimeMinutes',
  'cookTimeMinutes',
  'restTimeMinutes',
  'totalTimeMinutes',
  'yieldText',
  'yieldQuantity',
  'yieldUnitText',
  'servingCount',
  'servingSize',
]);

const EMPTY_DRAFT: RecipeDraft = {
  title: null,
  description: null,
  notes: null,
  yield: { yieldText: null, yieldQuantity: null, yieldUnitText: null, servingCount: null, servingSize: null },
  timing: { prepTimeMinutes: null, cookTimeMinutes: null, restTimeMinutes: null, totalTimeMinutes: null },
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  unresolvedQuestions: [],
  warnings: [],
  unrecognisedFields: [],
};

/**
 * Turns a proposal's change rows back into the draft they describe.
 *
 * **Group membership is positional, and the array's own order is what carries it.** The handler mints a fresh
 * id for every row because nothing exists yet on either end of a group/line relationship, so an ingredient
 * line's `Add` row does not name its group — it simply follows it. The server orders the published changes by
 * `SortOrder` before sending them (`AiOperationDescription`), and `SortOrder` is the order the handler emitted
 * them in, so "the most recent group `Add` before this line" is exactly "the most recent group `Add` earlier
 * in this array". That is why this walks the list once, forward, and never sorts or groups by `targetId`.
 *
 * A line arriving before any group has opened is put in an untitled group of its own rather than dropped. It
 * should not happen; losing a creator's proposed ingredient because the server's ordering surprised us would
 * be worse than showing it ungrouped.
 */
export function draftFromProposal(detail: AiProposalDetail | null): RecipeDraft {
  if (detail === null) return EMPTY_DRAFT;

  const recipeFields = new Map<string, string>();
  const ingredientGroups: MutableGroup<MutableIngredient>[] = [];
  const instructionGroups: MutableGroup<MutableStep>[] = [];
  const equipment: MutableEquipment[] = [];
  const unrecognisedFields: AiProposedChange[] = [];

  // What a `Set` row addressed to a target id belongs to, populated as each `Add` row opens one.
  const targets = new Map<string, MutableIngredient | MutableStep | MutableEquipment | MutableGroup<never>>();

  for (const change of detail.changes) {
    if (change.changeKind === 'Add') {
      switch (change.targetKind) {
        case 'IngredientGroup': {
          const group = openGroup<MutableIngredient>(change);
          ingredientGroups.push(group);
          if (change.targetId !== null) targets.set(change.targetId, group as MutableGroup<never>);
          break;
        }
        case 'InstructionGroup': {
          const group = openGroup<MutableStep>(change);
          instructionGroups.push(group);
          if (change.targetId !== null) targets.set(change.targetId, group as MutableGroup<never>);
          break;
        }
        case 'Ingredient': {
          const line: MutableIngredient = {
            changeId: change.changeId,
            targetId: change.targetId,
            displayText: change.afterValue ?? '',
            ingredientNameText: null,
            unitText: null,
            quantity: null,
            quantityUpper: null,
            preparationNote: null,
            isOptional: false,
          };
          currentGroup(ingredientGroups).items.push(line);
          if (change.targetId !== null) targets.set(change.targetId, line);
          break;
        }
        case 'InstructionStep': {
          const step: MutableStep = {
            changeId: change.changeId,
            targetId: change.targetId,
            text: change.afterValue ?? '',
            note: null,
            durationMinutes: null,
          };
          currentGroup(instructionGroups).items.push(step);
          if (change.targetId !== null) targets.set(change.targetId, step);
          break;
        }
        case 'Equipment': {
          const item: MutableEquipment = {
            changeId: change.changeId,
            targetId: change.targetId,
            displayText: change.afterValue ?? '',
            note: null,
            isOptional: false,
          };
          equipment.push(item);
          if (change.targetId !== null) targets.set(change.targetId, item);
          break;
        }
        default:
          unrecognisedFields.push(change);
      }
      continue;
    }

    if (change.changeKind !== 'Set' || change.fieldName === null) {
      unrecognisedFields.push(change);
      continue;
    }

    // Recipe-level: no target id, by the handler's own convention.
    if (change.targetKind === 'Recipe' && change.targetId === null) {
      if (RECIPE_FIELDS.has(change.fieldName)) {
        recipeFields.set(change.fieldName, change.afterValue ?? '');
      } else {
        unrecognisedFields.push(change);
      }
      continue;
    }

    const target = change.targetId === null ? undefined : targets.get(change.targetId);
    if (target === undefined || !applyField(target, change.fieldName, change)) unrecognisedFields.push(change);
  }

  return {
    title: recipeFields.get('title') ?? null,
    description: recipeFields.get('description') ?? null,
    notes: recipeFields.get('notes') ?? null,
    yield: {
      yieldText: recipeFields.get('yieldText') ?? null,
      yieldQuantity: recipeFields.get('yieldQuantity') ?? null,
      yieldUnitText: recipeFields.get('yieldUnitText') ?? null,
      servingCount: recipeFields.get('servingCount') ?? null,
      servingSize: recipeFields.get('servingSize') ?? null,
    },
    timing: {
      prepTimeMinutes: recipeFields.get('prepTimeMinutes') ?? null,
      cookTimeMinutes: recipeFields.get('cookTimeMinutes') ?? null,
      restTimeMinutes: recipeFields.get('restTimeMinutes') ?? null,
      totalTimeMinutes: recipeFields.get('totalTimeMinutes') ?? null,
    },
    ingredientGroups,
    instructionGroups,
    equipment,
    unresolvedQuestions: unresolvedQuestionsOf(detail),
    warnings: otherWarningsOf(detail),
    unrecognisedFields,
  };
}

/**
 * The questions the model declined to guess at.
 *
 * Separated from the rest so a review surface can lead with them. `AiWarningKind.UnresolvedQuestion` is the
 * server's own distinction: an assumption is what the model took as given, this is what it would not invent —
 * and AIREC-002's RESTRICTION is that those stay visible rather than being smoothed into a warning list.
 */
export function unresolvedQuestionsOf(detail: AiProposalDetail | null): readonly AiProposalWarning[] {
  if (detail === null) return [];
  return detail.warnings.filter((warning) => warning.kind === 'UnresolvedQuestion');
}

/** Every other flag: assumptions, cautions, unverified claims. */
export function otherWarningsOf(detail: AiProposalDetail | null): readonly AiProposalWarning[] {
  if (detail === null) return [];
  return detail.warnings.filter((warning) => warning.kind !== 'UnresolvedQuestion');
}

/**
 * Whether the draft is missing anything a recipe cannot be created without.
 *
 * A title and at least one ingredient line and one step. Reported rather than repaired: 9.4b's acceptance step
 * is what refuses an incomplete draft, and this exists so a creator sees the gap while reviewing instead of
 * meeting it as a failure at the end.
 */
export function missingRequiredFieldsOf(draft: RecipeDraft): readonly string[] {
  const missing: string[] = [];

  if (draft.title === null || draft.title.trim().length === 0) missing.push('Title');
  if (draft.ingredientGroups.every((group) => group.items.length === 0)) missing.push('Ingredients');
  if (draft.instructionGroups.every((group) => group.items.length === 0)) missing.push('Method');

  return missing;
}

/**
 * Wordings that stand in for a number, and so are an amount: the prompt that writes these drafts allows
 * them by name — "a pinch of salt", "to taste", a package size — and a line carrying one is complete.
 *
 * Short on purpose, and the asymmetry is deliberate. A wording missing from this list costs a creator a
 * second look at a line that was already fine; a line with no measure at all, passed over quietly, costs
 * them a recipe they cannot cook from. Grown only by adding a wording a draft actually used.
 */
const MEASURES_WITHOUT_A_NUMBER: readonly string[] = [
  'to taste',
  'as needed',
  'as required',
  'pinch',
  'dash',
  'splash',
  'handful',
  'drizzle',
  'sprinkle',
  'a few',
  'for serving',
  'to serve',
  'for garnish',
  'to garnish',
  'for dusting',
  'for greasing',
  'for frying',
  'to coat',
  'to cover',
];

/**
 * Whether a drafted ingredient line says how much — by a number, or by a measure that is not one.
 *
 * Deliberately generous about what counts: this decides whether to put a line in front of a creator as
 * worth checking, and a false flag is a glance while a false pass is a line nobody can cook from. Any
 * digit at all is enough, vulgar fractions included, because a line reading "1½ cups" has plainly stated
 * its amount whether or not the separate quantity field was filled in.
 */
export function ingredientLineStatesAmount(displayText: string): boolean {
  const text = displayText.toLowerCase();

  if (/[\d\u00bc-\u00be\u2150-\u215e]/.test(text)) return true;

  return MEASURES_WITHOUT_A_NUMBER.some((measure) => text.includes(measure));
}

/** How many lines, steps and items a draft holds — for an at-a-glance count beside a section heading. */
export function countItems<TItem>(groups: readonly RecipeDraftGroup<TItem>[]): number {
  return groups.reduce((total, group) => total + group.items.length, 0);
}

// ---------------------------------------------------------------------------
// Assembly internals
// ---------------------------------------------------------------------------

type MutableIngredient = { -readonly [K in keyof RecipeDraftIngredient]: RecipeDraftIngredient[K] };
type MutableStep = { -readonly [K in keyof RecipeDraftStep]: RecipeDraftStep[K] };
type MutableEquipment = { -readonly [K in keyof RecipeDraftEquipment]: RecipeDraftEquipment[K] };

interface MutableGroup<TItem> {
  changeId: string;
  targetId: string | null;
  title: string | null;
  isOptional: boolean;
  items: TItem[];
}

function openGroup<TItem>(change: AiProposedChange): MutableGroup<TItem> {
  return {
    changeId: change.changeId,
    targetId: change.targetId,
    // Blank is the same as absent here: an untitled group travels with no heading, and rendering "" as a
    // heading would put an empty line where the creator asked for nothing.
    title: change.afterValue !== null && change.afterValue.length > 0 ? change.afterValue : null,
    isOptional: false,
    items: [],
  };
}

/** The group a line belongs to: the most recent one opened, or an untitled one made for it. */
function currentGroup<TItem>(groups: MutableGroup<TItem>[]): MutableGroup<TItem> {
  const last = groups[groups.length - 1];
  if (last !== undefined) return last;

  const orphanage: MutableGroup<TItem> = {
    changeId: '',
    targetId: null,
    title: null,
    isOptional: false,
    items: [],
  };
  groups.push(orphanage);
  return orphanage;
}

/**
 * Places one `Set` row onto the row it addresses. False when this view does not know the field.
 *
 * Takes the field name separately because the caller has already established it is not null, and TypeScript
 * does not carry that narrowing across a call.
 */
function applyField(
  target: MutableIngredient | MutableStep | MutableEquipment | MutableGroup<never>,
  fieldName: string,
  change: AiProposedChange,
): boolean {
  const value = change.afterValue;

  if (fieldName === 'isOptional') {
    // The handler emits this row only when the flag is true, so its presence is the flag. Reading the value
    // anyway means a future "false" is honoured rather than inverted.
    (target as { isOptional: boolean }).isOptional = value !== 'false';
    return true;
  }

  // An allow-list per kind rather than a property-exists test. Every row carries `changeId` and `targetId`,
  // and a group carries `title`, so `fieldName in target` would let a server-sent field name overwrite this
  // view's own identity fields. An ingredient line and an equipment item both have `displayText` and only one
  // has a `note`, so the allow-list is also what keeps those two apart.
  const settable = 'text' in target ? STEP_FIELDS : 'displayText' in target ? lineFieldsFor(target) : GROUP_FIELDS;

  if (!settable.has(fieldName)) return false;

  (target as unknown as Record<string, string | null>)[fieldName] = value;
  return true;
}

const STEP_FIELDS = new Set(['note', 'durationMinutes']);
const INGREDIENT_FIELDS = new Set([
  'ingredientNameText',
  'unitText',
  'quantity',
  'quantityUpper',
  'preparationNote',
]);
const EQUIPMENT_FIELDS = new Set(['note']);

/** A group has no settable field of its own beyond `isOptional`, which is handled before this. */
const GROUP_FIELDS: ReadonlySet<string> = new Set<string>();

function lineFieldsFor(target: MutableIngredient | MutableEquipment): ReadonlySet<string> {
  return 'ingredientNameText' in target ? INGREDIENT_FIELDS : EQUIPMENT_FIELDS;
}
