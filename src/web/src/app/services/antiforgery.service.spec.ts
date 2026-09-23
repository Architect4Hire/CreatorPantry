import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AntiforgeryService } from './antiforgery.service';
import { RuntimeConfigService } from '../core/runtime-config.service';

describe('AntiforgeryService', () => {
  let service: AntiforgeryService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(AntiforgeryService);
  });

  afterEach(() => http.verify());

  it('fetches and caches the token, issuing only one request across repeated calls', async () => {
    const first = service.getToken();
    http.expectOne('https://gateway.example/bff/antiforgery').flush({ requestToken: 'token-a' });
    expect(await first).toBe('token-a');

    expect(await service.getToken()).toBe('token-a');
  });

  it('sends withCredentials since the antiforgery cookie is HttpOnly', async () => {
    const pending = service.getToken();
    const req = http.expectOne('https://gateway.example/bff/antiforgery');
    expect(req.request.withCredentials).toBeTrue();
    req.flush({ requestToken: 'token-a' });
    await pending;
  });

  it('adopts a token via setToken without issuing a fetch', async () => {
    service.setToken('token-from-login');
    expect(await service.getToken()).toBe('token-from-login');
  });

  it('fetches again after invalidate', async () => {
    service.setToken('stale-token');
    service.invalidate();

    const pending = service.getToken();
    http.expectOne('https://gateway.example/bff/antiforgery').flush({ requestToken: 'fresh-token' });
    expect(await pending).toBe('fresh-token');
  });

  it('rejects when the response body is malformed', async () => {
    const pending = service.getToken();
    http.expectOne('https://gateway.example/bff/antiforgery').flush({});
    await expectAsync(pending).toBeRejected();
  });

  it('retries on the next call after a failed fetch, instead of caching the rejection forever', async () => {
    const first = service.getToken();
    http.expectOne('https://gateway.example/bff/antiforgery').flush({});
    await expectAsync(first).toBeRejected();

    const second = service.getToken();
    http.expectOne('https://gateway.example/bff/antiforgery').flush({ requestToken: 'recovered-token' });
    expect(await second).toBe('recovered-token');
  });

  it('issues only one request for concurrent callers that all fail together, then recovers on the next call', async () => {
    const callA = service.getToken();
    const callB = service.getToken();
    const callC = service.getToken();

    http.expectOne('https://gateway.example/bff/antiforgery').flush({});

    await expectAsync(callA).toBeRejected();
    await expectAsync(callB).toBeRejected();
    await expectAsync(callC).toBeRejected();

    const recovered = service.getToken();
    http.expectOne('https://gateway.example/bff/antiforgery').flush({ requestToken: 'recovered-token' });
    expect(await recovered).toBe('recovered-token');
  });
});
