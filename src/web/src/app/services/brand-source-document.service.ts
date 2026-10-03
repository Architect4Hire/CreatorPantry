import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of, takeUntil } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  BrandLibraryPage,
  BrandLibraryQuery,
  BrandSourceDescription,
  BrandSourceDocumentDetail,
  BrandSourceDocumentPage,
  BrandSourceDocumentSummary,
  BrandSourcePasteBody,
  BrandSourceUsage,
  BrandSourceVersionPage,
  decodeBrandLibraryPage,
  decodeBrandSourceDocument,
  decodeBrandSourceDocumentDetail,
  decodeBrandSourceDocumentPage,
  decodeBrandSourceUsage,
  decodeBrandSourceVersionPage,
} from '../models/brand-source-document.models';

/** Why an add did not happen. Each has plain-language copy in the wizard; none leaks a server code. */
export type BrandSourceAddFailure =
  | 'unsupported'
  | 'too_large'
  | 'corrupt'
  | 'rejected'
  | 'invalid'
  | 'forbidden'
  | 'not_found'
  | 'key_reused'
  | 'cancelled'
  /** The document moved since the read this was composed against. Only a replacement can meet this. */
  | 'conflict'
  /** The document is on the shelf and takes no new version until it is restored. */
  | 'archived_conflict'
  | 'unavailable';

export type BrandSourceAddOutcome =
  | { readonly status: 'added'; readonly document: BrandSourceDocumentSummary; readonly replayed: boolean }
  | { readonly status: 'failed'; readonly reason: BrandSourceAddFailure };

/**
 * One page of the Brand Library, or why there is not one.
 *
 * `cursor_expired` and `invalid_request` are both the API's one `brand.source.invalid_request`, told apart by
 * which field it names, because the remedies differ: a refused cursor means start again at the first page of
 * the same search, while a refused filter means the request itself has to change.
 */
export type BrandLibraryOutcome =
  | { readonly status: 'ok'; readonly page: BrandLibraryPage }
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'invalid_request' }
  | { readonly status: 'unavailable' };

export type BrandSourceListOutcome =
  | { readonly status: 'ok'; readonly page: BrandSourceDocumentPage }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type BrandSourceVersionsOutcome =
  | { readonly status: 'ok'; readonly page: BrandSourceVersionPage }
  /** The cursor was issued for another document or filter set. Start the history again without one. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type BrandSourceUsageOutcome =
  | { readonly status: 'ok'; readonly usage: BrandSourceUsage }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * The outcome of adding a new version. `conflict` is the document having moved since the read this
 * replacement was composed against, and `archived` is a document on the shelf — restore it first. Both leave
 * the creator's chosen file where it is, because neither wrote anything.
 */
export type BrandSourceReplaceOutcome =
  | { readonly status: 'replaced'; readonly document: BrandSourceDocumentSummary; readonly replayed: boolean }
  | { readonly status: 'conflict' }
  | { readonly status: 'archived' }
  | { readonly status: 'failed'; readonly reason: BrandSourceAddFailure };

export type BrandSourceDetailOutcome =
  | { readonly status: 'ok'; readonly document: BrandSourceDocumentDetail }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** The outcome of putting a document on the shelf or bringing it back. */
export type BrandSourceShelfOutcome =
  | { readonly status: 'done'; readonly document: BrandSourceDocumentDetail }
  /** The document changed since it was read, so nothing was changed. Read it again and retry. */
  | { readonly status: 'conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

const FAILURE_BY_CODE: Readonly<Record<string, BrandSourceAddFailure>> = {
  'brand.source.file.unsupported.unprocessable': 'unsupported',
  'brand.source.file.payload_too_large': 'too_large',
  'brand.source.file.corrupt.unprocessable': 'corrupt',
  'brand.source.file.rejected.unprocessable': 'rejected',
  'brand.source.invalid_request': 'invalid',
  'brand.source.forbidden': 'forbidden',
  'idempotency.key_reused': 'key_reused',
  // Both are a replacement's own refusals, and they are told apart because the remedies differ: read the
  // document again and retry, versus restore it first.
  'brand.source.conflict': 'conflict',
  'brand.source.archived.conflict': 'archived_conflict',
  // Both mean "nothing was recorded, try again": the scan or the private store could not be reached.
  'brand.source.scan.unavailable': 'unavailable',
  'brand.source.storage.unavailable': 'unavailable',
};

/**
 * The query string for one library page. Only what is actually being filtered on is sent: an empty or null
 * filter is left out entirely rather than sent as an empty value the server would have to interpret.
 */
function encodeBrandLibraryQuery(query: BrandLibraryQuery): Record<string, string> {
  const params: Record<string, string> = { status: query.status, limit: String(query.limit) };

  const search = query.search.trim();
  if (search.length > 0) params['search'] = search;
  if (query.documentType) params['documentType'] = query.documentType;
  if (query.purpose) params['purpose'] = query.purpose;
  if (query.channelKey) params['channelKey'] = query.channelKey;
  if (query.extractionState) params['extractionState'] = query.extractionState;

  // Sent verbatim. A cursor is the server's own string and re-encoding it would be a different position.
  if (query.cursor) params['cursor'] = query.cursor;

  return params;
}

/** Whether a refusal is about the cursor, which is recoverable, rather than about a filter, which is not. */
function namesCursor(error: HttpErrorResponse): boolean {
  const body = error.error as Record<string, unknown> | null;
  const errors = body?.['errors'];
  return typeof errors === 'object' && errors !== null && 'cursor' in errors;
}

function failureOf(error: unknown): BrandSourceAddFailure {
  if (!(error instanceof HttpErrorResponse)) return 'unavailable';
  const body = error.error as Record<string, unknown> | null;
  const code = typeof body?.['code'] === 'string' ? body['code'] : null;
  if (code && FAILURE_BY_CODE[code]) return FAILURE_BY_CODE[code];
  if (error.status === 404) return 'not_found';
  if (error.status === 403) return 'forbidden';
  if (error.status === 413) return 'too_large';
  if (error.status === 400) return 'invalid';

  // A 409 with no code this client knows is still a conflict, and re-reading is still the remedy.
  if (error.status === 409) return 'conflict';
  return 'unavailable';
}

/**
 * The typed client for a workspace's brand source documents, as far as the setup wizard needs them: list,
 * upload a file, and add pasted text. The caller owns the `Idempotency-Key` across retries of one logical add,
 * so a retry after a dropped response returns the first result instead of storing a second document. No
 * workspace id is ever sent: the workspace is the route. Components never inject `HttpClient`.
 */
@Injectable({ providedIn: 'root' })
export class BrandSourceDocumentService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/brand-source-documents${suffix}`);
  }

  /** One page of the workspace's active documents, newest edit first. */
  async list(workspaceSlug: string, limit = 50): Promise<BrandSourceListOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };
    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true, params: { limit } }));
      const page = decodeBrandSourceDocumentPage(body);
      return page ? { status: 'ok', page } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  /**
   * One page of the Brand Library under the given filters.
   *
   * Returns an Observable where most of this service returns Promises, for the reason `RecipeService.searchRecipes`
   * does: a library screen re-runs this on every keystroke, and unsubscribing an `HttpClient` request aborts
   * it, so a caller piping through `switchMap` cancels the request it has superseded. A slow earlier response
   * can then never land on top of a later one and show the creator a page they are no longer asking for.
   *
   * It never errors: every outcome is a value, so a failed page cannot kill the subscription and leave the
   * screen unable to search again.
   *
   * Every filter goes to the server. Nothing is narrowed here — a page is a page of a filtered set, so
   * filtering in the browser would drop matches that live on pages this screen has not fetched.
   */
  searchLibrary(workspaceSlug: string, query: BrandLibraryQuery): Observable<BrandLibraryOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return of<BrandLibraryOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true, params: encodeBrandLibraryQuery(query) }).pipe(
      map((raw): BrandLibraryOutcome => {
        const page = decodeBrandLibraryPage(raw);
        return page ? { status: 'ok', page } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 400) {
          return of<BrandLibraryOutcome>(namesCursor(error) ? { status: 'cursor_expired' } : { status: 'invalid_request' });
        }

        // A 404 here is the workspace, not a document: an empty library is an empty page. Reported as
        // unavailable because the remedy is the same as for any other failed read.
        return of<BrandLibraryOutcome>({ status: 'unavailable' });
      }),
    );
  }

  /** One document, with the token the next archive or restore must quote. Archived documents read normally. */
  async get(workspaceSlug: string, documentId: string): Promise<BrandSourceDetailOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(documentId)}`);
    if (!url) return { status: 'unavailable' };
    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const document = decodeBrandSourceDocumentDetail(body);
      return document ? { status: 'ok', document } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  /**
   * One page of a document's version history, newest version first.
   *
   * A cursor is bound to the document it was issued for, so `cursor_expired` means start the history again
   * rather than that anything is wrong with the document.
   */
  async listVersions(workspaceSlug: string, documentId: string, cursor: string | null = null, limit = 25): Promise<BrandSourceVersionsOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(documentId)}/versions`);
    if (!url) return { status: 'unavailable' };

    // Sent verbatim: a cursor is the server's own string and re-encoding it would be a different position.
    const params: Record<string, string> = { limit: String(limit) };
    if (cursor) params['cursor'] = cursor;

    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true, params }));
      const page = decodeBrandSourceVersionPage(body);
      return page ? { status: 'ok', page } : { status: 'unavailable' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };
      if (error.status === 400) return { status: 'cursor_expired' };
      if (error.status === 404) return { status: 'not_found' };
      return { status: 'unavailable' };
    }
  }

  /** What still points at this document, and how much it holds. Never a warning: a removal loses nothing. */
  async usage(workspaceSlug: string, documentId: string): Promise<BrandSourceUsageOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(documentId)}/usage`);
    if (!url) return { status: 'unavailable' };

    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const usage = decodeBrandSourceUsage(body);
      return usage ? { status: 'ok', usage } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  /**
   * The link a browser follows to download one version, or null while the gateway address is not known.
   *
   * A link rather than a fetch: the route streams the file with its own `Content-Disposition`, so the browser
   * saves it without this app holding up to 20 MB in memory or minting an object URL. **No storage address is
   * ever constructed** — this is the API's own route, and the bytes are only ever reached through it.
   */
  versionDownloadUrl(workspaceSlug: string, documentId: string, versionNumber: number): string | null {
    // Guarded because this one returns a URL a browser will follow unexamined. A non-integer would
    // interpolate as a path segment the route cannot match, and null is the honest answer for "there is no
    // such download" — the same answer this gives while the gateway address is unknown.
    if (!Number.isInteger(versionNumber) || versionNumber < 1) return null;

    return this.url(workspaceSlug, `/${encodeURIComponent(documentId)}/versions/${versionNumber}/content`);
  }

  /**
   * Adds a new version from a new file. Nothing is overwritten: earlier versions keep their rows, their bytes
   * and their text, and this document's number moves on by one.
   *
   * The file is accepted on exactly an upload's terms, and what it is comes from its bytes — so the refusals
   * are the upload's refusals, plus the two this command has of its own.
   */
  async replace(
    workspaceSlug: string,
    documentId: string,
    file: File,
    expectedConcurrencyToken: string,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<BrandSourceReplaceOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(documentId)}/versions`);
    if (!url) return { status: 'failed', reason: 'unavailable' };

    const form = new FormData();
    form.append('expectedConcurrencyToken', expectedConcurrencyToken);
    // Last, so a server that stops reading early has the token already.
    form.append('file', file, file.name);

    const outcome = await this.send(this.http.post(url, form, this.options(idempotencyKey)), signal);

    if (outcome.status === 'added') {
      return { status: 'replaced', document: outcome.document, replayed: outcome.replayed };
    }

    // `send` maps every code it knows to an add failure. The two conflicts are this command's own, and are
    // separated here because their remedies differ: re-read and try again, versus restore first.
    return outcome.reason === 'archived_conflict'
      ? { status: 'archived' }
      : outcome.reason === 'conflict'
        ? { status: 'conflict' }
        : { status: 'failed', reason: outcome.reason };
  }

  /** Puts a document on the shelf: it stays in the library and can be brought back, but is no longer offered. */
  archive(workspaceSlug: string, documentId: string, concurrencyToken: string): Promise<BrandSourceShelfOutcome> {
    return this.shelve(workspaceSlug, documentId, 'archive', concurrencyToken);
  }

  unarchive(workspaceSlug: string, documentId: string, concurrencyToken: string): Promise<BrandSourceShelfOutcome> {
    return this.shelve(workspaceSlug, documentId, 'unarchive', concurrencyToken);
  }

  private async shelve(
    workspaceSlug: string,
    documentId: string,
    command: 'archive' | 'unarchive',
    concurrencyToken: string,
  ): Promise<BrandSourceShelfOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(documentId)}/${command}`);
    if (!url) return { status: 'unavailable' };
    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(url, { expectedConcurrencyToken: concurrencyToken }, { withCredentials: true }),
      );
      const document = decodeBrandSourceDocumentDetail(body);
      return document ? { status: 'done', document } : { status: 'unavailable' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };
      if (error.status === 409) return { status: 'conflict' };
      if (error.status === 403) return { status: 'forbidden' };
      if (error.status === 404) return { status: 'not_found' };
      return { status: 'unavailable' };
    }
  }

  /** Aborting the signal cancels the request and answers `cancelled`; the server may still have received it. */
  upload(
    workspaceSlug: string,
    file: File,
    description: BrandSourceDescription,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<BrandSourceAddOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return Promise.resolve({ status: 'failed', reason: 'unavailable' });

    const form = new FormData();
    form.append('title', description.title);
    form.append('documentType', description.documentType);
    form.append('purpose', description.purpose);
    if (description.audience) form.append('audience', description.audience);
    // Last, so a server that stops reading early has the description already.
    form.append('file', file, file.name);

    return this.send(this.http.post(url, form, this.options(idempotencyKey)), signal);
  }

  pasteText(workspaceSlug: string, body: BrandSourcePasteBody, idempotencyKey: string, signal?: AbortSignal): Promise<BrandSourceAddOutcome> {
    const url = this.url(workspaceSlug, '/text');
    if (!url) return Promise.resolve({ status: 'failed', reason: 'unavailable' });
    return this.send(this.http.post(url, body, this.options(idempotencyKey)), signal);
  }

  private options(idempotencyKey: string): { withCredentials: true; observe: 'response'; headers: Record<string, string> } {
    return { withCredentials: true, observe: 'response', headers: { 'Idempotency-Key': idempotencyKey } };
  }

  private async send(
    request: Observable<import('@angular/common/http').HttpResponse<unknown>>,
    signal?: AbortSignal,
  ): Promise<BrandSourceAddOutcome> {
    if (signal?.aborted) return { status: 'failed', reason: 'cancelled' };
    const aborted = signal
      ? new Observable<void>((subscriber) => {
          const onAbort = (): void => subscriber.next();
          signal.addEventListener('abort', onAbort, { once: true });
          return () => signal.removeEventListener('abort', onAbort);
        })
      : null;

    try {
      const response = await firstValueFrom(aborted ? request.pipe(takeUntil(aborted)) : request, { defaultValue: null });
      if (response === null) return { status: 'failed', reason: 'cancelled' };
      const document = decodeBrandSourceDocument(response.body);
      if (!document) return { status: 'failed', reason: 'unavailable' };
      return { status: 'added', document, replayed: response.headers.get('Idempotent-Replayed') === 'true' };
    } catch (error) {
      return { status: 'failed', reason: failureOf(error) };
    }
  }
}
