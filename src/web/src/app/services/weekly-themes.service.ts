import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  ThemedDayHistoryPage,
  WeeklyTheme,
  WeeklyThemeInput,
  decodeThemedDayHistoryPage,
  decodeWeeklyThemeWeek,
} from '../models/weekly-theme.models';
import { camelCaseFieldKey } from './brand-profile.service';

export type WeeklyThemeWeekOutcome =
  | { readonly status: 'found'; readonly themes: readonly WeeklyTheme[] }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type ReplaceWeeklyThemesOutcome =
  | { readonly status: 'saved'; readonly themes: readonly WeeklyTheme[] }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Another save changed the week while this one was in flight. Nothing was written. */
  | { readonly status: 'conflict' }
  /** The workspace holds as many theme rows as it may, retired ones included. */
  | { readonly status: 'limit' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type DeleteWeeklyThemeOutcome =
  | { readonly status: 'deleted'; readonly themes: readonly WeeklyTheme[] }
  | { readonly status: 'forbidden' }
  /** No such theme any more, which is what the creator asked for; the caller reloads the week. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type ThemedDayHistoryOutcome =
  | { readonly status: 'found'; readonly page: ThemedDayHistoryPage }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for a workspace's themed days (`…/weekly-themes`): the week, its one-write replace, deleting
 * a theme outright, and what was already made for one.
 *
 * Nothing is cached here. The week is read where it is edited, and the history is the server's to cache.
 */
@Injectable({ providedIn: 'root' })
export class WeeklyThemesService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** The live week, Monday first, then the retired themes. Any member may read. */
  async getWeek(workspaceSlug: string): Promise<WeeklyThemeWeekOutcome> {
    const url = this.themesUrl(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const themes = decodeWeeklyThemeWeek(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return themes ? { status: 'found', themes } : { status: 'unavailable' };
    } catch (error) {
      return { status: statusOf(error) === 404 ? 'not_found' : 'unavailable' };
    }
  }

  /**
   * Replaces the whole week. A live theme left out is retired, not deleted, and one whose key is submitted
   * again is brought back. Editor or above.
   */
  async replaceWeek(workspaceSlug: string, themes: readonly WeeklyThemeInput[]): Promise<ReplaceWeeklyThemesOutcome> {
    const url = this.themesUrl(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const saved = decodeWeeklyThemeWeek(
        await firstValueFrom(this.http.put<unknown>(url, { themes }, { withCredentials: true })),
      );

      return saved ? { status: 'saved', themes: saved } : { status: 'unavailable' };
    } catch (error) {
      switch (statusOf(error)) {
        case 400:
          return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
        case 403:
          return { status: 'forbidden' };
        case 404:
          return { status: 'not_found' };
        case 409:
          return { status: 'conflict' };
        case 422:
          return { status: 'limit' };
        default:
          return { status: 'unavailable' };
      }
    }
  }

  /** Deletes one theme outright, so its key stops resolving. Owner only. Answers with the remaining week. */
  async deleteTheme(workspaceSlug: string, key: string): Promise<DeleteWeeklyThemeOutcome> {
    const base = this.themesUrl(workspaceSlug);
    if (!base) return { status: 'unavailable' };

    try {
      const themes = decodeWeeklyThemeWeek(
        await firstValueFrom(this.http.delete<unknown>(`${base}/${encodeURIComponent(key)}`, { withCredentials: true })),
      );

      return themes ? { status: 'deleted', themes } : { status: 'unavailable' };
    } catch (error) {
      const code = statusOf(error);
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      return { status: 'unavailable' };
    }
  }

  /** One page of what the workspace already made for a theme, newest first. A retired theme still reads. */
  async getHistory(
    workspaceSlug: string,
    key: string,
    options: { readonly cursor?: string | null; readonly limit?: number } = {},
  ): Promise<ThemedDayHistoryOutcome> {
    const base = this.themesUrl(workspaceSlug);
    if (!base) return { status: 'unavailable' };

    const params: Record<string, string> = {};
    if (options.cursor) params['cursor'] = options.cursor;
    if (options.limit !== undefined) params['limit'] = String(options.limit);

    try {
      const page = decodeThemedDayHistoryPage(
        await firstValueFrom(
          this.http.get<unknown>(`${base}/${encodeURIComponent(key)}/history`, { params, withCredentials: true }),
        ),
      );

      return page ? { status: 'found', page } : { status: 'unavailable' };
    } catch (error) {
      return { status: statusOf(error) === 404 ? 'not_found' : 'unavailable' };
    }
  }

  private themesUrl(workspaceSlug: string): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/weekly-themes`);
  }
}

function statusOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** `ValidationProblemDetails.errors`, keyed in this client's casing: `themes[0].DisplayName` → `themes[0].displayName`. */
function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const errors = body?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[camelCaseFieldKey(field)] = messages;
    }
  }
  return result;
}
