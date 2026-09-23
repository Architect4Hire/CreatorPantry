import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';

import { AuthService } from '../services/auth.service';

/**
 * Redirects to /sign-in unless the session is confirmed authenticated. A degraded check redirects too,
 * tagged for the sign-in page to explain why. `ensureChecked()` shouldn't reject today (its internal
 * request is self-catching), but a route guard that lets an unexpected rejection escape would fail the
 * whole navigation outright instead of redirecting — so it's treated the same as a degraded check.
 */
export const authGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  try {
    await auth.ensureChecked();
  } catch {
    return router.createUrlTree(['/sign-in'], { queryParams: { reason: 'degraded' } });
  }

  const session = auth.session();
  if (session.status === 'authenticated') return true;

  return router.createUrlTree(['/sign-in'], session.status === 'degraded' ? { queryParams: { reason: 'degraded' } } : {});
};
