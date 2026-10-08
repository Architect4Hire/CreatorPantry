import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  PromptDetail,
  PromptSearchPage,
  PromptSearchQuery,
  decodePromptDetail,
  decodePromptSearchPage,
  encodePromptSearchQuery,
} from '../models/prompt-library.models';
import { problemCodeOf, statusCodeOf } from './ai-request';

export type PromptSearchOutcome =
  | { readonly status: 'found'; readonly page: PromptSearchPage }
  /** The cursor was issued for a different workspace or set of filters. Start the list again. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'unavailable' };

export type PromptDetailOutcome =
  | { readonly status: 'found'; readonly prompt: PromptDetail }
  /** Unknown, or another workspace's. One answer for both, as the route gives. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** `ContentErrorCodes.PromptCursorInvalid` — a stale cursor, whose remedy is to start the list again. */
const CURSOR_INVALID_CODE = 'content.prompt.cursor.invalid';

/**
 * The typed client for the workspace's prompt library: searching it, reading one prompt, and the two links a
 * browser follows to download one (PRM-002 through PRM-005).
 *
 * **Read-only.** A saved prompt is immutable — no route updates or deletes one — so nothing here writes.
 *
 * **A prompt is named by its id and never by a location.** Every URL built here is the gateway's own route with
 * a workspace slug and a guid in it. The downloads are links rather than fetches: the server names the file and
 * sets the disposition, so building a name here would be a second guess at a decision the route already makes.
 */
@Injectable({ providedIn: 'root' })
export class PromptLibraryService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** One page, newest first. Follow `nextCursor` until it is null. Any member may read. */
  search(workspaceSlug: string, query: PromptSearchQuery): Observable<PromptSearchOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return of<PromptSearchOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true, params: encodePromptSearchQuery(query) }).pipe(
      map((raw): PromptSearchOutcome => {
        const page = decodePromptSearchPage(raw);

        return page ? { status: 'found', page } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<PromptSearchOutcome>(
          statusCodeOf(error) === 400 && problemCodeOf(error) === CURSOR_INVALID_CODE
            ? { status: 'cursor_expired' }
            : { status: 'unavailable' },
        ),
      ),
    );
  }

  /** One prompt in full. The only read that carries the prompt itself. */
  get(workspaceSlug: string, promptRecordId: string): Observable<PromptDetailOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(promptRecordId)}`);
    if (!url) return of<PromptDetailOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): PromptDetailOutcome => {
        const prompt = decodePromptDetail(raw);

        return prompt ? { status: 'found', prompt } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<PromptDetailOutcome>(statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' }),
      ),
    );
  }

  /** The link that downloads the prompt as a `.txt` file, or null while the gateway address is not known. */
  textDownloadUrl(workspaceSlug: string, promptRecordId: string): string | null {
    return this.url(workspaceSlug, `/${encodeURIComponent(promptRecordId)}/text`);
  }

  /** The link that downloads the prompt and its provenance as a `.json` file. */
  recordDownloadUrl(workspaceSlug: string, promptRecordId: string): string | null {
    return this.url(workspaceSlug, `/${encodeURIComponent(promptRecordId)}/record`);
  }

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/prompts${suffix}`);
  }
}
