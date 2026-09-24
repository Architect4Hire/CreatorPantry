import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  ParseIngredientLinesRequest,
  ParseIngredientLinesResponse,
  decodeParseIngredientLinesResponse,
  encodeParseIngredientLinesRequest,
} from '../models/ingredient-parse.models';

export type ParseIngredientLinesOutcome =
  | { readonly status: 'parsed'; readonly lines: ParseIngredientLinesResponse['lines'] }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
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

function ingredientToolsUrl(apiBase: ApiBaseService, workspaceSlug: string): string | null {
  return apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/ingredient-tools/parse`);
}

/**
 * The typed client for `POST .../ingredient-tools/parse` (ING-001, 7.4). Read-only: nothing here is
 * persisted, and no component may inject `HttpClient` directly for this call (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class IngredientParseService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async parseIngredientLines(workspaceSlug: string, lines: readonly string[]): Promise<ParseIngredientLinesOutcome> {
    const url = ingredientToolsUrl(this.apiBase, workspaceSlug);
    if (!url) return { status: 'unavailable' };

    const request: ParseIngredientLinesRequest = { lines };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeParseIngredientLinesRequest(request), { withCredentials: true }),
      );
      const response = decodeParseIngredientLinesResponse(raw);
      return response ? { status: 'parsed', lines: response.lines } : { status: 'unavailable' };
    } catch (error) {
      if (statusCodeOf(error) === 400) return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      return { status: 'unavailable' };
    }
  }
}
