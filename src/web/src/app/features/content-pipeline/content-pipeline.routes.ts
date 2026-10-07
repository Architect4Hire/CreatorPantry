import { Routes } from '@angular/router';

/**
 * The Content Pipeline's routes, mounted under `workflows` so the Workflows nav item stays active throughout —
 * the pipeline is one of the guided journeys that section lists, not a section of its own.
 *
 * The step is a path segment rather than a query parameter: a creator's place in the journey is somewhere they
 * came from and can go back to, which is what Back and a reload both depend on. No `canDeactivate` guard,
 * because every answer is kept as it is made and leaving loses nothing.
 */
export const CONTENT_PIPELINE_ROUTES: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'setup' },
  {
    path: ':step',
    loadComponent: () => import('./content-pipeline-shell.component').then((m) => m.ContentPipelineShellComponent),
    data: { title: 'Content Pipeline' },
  },
];
