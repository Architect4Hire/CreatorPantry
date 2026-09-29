import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { AiUsageService } from './ai-usage.service';

const URL = 'https://gateway.example/api/v1/me/ai-usage';

function payload(overrides: { remaining?: number; allowance?: number; consumed?: number; isSuspended?: boolean } = {}) {
  const allowance = overrides.allowance ?? 1000;
  const remaining = overrides.remaining ?? 640;

  return {
    period: {
      unit: 'Credits',
      allowance,
      carriedOver: 0,
      consumed: overrides.consumed ?? allowance - remaining,
      reserved: 0,
      remaining,
      periodLength: 'Monthly',
      timeZoneId: 'Europe/London',
      startsAt: '2026-03-01T00:00:00+00:00',
      resetsAt: '2026-04-01T00:00:00+01:00',
    },
    isSuspended: overrides.isSuspended ?? false,
    byTask: [{ taskType: 'RecipeConcepts', amount: 240, requests: 12 }],
    byWorkspace: [{ workspaceId: '11111111-1111-1111-1111-111111111111', workspaceName: 'Kitchen', amount: 240, requests: 12 }],
  };
}

describe('AiUsageService', () => {
  let service: AiUsageService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(AiUsageService);
  });

  afterEach(() => http.verify());

  it('reads the account route and never names an account', async () => {
    const loading = service.ensureLoaded();
    const request = http.expectOne(URL);

    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.urlWithParams).toBe(URL);

    request.flush(payload());
    await loading;

    expect(service.state().status).toBe('ready');
  });

  it('reports a comfortable balance as healthy', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 640 }));
    await loading;

    const allowance = service.allowance();
    expect(allowance.kind).toBe('healthy');
    expect(allowance.kind !== 'unknown' && allowance.period.remaining).toBe(640);
    expect(allowance.kind !== 'unknown' && allowance.period.usedPercent).toBe(36);
  });

  it('reports a fifth or less as nearly spent', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 200 }));
    await loading;

    expect(service.allowance().kind).toBe('nearly-spent');
  });

  it('reports nothing left as exhausted', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 0 }));
    await loading;

    expect(service.allowance().kind).toBe('exhausted');
  });

  /** A suspension outranks the balance: there is no reset to wait for, whatever is left. */
  it('reports a suspended account as suspended even with allowance left', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 900, isSuspended: true }));
    await loading;

    expect(service.allowance().kind).toBe('suspended');
  });

  /**
   * The state this service adds over the memberships template. A failed refresh must not blank figures a
   * creator is reading, and must not be mistaken for "you have nothing left".
   */
  it('keeps the last figures when a refresh fails, and says it is degraded', async () => {
    const first = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 640 }));
    await first;

    const second = service.refresh();
    http.expectOne(URL).error(new ProgressEvent('network'));
    await second;

    const state = service.state();
    expect(state.status).toBe('degraded');
    expect(state.status === 'degraded' && state.usage.period.remaining).toBe(640);

    const allowance = service.allowance();
    expect(allowance.kind).toBe('degraded');
    expect(allowance.kind !== 'unknown' && allowance.period.remaining).toBe(640);
  });

  it('is an error, not degraded, when the first read fails with nothing held', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).error(new ProgressEvent('network'));
    await loading;

    expect(service.state().status).toBe('error');
    expect(service.allowance().kind).toBe('unknown');
  });

  /** A payload it cannot read is a failure, not a zero balance. */
  it('treats an undecodable payload as a failure rather than an empty allowance', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush({ nonsense: true });
    await loading;

    expect(service.state().status).toBe('error');
    expect(service.allowance().kind).toBe('unknown');
  });

  it('does not re-read once it has an answer, and retries after a failure', async () => {
    const failed = service.ensureLoaded();
    http.expectOne(URL).error(new ProgressEvent('network'));
    await failed;

    const retried = service.ensureLoaded();
    http.expectOne(URL).flush(payload());
    await retried;

    await service.ensureLoaded();
    http.expectNone(URL);

    expect(service.state().status).toBe('ready');
  });
});
