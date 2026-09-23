import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { authInterceptor } from './auth.interceptor';
import { ApiBaseService } from './api-base.service';
import { AntiforgeryService } from '../services/antiforgery.service';

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AntiforgeryService, useValue: { getToken: () => Promise.resolve('the-csrf-token') } },
        { provide: ApiBaseService, useValue: { resolve: () => ({ status: 'ready', baseUrl: 'https://gateway.example' }) } },
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('adds withCredentials to a GET request against /bff, without a CSRF header', () => {
    http.get('/bff/session').subscribe();
    const req = httpMock.expectOne('/bff/session');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.headers.has('X-XSRF-TOKEN')).toBeFalse();
    req.flush({});
  });

  it('adds both withCredentials and the CSRF header to an unsafe request against /bff (relative URL)', async () => {
    http.post('/bff/login', { email: 'a', password: 'b' }).subscribe();
    await Promise.resolve().then(() => Promise.resolve()); // let the getToken() promise chain settle before the request is dispatched
    const req = httpMock.expectOne('/bff/login');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.headers.get('X-XSRF-TOKEN')).toBe('the-csrf-token');
    req.flush({});
  });

  it('adds both withCredentials and the CSRF header to an unsafe request against the configured gateway origin', async () => {
    http.post('https://gateway.example/api/v1/workspaces', {}).subscribe();
    await Promise.resolve().then(() => Promise.resolve());
    const req = httpMock.expectOne('https://gateway.example/api/v1/workspaces');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.headers.get('X-XSRF-TOKEN')).toBe('the-csrf-token');
    req.flush({});
  });

  it('leaves requests to unrelated paths untouched', () => {
    http.get('/runtime-config.json').subscribe();
    const req = httpMock.expectOne('/runtime-config.json');
    expect(req.request.withCredentials).toBeFalse();
    expect(req.request.headers.has('X-XSRF-TOKEN')).toBeFalse();
    req.flush({});
  });

  it('does not attach credentials or the CSRF token to an absolute URL on a different origin, even with a matching path', () => {
    http.post('https://not-the-gateway.example/api/v1/workspaces', {}).subscribe();
    const req = httpMock.expectOne('https://not-the-gateway.example/api/v1/workspaces');
    expect(req.request.withCredentials).toBeFalse();
    expect(req.request.headers.has('X-XSRF-TOKEN')).toBeFalse();
    req.flush({});
  });

  it('does not treat near-miss paths as gateway session paths', () => {
    for (const url of ['/apiFoo', '/bff-status', '/API/v1/me', '/BFF/session']) {
      http.get(url).subscribe();
      const req = httpMock.expectOne(url);
      expect(req.request.withCredentials).withContext(url).toBeFalse();
      req.flush({});
    }
  });

  it('treats the bare /api and /bff paths (no trailing slash) as not matching, consistent with the prefix check', () => {
    for (const url of ['/api', '/bff']) {
      http.get(url).subscribe();
      const req = httpMock.expectOne(url);
      expect(req.request.withCredentials).withContext(url).toBeFalse();
      req.flush({});
    }
  });
});
