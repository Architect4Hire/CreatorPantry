import { RecipeDraft, RecipeDraftGroup, RecipeDraftIngredient } from '../../models/recipe-draft.models';

/**
 * One line of the recipe sheet: an ingredient, or a step of the method.
 *
 * A view model of what is shown, not a recipe. It carries no id of a record and nothing to edit, so any page
 * that reads a recipe-shaped thing — a first draft today — can give the sheet the same shape.
 */
export interface RecipeSheetLine {
  /** Stable across renders, so a list that changes keeps the rows it did not change. */
  readonly key: string;
  /** The line as the creator reads it: "2 tbsp doubanjiang", "Fry the paste until it darkens." */
  readonly text: string;
  /** An ingredient's preparation ("chopped") or a step's note, set muted beside or under the text. */
  readonly note?: string | null;
  /** A step's own duration, said once, under the text ("About 5 minutes."). */
  readonly detail?: string | null;
  readonly isOptional?: boolean;
  /** The line still carries the model's own question where its value belongs: it gets a warning icon. */
  readonly flagged?: boolean;
  /** One thing wrong with the line that is not its words — "Unit “knob” not recognised" — said beside it. */
  readonly warning?: string | null;
}

/** A group of lines. `title` is null for the ordinary ungrouped list, which gets no heading (recipes.md). */
export interface RecipeSheetGroup {
  readonly key: string;
  readonly title: string | null;
  readonly isOptional?: boolean;
  readonly lines: readonly RecipeSheetLine[];
}

/**
 * The model's own question written into a line or a step where its value belongs — "Unresolved question: flour
 * amount, all-purpose flour, sifted". It is only read, to mark the line and to count it: the text is shown as
 * the draft wrote it until the creator edits it, and nothing is rewritten for them (recipes.md, ai.md).
 */
const OPEN_QUESTION_TEXT = /\bunresolved\s+questions?\b/i;

/** Whether a line or step still carries the model's question where its value belongs, as it reads right now. */
export function hasOpenQuestion(text: string | null | undefined): boolean {
  return text !== null && text !== undefined && OPEN_QUESTION_TEXT.test(text);
}

/**
 * A preparation note worth saying under a line, or null when the line already says it.
 *
 * A model often writes "1 cup onion, finely chopped" and then "finely chopped" as the note as well, and the same
 * words beside the line read as a second instruction.
 */
export function preparationNoteToShow(note: string | null, lineText: string | null): string | null {
  const trimmed = note?.trim() ?? '';
  if (trimmed.length === 0) return null;

  return (lineText ?? '').toLowerCase().includes(trimmed.toLowerCase()) ? null : trimmed;
}

/** What differs from page to page when a draft becomes a sheet. Every option has an ordinary default. */
export interface RecipeSheetOptions {
  /**
   * The text a key stands for as it reads now: a page where the creator can reword a line passes what they typed,
   * and gets the sheet in their words. Keys are `<changeId>:displayText` for an ingredient and `<changeId>:text`
   * for a step. Defaults to what the draft proposed.
   */
  readonly read?: (key: string, proposed: string | null) => string | null;
  /** The sentence for a line whose unit CreatorPantry does not recognise, or null. */
  readonly unitWarning?: (line: RecipeDraftIngredient) => string | null;
}

/** An untitled group among several still needs telling apart; a lone untitled one is the ungrouped case. */
function groupTitle<TItem>(group: RecipeDraftGroup<TItem>, index: number, total: number): string | null {
  if (group.title !== null) return group.title;

  return total > 1 ? `Group ${index + 1}` : null;
}

/** A draft's ingredient groups, as the sheet shows them. */
export function ingredientSheetGroups(draft: RecipeDraft, options: RecipeSheetOptions = {}): RecipeSheetGroup[] {
  const read = options.read ?? ((_key: string, proposed: string | null) => proposed);
  const total = draft.ingredientGroups.length;

  return draft.ingredientGroups.map((group, index) => ({
    key: group.changeId,
    title: groupTitle(group, index, total),
    isOptional: group.isOptional,
    lines: group.items.map((line) => {
      const text = read(`${line.changeId}:displayText`, line.displayText) ?? '';

      return {
        key: line.changeId,
        text,
        note: preparationNoteToShow(line.preparationNote, text),
        isOptional: line.isOptional,
        flagged: hasOpenQuestion(text),
        warning: options.unitWarning?.(line) ?? null,
      };
    }),
  }));
}

/** A draft's method groups, as the sheet shows them. */
export function stepSheetGroups(draft: RecipeDraft, options: RecipeSheetOptions = {}): RecipeSheetGroup[] {
  const read = options.read ?? ((_key: string, proposed: string | null) => proposed);
  const total = draft.instructionGroups.length;

  return draft.instructionGroups.map((group, index) => ({
    key: group.changeId,
    title: groupTitle(group, index, total),
    isOptional: group.isOptional,
    lines: group.items.map((step) => {
      const text = read(`${step.changeId}:text`, step.text) ?? '';

      return {
        key: step.changeId,
        text,
        note: step.note,
        detail: step.durationMinutes ? `About ${step.durationMinutes} minutes.` : null,
        flagged: hasOpenQuestion(text),
      };
    }),
  }));
}
