import {
  addDays,
  decodePlanningDates,
  decodeTodayWorkTasks,
  decodeTopThree,
  decodeWeekWorkTasks,
  isCalendarDate,
  decodeWorkTask,
  encodeCaptureWorkTaskRequest,
  encodeMoveWorkTaskRequest,
  weekStartOf,
} from './work-task.models';

/** A task exactly as `WorkTaskServiceModel` serialises one, with every optional field set. */
const WIRE = {
  id: 't-1',
  title: 'Shoot the galette',
  description: 'Morning light',
  status: 'Backlog',
  priority: 'High',
  scheduledDate: '2038-10-14',
  planningWeekStart: '2038-10-11',
  dueDate: null,
  estimateMinutes: 45,
  owner: { membershipId: 'm-1', displayName: 'Sam' },
  assignee: { membershipId: 'm-1', displayName: 'Sam' },
  links: {
    workflowRunId: null,
    workflowStepKey: null,
    workflowRunRoute: null,
    recipeId: 'r-1',
    contentCardId: null,
    mediaAssetId: null,
  },
  labels: [{ id: 'tag-1', name: 'Weeknight' }],
  createdAt: '2038-10-14T17:00:00+00:00',
  updatedAt: '2038-10-14T17:00:00+00:00',
  completedAt: null,
  concurrencyToken: 'AAAAAAAAB9E=',
};

describe('work task models', () => {
  it('reads a task as the server sends it', () => {
    const task = decodeWorkTask(WIRE);

    expect(task).toEqual(
      jasmine.objectContaining({
        id: 't-1',
        title: 'Shoot the galette',
        status: 'Backlog',
        priority: 'High',
        scheduledDate: '2038-10-14',
        planningWeekStart: '2038-10-11',
        estimateMinutes: 45,
        labels: [{ id: 'tag-1', name: 'Weeknight' }],
      }),
    );
    expect(task!.links.recipeId).toBe('r-1');
    expect(task!.owner.displayName).toBe('Sam');
  });

  it('reads an unknown estimate as unknown, never as no time', () => {
    expect(decodeWorkTask({ ...WIRE, estimateMinutes: null })!.estimateMinutes).toBeNull();
  });

  it('refuses a body that is not a task rather than half-reading it', () => {
    expect(decodeWorkTask(null)).toBeNull();
    expect(decodeWorkTask({ ...WIRE, status: 'Approved' })).toBeNull();
    expect(decodeWorkTask({ ...WIRE, priority: 'Urgent' })).toBeNull();
    expect(decodeWorkTask({ ...WIRE, owner: null })).toBeNull();
    expect(decodeWorkTask({ ...WIRE, labels: [{ id: 'x' }] })).toBeNull();
    expect(decodeWorkTask({ ...WIRE, concurrencyToken: undefined })).toBeNull();
  });

  it('reads today and the week from a week answer, and nothing else', () => {
    expect(decodePlanningDates({ today: '2038-10-14', weekStart: '2038-10-11', days: [] })).toEqual({
      today: '2038-10-14',
      weekStart: '2038-10-11',
    });
    expect(decodePlanningDates({ today: '14/10/2038', weekStart: '2038-10-11' })).toBeNull();
    expect(decodePlanningDates({})).toBeNull();
  });

  it('sends a title alone as exactly that, and only the details chosen', () => {
    expect(encodeCaptureWorkTaskRequest({ title: 'Shoot the galette' })).toEqual({ title: 'Shoot the galette' });
    expect(
      encodeCaptureWorkTaskRequest({ title: 'Shoot it', estimateMinutes: 30, labelIds: [], recipeId: undefined }),
    ).toEqual({
      title: 'Shoot it',
      estimateMinutes: 30,
    });
  });

  it('moves a date by calendar days across a month, a year and a clock change', () => {
    expect(addDays('2038-10-14', 1)).toBe('2038-10-15');
    expect(addDays('2038-10-31', 1)).toBe('2038-11-01');
    expect(addDays('2038-12-31', 1)).toBe('2039-01-01');
    expect(addDays('2038-11-06', 1)).toBe('2038-11-07');
    expect(addDays('2038-11-07', 1)).toBe('2038-11-08');
    expect(addDays('2038-10-11', 7)).toBe('2038-10-18');
  });
});

describe('the day, as GET today answers it (13A.25, 13A.32)', () => {
  const day = (overrides: Record<string, unknown> = {}) => ({
    date: '2038-10-14',
    timeZoneId: 'America/Chicago',
    overdue: [],
    overdueHasMore: false,
    scheduledToday: [{ ...WIRE, id: 't-1' }],
    scheduledTodayHasMore: true,
    inProgress: [{ ...WIRE, id: 't-2', status: 'InProgress' }],
    inProgressHasMore: false,
    waiting: [],
    waitingHasMore: false,
    completedToday: [],
    completedTodayHasMore: false,
    suggested: [{ task: { ...WIRE, id: 't-9' }, reason: 'Next in your backlog.' }],
    topThree: ['t-2', 't-1'],
    ...overrides,
  });

  it('reads every group, the cut flags, the suggestions and the top three in its order', () => {
    const read = decodeTodayWorkTasks(day())!;

    expect(read.date).toBe('2038-10-14');
    expect(read.scheduledToday.map((task) => task.id)).toEqual(['t-1']);
    expect(read.scheduledTodayHasMore).toBeTrue();
    expect(read.inProgress[0].status).toBe('InProgress');
    expect(read.suggested[0]).toEqual(jasmine.objectContaining({ reason: 'Next in your backlog.' }));
    expect(read.topThree).toEqual(['t-2', 't-1']);
  });

  it('keeps only top-three ids the day holds, once each, so the page never points at nothing', () => {
    const read = decodeTodayWorkTasks(day({ topThree: ['t-2', 'gone', 't-2', 7, 't-9'] }))!;

    // t-9 is a suggestion, not part of the day.
    expect(read.topThree).toEqual(['t-2']);
  });

  it('reads a day from before top three existed as having none', () => {
    const wire = day();
    delete (wire as Record<string, unknown>)['topThree'];

    expect(decodeTodayWorkTasks(wire)!.topThree).toEqual([]);
  });

  it('refuses a day whose date or any group is not what it says', () => {
    expect(decodeTodayWorkTasks(day({ date: '14/10/2038' }))).toBeNull();
    expect(decodeTodayWorkTasks(day({ waiting: null }))).toBeNull();
    expect(decodeTodayWorkTasks(day({ overdue: [{ id: 'x' }] }))).toBeNull();
    expect(decodeTodayWorkTasks('nope')).toBeNull();
  });

  it('reads a top three answer, and refuses one that is not a list of ids', () => {
    expect(decodeTopThree({ date: '2038-10-14', taskIds: ['t-1', 't-2'] })).toEqual(['t-1', 't-2']);
    expect(decodeTopThree({ date: '2038-10-14', taskIds: [] })).toEqual([]);
    expect(decodeTopThree({ taskIds: [1] })).toBeNull();
    expect(decodeTopThree({})).toBeNull();
  });
});

describe('a move, on the wire (13A.28)', () => {
  const task = decodeWorkTask(WIRE)!;

  it('quotes the token and sends only the day or week the list needs', () => {
    expect(encodeMoveWorkTaskRequest(task, { status: 'InProgress' })).toEqual({
      expectedConcurrencyToken: 'AAAAAAAAB9E=',
      status: 'InProgress',
    });
    expect(encodeMoveWorkTaskRequest(task, { status: 'Backlog', scheduledDate: '2038-10-15' })).toEqual({
      expectedConcurrencyToken: 'AAAAAAAAB9E=',
      status: 'Backlog',
      scheduledDate: '2038-10-15',
    });
    expect(encodeMoveWorkTaskRequest(task, { status: 'Backlog', planningWeekStart: '2038-10-11' })).toEqual({
      expectedConcurrencyToken: 'AAAAAAAAB9E=',
      status: 'Backlog',
      planningWeekStart: '2038-10-11',
    });
  });
});

describe('weekStartOf', () => {
  it('is the Monday on or before the day, whatever the day, and across a month and a clock change', () => {
    expect(weekStartOf('2038-10-11')).toBe('2038-10-11');
    expect(weekStartOf('2038-10-14')).toBe('2038-10-11');
    expect(weekStartOf('2038-10-17')).toBe('2038-10-11');
    expect(weekStartOf('2038-11-01')).toBe('2038-11-01');
    expect(weekStartOf('2038-11-07')).toBe('2038-11-01');
    expect(weekStartOf('2038-10-03')).toBe('2038-09-27');
  });
});

describe('the week, as GET week answers it (13A.26, 13A.33)', () => {
  const totals = { taskCount: 1, estimatedTaskCount: 0, estimatedMinutes: null };
  const group = (tasks: unknown[] = []) => ({ tasks, hasMore: false, totals: { ...totals, taskCount: tasks.length } });
  const wire = (overrides: Record<string, unknown> = {}) => ({
    weekStart: '2038-10-11',
    weekEnd: '2038-10-17',
    today: '2038-10-14',
    timeZoneId: 'America/Chicago',
    isCurrentWeek: true,
    planned: { taskCount: 3, estimatedTaskCount: 2, estimatedMinutes: 150 },
    days: [11, 12, 13, 14, 15, 16, 17].map((day) => ({
      date: `2038-10-${day}`,
      dayOfWeek: 'Monday',
      isToday: day === 14,
      ...group(day === 14 ? [WIRE] : []),
    })),
    backlog: group(),
    unplanned: group(),
    carriedOver: group(),
    inProgress: group([{ ...WIRE, id: 't-2', status: 'InProgress' }]),
    waiting: group(),
    completed: { ...group(), hasMore: true, totals: { taskCount: 63, estimatedTaskCount: 0, estimatedMinutes: null } },
    ...overrides,
  });

  it('reads the edges, the seven days, every group and the server’s totals', () => {
    const week = decodeWeekWorkTasks(wire())!;

    expect(week.weekStart).toBe('2038-10-11');
    expect(week.isCurrentWeek).toBeTrue();
    expect(week.planned).toEqual({ taskCount: 3, estimatedTaskCount: 2, estimatedMinutes: 150 });
    expect(week.days.length).toBe(7);
    expect(week.days[3]).toEqual(jasmine.objectContaining({ date: '2038-10-14', isToday: true }));
    expect(week.days[3].tasks[0].id).toBe('t-1');
    expect(week.inProgress.tasks[0].status).toBe('InProgress');
    expect(week.completed.hasMore).toBeTrue();
    expect(week.completed.totals.taskCount).toBe(63);
  });

  it('keeps an unknown estimate unknown, never zero', () => {
    expect(decodeWeekWorkTasks(wire())!.backlog.totals.estimatedMinutes).toBeNull();
  });

  it('refuses a week that is not seven days, or whose edges or groups are not what they say', () => {
    expect(decodeWeekWorkTasks(wire({ days: [] }))).toBeNull();
    expect(decodeWeekWorkTasks(wire({ weekStart: 'Monday' }))).toBeNull();
    expect(decodeWeekWorkTasks(wire({ waiting: { tasks: [], hasMore: false } }))).toBeNull();
    expect(decodeWeekWorkTasks(wire({ planned: { taskCount: 'three', estimatedTaskCount: 0 } }))).toBeNull();
    expect(decodeWeekWorkTasks(null)).toBeNull();
  });
});

describe('isCalendarDate', () => {
  it('accepts a real date and refuses the shape of one that is not', () => {
    expect(isCalendarDate('2038-10-14')).toBeTrue();
    expect(isCalendarDate('2040-02-29')).toBeTrue();
    expect(isCalendarDate('2038-02-29')).toBeFalse();
    expect(isCalendarDate('2038-02-31')).toBeFalse();
    expect(isCalendarDate('2038-13-01')).toBeFalse();
    expect(isCalendarDate('14/10/2038')).toBeFalse();
  });
});
