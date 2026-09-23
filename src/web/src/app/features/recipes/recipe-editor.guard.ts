import { CanDeactivateFn } from '@angular/router';

import { RecipeEditorComponent } from './recipe-editor.component';

/**
 * Router-driven navigation away from the recipe editor (link clicks, programmatic navigate calls,
 * including the editor's own post-create redirect) is confirmed through the component itself,
 * which owns the dirty-state and the confirm dialog. A full browser navigation/close/refresh isn't
 * covered by this guard at all — that's the separate `beforeunload` listener in the component,
 * since the Router never sees those.
 */
export const recipeEditorCanDeactivateGuard: CanDeactivateFn<RecipeEditorComponent> = (component) =>
  component.confirmDiscardIfDirty();
