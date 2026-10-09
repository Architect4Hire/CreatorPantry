import { Routes } from '@angular/router';

import { CONTEXT_ROUTE_PARAM, CONTEXT_ROUTE_SEGMENT } from '../../shared/use-this-in/handoff-destinations';

/**
 * The Content Pipeline's routes, mounted under `workflows` so the Workflows nav item stays active throughout —
 * the pipeline is one of the guided journeys that section lists, not a section of its own.
 *
 * The step is a path segment rather than a query parameter: a creator's place in the journey is somewhere they
 * came from and can go back to, which is what Back and a reload both depend on. No `canDeactivate` guard,
 * because every answer is kept as it is made and leaving loses nothing.
 *
 * **A run opened for a creative context carries its id as path segments** — `context/:contextId/:step` — ahead
 * of the step (AF.1.4). The literal `context` is what keeps the two shapes apart: a step is one segment and so
 * is an id, so without it `content-pipeline/<id>` could not be told from `content-pipeline/<step>`. Those
 * routes come first for the same reason. The shell reads that context as the run (AF.3.1) and keeps its id in
 * the address between steps.
 */
export const CONTENT_PIPELINE_ROUTES: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'setup' },
  { path: `${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}`, pathMatch: 'full', redirectTo: `${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}/setup` },
  {
    path: `${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}/:step`,
    loadComponent: () => import('./content-pipeline-shell.component').then((m) => m.ContentPipelineShellComponent),
    data: { title: 'Content Pipeline' },
  },
  {
    path: ':step',
    loadComponent: () => import('./content-pipeline-shell.component').then((m) => m.ContentPipelineShellComponent),
    data: { title: 'Content Pipeline' },
  },
];
