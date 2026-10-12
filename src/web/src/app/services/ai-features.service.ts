import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiFeature, decodeAiFeatures } from '../models/ai-feature.models';

export type AiFeaturesOutcome =
  | { readonly status: 'found'; readonly features: readonly AiFeature[] }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type SaveAiFeaturesOutcome =
  | { readonly status: 'saved'; readonly features: readonly AiFeature[] }
  | { readonly status: 'forbidden' }
  /** Another Owner saved at the same moment. Nothing was written. */
  | { readonly status: 'conflict' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for which AI features a workspace has switched on.
 *
 * Nothing is cached, for the reason `WorkspaceSettingsService` gives: the page that edits a setting should
 * not show what another Owner replaced a minute ago.
 */
@Injectable({ providedIn: 'root' })
export class AiFeaturesService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** Every switchable feature and whether it is on. Any member may read. */
  async get(workspaceSlug: string): Promise<AiFeaturesOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const features = decodeAiFeatures(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return features ? { status: 'found', features } : { status: 'unavailable' };
    } catch (error) {
      return { status: statusOf(error) === 404 ? 'not_found' : 'unavailable' };
    }
  }

  /** Replaces the set that is on: every feature not named is switched off. Owner only. */
  async replace(workspaceSlug: string, enabled: readonly string[]): Promise<SaveAiFeaturesOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const features = decodeAiFeatures(
        await firstValueFrom(this.http.put<unknown>(url, { enabled }, { withCredentials: true })),
      );

      return features ? { status: 'saved', features } : { status: 'unavailable' };
    } catch (error) {
      switch (statusOf(error)) {
        case 403:
          return { status: 'forbidden' };
        case 404:
          return { status: 'not_found' };
        case 409:
          return { status: 'conflict' };
        default:
          return { status: 'unavailable' };
      }
    }
  }

  private url(workspaceSlug: string): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/ai-features`);
  }
}

function statusOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}
