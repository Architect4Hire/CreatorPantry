// What My Day (13A.32) decides from the server's answer, kept out of the component so each rule can be tested on its
// own. Nothing here counts, sorts or schedules: the order is the server's, and every change is a server command.

import { MoveWorkTaskRequest, TodayWorkTasks, WorkTask, addDays, weekStartOf } from '../../models/work-task.models';

// Shared with My Week; re-exported so the day's rules read from one place.
export type { WorkTaskLink as MyDayLink } from '../../shared/work-tasks/work-task-presentation';
export { estimateWords, linkFor, longDay, shortDay } from '../../shared/work-tasks/work-task-presentation';

export type MyDayGroupKey = 'overdue' | 'scheduledToday' | 'inProgress' | 'waiting' | 'completedToday';

export interface MyDayGroupDefinition {
  readonly key: MyDayGroupKey;
  readonly heading: string;
  /** How a task from this group says where it came from, when it is shown at the top instead. */
  readonly from: string;
}

/** The day's groups, in the order the page shows them: what presses first, what is finished last. */
export const MY_DAY_GROUPS: readonly MyDayGroupDefinition[] = [
  { key: 'overdue', heading: 'Overdue', from: 'Overdue' },
  { key: 'scheduledToday', heading: 'Today', from: 'Planned for today' },
  { key: 'inProgress', heading: 'In progress', from: 'In progress' },
  { key: 'waiting', heading: 'Waiting', from: 'Waiting' },
  { key: 'completedToday', heading: 'Done today', from: 'Done today' },
];

/** One task where the page shows it, and the group it belongs to on the server. */
export interface MyDayEntry {
  readonly task: WorkTask;
  readonly group: MyDayGroupKey;
}

export interface MyDayLayout {
  /**
   * The card at the top: the creator's own top three when they chose one, otherwise the one task to do next. Null
   * when nothing is left to do. Its tasks are taken out of their groups, so every task is on the page once.
   */
  readonly lead: { readonly kind: 'top-three' | 'next'; readonly entries: readonly MyDayEntry[] } | null;
  readonly groups: readonly {
    readonly definition: MyDayGroupDefinition;
    readonly entries: readonly MyDayEntry[];
    readonly hasMore: boolean;
  }[];
  /** Nothing late, planned, under way or waiting: the day has nothing left in it to do. */
  readonly nothingOpen: boolean;
}

/** Where up next comes from, in order. Waiting is never next: something outside the task is holding it. */
const NEXT_FROM: readonly MyDayGroupKey[] = ['overdue', 'inProgress', 'scheduledToday'];

export function layoutDay(day: TodayWorkTasks): MyDayLayout {
  const all: MyDayEntry[] = MY_DAY_GROUPS.flatMap((definition) =>
    day[definition.key].map((task) => ({ task, group: definition.key })),
  );
  const byId = new Map(all.map((entry) => [entry.task.id, entry]));

  const chosen = day.topThree.map((id) => byId.get(id)).filter((entry): entry is MyDayEntry => entry !== undefined);

  let lead: MyDayLayout['lead'] = null;
  if (chosen.length > 0) {
    lead = { kind: 'top-three', entries: chosen };
  } else {
    for (const key of NEXT_FROM) {
      const first = day[key][0];
      if (first) {
        lead = { kind: 'next', entries: [{ task: first, group: key }] };
        break;
      }
    }
  }

  const leading = new Set(lead?.entries.map((entry) => entry.task.id) ?? []);

  return {
    lead,
    groups: MY_DAY_GROUPS.map((definition) => ({
      definition,
      entries: day[definition.key]
        .filter((task) => !leading.has(task.id))
        .map((task) => ({ task, group: definition.key })),
      hasMore: day[`${definition.key}HasMore`],
    })),
    nothingOpen: NEXT_FROM.every((key) => day[key].length === 0) && day.waiting.length === 0,
  };
}

/**
 * The tasks a top three may be chosen from, in the page's order: everything left to do, and a finished one only
 * while it is chosen — so choosing again keeps what was already ticked off without offering the rest of Done today.
 */
export function topThreeCandidates(day: TodayWorkTasks): readonly MyDayEntry[] {
  const chosen = new Set(day.topThree);

  return MY_DAY_GROUPS.flatMap((definition) =>
    day[definition.key]
      .filter((task) => definition.key !== 'completedToday' || chosen.has(task.id))
      .map((task) => ({ task, group: definition.key })),
  );
}

export interface MyDayMove {
  /** Stable, for ids and tests. */
  readonly id: 'in-progress' | 'waiting' | 'not-started' | 'tomorrow' | 'later-this-week' | 'next-week';
  readonly label: string;
  readonly request: MoveWorkTaskRequest;
}

/**
 * The few places a task can go from My Day, by where it stands. A short list on purpose: anything finer — another
 * day, another week, the order in a list — is My Week's.
 */
export function movesFor(task: WorkTask, today: string): readonly MyDayMove[] {
  const tomorrow: MyDayMove = {
    id: 'tomorrow',
    label: 'Tomorrow',
    request: { status: 'Backlog', scheduledDate: addDays(today, 1) },
  };

  switch (task.status) {
    case 'Backlog': {
      const weekStart = weekStartOf(today);
      // On the last day of the week there is no "later" left in it.
      const later: MyDayMove =
        addDays(weekStart, 6) === today
          ? {
              id: 'next-week',
              label: 'Next week',
              request: { status: 'Backlog', planningWeekStart: addDays(weekStart, 7) },
            }
          : {
              id: 'later-this-week',
              label: 'Later this week',
              request: { status: 'Backlog', planningWeekStart: weekStart },
            };

      return [
        { id: 'in-progress', label: 'In progress', request: { status: 'InProgress' } },
        { id: 'waiting', label: 'Waiting', request: { status: 'Waiting' } },
        tomorrow,
        later,
      ];
    }
    case 'InProgress':
      return [
        { id: 'waiting', label: 'Waiting', request: { status: 'Waiting' } },
        { id: 'not-started', label: 'Not started', request: { status: 'Backlog', scheduledDate: today } },
        tomorrow,
      ];
    case 'Waiting':
      return [{ id: 'in-progress', label: 'Back in progress', request: { status: 'InProgress' } }, tomorrow];
    default:
      return [];
  }
}

/**
 * The request that puts a task back where it was before a move: its status, and its day or week when it was not
 * started. In progress and waiting keep the day and week a task already has, so those are not sent with them.
 */
export function moveBack(before: WorkTask): MoveWorkTaskRequest | null {
  switch (before.status) {
    case 'Backlog':
      return before.scheduledDate
        ? { status: 'Backlog', scheduledDate: before.scheduledDate }
        : before.planningWeekStart
          ? { status: 'Backlog', planningWeekStart: before.planningWeekStart }
          : { status: 'Backlog' };
    case 'InProgress':
    case 'Waiting':
      return { status: before.status };
    default:
      return null;
  }
}

/** The group a task is in on this answer, or null when the day no longer shows it. */
export function groupOf(day: TodayWorkTasks, taskId: string): MyDayGroupKey | null {
  return MY_DAY_GROUPS.find((definition) => day[definition.key].some((task) => task.id === taskId))?.key ?? null;
}
