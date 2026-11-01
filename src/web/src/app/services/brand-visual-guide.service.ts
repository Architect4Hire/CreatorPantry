import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { BrandVisualGuide, BrandVisualTask, decodeBrandVisualGuide } from '../models/brand-visual-guide.models';

/** `unavailable` covers every way the read can fail: the caller says the same thing about all of them. */
export type BrandVisualGuideOutcome =
  { readonly status: 'found'; readonly guide: BrandVisualGuide } | { readonly status: 'unavailable' };

/**
 * The typed client for `GET .../brand-visual-guide`: the look the workspace's active style guide describes for
 * an image task, as the server would use it. Read-only — the look is edited in the Brand Style Guide and
 * nowhere else. Components never inject `HttpClient` for this call (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class BrandVisualGuideService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async get(workspaceSlug: string, task: BrandVisualTask): Promise<BrandVisualGuideOutcome> {
    const url = this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/brand-visual-guide`);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { params: { task }, withCredentials: true }));
      const guide = decodeBrandVisualGuide(raw);

      return guide ? { status: 'found', guide } : { status: 'unavailable' };
    } catch {
      return { status: 'unavailable' };
    }
  }
}
