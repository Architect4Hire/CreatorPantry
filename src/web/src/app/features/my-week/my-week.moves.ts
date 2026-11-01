// Moving a task on My Week (13A.33, turns 2–5): which list it is in, where it may go, what request that is, and how it
// looks before the server answers. The server decides whether a move happens and where the task ends up; this only
// says what was asked, and shows it at once so the board answers the hand that moved it.

import { MoveWorkTaskRequest, WeekWorkTasks, WorkTask, WorkTaskGroup, addDays } from '../../models/work-task.models';
import { shortDay } from '../../shared/work-tasks/work-task-presentation';

/** One list on the board — one of the server's groups. The ids are the sections' own. */
export type MyWeekListId =
  'backlog' | 'carried-over' | 'unplanned' | 'in-progress' | 'waiting' | 'done' | `day-${string}`;

/** Where a task can be sent: a list on the board, or next week's backlog, which is not on it. */
export type MyWeekDestination = MyWeekListId | 'next-week';

export interface MyWeekPlace {
  readonly list: MyWeekDestination;
  /** The task it goes immediately before; null for the bottom. Ignored by Done and next week. */
  readonly beforeTaskId: string | null;
}

/** What one placement asks of the server. */
export type MyWeekCommand =
  | { readonly kind: 'none' }
  | { readonly kind: 'reorder'; readonly beforeTaskId: string | null }
  | { readonly kind: 'move'; readonly request: MoveWorkTaskRequest }
  | { readonly kind: 'complete' };

/**
 * Lists a task may be dropped into from another. "From earlier weeks" and "No week yet" are where tasks are found,
 * not where they are sent: a task can be reordered inside one, but nothing is moved into either.
 */
export function acceptsFromElsewhere(list: MyWeekListId): boolean {
  return list !== 'carried-over' && list !== 'unplanned';
}

/** The list a task is in on this week, or null when the week does not show it. */
export function listOf(week: WeekWorkTasks, taskId: string): MyWeekListId | null {
  for (const [list, group] of groups(week)) {
    if (group.tasks.some((task) => task.id === taskId)) return list;
  }

  return null;
}

/** The tasks the server returned for one list, in its order. */
export function tasksIn(week: WeekWorkTasks, list: MyWeekListId): readonly WorkTask[] {
  return groups(week).find(([id]) => id === list)?.[1].tasks ?? [];
}

/** Every place a task can be sent from the keyboard, named as a creator would say it. */
export function destinationsFor(
  week: WeekWorkTasks,
): readonly { readonly value: MyWeekDestination; readonly label: string }[] {
  return [
    { value: 'backlog', label: 'Backlog — this week, without a day' },
    ...week.days.map((day) => ({
      value: `day-${day.date}` as MyWeekDestination,
      label: day.isToday ? `${shortDay(day.date)} (today)` : shortDay(day.date),
    })),
    { value: 'in-progress', label: 'In progress' },
    { value: 'waiting', label: 'Waiting' },
    { value: 'done', label: 'Done — marks it finished' },
    { value: 'next-week', label: `Next week’s backlog (week of ${shortDay(addDays(week.weekStart, 7))})` },
  ];
}

/**
 * The places in one list a task can take: before each task already there, or at the bottom. Done and next week
 * have no places to offer — finished work is newest first, and next week is not on the board.
 */
export function placesIn(
  week: WeekWorkTasks,
  list: MyWeekDestination,
  movingId: string,
): readonly { readonly value: string; readonly label: string }[] {
  if (list === 'done' || list === 'next-week') return [];

  const others = tasksIn(week, list).filter((task) => task.id !== movingId);

  return [
    ...others.map((task, index) => ({
      value: task.id,
      label: index === 0 ? `At the top, before “${task.title}”` : `Before “${task.title}”`,
    })),
    { value: '', label: others.length === 0 ? 'The only one there' : 'At the bottom' },
  ];
}

/** Where a task is now: its list and the task after it, so the same place can be asked for again. */
export function placeOf(week: WeekWorkTasks, taskId: string): MyWeekPlace | null {
  const list = listOf(week, taskId);
  if (!list) return null;

  const tasks = tasksIn(week, list);
  const index = tasks.findIndex((task) => task.id === taskId);

  return { list, beforeTaskId: tasks[index + 1]?.id ?? null };
}

/**
 * What placing `task` at `place` asks of the server. `original` is the task as it was before an earlier move, for
 * Undo to send it back to the week it came from. The same list is a reorder — the server refuses a "move" to
 * the list a task is already in — a different one is a move, and Done is a completion.
 */
export function commandFor(
  week: WeekWorkTasks,
  task: WorkTask,
  place: MyWeekPlace,
  original: WorkTask = task,
): MyWeekCommand {
  const from = listOf(week, task.id);
  if (task.status === 'Done') return { kind: 'none' };
  if (place.list === 'done') return { kind: 'complete' };

  if (place.list === from) {
    const current = placeOf(week, task.id);
    return current && current.beforeTaskId === place.beforeTaskId
      ? { kind: 'none' }
      : { kind: 'reorder', beforeTaskId: place.beforeTaskId };
  }

  const before = place.beforeTaskId ? { beforeTaskId: place.beforeTaskId } : {};

  switch (place.list) {
    case 'next-week':
      return { kind: 'move', request: { status: 'Backlog', planningWeekStart: addDays(week.weekStart, 7) } };
    case 'backlog':
      return { kind: 'move', request: { status: 'Backlog', planningWeekStart: week.weekStart, ...before } };
    case 'in-progress':
      return { kind: 'move', request: { status: 'InProgress', ...before } };
    case 'waiting':
      return { kind: 'move', request: { status: 'Waiting', ...before } };
    case 'carried-over':
      // Only ever asked for by Undo: back to the week it was planned for, which is the task's own.
      return original.planningWeekStart && original.planningWeekStart !== week.weekStart
        ? { kind: 'move', request: { status: 'Backlog', planningWeekStart: original.planningWeekStart, ...before } }
        : { kind: 'none' };
    case 'unplanned':
      // A move cannot take a task out of every week, so this is not offered and not undone.
      return { kind: 'none' };
    default:
      return {
        kind: 'move',
        request: { status: 'Backlog', scheduledDate: place.list.slice('day-'.length), ...before },
      };
  }
}

/**
 * The week as it will look if the server agrees: the task out of the list it was in and into the place asked for,
 * saying what it now is. Only the cards move — every total stays the server's until the week is read again.
 */
export function applyPlace(
  week: WeekWorkTasks,
  task: WorkTask,
  place: MyWeekPlace,
  original: WorkTask = task,
): WeekWorkTasks {
  const moved = movedTask(week, task, place, original);
  const without = (group: WorkTaskGroup): WorkTaskGroup => ({
    ...group,
    tasks: group.tasks.filter((each) => each.id !== task.id),
  });

  const into = (list: MyWeekListId, group: WorkTaskGroup): WorkTaskGroup => {
    const rest = without(group);
    if (place.list !== list || moved === null) return rest;

    // Done is newest first, so a task just finished is at its top.
    if (list === 'done') return { ...rest, tasks: [moved, ...rest.tasks] };

    const index = place.beforeTaskId ? rest.tasks.findIndex((each) => each.id === place.beforeTaskId) : -1;
    const tasks = [...rest.tasks];
    tasks.splice(index < 0 ? tasks.length : index, 0, moved);

    return { ...rest, tasks };
  };

  return {
    ...week,
    days: week.days.map((day) => ({ ...day, ...into(`day-${day.date}`, day) })),
    backlog: into('backlog', week.backlog),
    carriedOver: into('carried-over', week.carriedOver),
    unplanned: into('unplanned', week.unplanned),
    inProgress: into('in-progress', week.inProgress),
    waiting: into('waiting', week.waiting),
    completed: into('done', week.completed),
  };
}

/** What a destination is called in a sentence: "Thursday 14 Oct", "In progress", "next week's backlog". */
export function destinationWords(week: WeekWorkTasks, list: MyWeekDestination): string {
  switch (list) {
    case 'backlog':
      return 'the backlog';
    case 'carried-over':
      return 'From earlier weeks';
    case 'unplanned':
      return 'No week yet';
    case 'in-progress':
      return 'In progress';
    case 'waiting':
      return 'Waiting';
    case 'done':
      return 'Done';
    case 'next-week':
      return 'next week’s backlog';
    default:
      return shortDay(list.slice('day-'.length)) + (week.today === list.slice('day-'.length) ? ' (today)' : '');
  }
}

function movedTask(week: WeekWorkTasks, task: WorkTask, place: MyWeekPlace, original: WorkTask): WorkTask | null {
  switch (place.list) {
    case 'next-week':
      return null;
    case 'done':
      return { ...task, status: 'Done' };
    case 'in-progress':
      return { ...task, status: 'InProgress' };
    case 'waiting':
      return { ...task, status: 'Waiting' };
    case 'backlog':
      return { ...task, status: 'Backlog', scheduledDate: null, planningWeekStart: week.weekStart };
    case 'carried-over':
      return { ...task, status: 'Backlog', scheduledDate: null, planningWeekStart: original.planningWeekStart };
    case 'unplanned':
      return task;
    default: {
      const date = place.list.slice('day-'.length);
      return { ...task, status: 'Backlog', scheduledDate: date, planningWeekStart: week.weekStart };
    }
  }
}

function groups(week: WeekWorkTasks): readonly (readonly [MyWeekListId, WorkTaskGroup])[] {
  return [
    ['backlog', week.backlog],
    ['carried-over', week.carriedOver],
    ['unplanned', week.unplanned],
    ...week.days.map((day) => [`day-${day.date}` as MyWeekListId, day] as const),
    ['in-progress', week.inProgress],
    ['waiting', week.waiting],
    ['done', week.completed],
  ];
}
