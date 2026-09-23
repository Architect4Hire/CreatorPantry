import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';

import { authGuard } from './core/auth.guard';

const SECTION_ROUTES: Routes = [
  { path: 'dashboard', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Dashboard' } },
  { path: 'workflows', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Workflows' } },
  { path: 'my-day', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'My Day' } },
  { path: 'my-week', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'My Week' } },
  { path: 'recipes', loadComponent: () => import('./shell/placeholder-section.component').then((m) => m.PlaceholderSectionComponent), data: { title: 'Recipes' } },
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
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./shell/app-shell.component').then((m) => m.AppShellComponent),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./shell/workspace-gate.component').then((m) => m.WorkspaceGateComponent), data: { title: 'Workspaces' } },
      { path: ':workspaceSlug', children: SECTION_ROUTES },
    ],
  },
  {
    path: 'sign-in',
    loadComponent: () => import('./features/sign-in/sign-in.component').then((m) => m.SignInComponent),
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
];
