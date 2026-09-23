import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RegistrationService } from './registration.service';
import { RuntimeConfigService } from '../core/runtime-config.service';

describe('RegistrationService', () => {
  let service: RegistrationService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RegistrationService);
  });

  afterEach(() => http.verify());

  describe('register', () => {
    it('reports success on a 202 pending-confirmation response', async () => {
      const register = service.register('robert@example.com', 'correct horse battery', 'Robert');
      const req = http.expectOne('https://gateway.example/api/v1/auth/register');
      expect(req.request.body).toEqual({ email: 'robert@example.com', password: 'correct horse battery', displayName: 'Robert' });
      expect(req.request.withCredentials).toBeTrue();
      req.flush({ status: 'pending_confirmation' }, { status: 202, statusText: 'Accepted' });

      expect(await register).toEqual({ status: 'success' });
    });

    it('decodes field errors on a 400 validation failure', async () => {
      const register = service.register('bad', 'short', '');
      http.expectOne('https://gateway.example/api/v1/auth/register').flush(
        { errors: { email: ['Enter a valid email address.'], password: ['Use at least 12 characters.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(await register).toEqual({
        status: 'invalid',
        fieldErrors: { email: ['Enter a valid email address.'], password: ['Use at least 12 characters.'] },
      });
    });

    it('reports unavailable when the API is unreachable', async () => {
      const register = service.register('robert@example.com', 'correct horse battery', 'Robert');
      http.expectOne('https://gateway.example/api/v1/auth/register').flush('down', { status: 503, statusText: 'Service Unavailable' });

      expect(await register).toEqual({ status: 'unavailable' });
    });
  });

  describe('confirmEmail', () => {
    it('reports success on a 200 confirmation response', async () => {
      const confirm = service.confirmEmail('robert@example.com', 'a-token');
      const req = http.expectOne('https://gateway.example/api/v1/auth/confirm-email');
      expect(req.request.body).toEqual({ email: 'robert@example.com', token: 'a-token' });
      req.flush({ status: 'email_confirmed' });

      expect(await confirm).toEqual({ status: 'success' });
    });

    it('reports invalid_link on a 400 (unknown email, bad, or expired token)', async () => {
      const confirm = service.confirmEmail('robert@example.com', 'stale-token');
      http.expectOne('https://gateway.example/api/v1/auth/confirm-email').flush(
        { code: 'auth.email_confirmation.invalid_token' },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(await confirm).toEqual({ status: 'invalid_link' });
    });

    it('reports unavailable when the API is unreachable', async () => {
      const confirm = service.confirmEmail('robert@example.com', 'a-token');
      http.expectOne('https://gateway.example/api/v1/auth/confirm-email').flush('down', { status: 503, statusText: 'Service Unavailable' });

      expect(await confirm).toEqual({ status: 'unavailable' });
    });
  });
});
