import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  CookingTechnique,
  Ingredient,
  MeasurementUnit,
  ReferenceEntry,
  decodeCookingTechnique,
  decodeCursorPage,
  decodeIngredient,
  decodeMeasurementUnit,
  decodeReferenceEntry,
} from '../models/reference.models';

export type ListUnitsOutcome =
  | { readonly status: 'found'; readonly units: readonly MeasurementUnit[] }
  | { readonly status: 'unavailable' };

/** One plain controlled vocabulary, whole. */
export type ListReferenceEntriesOutcome =
  | { readonly status: 'found'; readonly entries: readonly ReferenceEntry[] }
  | { readonly status: 'unavailable' };

export type ListTechniquesOutcome =
  | { readonly status: 'found'; readonly techniques: readonly CookingTechnique[] }
  | { readonly status: 'unavailable' };

/**
 * What one ingredient search came back with.
 *
 * `tooShort` is its own answer rather than an empty `found`: the server ignores a term under
 * `INGREDIENT_SEARCH_MIN_LENGTH` and returns the first page unfiltered, so reporting zero matches would be a
 * lie and reporting `unavailable` would blame an outage for a term nobody finished typing.
 */
export type SearchIngredientsOutcome =
  | { readonly status: 'found'; readonly ingredients: readonly Ingredient[]; readonly hasMore: boolean }
  | { readonly status: 'tooShort' }
  | { readonly status: 'unavailable' };

/**
 * The largest page the server will serve (`ReferencePolicy.MaxPageSize`). Asking for it directly means the
 * catalogue as it stands today arrives in one request, and the cursor loop below is what keeps that true
 * after it grows rather than a promise that it never will.
 */
const MAX_PAGE_SIZE = 100;

/**
 * A ceiling on how many pages one listing will follow.
 *
 * Not a page-size guess: it is a guard against a server that keeps handing back a cursor, so a catalogue
 * read can never become an unbounded loop in a creator's browser.
 */
const MAX_PAGES = 20;

/**
 * Below this a search term is ignored server-side (`ReferencePolicy.MinSearchLength`), which would come back
 * as the first page of the whole catalogue. Exported so a type-ahead can say "keep typing" with the same
 * threshold the server applies rather than a guess at it.
 */
export const INGREDIENT_SEARCH_MIN_LENGTH = 2;

/** `ReferencePolicy.SearchMaxLength`. Above it the server refuses the request. */
const SEARCH_MAX_LENGTH = 128;

/**
 * How many matches one ingredient search asks for.
 *
 * Small on purpose: this fills a dropdown someone is reading while they type, and a list longer than this is
 * narrowed faster by another keystroke than by scrolling.
 */
const INGREDIENT_SEARCH_PAGE_SIZE = 20;

/**
 * The typed client for the shared platform catalogue, `/api/v1/reference/*`.
 *
 * **Global, never workspace-scoped.** These routes sit outside `/workspaces/{workspaceSlug}`; reference data
 * has no `WorkspaceId` to filter by, and nothing here may take, send or cache a workspace identifier
 * (tenancy.md). Everything it returns is the same for every creator.
 *
 * No component may inject `HttpClient` directly for these calls; this service is the only path
 * (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class ReferenceService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * The unit catalogue once it has been read, shared by every caller for the rest of the session.
   *
   * Safe to hold indefinitely, and safe to share, for the reason this whole service exists: reference data
   * is global and has no `WorkspaceId`, so one creator's copy is every creator's copy and switching
   * workspaces cannot make it wrong (tenancy.md). Held as the in-flight promise rather than the result, so
   * two sections asking at once make one request between them.
   *
   * A failed read is not kept: the next caller tries again rather than inheriting an outage.
   */
  private unitsInFlight: Promise<ListUnitsOutcome> | null = null;

  /** The three vocabularies that describe a dish, cached for the session on the same terms as the units. */
  private cuisinesInFlight: Promise<ListReferenceEntriesOutcome> | null = null;
  private coursesInFlight: Promise<ListReferenceEntriesOutcome> | null = null;
  private techniquesInFlight: Promise<ListTechniquesOutcome> | null = null;

  /**
   * Every active unit of measure, following the cursor until the catalogue is exhausted.
   *
   * A partial catalogue is not returned: a picker built from half the units would silently be missing the
   * one a creator is looking for, which reads as "this unit does not exist" rather than as a failure. A page
   * that cannot be read answers `unavailable` for the whole listing.
   */
  listUnits(): Promise<ListUnitsOutcome> {
    this.unitsInFlight ??= this.readCatalogue('/api/v1/reference/units', decodeMeasurementUnit).then((units) => {
      if (units === null) {
        this.unitsInFlight = null;
        return { status: 'unavailable' } as const;
      }

      return { status: 'found', units } as const;
    });

    return this.unitsInFlight;
  }

  /** Every active cuisine. Whole or `unavailable`, for {@link listUnits}'s reason. */
  listCuisines(): Promise<ListReferenceEntriesOutcome> {
    this.cuisinesInFlight ??= this.readEntries('/api/v1/reference/cuisines').then((outcome) => {
      if (outcome.status !== 'found') this.cuisinesInFlight = null;
      return outcome;
    });

    return this.cuisinesInFlight;
  }

  /** Every active course — the role a dish plays in a meal. */
  listCourses(): Promise<ListReferenceEntriesOutcome> {
    this.coursesInFlight ??= this.readEntries('/api/v1/reference/courses').then((outcome) => {
      if (outcome.status !== 'found') this.coursesInFlight = null;
      return outcome;
    });

    return this.coursesInFlight;
  }

  /**
   * Every active cooking method, each with the caution flag the catalogue holds for it.
   *
   * A caller that renders a method must carry that flag through: `false` is the absence of an attached
   * caution, not a finding that the method is safe (.claude/rules/ai.md).
   */
  listTechniques(): Promise<ListTechniquesOutcome> {
    this.techniquesInFlight ??= this.readCatalogue(
      '/api/v1/reference/techniques',
      decodeCookingTechnique,
    ).then((techniques) => {
      if (techniques === null) {
        this.techniquesInFlight = null;
        return { status: 'unavailable' } as const;
      }

      return { status: 'found', techniques } as const;
    });

    return this.techniquesInFlight;
  }

  private async readEntries(path: string): Promise<ListReferenceEntriesOutcome> {
    const entries = await this.readCatalogue(path, decodeReferenceEntry);

    return entries === null ? { status: 'unavailable' } : { status: 'found', entries };
  }

  /**
   * Every page of one reference catalogue, or null when any part of it could not be read.
   *
   * Null rather than a short list, for the reason {@link listUnits} gives: a picker built from half a
   * catalogue reads as "that entry does not exist" rather than as a failure, which is the one answer a
   * catalogue read must never give.
   */
  private async readCatalogue<T>(path: string, decodeItem: (item: unknown) => T | null): Promise<T[] | null> {
    const url = this.apiBase.url(path);
    if (!url) return null;

    const items: T[] = [];
    let cursor: string | null = null;

    for (let page = 0; page < MAX_PAGES; page++) {
      const params: Record<string, string> = { limit: String(MAX_PAGE_SIZE) };
      if (cursor !== null) params['cursor'] = cursor;

      let raw: unknown;
      try {
        raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true, params }));
      } catch (error) {
        // Nothing to distinguish here: a stale cursor, a refusal and an outage all leave the caller with no
        // usable catalogue, and the remedy for every one of them is to try again.
        void (error as HttpErrorResponse);
        return null;
      }

      const decoded = decodeCursorPage(raw, decodeItem);
      if (decoded === null) return null;

      items.push(...decoded.items);
      if (decoded.nextCursor === null) return items;

      cursor = decoded.nextCursor;
    }

    // The server is still offering more after MAX_PAGES. Answering with what arrived would hand back a
    // catalogue that is quietly incomplete, which is the one thing this method promises not to do.
    return null;
  }

  /**
   * Ingredients in the shared catalogue matching `search`, as one page.
   *
   * Unlike {@link listUnits} this reads a single page and never follows the cursor. The ingredient catalogue
   * is far too large to hold in a browser, so this is a search rather than a catalogue read, and the way to
   * see past the first page of matches is to type more of the name — which is why `hasMore` is reported
   * rather than paged through.
   *
   * **Returns an observable, not a promise, so that a caller can cancel it.** Unsubscribing aborts the
   * request in flight, which is what lets a type-ahead abandon the answer to a term the creator has already
   * typed past instead of racing it against the next one. Nothing here debounces: when to ask is the calling
   * surface's decision, not this service's.
   *
   * Not cached either, for the same reason — the term is the key, and the server already caches reference
   * pages under a global key.
   */
  searchIngredients(search: string): Observable<SearchIngredientsOutcome> {
    // Cut rather than refused: the server rejects a longer term outright, and a creator who pasted a
    // paragraph into the box is better served by a search on the front of it than by an error.
    const term = search.trim().slice(0, SEARCH_MAX_LENGTH);
    if (term.length < INGREDIENT_SEARCH_MIN_LENGTH) return of<SearchIngredientsOutcome>({ status: 'tooShort' });

    const url = this.apiBase.url('/api/v1/reference/ingredients');
    if (!url) return of<SearchIngredientsOutcome>({ status: 'unavailable' });

    return this.http
      .get<unknown>(url, {
        withCredentials: true,
        params: { search: term, limit: String(INGREDIENT_SEARCH_PAGE_SIZE) },
      })
      .pipe(
        map((raw): SearchIngredientsOutcome => {
          const decoded = decodeCursorPage(raw, decodeIngredient);
          if (decoded === null) return { status: 'unavailable' };

          return { status: 'found', ingredients: decoded.items, hasMore: decoded.nextCursor !== null };
        }),
        // A refusal, an outage and a shape this client cannot read are one answer to the caller: no list to
        // offer. The typed text stands either way, so a failed search costs a suggestion, not an edit.
        catchError(() => of<SearchIngredientsOutcome>({ status: 'unavailable' })),
      );
  }
}
