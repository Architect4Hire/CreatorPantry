import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { MeasurementUnit, decodeCursorPage, decodeMeasurementUnit } from '../models/reference.models';

export type ListUnitsOutcome =
  | { readonly status: 'found'; readonly units: readonly MeasurementUnit[] }
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

  /**
   * Every active unit of measure, following the cursor until the catalogue is exhausted.
   *
   * A partial catalogue is not returned: a picker built from half the units would silently be missing the
   * one a creator is looking for, which reads as "this unit does not exist" rather than as a failure. A page
   * that cannot be read answers `unavailable` for the whole listing.
   */
  listUnits(): Promise<ListUnitsOutcome> {
    this.unitsInFlight ??= this.readUnits().then((outcome) => {
      if (outcome.status !== 'found') this.unitsInFlight = null;
      return outcome;
    });

    return this.unitsInFlight;
  }

  private async readUnits(): Promise<ListUnitsOutcome> {
    const url = this.apiBase.url('/api/v1/reference/units');
    if (!url) return { status: 'unavailable' };

    const units: MeasurementUnit[] = [];
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
        return { status: 'unavailable' };
      }

      const decoded = decodeCursorPage(raw, decodeMeasurementUnit);
      if (decoded === null) return { status: 'unavailable' };

      units.push(...decoded.items);
      if (decoded.nextCursor === null) return { status: 'found', units };

      cursor = decoded.nextCursor;
    }

    // The server is still offering more after MAX_PAGES. Answering `found` here would hand back a catalogue
    // that is quietly incomplete, which is the one thing this method promises not to do.
    return { status: 'unavailable' };
  }
}
