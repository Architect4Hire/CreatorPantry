import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AntiforgeryService } from './antiforgery.service';

export type SessionState =
  | { readonly status: 'checking' }
  | { readonly status: 'anonymous' }
  | { readonly status: 'authenticated'; readonly displayName: string }
  | { readonly status: 'expired' }
  | { readonly status: 'degraded' };

export type LoginOutcome =
  | { readonly status: 'success' }
  | { readonly status: 'invalid_credentials' }
  | { readonly status: 'email_unconfirmed' }
  | { readonly status: 'rate_limited' }
  | { readonly status: 'unavailable' };

interface SessionResponse {
  readonly authenticated: boolean;
  readonly displayName: string | null;
}

interface LoginResponse extends SessionResponse {
  readonly requestToken: string;
}

function decodeSessionResponse(value: unknown): SessionResponse | null {
  if (typeof value !== 'object' || value === null) return null;
  const record = value as Record<string, unknown>;
  const authenticated = record['authenticated'];
  const displayName = record['displayName'];
  if (typeof authenticated !== 'boolean') return null;
  if (displayName !== null && typeof displayName !== 'string') return null;
  return { authenticated, displayName: displayName ?? null };
}

function decodeLoginResponse(value: unknown): LoginResponse | null {
  const session = decodeSessionResponse(value);
  const requestToken = (value as Record<string, unknown> | null)?.['requestToken'];
  return session && typeof requestToken === 'string' ? { ...session, requestToken } : null;
}

/**
 * Session state lives here, not in components: `AuthService` is the only thing that calls the BFF's
 * /bff/session, /bff/login, and /bff/logout endpoints. Angular never sees an access or refresh token —
 * only this opaque, cookie-backed authenticated/anonymous signal.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);
  private readonly antiforgery = inject(AntiforgeryService);

  private readonly sessionSignal = signal<SessionState>({ status: 'checking' });
  readonly session = this.sessionSignal.asReadonly();

  private inFlightCheck: Promise<void> | null = null;
  private checkGeneration = 0;

  /**
   * Ensures the session has been checked at least once; safe to call from multiple guards concurrently.
   * A prior 'degraded' result (the check itself failed, e.g. a network blip) is retried on the next call
   * rather than cached forever — unlike 'anonymous'/'authenticated'/'expired', which are definitive
   * answers and are not re-checked here.
   */
  ensureChecked(): Promise<void> {
    if (this.inFlightCheck) return this.inFlightCheck;
    const status = this.sessionSignal().status;
    if (status === 'checking' || status === 'degraded') return this.checkSession();
    return Promise.resolve();
  }

  /**
   * Re-checks the session against the gateway. Distinguishes a first-time visitor from an expired one.
   * If a newer call to checkSession() starts before this one's response arrives, this call's result is
   * discarded — the most recently *started* check always wins, regardless of which resolves first.
   */
  async checkSession(): Promise<void> {
    const generation = ++this.checkGeneration;
    const isCurrent = () => generation === this.checkGeneration;

    const wasAuthenticated = this.sessionSignal().status === 'authenticated';
    this.sessionSignal.set({ status: 'checking' });

    const request = (async () => {
      const url = this.apiBase.url('/bff/session');
      if (!url) {
        if (isCurrent()) this.sessionSignal.set({ status: 'degraded' });
        return;
      }

      try {
        const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
        if (!isCurrent()) return;
        const response = decodeSessionResponse(raw);
        if (!response) {
          this.sessionSignal.set({ status: 'degraded' });
        } else if (response.authenticated) {
          this.sessionSignal.set({ status: 'authenticated', displayName: response.displayName ?? '' });
        } else {
          this.sessionSignal.set({ status: wasAuthenticated ? 'expired' : 'anonymous' });
        }
      } catch {
        if (isCurrent()) this.sessionSignal.set({ status: 'degraded' });
      }
    })();

    this.inFlightCheck = request;
    try {
      await request;
    } finally {
      if (this.inFlightCheck === request) this.inFlightCheck = null;
    }
  }

  async login(email: string, password: string): Promise<LoginOutcome> {
    const url = this.apiBase.url('/bff/login');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.post<unknown>(url, { email, password }, { withCredentials: true }));
      const response = decodeLoginResponse(raw);
      if (!response?.authenticated) return { status: 'unavailable' };

      this.antiforgery.setToken(response.requestToken);
      this.sessionSignal.set({ status: 'authenticated', displayName: response.displayName ?? '' });
      return { status: 'success' };
    } catch (error) {
      if (error instanceof HttpErrorResponse) {
        if (error.status === 429) return { status: 'rate_limited' };
        if (error.status === 400) {
          // auth.signin.failed and auth.signin.invalid_request both mean "try again with different
          // credentials"; auth.signin.email_unconfirmed is a distinct, actionable case the backend
          // already distinguishes by code, so the SPA shouldn't flatten it into generic bad-credentials.
          const code = (error.error as Record<string, unknown> | null)?.['code'];
          return code === 'auth.signin.email_unconfirmed' ? { status: 'email_unconfirmed' } : { status: 'invalid_credentials' };
        }
      }
      return { status: 'unavailable' };
    }
  }

  async logout(): Promise<void> {
    const url = this.apiBase.url('/bff/logout');
    if (url) {
      try {
        await firstValueFrom(this.http.post<unknown>(url, null, { withCredentials: true }));
      } catch {
        // Logout is always treated as successful client-side — the server-side ticket is
        // best-effort revoked; the browser's cookie is what actually gates future requests.
      }
    }
    this.antiforgery.invalidate();
    this.sessionSignal.set({ status: 'anonymous' });
  }
}
