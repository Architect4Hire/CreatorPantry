/**
 * The way back to a guided run from a product page it deep-linked into (13A.17). The run passes its id as
 * `?workflowRun=`; the page uses it to draw one back link and for nothing else — it grants nothing and is never sent
 * anywhere. Only a well-formed id is taken, so the link can only ever name a run page, and a run that is not the
 * reader's answers there as not found.
 */
export const WORKFLOW_RUN_PARAM = 'workflowRun';

const RUN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The run id from a query parameter's value, or null when there is none or it is not an id. */
export function workflowRunFrom(value: string | null | undefined): string | null {
  return value && RUN_ID.test(value) ? value : null;
}

/** Router commands for the run page. */
export function workflowRunLink(workspaceSlug: string, runId: string): string[] {
  return ['/', workspaceSlug, 'workflows', 'runs', runId];
}

/** The query that carries a run along with a deep link, or none. */
export function workflowRunQuery(runId: string | null): Record<string, string> {
  return runId ? { [WORKFLOW_RUN_PARAM]: runId } : {};
}
