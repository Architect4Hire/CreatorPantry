import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';
import { newestLinked } from '../creative-work/creative-work-run';

/** The key of the "Plan and publish content" journey (`WorkflowCatalogue.PlanAndPublishKey`). */
export const PLAN_AND_PUBLISH_DEFINITION_KEY = 'publishing.plan-and-publish';

/**
 * The board card this run is about (13A.22): the one it started from, the one its first step chose, or — before
 * that step was done — the one that step is keeping. Null before there is any.
 */
export function runCardId(run: WorkflowRun): string | null {
  return newestLinked(run, 'ContentCard') ?? run.steps.find((step) => step.draft?.kind === 'ContentCard')?.draft?.id ?? null;
}

/**
 * The booking this run made, as the run holds it. Null until one exists.
 *
 * **Never read as proof that anything went out.** It names a publishing record; what became of that record is
 * the record's own `status`, which the last step reads and the server's completion rule judges.
 */
export function runRecordId(run: WorkflowRun): string | null {
  return (
    newestLinked(run, 'PublishingRecord') ??
    run.steps.find((step) => step.draft?.kind === 'PublishingRecord')?.draft?.id ??
    null
  );
}

/** A board card, as a run names one. */
export function cardPointer(cardId: string): WorkflowResourcePointer {
  return { kind: 'ContentCard', id: cardId };
}

/** A booking, as a run names one. */
export function recordPointer(recordId: string): WorkflowResourcePointer {
  return { kind: 'PublishingRecord', id: recordId };
}
