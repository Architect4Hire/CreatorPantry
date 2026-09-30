import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  CreateRecipeTestRunRequest,
  CreatedRecipeTestRun,
  RecipeTestRun,
  ResolveTestIssueRequest,
  ResolvedTestIssue,
  TestRunHistoryPage,
  TestRunHistoryQuery,
  UpdateRecipeTestRunRequest,
  decodeCreatedRecipeTestRun,
  decodeRecipeTestRun,
  decodeResolvedTestIssue,
  decodeTestRunHistoryPage,
  encodeCreateRecipeTestRunRequest,
  encodeResolveTestIssueRequest,
  encodeTestRunHistoryQuery,
  encodeUpdateRecipeTestRunRequest,
} from '../models/recipe-test-run.models';

export type CreateTestRunOutcome =
  | { readonly status: 'created'; readonly testRun: CreatedRecipeTestRun; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /**
   * The recipe is readable and has no version with that number; `fieldErrors` names `sourceVersionNumber`.
   * Its own outcome rather than a plain 404 because the remedy differs: this one names a field.
   */
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Recording a test carries the Contributor bar — the same as creating a recipe. */
  | { readonly status: 'forbidden' }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  /** The recipe is archived. Retrying will fail forever; bringing it back is the remedy. */
  | { readonly status: 'archived_conflict' }
  /** The `Idempotency-Key` was reused for a different body. Generate a fresh one and retry. */
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'unavailable' };

export type UpdateTestRunOutcome =
  /** The whole test as it now stands, carrying the refreshed token the next edit must quote. */
  | { readonly status: 'updated'; readonly testRun: RecipeTestRun; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'forbidden' }
  /** Unknown test, another workspace's, or one belonging to a different recipe — all one answer. */
  | { readonly status: 'not_found' }
  /** The token quoted is one the test has moved past: somebody else saved first. Nothing was written. */
  | { readonly status: 'conflict' }
  /**
   * The submitted list dropped an issue that has been resolved. Separate from `conflict` although both are
   * 409s, because re-reading fixes one and nothing fixes the other but putting the issue back.
   */
  | { readonly status: 'issue_removal_conflict' }
  | { readonly status: 'archived_conflict' }
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'unavailable' };

export type TestRunHistoryOutcome =
  | { readonly status: 'found'; readonly page: TestRunHistoryPage }
  /** The cursor was issued for a different workspace, recipe or set of filters. Start the list again. */
  | { readonly status: 'cursor_expired' }
  /** A filter the server could not read; `fieldErrors` names which one. */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Unknown recipe, or one belonging to another workspace. An untested recipe is an empty page, not this. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type ResolveTestIssueOutcome =
  | { readonly status: 'resolved'; readonly resolved: ResolvedTestIssue; readonly replayed: boolean }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Recording a decision about an issue carries the Editor bar, a step above reporting one. */
  | { readonly status: 'forbidden' }
  /** The test or the issue is not there, or belongs to another workspace. */
  | { readonly status: 'not_found' }
  /** The recipe has no version with that number; `fieldErrors` names `resolutionVersionNumber`. */
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Already resolved. Immutable once written, so retrying will never change this. */
  | { readonly status: 'already_resolved' }
  | { readonly status: 'idempotency_key_conflict' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body, which is what distinguishes one 409 from another. */
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

/** RecipeErrorCodes.VersionNotFound — a missing version rather than an unreadable recipe on a 404. */
const VERSION_NOT_FOUND_CODE = 'recipes.version.not_found';

/** RecipeErrorCodes.RecipeArchivedConflict — the 409 whose remedy is to unarchive, not to retry. */
const ARCHIVED_CONFLICT_CODE = 'recipes.archived.conflict';

/** RecipeErrorCodes.TestIssueRemovalConflict — a resolved issue cannot be dropped by omitting it. */
const ISSUE_REMOVAL_CONFLICT_CODE = 'recipes.testIssue.removal.conflict';

/** RecipeErrorCodes.CursorInvalidRequest — a stale cursor, whose remedy is to start the list again. */
const CURSOR_INVALID_CODE = 'recipes.cursor.invalid_request';

/**
 * The typed client for recording and correcting one cook of one exact version (TESTRUN-001/002).
 *
 * **Two routes, and deliberately not four.** Reading a test's history and resolving one of its issues are
 * different surfaces with different roles behind them — a Viewer may read, only an Editor may resolve — and
 * neither is reachable from the entry form. They belong to the client that renders them.
 *
 * The caller owns the idempotency key across retries of one logical save, and owns round-tripping
 * `concurrencyToken` into the next edit's `expectedConcurrencyToken`. No component may inject `HttpClient`
 * for these calls; this service is the only path (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class RecipeTestRunService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async createTestRun(
    workspaceSlug: string,
    recipeId: string,
    request: CreateRecipeTestRunRequest,
    idempotencyKey?: string,
  ): Promise<CreateTestRunOutcome> {
    const url = this.testRunsUrl(workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeCreateRecipeTestRunRequest(request), {
          withCredentials: true,
          observe: 'response',
          ...idempotencyHeaders(idempotencyKey),
        }),
      );
      const testRun = decodeCreatedRecipeTestRun(response.body);
      return testRun
        ? { status: 'created', testRun, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) {
        // Both are 404, and the stable code is what tells them apart: a version this recipe does not have
        // names a field the creator can act on, while a recipe they cannot see is not explained further.
        return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
          ? { status: 'version_not_found', fieldErrors: decodeFieldErrors(error) }
          : { status: 'not_found' };
      }
      if (code === 409) return { status: 'archived_conflict' };
      if (code === 422) return { status: 'idempotency_key_conflict' };
      return { status: 'unavailable' };
    }
  }

  async updateTestRun(
    workspaceSlug: string,
    recipeId: string,
    testRunId: string,
    request: UpdateRecipeTestRunRequest,
    idempotencyKey?: string,
  ): Promise<UpdateTestRunOutcome> {
    const url = this.testRunsUrl(workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.patch<unknown>(`${url}/${encodeURIComponent(testRunId)}`, encodeUpdateRecipeTestRunRequest(request), {
          withCredentials: true,
          observe: 'response',
          ...idempotencyHeaders(idempotencyKey),
        }),
      );
      const testRun = decodeRecipeTestRun(response.body);
      return testRun
        ? { status: 'updated', testRun, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      if (code === 409) {
        // Three different 409s with three different remedies: re-read, put the issue back, or unarchive.
        // Retrying the same request answers the first and never the other two.
        switch (problemCodeOf(error)) {
          case ARCHIVED_CONFLICT_CODE:
            return { status: 'archived_conflict' };
          case ISSUE_REMOVAL_CONFLICT_CODE:
            return { status: 'issue_removal_conflict' };
          default:
            return { status: 'conflict' };
        }
      }
      if (code === 422) return { status: 'idempotency_key_conflict' };
      return { status: 'unavailable' };
    }
  }

  /**
   * One page of a recipe's recorded tests, most recently **cooked** first.
   *
   * An Observable where the writes above are Promises, and that is the point rather than an inconsistency: a
   * filter changes as fast as a creator can click, and unsubscribing an `HttpClient` request aborts it. A
   * caller piping this through `switchMap` therefore cancels the page it has superseded — which matters
   * because an earlier, slower response arriving after a later one would otherwise show the wrong rows under
   * the wrong filters.
   *
   * It never errors. Every outcome is a value, so a failed page cannot kill the subscription and leave the
   * screen unable to search again without being rebuilt.
   */
  listTestRuns(
    workspaceSlug: string,
    recipeId: string,
    query: TestRunHistoryQuery,
  ): Observable<TestRunHistoryOutcome> {
    const url = this.testRunsUrl(workspaceSlug, recipeId);
    if (!url) return of<TestRunHistoryOutcome>({ status: 'unavailable' });

    return this.http
      .get<unknown>(url, { withCredentials: true, params: encodeTestRunHistoryQuery(query) })
      .pipe(
        map((raw): TestRunHistoryOutcome => {
          const page = decodeTestRunHistoryPage(raw);
          return page ? { status: 'found', page } : { status: 'unavailable' };
        }),
        catchError((error: unknown) => {
          const code = statusCodeOf(error);

          if (code === 400) {
            // Two different 400s with two different remedies: a stale cursor means start again from the first
            // page, while a rejected filter means the request itself has to change. Telling them apart is why
            // the API gives them separate codes.
            return of<TestRunHistoryOutcome>(
              problemCodeOf(error) === CURSOR_INVALID_CODE
                ? { status: 'cursor_expired' }
                : { status: 'invalid_request', fieldErrors: decodeFieldErrors(error) },
            );
          }

          // Unlike the recipe search, a 404 here is real: it is the recipe. A recipe nobody has tested answers
          // an empty page instead.
          return of<TestRunHistoryOutcome>(code === 404 ? { status: 'not_found' } : { status: 'unavailable' });
        }),
      );
  }

  /**
   * Records what was done about one issue a test found.
   *
   * A separate command from the edit above, and deliberately so: it writes exactly one row and rewrites
   * neither the issue nor the observation it came from. It carries no concurrency token, because nothing is
   * being overwritten — the conflict it has instead is an issue already resolved, which no retry can change.
   */
  async resolveIssue(
    workspaceSlug: string,
    recipeId: string,
    testRunId: string,
    issueId: string,
    request: ResolveTestIssueRequest,
    idempotencyKey?: string,
  ): Promise<ResolveTestIssueOutcome> {
    const url = this.testRunsUrl(workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    const path =
      `${url}/${encodeURIComponent(testRunId)}/issues/${encodeURIComponent(issueId)}/resolution`;

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(path, encodeResolveTestIssueRequest(request), {
          withCredentials: true,
          observe: 'response',
          ...idempotencyHeaders(idempotencyKey),
        }),
      );
      const resolved = decodeResolvedTestIssue(response.body);
      return resolved
        ? { status: 'resolved', resolved, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) {
        // A version this recipe does not have names the field a creator can fix; an unknown test or issue does
        // not, and both are 404.
        return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
          ? { status: 'version_not_found', fieldErrors: decodeFieldErrors(error) }
          : { status: 'not_found' };
      }
      if (code === 409) return { status: 'already_resolved' };
      if (code === 422) return { status: 'idempotency_key_conflict' };
      return { status: 'unavailable' };
    }
  }

  private testRunsUrl(workspaceSlug: string, recipeId: string): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}/test-runs`,
    );
  }
}
