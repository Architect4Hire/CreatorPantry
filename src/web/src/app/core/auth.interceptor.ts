import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';

import { ApiBaseService } from './api-base.service';
import { AntiforgeryService } from '../services/antiforgery.service';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS', 'TRACE']);
const ABSOLUTE_URL_PATTERN = /^[a-z][a-z0-9+.-]*:\/\//i;

/**
 * True for a relative /bff or /api path (always same-origin as the app, safe by construction), or an
 * absolute URL whose origin matches the actually-configured gateway origin. An absolute URL on some
 * other host that merely happens to share the /bff or /api path prefix must never be trusted with
 * credentials or the CSRF token.
 */
function targetsGatewaySession(url: string, apiBase: ApiBaseService): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url, location.origin);
  } catch {
    return false;
  }

  if (!(parsed.pathname.startsWith('/bff/') || parsed.pathname.startsWith('/api/'))) return false;
  if (!ABSOLUTE_URL_PATTERN.test(url)) return true;

  const resolution = apiBase.resolve();
  if (resolution.status !== 'ready') return false;
  try {
    return parsed.origin === new URL(resolution.baseUrl).origin;
  } catch {
    return false;
  }
}

/**
 * Every request to a /bff or /api path (relative or resolved to the gateway's actual configured origin)
 * sends credentials, since the session cookie is HttpOnly and cross-origin in local development. Unsafe
 * methods additionally get the X-XSRF-TOKEN header from AntiforgeryService — the gateway's antiforgery
 * cookie is HttpOnly too, so this is the only way the SPA can supply it.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const apiBase = inject(ApiBaseService);
  if (!targetsGatewaySession(req.url, apiBase)) return next(req);

  const withCredentials = req.clone({ withCredentials: true });
  if (SAFE_METHODS.has(req.method)) return next(withCredentials);

  const antiforgery = inject(AntiforgeryService);
  return from(antiforgery.getToken()).pipe(
    switchMap((token) => next(withCredentials.clone({ setHeaders: { 'X-XSRF-TOKEN': token } }))),
  );
};
