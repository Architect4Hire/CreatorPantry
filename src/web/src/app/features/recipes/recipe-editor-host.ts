import { Injectable } from '@angular/core';

/**
 * The parts of the recipe form a host can show: one or two of the form's sections, or `all` of them in form order —
 * the whole recipe on one page, for a step that edits whatever the creator chooses.
 */
export type RecipeEditorFocus = 'details' | 'ingredients' | 'method' | 'yield-and-time' | 'all';

/**
 * Puts {@link RecipeEditorComponent} on a page that is not its own — a guided step that is about one part of a
 * recipe — without a second editor. Provided by the host, read once by the editor as it is made.
 *
 * <p>Hosted, the editor is the same form with the same save, the same validation and the same concurrency token. It
 * shows only the sections {@link focus} names and none of the page around them: no page head, no areas, no tools.
 * Every section still loads and still submits, so a save from one part never drops another part's fields.</p>
 *
 * <p>It never navigates. Creating a recipe calls {@link created} instead, and the host decides what comes next — a
 * hosted editor for a new recipe is recreated for that recipe, because the editor reads its recipe once.</p>
 *
 * <p>Plain fields rather than signals, on purpose: the editor takes them as it is constructed, so the host sets
 * them before it renders the editor, and renders a new one to change them.</p>
 */
@Injectable()
export class RecipeEditorHost {
  workspaceSlug = '';
  /** The recipe to edit, or null to create one. */
  recipeId: string | null = null;
  focus: RecipeEditorFocus = 'details';
  /** Called once a recipe is created, with its id, in place of the editor's navigation to it. */
  created: (recipeId: string) => void = () => undefined;
}
