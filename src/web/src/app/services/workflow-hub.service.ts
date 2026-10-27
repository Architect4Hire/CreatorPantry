import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  WorkflowDefinitionSummary,
  WorkflowRunSummaryPage,
  decodeWorkflowDefinitions,
  decodeWorkflowRunSummaryPage,
} from '../models/workflow-hub.models';

export type WorkflowDefinitionsOutcome =
  | { readonly status: 'found'; readonly definitions: readonly WorkflowDefinitionSummary[] }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type WorkflowRunListOutcome =
  | { readonly status: 'found'; readonly page: WorkflowRunSummaryPage }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type WorkflowRunListView = 'mine' | 'completed' | 'attention' | 'all';

/**
 * The typed client for what the Workflow Hub reads (13A.13): the workspace's workflows, and pages of its runs.
 * Starting a run is {@link WorkflowRunService.start}'s. Components never inject `HttpClient`.
 */
@Injectable({ providedIn: 'root' })
export class WorkflowHubService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async definitions(workspaceSlug: string): Promise<WorkflowDefinitionsOutcome> {
    const url = this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/workflow-definitions`);
    if (!url) return { status: 'unavailable' };

    try {
      const definitions = decodeWorkflowDefinitions(
        await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })),
      );
      return definitions ? { status: 'found', definitions } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404
        ? { status: 'not_found' }
        : { status: 'unavailable' };
    }
  }

  async runs(
    workspaceSlug: string,
    query: { readonly view: WorkflowRunListView; readonly limit?: number; readonly cursor?: string | null },
  ): Promise<WorkflowRunListOutcome> {
    const url = this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/workflow-runs`);
    if (!url) return { status: 'unavailable' };

    let params = new HttpParams().set('view', query.view);
    if (query.limit !== undefined) params = params.set('limit', query.limit);
    if (query.cursor) params = params.set('cursor', query.cursor);

    try {
      const page = decodeWorkflowRunSummaryPage(
        await firstValueFrom(this.http.get<unknown>(url, { params, withCredentials: true })),
      );
      return page ? { status: 'found', page } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404
        ? { status: 'not_found' }
        : { status: 'unavailable' };
    }
  }
}
