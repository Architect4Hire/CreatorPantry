import { Routes } from '@angular/router';

import { CONTEXT_ROUTE_PARAM, CONTEXT_ROUTE_SEGMENT } from '../../shared/use-this-in/handoff-destinations';
import { recipeFirstDraftReviewCanDeactivateGuard } from './recipe-first-draft-review.guard';

export const AI_RECIPE_STUDIO_ROUTES: Routes = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./recipe-concept-studio.component').then((m) => m.RecipeConceptStudioComponent), data: { title: 'AI Recipe Studio' } },
  // The draft under review is named by `?request=`, not by a path segment, so the page resumes after a
  // refresh the same way the studio itself does. Nothing in the app creates such a request yet; the
  // surface that will is separate work.
  { path: 'draft', loadComponent: () => import('./recipe-first-draft-review.component').then((m) => m.RecipeFirstDraftReviewComponent), canDeactivate: [recipeFirstDraftReviewCanDeactivateGuard], data: { title: 'Recipe first draft' } },
  // The same review, opened for a creative context (AF.1.4). The id is a path segment after a literal
  // `context`; the draft is still named by `?request=`. The page does not read the id yet.
  { path: `draft/${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}`, loadComponent: () => import('./recipe-first-draft-review.component').then((m) => m.RecipeFirstDraftReviewComponent), canDeactivate: [recipeFirstDraftReviewCanDeactivateGuard], data: { title: 'Recipe first draft' } },
];
