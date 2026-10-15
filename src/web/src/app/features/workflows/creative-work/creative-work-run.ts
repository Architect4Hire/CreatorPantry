import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';

/** The key of the "Create food images" journey (`WorkflowCatalogue.ImagesKey`). */
export const IMAGES_DEFINITION_KEY = 'images.create';

/** The key of the "Create social posts" journey (`WorkflowCatalogue.SocialPostsKey`). */
export const SOCIAL_POSTS_DEFINITION_KEY = 'social.create';

/**
 * The piece of work this run holds (13A.19, 13A.20): the newest one linked, or — before the first step was done —
 * the one its first step is keeping its work in. Null before there is any.
 */
export function runContextId(run: WorkflowRun): string | null {
  const linked = newestLinked(run, 'CreativeContext');
  if (linked !== null) return linked;

  const draft = run.steps.find((step) => step.draft?.kind === 'CreativeContext')?.draft ?? null;
  return draft?.id ?? null;
}

/** The newest record of one kind the run holds, as the server keeps them in the order they were linked. */
export function newestLinked(run: WorkflowRun, kind: string): string | null {
  const links = run.links.filter((link) => link.kind === kind);
  return links.length > 0 ? links[links.length - 1].id : null;
}

/** A piece of work, as a run names one. */
export function contextPointer(contextId: string): WorkflowResourcePointer {
  return { kind: 'CreativeContext', id: contextId };
}
