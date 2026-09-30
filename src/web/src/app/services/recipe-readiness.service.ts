import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  RecipeReadiness,
  RecipeTransitionRequest,
  decodeRecipeReadiness,
  encodeRecipeTransitionRequest,
} from '../models/recipe-readiness.models';
import { RecipeDetail, decodeRecipeDetail } from '../models/recipe.models';

export type RecipeReadinessOutcome =
  | { readonly status: 'found'; readonly readiness: RecipeReadiness }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type RecipeTransitionOutcome =
  /** The recipe as it now stands, carrying its new status and the token the next write must quote. */
  | { readonly status: 'moved'; readonly recipe: RecipeDetail; readonly replayed: boolean }
  /**
   * The move does not exist from where the recipe is, or it is missing something the move requires.
   * `detail` is the server's own sentence, which names the states the recipe could have gone to instead.
   */
  | { readonly status: 'invalid'; readonly message: string; readonly detail: string }
  /** The move exists but is above the caller's role. `message` names the role it needs. */
  | { readonly status: 'forbidden'; readonly message: string }
  /**
   * Approval refused because readiness is not clear. `blockingRuleIds` are the rule ids, not their details —
   * the details belong to the readiness screen, which already has them.
   */
  | { readonly status: 'blocked'; readonly message: string; readonly blockingRuleIds: readonly string[] }
  /**
   * The token quoted is one the recipe has moved past, or the approval's evaluation describes content that
   * has since changed. Nothing was written either way, and the remedy is the same: go and look.
   */
  | { readonly status: 'conflict' }
  | { readonly status: 'not_found' }
  /** The `Idempotency-Key` was reused for a different body. Take a fresh one and retry. */
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

function bodyOf(error: unknown): Record<string, unknown> | null {
  return error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
}

/** The stable `code` extension, which is what distinguishes one 409 from another. */
function problemCodeOf(error: unknown): string | null {
  const code = bodyOf(error)?.['code'];
  return typeof code === 'string' ? code : null;
}

/**
 * `ProblemDetails.title`, which for a transition refusal is the server's own sentence about the move.
 *
 * Shown rather than replaced with wording of this client's own: the server knows which two states were
 * involved and which role a move needs, and a second copy of those sentences here would be a second thing to
 * keep in step with the machine.
 */
function problemTitleOf(error: unknown): string {
  const title = bodyOf(error)?.['title'];
  return typeof title === 'string' ? title : '';
}

/** One named field's messages from `ValidationProblemDetails.errors`. */
function fieldMessages(error: unknown, field: string): readonly string[] {
  const errors = bodyOf(error)?.['errors'];
  if (typeof errors !== 'object' || errors === null) return [];

  const messages = (errors as Record<string, unknown>)[field];
  return Array.isArray(messages) && messages.every((message) => typeof message === 'string') ? messages : [];
}

/** RecipeErrorCodes.TransitionBlockedConflict. */
const TRANSITION_BLOCKED_CODE = 'recipes.transition.blocked.conflict';

/**
 * The typed client for the readiness evaluation and the editorial state machine (TESTRUN-004/005).
 *
 * **The evaluation is a read and is never cached here.** It describes the recipe at the moment it was made —
 * the route sends `no-store` for exactly that reason — so this service hands each answer straight back and
 * holds nothing. A stored verdict would be a second source of truth that goes stale on the next edit.
 *
 * **The transition is the only door into the machine.** The caller owns the idempotency key across retries of
 * one logical move, and owns quoting the recipe's `concurrencyToken`.
 */
@Injectable({ providedIn: 'root' })
export class RecipeReadinessService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async getReadiness(workspaceSlug: string, recipeId: string): Promise<RecipeReadinessOutcome> {
    const url = this.recipeUrl(workspaceSlug, recipeId, 'readiness');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const readiness = decodeRecipeReadiness(raw);
      return readiness ? { status: 'found', readiness } : { status: 'unavailable' };
    } catch (error) {
      return statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  async transition(
    workspaceSlug: string,
    recipeId: string,
    request: RecipeTransitionRequest,
    idempotencyKey: string,
  ): Promise<RecipeTransitionOutcome> {
    const url = this.recipeUrl(workspaceSlug, recipeId, 'readiness-transitions');
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRecipeTransitionRequest(request), {
          withCredentials: true,
          observe: 'response',
          // Required on this route, unlike the test-run writes where it is optional.
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const recipe = decodeRecipeDetail(response.body);
      return recipe
        ? { status: 'moved', recipe, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);

      if (code === 400) {
        return {
          status: 'invalid',
          message: problemTitleOf(error),
          // `errors.transition` is where Business puts the sentence naming the legal targets.
          detail: fieldMessages(error, 'transition').join(' '),
        };
      }

      if (code === 403) return { status: 'forbidden', message: problemTitleOf(error) };
      if (code === 404) return { status: 'not_found' };

      if (code === 409) {
        // Two 409s with two different remedies: clear the blockers, or re-read the recipe. Retrying the same
        // request answers the second and never the first.
        return problemCodeOf(error) === TRANSITION_BLOCKED_CODE
          ? {
              status: 'blocked',
              message: problemTitleOf(error),
              blockingRuleIds: fieldMessages(error, 'blockingRules'),
            }
          : { status: 'conflict' };
      }

      if (code === 422) return { status: 'idempotency_key_conflict' };
      return { status: 'unavailable' };
    }
  }

  private recipeUrl(workspaceSlug: string, recipeId: string, suffix: string): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}/${suffix}`,
    );
  }
}
