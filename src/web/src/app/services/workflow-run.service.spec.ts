import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ApiBaseService } from '../core/api-base.service';
import { wireRun } from '../models/workflow-run.testing';
import { WorkflowRun, decodeWorkflowRun } from '../models/workflow-run.models';
import { WorkflowRunService } from './workflow-run.service';

const BASE = 'https://gateway.test';
const RUNS = `${BASE}/api/v1/workspaces/sams-kitchen/workflow-runs`;

describe('WorkflowRunService', () => {
  let service: WorkflowRunService;
  let http: HttpTestingController;
  let run: WorkflowRun;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ApiBaseService, useValue: { url: (path: string) => new URL(path, BASE).toString() } },
      ],
    });
    service = TestBed.inject(WorkflowRunService);
    http = TestBed.inject(HttpTestingController);
    run = decodeWorkflowRun(wireRun())!;
  });

  afterEach(() => http.verify());

  it("reads a run, and an unknown or another workspace's run as not found", async () => {
    const found = service.get('sams-kitchen', 'run-1');
    http.expectOne(`${RUNS}/run-1`).flush(wireRun());
    expect((await found).status).toBe('found');

    const missing = service.get('sams-kitchen', 'run-2');
    http
      .expectOne(`${RUNS}/run-2`)
      .flush({ code: 'workflows.run.not_found' }, { status: 404, statusText: 'Not Found' });
    expect(await missing).toEqual({ status: 'not_found' });
  });

  it('treats a body that is not a run as unavailable rather than a run with holes', async () => {
    const read = service.get('sams-kitchen', 'run-1');
    http.expectOne(`${RUNS}/run-1`).flush({ id: 'run-1' });

    expect(await read).toEqual({ status: 'unavailable', offline: false });
  });

  it("sends each command to its route with the run's token", async () => {
    const commands: [Promise<unknown>, string, string, unknown][] = [
      [
        service.completeStep('sams-kitchen', run, 'ingredients', [{ kind: 'Recipe', id: 'r-1' }]),
        'POST',
        '/run-1/steps/ingredients/completion',
        { results: [{ kind: 'Recipe', id: 'r-1' }], expectedConcurrencyToken: run.concurrencyToken },
      ],
      [
        service.back('sams-kitchen', run, 'ingredients'),
        'POST',
        '/run-1/steps/ingredients/back',
        { expectedConcurrencyToken: run.concurrencyToken },
      ],
      [
        service.skip('sams-kitchen', run, 'photo'),
        'POST',
        '/run-1/steps/photo/skip',
        { reason: null, expectedConcurrencyToken: run.concurrencyToken },
      ],
      [
        service.saveDraft('sams-kitchen', run, 'ingredients', { kind: 'Recipe', id: 'd-1' }),
        'PUT',
        '/run-1/steps/ingredients/draft',
        { kind: 'Recipe', id: 'd-1', expectedConcurrencyToken: run.concurrencyToken },
      ],
      [
        service.saveAndExit('sams-kitchen', run, 'ingredients', {
          draft: { kind: 'Recipe', id: 'd-1' },
          capabilityKey: 'recipes.edit',
        }),
        'POST',
        '/run-1/steps/ingredients/save-and-exit',
        {
          draft: { kind: 'Recipe', id: 'd-1' },
          capabilityKey: 'recipes.edit',
          expectedConcurrencyToken: run.concurrencyToken,
        },
      ],
      [
        service.completeRun('sams-kitchen', run),
        'POST',
        '/run-1/completion',
        { expectedConcurrencyToken: run.concurrencyToken },
      ],
      [
        service.abandon('sams-kitchen', run, 'Not this week'),
        'POST',
        '/run-1/abandonment',
        { confirmed: true, reason: 'Not this week', expectedConcurrencyToken: run.concurrencyToken },
      ],
    ];

    for (const [pending, method, path, body] of commands) {
      const request = http.expectOne(`${RUNS}${path}`);
      expect(request.request.method).toBe(method);
      expect(request.request.body).toEqual(body);
      expect(request.request.withCredentials).toBeTrue();
      request.flush(wireRun());
      expect(((await pending) as { status: string }).status).toBe('done');
    }
  });

  it("starts over and starts again under the caller's idempotency key", async () => {
    const restart = service.restart('sams-kitchen', run, 'key-1');
    const restartRequest = http.expectOne(`${RUNS}/run-1/restart`);
    expect(restartRequest.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(restartRequest.request.body).toEqual({ confirmed: true, expectedConcurrencyToken: run.concurrencyToken });
    restartRequest.flush(wireRun({ id: 'run-2' }), { status: 201, statusText: 'Created' });
    const restarted = await restart;
    expect(restarted.status).toBe('started');

    const start = service.start('sams-kitchen', 'recipe.create', 'key-2');
    const startRequest = http.expectOne(RUNS);
    expect(startRequest.request.headers.get('Idempotency-Key')).toBe('key-2');
    expect(startRequest.request.body).toEqual({ definitionKey: 'recipe.create' });
    startRequest.flush(wireRun({ id: 'run-3' }), { status: 201, statusText: 'Created' });
    expect((await start).status).toBe('started');
  });

  for (const [status, code, body, expected] of [
    [409, 'workflows.run.conflict', {}, { status: 'conflict' }],
    [409, 'workflows.run.not_in_progress.conflict', {}, { status: 'not_in_progress' }],
    [409, 'workflows.run.at_first_step.conflict', {}, { status: 'at_first_step' }],
    [409, 'workflows.step.not_current.conflict', {}, { status: 'step_moved' }],
    [
      422,
      'workflows.step.incomplete.unprocessable',
      { missing: ['Add an ingredient first.'] },
      { status: 'step_incomplete', missing: ['Add an ingredient first.'] },
    ],
    [
      422,
      'workflows.run.incomplete.unprocessable',
      { incompleteSteps: [{ stepKey: 'a', title: 'A', missing: ['x'] }] },
      { status: 'run_incomplete', incompleteSteps: [{ stepKey: 'a', title: 'A', missing: ['x'] }] },
    ],
    [422, 'workflows.step.required.unprocessable', {}, { status: 'step_required' }],
    [
      422,
      'workflows.step.result.unprocessable',
      { title: "That recipe isn't one this workspace has." },
      { status: 'unprocessable', message: "That recipe isn't one this workspace has." },
    ],
    [400, 'workflows.step.completion.invalid', {}, { status: 'invalid' }],
    [403, 'workflows.run.forbidden', {}, { status: 'forbidden' }],
    [404, 'workflows.run.not_found', {}, { status: 'not_found' }],
    [503, 'unavailable', {}, { status: 'unavailable', offline: false }],
  ] as const) {
    it(`reads ${status} ${code} as ${expected.status}`, async () => {
      const pending = service.completeStep('sams-kitchen', run, 'ingredients');
      http
        .expectOne(`${RUNS}/run-1/steps/ingredients/completion`)
        .flush({ code, ...body }, { status, statusText: 'x' });

      expect(await pending).toEqual(expected as never);
    });
  }

  it('says when a command failed because the browser is offline', async () => {
    spyOnProperty(navigator, 'onLine').and.returnValue(false);

    const pending = service.back('sams-kitchen', run, 'ingredients');
    http.expectOne(`${RUNS}/run-1/steps/ingredients/back`).error(new ProgressEvent('error'));

    expect(await pending).toEqual({ status: 'unavailable', offline: true });
  });

  it('sends nothing before the gateway is known', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ApiBaseService, useValue: { url: () => null } },
      ],
    });
    const unready = TestBed.inject(WorkflowRunService);

    expect((await unready.get('sams-kitchen', 'run-1')).status).toBe('unavailable');
    expect((await unready.back('sams-kitchen', run, 'ingredients')).status).toBe('unavailable');
    TestBed.inject(HttpTestingController).verify();
  });
});
