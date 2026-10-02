import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, firstValueFrom, takeUntil } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  BrandSourceDescription,
  BrandSourceDocumentDetail,
  BrandSourceDocumentPage,
  BrandSourceDocumentSummary,
  BrandSourcePasteBody,
  decodeBrandSourceDocument,
  decodeBrandSourceDocumentDetail,
  decodeBrandSourceDocumentPage,
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
  | 'unavailable';

export type BrandSourceAddOutcome =
  | { readonly status: 'added'; readonly document: BrandSourceDocumentSummary; readonly replayed: boolean }
  | { readonly status: 'failed'; readonly reason: BrandSourceAddFailure };

export type BrandSourceListOutcome =
  | { readonly status: 'ok'; readonly page: BrandSourceDocumentPage }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

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
  // Both mean "nothing was recorded, try again": the scan or the private store could not be reached.
  'brand.source.scan.unavailable': 'unavailable',
  'brand.source.storage.unavailable': 'unavailable',
};

function failureOf(error: unknown): BrandSourceAddFailure {
  if (!(error instanceof HttpErrorResponse)) return 'unavailable';
  const body = error.error as Record<string, unknown> | null;
  const code = typeof body?.['code'] === 'string' ? body['code'] : null;
  if (code && FAILURE_BY_CODE[code]) return FAILURE_BY_CODE[code];
  if (error.status === 404) return 'not_found';
  if (error.status === 403) return 'forbidden';
  if (error.status === 413) return 'too_large';
  if (error.status === 400) return 'invalid';
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
