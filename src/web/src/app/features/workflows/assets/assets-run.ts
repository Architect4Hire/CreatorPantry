import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';
import { newestLinked } from '../creative-work/creative-work-run';

/** The key of the "Organize your pictures" journey (`WorkflowCatalogue.OrganizeAssetsKey`). */
export const ORGANIZE_ASSETS_DEFINITION_KEY = 'assets.organize';

/**
 * The library picture this run is about (13A.21): the newest one linked — the one it started from, or the one its
 * first step chose — or, before that step was done, the one the step is keeping. Null before there is any.
 */
export function runAssetId(run: WorkflowRun): string | null {
  return (
    newestLinked(run, 'MediaAsset') ?? run.steps.find((step) => step.draft?.kind === 'MediaAsset')?.draft?.id ?? null
  );
}

/** A library picture, as a run names one. */
export function assetPointer(assetId: string): WorkflowResourcePointer {
  return { kind: 'MediaAsset', id: assetId };
}
