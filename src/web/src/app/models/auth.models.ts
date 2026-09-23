export type WorkspaceRole = 'Viewer' | 'Contributor' | 'Editor' | 'Owner';
export type WorkspaceMembershipStatus = 'Invited' | 'Active' | 'Removed';

export interface MyWorkspaceMembership {
  readonly workspaceId: string;
  readonly workspaceSlug: string;
  readonly workspaceName: string;
  readonly membershipId: string;
  readonly role: WorkspaceRole;
  readonly status: WorkspaceMembershipStatus;
}

const ROLE_BY_NUMBER: Record<number, WorkspaceRole> = { 0: 'Viewer', 10: 'Contributor', 20: 'Editor', 30: 'Owner' };
const STATUS_BY_NUMBER: Record<number, WorkspaceMembershipStatus> = { 0: 'Invited', 10: 'Active', 20: 'Removed' };

function decodeRole(value: unknown): WorkspaceRole | null {
  return typeof value === 'number' ? (ROLE_BY_NUMBER[value] ?? null) : null;
}

function decodeStatus(value: unknown): WorkspaceMembershipStatus | null {
  return typeof value === 'number' ? (STATUS_BY_NUMBER[value] ?? null) : null;
}

/**
 * Decodes one entry of the GET /api/v1/me response. `role`/`status` are always raw integers on the
 * wire — confirmed against the API's committed OpenAPI snapshot, which documents both as
 * `"type": "integer"` (no JsonStringEnumConverter is configured anywhere in the solution). If that
 * ever changes, this should fail loudly (an entry silently dropped) rather than quietly widen to
 * tolerate a format the backend was never proven to send.
 */
export function decodeMyWorkspaceMembership(value: unknown): MyWorkspaceMembership | null {
  if (typeof value !== 'object' || value === null) return null;
  const record = value as Record<string, unknown>;

  const workspaceId = record['workspaceId'];
  const workspaceSlug = record['workspaceSlug'];
  const workspaceName = record['workspaceName'];
  const membershipId = record['membershipId'];
  const role = decodeRole(record['role']);
  const status = decodeStatus(record['status']);

  if (
    typeof workspaceId !== 'string' ||
    typeof workspaceSlug !== 'string' ||
    typeof workspaceName !== 'string' ||
    typeof membershipId !== 'string' ||
    role === null ||
    status === null
  ) {
    return null;
  }

  return { workspaceId, workspaceSlug, workspaceName, membershipId, role, status };
}

/** Decodes the full GET /api/v1/me response array, silently dropping any entry that fails to decode. */
export function decodeMyWorkspaceMemberships(value: unknown): MyWorkspaceMembership[] {
  if (!Array.isArray(value)) return [];
  return value.map(decodeMyWorkspaceMembership).filter((entry): entry is MyWorkspaceMembership => entry !== null);
}
