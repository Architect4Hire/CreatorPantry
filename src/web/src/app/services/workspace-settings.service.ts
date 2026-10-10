import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { Workspace, WorkspaceMeasurementSystem, decodeWorkspace } from '../models/workspace.models';

export type WorkspaceOutcome =
  | { readonly status: 'found'; readonly workspace: Workspace }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type SaveMeasurementPreferenceOutcome =
  | { readonly status: 'saved'; readonly workspace: Workspace }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for a workspace's own settings: reading the workspace and setting its default measurement
 * system (B-08).
 *
 * Nothing is cached. The setting is read where it is edited, and the page that edits it should not show what
 * another Owner replaced a minute ago.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceSettingsService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** The workspace and the caller's role in it. Any member may read. */
  async get(workspaceSlug: string): Promise<WorkspaceOutcome> {
    const url = this.workspaceUrl(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const workspace = decodeWorkspace(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return workspace ? { status: 'found', workspace } : { status: 'unavailable' };
    } catch (error) {
      return { status: statusOf(error) === 404 ? 'not_found' : 'unavailable' };
    }
  }

  /** Sets the system new AI recipe drafts are written in. Owner only; no existing recipe is changed. */
  async setMeasurementSystem(
    workspaceSlug: string,
    system: WorkspaceMeasurementSystem,
  ): Promise<SaveMeasurementPreferenceOutcome> {
    const base = this.workspaceUrl(workspaceSlug);
    if (!base) return { status: 'unavailable' };

    try {
      const workspace = decodeWorkspace(
        await firstValueFrom(
          this.http.put<unknown>(
            `${base}/measurement-preference`,
            { defaultMeasurementSystem: system },
            { withCredentials: true },
          ),
        ),
      );

      return workspace ? { status: 'saved', workspace } : { status: 'unavailable' };
    } catch (error) {
      const code = statusOf(error);
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      return { status: 'unavailable' };
    }
  }

  private workspaceUrl(workspaceSlug: string): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}`);
  }
}

function statusOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}
