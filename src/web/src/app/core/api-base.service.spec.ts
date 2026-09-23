import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ApiBaseService } from './api-base.service';
import { RuntimeConfigService } from './runtime-config.service';

describe('ApiBaseService', () => {
  let apiBase: ApiBaseService;
  let runtimeConfig: RuntimeConfigService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    apiBase = TestBed.inject(ApiBaseService);
    runtimeConfig = TestBed.inject(RuntimeConfigService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('resolves an absolute URL against the gateway origin once config is ready (success)', async () => {
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    expect(apiBase.resolve()).toEqual({ status: 'ready', baseUrl: 'https://gateway.example' });
    expect(apiBase.url('/bff/session')).toBe('https://gateway.example/bff/session');
  });

  it('returns null while config is still loading (before app bootstrap resolves it)', () => {
    expect(runtimeConfig.state()).toEqual({ status: 'loading' });
    expect(apiBase.resolve()).toEqual({ status: 'unavailable' });
    expect(apiBase.url('/bff/session')).toBeNull();
  });

  it('degrades to unavailable when the config file is missing (missing-config)', async () => {
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush('not found', { status: 404, statusText: 'Not Found' });
    await loading;

    expect(apiBase.resolve()).toEqual({ status: 'unavailable' });
    expect(apiBase.url('/bff/session')).toBeNull();
  });

  it('degrades to unavailable when the config body is malformed (malformed-config)', async () => {
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'javascript:alert(1)' });
    await loading;

    expect(apiBase.resolve()).toEqual({ status: 'unavailable' });
    expect(apiBase.url('/bff/session')).toBeNull();
  });

  it('never throws when degraded, so a typed service can start in a degraded state (degraded-start)', async () => {
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').error(new ProgressEvent('error'));
    await loading;

    expect(() => apiBase.resolve()).not.toThrow();
    expect(() => apiBase.url('/api/v1/me')).not.toThrow();
    expect(apiBase.url('/api/v1/me')).toBeNull();
  });
});
