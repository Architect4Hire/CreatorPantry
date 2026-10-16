// Builders for My Day's specs (13A.32). 2038-10-14 is a Thursday; its week starts on Monday 2038-10-11.

import { TodayWorkTasks, WorkTask } from '../../models/work-task.models';

export const TODAY = '2038-10-14';

export const RUN = '6f1c2a4e-8b1d-4c3e-9f2a-1b2c3d4e5f60';

export function task(overrides: Partial<WorkTask> = {}): WorkTask {
  return {
    id: 't-1',
    title: 'Shoot the galette',
    description: null,
    status: 'Backlog',
    priority: 'Normal',
    scheduledDate: TODAY,
    planningWeekStart: '2038-10-11',
    dueDate: null,
    estimateMinutes: null,
    owner: { membershipId: 'm-1', displayName: 'Sam' },
    assignee: { membershipId: 'm-1', displayName: 'Sam' },
    links: { workflowRunId: null, workflowStepKey: null, workflowRunRoute: null, recipeId: null, contentCardId: null, mediaAssetId: null },
    labels: [],
    createdAt: '2038-10-14T15:00:00Z',
    updatedAt: '2038-10-14T15:00:00Z',
    completedAt: null,
    concurrencyToken: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

export function day(overrides: Partial<TodayWorkTasks> = {}): TodayWorkTasks {
  return {
    date: TODAY,
    timeZoneId: 'America/Chicago',
    overdue: [],
    overdueHasMore: false,
    scheduledToday: [],
    scheduledTodayHasMore: false,
    inProgress: [],
    inProgressHasMore: false,
    waiting: [],
    waitingHasMore: false,
    completedToday: [],
    completedTodayHasMore: false,
    suggested: [],
    topThree: [],
    ...overrides,
  };
}

/** A day with something in every group, in the shape most specs start from. */
export function fullDay(overrides: Partial<TodayWorkTasks> = {}): TodayWorkTasks {
  return day({
    overdue: [task({ id: 'late', title: 'Send the invoice', dueDate: '2038-10-12', scheduledDate: null })],
    scheduledToday: [
      task({ id: 'plan-1', title: 'Write the headnote' }),
      task({ id: 'plan-2', title: 'Edit the photos', estimateMinutes: 90 }),
    ],
    inProgress: [task({ id: 'going', title: 'Draft the newsletter', status: 'InProgress' })],
    waiting: [task({ id: 'held', title: 'Hear back from the editor', status: 'Waiting' })],
    completedToday: [task({ id: 'done', title: 'Buy apples', status: 'Done', completedAt: '2038-10-14T14:00:00Z' })],
    ...overrides,
  });
}
