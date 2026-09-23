import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  CreateRecipeRequest,
  CreatedRecipe,
  RecipeDetail,
  UpdateRecipeRequest,
  decodeCreatedRecipe,
  decodeRecipeDetail,
  encodeCreateRecipeRequest,
  encodeUpdateRecipeRequest,
} from '../models/recipe.models';

export type CreateRecipeOutcome =
  | { readonly status: 'created'; readonly recipe: CreatedRecipe; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

export type RecipeDetailOutcome =
  | { readonly status: 'found'; readonly recipe: RecipeDetail }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type UpdateRecipeOutcome =
  | { readonly status: 'updated'; readonly recipe: RecipeDetail; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'conflict' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** `ValidationProblemDetails.errors`, keyed by camelCase request field name (`OperationError.FieldErrors`). */
function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const errors = body?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }
  return result;
}

function idempotencyHeaders(idempotencyKey: string | undefined): { headers: Record<string, string> } | Record<string, never> {
  return idempotencyKey ? { headers: { 'Idempotency-Key': idempotencyKey } } : {};
}

function recipeUrl(apiBase: ApiBaseService, workspaceSlug: string, recipeId?: string): string | null {
  const path = `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes${recipeId ? `/${encodeURIComponent(recipeId)}` : ''}`;
  return apiBase.url(path);
}

/**
 * The typed client for the Phase 5 recipe create/detail/update endpoints. The caller owns the
 * idempotency key across retries of one logical create/update — this service never generates or
 * caches one itself — and owns round-tripping RecipeDetail.concurrencyToken into the next PATCH's
 * expectedConcurrencyToken. No component may inject HttpClient directly for recipe calls; this
 * service is the only path (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class RecipeService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async createRecipe(workspaceSlug: string, request: CreateRecipeRequest, idempotencyKey?: string): Promise<CreateRecipeOutcome> {
    const url = recipeUrl(this.apiBase, workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeCreateRecipeRequest(request), {
          withCredentials: true,
          observe: 'response',
          ...idempotencyHeaders(idempotencyKey),
        }),
      );
      const recipe = decodeCreatedRecipe(response.body);
      return recipe ? { status: 'created', recipe, replayed: response.headers.has('Idempotent-Replayed') } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      // 422 here means only one thing on this controller: the Idempotency-Key was reused for a
      // request with a different body. It is not a field-validation failure — the caller should
      // generate a fresh key and retry, not surface a "fix your input" message.
      if (code === 422) return { status: 'idempotency_key_conflict' };
      if (code === 403) return { status: 'forbidden' };
      return { status: 'unavailable' };
    }
  }

  async getRecipeDetail(workspaceSlug: string, recipeId: string): Promise<RecipeDetailOutcome> {
    const url = recipeUrl(this.apiBase, workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const recipe = decodeRecipeDetail(raw);
      return recipe ? { status: 'found', recipe } : { status: 'unavailable' };
    } catch (error) {
      return statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  async updateRecipe(
    workspaceSlug: string,
    recipeId: string,
    request: UpdateRecipeRequest,
    idempotencyKey?: string,
  ): Promise<UpdateRecipeOutcome> {
    const url = recipeUrl(this.apiBase, workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.patch<unknown>(url, encodeUpdateRecipeRequest(request), {
          withCredentials: true,
          observe: 'response',
          ...idempotencyHeaders(idempotencyKey),
        }),
      );
      const recipe = decodeRecipeDetail(response.body);
      return recipe ? { status: 'updated', recipe, replayed: response.headers.has('Idempotent-Replayed') } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      // Same idempotency-key-reuse case as createRecipe, not a field-validation failure.
      if (code === 422) return { status: 'idempotency_key_conflict' };
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      if (code === 409) return { status: 'conflict' };
      return { status: 'unavailable' };
    }
  }
}
