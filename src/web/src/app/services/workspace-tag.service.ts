import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { WorkspaceTag, decodeWorkspaceTags } from '../models/workspace-tag.models';

export type WorkspaceTagsOutcome =
  | { readonly status: 'found'; readonly tags: readonly WorkspaceTag[] }
  | { readonly status: 'unavailable' };

/**
 * The typed client for the workspace's tag vocabulary: the tags that may be offered as new choices.
 *
 * **Read-only.** A tag enters the vocabulary when a creator tags a recipe; nothing here creates, renames or
 * retires one. Retired tags are not in the list — a record that already carries one names it itself.
 *
 * Nothing is cached: the vocabulary is the creator's own words, the route answers `no-store`, and a picker
 * opened after a recipe was tagged should offer the new tag.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceTagService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** Every active tag of the workspace, by name. Any member may read. */
  async list(workspaceSlug: string): Promise<WorkspaceTagsOutcome> {
    const url = this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/tags`);
    if (!url) return { status: 'unavailable' };

    try {
      const tags = decodeWorkspaceTags(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return tags ? { status: 'found', tags } : { status: 'unavailable' };
    } catch {
      return { status: 'unavailable' };
    }
  }
}
