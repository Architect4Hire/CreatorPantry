import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { FoodSafetyWarnings, decodeFoodSafetyWarnings } from '../models/food-safety.models';

export type FoodSafetyWarningsOutcome =
  | { readonly status: 'found'; readonly warnings: FoodSafetyWarnings }
  /** The recipe is readable and has no version with that number. */
  | { readonly status: 'version_not_found' }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable (tenancy.md). */
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  /** An outage, or an answer this client could not read whole. Nothing is shown rather than part of it. */
  | { readonly status: 'unavailable' };

/** RecipeErrorCodes.VersionNotFound — what distinguishes a missing version from an unreadable recipe. */
const VERSION_NOT_FOUND_CODE = 'recipes.version.not_found';

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

/**
 * The typed client for the high-risk method warnings (B-37, routed by 14.7's H-3).
 *
 * **Read-only, and a plain GET**: it takes no selection, writes nothing, and answers `no-store` because the
 * technique vocabulary and ingredient categories it reads change as they are curated. Any member may ask.
 *
 * No component may inject `HttpClient` for this call; this service is the only path
 * (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class FoodSafetyService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** The warnings for one saved version, by the number the history lists it under. */
  async read(workspaceSlug: string, recipeId: string, versionNumber: number): Promise<FoodSafetyWarningsOutcome> {
    const url = this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}` +
        `/versions/${versionNumber}/analysis/food-safety`,
    );
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const warnings = decodeFoodSafetyWarnings(raw);

      return warnings ? { status: 'found', warnings } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);

      // Kept apart from `unavailable`: an expired session is not a network fault, and telling a creator to
      // check their connection sends them to fix the wrong thing.
      if (status === 401 || status === 403) return { status: 'unauthorized' };

      if (status === 404) {
        return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
          ? { status: 'version_not_found' }
          : { status: 'not_found' };
      }

      return { status: 'unavailable' };
    }
  }
}
