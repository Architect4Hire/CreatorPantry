// One person's work task, as the planning routes answer with it. Mirrors `WorkTaskServiceModel` (13A.24–13A.31).

import { isRecord } from './recipe.models';

export type WorkTaskStatus = 'Backlog' | 'InProgress' | 'Waiting' | 'Done';

export type WorkTaskPriority = 'Low' | 'Normal' | 'High';

const STATUSES: readonly WorkTaskStatus[] = ['Backlog', 'InProgress', 'Waiting', 'Done'];
const PRIORITIES: readonly WorkTaskPriority[] = ['Low', 'Normal', 'High'];

export interface WorkTaskMember {
  readonly membershipId: string;
  /** Null for somebody who has since left the workspace. */
  readonly displayName: string | null;
}

export interface WorkTaskLabel {
  readonly id: string;
  readonly name: string;
}

/** What a task is about, by id. Pointers only: the owning feature says what each record is called. */
export interface WorkTaskLinks {
  readonly workflowRunId: string | null;
  readonly workflowStepKey: string | null;
  readonly workflowRunRoute: string | null;
  readonly recipeId: string | null;
  readonly contentCardId: string | null;
  readonly mediaAssetId: string | null;
}

export interface WorkTask {
  readonly id: string;
  readonly title: string;
  readonly description: string | null;
  readonly status: WorkTaskStatus;
  readonly priority: WorkTaskPriority;
  /** A calendar date (`yyyy-MM-dd`) in the workspace's own zone. */
  readonly scheduledDate: string | null;
  /** The Monday of the week it is planned for. */
  readonly planningWeekStart: string | null;
  readonly dueDate: string | null;
  /** Null is unknown, never "no time". */
  readonly estimateMinutes: number | null;
  readonly owner: WorkTaskMember;
  readonly assignee: WorkTaskMember;
  readonly links: WorkTaskLinks;
  readonly labels: readonly WorkTaskLabel[];
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly completedAt: string | null;
  readonly concurrencyToken: string;
}

/**
 * A quick capture (13A.24, 13A.31). Only `title` is required; every other field is sent only when the creator
 * chose it, so a title alone is exactly `{ title }` and the server's defaults apply.
 */
export interface CaptureWorkTaskRequest {
  readonly title: string;
  readonly priority?: WorkTaskPriority;
  readonly scheduledDate?: string;
  readonly planningWeekStart?: string;
  readonly estimateMinutes?: number;
  readonly labelIds?: readonly string[];
  readonly recipeId?: string;
  readonly contentCardId?: string;
  readonly mediaAssetId?: string;
  readonly workflowRunId?: string;
}

/** Today and this week's Monday in the workspace's own zone, as the week route says them (13A.26). */
export interface PlanningDates {
  readonly today: string;
  readonly weekStart: string;
}

/** One unplanned task the day offers, and why (13A.25). An offer: nothing is scheduled until the creator says so. */
export interface SuggestedWorkTask {
  readonly task: WorkTask;
  readonly reason: string;
}

/**
 * One person's day (13A.25, 13A.32). The groups do not overlap — each task is in exactly one — and `topThree`
 * names tasks already in them, by id, in the order the creator put them.
 */
export interface TodayWorkTasks {
  /** The local day this is about, in `timeZoneId`. */
  readonly date: string;
  readonly timeZoneId: string;
  readonly overdue: readonly WorkTask[];
  readonly overdueHasMore: boolean;
  readonly scheduledToday: readonly WorkTask[];
  readonly scheduledTodayHasMore: boolean;
  readonly inProgress: readonly WorkTask[];
  readonly inProgressHasMore: boolean;
  readonly waiting: readonly WorkTask[];
  readonly waitingHasMore: boolean;
  readonly completedToday: readonly WorkTask[];
  readonly completedTodayHasMore: boolean;
  readonly suggested: readonly SuggestedWorkTask[];
  readonly topThree: readonly string[];
}

/**
 * How much work a group of the week holds (13A.26). An unknown stays unknown: a task with no estimate is counted in
 * `taskCount` and nowhere else, and `estimatedMinutes` is null — never zero — when no task has one.
 */
export interface WorkTaskTotals {
  readonly taskCount: number;
  readonly estimatedTaskCount: number;
  readonly estimatedMinutes: number | null;
}

/** One group of the week: its first rows, whether there were more, and what the whole group adds up to. */
export interface WorkTaskGroup {
  readonly tasks: readonly WorkTask[];
  readonly hasMore: boolean;
  readonly totals: WorkTaskTotals;
}

/** One local day of the week: the not-started tasks planned for it, in the person's own order. */
export interface WorkTaskWeekDay extends WorkTaskGroup {
  readonly date: string;
  readonly isToday: boolean;
}

/**
 * One person's week (13A.26, 13A.33). The groups do not overlap, and none is a content-board column. In progress
 * and waiting are the person's now, whatever week is read; `carriedOver` is only ever filled in the current week.
 */
export interface WeekWorkTasks {
  readonly weekStart: string;
  readonly weekEnd: string;
  readonly today: string;
  readonly timeZoneId: string;
  readonly isCurrentWeek: boolean;
  /** The seven days and the backlog: the week's own planned work. */
  readonly planned: WorkTaskTotals;
  /** Monday to Sunday, always seven. */
  readonly days: readonly WorkTaskWeekDay[];
  readonly backlog: WorkTaskGroup;
  readonly unplanned: WorkTaskGroup;
  readonly carriedOver: WorkTaskGroup;
  readonly inProgress: WorkTaskGroup;
  readonly waiting: WorkTaskGroup;
  readonly completed: WorkTaskGroup;
}

/** Where a move sends a task (13A.28). Done is not a destination: a task is completed instead. */
export interface MoveWorkTaskRequest {
  readonly status: 'Backlog' | 'InProgress' | 'Waiting';
  /** A day's list. Only with `Backlog`. */
  readonly scheduledDate?: string;
  /** A week's backlog of no day. Only with `Backlog`. */
  readonly planningWeekStart?: string;
  /** The task it goes immediately before, in the list it is joining; left out, it goes to the bottom (13A.33). */
  readonly beforeTaskId?: string;
}

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

/** A task, or null when the body is not one. */
export function decodeWorkTask(value: unknown): WorkTask | null {
  if (!isRecord(value)) return null;

  const { id, title, status, priority, owner, assignee, links, labels, createdAt, updatedAt, concurrencyToken } = value;

  if (
    typeof id !== 'string' || id === '' ||
    typeof title !== 'string' ||
    typeof status !== 'string' || !(STATUSES as readonly string[]).includes(status) ||
    typeof priority !== 'string' || !(PRIORITIES as readonly string[]).includes(priority) ||
    typeof createdAt !== 'string' || typeof updatedAt !== 'string' ||
    typeof concurrencyToken !== 'string'
  ) {
    return null;
  }

  const ownerRead = decodeMember(owner);
  const assigneeRead = decodeMember(assignee);
  const linksRead = decodeLinks(links);
  const labelsRead = decodeLabels(labels);
  if (!ownerRead || !assigneeRead || !linksRead || !labelsRead) return null;

  const estimate = value['estimateMinutes'];

  return {
    id,
    title,
    description: optionalString(value['description']),
    status: status as WorkTaskStatus,
    priority: priority as WorkTaskPriority,
    scheduledDate: optionalDate(value['scheduledDate']),
    planningWeekStart: optionalDate(value['planningWeekStart']),
    dueDate: optionalDate(value['dueDate']),
    estimateMinutes: typeof estimate === 'number' && Number.isInteger(estimate) ? estimate : null,
    owner: ownerRead,
    assignee: assigneeRead,
    links: linksRead,
    labels: labelsRead,
    createdAt,
    updatedAt,
    completedAt: optionalString(value['completedAt']),
    concurrencyToken,
  };
}

/** A day, or null when the body is not one. A group that is not a list of tasks makes the whole day unreadable. */
export function decodeTodayWorkTasks(value: unknown): TodayWorkTasks | null {
  if (!isRecord(value)) return null;

  const { date, timeZoneId } = value;
  if (typeof date !== 'string' || !ISO_DATE.test(date) || typeof timeZoneId !== 'string') return null;

  const overdue = decodeTasks(value['overdue']);
  const scheduledToday = decodeTasks(value['scheduledToday']);
  const inProgress = decodeTasks(value['inProgress']);
  const waiting = decodeTasks(value['waiting']);
  const completedToday = decodeTasks(value['completedToday']);
  const suggested = decodeSuggestions(value['suggested']);
  if (!overdue || !scheduledToday || !inProgress || !waiting || !completedToday || !suggested) return null;

  // Only ids the day actually holds: a pointer the page cannot draw is no emphasis at all.
  const held = new Set([...overdue, ...scheduledToday, ...inProgress, ...waiting, ...completedToday].map((task) => task.id));
  const chosen = Array.isArray(value['topThree']) ? value['topThree'] : [];
  const topThree = chosen.filter((id): id is string => typeof id === 'string' && held.has(id));

  return {
    date,
    timeZoneId,
    overdue,
    overdueHasMore: value['overdueHasMore'] === true,
    scheduledToday,
    scheduledTodayHasMore: value['scheduledTodayHasMore'] === true,
    inProgress,
    inProgressHasMore: value['inProgressHasMore'] === true,
    waiting,
    waitingHasMore: value['waitingHasMore'] === true,
    completedToday,
    completedTodayHasMore: value['completedTodayHasMore'] === true,
    suggested,
    topThree: [...new Set(topThree)],
  };
}

/** A week, or null when the body is not one. Seven days, every group a list of tasks, or nothing at all. */
export function decodeWeekWorkTasks(value: unknown): WeekWorkTasks | null {
  if (!isRecord(value)) return null;

  const { weekStart, weekEnd, today, timeZoneId } = value;
  if (
    typeof weekStart !== 'string' || !ISO_DATE.test(weekStart) ||
    typeof weekEnd !== 'string' || !ISO_DATE.test(weekEnd) ||
    typeof today !== 'string' || !ISO_DATE.test(today) ||
    typeof timeZoneId !== 'string'
  ) {
    return null;
  }

  const planned = decodeTotals(value['planned']);
  const backlog = decodeGroup(value['backlog']);
  const unplanned = decodeGroup(value['unplanned']);
  const carriedOver = decodeGroup(value['carriedOver']);
  const inProgress = decodeGroup(value['inProgress']);
  const waiting = decodeGroup(value['waiting']);
  const completed = decodeGroup(value['completed']);
  if (!planned || !backlog || !unplanned || !carriedOver || !inProgress || !waiting || !completed) return null;

  const rawDays = value['days'];
  if (!Array.isArray(rawDays) || rawDays.length !== 7) return null;

  const days: WorkTaskWeekDay[] = [];
  for (const raw of rawDays) {
    const group = decodeGroup(raw);
    if (!group || !isRecord(raw) || typeof raw['date'] !== 'string' || !ISO_DATE.test(raw['date'])) return null;
    days.push({ ...group, date: raw['date'], isToday: raw['isToday'] === true });
  }

  return {
    weekStart,
    weekEnd,
    today,
    timeZoneId,
    isCurrentWeek: value['isCurrentWeek'] === true,
    planned,
    days,
    backlog,
    unplanned,
    carriedOver,
    inProgress,
    waiting,
    completed,
  };
}

/** The ids of a top three from its answer, or null when the body is not one. */
export function decodeTopThree(value: unknown): readonly string[] | null {
  if (!isRecord(value) || !Array.isArray(value['taskIds'])) return null;

  const ids = value['taskIds'];
  return ids.every((id) => typeof id === 'string') ? (ids as string[]) : null;
}

/** A move's body: the token it was composed against, the list, and only the day or week that list needs. */
export function encodeMoveWorkTaskRequest(task: WorkTask, request: MoveWorkTaskRequest): Record<string, unknown> {
  return {
    expectedConcurrencyToken: task.concurrencyToken,
    status: request.status,
    ...(request.scheduledDate ? { scheduledDate: request.scheduledDate } : {}),
    ...(request.planningWeekStart ? { planningWeekStart: request.planningWeekStart } : {}),
    ...(request.beforeTaskId ? { beforeTaskId: request.beforeTaskId } : {}),
  };
}

/** Whether `value` is a real calendar date written `yyyy-MM-dd` — not just that shape, so 2038-02-31 is not one. */
export function isCalendarDate(value: string): boolean {
  if (!ISO_DATE.test(value)) return false;

  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));

  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

/** The Monday of the week holding `iso`. A planning week runs Monday to Sunday everywhere (13A.26). */
export function weekStartOf(iso: string): string {
  const [year, month, day] = iso.split('-').map(Number);
  const weekday = new Date(Date.UTC(year, month - 1, day)).getUTCDay();

  return addDays(iso, -((weekday + 6) % 7));
}

/** Today and the week's Monday from a week answer, or null when it does not carry both as dates. */
export function decodePlanningDates(value: unknown): PlanningDates | null {
  if (!isRecord(value)) return null;

  const { today, weekStart } = value;
  return typeof today === 'string' && ISO_DATE.test(today) && typeof weekStart === 'string' && ISO_DATE.test(weekStart)
    ? { today, weekStart }
    : null;
}

/** Drops every field the creator did not choose, so the wire carries only what was asked for. */
export function encodeCaptureWorkTaskRequest(request: CaptureWorkTaskRequest): Record<string, unknown> {
  const body: Record<string, unknown> = { title: request.title };

  for (const [key, value] of Object.entries(request)) {
    if (key === 'title' || value === undefined) continue;
    if (Array.isArray(value) && value.length === 0) continue;
    body[key] = value;
  }

  return body;
}

/** `iso` moved by `days` calendar days. Pure date arithmetic: no zone, no hour, so a clock change cannot shift it. */
export function addDays(iso: string, days: number): string {
  const [year, month, day] = iso.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day + days));

  return date.toISOString().slice(0, 10);
}

function decodeTasks(value: unknown): WorkTask[] | null {
  if (!Array.isArray(value)) return null;

  const tasks: WorkTask[] = [];
  for (const item of value) {
    const task = decodeWorkTask(item);
    if (!task) return null;
    tasks.push(task);
  }

  return tasks;
}

function decodeTotals(value: unknown): WorkTaskTotals | null {
  if (!isRecord(value)) return null;

  const { taskCount, estimatedTaskCount, estimatedMinutes } = value;
  if (!Number.isInteger(taskCount) || !Number.isInteger(estimatedTaskCount)) return null;

  return {
    taskCount: taskCount as number,
    estimatedTaskCount: estimatedTaskCount as number,
    estimatedMinutes: typeof estimatedMinutes === 'number' ? estimatedMinutes : null,
  };
}

function decodeGroup(value: unknown): WorkTaskGroup | null {
  if (!isRecord(value)) return null;

  const tasks = decodeTasks(value['tasks']);
  const totals = decodeTotals(value['totals']);

  return tasks && totals ? { tasks, hasMore: value['hasMore'] === true, totals } : null;
}

function decodeSuggestions(value: unknown): SuggestedWorkTask[] | null {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value)) return null;

  const suggestions: SuggestedWorkTask[] = [];
  for (const item of value) {
    const task = isRecord(item) ? decodeWorkTask(item['task']) : null;
    if (!task || !isRecord(item) || typeof item['reason'] !== 'string') return null;
    suggestions.push({ task, reason: item['reason'] });
  }

  return suggestions;
}

function decodeMember(value: unknown): WorkTaskMember | null {
  if (!isRecord(value) || typeof value['membershipId'] !== 'string') return null;

  return { membershipId: value['membershipId'], displayName: optionalString(value['displayName']) };
}

function decodeLinks(value: unknown): WorkTaskLinks | null {
  if (!isRecord(value)) return null;

  return {
    workflowRunId: optionalString(value['workflowRunId']),
    workflowStepKey: optionalString(value['workflowStepKey']),
    workflowRunRoute: optionalString(value['workflowRunRoute']),
    recipeId: optionalString(value['recipeId']),
    contentCardId: optionalString(value['contentCardId']),
    mediaAssetId: optionalString(value['mediaAssetId']),
  };
}

function decodeLabels(value: unknown): WorkTaskLabel[] | null {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value)) return null;

  const labels: WorkTaskLabel[] = [];
  for (const item of value) {
    if (!isRecord(item) || typeof item['id'] !== 'string' || typeof item['name'] !== 'string') return null;
    labels.push({ id: item['id'], name: item['name'] });
  }

  return labels;
}

function optionalString(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function optionalDate(value: unknown): string | null {
  return typeof value === 'string' && ISO_DATE.test(value) ? value : null;
}
