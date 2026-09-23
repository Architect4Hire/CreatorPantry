import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';

import { authGuard } from './auth.guard';
import { AuthService, SessionState } from '../services/auth.service';

describe('authGuard', () => {
  function configure(session: SessionState) {
    const authServiceStub = { ensureChecked: () => Promise.resolve(), session: () => session };
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: authServiceStub },
        { provide: Router, useValue: { createUrlTree: jasmine.createSpy('createUrlTree').and.returnValue(new UrlTree()) } },
      ],
    });
  }

  it('allows activation when authenticated', async () => {
    configure({ status: 'authenticated', displayName: 'Robert' });
    const result = await TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));
    expect(result).toBeTrue();
  });

  it('redirects to /sign-in when anonymous, without a degraded reason', async () => {
    configure({ status: 'anonymous' });
    await TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));

    const router = TestBed.inject(Router);
    expect(router.createUrlTree).toHaveBeenCalledWith(['/sign-in'], {});
  });

  it('redirects to /sign-in with a degraded reason when the session check failed', async () => {
    configure({ status: 'degraded' });
    await TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));

    const router = TestBed.inject(Router);
    expect(router.createUrlTree).toHaveBeenCalledWith(['/sign-in'], { queryParams: { reason: 'degraded' } });
  });

  it('redirects to /sign-in for an expired session', async () => {
    configure({ status: 'expired' });
    await TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));

    const router = TestBed.inject(Router);
    expect(router.createUrlTree).toHaveBeenCalledWith(['/sign-in'], {});
  });

  it('redirects to /sign-in with a degraded reason instead of failing the navigation if ensureChecked() rejects', async () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { ensureChecked: () => Promise.reject(new Error('boom')), session: () => ({ status: 'checking' }) } },
        { provide: Router, useValue: { createUrlTree: jasmine.createSpy('createUrlTree').and.returnValue(new UrlTree()) } },
      ],
    });

    const result = await TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));

    const router = TestBed.inject(Router);
    expect(router.createUrlTree).toHaveBeenCalledWith(['/sign-in'], { queryParams: { reason: 'degraded' } });
    expect(result).toBeInstanceOf(UrlTree);
  });
});
