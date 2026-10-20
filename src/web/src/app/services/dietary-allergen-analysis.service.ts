import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  DietaryAllergenAnalysis,
  DietaryAllergenSelection,
  decodeDietaryAllergenAnalysis,
  encodeDietaryAllergenSelection,
} from '../models/dietary-allergen-analysis.models';

export type DietaryAllergenAnalysisOutcome =
  | { readonly status: 'analysed'; readonly analysis: DietaryAllergenAnalysis }
  /** The selection cannot be read — nothing chosen, too many, an empty or repeated id. `fieldErrors` names which. */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /**
   * An allergen or profile the catalogue does not offer, or has retired. Separate from `invalid_request`
   * because the remedy is different: the request was well formed, and what changed was the catalogue.
   */
  | { readonly status: 'subject_unprocessable'; readonly message: string }
  /** The recipe is readable and has no version with that number — it moved on under the panel. */
  | { readonly status: 'version_not_found' }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable (tenancy.md). */
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  /** An outage, or an answer this client could not read. Nothing is shown rather than part of an analysis. */
  | { readonly status: 'unavailable' };

/** RecipeErrorCodes.VersionNotFound — what distinguishes a missing version from an unreadable recipe on a 404. */
const VERSION_NOT_FOUND_CODE = 'recipes.version.not_found';

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

function bodyOf(error: unknown): Record<string, unknown> | null {
  return error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
}

function problemCodeOf(error: unknown): string | null {
  const code = bodyOf(error)?.['code'];
  return typeof code === 'string' ? code : null;
}

/** `ProblemDetails.title` — the server's own sentence, used where it knows something this client does not. */
function problemTitleOf(error: unknown): string {
  const title = bodyOf(error)?.['title'];
  return typeof title === 'string' ? title : '';
}

/** `ValidationProblemDetails.errors`, keyed by camelCase request field name (`OperationError.FieldErrors`). */
function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const errors = bodyOf(error)?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }
  return result;
}

/**
 * The typed client for the dietary and allergen analysis route (ING-007; B-38).
 *
 * **Read-only, despite being a POST.** The selection — up to twenty allergens and twenty profiles — travels in
 * a body because forty ids do not belong in a URL. Nothing is written, so there is no idempotency key and no
 * concurrency token, and the server answers `no-store`: an analysis describes the records as they stood when
 * it was read, and they change as they are reviewed.
 *
 * Nothing here interprets an answer. Findings, their sentences and the disclaimer are the server's, decoded
 * whole or not at all — see `decodeDietaryAllergenAnalysis`.
 *
 * No component may inject `HttpClient` directly for this call; this service is the only path
 * (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class DietaryAllergenAnalysisService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Reads one recipe version's approved ingredient records against the selected allergens and dietary
   * profiles.
   *
   * The version is named by number rather than defaulted, as every public recipe route names one: an analysis
   * shown beside a recipe that has since moved on is an analysis of the older content, and the echoed
   * `versionNumber` is what lets a caller notice.
   */
  async analyse(
    workspaceSlug: string,
    recipeId: string,
    versionNumber: number,
    selection: DietaryAllergenSelection,
  ): Promise<DietaryAllergenAnalysisOutcome> {
    const url = this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}` +
        `/versions/${versionNumber}/analysis/dietary-allergen`,
    );
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeDietaryAllergenSelection(selection), { withCredentials: true }),
      );
      const analysis = decodeDietaryAllergenAnalysis(raw);

      return analysis ? { status: 'analysed', analysis } : { status: 'unavailable' };
    } catch (error) {
      return this.refusalFor(error);
    }
  }

  private refusalFor(error: unknown): DietaryAllergenAnalysisOutcome {
    const status = statusCodeOf(error);

    if (status === 400) return { status: 'invalid_request', fieldErrors: decodeFieldErrors(error) };

    // The server names which allergen or profile it could not accept, and it is the only thing that knows.
    if (status === 422) return { status: 'subject_unprocessable', message: problemTitleOf(error) };

    // Kept apart from `unavailable`, which the panel reports as a connection problem. An expired session is
    // not a network fault, and telling a creator to check their connection sends them to fix the wrong thing.
    if (status === 401 || status === 403) return { status: 'unauthorized' };

    // Both are 404, and the stable code tells them apart: a version this recipe does not have is something
    // the creator can act on by reloading, while a recipe they cannot see is not explained further — saying
    // more would disclose that it exists (tenancy.md).
    if (status === 404) {
      return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
        ? { status: 'version_not_found' }
        : { status: 'not_found' };
    }

    return { status: 'unavailable' };
  }
}
