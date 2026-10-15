import { BrandStyleGuideDetail } from '../../../models/brand-style-guide.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { BrandStyleGuideService } from '../../../services/brand-style-guide.service';

/** What a "Create my brand voice" step reads its guide as: found, gone, or not readable just now. */
export type LoadedGuide =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly guide: BrandStyleGuideDetail };

/** The guide this run holds: the newest one linked, as the server keeps them in the order they were linked. */
export function linkedGuideId(run: WorkflowRun): string | null {
  const guides = run.links.filter((link) => link.kind === 'BrandStyleGuide');
  return guides.length > 0 ? guides[guides.length - 1].id : null;
}

/** The run's guide, read for a step: found, gone, or not readable just now. */
export async function loadGuide(guides: BrandStyleGuideService, workspaceSlug: string, run: WorkflowRun): Promise<LoadedGuide> {
  const id = linkedGuideId(run);
  if (id === null) return { status: 'missing' };
  const outcome = await guides.getDetail(workspaceSlug, id);
  if (outcome.status === 'ok') return { status: 'ready', guide: outcome.guide };
  return outcome.status === 'not_found' ? { status: 'missing' } : { status: 'unavailable' };
}

export const NO_GUIDE_YET = 'There’s no guide yet. Go back to the first step and build one.';

