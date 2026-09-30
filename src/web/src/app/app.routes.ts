import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';

import { anonymousOnlyGuard } from './core/anonymous-only.guard';
import { authGuard } from './core/auth.guard';
import { brandSettingsCanDeactivateGuard } from './features/brand/brand-settings.guard';
import { recipeFirstDraftReviewCanDeactivateGuard } from './features/ai/recipe-first-draft-review.guard';
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
  { path: 'brand', loadComponent: () => import('./features/brand/brand-settings.component').then((m) => m.BrandSettingsComponent), canDeactivate: [brandSettingsCanDeactivateGuard], data: { title: 'Brand' } },
  {
    path: 'ai-recipe-studio',
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/ai/recipe-concept-studio.component').then((m) => m.RecipeConceptStudioComponent), data: { title: 'AI Recipe Studio' } },
      // The draft under review is named by `?request=`, not by a path segment, so the page resumes after a
      // refresh the same way the studio itself does. Nothing in the app creates such a request yet; the
      // surface that will is separate work.
      { path: 'draft', loadComponent: () => import('./features/ai/recipe-first-draft-review.component').then((m) => m.RecipeFirstDraftReviewComponent), canDeactivate: [recipeFirstDraftReviewCanDeactivateGuard], data: { title: 'Recipe first draft' } },
    ],
  },
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
  // The public landing page. Anonymous-only: an already-authenticated visitor is forwarded into the app
  // instead of seeing marketing content. It renders before any workspace context exists, so it must
  // never request or expose workspace-scoped data.
  {
    path: '',
    pathMatch: 'full',
    canActivate: [anonymousOnlyGuard],
    loadComponent: () => import('./features/landing/landing.component').then((m) => m.LandingComponent),
  },
  // 'app' hosts the signed-in shell's own index page (workspace resolution/creation) before a workspace
  // is chosen. It is therefore, like 'sign-in'/'sign-up'/etc., a literal top-level slug no real workspace
  // may use.
  {
    path: 'app',
    canActivate: [authGuard],
    loadComponent: () => import('./shell/app-shell.component').then((m) => m.AppShellComponent),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./shell/workspace-gate.component').then((m) => m.WorkspaceGateComponent), data: { title: 'Workspaces' } },
      // Account-scoped, and deliberately not one of the workspace sections: an allowance belongs to the
      // person, and the answer spans every workspace they work in. A route under ':workspaceSlug' could only
      // narrow it to one or answer about workspaces it does not name.
      { path: 'ai-usage', loadComponent: () => import('./features/ai-usage/ai-usage.component').then((m) => m.AiUsageComponent), data: { title: 'AI allowance' } },
    ],
  },
  // Must come after every literal top-level path (sign-in, app, design-system, ...): ':workspaceSlug'
  // below matches any single segment, so if this route were tried first it would swallow those literal
  // paths as a workspace slug, send them through authGuard, and redirect back into the same trap — an
  // infinite navigation loop with no console error, since nothing ever actually throws.
  {
    path: ':workspaceSlug',
    canActivate: [authGuard],
    loadComponent: () => import('./shell/app-shell.component').then((m) => m.AppShellComponent),
    children: SECTION_ROUTES,
  },
];
