import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { decodeBillingRedirect } from '../models/billing.models';
import { BillingService } from './billing.service';

const BASE = 'https://gateway.example/api/v1/me/billing';

describe('BillingService', () => {
  let service: BillingService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const loading = TestBed.inject(RuntimeConfigService).load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(BillingService);
  });

  afterEach(() => http.verify());

  it('reads the account’s billing with its session', async () => {
    const pending = service.read();

    const request = http.expectOne(BASE);
    expect(request.request.withCredentials).toBeTrue();
    request.flush({
      paymentsAvailable: true,
      isTestMode: true,
      plan: {
        key: 'trial',
        name: 'Trial',
        credits: 1000,
        period: 'Once',
        dailyImageCap: 5,
        isTrial: true,
        trialEndsAt: '2026-11-12T09:00:00+00:00',
        trialEnded: false,
      },
      subscription: null,
      packBalance: 0,
      canSubscribe: true,
      canBuyPacks: true,
    });

    const billing = await pending;
    expect(billing?.plan?.name).toBe('Trial');
    expect(billing?.canSubscribe).toBeTrue();
  });

  it('starts a pack’s Checkout under the caller’s key, naming this page’s origin to come back to', async () => {
    const pending = service.startPackCheckout('small', 'key-1');

    const request = http.expectOne(`${BASE}/checkout/pack`);
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.body).toEqual({ packKey: 'small', returnOrigin: window.location.origin });
    request.flush({ url: 'https://checkout.stripe.test/c/1', scheduled: false });

    expect(await pending).toEqual({ status: 'redirect', redirect: { url: 'https://checkout.stripe.test/c/1', scheduled: false } });
  });

  it('reads a scheduled plan change as nothing to go to', async () => {
    const pending = service.changePlan('creator', 'Monthly', 'key-1');

    http.expectOne(`${BASE}/plan-change`).flush({ url: null, scheduled: true });

    expect(await pending).toEqual({ status: 'redirect', redirect: { url: null, scheduled: true } });
  });

  it('says a refusal in the server’s words, and asks for a re-read on a conflict', async () => {
    const pending = service.startPlanCheckout('creator', 'Monthly', 'key-1');

    http.expectOne(`${BASE}/checkout/subscription`).flush(
      { code: 'billing.checkout.subscribed.conflict', title: 'You already have a plan.' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(await pending).toEqual({
      status: 'refused',
      code: 'billing.checkout.subscribed.conflict',
      message: 'You already have a plan.',
      reread: true,
    });
  });

  it('reads a 503 as payments unavailable', async () => {
    const pending = service.openPortal();

    http.expectOne(`${BASE}/portal`).flush(
      { code: 'billing.payments.unavailable', title: 'Payments aren’t available.' },
      { status: 503, statusText: 'Unavailable' },
    );

    expect(await pending).toEqual({ status: 'unavailable' });
  });

  it('withdraws a scheduled change under the caller’s key', async () => {
    const pending = service.withdrawPlanChange('key-2');

    const request = http.expectOne(`${BASE}/plan-change`);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-2');
    request.flush(null, { status: 204, statusText: 'No Content' });

    expect(await pending).toEqual({ status: 'done' });
  });

  it('asks whether a Checkout has landed, naming what it was for', async () => {
    const pending = service.checkout('cs_test_1', 'Pack');

    const request = http.expectOne((req) => req.url === `${BASE}/checkout/cs_test_1`);
    expect(request.request.params.get('kind')).toBe('Pack');
    request.flush({ state: 'Complete', planName: null, credits: 2500 });

    expect(await pending).toEqual({ state: 'Complete', planName: null, credits: 2500 });
  });

  it('never goes anywhere but an https page the server named', () => {
    expect(decodeBillingRedirect({ url: 'http://checkout.stripe.test/c/1' })).toBeNull();
    expect(decodeBillingRedirect({ url: 'javascript:alert(1)' })).toBeNull();
    expect(decodeBillingRedirect({ url: null, scheduled: false })).toBeNull();
  });
});
