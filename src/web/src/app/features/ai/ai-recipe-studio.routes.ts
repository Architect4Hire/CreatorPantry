import { Routes } from '@angular/router';

import { recipeFirstDraftReviewCanDeactivateGuard } from './recipe-first-draft-review.guard';

export const AI_RECIPE_STUDIO_ROUTES: Routes = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./recipe-concept-studio.component').then((m) => m.RecipeConceptStudioComponent), data: { title: 'AI Recipe Studio' } },
  // The draft under review is named by `?request=`, not by a path segment, so the page resumes after a
  // refresh the same way the studio itself does. Nothing in the app creates such a request yet; the
  // surface that will is separate work.
  { path: 'draft', loadComponent: () => import('./recipe-first-draft-review.component').then((m) => m.RecipeFirstDraftReviewComponent), canDeactivate: [recipeFirstDraftReviewCanDeactivateGuard], data: { title: 'Recipe first draft' } },
];
