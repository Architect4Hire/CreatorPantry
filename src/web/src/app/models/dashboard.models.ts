import { AiTaskType, AI_TASK_TYPE_VALUES } from './ai-proposal.models';
import { CONTENT_BOARD_COLUMNS, ContentBoardColumn } from './content-board.models';
import { DamAssetKind, DAM_ASSET_KIND_LABELS } from './dam-asset.models';
import { PUBLISHING_STATUSES, PublishingStatusValue } from './publishing.models';
import { RecipeStatus, decodeEnum, isRecord, isStringOrNull } from './recipe.models';
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
export type DashboardSection<T> =
  | { readonly status: 'ready'; readonly data: T }
  | { readonly status: 'unavailable' };

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

export interface Dashboard {
  readonly generatedAt: string;
  /** The workspace's own zone, or null when it has set none — and then `today` is null too. */
  readonly timeZoneId: string | null;
  /** The workspace's day. Null when it has set no zone: the server will not guess one. */
  readonly today: string | null;
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

  return isId(id) && isStringOrNull(contentCardId) && isStringOrNull(contentCardTitle) && isString(platform) && isString(scheduledAt) && status !== null
    ? { id, contentCardId, contentCardTitle, platform, status, scheduledAt }
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

    return isId(id) && isString(title) && status !== null && isString(updatedAt) ? { id, title, status, updatedAt } : null;
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

    return isId(recipeId) && isString(title) && isCount(blockerCount) && blockers ? { recipeId, title, blockerCount, blockers } : null;
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

    return isId(issueId) && isId(testRunId) && isId(recipeId) && isString(recipeTitle) && isString(title) && severity !== null && isString(testedAt)
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

  return {
    generatedAt,
    timeZoneId,
    today,
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
