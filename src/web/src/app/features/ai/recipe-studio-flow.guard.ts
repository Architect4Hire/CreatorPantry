import { CanDeactivateFn } from '@angular/router';

import { RecipeFirstDraftReviewComponent } from './recipe-first-draft-review.component';

/**
 * Router-driven navigation away from the first-draft review is confirmed through the component, which owns
 * the creator's rewrites and the confirm dialog.
 *
 * The rewrites have nowhere to be saved to — this page calls no write route, by design — so leaving really
 * does lose them, and `?request=` bringing the creator back to the same draft makes that more surprising
 * rather than less. A full browser navigation, close or refresh is not covered here and cannot be; that is
 * the component's own `beforeunload` listener, which the Router never sees.
 */
export const recipeFirstDraftReviewCanDeactivateGuard: CanDeactivateFn<RecipeFirstDraftReviewComponent> = (
  component,
) => component.confirmDiscardIfDirty();
