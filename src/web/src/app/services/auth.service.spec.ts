import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AntiforgeryService } from './antiforgery.service';
import { AuthService } from './auth.service';
import { RuntimeConfigService } from '../core/runtime-config.service';

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(AuthService);
  });

  afterEach(() => http.verify());

  it('starts in the checking state', () => {
    expect(service.session()).toEqual({ status: 'checking' });
  });

  describe('checkSession', () => {
    it('becomes authenticated when the gateway confirms a session (success)', async () => {
      const checking = service.checkSession();
      const req = http.expectOne('https://gateway.example/bff/session');
      expect(req.request.withCredentials).toBeTrue();
      req.flush({ authenticated: true, displayName: 'Robert' });
      await checking;

      expect(service.session()).toEqual({ status: 'authenticated', displayName: 'Robert' });
    });

    it('becomes anonymous for a first-time visitor (never previously authenticated)', async () => {
      const checking = service.checkSession();
      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: false, displayName: null });
      await checking;

      expect(service.session()).toEqual({ status: 'anonymous' });
    });

    it('becomes expired (not anonymous) when a previously-authenticated session is no longer valid', async () => {
      const first = service.checkSession();
      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: true, displayName: 'Robert' });
      await first;

      const second = service.checkSession();
      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: false, displayName: null });
      await second;

      expect(service.session()).toEqual({ status: 'expired' });
    });

    it('becomes degraded when the gateway cannot be reached', async () => {
      const checking = service.checkSession();
      http.expectOne('https://gateway.example/bff/session').flush('down', { status: 503, statusText: 'Service Unavailable' });
      await checking;

      expect(service.session()).toEqual({ status: 'degraded' });
    });

    it('becomes degraded on a 200 response with a malformed body', async () => {
      const checking = service.checkSession();
      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: 'yes' }); // wrong type
      await checking;

      expect(service.session()).toEqual({ status: 'degraded' });
    });
  });

  describe('ensureChecked', () => {
    it('checks once and lets concurrent callers share the in-flight request', async () => {
      const first = service.ensureChecked();
      const second = service.ensureChecked();

      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: true, displayName: 'Robert' });
      await Promise.all([first, second]);

      expect(service.session()).toEqual({ status: 'authenticated', displayName: 'Robert' });
    });

    it('does not re-check once already resolved to a definitive state', async () => {
      const first = service.checkSession();
      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: false, displayName: null });
      await first;
      expect(service.session()).toEqual({ status: 'anonymous' });

      await service.ensureChecked();
      http.expectNone('https://gateway.example/bff/session');
    });

    it('retries on the next call after landing in degraded, instead of caching the failure forever', async () => {
      const first = service.ensureChecked();
      http.expectOne('https://gateway.example/bff/session').flush('down', { status: 503, statusText: 'Service Unavailable' });
      await first;
      expect(service.session()).toEqual({ status: 'degraded' });

      const second = service.ensureChecked();
      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: true, displayName: 'Robert' });
      await second;

      expect(service.session()).toEqual({ status: 'authenticated', displayName: 'Robert' });
    });
  });

  describe('overlapping checkSession calls', () => {
    it('lets the most recently started call win, even if its response resolves before an earlier, now-stale call\'s', async () => {
      const firstCall = service.checkSession();
      const firstReq = http.expectOne('https://gateway.example/bff/session');

      const secondCall = service.checkSession();
      const secondReq = http.expectOne('https://gateway.example/bff/session');

      // The second (later-started) call's response arrives first...
      secondReq.flush({ authenticated: true, displayName: 'Robert' });
      await secondCall;
      expect(service.session()).toEqual({ status: 'authenticated', displayName: 'Robert' });

      // ...and the first (now-stale) call's response arrives after — it must not overwrite the newer result.
      firstReq.flush({ authenticated: false, displayName: null });
      await firstCall;
      expect(service.session()).toEqual({ status: 'authenticated', displayName: 'Robert' });
    });
  });

  describe('login', () => {
    it('authenticates and adopts the fresh antiforgery token on success', async () => {
      const login = service.login('robert@example.com', 'correct-password');
      const req = http.expectOne('https://gateway.example/bff/login');
      expect(req.request.body).toEqual({ email: 'robert@example.com', password: 'correct-password' });
      expect(req.request.withCredentials).toBeTrue();
      req.flush({ authenticated: true, displayName: 'Robert', requestToken: 'fresh-token' });

      expect(await login).toEqual({ status: 'success' });
      expect(service.session()).toEqual({ status: 'authenticated', displayName: 'Robert' });

      const antiforgery = TestBed.inject(AntiforgeryService);
      expect(await antiforgery.getToken()).toBe('fresh-token');
    });

    it('reports invalid_credentials on a 400 with the generic auth.signin.failed code', async () => {
      const login = service.login('robert@example.com', 'wrong-password');
      http.expectOne('https://gateway.example/bff/login').flush({ code: 'auth.signin.failed' }, { status: 400, statusText: 'Bad Request' });

      expect(await login).toEqual({ status: 'invalid_credentials' });
    });

    it('reports the distinct email_unconfirmed outcome on a 400 with that specific code', async () => {
      const login = service.login('robert@example.com', 'correct-password');
      http.expectOne('https://gateway.example/bff/login').flush({ code: 'auth.signin.email_unconfirmed' }, { status: 400, statusText: 'Bad Request' });

      expect(await login).toEqual({ status: 'email_unconfirmed' });
    });

    it('reports unavailable on a 200 response with a malformed body', async () => {
      const login = service.login('robert@example.com', 'correct-password');
      http.expectOne('https://gateway.example/bff/login').flush({ authenticated: true, displayName: 'Robert' }); // missing requestToken

      expect(await login).toEqual({ status: 'unavailable' });
      expect(service.session()).toEqual({ status: 'checking' });
    });

    it('reports rate_limited on a 429', async () => {
      const login = service.login('robert@example.com', 'x');
      http.expectOne('https://gateway.example/bff/login').flush('slow down', { status: 429, statusText: 'Too Many Requests' });

      expect(await login).toEqual({ status: 'rate_limited' });
    });

    it('reports unavailable when the API is unreachable (503)', async () => {
      const login = service.login('robert@example.com', 'x');
      http.expectOne('https://gateway.example/bff/login').flush('down', { status: 503, statusText: 'Service Unavailable' });

      expect(await login).toEqual({ status: 'unavailable' });
    });
  });

  describe('logout', () => {
    it('returns to anonymous and invalidates the cached antiforgery token', async () => {
      const antiforgery = TestBed.inject(AntiforgeryService);
      antiforgery.setToken('stale-token');

      const first = service.checkSession();
      http.expectOne('https://gateway.example/bff/session').flush({ authenticated: true, displayName: 'Robert' });
      await first;

      const logout = service.logout();
      http.expectOne('https://gateway.example/bff/logout').flush(null);
      await logout;

      expect(service.session()).toEqual({ status: 'anonymous' });

      const pending = antiforgery.getToken();
      http.expectOne('https://gateway.example/bff/antiforgery').flush({ requestToken: 'new-token' });
      expect(await pending).toBe('new-token');
    });

    it('still clears local session state even if the logout request fails', async () => {
      const logout = service.logout();
      http.expectOne('https://gateway.example/bff/logout').flush('down', { status: 503, statusText: 'Service Unavailable' });
      await logout;

      expect(service.session()).toEqual({ status: 'anonymous' });
    });
  });
});
