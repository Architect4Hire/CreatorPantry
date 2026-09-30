import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { BrandProfile, ContentChannel, decodeBrandProfile, decodeContentChannels } from '../models/brand-profile.models';

type FieldErrors = Readonly<Record<string, readonly string[]>>;

/** `BrandErrorCodes.ProfileNotFound`: the workspace is fine and has no profile yet. */
const PROFILE_NOT_FOUND_CODE = 'brand.profile.not_found';

/** `BrandErrorCodes.AlreadyExistsConflict`: a profile appeared since the page loaded. */
const ALREADY_EXISTS_CODE = 'brand.profile.exists.conflict';

/** `BrandErrorCodes.AssetsUnprocessable`: logo links are not accepted yet. */
const ASSETS_UNPROCESSABLE_CODE = 'brand.assets.unprocessable';

/** `IdempotencyPolicy.KeyReusedCode`: the key belongs to a request with a different body. */
const IDEMPOTENCY_KEY_REUSED_CODE = 'idempotency.key_reused';

export type BrandProfileOutcome =
  | { readonly status: 'found'; readonly profile: BrandProfile }
  /** The workspace is readable and has no profile yet: the first-use state, not a failure. */
  | { readonly status: 'first_use' }
  /** Unknown workspace, or one the caller cannot see. Deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type SaveBrandProfileOutcome =
  | { readonly status: 'saved'; readonly profile: BrandProfile; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: FieldErrors }
  /** The profile moved on since it was read. Nothing was written. */
  | { readonly status: 'conflict' }
  /** Create only: someone created the profile first. */
  | { readonly status: 'already_exists' }
  /** Update only: the profile no longer exists. */
  | { readonly status: 'profile_missing' }
  /** Logo links are refused until the media library exists. */
  | { readonly status: 'assets_unavailable' }
  /** The idempotency key was reused for a different body. */
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

export type ContentChannelsOutcome =
  | { readonly status: 'found'; readonly channels: readonly ContentChannel[] }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body, which tells apart responses that share a status. */
function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

/** `ValidationProblemDetails.errors`, keyed by the request's camelCase field path. */
function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const errors = body?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }
  return result;
}

function idempotencyHeaders(key: string | undefined): { headers: Record<string, string> } | Record<string, never> {
  return key ? { headers: { 'Idempotency-Key': key } } : {};
}

/**
 * The typed client for the workspace brand profile and the content-channel vocabulary. The caller owns the
 * idempotency key across retries of one logical save and round-trips `concurrencyToken` into the next write.
 * Components never inject `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class BrandProfileService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private profileUrl(workspaceSlug: string): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/brand-profile`);
  }

  async getBrandProfile(workspaceSlug: string): Promise<BrandProfileOutcome> {
    const url = this.profileUrl(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const profile = decodeBrandProfile(raw);
      return profile ? { status: 'found', profile } : { status: 'unavailable' };
    } catch (error) {
      if (statusCodeOf(error) !== 404) return { status: 'unavailable' };
      return problemCodeOf(error) === PROFILE_NOT_FOUND_CODE ? { status: 'first_use' } : { status: 'not_found' };
    }
  }

  createBrandProfile(
    workspaceSlug: string,
    body: Record<string, unknown>,
    idempotencyKey?: string,
  ): Promise<SaveBrandProfileOutcome> {
    return this.save('post', workspaceSlug, body, idempotencyKey);
  }

  updateBrandProfile(
    workspaceSlug: string,
    body: Record<string, unknown>,
    idempotencyKey?: string,
  ): Promise<SaveBrandProfileOutcome> {
    return this.save('patch', workspaceSlug, body, idempotencyKey);
  }

  private async save(
    method: 'post' | 'patch',
    workspaceSlug: string,
    body: Record<string, unknown>,
    idempotencyKey: string | undefined,
  ): Promise<SaveBrandProfileOutcome> {
    const url = this.profileUrl(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    const options = { withCredentials: true, observe: 'response' as const, ...idempotencyHeaders(idempotencyKey) };

    try {
      const response = await firstValueFrom(
        method === 'post' ? this.http.post<unknown>(url, body, options) : this.http.patch<unknown>(url, body, options),
      );
      const profile = decodeBrandProfile(response.body);
      return profile
        ? { status: 'saved', profile, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      const problem = problemCodeOf(error);

      if (code === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return problem === PROFILE_NOT_FOUND_CODE ? { status: 'profile_missing' } : { status: 'unavailable' };
      if (code === 409) return problem === ALREADY_EXISTS_CODE ? { status: 'already_exists' } : { status: 'conflict' };
      // 422 has two meanings on this route, told apart by code: logo links refused, or a reused key. Any other
      // 422 is something this client does not know, and is reported as a failure rather than guessed at.
      if (code === 422) {
        if (problem === ASSETS_UNPROCESSABLE_CODE) return { status: 'assets_unavailable' };
        if (problem === IDEMPOTENCY_KEY_REUSED_CODE) return { status: 'idempotency_key_conflict' };
        return { status: 'unavailable' };
      }
      return { status: 'unavailable' };
    }
  }

  async listContentChannels(): Promise<ContentChannelsOutcome> {
    const url = this.apiBase.url('/api/v1/reference/content-channels');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const channels = decodeContentChannels(raw);
      return channels ? { status: 'found', channels } : { status: 'unavailable' };
    } catch {
      return { status: 'unavailable' };
    }
  }
}
