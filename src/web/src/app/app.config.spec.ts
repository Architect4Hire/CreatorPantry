import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { appConfig } from './app.config';

describe('appConfig wiring', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    // provideHttpClientTesting() after appConfig.providers overrides just the backend, keeping the
    // real interceptor chain and app initializer from appConfig itself under test.
    TestBed.configureTestingModule({ providers: [...appConfig.providers, provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('registers authInterceptor: a /bff request made through appConfig gets withCredentials', () => {
    TestBed.inject(HttpClient).get('/bff/session').subscribe();
    const req = http.expectOne('/bff/session');
    expect(req.request.withCredentials).toBeTrue();
    req.flush({});
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
  });

  it('gates app initialization on RuntimeConfigService.load() completing', async () => {
    const initStatus = TestBed.inject(ApplicationInitStatus);
    let resolved = false;
    initStatus.donePromise.then(() => (resolved = true));

    await Promise.resolve();
    expect(resolved).toBeFalse();

    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await initStatus.donePromise;

    expect(resolved).toBeTrue();
  });

  it('applies the theme (data-cp-theme on <html>) as part of app initialization, before any route renders', async () => {
    // CpThemeService's app initializer runs synchronously as soon as the environment injector is
    // created (during the outer beforeEach's TestBed.inject(HttpTestingController) above), so the
    // attribute is already set by the time this test body runs; awaiting donePromise below just
    // confirms initialization as a whole still completes with the pending runtime-config request.
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await TestBed.inject(ApplicationInitStatus).donePromise;

    expect(['light', 'dark']).toContain(document.documentElement.dataset['cpTheme'] ?? '');
  });
});
