import { AiTaskType, AI_TASK_TYPE_VALUES } from './ai-proposal.models';
import { CONTENT_BOARD_COLUMNS, ContentBoardColumn } from './content-board.models';
import { DamAssetKind, DAM_ASSET_KIND_LABELS } from './dam-asset.models';
import { PUBLISHING_STATUSES, PublishingStatusValue } from './publishing.models';
import { RecipeStatus, decodeEnum, isRecord, isStringOrNull } from './recipe.models';
import { WorkTaskPriority, WorkTaskStatus } from './work-task.models';
import { TestIssueSeverity, TEST_ISSUE_SEVERITY_VALUES } from './recipe-test-run.models';

/**
 * The workspace dashboard (13.11's read model, `DashboardServiceModel`), decoded section by section.
 *
 * **Each section stands alone.** The server marks a section it could not read `Unavailable`; a section whose
 * data this client cannot read is treated the same way, so one unexpected shape costs one card, never the page.
 *
 * **Every number is the server's.** Nothing here adds, subtracts or derives a count: what the page shows is
 * what came back.
 */
export type DashboardSection<T> = { readonly status: 'ready'; readonly data: T } | { readonly status: 'unavailable' };

export interface DashboardStatusCount<S extends string> {
  readonly status: S;
  readonly count: number;
}

export interface DashboardRecipe {
  readonly id: string;
  readonly title: string;
  readonly status: RecipeStatus;
  readonly updatedAt: string;
}

export interface DashboardRecipesInProgress {
  /** Every in-progress state, in the order work moves through them, zero included. */
  readonly counts: readonly DashboardStatusCount<RecipeStatus>[];
  readonly recent: readonly DashboardRecipe[];
}

export interface DashboardBlocker {
  readonly ruleId: string;
  readonly summary: string;
}

export interface DashboardBlockedRecipe {
  readonly recipeId: string;
  readonly title: string;
  /** Every blocker; `blockers` names the first few. */
  readonly blockerCount: number;
  readonly blockers: readonly DashboardBlocker[];
}

export interface DashboardReadiness {
  /** How many of the listed in-progress recipes were checked — never more than are listed. */
  readonly evaluated: number;
  /** How many of those could not be checked just now. */
  readonly unevaluated: number;
  readonly blocked: readonly DashboardBlockedRecipe[];
}

export interface DashboardAiProposal {
  readonly operationId: string;
  readonly taskType: AiTaskType;
  readonly recipeId: string | null;
  readonly recipeTitle: string | null;
  readonly proposedAt: string;
}

export interface DashboardAiProposals {
  readonly awaitingDecision: number;
  readonly recent: readonly DashboardAiProposal[];
}

export interface DashboardTestFollowUp {
  readonly issueId: string;
  readonly testRunId: string;
  readonly recipeId: string;
  readonly recipeTitle: string;
  readonly title: string;
  readonly severity: TestIssueSeverity;
  readonly testedAt: string;
}

export interface DashboardTestFollowUps {
  readonly open: number;
  readonly blocking: number;
  readonly major: number;
  readonly minor: number;
  readonly recent: readonly DashboardTestFollowUp[];
}

export interface DashboardUpcomingCard {
  readonly id: string;
  readonly title: string;
  readonly column: ContentBoardColumn;
  /** `yyyy-MM-dd`, a day in the workspace's own zone. */
  readonly plannedDate: string;
  readonly channelKey: string | null;
}

export interface DashboardPublishingItem {
  readonly id: string;
  readonly contentCardId: string | null;
  readonly contentCardTitle: string | null;
  readonly platform: string;
  readonly status: PublishingStatusValue;
  readonly scheduledAt: string;
}

export interface DashboardUpcoming {
  readonly from: string;
  readonly through: string;
  readonly cardCount: number;
  readonly cards: readonly DashboardUpcomingCard[];
  readonly postCount: number;
  readonly posts: readonly DashboardPublishingItem[];
}

export interface DashboardBoard {
  readonly total: number;
  readonly columns: readonly { readonly column: ContentBoardColumn; readonly count: number }[];
}

/**
 * One guided workflow run as the dashboard names it (13A.33a). The run itself is the deep link: opening it
 * resumes on the step it is on, which is why only that step's name travels here.
 */
export interface DashboardWorkflowRun {
  readonly id: string;
  readonly title: string;
  readonly definitionKey: string;
  /** What the workflow is for, from the version the run is pinned to. */
  readonly goal: string;
  /** The step it is on, or null when every step is done and only finishing is left. */
  readonly stepKey: string | null;
  readonly stepTitle: string | null;
  /** Whether that step is this reader's to take. False means someone else's to carry on. */
  readonly youCanDoIt: boolean;
  readonly updatedAt: string;
  /** Why it needs a look: `idle`, `newer_version`, `not_yours_to_do`. Empty when nothing does. */
  readonly attentionReasons: readonly string[];
}

/** The reader's own guided workflows: what to carry on with, and what has stopped. */
export interface DashboardWorkflows {
  readonly inProgressCount: number;
  readonly continue: readonly DashboardWorkflowRun[];
  readonly needsAttentionCount: number;
  readonly needsAttention: readonly DashboardWorkflowRun[];
}

/** One of the reader's own tasks, as the dashboard names it. The title is their own words. */
export interface DashboardTask {
  readonly id: string;
  readonly title: string;
  readonly status: WorkTaskStatus;
  readonly priority: WorkTaskPriority;
  readonly dueDate: string | null;
  readonly scheduledDate: string | null;
}

/**
 * The week holding today, as totals only. `estimatedMinutes` is null when nothing planned carries an
 * estimate: zero would claim the work takes no time.
 */
export interface DashboardWorkWeek {
  readonly weekStart: string;
  readonly weekEnd: string;
  readonly plannedTaskCount: number;
  readonly estimatedTaskCount: number;
  readonly estimatedMinutes: number | null;
  readonly inProgressCount: number;
  readonly waitingCount: number;
  readonly completedCount: number;
}

/**
 * The reader's own work plan (13A.33a): what is late, what today holds, what the week adds up to, and what
 * has been captured and not yet planned.
 */
export interface DashboardWorkPlan {
  /** The local day this is about, in `timeZoneId`. */
  readonly date: string;
  readonly timeZoneId: string;
  readonly overdueCount: number;
  readonly overdue: readonly DashboardTask[];
  readonly todayCount: number;
  readonly today: readonly DashboardTask[];
  /** Today's chosen top three, by id and in their order — only tasks listed above. */
  readonly topThree: readonly string[];
  readonly thisWeek: DashboardWorkWeek;
  readonly unplannedCount: number;
}

export interface DashboardUpload {
  readonly id: string;
  readonly title: string;
  readonly kind: DamAssetKind | null;
  readonly createdAt: string;
}

export interface DashboardPublishing {
  readonly counts: readonly DashboardStatusCount<PublishingStatusValue>[];
  readonly needsAttentionCount: number;
  readonly needsAttention: readonly DashboardPublishingItem[];
}

/**
 * Why there is no day to work in, which is not one situation but three. `NotSet` is the creator's to fix in
 * Brand settings; `Unreadable` says nothing about whether a zone is set, so a page must not send that creator
 * off to set one that may already be there. An unknown value from a newer server reads as `Unreadable`, which
 * claims the least.
 */
export type DashboardTimeZoneState = 'Set' | 'NotSet' | 'Unreadable';

export interface Dashboard {
  readonly generatedAt: string;
  /** The workspace's own zone, or null when it is not set and when it could not be read. */
  readonly timeZoneId: string | null;
  /** The workspace's day. Null without a usable zone: the server will not guess one. */
  readonly today: string | null;
  /** Which of the three answers `timeZoneId` being null is. */
  readonly timeZoneState: DashboardTimeZoneState;
  /** The caller's own runs — not the workspace's. Keyed on their membership and their role. */
  readonly workflows: DashboardSection<DashboardWorkflows>;
  /** The caller's own tasks. Unavailable without a zone: late, today and this week all need a day. */
  readonly workPlan: DashboardSection<DashboardWorkPlan>;
  readonly recipesInProgress: DashboardSection<DashboardRecipesInProgress>;
  readonly readinessBlockers: DashboardSection<DashboardReadiness>;
  readonly aiProposals: DashboardSection<DashboardAiProposals>;
  readonly testFollowUps: DashboardSection<DashboardTestFollowUps>;
  readonly upcoming: DashboardSection<DashboardUpcoming>;
  readonly board: DashboardSection<DashboardBoard>;
  readonly recentUploads: DashboardSection<DashboardUpload[]>;
  readonly publishing: DashboardSection<DashboardPublishing>;
}

/** Words for each kind of AI request, as a creator would say what it was for. */
export const AI_TASK_TYPE_LABELS: Readonly<Partial<Record<AiTaskType, string>>> = {
  RecipeConcepts: 'Recipe ideas',
  RecipeFirstDraft: 'First draft',
  RecipeRevision: 'Revision',
  IngredientSubstitution: 'Substitution',
  RecipeAdaptation: 'Adaptation',
  RecipeReview: 'Review',
  ProposalExplanation: 'Explanation',
  EditorialPackage: 'Editorial package',
  SeoPackage: 'SEO package',
  BrandGuideProposal: 'Brand guide',
  PhotographyConcept: 'Photography concepts',
  ImagePrompt: 'Image prompt',
  BrandStyleTestDrive: 'Style test drive',
  ReferenceImageAnalysis: 'Reference picture',
};

export function aiTaskLabel(taskType: AiTaskType): string {
  return AI_TASK_TYPE_LABELS[taskType] ?? 'AI request';
}

// ---- Decoding ----

const RECIPE_STATUSES: ReadonlySet<string> = new Set<RecipeStatus>([
  'Draft',
  'InDevelopment',
  'Testing',
  'ReadyForReview',
  'Approved',
  'Archived',
]);
const COLUMNS: ReadonlySet<string> = new Set<ContentBoardColumn>(CONTENT_BOARD_COLUMNS);
const PUBLISHING: ReadonlySet<string> = new Set<string>(PUBLISHING_STATUSES);
const KINDS: ReadonlySet<string> = new Set(Object.keys(DAM_ASSET_KIND_LABELS));

const isString = (value: unknown): value is string => typeof value === 'string';
const isId = (value: unknown): value is string => typeof value === 'string' && value !== '';
const isCount = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 0;

/** Every element or none: a list with one unreadable row is unreadable, rather than silently shorter. */
function list<T>(raw: unknown, decode: (item: unknown) => T | null): T[] | null {
  if (!Array.isArray(raw)) return null;

  const out: T[] = [];
  for (const item of raw) {
    const decoded = decode(item);
    if (decoded === null) return null;
    out.push(decoded);
  }

  return out;
}

function section<T>(raw: unknown, decode: (data: unknown) => T | null): DashboardSection<T> {
  if (!isRecord(raw) || raw['status'] !== 'Ready') return { status: 'unavailable' };

  const data = decode(raw['data']);

  return data === null ? { status: 'unavailable' } : { status: 'ready', data };
}

/** A publishing state this build does not know is said to be unrecognised, not guessed at. */
function publishingStatus(raw: unknown): PublishingStatusValue | null {
  if (!isString(raw)) return null;

  return PUBLISHING.has(raw) ? (raw as PublishingStatusValue) : 'Unrecognised';
}

function publishingItem(raw: unknown): DashboardPublishingItem | null {
  if (!isRecord(raw)) return null;

  const { id, contentCardId, contentCardTitle, platform, scheduledAt } = raw;
  const status = publishingStatus(raw['status']);

  return isId(id) &&
    isStringOrNull(contentCardId) &&
    isStringOrNull(contentCardTitle) &&
    isString(platform) &&
    isString(scheduledAt) &&
    status !== null
    ? { id, contentCardId, contentCardTitle, platform, status, scheduledAt }
    : null;
}

const TASK_STATUSES: ReadonlySet<string> = new Set<WorkTaskStatus>(['Backlog', 'InProgress', 'Waiting', 'Done']);
const TASK_PRIORITIES: ReadonlySet<string> = new Set<WorkTaskPriority>(['Low', 'Normal', 'High']);

function workflowRun(raw: unknown): DashboardWorkflowRun | null {
  if (!isRecord(raw) || typeof raw['youCanDoIt'] !== 'boolean') return null;

  const { id, title, definitionKey, goal, stepKey, stepTitle, updatedAt } = raw;
  const reasons = list(raw['attentionReasons'], (reason) => (isString(reason) ? reason : null));

  return isId(id) &&
    isString(title) &&
    isString(definitionKey) &&
    isString(goal) &&
    isStringOrNull(stepKey) &&
    isStringOrNull(stepTitle) &&
    isString(updatedAt) &&
    reasons
    ? {
        id,
        title,
        definitionKey,
        goal,
        stepKey,
        stepTitle,
        youCanDoIt: raw['youCanDoIt'],
        updatedAt,
        attentionReasons: reasons,
      }
    : null;
}

function workflows(raw: unknown): DashboardWorkflows | null {
  if (!isRecord(raw) || !isCount(raw['inProgressCount']) || !isCount(raw['needsAttentionCount'])) return null;

  const carryOn = list(raw['continue'], workflowRun);
  const needsAttention = list(raw['needsAttention'], workflowRun);

  return carryOn && needsAttention
    ? {
        inProgressCount: raw['inProgressCount'],
        continue: carryOn,
        needsAttentionCount: raw['needsAttentionCount'],
        needsAttention,
      }
    : null;
}

function task(raw: unknown): DashboardTask | null {
  if (!isRecord(raw)) return null;

  const status = decodeEnum<WorkTaskStatus>(TASK_STATUSES, raw['status']);
  const priority = decodeEnum<WorkTaskPriority>(TASK_PRIORITIES, raw['priority']);
  const { id, title, dueDate, scheduledDate } = raw;

  return isId(id) &&
    isString(title) &&
    status !== null &&
    priority !== null &&
    isStringOrNull(dueDate) &&
    isStringOrNull(scheduledDate)
    ? { id, title, status, priority, dueDate, scheduledDate }
    : null;
}

function workWeek(raw: unknown): DashboardWorkWeek | null {
  if (!isRecord(raw)) return null;

  const {
    weekStart,
    weekEnd,
    plannedTaskCount,
    estimatedTaskCount,
    estimatedMinutes,
    inProgressCount,
    waitingCount,
    completedCount,
  } = raw;
  const minutes =
    estimatedMinutes === null || isCount(estimatedMinutes) ? (estimatedMinutes as number | null) : undefined;

  return isString(weekStart) &&
    isString(weekEnd) &&
    isCount(plannedTaskCount) &&
    isCount(estimatedTaskCount) &&
    minutes !== undefined &&
    isCount(inProgressCount) &&
    isCount(waitingCount) &&
    isCount(completedCount)
    ? {
        weekStart,
        weekEnd,
        plannedTaskCount,
        estimatedTaskCount,
        estimatedMinutes: minutes,
        inProgressCount,
        waitingCount,
        completedCount,
      }
    : null;
}

function workPlan(raw: unknown): DashboardWorkPlan | null {
  if (!isRecord(raw) || !isCount(raw['overdueCount']) || !isCount(raw['todayCount']) || !isCount(raw['unplannedCount']))
    return null;

  const { date, timeZoneId } = raw;
  const overdue = list(raw['overdue'], task);
  const todayTasks = list(raw['today'], task);
  const topThree = list(raw['topThree'], (id) => (isId(id) ? id : null));
  const thisWeek = workWeek(raw['thisWeek']);

  return isString(date) && isString(timeZoneId) && overdue && todayTasks && topThree && thisWeek
    ? {
        date,
        timeZoneId,
        overdueCount: raw['overdueCount'],
        overdue,
        todayCount: raw['todayCount'],
        today: todayTasks,
        topThree,
        thisWeek,
        unplannedCount: raw['unplannedCount'],
      }
    : null;
}

function recipesInProgress(raw: unknown): DashboardRecipesInProgress | null {
  if (!isRecord(raw)) return null;

  const counts = list(raw['counts'], (item) => {
    if (!isRecord(item)) return null;
    const status = decodeEnum<RecipeStatus>(RECIPE_STATUSES, item['status']);

    return status !== null && isCount(item['count']) ? { status, count: item['count'] } : null;
  });
  const recent = list(raw['recent'], (item) => {
    if (!isRecord(item)) return null;
    const status = decodeEnum<RecipeStatus>(RECIPE_STATUSES, item['status']);
    const { id, title, updatedAt } = item;

    return isId(id) && isString(title) && status !== null && isString(updatedAt)
      ? { id, title, status, updatedAt }
      : null;
  });

  return counts && recent ? { counts, recent } : null;
}

function readiness(raw: unknown): DashboardReadiness | null {
  if (!isRecord(raw) || !isCount(raw['evaluated']) || !isCount(raw['unevaluated'])) return null;

  const blocked = list(raw['blocked'], (item) => {
    if (!isRecord(item)) return null;
    const blockers = list(item['blockers'], (blocker) =>
      isRecord(blocker) && isString(blocker['ruleId']) && isString(blocker['summary'])
        ? { ruleId: blocker['ruleId'], summary: blocker['summary'] }
        : null,
    );
    const { recipeId, title, blockerCount } = item;

    return isId(recipeId) && isString(title) && isCount(blockerCount) && blockers
      ? { recipeId, title, blockerCount, blockers }
      : null;
  });

  return blocked ? { evaluated: raw['evaluated'], unevaluated: raw['unevaluated'], blocked } : null;
}

function aiProposals(raw: unknown): DashboardAiProposals | null {
  if (!isRecord(raw) || !isCount(raw['awaitingDecision'])) return null;

  const recent = list(raw['recent'], (item) => {
    if (!isRecord(item)) return null;
    const taskType = decodeEnum<AiTaskType>(AI_TASK_TYPE_VALUES, item['taskType']) ?? 'Unspecified';
    const { operationId, recipeId, recipeTitle, proposedAt } = item;

    return isId(operationId) && isStringOrNull(recipeId) && isStringOrNull(recipeTitle) && isString(proposedAt)
      ? { operationId, taskType, recipeId, recipeTitle, proposedAt }
      : null;
  });

  return recent ? { awaitingDecision: raw['awaitingDecision'], recent } : null;
}

function testFollowUps(raw: unknown): DashboardTestFollowUps | null {
  if (!isRecord(raw)) return null;

  const { open, blocking, major, minor } = raw;
  const recent = list(raw['recent'], (item) => {
    if (!isRecord(item)) return null;
    const severity = decodeEnum<TestIssueSeverity>(TEST_ISSUE_SEVERITY_VALUES, item['severity']);
    const { issueId, testRunId, recipeId, recipeTitle, title, testedAt } = item;

    return isId(issueId) &&
      isId(testRunId) &&
      isId(recipeId) &&
      isString(recipeTitle) &&
      isString(title) &&
      severity !== null &&
      isString(testedAt)
      ? { issueId, testRunId, recipeId, recipeTitle, title, severity, testedAt }
      : null;
  });

  return isCount(open) && isCount(blocking) && isCount(major) && isCount(minor) && recent
    ? { open, blocking, major, minor, recent }
    : null;
}

function upcoming(raw: unknown): DashboardUpcoming | null {
  if (!isRecord(raw)) return null;

  const { from, through, cardCount, postCount } = raw;
  const cards = list(raw['cards'], (item) => {
    if (!isRecord(item)) return null;
    const column = decodeEnum<ContentBoardColumn>(COLUMNS, item['column']);
    const { id, title, plannedDate, channelKey } = item;

    return isId(id) && isString(title) && column !== null && isString(plannedDate) && isStringOrNull(channelKey)
      ? { id, title, column, plannedDate, channelKey }
      : null;
  });
  const posts = list(raw['posts'], publishingItem);

  return isString(from) && isString(through) && isCount(cardCount) && isCount(postCount) && cards && posts
    ? { from, through, cardCount, cards, postCount, posts }
    : null;
}

function board(raw: unknown): DashboardBoard | null {
  if (!isRecord(raw) || !isCount(raw['total'])) return null;

  const columns = list(raw['columns'], (item) => {
    if (!isRecord(item)) return null;
    const column = decodeEnum<ContentBoardColumn>(COLUMNS, item['column']);

    return column !== null && isCount(item['count']) ? { column, count: item['count'] } : null;
  });

  return columns ? { total: raw['total'], columns } : null;
}

function uploads(raw: unknown): DashboardUpload[] | null {
  if (!isRecord(raw)) return null;

  return list(raw['items'], (item) => {
    if (!isRecord(item)) return null;
    const { id, title, createdAt } = item;
    const kind = decodeEnum<DamAssetKind>(KINDS, item['kind']);

    return isId(id) && isString(title) && isString(createdAt) ? { id, title, kind, createdAt } : null;
  });
}

function publishing(raw: unknown): DashboardPublishing | null {
  if (!isRecord(raw) || !isCount(raw['needsAttentionCount'])) return null;

  const counts = list(raw['counts'], (item) => {
    if (!isRecord(item)) return null;
    const status = publishingStatus(item['status']);

    return status !== null && isCount(item['count']) ? { status, count: item['count'] } : null;
  });
  const needsAttention = list(raw['needsAttention'], publishingItem);

  return counts && needsAttention ? { counts, needsAttentionCount: raw['needsAttentionCount'], needsAttention } : null;
}

/** Null only when the envelope itself is unreadable; an unreadable section is that section's alone. */
export function decodeDashboard(raw: unknown): Dashboard | null {
  if (!isRecord(raw)) return null;

  const { generatedAt, timeZoneId, today } = raw;
  if (!isString(generatedAt) || !isStringOrNull(timeZoneId) || !isStringOrNull(today)) return null;

  // A state this build does not know claims the least: not that a zone is missing, which would send the
  // creator to set one they may already have.
  const state = raw['timeZoneState'];
  const timeZoneState: DashboardTimeZoneState =
    state === 'Set' || state === 'NotSet' || state === 'Unreadable' ? state : 'Unreadable';

  return {
    generatedAt,
    timeZoneId,
    today,
    timeZoneState,
    workflows: section(raw['workflows'], workflows),
    workPlan: section(raw['workPlan'], workPlan),
    recipesInProgress: section(raw['recipesInProgress'], recipesInProgress),
    readinessBlockers: section(raw['readinessBlockers'], readiness),
    aiProposals: section(raw['aiProposals'], aiProposals),
    testFollowUps: section(raw['testFollowUps'], testFollowUps),
    upcoming: section(raw['upcoming'], upcoming),
    board: section(raw['board'], board),
    recentUploads: section(raw['recentUploads'], uploads),
    publishing: section(raw['publishing'], publishing),
  };
}
