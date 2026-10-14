import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { Dashboard, decodeDashboard } from '../models/dashboard.models';
import { statusCodeOf } from './ai-request';

export type DashboardOutcome =
  | { readonly status: 'found'; readonly dashboard: Dashboard }
  /** The workspace is unknown, or not this person's. One answer for both, as the route gives. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for the workspace dashboard (13.11's `GET .../dashboard`).
 *
 * Read-only and never cached here: the route reads every section fresh, and a section it could not read comes
 * back marked so, which the model keeps rather than failing the whole page.
 */
@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  get(workspaceSlug: string): Observable<DashboardOutcome> {
    const url = this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/dashboard`);
    if (!url) return of<DashboardOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): DashboardOutcome => {
        const dashboard = decodeDashboard(raw);

        return dashboard ? { status: 'found', dashboard } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<DashboardOutcome>(statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' }),
      ),
    );
  }
}
