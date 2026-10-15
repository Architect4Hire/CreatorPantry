import { CanDeactivateFn } from '@angular/router';

// A type only, so the route table that names this guard does not pull the page into the main bundle.
import type { WorkflowRunPageComponent } from './workflow-run-page.component';

/**
 * Leaving a run by the router: the shell keeps the step's work first and asks only when it cannot be kept. The
 * shell's own moves — Save and exit, Start over — are let through. A browser close or refresh is the shell's
 * `beforeunload` listener, which the router never sees.
 */
export const workflowRunCanDeactivateGuard: CanDeactivateFn<WorkflowRunPageComponent> = (component) =>
  component.confirmLeaveIfUnsaved();
