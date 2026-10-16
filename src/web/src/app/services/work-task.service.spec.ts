import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ApiBaseService } from '../core/api-base.service';
import { WorkTaskService } from './work-task.service';

const BASE = 'https://gateway.test';
const WS = `${BASE}/api/v1/workspaces/sams-kitchen`;

const TASK = {
  id: 't-1',
  title: 'Shoot the galette',
  description: null,
  status: 'Backlog',
  priority: 'Normal',
  scheduledDate: null,
  planningWeekStart: '2038-10-11',
  dueDate: null,
  estimateMinutes: null,
  owner: { membershipId: 'm-1', displayName: 'Sam' },
  assignee: { membershipId: 'm-1', displayName: 'Sam' },
  links: { workflowRunId: null, workflowStepKey: null, workflowRunRoute: null, recipeId: null, contentCardId: null, mediaAssetId: null },
  labels: [],
  createdAt: '2038-10-14T17:00:00+00:00',
  updatedAt: '2038-10-14T17:00:00+00:00',
  completedAt: null,
  concurrencyToken: 'AAAAAAAAB9E=',
};

describe('WorkTaskService', () => {
  let service: WorkTaskService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ApiBaseService, useValue: { url: (path: string) => new URL(path, BASE).toString() } },
      ],
    });
    service = TestBed.inject(WorkTaskService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function problem(code: string, errors: Record<string, string[]> = {}) {
    return { code, errors };
  }

  it('captures with the title alone, the key in its header, and credentials sent', async () => {
    const pending = service.capture('sams-kitchen', { title: 'Shoot the galette' }, 'key-1');

    const request = http.expectOne(`${WS}/work-tasks`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ title: 'Shoot the galette' });
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(TASK, { status: 201, statusText: 'Created' });

    const outcome = await pending;
    expect(outcome.status).toBe('captured');
    expect(outcome.status === 'captured' && outcome.replayed).toBeFalse();
  });

  it('says when the answer was a replay of an earlier capture', async () => {
    const pending = service.capture('sams-kitchen', { title: 'Shoot the galette' }, 'key-1');
    http.expectOne(`${WS}/work-tasks`).flush(TASK, { status: 201, statusText: 'Created', headers: { 'Idempotent-Replayed': 'true' } });

    const outcome = await pending;
    expect(outcome.status === 'captured' && outcome.replayed).toBeTrue();
  });

  it('reads each refusal as the remedy it calls for', async () => {
    const cases: [number, object, string][] = [
      [400, problem('planning.work_task.invalid', { title: ['A task needs a title.'] }), 'invalid'],
      [403, problem('forbidden'), 'forbidden'],
      [422, problem('planning.work_task.label.unprocessable', { labelIds: ['…'] }), 'label_refused'],
      [422, problem('planning.work_task.link.unprocessable', { recipeId: ['…'] }), 'link_refused'],
      [422, problem('idempotency.key_reused'), 'key_reused'],
      [500, {}, 'unavailable'],
    ];

    for (const [status, body, expected] of cases) {
      const pending = service.capture('sams-kitchen', { title: 'x' }, 'key');
      http.expectOne(`${WS}/work-tasks`).flush(body, { status, statusText: 'x' });
      const outcome = await pending;

      expect(outcome.status).withContext(`${status} ${JSON.stringify(body)}`).toBe(expected as never);
      if (outcome.status === 'invalid') expect(outcome.fieldErrors['title']).toEqual(['A task needs a title.']);
      if (outcome.status === 'link_refused') expect(outcome.fields).toEqual(['recipeId']);
    }
  });

  it('reads today and the week from the week route', async () => {
    const pending = service.planningDates('sams-kitchen');
    http.expectOne(`${WS}/work-tasks/week`).flush({ today: '2038-10-14', weekStart: '2038-10-11' });

    expect(await pending).toEqual({ status: 'found', dates: { today: '2038-10-14', weekStart: '2038-10-11' } });
  });

  it('says a workspace keeps no calendar, and that the week could not be read, apart', async () => {
    const noZone = service.planningDates('sams-kitchen');
    http.expectOne(`${WS}/work-tasks/week`).flush(problem('planning.week.time_zone_not_set.unprocessable'), { status: 422, statusText: 'x' });
    expect(await noZone).toEqual({ status: 'no_time_zone' });

    const broken = service.planningDates('sams-kitchen');
    http.expectOne(`${WS}/work-tasks/week`).flush({}, { status: 500, statusText: 'x' });
    expect(await broken).toEqual({ status: 'unavailable' });
  });

  // ---- My Week (13A.33) ----

  const WEEK_GROUP = { tasks: [], hasMore: false, totals: { taskCount: 0, estimatedTaskCount: 0, estimatedMinutes: null } };
  const WEEK = {
    weekStart: '2038-10-11',
    weekEnd: '2038-10-17',
    today: '2038-10-14',
    timeZoneId: 'America/Chicago',
    isCurrentWeek: true,
    planned: WEEK_GROUP.totals,
    days: [11, 12, 13, 14, 15, 16, 17].map((day) => ({ date: `2038-10-${day}`, dayOfWeek: 'Monday', isToday: day === 14, ...WEEK_GROUP })),
    backlog: WEEK_GROUP,
    unplanned: WEEK_GROUP,
    carriedOver: WEEK_GROUP,
    inProgress: WEEK_GROUP,
    waiting: WEEK_GROUP,
    completed: WEEK_GROUP,
  };

  it('reads the current week with no week named, and a named one by its Monday', async () => {
    const current = service.week('sams-kitchen', null);
    const plain = http.expectOne(`${WS}/work-tasks/week`);
    expect(plain.request.withCredentials).toBeTrue();
    plain.flush(WEEK);
    expect((await current).status).toBe('found');

    const named = service.week('sams-kitchen', '2038-10-18');
    http.expectOne(`${WS}/work-tasks/week?weekStart=2038-10-18`).flush({ ...WEEK, weekStart: '2038-10-18', weekEnd: '2038-10-24', isCurrentWeek: false });
    const outcome = await named;
    expect(outcome.status === 'found' && outcome.week.isCurrentWeek).toBeFalse();
  });

  it('reads a week the server will not answer as the remedy each calls for', async () => {
    const cases: [number, object, string][] = [
      [400, problem('planning.week.invalid'), 'invalid'],
      [422, problem('planning.week.time_zone_not_set.unprocessable'), 'no_time_zone'],
      [500, {}, 'unavailable'],
    ];

    for (const [status, body, expected] of cases) {
      const pending = service.week('sams-kitchen', null);
      http.expectOne(`${WS}/work-tasks/week`).flush(body, { status, statusText: 'x' });
      expect((await pending).status).withContext(`${status}`).toBe(expected);
    }

    const broken = service.week('sams-kitchen', null);
    http.expectOne(`${WS}/work-tasks/week`).flush({ ...WEEK, days: [] });
    expect(await broken).toEqual({ status: 'unavailable', offline: false });
  });

  // ---- My Day (13A.32) ----

  const DAY = {
    date: '2038-10-14',
    timeZoneId: 'America/Chicago',
    overdue: [],
    overdueHasMore: false,
    scheduledToday: [TASK],
    scheduledTodayHasMore: false,
    inProgress: [],
    inProgressHasMore: false,
    waiting: [],
    waitingHasMore: false,
    completedToday: [],
    completedTodayHasMore: false,
    suggested: [],
    topThree: ['t-1'],
  };

  it('reads the day with suggestions asked for, credentials sent', async () => {
    const pending = service.today('sams-kitchen', true);

    const request = http.expectOne(`${WS}/work-tasks/today?includeSuggestions=true`);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(DAY);

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    expect(outcome.status === 'found' && outcome.day.topThree).toEqual(['t-1']);
  });

  it('reads a day the workspace keeps no calendar for as that, and anything else as unavailable', async () => {
    const noZone = service.today('sams-kitchen');
    http.expectOne(`${WS}/work-tasks/today`).flush(problem('planning.today.time_zone_not_set.unprocessable'), {
      status: 422,
      statusText: 'x',
    });
    expect(await noZone).toEqual({ status: 'no_time_zone' });

    const broken = service.today('sams-kitchen');
    http.expectOne(`${WS}/work-tasks/today`).flush({ nope: true });
    expect(await broken).toEqual({ status: 'unavailable', offline: false });
  });

  it('moves, completes and reopens quoting the token, with no idempotency key', async () => {
    const task = (await (async () => {
      const pending = service.today('sams-kitchen');
      http.expectOne(`${WS}/work-tasks/today`).flush(DAY);
      const outcome = await pending;
      return outcome.status === 'found' ? outcome.day.scheduledToday[0] : null;
    })())!;

    const moved = service.move('sams-kitchen', task, { status: 'Backlog', scheduledDate: '2038-10-15' });
    const move = http.expectOne(`${WS}/work-tasks/t-1/move`);
    expect(move.request.method).toBe('POST');
    expect(move.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=', status: 'Backlog', scheduledDate: '2038-10-15' });
    expect(move.request.headers.has('Idempotency-Key')).toBeFalse();
    move.flush({ ...TASK, scheduledDate: '2038-10-15' });
    expect((await moved).status).toBe('saved');

    const completed = service.complete('sams-kitchen', task);
    const complete = http.expectOne(`${WS}/work-tasks/t-1/complete`);
    expect(complete.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=' });
    complete.flush({ ...TASK, status: 'Done', completedAt: '2038-10-14T18:00:00Z' });
    expect((await completed).status).toBe('saved');

    const reopened = service.reopen('sams-kitchen', task);
    http.expectOne(`${WS}/work-tasks/t-1/reopen`).flush(TASK);
    expect((await reopened).status).toBe('saved');
  });

  it('reorders with the token and the place, and reads a place that is gone (13A.33)', async () => {
    const pending = service.today('sams-kitchen');
    http.expectOne(`${WS}/work-tasks/today`).flush(DAY);
    const outcome = await pending;
    if (outcome.status !== 'found') throw new Error('no day');
    const task = outcome.day.scheduledToday[0];

    const reordered = service.reorder('sams-kitchen', task, 't-9');
    const reorder = http.expectOne(`${WS}/work-tasks/t-1/reorder`);
    expect(reorder.request.method).toBe('POST');
    expect(reorder.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=', beforeTaskId: 't-9' });
    reorder.flush(TASK);
    expect((await reordered).status).toBe('saved');

    const bottom = service.reorder('sams-kitchen', task, null);
    const toBottom = http.expectOne(`${WS}/work-tasks/t-1/reorder`);
    expect(toBottom.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=' });
    toBottom.flush(problem('planning.work_task.move.unprocessable'), { status: 422, statusText: 'x' });
    expect((await bottom).status).toBe('place_refused');

    const moved = service.move('sams-kitchen', task, { status: 'InProgress', beforeTaskId: 't-9' });
    const move = http.expectOne(`${WS}/work-tasks/t-1/move`);
    expect(move.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=', status: 'InProgress', beforeTaskId: 't-9' });
    move.flush(problem('planning.work_task.invalid'), { status: 400, statusText: 'x' });
    expect((await moved).status).toBe('invalid');
  });

  it('reads each refusal of a change as what the page should do about it', async () => {
    const task = { ...(await firstTask()) };
    const cases: [number, object, string][] = [
      [409, problem('planning.work_task.stale.conflict'), 'stale'],
      [409, problem('planning.work_task.completed.conflict'), 'already'],
      [409, problem('planning.work_task.open.conflict'), 'already'],
      [409, problem('planning.work_task.conflict'), 'unavailable'],
      [404, problem('planning.work_task.not_found'), 'not_found'],
      [403, problem('planning.work_task.forbidden'), 'forbidden'],
      [500, {}, 'unavailable'],
    ];

    for (const [status, body, expected] of cases) {
      const pending = service.complete('sams-kitchen', task);
      http.expectOne(`${WS}/work-tasks/t-1/complete`).flush(body, { status, statusText: 'x' });
      expect((await pending).status).withContext(`${status} ${JSON.stringify(body)}`).toBe(expected);
    }

    async function firstTask() {
      const pending = service.today('sams-kitchen');
      http.expectOne(`${WS}/work-tasks/today`).flush(DAY);
      const outcome = await pending;
      if (outcome.status !== 'found') throw new Error('no day');
      return outcome.day.scheduledToday[0];
    }
  });

  it('puts the whole top three, in order, and reads its answer and its refusals', async () => {
    const saved = service.setTopThree('sams-kitchen', ['t-2', 't-1']);
    const put = http.expectOne(`${WS}/work-tasks/top-three`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ taskIds: ['t-2', 't-1'] });
    expect(put.request.withCredentials).toBeTrue();
    put.flush({ date: '2038-10-14', taskIds: ['t-2', 't-1'] });
    expect(await saved).toEqual({ status: 'saved', taskIds: ['t-2', 't-1'] });

    const cases: [number, object, string][] = [
      [422, problem('planning.top_three.unprocessable'), 'refused'],
      [422, problem('planning.today.time_zone_not_set.unprocessable'), 'no_time_zone'],
      [403, problem('planning.work_task.forbidden'), 'forbidden'],
      [409, problem('planning.top_three.conflict'), 'unavailable'],
    ];
    for (const [status, body, expected] of cases) {
      const pending = service.setTopThree('sams-kitchen', []);
      http.expectOne(`${WS}/work-tasks/top-three`).flush(body, { status, statusText: 'x' });
      expect((await pending).status).withContext(JSON.stringify(body)).toBe(expected);
    }
  });
});
