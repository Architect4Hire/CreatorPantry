import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  NutritionEstimate,
  NutritionEstimateRequest,
  NutritionMapping,
  NutritionMappingDecision,
  decodeNutritionEstimate,
  decodeNutritionMapping,
  encodeNutritionEstimateRequest,
  encodeNutritionMappingDecision,
} from '../models/nutrition.models';

export type NutritionEstimateOutcome =
  | { readonly status: 'estimated'; readonly estimate: NutritionEstimate }
  /** The requested serving count is one the server will not use; `fieldErrors` names it. */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** A total too large to state as a decimal. The server's own sentence says so. */
  | { readonly status: 'quantities_unprocessable'; readonly message: string }
  /**
   * The nutrition source could not be asked — down, throttled, or not available on this installation. No
   * estimate at all rather than a smaller one that looks complete, and the message says which case it is.
   */
  | { readonly status: 'source_unavailable'; readonly message: string }
  | { readonly status: 'version_not_found' }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable (tenancy.md). */
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

export type NutritionMappingOutcome =
  | { readonly status: 'found'; readonly mapping: NutritionMapping }
  /** No mapping for this ingredient, or another workspace's. Nothing has been proposed yet. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

export type NutritionMappingDecisionOutcome =
  | { readonly status: 'decided'; readonly mapping: NutritionMapping }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /**
   * The token quoted is one the mapping has moved past — another decision, or a refresh that put a newer
   * source version under review. Nothing was written; the remedy is to read it again.
   */
  | { readonly status: 'conflict' }
  /** There is nothing left to decide: already confirmed, or unresolved with no reference to reject. */
  | { readonly status: 'state_conflict' }
  /** Below Editor. Confirming is what a published estimate rests on, so it takes the approving role. */
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  /** Nothing was written — the source could not be asked, or the store refused. Safe to send again. */
  | { readonly status: 'retryable'; readonly message: string }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/** RecipeErrorCodes.VersionNotFound — what distinguishes a missing version from an unreadable recipe. */
const VERSION_NOT_FOUND_CODE = 'recipes.version.not_found';

/**
 * `NutritionErrorCodes.StateConflict`, which is what tells one 409 from the other: a token the mapping has
 * moved past (read it again) versus nothing left to decide on it.
 */
const MAPPING_STATE_CONFLICT_CODE = 'nutrition.mapping.state.conflict';

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

/**
 * `ProblemDetails.title` — the server's own sentence.
 *
 * Shown rather than replaced for the answers where the server knows something this client cannot: whether a
 * source is down or simply not configured on this installation, and which total was too large to state.
 */
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
 * The typed client for the nutrition estimate (ING-008, B-39) and the ingredient mappings it rests on
 * (B-34, B-40).
 *
 * **The estimate is read-only**, despite being a POST: the yield basis belongs in a body, it writes nothing —
 * not even to a mapping it finds out of date — takes no idempotency key, and answers `no-store`, because
 * mappings and source records change.
 *
 * **A decision is a write, and carries a token.** Confirming and rejecting quote the token of the mapping as
 * read, so nobody decides a mapping they did not see; a repeat is a conflict rather than a second write,
 * which is why there is no idempotency key here either.
 *
 * Nothing here calculates. No component may inject `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class NutritionService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Estimates one recipe version's nutrition from the workspace's confirmed mappings.
   *
   * The version is named by number, as every public recipe route names one: an estimate shown beside a recipe
   * that has since moved on is an estimate of the older content, and the echoed `versionNumber` is what lets
   * a caller notice.
   */
  async estimate(
    workspaceSlug: string,
    recipeId: string,
    versionNumber: number,
    request: NutritionEstimateRequest,
  ): Promise<NutritionEstimateOutcome> {
    const url = this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}` +
        `/versions/${versionNumber}/analysis/nutrition`,
    );
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeNutritionEstimateRequest(request), { withCredentials: true }),
      );
      const estimate = decodeNutritionEstimate(raw);

      return estimate ? { status: 'estimated', estimate } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);

      if (status === 400) return { status: 'invalid_request', fieldErrors: decodeFieldErrors(error) };
      if (status === 422) return { status: 'quantities_unprocessable', message: problemTitleOf(error) };
      if (status === 503) return { status: 'source_unavailable', message: problemTitleOf(error) };
      if (status === 401 || status === 403) return { status: 'unauthorized' };
      if (status === 404) {
        return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
          ? { status: 'version_not_found' }
          : { status: 'not_found' };
      }

      return { status: 'unavailable' };
    }
  }

  /**
   * Reads this workspace's mapping for one platform ingredient.
   *
   * Included in this client because the decisions owe a concurrency token and nothing else hands one out: the
   * estimate carries a mapping id, not its token. Any member may read.
   */
  async getMapping(
    workspaceSlug: string,
    ingredientId: string,
  ): Promise<NutritionMappingOutcome> {
    const url = this.mappingUrl(workspaceSlug, ingredientId, '');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const mapping = decodeNutritionMapping(raw);

      return mapping ? { status: 'found', mapping } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);

      if (status === 401 || status === 403) return { status: 'unauthorized' };
      if (status === 404) return { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  /** Confirms the reference a mapping holds — a proposal, or a newer version of it under review. Editor. */
  confirmMapping(
    workspaceSlug: string,
    ingredientId: string,
    decision: NutritionMappingDecision,
  ): Promise<NutritionMappingDecisionOutcome> {
    return this.decide(workspaceSlug, ingredientId, 'confirm', decision);
  }

  /**
   * Turns down the reference a mapping holds, so no later proposal offers it again, and leaves the mapping
   * unresolved. Editor.
   */
  rejectMapping(
    workspaceSlug: string,
    ingredientId: string,
    decision: NutritionMappingDecision,
  ): Promise<NutritionMappingDecisionOutcome> {
    return this.decide(workspaceSlug, ingredientId, 'reject', decision);
  }

  private async decide(
    workspaceSlug: string,
    ingredientId: string,
    operation: 'confirm' | 'reject',
    decision: NutritionMappingDecision,
  ): Promise<NutritionMappingDecisionOutcome> {
    const url = this.mappingUrl(workspaceSlug, ingredientId, `/${operation}`);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeNutritionMappingDecision(decision), { withCredentials: true }),
      );
      const mapping = decodeNutritionMapping(raw);

      return mapping ? { status: 'decided', mapping } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);
      const code = problemCodeOf(error);

      if (status === 400) return { status: 'invalid_request', fieldErrors: decodeFieldErrors(error) };

      // Two 409s with different remedies: one says read it again, the other that there is nothing to decide.
      if (status === 409) {
        return code === MAPPING_STATE_CONFLICT_CODE ? { status: 'state_conflict' } : { status: 'conflict' };
      }

      // A role refusal, not a lost session: kept apart so the panel can say which role this takes rather
      // than sending the creator to sign in again. Both the facade's own code and the route policy's bare
      // 403 mean the same thing to a reader.
      if (status === 403) return { status: 'forbidden' };

      if (status === 401) return { status: 'unauthorized' };
      if (status === 404) return { status: 'not_found' };

      // Both 503s wrote nothing and are safe to send again; the message says which one it was.
      if (status === 503) return { status: 'retryable', message: problemTitleOf(error) };

      return { status: 'unavailable' };
    }
  }

  private mappingUrl(workspaceSlug: string, ingredientId: string, suffix: string): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/nutrition-mappings/` +
        `${encodeURIComponent(ingredientId)}${suffix}`,
    );
  }
}
