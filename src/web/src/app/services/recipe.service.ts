import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  RecipeVersionComparison,
  RecipeVersionHistoryPage,
  RestoreRecipeVersionRequest,
  decodeRecipeVersionComparison,
  decodeRecipeVersionHistoryPage,
  encodeRestoreRecipeVersionRequest,
} from '../models/recipe-version.models';
import {
  CreateRecipeRequest,
  CreatedRecipe,
  DuplicateRecipeRequest,
  RecipeDetail,
  RecipeLifecycleRequest,
  RecipeSearchPage,
  RecipeSearchQuery,
  UpdateRecipeRequest,
  decodeCreatedRecipe,
  decodeRecipeDetail,
  decodeRecipeSearchPage,
  encodeCreateRecipeRequest,
  encodeDuplicateRecipeRequest,
  encodeRecipeSearchQuery,
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

export type RecipeVersionHistoryOutcome =
  | { readonly status: 'found'; readonly page: RecipeVersionHistoryPage }
  /** The cursor was issued for a different workspace or recipe. Start the list again. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type RecipeVersionComparisonOutcome =
  | { readonly status: 'found'; readonly comparison: RecipeVersionComparison }
  /** The query did not name two version numbers. */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** The recipe is readable but one of the numbers names no version of it; `fieldErrors` says which. */
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type RecipeLifecycleOutcome =
  /** The recipe as it now stands, carrying the refreshed token the next write must quote. */
  | { readonly status: 'updated'; readonly recipe: RecipeDetail }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Archiving takes a recipe out of every collaborator's library, so it carries the Editor bar. */
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  /** The token quoted is one the recipe has moved past: someone is editing it. Nothing was moved. */
  | { readonly status: 'conflict' }
  | { readonly status: 'unavailable' };

export type DuplicateRecipeOutcome =
  /** The copy, as a create returns one. It is a recipe in its own right, always a Draft, at version 1. */
  | { readonly status: 'created'; readonly recipe: CreatedRecipe; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'idempotency_key_conflict' }
  /** Duplicating needs the Contributor role — the same bar as creating a recipe, not the Editor bar a restore carries. */
  | { readonly status: 'forbidden' }
  /** The source is readable and has no version with that number; `fieldErrors` names `sourceVersionNumber`. */
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type RestoreRecipeVersionOutcome =
  /** The whole recipe as it now stands, carrying the refreshed token the next write must quote. */
  | { readonly status: 'restored'; readonly recipe: RecipeDetail; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'idempotency_key_conflict' }
  /** Restoring needs the Editor role, a step above editing. */
  | { readonly status: 'forbidden' }
  /** The recipe is readable and has no version with that number; `fieldErrors` names `versionNumber`. */
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'not_found' }
  /** The token quoted is one the recipe has moved past. Re-read and ask again; nothing was written. */
  | { readonly status: 'conflict' }
  /**
   * The recipe is archived. A separate outcome from `conflict` although both are 409s, because the remedies
   * are opposites: a stale token says retry, and this says unarchive first — retrying will fail forever.
   */
  | { readonly status: 'archived_conflict' }
  | { readonly status: 'unavailable' };

export type RecipeSearchOutcome =
  | { readonly status: 'found'; readonly page: RecipeSearchPage }
  /** The cursor was issued for a different workspace, ordering or set of filters. Start the list again. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body, which is what distinguishes one 400 from another. */
function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
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

function versionsUrl(apiBase: ApiBaseService, workspaceSlug: string, recipeId: string, suffix = ''): string | null {
  const recipe = recipeUrl(apiBase, workspaceSlug, recipeId);
  return recipe === null ? null : `${recipe}/versions${suffix}`;
}

/** RecipeErrorCodes.CursorInvalidRequest — a stale cursor, whose remedy is to start the list again. */
const CURSOR_INVALID_CODE = 'recipes.cursor.invalid_request';

/** RecipeErrorCodes.VersionNotFound — distinguishes a missing version from an unreadable recipe on a 404. */
const VERSION_NOT_FOUND_CODE = 'recipes.version.not_found';

/** RecipeErrorCodes.RecipeArchivedConflict — the 409 whose remedy is to unarchive, not to retry. */
const ARCHIVED_CONFLICT_CODE = 'recipes.archived.conflict';

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

  /**
   * One page of the workspace's recipes.
   *
   * Returns an Observable where the rest of this service returns Promises, and that is the point rather than an
   * inconsistency: a library screen re-runs this on every keystroke, and unsubscribing an `HttpClient` request
   * aborts it. A caller that pipes this through `switchMap` therefore cancels the request it has superseded —
   * which a Promise cannot express, and which matters here because an earlier, slower response arriving after a
   * later one would otherwise overwrite the results a creator is looking at.
   *
   * It never errors. Every outcome is a value, so a failed page cannot kill the subscription and leave the
   * screen unable to search again without being rebuilt.
   */
  searchRecipes(workspaceSlug: string, query: RecipeSearchQuery): Observable<RecipeSearchOutcome> {
    const url = recipeUrl(this.apiBase, workspaceSlug);
    if (!url) return of<RecipeSearchOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true, params: encodeRecipeSearchQuery(query) }).pipe(
      map((raw): RecipeSearchOutcome => {
        const page = decodeRecipeSearchPage(raw);
        return page ? { status: 'found', page } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        if (statusCodeOf(error) === 400) {
          // Two different 400s with two different remedies: a stale cursor means start again from the first
          // page, while a rejected filter means the request itself has to change. Telling them apart is the
          // reason the API gives them separate codes.
          return of<RecipeSearchOutcome>(
            problemCodeOf(error) === 'recipes.cursor.invalid_request'
              ? { status: 'cursor_expired' }
              : { status: 'invalid_request', fieldErrors: decodeFieldErrors(error) },
          );
        }

        // A 404 here is the workspace, not a recipe: an empty library is an empty page, never a not-found. It
        // is reported as unavailable because the remedy is the same as any other failed read.
        return of<RecipeSearchOutcome>({ status: 'unavailable' });
      }),
    );
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

  /**
   * One page of a recipe's version history, newest first. Follow `nextCursor` until it is null.
   *
   * A cursor is bound to the workspace and recipe it was issued for, so replaying one elsewhere is refused
   * rather than silently paging the wrong history — which arrives here as `cursor_expired`, whose remedy is
   * to start the list again without a cursor.
   */
  async getVersionHistory(workspaceSlug: string, recipeId: string, cursor?: string): Promise<RecipeVersionHistoryOutcome> {
    const url = versionsUrl(this.apiBase, workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.get<unknown>(url, { withCredentials: true, params: cursor ? { cursor } : {} }),
      );
      const page = decodeRecipeVersionHistoryPage(raw);
      return page ? { status: 'found', page } : { status: 'unavailable' };
    } catch (error) {
      if (problemCodeOf(error) === CURSOR_INVALID_CODE) return { status: 'cursor_expired' };
      return statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  /**
   * What differs between two of a recipe's versions, as the server calculated it.
   *
   * `from` and `to` are version numbers as the history lists them, not version ids. Either order is allowed,
   * and the same number twice is a legitimate question whose answer is a comparison with nothing in it.
   *
   * The response carries no snapshots, and nothing here derives a difference of its own: a client that
   * recomputed one could disagree with the server about what changed, which is the reason this route
   * returns the answer rather than the inputs.
   */
  async compareVersions(
    workspaceSlug: string,
    recipeId: string,
    from: number,
    to: number,
  ): Promise<RecipeVersionComparisonOutcome> {
    const url = versionsUrl(this.apiBase, workspaceSlug, recipeId, '/compare');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.get<unknown>(url, {
          withCredentials: true,
          params: { from: String(from), to: String(to) },
        }),
      );
      const comparison = decodeRecipeVersionComparison(raw);
      return comparison ? { status: 'found', comparison } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);
      if (status === 400) return { status: 'invalid_request', fieldErrors: decodeFieldErrors(error) };

      // Both are 404, and the stable code is what tells them apart: a version this recipe does not have is
      // the creator's to fix, while a recipe they cannot see is not something to explain further.
      if (status === 404) {
        return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
          ? { status: 'version_not_found', fieldErrors: decodeFieldErrors(error) }
          : { status: 'not_found' };
      }

      return { status: 'unavailable' };
    }
  }

  /**
   * Shelves one recipe, or takes it back off the shelf.
   *
   * **Archiving is not deleting.** Every version, tag, media link, ingredient line and step stays where it
   * was, and the recipe is still readable by id — its history, its comparisons and copying it all keep
   * working. What changes is that it leaves the default library listing and stops accepting content changes
   * until it is brought back. Unarchiving returns it as a Draft, whatever it was before.
   *
   * The token is required even though both commands are idempotent, and for a reason idempotency does not
   * cover: archiving a recipe someone else is editing should say so rather than shelving work unseen. There
   * is no idempotency key — repeating either command succeeds and changes nothing.
   */
  async setArchived(
    workspaceSlug: string,
    recipeId: string,
    archived: boolean,
    request: RecipeLifecycleRequest,
  ): Promise<RecipeLifecycleOutcome> {
    const recipe = recipeUrl(this.apiBase, workspaceSlug, recipeId);
    if (!recipe) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(`${recipe}/${archived ? 'archive' : 'unarchive'}`, { ...request }, { withCredentials: true }),
      );
      const detail = decodeRecipeDetail(raw);
      return detail ? { status: 'updated', recipe: detail } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);
      if (status === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      if (status === 403) return { status: 'forbidden' };
      if (status === 404) return { status: 'not_found' };
      if (status === 409) return { status: 'conflict' };
      return { status: 'unavailable' };
    }
  }

  /**
   * Copies one recipe into a new, independent recipe of its own.
   *
   * The copy is not a derivative: editing either recipe does nothing to the other, it starts its own history
   * at version 1, and it is always a Draft whatever the source was. Everything but the title comes from the
   * source version — which is why this request carries no content and offers no copy options.
   *
   * There is no concurrency token and no conflict: nothing is being overwritten, so there is no prior state
   * to quote. Sending an idempotency key is worth doing anyway — a retried network failure would otherwise
   * leave the creator with two copies to tell apart.
   */
  async duplicateRecipe(
    workspaceSlug: string,
    recipeId: string,
    request: DuplicateRecipeRequest,
    idempotencyKey?: string,
  ): Promise<DuplicateRecipeOutcome> {
    const recipe = recipeUrl(this.apiBase, workspaceSlug, recipeId);
    if (!recipe) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(`${recipe}/duplicate`, encodeDuplicateRecipeRequest(request), {
          withCredentials: true,
          observe: 'response',
          ...idempotencyHeaders(idempotencyKey),
        }),
      );
      const created = decodeCreatedRecipe(response.body);
      return created ? { status: 'created', recipe: created, replayed: response.headers.has('Idempotent-Replayed') } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);
      if (status === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      if (status === 422) return { status: 'idempotency_key_conflict' };
      if (status === 403) return { status: 'forbidden' };

      // A version the source does not have is the creator's to fix; a recipe they cannot see is not.
      if (status === 404) {
        return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
          ? { status: 'version_not_found', fieldErrors: decodeFieldErrors(error) }
          : { status: 'not_found' };
      }

      return { status: 'unavailable' };
    }
  }

  /**
   * Puts a recipe back to what one of its versions said, as a *new* version.
   *
   * The named version is left exactly where it is: it does not become current, is not renumbered and is not
   * deleted. What the server writes is a new version whose content came from the archive — which is why this
   * request carries no recipe content at all, only the token the restore was decided against and, optionally,
   * the creator's reason.
   *
   * `expectedConcurrencyToken` is what makes the failure recoverable rather than destructive: a restore
   * composed against a state the recipe has moved past is refused with `conflict` rather than overwriting
   * whoever saved in between. Nothing of the creator's is lost by re-reading and asking again — the decision
   * was "put it back to version 3", not a body of prose.
   *
   * The caller owns the idempotency key across retries of one logical restore, as it does for saves: a retry
   * carrying the same key, version and token returns the original response instead of writing a second
   * version.
   */
  async restoreVersion(
    workspaceSlug: string,
    recipeId: string,
    versionNumber: number,
    request: RestoreRecipeVersionRequest,
    idempotencyKey?: string,
  ): Promise<RestoreRecipeVersionOutcome> {
    const url = versionsUrl(this.apiBase, workspaceSlug, recipeId, `/${versionNumber}/restore`);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRestoreRecipeVersionRequest(request), {
          withCredentials: true,
          observe: 'response',
          ...idempotencyHeaders(idempotencyKey),
        }),
      );
      const recipe = decodeRecipeDetail(response.body);
      return recipe ? { status: 'restored', recipe, replayed: response.headers.has('Idempotent-Replayed') } : { status: 'unavailable' };
    } catch (error) {
      const status = statusCodeOf(error);
      if (status === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      if (status === 422) return { status: 'idempotency_key_conflict' };
      if (status === 403) return { status: 'forbidden' };

      // The same split the comparison route makes, for the same reason: a version this recipe does not have
      // is the creator's to fix, while a recipe they cannot see is not something to explain further.
      if (status === 404) {
        return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
          ? { status: 'version_not_found', fieldErrors: decodeFieldErrors(error) }
          : { status: 'not_found' };
      }

      // Two 409s with opposite remedies, told apart by the stable code rather than by the status.
      if (status === 409) {
        return problemCodeOf(error) === ARCHIVED_CONFLICT_CODE ? { status: 'archived_conflict' } : { status: 'conflict' };
      }

      return { status: 'unavailable' };
    }
  }
}
