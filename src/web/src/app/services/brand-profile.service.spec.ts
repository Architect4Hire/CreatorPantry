import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { BrandProfileService } from './brand-profile.service';

const WIRE = {
  id: 'p1',
  brandName: 'Sam',
  shortDescription: null,
  defaultAudience: null,
  locale: null,
  timeZoneId: null,
  channelDefaults: [],
  links: [],
  assets: [],
  revision: 1,
  createdAt: '2026-09-30T12:00:00Z',
  updatedAt: '2026-09-30T12:00:00Z',
  concurrencyToken: 'tok',
};

const URL = 'https://gateway.example/api/v1/workspaces/w/brand-profile';

describe('BrandProfileService', () => {
  let service: BrandProfileService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(BrandProfileService);
  });

  afterEach(() => http.verify());

  function problem(status: number, code: string, errors?: Record<string, string[]>): [object, { status: number; statusText: string }] {
    return [{ code, ...(errors ? { errors } : {}) }, { status, statusText: 'x' }];
  }

  it('reads a profile with credentials', async () => {
    const pending = service.getBrandProfile('w');
    const request = http.expectOne(URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(WIRE);

    const outcome = await pending;
    expect(outcome.status).toBe('found');
  });

  it('tells the first-use 404 from a workspace 404 by problem code', async () => {
    const first = service.getBrandProfile('w');
    http.expectOne(URL).flush(...problem(404, 'brand.profile.not_found'));
    expect((await first).status).toBe('first_use');

    const unknown = service.getBrandProfile('w');
    http.expectOne(URL).flush(...problem(404, 'not_found'));
    expect((await unknown).status).toBe('not_found');
  });

  it('reports a failed or undecodable read as unavailable', async () => {
    const failed = service.getBrandProfile('w');
    http.expectOne(URL).flush('', { status: 500, statusText: 'x' });
    expect((await failed).status).toBe('unavailable');

    const garbage = service.getBrandProfile('w');
    http.expectOne(URL).flush({ id: 1 });
    expect((await garbage).status).toBe('unavailable');
  });

  it('sends create as POST with the idempotency key and reports a replay', async () => {
    const pending = service.createBrandProfile('w', { brandName: 'Sam' }, 'key-1');
    const request = http.expectOne(URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.body).toEqual({ brandName: 'Sam' });
    request.flush(WIRE, { headers: { 'Idempotent-Replayed': 'true' } });

    expect(await pending).toEqual(jasmine.objectContaining({ status: 'saved', replayed: true }));
  });

  it('sends update as PATCH', async () => {
    const pending = service.updateBrandProfile('w', { expectedConcurrencyToken: 'tok' });
    const request = http.expectOne(URL);
    expect(request.request.method).toBe('PATCH');
    request.flush(WIRE);

    expect(await pending).toEqual(jasmine.objectContaining({ status: 'saved', replayed: false }));
  });

  it('maps each failure status to its outcome', async () => {
    const cases: readonly [number, string, string][] = [
      [403, 'brand.profile.forbidden', 'forbidden'],
      [404, 'brand.profile.not_found', 'profile_missing'],
      [409, 'brand.profile.conflict', 'conflict'],
      [409, 'brand.profile.exists.conflict', 'already_exists'],
      [422, 'brand.assets.unprocessable', 'assets_unavailable'],
      [422, 'idempotency.key_reused', 'idempotency_key_conflict'],
      [422, 'something.new', 'unavailable'],
      [500, 'internal_error', 'unavailable'],
    ];

    for (const [status, code, expected] of cases) {
      const pending = service.updateBrandProfile('w', {});
      http.expectOne(URL).flush(...problem(status, code));
      expect((await pending).status).withContext(`${status} ${code}`).toBe(expected);
    }
  });

  it('keeps the field errors of a 400', async () => {
    const pending = service.createBrandProfile('w', {});
    http.expectOne(URL).flush(...problem(400, 'brand.profile.invalid_request', { brandName: ['A brand needs a name.'] }));

    expect(await pending).toEqual({ status: 'validation_failed', fieldErrors: { brandName: ['A brand needs a name.'] } });
  });

  it('reads the channel list, and reports a bad response as unavailable', async () => {
    const pending = service.listContentChannels();
    http.expectOne('https://gateway.example/api/v1/reference/content-channels').flush([
      { key: 'instagram', displayName: 'Instagram', isActive: true },
    ]);
    expect(await pending).toEqual({ status: 'found', channels: [{ key: 'instagram', displayName: 'Instagram', isActive: true }] });

    const bad = service.listContentChannels();
    http.expectOne('https://gateway.example/api/v1/reference/content-channels').flush({ nope: true });
    expect((await bad).status).toBe('unavailable');
  });
});
