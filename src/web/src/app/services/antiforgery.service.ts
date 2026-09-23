import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';

interface AntiforgeryResponse {
  readonly requestToken: string;
}

function decodeAntiforgeryResponse(value: unknown): string | null {
  if (typeof value !== 'object' || value === null) return null;
  const token = (value as Record<string, unknown>)['requestToken'];
  return typeof token === 'string' && token.length > 0 ? token : null;
}

/**
 * Caches the CSRF request token the gateway issues at GET /bff/antiforgery. The gateway's antiforgery
 * cookie is HttpOnly, so this token — read from the JSON response body, never the cookie — is the only
 * way client code can supply it back as the X-XSRF-TOKEN header on unsafe requests (see auth.interceptor.ts).
 */
@Injectable({ providedIn: 'root' })
export class AntiforgeryService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private cachedToken: Promise<string> | null = null;

  /** Returns the cached token, fetching one if none is cached yet. A failed fetch is never cached, so the next call retries. */
  getToken(): Promise<string> {
    this.cachedToken ??= this.fetchToken().catch((error: unknown) => {
      this.cachedToken = null;
      throw error;
    });
    return this.cachedToken;
  }

  /** Adopts a fresh token issued alongside another response (e.g. a successful login), skipping a fetch. */
  setToken(token: string): void {
    this.cachedToken = Promise.resolve(token);
  }

  /** Clears the cached token so the next `getToken()` call fetches a fresh one. */
  invalidate(): void {
    this.cachedToken = null;
  }

  private async fetchToken(): Promise<string> {
    const url = this.apiBase.url('/bff/antiforgery');
    if (!url) throw new Error('Cannot fetch an antiforgery token before runtime configuration is ready.');

    const response = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
    const token = decodeAntiforgeryResponse(response);
    if (!token) throw new Error('Antiforgery response was malformed.');
    return token;
  }
}
