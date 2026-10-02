import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { BrandSourceExtraction, decodeBrandSourceExtraction } from '../models/brand-source-extraction.models';

export type BrandSourceExtractionReadOutcome =
  | { readonly status: 'ok'; readonly extraction: BrandSourceExtraction }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** Why a correction or a retry did not happen. Each has plain-language copy in the wizard; none leaks a server code. */
export type BrandSourceExtractionFailure =
  /** The text changed since it was read. */
  | 'conflict'
  /** Reading it again could not change the answer (already read, nothing to read, or corrected). */
  | 'not_retryable'
  /** The document is on the shelf. */
  | 'archived'
  /** A newer file replaced this one. */
  | 'superseded'
  /** The first read has not finished. */
  | 'pending'
  /** The text has characters that cannot be kept, or is too long. */
  | 'invalid'
  | 'forbidden'
  | 'not_found'
  | 'key_reused'
  | 'unavailable';

export type BrandSourceExtractionWriteOutcome =
  | { readonly status: 'ok'; readonly extraction: BrandSourceExtraction }
  | { readonly status: 'failed'; readonly reason: BrandSourceExtractionFailure };

const FAILURE_BY_CODE: Readonly<Record<string, BrandSourceExtractionFailure>> = {
  'brand.source.extraction.conflict': 'conflict',
  'brand.source.extraction.not_retryable.conflict': 'not_retryable',
  'brand.source.archived.conflict': 'archived',
  'brand.source.extraction.superseded.conflict': 'superseded',
  'brand.source.extraction.pending.conflict': 'pending',
  'brand.source.invalid_request': 'invalid',
  'brand.source.forbidden': 'forbidden',
  'brand.source.not_found': 'not_found',
  'idempotency.key_reused': 'key_reused',
  'brand.source.storage.unavailable': 'unavailable',
};

function failureOf(error: unknown): BrandSourceExtractionFailure {
  if (!(error instanceof HttpErrorResponse)) return 'unavailable';
  const body = error.error as Record<string, unknown> | null;
  const code = typeof body?.['code'] === 'string' ? body['code'] : null;
  if (code && FAILURE_BY_CODE[code]) return FAILURE_BY_CODE[code];
  if (error.status === 400) return 'invalid';
  if (error.status === 403) return 'forbidden';
  if (error.status === 404) return 'not_found';
  if (error.status === 409) return 'conflict';
  return 'unavailable';
}

/**
 * The typed client for reading, correcting and re-reading the text of a brand source document version. The caller
 * owns each `Idempotency-Key` across retries of one logical action, so a retry after a dropped response returns the
 * first result instead of writing a second one. No workspace id is ever sent: the workspace is the route.
 * Components never inject `HttpClient`.
 */
@Injectable({ providedIn: 'root' })
export class BrandSourceExtractionService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private url(workspaceSlug: string, documentId: string, versionNumber: number, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/brand-source-documents/${encodeURIComponent(documentId)}` +
        `/versions/${versionNumber}/extraction${suffix}`,
    );
  }

  async get(workspaceSlug: string, documentId: string, versionNumber: number): Promise<BrandSourceExtractionReadOutcome> {
    const url = this.url(workspaceSlug, documentId, versionNumber);
    if (!url) return { status: 'unavailable' };
    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const extraction = decodeBrandSourceExtraction(body);
      return extraction ? { status: 'ok', extraction } : { status: 'unavailable' };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404 ? { status: 'not_found' } : { status: 'unavailable' };
    }
  }

  /** Saves the corrected text in full as a new artifact; the one it replaces stays as history. */
  correct(
    workspaceSlug: string,
    documentId: string,
    versionNumber: number,
    body: { readonly expectedExtractionId: string; readonly text: string; readonly reason: string },
    idempotencyKey: string,
  ): Promise<BrandSourceExtractionWriteOutcome> {
    const url = this.url(workspaceSlug, documentId, versionNumber, '/corrections');
    if (!url) return Promise.resolve({ status: 'failed', reason: 'unavailable' });
    return this.write(url, body, idempotencyKey);
  }

  /** Asks for the text to be read again. `expectedExtractionId` is null for a version nothing has finished reading. */
  retry(
    workspaceSlug: string,
    documentId: string,
    versionNumber: number,
    expectedExtractionId: string | null,
    idempotencyKey: string,
  ): Promise<BrandSourceExtractionWriteOutcome> {
    const url = this.url(workspaceSlug, documentId, versionNumber, '/retry');
    if (!url) return Promise.resolve({ status: 'failed', reason: 'unavailable' });
    return this.write(url, { expectedExtractionId }, idempotencyKey);
  }

  private async write(url: string, body: object, idempotencyKey: string): Promise<BrandSourceExtractionWriteOutcome> {
    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, body, { withCredentials: true, headers: { 'Idempotency-Key': idempotencyKey } }),
      );
      const extraction = decodeBrandSourceExtraction(response);
      return extraction ? { status: 'ok', extraction } : { status: 'failed', reason: 'unavailable' };
    } catch (error) {
      return { status: 'failed', reason: failureOf(error) };
    }
  }
}
