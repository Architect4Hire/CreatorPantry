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
});
