import { Routes } from '@angular/router';

import { recipeEditorCanDeactivateGuard } from './recipe-editor.guard';

export const RECIPES_ROUTES: Routes = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./recipe-library.component').then((m) => m.RecipeLibraryComponent), data: { title: 'Recipes' } },
  { path: 'new', loadComponent: () => import('./recipe-editor.component').then((m) => m.RecipeEditorComponent), canDeactivate: [recipeEditorCanDeactivateGuard], data: { title: 'New recipe' } },
  // Before ':recipeId' only for the reader: the two do not overlap, since that one matches a single segment.
  { path: ':recipeId/kitchen', loadComponent: () => import('./recipe-kitchen-view.component').then((m) => m.RecipeKitchenViewComponent), data: { title: 'Kitchen view' } },
  { path: ':recipeId', loadComponent: () => import('./recipe-editor.component').then((m) => m.RecipeEditorComponent), canDeactivate: [recipeEditorCanDeactivateGuard], data: { title: 'Edit recipe' } },
];
