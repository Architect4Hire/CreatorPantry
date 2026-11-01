// What My Week (13A.33) lays out from the server's answer, kept out of the component so each rule can be tested on
// its own. The order and every count are the server's; the board only groups what it was given into columns.

import { WeekWorkTasks, WorkTask, WorkTaskGroup, WorkTaskTotals } from '../../models/work-task.models';
import { estimateWords, shortDay } from '../../shared/work-tasks/work-task-presentation';

/** Compact: the days as sections of one "This week" column. Days: each day a column of its own. */
export type MyWeekView = 'compact' | 'days';

export interface MyWeekFilters {
  /** Words the title must contain, as typed. Empty is no filter. */
  readonly query: string;
  readonly highOnly: boolean;
  /** A workspace tag the task must carry, by id, or null. */
  readonly labelId: string | null;
}

export const NO_FILTERS: MyWeekFilters = { query: '', highOnly: false, labelId: null };

/** One list inside a column: the column itself when it has one, or one of its named parts. */
export interface MyWeekSection {
  /** Stable, for ids and tests. */
  readonly id: string;
  /** Null when the section is the whole column and the column's heading names it. */
  readonly heading: string | null;
  /** A word beside the heading that says more than a colour could: "Today". */
  readonly marker: string | null;
  /** The tasks the filters leave, in the server's order. */
  readonly tasks: readonly WorkTask[];
  /** How many the server returned for this list, before any filter. */
  readonly loaded: number;
  readonly hasMore: boolean;
  readonly totals: WorkTaskTotals;
  /** What an empty list says, in its own words. */
  readonly empty: string;
}

export interface MyWeekColumn {
  readonly id: string;
  readonly heading: string;
  /** The server's count for everything in the column, loaded or not. */
  readonly count: number;
  readonly sections: readonly MyWeekSection[];
}

export function hasFilters(filters: MyWeekFilters): boolean {
  return filters.query.trim() !== '' || filters.highOnly || filters.labelId !== null;
}

/**
 * The board's columns for one view. Every task the server returned is in exactly one section; none is a
 * content-board column, and Done is the week's finished work, newest first, as the server ordered it.
 */
export function columnsFor(week: WeekWorkTasks, view: MyWeekView, filters: MyWeekFilters): readonly MyWeekColumn[] {
  const filtered = hasFilters(filters);
  const keep = (tasks: readonly WorkTask[]) => tasks.filter((task) => matches(task, filters));

  const section = (
    id: string,
    heading: string | null,
    group: WorkTaskGroup,
    empty: string,
    marker: string | null = null,
  ): MyWeekSection => ({
    id,
    heading,
    marker,
    tasks: keep(group.tasks),
    loaded: group.tasks.length,
    hasMore: group.hasMore,
    totals: group.totals,
    empty: filtered && group.tasks.length > 0 ? 'Nothing here matches these filters.' : empty,
  });

  const column = (id: string, heading: string, sections: readonly MyWeekSection[]): MyWeekColumn => ({
    id,
    heading,
    count: sections.reduce((sum, part) => sum + part.totals.taskCount, 0),
    sections,
  });

  // The backlog's three parts. "From earlier weeks" only ever has anything in the current week, and is shown only
  // then, so a past or future week does not carry a heading for something it can never hold.
  const backlogSections = [
    section('backlog', 'Without a day', week.backlog, 'Nothing planned for this week without a day.'),
    ...(week.isCurrentWeek || week.carriedOver.totals.taskCount > 0
      ? [section('carried-over', 'From earlier weeks', week.carriedOver, 'Nothing left over from earlier weeks.')]
      : []),
    section('unplanned', 'No week yet', week.unplanned, 'Everything has a week.'),
  ];

  const daySections = week.days.map((day) =>
    // A free day's line: quiet, and short, because its heading — a section's or a column's — already names the day.
    section(`day-${day.date}`, shortDay(day.date), day, 'Nothing planned.', day.isToday ? 'Today' : null),
  );

  const status = [
    column('in-progress', 'In progress', [section('in-progress', null, week.inProgress, 'Nothing under way.')]),
    column('waiting', 'Waiting', [section('waiting', null, week.waiting, 'Nothing held up.')]),
    column('done', 'Done', [section('done', null, week.completed, 'Nothing finished this week yet.')]),
  ];

  const backlog = column('backlog', 'Backlog', backlogSections);

  if (view === 'days') {
    return [
      backlog,
      ...daySections.map((day) =>
        column(day.id, day.marker ? `${day.heading} · ${day.marker}` : day.heading!, [
          { ...day, heading: null, marker: null },
        ]),
      ),
      ...status,
    ];
  }

  return [backlog, column('this-week', 'This week', daySections), ...status];
}

/** Whether the server sent nothing at all for this week: no task in any group. */
export function weekIsEmpty(week: WeekWorkTasks): boolean {
  return [
    week.backlog,
    week.unplanned,
    week.carriedOver,
    week.inProgress,
    week.waiting,
    week.completed,
    ...week.days,
  ].every((group) => group.totals.taskCount === 0);
}

/** Whether the filters left nothing on the board, though the week had tasks loaded. */
export function nothingShown(columns: readonly MyWeekColumn[]): boolean {
  return columns.every((column) => column.sections.every((part) => part.tasks.length === 0));
}

/**
 * "3 tasks · 2 hr 30 min estimated · 1 not estimated": what a group adds up to, in the server's numbers. A task with
 * no estimate is never counted as no time, and no capacity is judged — nobody has said how many hours there are.
 */
export function totalsWords(totals: WorkTaskTotals): string {
  const { taskCount, estimatedTaskCount, estimatedMinutes } = totals;
  if (taskCount === 0) return 'Nothing planned';

  const parts = [`${taskCount} ${taskCount === 1 ? 'task' : 'tasks'}`];
  const unestimated = taskCount - estimatedTaskCount;

  if (estimatedMinutes !== null && estimatedTaskCount > 0) {
    parts.push(`${estimateWords(estimatedMinutes)} estimated`);
    if (unestimated > 0) parts.push(`${unestimated} not estimated`);
  } else {
    parts.push(taskCount === 1 ? 'not estimated' : 'none estimated');
  }

  return parts.join(' · ');
}

/**
 * What a list says about being cut or filtered, or nothing. The week route returns at most 50 of a list and has no
 * way to ask for the rest, so a cut list says so plainly — and a filter over one says it looked at the first 50 only.
 */
export function shownWords(part: MyWeekSection, filtered: boolean): string | null {
  const narrowed = filtered && part.loaded > 0 && part.tasks.length !== part.loaded;

  if (part.hasMore) {
    return narrowed
      ? `${part.tasks.length} of the first ${part.loaded} shown — ${part.totals.taskCount} in all`
      : `Showing the first ${part.loaded} of ${part.totals.taskCount}`;
  }

  return narrowed ? `${part.tasks.length} of ${part.loaded} shown` : null;
}

/** "Week of Mon 12 Oct" — the week the page is about, by its Monday. */
export function weekHeading(weekStart: string): string {
  return `Week of ${shortDay(weekStart)}`;
}

function matches(task: WorkTask, filters: MyWeekFilters): boolean {
  const query = filters.query.trim().toLocaleLowerCase();
  if (query !== '' && !task.title.toLocaleLowerCase().includes(query)) return false;
  if (filters.highOnly && task.priority !== 'High') return false;
  if (filters.labelId !== null && !task.labels.some((label) => label.id === filters.labelId)) return false;

  return true;
}
