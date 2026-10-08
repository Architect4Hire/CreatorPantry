import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  LinkRecipeAssetOutcome,
  LinkRecipeAssetRequest,
  RecipeAssetLinkFieldErrors,
  UnlinkRecipeAssetOutcome,
  encodeLinkRecipeAssetRequest,
} from '../models/recipe-asset-link.models';
import { decodeRecipeDetail } from '../models/recipe.models';

const ARCHIVED_CONFLICT_CODE = 'recipes.archived.conflict';
const HERO_CONFLICT_CODE = 'recipes.assetLink.hero.conflict';
const DUPLICATE_CONFLICT_CODE = 'recipes.assetLink.duplicate.conflict';
const TARGET_UNPROCESSABLE_CODE = 'recipes.assetLink.target.unprocessable';
const LINK_NOT_FOUND_CODE = 'recipes.assetLink.not_found';

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body, which is what tells one 409 or 422 from another. */
function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

function decodeFieldErrors(error: unknown): RecipeAssetLinkFieldErrors {
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

/**
 * Putting a library picture to use on a recipe, and taking it off again (RCPUB-005).
 *
 * **Both are edits of the recipe.** Each quotes the recipe's `expectedConcurrencyToken`, writes one new
 * version and answers with the whole recipe — which is why the answer is a `RecipeDetail` and not a link, and
 * why the caller must take the new token from it.
 *
 * **Neither touches the asset.** Unlinking removes the link; the picture stays in the library with its
 * versions and usage history. There is no method here that could do otherwise.
 *
 * The caller owns the idempotency key across retries of one attempt, as for a save: the same key with the same
 * request returns the first answer instead of linking twice.
 */
@Injectable({ providedIn: 'root' })
export class RecipeAssetLinkService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async link(
    workspaceSlug: string,
    recipeId: string,
    request: LinkRecipeAssetRequest,
    idempotencyKey: string,
  ): Promise<LinkRecipeAssetOutcome> {
    const url = this.url(workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeLinkRecipeAssetRequest(request), {
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const recipe = decodeRecipeDetail(response.body);

      return recipe
        ? { status: 'linked', recipe, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);
      const code = problemCodeOf(error);

      if (status === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };

      // Two 422s with different remedies: the picture, version or step is not there to link, or the key was
      // already spent on a different request.
      if (status === 422) {
        return code === TARGET_UNPROCESSABLE_CODE
          ? { status: 'target_refused', fieldErrors: decodeFieldErrors(error) }
          : { status: 'idempotency_key_conflict' };
      }

      if (status === 409) {
        if (code === HERO_CONFLICT_CODE) return { status: 'hero_exists' };
        if (code === DUPLICATE_CONFLICT_CODE) return { status: 'already_linked' };
        return code === ARCHIVED_CONFLICT_CODE ? { status: 'archived_conflict' } : { status: 'conflict' };
      }

      if (status === 403) return { status: 'forbidden' };
      if (status === 404) return { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  async unlink(
    workspaceSlug: string,
    recipeId: string,
    linkId: string,
    expectedConcurrencyToken: string,
    idempotencyKey: string,
  ): Promise<UnlinkRecipeAssetOutcome> {
    const url = this.url(workspaceSlug, recipeId, `/${encodeURIComponent(linkId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.delete<unknown>(url, {
          body: { expectedConcurrencyToken },
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const recipe = decodeRecipeDetail(response.body);

      return recipe
        ? { status: 'unlinked', recipe, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);
      const code = problemCodeOf(error);

      if (status === 409) return code === ARCHIVED_CONFLICT_CODE ? { status: 'archived_conflict' } : { status: 'conflict' };
      if (status === 422) return { status: 'idempotency_key_conflict' };
      if (status === 403) return { status: 'forbidden' };

      // A link this recipe no longer has is the creator's to see; a recipe they cannot see is not something
      // to explain further.
      if (status === 404) return code === LINK_NOT_FOUND_CODE ? { status: 'link_not_found' } : { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  private url(workspaceSlug: string, recipeId: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}/asset-links${suffix}`,
    );
  }
}
