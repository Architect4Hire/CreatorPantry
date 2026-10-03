import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  BrandStyleGuideActivated,
  BrandStyleGuideApproved,
  BrandStyleGuideCreated,
  CreateBrandStyleGuideRequest,
  decodeBrandStyleGuideActivated,
  decodeBrandStyleGuideApproved,
  decodeBrandStyleGuideCreated,
} from '../models/brand-style-guide.models';

/**
 * Why a guide write did not happen. Each has plain-language copy where it is shown; none leaks a server code.
 */
export type BrandStyleGuideFailure =
  /** The request was refused on its shape: a blank name, a section the server will not take. */
  | 'invalid'
  | 'forbidden'
  | 'not_found'
  /** The version is not approved, so it cannot be the workspace default. */
  | 'unapproved'
  /** The version cites a source document that has been replaced since it was written. */
  | 'stale'
  /** The version has no sections and no rules. */
  | 'empty'
  /** The guide is archived. */
  | 'archived'
  /** The workspace's active version is not the one this request expected. */
  | 'activation_conflict'
  /** Two requests approved the same version at once; asking again reports the approval that was recorded. */
  | 'approval_conflict'
  | 'key_reused'
  | 'unavailable';

export type BrandStyleGuideCreateOutcome =
  | { readonly status: 'created'; readonly guide: BrandStyleGuideCreated }
  | { readonly status: 'failed'; readonly reason: BrandStyleGuideFailure };

export type BrandStyleGuideApproveOutcome =
  | { readonly status: 'approved'; readonly approval: BrandStyleGuideApproved }
  | { readonly status: 'failed'; readonly reason: BrandStyleGuideFailure };

export type BrandStyleGuideActivateOutcome =
  | { readonly status: 'activated'; readonly activation: BrandStyleGuideActivated }
  | { readonly status: 'failed'; readonly reason: BrandStyleGuideFailure };

const FAILURE_BY_CODE: Readonly<Record<string, BrandStyleGuideFailure>> = {
  'brand.guide.invalid_request': 'invalid',
  'brand.guide.forbidden': 'forbidden',
  'brand.guide.not_found': 'not_found',
  'brand.guide.version.not_found': 'not_found',
  'brand.guide.version.unapproved.conflict': 'unapproved',
  'brand.guide.version.stale.conflict': 'stale',
  'brand.guide.version.empty.conflict': 'empty',
  'brand.guide.archived.conflict': 'archived',
  'brand.guide.activation.conflict': 'activation_conflict',
  'brand.guide.version.approval.conflict': 'approval_conflict',
  'brand.guide.source.unprocessable': 'invalid',
  'idempotency.key_reused': 'key_reused',
};

function failureOf(error: unknown): BrandStyleGuideFailure {
  if (!(error instanceof HttpErrorResponse)) return 'unavailable';

  const body = error.error as Record<string, unknown> | null;
  const code = typeof body?.['code'] === 'string' ? body['code'] : null;
  if (code && FAILURE_BY_CODE[code]) return FAILURE_BY_CODE[code];

  if (error.status === 403) return 'forbidden';
  if (error.status === 404) return 'not_found';
  if (error.status === 400) return 'invalid';

  return 'unavailable';
}

/**
 * The typed client for a workspace's brand style guides, as far as finishing the setup wizard needs them:
 * write the guide, approve its version, make it the workspace default.
 *
 * **Three writes, three decisions.** They are separate routes because they are separate decisions with
 * separate roles — an Editor writes and approves, an Owner activates — and this client keeps them separate
 * rather than offering one method that hides which of them failed.
 *
 * **The caller owns the idempotency key** across retries of one logical attempt, so a retry after a dropped
 * response returns the first result instead of writing a second guide. No workspace id is ever sent: the
 * workspace is the route. Components never inject `HttpClient`.
 */
@Injectable({ providedIn: 'root' })
export class BrandStyleGuideService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/brand-style-guides${suffix}`);
  }

  private options(idempotencyKey: string): { withCredentials: true; headers: Record<string, string> } {
    return { withCredentials: true, headers: { 'Idempotency-Key': idempotencyKey } };
  }

  /** Writes the guide and its version 1. Not approved and not the workspace default. Editor or above. */
  async create(
    workspaceSlug: string,
    request: CreateBrandStyleGuideRequest,
    idempotencyKey: string,
  ): Promise<BrandStyleGuideCreateOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'failed', reason: 'unavailable' };

    try {
      const body = await firstValueFrom(this.http.post<unknown>(url, request, this.options(idempotencyKey)));
      const guide = decodeBrandStyleGuideCreated(body);
      return guide ? { status: 'created', guide } : { status: 'failed', reason: 'unavailable' };
    } catch (error) {
      return { status: 'failed', reason: failureOf(error) };
    }
  }

  /** Marks one version finished, which is what lets an Owner activate it. Editor or above. */
  async approve(
    workspaceSlug: string,
    guideId: string,
    versionNumber: number,
    reason: string | null,
    idempotencyKey: string,
  ): Promise<BrandStyleGuideApproveOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}/versions/${versionNumber}/approval`);
    if (!url) return { status: 'failed', reason: 'unavailable' };

    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(url, { confirmed: true, ...(reason ? { reason } : {}) }, this.options(idempotencyKey)),
      );
      const approval = decodeBrandStyleGuideApproved(body);
      return approval ? { status: 'approved', approval } : { status: 'failed', reason: 'unavailable' };
    } catch (error) {
      return { status: 'failed', reason: failureOf(error) };
    }
  }

  /**
   * Makes one approved version the workspace's default. **Owner only.**
   *
   * `expectedActiveVersionId` is a claim, not a waiver: null asserts the workspace has no default, and the
   * server refuses rather than assuming, so a caller cannot replace a default they never saw.
   */
  async activate(
    workspaceSlug: string,
    guideId: string,
    versionNumber: number,
    expectedActiveVersionId: string | null,
    idempotencyKey: string,
  ): Promise<BrandStyleGuideActivateOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}/versions/${versionNumber}/activation`);
    if (!url) return { status: 'failed', reason: 'unavailable' };

    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(
          url,
          { confirmed: true, ...(expectedActiveVersionId ? { expectedActiveVersionId } : {}) },
          this.options(idempotencyKey),
        ),
      );
      const activation = decodeBrandStyleGuideActivated(body);
      return activation ? { status: 'activated', activation } : { status: 'failed', reason: 'unavailable' };
    } catch (error) {
      return { status: 'failed', reason: failureOf(error) };
    }
  }
}
