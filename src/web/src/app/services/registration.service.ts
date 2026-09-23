import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';

export type RegisterOutcome =
  | { readonly status: 'success' }
  | { readonly status: 'invalid'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'unavailable' };

export type ConfirmEmailOutcome =
  | { readonly status: 'success' }
  | { readonly status: 'invalid_link' }
  | { readonly status: 'unavailable' };

function decodeFieldErrors(value: unknown): Record<string, readonly string[]> {
  const errors = (value as Record<string, unknown> | null)?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }
  return result;
}

/**
 * Calls the account-creation routes under `/api/v1/auth/**` directly (not `/bff/**`; see `AuthService`,
 * which is scoped to the browser session). Registration and confirmation never establish a session
 * themselves — a confirmed account still has to sign in through `AuthService.login()`.
 */
@Injectable({ providedIn: 'root' })
export class RegistrationService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * The response is identical whether or not the email already has an account, so a caller can never
   * learn which case occurred — 'success' here means only "the request was accepted", not "an account
   * was created".
   */
  async register(email: string, password: string, displayName: string): Promise<RegisterOutcome> {
    const url = this.apiBase.url('/api/v1/auth/register');
    if (!url) return { status: 'unavailable' };

    try {
      await firstValueFrom(this.http.post<unknown>(url, { email, password, displayName }, { withCredentials: true }));
      return { status: 'success' };
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 400) {
        return { status: 'invalid', fieldErrors: decodeFieldErrors(error.error) };
      }
      return { status: 'unavailable' };
    }
  }

  async confirmEmail(email: string, token: string): Promise<ConfirmEmailOutcome> {
    const url = this.apiBase.url('/api/v1/auth/confirm-email');
    if (!url) return { status: 'unavailable' };

    try {
      await firstValueFrom(this.http.post<unknown>(url, { email, token }, { withCredentials: true }));
      return { status: 'success' };
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 400) {
        return { status: 'invalid_link' };
      }
      return { status: 'unavailable' };
    }
  }
}
