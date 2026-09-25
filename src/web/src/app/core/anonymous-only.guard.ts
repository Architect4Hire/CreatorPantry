import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';

import { AuthService } from '../services/auth.service';

/**
 * Keeps the public landing route out of an authenticated visitor's way by sending them straight into
 * the app instead. Unlike `authGuard`, a degraded or in-flight session check does not redirect here —
 * the landing page needs no session data to render, so the safe default for any non-authenticated
 * status (including a failed check) is to just show it rather than trap the visitor mid-navigation.
 */
export const anonymousOnlyGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  try {
    await auth.ensureChecked();
  } catch {
    return true;
  }

  const session = auth.session();
  if (session.status === 'authenticated') return router.createUrlTree(['/app']);

  return true;
};
