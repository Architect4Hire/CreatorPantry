import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';

import { authGuard } from './core/auth.guard';
import { recipeEditorCanDeactivateGuard } from './features/recipes/recipe-editor.guard';

const SECTION_ROUTES: Routes = [
  { path: 'dashboard', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Dashboard' } },
  { path: 'workflows', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Workflows' } },
  { path: 'my-day', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'My Day' } },
  { path: 'my-week', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'My Week' } },
  {
    path: 'recipes',
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/recipes/recipe-library.component').then((m) => m.RecipeLibraryComponent), data: { title: 'Recipes' } },
      { path: 'new', loadComponent: () => import('./features/recipes/recipe-editor.component').then((m) => m.RecipeEditorComponent), canDeactivate: [recipeEditorCanDeactivateGuard], data: { title: 'New recipe' } },
      { path: ':recipeId', loadComponent: () => import('./features/recipes/recipe-editor.component').then((m) => m.RecipeEditorComponent), canDeactivate: [recipeEditorCanDeactivateGuard], data: { title: 'Edit recipe' } },
    ],
  },
  { path: 'brand', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Brand' } },
  { path: 'ai-recipe-studio', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'AI Recipe Studio' } },
  { path: 'image-studio', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Image Studio' } },
  { path: 'social-studio', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Social Studio' } },
  { path: 'dam', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'DAM' } },
  { path: 'content-board', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Content Board' } },
  { path: 'prompt-library', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Prompt Library' } },
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
];

export const routes: Routes = [
  {
    path: 'sign-in',
    loadComponent: () => import('./features/sign-in/sign-in.component').then((m) => m.SignInComponent),
  },
  {
    path: 'sign-up',
    loadComponent: () => import('./features/sign-up/sign-up.component').then((m) => m.SignUpComponent),
  },
  {
    path: 'confirm-email',
    loadComponent: () => import('./features/confirm-email/confirm-email.component').then((m) => m.ConfirmEmailComponent),
  },
  ...(isDevMode()
    ? [
        {
          path: 'design-system',
          loadComponent: () =>
            import('./design-system-showcase/design-system-showcase.component').then(
              (m) => m.DesignSystemShowcaseComponent,
            ),
        },
      ]
    : []),
  // Must come after every literal top-level path (sign-in, design-system, ...): ':workspaceSlug' below
  // matches any single segment, so if this route were tried first it would swallow those literal paths
  // as a workspace slug, send them through authGuard, and redirect back into the same trap — an infinite
  // navigation loop with no console error, since nothing ever actually throws.
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./shell/app-shell.component').then((m) => m.AppShellComponent),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./shell/workspace-gate.component').then((m) => m.WorkspaceGateComponent), data: { title: 'Workspaces' } },
      { path: ':workspaceSlug', children: SECTION_ROUTES },
    ],
  },
];
