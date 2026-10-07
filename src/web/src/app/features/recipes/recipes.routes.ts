import { Routes } from '@angular/router';

import { recipeEditorCanDeactivateGuard } from './recipe-editor.guard';

export const RECIPES_ROUTES: Routes = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./recipe-library.component').then((m) => m.RecipeLibraryComponent), data: { title: 'Recipes' } },
  { path: 'new', loadComponent: () => import('./recipe-editor.component').then((m) => m.RecipeEditorComponent), canDeactivate: [recipeEditorCanDeactivateGuard], data: { title: 'New recipe' } },
  { path: ':recipeId', loadComponent: () => import('./recipe-editor.component').then((m) => m.RecipeEditorComponent), canDeactivate: [recipeEditorCanDeactivateGuard], data: { title: 'Edit recipe' } },
];
