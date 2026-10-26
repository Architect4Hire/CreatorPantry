import { CanDeactivateFn } from '@angular/router';

import type { RecipeStudioFlowComponent } from './recipe-studio-flow.component';

/**
 * Router-driven navigation away from the studio is confirmed through the flow, which knows whether the first-draft
 * review is on screen (AU.5.2). The review owns the creator's rewrites and the confirm dialog.
 *
 * The rewrites have nowhere to be saved to — the review calls no write route, by design — so leaving really does
 * lose them, and `?draft=` bringing the creator back to the same draft makes that more surprising rather than less.
 * A full browser navigation, close or refresh is not covered here and cannot be; that is the review's own
 * `beforeunload` listener, which the Router never sees.
 */
export const recipeStudioFlowCanDeactivateGuard: CanDeactivateFn<RecipeStudioFlowComponent> = (component) =>
  component.confirmLeaveIfUnsaved();
