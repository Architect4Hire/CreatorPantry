// Builders for My Week's specs (13A.33). The week of Monday 2038-10-11; today is Thursday 2038-10-14.

import { WeekWorkTasks, WorkTask, WorkTaskGroup, WorkTaskTotals, addDays } from '../../models/work-task.models';
import { task } from '../my-day/my-day.fixtures';

export { task };

export const WEEK = '2038-10-11';
export const TODAY = '2038-10-14';

export function totals(taskCount: number, estimatedTaskCount = 0, estimatedMinutes: number | null = null): WorkTaskTotals {
  return { taskCount, estimatedTaskCount, estimatedMinutes };
}

/** A group of these tasks, its totals counted from them unless given. */
export function group(tasks: readonly WorkTask[] = [], overrides: Partial<WorkTaskGroup> = {}): WorkTaskGroup {
  const estimated = tasks.filter((each) => each.estimateMinutes !== null);
  const minutes = estimated.reduce((sum, each) => sum + each.estimateMinutes!, 0);

  return {
    tasks,
    hasMore: false,
    totals: totals(tasks.length, estimated.length, estimated.length === 0 ? null : minutes),
    ...overrides,
  };
}

export function week(overrides: Partial<WeekWorkTasks> & { readonly dayTasks?: Record<number, readonly WorkTask[]> } = {}): WeekWorkTasks {
  const { dayTasks = {}, ...rest } = overrides;
  const weekStart = rest.weekStart ?? WEEK;

  return {
    weekStart,
    weekEnd: addDays(weekStart, 6),
    today: TODAY,
    timeZoneId: 'America/Chicago',
    isCurrentWeek: true,
    planned: totals(0),
    days: [0, 1, 2, 3, 4, 5, 6].map((offset) => {
      const date = addDays(weekStart, offset);
      return { ...group(dayTasks[offset] ?? []), date, isToday: date === TODAY };
    }),
    backlog: group(),
    unplanned: group(),
    carriedOver: group(),
    inProgress: group(),
    waiting: group(),
    completed: group(),
    ...rest,
  };
}

/** A week with something in most places, in the shape most specs start from. */
export function fullWeek(overrides: Partial<WeekWorkTasks> = {}): WeekWorkTasks {
  return week({
    planned: totals(4, 2, 150),
    dayTasks: {
      0: [task({ id: 'mon', title: 'Edit the photos', scheduledDate: '2038-10-11', estimateMinutes: 90 })],
      3: [
        task({ id: 'thu-1', title: 'Write the headnote', priority: 'High', estimateMinutes: 60 }),
        task({ id: 'thu-2', title: 'Shoot the galette', labels: [{ id: 'tag-1', name: 'Weeknight' }] }),
      ],
    },
    backlog: group([task({ id: 'later', title: 'Pitch the cookbook', scheduledDate: null })]),
    carriedOver: group([task({ id: 'old', title: 'Send the invoice', scheduledDate: null, planningWeekStart: '2038-10-04' })]),
    unplanned: group([task({ id: 'loose', title: 'Someday soup', scheduledDate: null, planningWeekStart: null })]),
    inProgress: group([task({ id: 'going', title: 'Draft the newsletter', status: 'InProgress' })]),
    waiting: group([task({ id: 'held', title: 'Hear back from the editor', status: 'Waiting' })]),
    completed: group([task({ id: 'done', title: 'Buy apples', status: 'Done', completedAt: '2038-10-12T14:00:00Z' })]),
    ...overrides,
  });
}
