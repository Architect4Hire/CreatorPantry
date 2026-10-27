import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';

/**
 * A guided-workflow run (13A.5–13A.10b) as `GET .../workflow-runs/{runId}` and every command on it answer: where the
 * creator is, what each step's state is, which records the run holds by kind and id, and what to do next. State and
 * pointers only — a run never carries a recipe, a piece of writing or a picture.
 */

export type WorkflowRunStatus = 'InProgress' | 'Completed' | 'Abandoned';
export type WorkflowStepRunStatus = 'NotStarted' | 'InProgress' | 'Completed' | 'Skipped';
export type WorkflowStepRequirement = 'Required' | 'Optional';

const RUN_STATUSES: ReadonlySet<string> = new Set<WorkflowRunStatus>(['InProgress', 'Completed', 'Abandoned']);
const STEP_STATUSES: ReadonlySet<string> = new Set<WorkflowStepRunStatus>([
  'NotStarted',
  'InProgress',
  'Completed',
  'Skipped',
]);
const REQUIREMENTS: ReadonlySet<string> = new Set<WorkflowStepRequirement>(['Required', 'Optional']);

/**
 * A record by kind and id. The kind is kept as the server's word rather than narrowed to a union: the server adds
 * kinds as modules join the workflows, and a client that refused an unknown one would refuse the whole run.
 */
export interface WorkflowResourcePointer {
  readonly kind: string;
  readonly id: string;
}

export interface WorkflowStepRun {
  readonly key: string;
  readonly title: string;
  readonly requirement: WorkflowStepRequirement;
  readonly status: WorkflowStepRunStatus;
  readonly completedAt: string | null;
  readonly skippedAt: string | null;
  readonly skipReason: string | null;
  readonly draft: WorkflowResourcePointer | null;
  readonly draftSavedAt: string | null;
}

export interface WorkflowRunLink extends WorkflowResourcePointer {
  /** The step that made it, or null for the record the run started from. */
  readonly stepKey: string | null;
}

/** One way to do a step: a screen elsewhere in the product, by name and address. */
export interface WorkflowAction {
  readonly capabilityKey: string;
  readonly name: string;
  /** Relative to the workspace (`recipes/42`), or null when it needs a record the run does not hold. */
  readonly route: string | null;
  readonly available: boolean;
  readonly youCanDoIt: boolean;
}

export interface WorkflowNextAction {
  readonly stepKey: string;
  readonly title: string;
  readonly help: string;
  readonly requirement: WorkflowStepRequirement;
  /** The step's main action first, then its alternatives. */
  readonly actions: readonly WorkflowAction[];
  /** What has to be true before the step can be done, each in plain words. */
  readonly waitingFor: readonly string[];
}

export interface WorkflowRunWarning {
  readonly code: string;
  readonly message: string;
}

/**
 * Something to do once this is done: another journey, or one screen on its own — labelled by the target's own goal
 * or name, with the definition's reason.
 */
export interface WorkflowSuggestion {
  readonly kind: 'Workflow' | 'Capability';
  /** The workflow's definition key, or the capability's key. */
  readonly key: string;
  readonly label: string;
  readonly why: string;
  /** A capability's address relative to the workspace; null for a workflow, or for a screen that needs a record the run does not hold. */
  readonly route: string | null;
  /** For a workflow: the record this run holds that the next run can start from, or null to start from nothing. */
  readonly startFrom: WorkflowResourcePointer | null;
  readonly available: boolean;
  readonly youCanDoIt: boolean;
}

export interface WorkflowRun {
  readonly id: string;
  readonly definitionKey: string;
  readonly definitionVersion: number;
  /** The journey's plain-language goal: the shell's heading. */
  readonly goal: string;
  /** What the creator has at the end: the shell's lede. Null from a server that predates it. */
  readonly expectedOutcome: string | null;
  readonly title: string;
  readonly status: WorkflowRunStatus;
  readonly stepCount: number;
  readonly completedStepCount: number;
  readonly skippedStepCount: number;
  readonly startedAt: string;
  readonly updatedAt: string;
  readonly completedAt: string | null;
  readonly abandonedAt: string | null;
  readonly abandonReason: string | null;
  readonly replacedByRunId: string | null;
  readonly lastRoute: string | null;
  readonly concurrencyToken: string;
  readonly steps: readonly WorkflowStepRun[];
  readonly links: readonly WorkflowRunLink[];
  /** The step to do now; null once every step is done or passed over, and once the run is finished or stopped. */
  readonly nextAction: WorkflowNextAction | null;
  readonly resumeRoute: string | null;
  /** Every required step is done: the run can be finished. */
  readonly readyToComplete: boolean;
  readonly warnings: readonly WorkflowRunWarning[];
  /** What to do once this is done, most recommended first. Empty from a server that predates it. */
  readonly nextSuggestions: readonly WorkflowSuggestion[];
}

/** One required step still in the way of finishing, from a refused completion (`incompleteSteps`). */
export interface WorkflowIncompleteStep {
  readonly stepKey: string;
  readonly title: string;
  readonly missing: readonly string[];
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

function isNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function strings(value: unknown): readonly string[] | null {
  return Array.isArray(value) && value.every(isString) ? value : null;
}

/** Each item decoded, or null when any one is not what it should be: a half-read run is not a run. */
function every<T>(value: unknown, decode: (item: unknown) => T | null): readonly T[] | null {
  if (!Array.isArray(value)) return null;
  const decoded: T[] = [];
  for (const item of value) {
    const one = decode(item);
    if (one === null) return null;
    decoded.push(one);
  }
  return decoded;
}

function optionalString(value: unknown): string | null {
  return isString(value) ? value : null;
}

function decodeStep(value: unknown): WorkflowStepRun | null {
  if (!isRecord(value)) return null;
  const requirement = decodeEnum<WorkflowStepRequirement>(REQUIREMENTS, value['requirement']);
  const status = decodeEnum<WorkflowStepRunStatus>(STEP_STATUSES, value['status']);
  if (!isString(value['key']) || !isString(value['title']) || requirement === null || status === null) return null;

  const draftKind = optionalString(value['draftKind']);
  const draftId = optionalString(value['draftResourceId']);

  return {
    key: value['key'],
    title: value['title'],
    requirement,
    status,
    completedAt: optionalString(value['completedAt']),
    skippedAt: optionalString(value['skippedAt']),
    skipReason: optionalString(value['skipReason']),
    draft: draftKind !== null && draftId !== null ? { kind: draftKind, id: draftId } : null,
    draftSavedAt: optionalString(value['draftSavedAt']),
  };
}

function decodeLink(value: unknown): WorkflowRunLink | null {
  if (!isRecord(value) || !isString(value['kind']) || !isString(value['resourceId'])) return null;
  return { kind: value['kind'], id: value['resourceId'], stepKey: optionalString(value['stepKey']) };
}

function decodeAction(value: unknown): WorkflowAction | null {
  if (
    !isRecord(value) ||
    !isString(value['capabilityKey']) ||
    !isString(value['name']) ||
    !isStringOrNull(value['route'] ?? null) ||
    typeof value['available'] !== 'boolean' ||
    typeof value['youCanDoIt'] !== 'boolean'
  ) {
    return null;
  }

  return {
    capabilityKey: value['capabilityKey'],
    name: value['name'],
    route: optionalString(value['route']),
    available: value['available'],
    youCanDoIt: value['youCanDoIt'],
  };
}

function decodeNextAction(value: unknown): WorkflowNextAction | null {
  if (!isRecord(value)) return null;
  const requirement = decodeEnum<WorkflowStepRequirement>(REQUIREMENTS, value['requirement']);
  const actions = every(value['actions'], decodeAction);
  const waitingFor = strings(value['waitingFor']);
  if (
    !isString(value['stepKey']) ||
    !isString(value['title']) ||
    !isString(value['help']) ||
    requirement === null ||
    actions === null ||
    waitingFor === null
  ) {
    return null;
  }

  return { stepKey: value['stepKey'], title: value['title'], help: value['help'], requirement, actions, waitingFor };
}

const SUGGESTION_KINDS: ReadonlySet<string> = new Set<WorkflowSuggestion['kind']>(['Workflow', 'Capability']);

function decodeSuggestion(value: unknown): WorkflowSuggestion | null {
  if (!isRecord(value)) return null;
  const kind = decodeEnum<WorkflowSuggestion['kind']>(SUGGESTION_KINDS, value['kind']);
  if (
    kind === null ||
    !isString(value['key']) ||
    !isString(value['label']) ||
    !isString(value['why']) ||
    typeof value['available'] !== 'boolean' ||
    typeof value['youCanDoIt'] !== 'boolean'
  ) {
    return null;
  }

  const fromKind = optionalString(value['startFromKind']);
  const fromId = optionalString(value['startFromId']);

  return {
    kind,
    key: value['key'],
    label: value['label'],
    why: value['why'],
    route: optionalString(value['route']),
    startFrom: fromKind !== null && fromId !== null ? { kind: fromKind, id: fromId } : null,
    available: value['available'],
    youCanDoIt: value['youCanDoIt'],
  };
}

function decodeWarning(value: unknown): WorkflowRunWarning | null {
  return isRecord(value) && isString(value['code']) && isString(value['message'])
    ? { code: value['code'], message: value['message'] }
    : null;
}

/** A run as the server sent it, or null when it is not one — never a partial run with holes in it. */
export function decodeWorkflowRun(value: unknown): WorkflowRun | null {
  if (!isRecord(value)) return null;

  const status = decodeEnum<WorkflowRunStatus>(RUN_STATUSES, value['status']);
  const steps = every(value['steps'], decodeStep);
  const links = every(value['links'], decodeLink);
  const warnings = value['warnings'] === undefined ? [] : every(value['warnings'], decodeWarning);
  const nextSuggestions =
    value['nextSuggestions'] === undefined ? [] : every(value['nextSuggestions'], decodeSuggestion);
  const rawNext = value['nextAction'];
  const nextAction = rawNext === null || rawNext === undefined ? null : decodeNextAction(rawNext);

  if (
    !isString(value['id']) ||
    !isString(value['definitionKey']) ||
    !isNumber(value['definitionVersion']) ||
    !isString(value['goal']) ||
    !isString(value['title']) ||
    status === null ||
    !isNumber(value['stepCount']) ||
    !isNumber(value['completedStepCount']) ||
    !isNumber(value['skippedStepCount']) ||
    !isString(value['startedAt']) ||
    !isString(value['updatedAt']) ||
    !isString(value['concurrencyToken']) ||
    steps === null ||
    links === null ||
    warnings === null ||
    nextSuggestions === null ||
    (rawNext !== null && rawNext !== undefined && nextAction === null)
  ) {
    return null;
  }

  return {
    id: value['id'],
    definitionKey: value['definitionKey'],
    definitionVersion: value['definitionVersion'],
    goal: value['goal'],
    expectedOutcome: optionalString(value['expectedOutcome']),
    title: value['title'],
    status,
    stepCount: value['stepCount'],
    completedStepCount: value['completedStepCount'],
    skippedStepCount: value['skippedStepCount'],
    startedAt: value['startedAt'],
    updatedAt: value['updatedAt'],
    completedAt: optionalString(value['completedAt']),
    abandonedAt: optionalString(value['abandonedAt']),
    abandonReason: optionalString(value['abandonReason']),
    replacedByRunId: optionalString(value['replacedByRunId']),
    lastRoute: optionalString(value['lastRoute']),
    concurrencyToken: value['concurrencyToken'],
    steps,
    links,
    nextAction,
    resumeRoute: optionalString(value['resumeRoute']),
    readyToComplete: value['readyToComplete'] === true,
    warnings,
    nextSuggestions,
  };
}

/** `missing` from a refused step completion: what the step still needs, in plain words. */
export function decodeMissing(body: unknown): readonly string[] {
  return (isRecord(body) && strings(body['missing'])) || [];
}

/** `incompleteSteps` from a refused run completion. Entries that are not what they should be are left out. */
export function decodeIncompleteSteps(body: unknown): readonly WorkflowIncompleteStep[] {
  if (!isRecord(body) || !Array.isArray(body['incompleteSteps'])) return [];
  return body['incompleteSteps'].flatMap((item: unknown) => {
    if (!isRecord(item) || !isString(item['stepKey']) || !isString(item['title'])) return [];
    return [{ stepKey: item['stepKey'], title: item['title'], missing: strings(item['missing']) ?? [] }];
  });
}

/**
 * Where a run's step stands for the stepper: done, passed over, the one the creator is on, or still to come. The
 * stepper's own vocabulary, so the shell maps once, here.
 */
export type WorkflowStepperStatus = 'upcoming' | 'current' | 'done' | 'skipped';

export function stepperStatusOf(step: WorkflowStepRun, currentKey: string | null): WorkflowStepperStatus {
  if (step.status === 'Completed') return 'done';
  if (step.status === 'Skipped') return 'skipped';
  return step.key === currentKey ? 'current' : 'upcoming';
}
