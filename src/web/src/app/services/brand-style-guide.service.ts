import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  BrandStyleGuideActivated,
  BrandStyleGuideApproved,
  BrandStyleGuideCitation,
  BrandStyleGuideCreated,
  BrandStyleGuideDetail,
  BrandStyleGuideEditSession,
  BrandStyleGuideSummary,
  BrandStyleGuideVersionComparison,
  BrandStyleGuideVersionPage,
  BrandStyleGuideVersionSaveRequest,
  BrandStyleGuideVersionSaved,
  CreateBrandStyleGuideRequest,
  decodeBrandStyleGuideActivated,
  decodeBrandStyleGuideApproved,
  decodeBrandStyleGuideCreated,
  decodeBrandStyleGuideDetail,
  decodeBrandStyleGuideEditSession,
  decodeBrandStyleGuideSummary,
  decodeBrandStyleGuideVersionComparison,
  decodeBrandStyleGuideVersionPage,
  decodeBrandStyleGuideVersionSaved,
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

/**
 * Which version actually holds the workspace default, as an activation conflict reports it.
 *
 * Read from the problem's own extensions so the refusal can name the version that won rather than only say
 * the request lost. Every field is null when the workspace has no default at all.
 */
export interface BrandStyleGuideActiveVersion {
  readonly guideId: string | null;
  readonly versionId: string | null;
  readonly versionNumber: number | null;
}

export type BrandStyleGuideActivateOutcome =
  | { readonly status: 'activated'; readonly activation: BrandStyleGuideActivated }
  | {
      readonly status: 'failed';
      readonly reason: BrandStyleGuideFailure;
      /** Present only on `activation_conflict`: what holds the default now. */
      readonly active?: BrandStyleGuideActiveVersion;
    };

/** One guide, as far as the history screen reads it. */
export type BrandStyleGuideReadOutcome =
  | { readonly status: 'ok'; readonly guide: BrandStyleGuideSummary }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** One guide with its working version in full: what the editor reads. */
export type BrandStyleGuideDetailOutcome =
  | { readonly status: 'ok'; readonly guide: BrandStyleGuideDetail }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * The caller's own unsaved edit of a guide. `none` is the ordinary state before they have typed anything —
 * the route answers `204`, which is not an error and must not be rendered as one.
 */
export type BrandStyleGuideEditSessionOutcome =
  | { readonly status: 'ok'; readonly session: BrandStyleGuideEditSession }
  | { readonly status: 'none' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * Keeping a draft. `conflict` is the caller's own copy having moved somewhere else — a second tab, or a save
 * that wrote a version and cleared it — so the remedy is to read the draft again, never to retry this one.
 */
export type BrandStyleGuideEditSessionSaveOutcome =
  | { readonly status: 'kept'; readonly session: BrandStyleGuideEditSession }
  | { readonly status: 'conflict' }
  | { readonly status: 'invalid' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type BrandStyleGuideEditSessionDiscardOutcome =
  | { readonly status: 'discarded' }
  | { readonly status: 'conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * Writing one further version from a creator's own edit.
 *
 * `rebase_needed` is the guide having gained a version since this edit was composed — the server refuses
 * rather than rebasing silently, and the editor re-reads and offers to apply the same change over the newer
 * version, which is safe precisely because a submitted change names only the parts it touches.
 */
export type BrandStyleGuideVersionSaveOutcome =
  | { readonly status: 'saved'; readonly result: BrandStyleGuideVersionSaved }
  | { readonly status: 'rebase_needed'; readonly workingVersionNumber: number | null }
  | { readonly status: 'unusable_sources'; readonly sources: readonly BrandStyleGuideCitation[] }
  | { readonly status: 'archived' }
  | { readonly status: 'limit' }
  | { readonly status: 'invalid' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'key_reused' }
  | { readonly status: 'unavailable' };

/**
 * One page of a guide's version history.
 *
 * `cursor_expired` is the API's `brand.guide.invalid_request` on this route: a cursor issued for another
 * workspace or another guide. Its remedy — start the history again without one — is not the remedy for being
 * unable to reach the server, so the two stay apart.
 */
export type BrandStyleGuideVersionsOutcome =
  | { readonly status: 'ok'; readonly page: BrandStyleGuideVersionPage }
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/**
 * A comparison of two versions.
 *
 * `version_gone` covers both ways the two numbers can fail to name versions — one the guide does not have, or
 * one refused as out of range — because the remedy for each is the same: read the history again and choose
 * from what it says now.
 */
export type BrandStyleGuideComparisonOutcome =
  | { readonly status: 'ok'; readonly comparison: BrandStyleGuideVersionComparison }
  | { readonly status: 'version_gone' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

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

  /**
   * One guide: its name, whether it is archived, and which of its versions holds the workspace default.
   *
   * An unknown guide and another workspace's answer identically, so this reports one `not_found` for both
   * rather than guessing which happened.
   */
  async get(workspaceSlug: string, guideId: string): Promise<BrandStyleGuideReadOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const guide = decodeBrandStyleGuideSummary(body);

      return guide ? { status: 'ok', guide } : { status: 'unavailable' };
    } catch (error) {
      return { status: failureOf(error) === 'not_found' ? 'not_found' : 'unavailable' };
    }
  }

  /**
   * One guide with its working version in full: every section, rule and citation, plus whether that version
   * is approved and whether it holds the workspace default.
   *
   * The same route as {@link get}, read further: the editor needs the content, and the history screen needs
   * none of it. One decoder reads the payload and the narrower shape is projected from it, so the two cannot
   * come to disagree about what the route sends.
   */
  async getDetail(workspaceSlug: string, guideId: string): Promise<BrandStyleGuideDetailOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
      const guide = decodeBrandStyleGuideDetail(body);

      return guide ? { status: 'ok', guide } : { status: 'unavailable' };
    } catch (error) {
      return { status: failureOf(error) === 'not_found' ? 'not_found' : 'unavailable' };
    }
  }

  /**
   * One page of a guide's version history, newest version first.
   *
   * The cursor is sent verbatim: it is the server's own string, and re-encoding it would name a different
   * position. `limit` is clamped server-side rather than refused, so an out-of-range value is not this
   * client's to police.
   */
  async listVersions(
    workspaceSlug: string,
    guideId: string,
    cursor: string | null = null,
    limit = 20,
  ): Promise<BrandStyleGuideVersionsOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}/versions`);
    if (!url) return { status: 'unavailable' };

    const params: Record<string, string> = { limit: String(limit) };
    if (cursor) params['cursor'] = cursor;

    try {
      const body = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true, params }));
      const page = decodeBrandStyleGuideVersionPage(body);

      return page ? { status: 'ok', page } : { status: 'unavailable' };
    } catch (error) {
      switch (failureOf(error)) {
        case 'not_found':
          return { status: 'not_found' };
        // The only thing this route refuses as a bad request is a cursor from another workspace or guide.
        case 'invalid':
          return { status: 'cursor_expired' };
        default:
          return { status: 'unavailable' };
      }
    }
  }

  /**
   * What differs between two of a guide's versions, by version number as the history lists them.
   *
   * **The comparison is the server's.** This returns the answer rather than the two versions, and nothing in
   * this client derives a second opinion about what changed. Either order is allowed, and `from` equal to `to`
   * is a comparison with no changes in it rather than an error.
   */
  async compareVersions(
    workspaceSlug: string,
    guideId: string,
    from: number,
    to: number,
  ): Promise<BrandStyleGuideComparisonOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}/versions/compare`);
    if (!url) return { status: 'unavailable' };

    try {
      const body = await firstValueFrom(
        this.http.get<unknown>(url, {
          withCredentials: true,
          params: { from: String(from), to: String(to) },
        }),
      );
      const comparison = decodeBrandStyleGuideVersionComparison(body);

      return comparison ? { status: 'ok', comparison } : { status: 'unavailable' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };

      const body = error.error as Record<string, unknown> | null;
      const code = typeof body?.['code'] === 'string' ? body['code'] : null;

      // A version number this guide does not have, and one the edge refused as out of range, have the same
      // remedy: read the history again. The guide itself being unreadable is a different answer.
      if (code === 'brand.guide.version.not_found' || code === 'brand.guide.invalid_request') {
        return { status: 'version_gone' };
      }

      return { status: failureOf(error) === 'not_found' ? 'not_found' : 'unavailable' };
    }
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
   * The caller's own unsaved edit of a guide, or that they have none.
   *
   * There is no way to read anybody else's: the route is scoped to the authenticated caller, so a second
   * Editor's half-written draft of the same guide is not reachable from here.
   */
  async getEditSession(workspaceSlug: string, guideId: string): Promise<BrandStyleGuideEditSessionOutcome> {
    const url = this.editSessionUrl(workspaceSlug, guideId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.get(url, { withCredentials: true, observe: 'response', responseType: 'json' }),
      );

      // 204: the ordinary state before a creator has typed anything, and not an error.
      if (response.status === 204 || response.body === null) return { status: 'none' };

      const session = decodeBrandStyleGuideEditSession(response.body);

      return session ? { status: 'ok', session } : { status: 'unavailable' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };
      if (error.status === 403) return { status: 'forbidden' };
      if (error.status === 404) return { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  /**
   * Keeps the caller's unsaved edit, creating it on first save.
   *
   * `rowVersion` is the token of the draft this save was composed against, quoted as `If-Match`; null creates
   * one, and sending null once a draft exists is refused rather than allowed to overwrite the copy another
   * tab has been writing.
   */
  async saveEditSession(
    workspaceSlug: string,
    guideId: string,
    baselineVersionNumber: number,
    draftJson: string,
    rowVersion: string | null,
  ): Promise<BrandStyleGuideEditSessionSaveOutcome> {
    const url = this.editSessionUrl(workspaceSlug, guideId);
    if (!url) return { status: 'unavailable' };

    try {
      const body = await firstValueFrom(
        this.http.put<unknown>(
          url,
          { baselineVersionNumber, draftJson },
          {
            withCredentials: true,
            // Quoted, as the HTTP specification has it; the server trims the quotes back off.
            headers: rowVersion ? { 'If-Match': `"${rowVersion}"` } : {},
          },
        ),
      );
      const session = decodeBrandStyleGuideEditSession(body);

      return session ? { status: 'kept', session } : { status: 'unavailable' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };

      if (error.status === 409) return { status: 'conflict' };
      if (error.status === 403) return { status: 'forbidden' };
      if (error.status === 404) return { status: 'not_found' };
      if (error.status === 400) return { status: 'invalid' };

      return { status: 'unavailable' };
    }
  }

  /** Discards the caller's own unsaved edit. Discarding what is not there is a success. */
  async discardEditSession(
    workspaceSlug: string,
    guideId: string,
  ): Promise<BrandStyleGuideEditSessionDiscardOutcome> {
    const url = this.editSessionUrl(workspaceSlug, guideId);
    if (!url) return { status: 'unavailable' };

    try {
      await firstValueFrom(this.http.delete<unknown>(url, { withCredentials: true }));

      return { status: 'discarded' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };

      if (error.status === 409) return { status: 'conflict' };
      if (error.status === 403) return { status: 'forbidden' };
      if (error.status === 404) return { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  /**
   * Writes one further version from a creator's own edit. Editor or above.
   *
   * The request is a submitted change, not a replacement guide: an omitted collection is untouched. The
   * caller owns the idempotency key across retries of one attempt, so a retry after a dropped response
   * returns the version the first request wrote instead of writing a second.
   */
  async saveVersion(
    workspaceSlug: string,
    guideId: string,
    request: BrandStyleGuideVersionSaveRequest,
    idempotencyKey: string,
  ): Promise<BrandStyleGuideVersionSaveOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}/versions`);
    if (!url) return { status: 'unavailable' };

    try {
      const body = await firstValueFrom(this.http.post<unknown>(url, request, this.options(idempotencyKey)));
      const result = decodeBrandStyleGuideVersionSaved(body);

      return result ? { status: 'saved', result } : { status: 'unavailable' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable' };

      const problem = error.error as Record<string, unknown> | null;
      const code = typeof problem?.['code'] === 'string' ? problem['code'] : null;

      switch (code) {
        case 'brand.guide.workingVersion.conflict':
          return {
            status: 'rebase_needed',
            workingVersionNumber:
              typeof problem?.['workingVersionNumber'] === 'number' ? problem['workingVersionNumber'] : null,
          };
        case 'brand.guide.source.unprocessable':
          return { status: 'unusable_sources', sources: unusableSourcesFrom(problem) };
        case 'brand.guide.archived.conflict':
          return { status: 'archived' };
        case 'brand.guide.version.limit.invalid_request':
          return { status: 'limit' };
        case 'brand.guide.not_found':
          return { status: 'not_found' };
        case 'brand.guide.forbidden':
          return { status: 'forbidden' };
        case 'idempotency.key_reused':
          return { status: 'key_reused' };
        case 'brand.guide.invalid_request':
          return { status: 'invalid' };
        default:
          return { status: error.status === 403 ? 'forbidden' : 'unavailable' };
      }
    }
  }

  private editSessionUrl(workspaceSlug: string, guideId: string): string | null {
    return this.url(workspaceSlug, `/${encodeURIComponent(guideId)}/edit-session`);
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
    reason: string | null = null,
  ): Promise<BrandStyleGuideActivateOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(guideId)}/versions/${versionNumber}/activation`);
    if (!url) return { status: 'failed', reason: 'unavailable' };

    try {
      const body = await firstValueFrom(
        this.http.post<unknown>(
          url,
          {
            confirmed: true,
            ...(expectedActiveVersionId ? { expectedActiveVersionId } : {}),
            // Omitted rather than sent empty: the server stores it as the creator's own words, and a blank
            // string is not words. Last, so the wizard's existing calls keep their positions.
            ...(reason ? { reason } : {}),
          },
          this.options(idempotencyKey),
        ),
      );
      const activation = decodeBrandStyleGuideActivated(body);
      return activation ? { status: 'activated', activation } : { status: 'failed', reason: 'unavailable' };
    } catch (error) {
      const reason = failureOf(error);

      // The one refusal that names the state the caller got wrong, so the screen can say which version holds
      // the default now instead of only that this one does not.
      return reason === 'activation_conflict'
        ? { status: 'failed', reason, active: activeFrom(error) }
        : { status: 'failed', reason };
    }
  }
}

/**
 * The citations a save said it could not use, from the problem's own extension.
 *
 * Named by document and version rather than by request index, because the one at fault can be a citation the
 * version already held — a document removed since it was written — which no index into this request names.
 */
function unusableSourcesFrom(problem: Record<string, unknown> | null): readonly BrandStyleGuideCitation[] {
  const listed = problem?.['unusableSources'];
  if (!Array.isArray(listed)) return [];

  const sources: BrandStyleGuideCitation[] = [];

  for (const item of listed) {
    if (
      typeof item === 'object' &&
      item !== null &&
      typeof (item as Record<string, unknown>)['documentId'] === 'string' &&
      typeof (item as Record<string, unknown>)['versionNumber'] === 'number'
    ) {
      sources.push({
        documentId: (item as Record<string, unknown>)['documentId'] as string,
        versionNumber: (item as Record<string, unknown>)['versionNumber'] as number,
      });
    }
  }

  return sources;
}

/** What an activation conflict says actually holds the default. Every field null when nothing does. */
function activeFrom(error: unknown): BrandStyleGuideActiveVersion {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;

  return {
    guideId: typeof body?.['activeGuideId'] === 'string' ? body['activeGuideId'] : null,
    versionId: typeof body?.['activeVersionId'] === 'string' ? body['activeVersionId'] : null,
    versionNumber: typeof body?.['activeVersionNumber'] === 'number' ? body['activeVersionNumber'] : null,
  };
}
