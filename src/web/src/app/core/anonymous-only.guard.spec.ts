import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';

import { anonymousOnlyGuard } from './anonymous-only.guard';
import { AuthService, SessionState } from '../services/auth.service';

describe('anonymousOnlyGuard', () => {
  function configure(session: SessionState, ensureChecked: () => Promise<void> = () => Promise.resolve()) {
    const authServiceStub = { ensureChecked, session: () => session };
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: authServiceStub },
        { provide: Router, useValue: { createUrlTree: jasmine.createSpy('createUrlTree').and.returnValue(new UrlTree()) } },
      ],
    });
  }

  it('redirects to /app when authenticated', async () => {
    configure({ status: 'authenticated', displayName: 'Robert' });
    const result = await TestBed.runInInjectionContext(() => anonymousOnlyGuard({} as never, {} as never));

    const router = TestBed.inject(Router);
    expect(router.createUrlTree).toHaveBeenCalledWith(['/app']);
    expect(result).toBeInstanceOf(UrlTree);
  });

  it('allows activation when anonymous', async () => {
    configure({ status: 'anonymous' });
    const result = await TestBed.runInInjectionContext(() => anonymousOnlyGuard({} as never, {} as never));
    expect(result).toBeTrue();
  });

  it('allows activation for an expired session', async () => {
    configure({ status: 'expired' });
    const result = await TestBed.runInInjectionContext(() => anonymousOnlyGuard({} as never, {} as never));
    expect(result).toBeTrue();
  });

  it('allows activation when the session check is degraded, rather than trapping the visitor', async () => {
    configure({ status: 'degraded' });
    const result = await TestBed.runInInjectionContext(() => anonymousOnlyGuard({} as never, {} as never));
    expect(result).toBeTrue();
  });

  it('allows activation if ensureChecked() rejects', async () => {
    configure({ status: 'checking' }, () => Promise.reject(new Error('boom')));
    const result = await TestBed.runInInjectionContext(() => anonymousOnlyGuard({} as never, {} as never));
    expect(result).toBeTrue();
  });
});
