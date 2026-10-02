import { HttpClient, HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { BrandSetupSession, BrandSetupSessionWrite, decodeBrandSetupSession } from '../models/brand-setup.models';

/** `brand_setup_session_completed`: the session was already finished, so it can no longer be changed. */
const COMPLETED_CODE = 'brand_setup_session_completed';

export type BrandSetupSessionReadOutcome =
  | { readonly status: 'found'; readonly session: BrandSetupSession }
  /** 204: the creator has no setup session in this workspace. The first-use state, not a failure. */
  | { readonly status: 'none' }
  /** Unknown workspace, or one the caller cannot see. Deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type BrandSetupSessionWriteOutcome =
  | { readonly status: 'saved'; readonly session: BrandSetupSession }
  /** The stored session changed under this client. Nothing was written. */
  | { readonly status: 'conflict' }
  /** The session was already completed. Nothing was written. */
  | { readonly status: 'session_completed' }
  | { readonly status: 'validation_failed' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type BrandSetupSessionDeleteOutcome =
  | { readonly status: 'deleted' }
  /** 409 `brand_setup_session_conflict`: the row survived concurrent writes, so nothing was deleted. */
  | { readonly status: 'conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

function problemCodeOf(error: HttpErrorResponse): string | null {
  const body = error.error as Record<string, unknown> | null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

/**
 * The typed client for the creator's resumable "Create my voice" session. The caller owns `rowVersion` and
 * sends the latest one with every update; the server refuses a stale one with 409 rather than overwriting.
 * Nothing here activates guidance or touches sources or content. Components never inject `HttpClient`.
 */
@Injectable({ providedIn: 'root' })
export class BrandSetupSessionService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/brand-setup-session${suffix}`);
  }

  async getSession(workspaceSlug: string): Promise<BrandSetupSessionReadOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true, observe: 'response' }));
      if (response.status === 204) return { status: 'none' };
      const session = decodeBrandSetupSession(response.body);
      return session ? { status: 'found', session } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  /** Creates the session when `rowVersion` is null (no If-Match); otherwise replaces it, guarded by If-Match. */
  saveSession(workspaceSlug: string, body: BrandSetupSessionWrite, rowVersion: string | null): Promise<BrandSetupSessionWriteOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return Promise.resolve({ status: 'unavailable' });
    return this.write(this.http.put<unknown>(url, body, this.options(rowVersion)));
  }

  completeSession(workspaceSlug: string, rowVersion: string): Promise<BrandSetupSessionWriteOutcome> {
    const url = this.url(workspaceSlug, '/complete');
    if (!url) return Promise.resolve({ status: 'unavailable' });
    return this.write(this.http.post<unknown>(url, null, this.options(rowVersion)));
  }

  /** Removes only the creator's own draft; idempotent. Never touches an active guide, sources or content. */
  async deleteSession(workspaceSlug: string): Promise<BrandSetupSessionDeleteOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      await firstValueFrom(this.http.delete(url, { withCredentials: true }));
      return { status: 'deleted' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };
      if (error.status === 403) return { status: 'forbidden' };
      if (error.status === 404) return { status: 'not_found' };
      if (error.status === 409) return { status: 'conflict' };
      return { status: 'unavailable' };
    }
  }

  private options(rowVersion: string | null): {
    withCredentials: true;
    observe: 'response';
    headers?: Record<string, string>;
  } {
    return { withCredentials: true, observe: 'response', ...(rowVersion ? { headers: { 'If-Match': rowVersion } } : {}) };
  }

  /** Any 409 that is not the completed code is the stale-version conflict (`brand_setup_session_conflict`). */
  private async write(request: Observable<HttpResponse<unknown>>): Promise<BrandSetupSessionWriteOutcome> {
    try {
      const response = await firstValueFrom(request);
      const session = decodeBrandSetupSession(response.body);
      return session ? { status: 'saved', session } : { status: 'unavailable' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };
      if (error.status === 400) return { status: 'validation_failed' };
      if (error.status === 403) return { status: 'forbidden' };
      if (error.status === 404) return { status: 'not_found' };
      if (error.status === 409) {
        return problemCodeOf(error) === COMPLETED_CODE ? { status: 'session_completed' } : { status: 'conflict' };
      }
      return { status: 'unavailable' };
    }
  }
}
