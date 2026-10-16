import { decodeEnum, isRecord } from './recipe.models';
import { WorkflowRunStatus } from './workflow-run.models';

/**
 * What the Workflow Hub (13A.13) reads: the workflows a workspace offers (`GET .../workflow-definitions`), and pages
 * of runs (`GET .../workflow-runs`). Plain language and pointers only — no step, capability or model detail.
 */

export type WorkspaceRoleName = 'Viewer' | 'Contributor' | 'Editor' | 'Owner';

const ROLES: ReadonlySet<string> = new Set<WorkspaceRoleName>(['Viewer', 'Contributor', 'Editor', 'Owner']);
const RUN_STATUSES: ReadonlySet<string> = new Set<WorkflowRunStatus>(['InProgress', 'Completed', 'Abandoned']);

/** Something a workflow needs before it starts, and the workspace page that sorts it out. */
export interface WorkflowStartNeed {
  readonly message: string;
  /** Relative to the workspace (`brand`), or null. */
  readonly route: string | null;
}

export interface WorkflowDefinitionSummary {
  readonly key: string;
  readonly goal: string;
  readonly description: string;
  readonly expectedOutcome: string;
  readonly available: boolean;
  readonly canStart: boolean;
  /** The least role that could start it, when someone could and this reader cannot. */
  readonly neededRole: WorkspaceRoleName | null;
  readonly waitingFor: readonly WorkflowStartNeed[];
}

/** Why an in-progress run needs a look, as the server codes it. Treat as an open set. */
export type WorkflowAttentionReason = string;

export interface WorkflowRunMember {
  readonly membershipId: string;
  /** Null for someone who has left the workspace. */
  readonly displayName: string | null;
}

export interface WorkflowRunSummary {
  readonly id: string;
  readonly title: string;
  readonly status: WorkflowRunStatus;
  readonly definitionKey: string;
  readonly goal: string;
  readonly owner: WorkflowRunMember;
  readonly stepCount: number;
  readonly completedStepCount: number;
  readonly skippedStepCount: number;
  readonly currentStepTitle: string | null;
  /** The next step's title, while the run has one to do. */
  readonly nextStepTitle: string | null;
  readonly updatedAt: string;
  readonly completedAt: string | null;
  readonly attentionReasons: readonly WorkflowAttentionReason[];
}

export interface WorkflowRunSummaryPage {
  readonly items: readonly WorkflowRunSummary[];
  readonly nextCursor: string | null;
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

function isCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

function decodeNeed(value: unknown): WorkflowStartNeed | null {
  if (!isRecord(value) || !isString(value['message'])) return null;
  return { message: value['message'], route: isString(value['route']) ? value['route'] : null };
}

function decodeDefinition(value: unknown): WorkflowDefinitionSummary | null {
  if (
    !isRecord(value)
    || !isString(value['key'])
    || !isString(value['goal'])
    || !isString(value['description'])
    || !isString(value['expectedOutcome'])
    || typeof value['available'] !== 'boolean'
    || typeof value['canStart'] !== 'boolean'
    || !Array.isArray(value['waitingFor'])
  ) {
    return null;
  }

  const waitingFor = value['waitingFor'].map(decodeNeed);
  if (waitingFor.some((need) => need === null)) return null;

  return {
    key: value['key'],
    goal: value['goal'],
    description: value['description'],
    expectedOutcome: value['expectedOutcome'],
    available: value['available'],
    canStart: value['canStart'],
    neededRole: decodeEnum<WorkspaceRoleName>(ROLES, value['neededRole']),
    waitingFor: waitingFor as WorkflowStartNeed[],
  };
}

/** The workflows a workspace offers, or null when the answer is not that list. */
export function decodeWorkflowDefinitions(value: unknown): readonly WorkflowDefinitionSummary[] | null {
  if (!Array.isArray(value)) return null;
  const decoded = value.map(decodeDefinition);
  return decoded.some((item) => item === null) ? null : (decoded as WorkflowDefinitionSummary[]);
}

function decodeMember(value: unknown): WorkflowRunMember | null {
  if (!isRecord(value) || !isString(value['membershipId'])) return null;
  return { membershipId: value['membershipId'], displayName: isString(value['displayName']) ? value['displayName'] : null };
}

function titleOf(value: unknown, key: string): string | null {
  return isRecord(value) && isString(value[key]) ? value[key] : null;
}

function decodeSummary(value: unknown): WorkflowRunSummary | null {
  if (!isRecord(value)) return null;
  const status = decodeEnum<WorkflowRunStatus>(RUN_STATUSES, value['status']);
  const owner = decodeMember(value['owner']);
  const reasons = value['attentionReasons'];

  if (
    !isString(value['id'])
    || !isString(value['title'])
    || status === null
    || !isString(value['definitionKey'])
    || !isString(value['goal'])
    || owner === null
    || !isCount(value['stepCount'])
    || !isCount(value['completedStepCount'])
    || !isCount(value['skippedStepCount'])
    || !isString(value['updatedAt'])
    || (reasons !== undefined && !(Array.isArray(reasons) && reasons.every(isString)))
  ) {
    return null;
  }

  return {
    id: value['id'],
    title: value['title'],
    status,
    definitionKey: value['definitionKey'],
    goal: value['goal'],
    owner,
    stepCount: value['stepCount'],
    completedStepCount: value['completedStepCount'],
    skippedStepCount: value['skippedStepCount'],
    currentStepTitle: titleOf(value['currentStep'], 'title'),
    nextStepTitle: titleOf(value['nextAction'], 'title'),
    updatedAt: value['updatedAt'],
    completedAt: isString(value['completedAt']) ? value['completedAt'] : null,
    attentionReasons: (reasons as string[] | undefined) ?? [],
  };
}

/** A page of runs, or null when it is not one. */
export function decodeWorkflowRunSummaryPage(value: unknown): WorkflowRunSummaryPage | null {
  if (!isRecord(value) || !Array.isArray(value['items'])) return null;
  const items = value['items'].map(decodeSummary);
  if (items.some((item) => item === null)) return null;
  const next = value['nextCursor'];
  if (next !== null && next !== undefined && !isString(next)) return null;
  return { items: items as WorkflowRunSummary[], nextCursor: isString(next) ? next : null };
}

/** Each attention code in the creator's words. A code this client does not know still says something true. */
export function attentionReasonText(code: WorkflowAttentionReason): string {
  switch (code) {
    case 'idle':
      return 'No one has touched this in a week.';
    case 'newer_version':
      return 'A newer version of this journey is available.';
    case 'not_yours_to_do':
      return 'Someone with more access needs to do the next step.';
    case 'version_gone':
      return 'The version this started from is no longer available, so its steps cannot be shown. Your work is still here.';
    default:
      return 'This needs a look.';
  }
}
