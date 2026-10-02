import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { BrandSetupSessionWrite } from '../models/brand-setup.models';
import { BrandSetupSessionService } from './brand-setup-session.service';

const URL = 'https://gateway.example/api/v1/workspaces/w/brand-setup-session';

const WIRE = {
  status: 'inProgress',
  currentStep: 'goals',
  furthestStep: 'goals',
  completedSteps: [],
  skippedSteps: [],
  draftJson: '{}',
  createdUtc: '2026-10-01T10:00:00Z',
  updatedUtc: '2026-10-01T10:00:00Z',
  completedUtc: null,
  rowVersion: 'rv1',
};

const BODY: BrandSetupSessionWrite = {
  currentStep: 'goals',
  furthestStep: 'goals',
  completedSteps: [],
  skippedSteps: [],
  draftJson: '{}',
};

describe('BrandSetupSessionService', () => {
  let service: BrandSetupSessionService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    const loading = TestBed.inject(RuntimeConfigService).load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;
    service = TestBed.inject(BrandSetupSessionService);
  });

  afterEach(() => http.verify());

  const fail = (status: number, code?: string): [object, { status: number; statusText: string }] => [
    code ? { code } : {},
    { status, statusText: 'x' },
  ];

  it('reads the session with credentials', async () => {
    const pending = service.getSession('w');
    const request = http.expectOne(URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(WIRE);
    const outcome = await pending;
    expect(outcome.status === 'found' && outcome.session.rowVersion).toBe('rv1');
  });

  it('maps 204 to none, 404 to not_found, other failures and garbage to unavailable', async () => {
    const none = service.getSession('w');
    http.expectOne(URL).flush(null, { status: 204, statusText: 'No Content' });
    expect((await none).status).toBe('none');

    const missing = service.getSession('w');
    http.expectOne(URL).flush(...fail(404));
    expect((await missing).status).toBe('not_found');

    const broken = service.getSession('w');
    http.expectOne(URL).flush(...fail(500));
    expect((await broken).status).toBe('unavailable');

    const garbage = service.getSession('w');
    http.expectOne(URL).flush({ nope: true });
    expect((await garbage).status).toBe('unavailable');
  });

  it('creates with PUT and no If-Match', async () => {
    const pending = service.saveSession('w', BODY, null);
    const request = http.expectOne(URL);
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.has('If-Match')).toBeFalse();
    expect(request.request.body).toEqual(BODY);
    request.flush(WIRE);
    expect((await pending).status).toBe('saved');
  });

  it('updates with PUT and If-Match carrying the row version', async () => {
    const pending = service.saveSession('w', BODY, 'rv1');
    const request = http.expectOne(URL);
    expect(request.request.headers.get('If-Match')).toBe('rv1');
    request.flush({ ...WIRE, rowVersion: 'rv2' });
    const outcome = await pending;
    expect(outcome.status === 'saved' && outcome.session.rowVersion).toBe('rv2');
  });

  it('maps write errors', async () => {
    const cases: [number, string | undefined, string][] = [
      [400, undefined, 'validation_failed'],
      [403, undefined, 'forbidden'],
      [404, undefined, 'not_found'],
      [409, 'brand_setup_session_conflict', 'conflict'],
      [409, 'brand_setup_session_completed', 'session_completed'],
      [409, undefined, 'conflict'],
      [500, undefined, 'unavailable'],
    ];
    for (const [status, code, expected] of cases) {
      const pending = service.saveSession('w', BODY, 'rv1');
      http.expectOne(URL).flush(...fail(status, code));
      expect((await pending).status).withContext(`${status} ${code}`).toBe(expected);
    }
  });

  it('completes with POST to /complete and If-Match', async () => {
    const pending = service.completeSession('w', 'rv9');
    const request = http.expectOne(`${URL}/complete`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('If-Match')).toBe('rv9');
    request.flush({ ...WIRE, status: 'completed', completedUtc: '2026-10-01T11:00:00Z' });
    const outcome = await pending;
    expect(outcome.status === 'saved' && outcome.session.status).toBe('completed');
  });

  it('maps a conflict on complete', async () => {
    const pending = service.completeSession('w', 'old');
    http.expectOne(`${URL}/complete`).flush(...fail(409, 'brand_setup_session_conflict'));
    expect((await pending).status).toBe('conflict');
  });

  it('deletes with DELETE and maps 204 and failures', async () => {
    const ok = service.deleteSession('w');
    const request = http.expectOne(URL);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(null, { status: 204, statusText: 'No Content' });
    expect((await ok).status).toBe('deleted');

    const forbidden = service.deleteSession('w');
    http.expectOne(URL).flush(...fail(403));
    expect((await forbidden).status).toBe('forbidden');

    const broken = service.deleteSession('w');
    http.expectOne(URL).flush(...fail(500));
    expect((await broken).status).toBe('unavailable');
  });

  it('maps a delete that lost a race (409 brand_setup_session_conflict) to a typed conflict, not an error', async () => {
    const pending = service.deleteSession('w');
    http.expectOne(URL).flush(...fail(409, 'brand_setup_session_conflict'));
    expect((await pending).status).toBe('conflict');
  });

  it('encodes the workspace slug from the caller only', async () => {
    const pending = service.getSession('a b');
    http.expectOne('https://gateway.example/api/v1/workspaces/a%20b/brand-setup-session').flush(null, { status: 204, statusText: 'x' });
    expect((await pending).status).toBe('none');
  });
});
