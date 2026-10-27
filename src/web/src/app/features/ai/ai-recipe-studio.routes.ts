import { Routes } from '@angular/router';

import { CONTEXT_ROUTE_PARAM, CONTEXT_ROUTE_SEGMENT } from '../../shared/use-this-in/handoff-destinations';
import { recipeStudioFlowCanDeactivateGuard } from './recipe-studio-flow.guard';

export const AI_RECIPE_STUDIO_ROUTES: Routes = [
  // The studio is one flow (AU.5.2): describe what you're after, choose a concept, review the first draft. Its
  // place is in the address — `?step=`, the ideas request as `?request=`, the draft request as `?draft=` — so it
  // resumes after a refresh. Leaving it from the review asks first, as the review's own page did.
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('./recipe-studio-flow.component').then((m) => m.RecipeStudioFlowComponent),
    canDeactivate: [recipeStudioFlowCanDeactivateGuard],
    data: { title: 'AI Recipe Studio', pageWidth: 'wide' },
  },
  // The same studio, opened for a piece of work (AF.7.4): its id is a path segment after a literal `context`, as
  // in every other destination, and the ideas asked for there follow that work's themed day.
  {
    path: `${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}`,
    loadComponent: () => import('./recipe-studio-flow.component').then((m) => m.RecipeStudioFlowComponent),
    canDeactivate: [recipeStudioFlowCanDeactivateGuard],
    data: { title: 'AI Recipe Studio', pageWidth: 'wide' },
  },
  // The review's old addresses, named by `?request=`, open the flow on its last step and move to its address.
  {
    path: 'draft',
    loadComponent: () => import('./recipe-studio-flow.component').then((m) => m.RecipeStudioFlowComponent),
    canDeactivate: [recipeStudioFlowCanDeactivateGuard],
    data: { title: 'AI Recipe Studio', pageWidth: 'wide', legacyDraft: true },
  },
  {
    path: `draft/${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}`,
    loadComponent: () => import('./recipe-studio-flow.component').then((m) => m.RecipeStudioFlowComponent),
    canDeactivate: [recipeStudioFlowCanDeactivateGuard],
    data: { title: 'AI Recipe Studio', pageWidth: 'wide', legacyDraft: true },
  },
];
