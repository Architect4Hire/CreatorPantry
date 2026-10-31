import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { allowanceBlocksNewRequests } from '../shared/allowance-gate';
import { AiUsageService } from './ai-usage.service';

const URL = 'https://gateway.example/api/v1/me/ai-usage';

function payload(
  overrides: {
    remaining?: number;
    allowance?: number;
    consumed?: number;
    isSuspended?: boolean;
    startsAt?: string;
    isNearlySpent?: boolean;
    packRemaining?: number;
    lowCredit?: { isNoticed?: boolean; isDismissed?: boolean; noticeEnabled?: boolean; spendable?: number };
  } = {},
) {
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
      startsAt: overrides.startsAt ?? '2026-03-01T00:00:00+00:00',
      resetsAt: '2026-04-01T00:00:00+01:00',
    },
    isSuspended: overrides.isSuspended ?? false,
    // What the server would say for these figures, unless a case says otherwise.
    isNearlySpent: overrides.isNearlySpent ?? (remaining > 0 && remaining <= allowance / 5),
    pictures: 0,
    writing: 240,
    packRemaining: overrides.packRemaining ?? 0,
    plan: null,
    lowCredit: {
      isNoticed: overrides.lowCredit?.isNoticed ?? false,
      isDismissed: overrides.lowCredit?.isDismissed ?? false,
      noticeEnabled: overrides.lowCredit?.noticeEnabled ?? true,
      spendable: overrides.lowCredit?.spendable ?? remaining + (overrides.packRemaining ?? 0),
    },
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

  /** The server draws the line (AP.2.6): the service reads its answer and keeps no threshold of its own. */
  it('takes nearly spent from the server, whatever the figures would suggest', async () => {
    const plenty = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 640, isNearlySpent: true }));
    await plenty;

    expect(service.allowance().kind).toBe('nearly-spent');

    const low = service.refresh();
    http.expectOne(URL).flush(payload({ remaining: 100, isNearlySpent: false }));
    await low;

    expect(service.allowance().kind).toBe('healthy');
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

  /**
   * Admission spends packs once the allowance is gone (AP.5.2), so a spent allowance with packs left is not
   * exhausted — and the gate every AI surface reads stays open for a request the server would accept.
   */
  it('does not report a spent allowance as exhausted while credit packs are left', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 0, packRemaining: 400 }));
    await loading;

    const allowance = service.allowance();
    expect(allowance.kind).toBe('healthy');
    expect(allowance.kind !== 'unknown' && allowance.period.packRemaining).toBe(400);
    expect(allowanceBlocksNewRequests(allowance)).toBeFalse();
  });

  it('reports exhausted once the allowance and the packs are both spent', async () => {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush(payload({ remaining: 0, packRemaining: 0 }));
    await loading;

    expect(allowanceBlocksNewRequests(service.allowance())).toBeTrue();
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

  // ---- the low-credit notice (B-59) --------------------------------------------------------------------

  const NOTICE = `${URL}/low-credit-notice`;

  /** Lets the awaited save resolve before the re-read it starts is expected. */
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

  async function readWith(overrides: Parameters<typeof payload>[0]): Promise<void> {
    const loading = service.ensureLoaded();
    http.expectOne(URL).flush(payload(overrides));
    await loading;
  }

  it('says nothing about running low until the server has raised a notice', async () => {
    await readWith({ remaining: 90 });

    expect(service.lowCredit()).toBeNull();
  });

  it('carries the figures the notice quotes, summed by the server', async () => {
    await readWith({ remaining: 60, packRemaining: 30, lowCredit: { isNoticed: true } });

    const notice = service.lowCredit();

    expect(notice?.spendable).toBe(90);
    expect(notice?.unit).toBe('Credits');
    expect(notice?.resetsAt).toBe('2026-04-01T00:00:00+01:00');
    expect(notice?.timeZoneId).toBe('Europe/London');
  });

  it('says nothing once the creator has put it away', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true, isDismissed: true } });

    expect(service.lowCredit()).toBeNull();
  });

  it('says nothing when the account has turned the notice off', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true, noticeEnabled: false } });

    expect(service.lowCredit()).toBeNull();
    expect(service.lowCreditNoticeEnabled()).toBeFalse();
  });

  /** Spent and switched off each say something stronger, and each say it for themselves. */
  it('says nothing to an account that is already spent or suspended', async () => {
    await readWith({ remaining: 0, packRemaining: 0, lowCredit: { isNoticed: true } });
    expect(service.lowCredit()).toBeNull();

    const reread = service.refresh();
    http.expectOne(URL).flush(payload({ remaining: 90, isSuspended: true, lowCredit: { isNoticed: true } }));
    await reread;

    expect(service.lowCredit()).toBeNull();
  });

  /** A notice offered on figures nobody is sure of would train a creator to ignore it. */
  it('says nothing while the figures are degraded', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true } });
    expect(service.lowCredit()).not.toBeNull();

    const refresh = service.refresh();
    http.expectOne(URL).error(new ProgressEvent('network'));
    await refresh;

    expect(service.state().status).toBe('degraded');
    expect(service.lowCredit()).toBeNull();
  });

  it('hides the notice at once when it is dismissed, and records it', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true } });

    const dismissing = service.dismissLowCreditNotice();

    // Hidden before the request has even been answered: the creator asked for it to go.
    expect(service.lowCredit()).toBeNull();

    const request = http.expectOne(`${NOTICE}/dismissal`);
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();

    request.flush(null, { status: 204, statusText: 'No Content' });
    await dismissing;

    expect(service.lowCredit()).toBeNull();
  });

  /** Nothing the creator typed depends on it landing, so a failure leaves it hidden rather than shouting. */
  it('leaves a failed dismissal hidden for the session', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true } });

    const dismissing = service.dismissLowCreditNotice();
    http.expectOne(`${NOTICE}/dismissal`).error(new ProgressEvent('network'));
    await dismissing;

    expect(service.lowCredit()).toBeNull();
  });

  /** A dismissal belongs to the period it was about: the next period is told again. */
  it('shows the next period’s notice after a roll, despite this session’s dismissal', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true } });

    const dismissing = service.dismissLowCreditNotice();
    http.expectOne(`${NOTICE}/dismissal`).flush(null, { status: 204, statusText: 'No Content' });
    await dismissing;

    const rolled = service.refresh();
    http.expectOne(URL).flush(
      payload({ remaining: 90, startsAt: '2026-04-01T00:00:00+01:00', lowCredit: { isNoticed: true } }),
    );
    await rolled;

    expect(service.lowCredit()).not.toBeNull();
  });

  it('records the setting and re-reads, so every surface agrees', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true } });

    const saving = service.setLowCreditNoticeEnabled(false);

    const request = http.expectOne(NOTICE);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ enabled: false });
    expect(request.request.withCredentials).toBeTrue();
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    http.expectOne(URL).flush(payload({ remaining: 90, lowCredit: { isNoticed: true, noticeEnabled: false } }));

    expect(await saving).toBeTrue();
    expect(service.lowCreditNoticeEnabled()).toBeFalse();
    expect(service.lowCredit()).toBeNull();
  });

  it('reports a setting that could not be recorded, and re-reads nothing', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true } });

    const saving = service.setLowCreditNoticeEnabled(false);
    http.expectOne(NOTICE).error(new ProgressEvent('network'));

    expect(await saving).toBeFalse();
    expect(service.lowCreditNoticeEnabled()).toBeTrue();
  });

  /**
   * A notice the creator put away stays away for the period, whatever they then do with the setting: the
   * server keeps the dismissal, and turning the notice off and on again does not clear it. Turning it on
   * does show one they have *not* yet put away, which is why the local hide is dropped.
   */
  it('leaves a dismissed notice dismissed when the setting is turned on again', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true } });

    const dismissing = service.dismissLowCreditNotice();
    http.expectOne(`${NOTICE}/dismissal`).flush(null, { status: 204, statusText: 'No Content' });
    await dismissing;

    const saving = service.setLowCreditNoticeEnabled(true);
    http.expectOne(NOTICE).flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    // What the server really sends after a dismissal: it is still dismissed.
    http.expectOne(URL).flush(payload({ remaining: 90, lowCredit: { isNoticed: true, isDismissed: true } }));
    await saving;

    expect(service.lowCredit()).toBeNull();
  });

  /** One the account is owed and has not put away does appear when the notice is switched back on. */
  it('shows an undismissed notice when the setting is turned on again', async () => {
    await readWith({ remaining: 90, lowCredit: { isNoticed: true, noticeEnabled: false } });
    expect(service.lowCredit()).toBeNull();

    const saving = service.setLowCreditNoticeEnabled(true);
    http.expectOne(NOTICE).flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    http.expectOne(URL).flush(payload({ remaining: 90, lowCredit: { isNoticed: true } }));
    await saving;

    expect(service.lowCredit()).not.toBeNull();
  });
});
