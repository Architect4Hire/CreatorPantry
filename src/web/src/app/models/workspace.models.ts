// One workspace and the caller's membership in it, as `GET /api/v1/workspaces/{slug}` answers. Mirrors
// `WorkspaceServiceModel`.

import { WorkspaceRole } from './auth.models';
import { isRecord } from './recipe.models';

/**
 * The systems a workspace may work in by default (B-08). Deliberately narrower than the reference
 * `MeasurementSystem`: a workspace is never `Neutral` or `Imperial`, and the API refuses both.
 */
export type WorkspaceMeasurementSystem = 'Metric' | 'UsCustomary';

export const WORKSPACE_MEASUREMENT_SYSTEMS: readonly WorkspaceMeasurementSystem[] = ['UsCustomary', 'Metric'];

export interface Workspace {
  readonly workspaceId: string;
  readonly name: string;
  readonly slug: string;
  readonly role: WorkspaceRole;
  readonly defaultMeasurementSystem: WorkspaceMeasurementSystem;
}

const ROLES: readonly WorkspaceRole[] = ['Viewer', 'Contributor', 'Editor', 'Owner'];

export function isWorkspaceMeasurementSystem(value: unknown): value is WorkspaceMeasurementSystem {
  return typeof value === 'string' && (WORKSPACE_MEASUREMENT_SYSTEMS as readonly string[]).includes(value);
}

/**
 * The workspace, or null when the body is not one.
 *
 * A system this client does not know fails the decode rather than falling back to one it does: showing a
 * creator "US customary" for a workspace set to something else would be a wrong answer, not a degraded one.
 */
export function decodeWorkspace(value: unknown): Workspace | null {
  if (!isRecord(value)) return null;

  const { workspaceId, name, slug, role, defaultMeasurementSystem } = value;
  if (
    typeof workspaceId !== 'string' ||
    typeof name !== 'string' ||
    typeof slug !== 'string' ||
    typeof role !== 'string' ||
    !(ROLES as readonly string[]).includes(role) ||
    !isWorkspaceMeasurementSystem(defaultMeasurementSystem)
  ) {
    return null;
  }

  return { workspaceId, name, slug, role: role as WorkspaceRole, defaultMeasurementSystem };
}
