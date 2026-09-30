import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  RecipeExportChoices,
  RecipeExportSummary,
  decodeRecipeExportSummary,
} from '../models/recipe-export.models';

export type RecipeExportSummaryOutcome =
  | { readonly status: 'found'; readonly summary: RecipeExportSummary }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** The three downloads the panel offers. */
export type RecipeExportFormat = 'json-ld' | 'markdown' | 'pdf';

/**
 * The typed client for the export summary, and the one place that spells the export routes.
 *
 * **No export logic lives here.** The summary is read and handed back; the documents themselves are produced
 * by the server and fetched by the browser following a link, so this service only builds the link. It never
 * fetches a document, never holds its bytes, and never produces an address that is not the gateway's own
 * export route.
 */
@Injectable({ providedIn: 'root' })
export class RecipeExportService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async getSummary(workspaceSlug: string, recipeId: string): Promise<RecipeExportSummaryOutcome> {
    const url = this.exportsUrl(workspaceSlug, recipeId, 'summary');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const summary = decodeRecipeExportSummary(raw);
      return summary ? { status: 'found', summary } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404
        ? { status: 'not_found' }
        : { status: 'unavailable' };
    }
  }

  /**
   * The link a browser follows to get one export, or null while the gateway address is not known.
   *
   * Only the choices that route understands are sent: Markdown takes no page size, and JSON-LD takes no
   * layout choice at all, so a parameter never travels to a route that ignores it. `versionNumber` is pinned
   * to the version the summary described, so a recipe edited in another tab cannot change what this link
   * downloads from under a creator who was looking at the earlier version.
   */
  downloadUrl(
    workspaceSlug: string,
    recipeId: string,
    format: RecipeExportFormat,
    versionNumber: number,
    choices: RecipeExportChoices,
  ): string | null {
    const query = new URLSearchParams({ versionNumber: String(versionNumber) });
    if (format !== 'json-ld') {
      query.set('template', choices.template);
      query.set('units', choices.units);
    }
    if (format === 'pdf') query.set('pageSize', choices.pageSize);

    const url = this.exportsUrl(workspaceSlug, recipeId, format);
    return url ? `${url}?${query.toString()}` : null;
  }

  private exportsUrl(workspaceSlug: string, recipeId: string, suffix: string): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}/exports/${suffix}`,
    );
  }
}
