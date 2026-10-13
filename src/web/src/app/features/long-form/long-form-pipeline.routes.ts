import { CanDeactivateFn, Routes, UrlMatchResult, UrlSegment } from '@angular/router';

import type { LongFormPipelineShellComponent } from './long-form-pipeline-shell.component';

/**
 * Every address of the long-form pipeline (LF.6.2), as one route.
 *
 * ```text
 * long-form                                              a piece not started
 * long-form/context/:contextId                           a piece of work, opened where it stands
 * long-form/context/:contextId/:step                     a piece with no draft yet (the first step)
 * long-form/context/:contextId/draft/:draftId            a draft, opened where it stands
 * long-form/context/:contextId/draft/:draftId/:step      a draft, at a step
 * ```
 *
 * **One matcher rather than five routes**, because the router reuses a component only while the same route
 * matches. Filing the work, starting its draft and moving between steps each change the address's shape; as
 * five routes each would destroy the page and the step on it, and with it whatever the creator had just typed.
 *
 * The literals `context` and `draft` keep an id from being read as a step. A step that is not one of the four
 * is still matched — the shell sends it to the step the piece has reached — but a shape that is none of the
 * above is not this route's, and falls through to the app's not-found page.
 */
export function longFormPipelineMatcher(segments: UrlSegment[]): UrlMatchResult | null {
  const paths = segments.map((segment) => segment.path);
  const posParams: Record<string, UrlSegment> = {};

  if (paths.length === 0) return { consumed: segments, posParams };
  if (paths[0] !== 'context' || paths.length < 2) return null;

  posParams['contextId'] = segments[1]!;

  if (paths.length === 2) return { consumed: segments, posParams };

  if (paths.length === 3 && paths[2] !== 'draft') {
    posParams['step'] = segments[2]!;

    return { consumed: segments, posParams };
  }

  if (paths[2] !== 'draft' || paths.length < 4 || paths.length > 5) return null;

  posParams['draftId'] = segments[3]!;
  if (paths.length === 5) posParams['step'] = segments[4]!;

  return { consumed: segments, posParams };
}

/**
 * Asked on every way out of a step the shell did not take itself: the browser's Back, a link in the app's
 * navigation, a typed address. The step on screen may hold text the creator has not saved, and it is the one
 * that knows; the shell passes the question on and has already answered it for its own buttons.
 */
export const longFormPipelineCanDeactivateGuard: CanDeactivateFn<LongFormPipelineShellComponent> = (component) =>
  component.confirmLeaveIfUnsaved();

/** Mounted under `workflows`, so the Workflows nav item stays active throughout, as for the Content Pipeline. */
export const LONG_FORM_PIPELINE_ROUTES: Routes = [
  {
    matcher: longFormPipelineMatcher,
    loadComponent: () => import('./long-form-pipeline-shell.component').then((m) => m.LongFormPipelineShellComponent),
    canDeactivate: [longFormPipelineCanDeactivateGuard],

    // Always, so a move between two steps of one piece asks the guard too: it is the same route either way.
    runGuardsAndResolvers: 'always',
    data: { title: 'Long-form' },
  },
];
