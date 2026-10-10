import { HttpClient, HttpEventType } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, filter, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  DamAssetDetail,
  DamAssetRemoval,
  DamAssetSearchPage,
  DamAssetSearchQuery,
  DamAssetUtilization,
  DamAssetUtilizationPage,
  DamAssetVersion,
  DamCreatedAsset,
  decodeDamAssetDetail,
  decodeDamAssetRemoval,
  decodeDamAssetSearchPage,
  decodeDamAssetUtilization,
  decodeDamAssetUtilizationPage,
  decodeDamAssetVersion,
  decodeDamCreatedAsset,
  encodeDamAssetSearchQuery,
} from '../models/dam-asset.models';
import { PictureRendition, renditionQuery } from '../models/picture-rendition.models';
import { decodeFieldErrors, problemCodeOf, problemMessageOf, statusCodeOf } from './ai-request';

export type DamAssetSearchOutcome =
  | { readonly status: 'found'; readonly page: DamAssetSearchPage }
  /** The cursor was issued for a different workspace, ordering or set of filters. Start the list again. */
  | { readonly status: 'cursor_expired' }
  /** A filter the server could not read. Clearing the filters is the remedy. */
  | { readonly status: 'invalid_request' }
  | { readonly status: 'unavailable' };

/** One asset's current picture, or the one reason it did not arrive. */
export type DamAssetContentOutcome =
  | { readonly status: 'found'; readonly bytes: Blob }
  /** Unknown, another workspace's, removed, or with no current version. One answer for all four. */
  | { readonly status: 'gone' }
  /** The asset is there and its bytes could not be read. Retrying is the remedy, which the route states. */
  | { readonly status: 'unavailable' };

export type DamAssetDetailOutcome =
  | { readonly status: 'found'; readonly asset: DamAssetDetail }
  /** Unknown, another workspace's, or removed. One answer for all three, as the route gives. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type DamAssetUtilizationOutcome =
  | { readonly status: 'found'; readonly page: DamAssetUtilizationPage }
  /** The cursor was issued for a different workspace or asset. Start the history again. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type DamAssetPatchOutcome =
  /** The whole asset as it now stands, carrying the token the next edit must quote. */
  | { readonly status: 'updated'; readonly asset: DamAssetDetail }
  /** Someone else changed the asset since it was read. Nothing was saved; re-read before trying again. */
  | { readonly status: 'stale' }
  /** The edit was the wrong shape, in the server's own words, by request field. */
  | {
      readonly status: 'invalid';
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  | { readonly status: 'forbidden' }
  /** Unknown, another workspace's, or removed since the page was read. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type DamUseLogOutcome =
  | { readonly status: 'logged'; readonly use: DamAssetUtilization }
  | {
      readonly status: 'invalid';
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  | { readonly status: 'forbidden' }
  /** Unknown, another workspace's, or removed. A use cannot be logged against any of them. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type DamAssetRemovalOutcome =
  /** Removed now, or found already removed — `removal.alreadyDeleted` says which. */
  | { readonly status: 'removed'; readonly removal: DamAssetRemoval }
  /** The asset changed since it was read. Nothing was removed; re-read and ask again. */
  | { readonly status: 'stale' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** Why a new version was not added. Each has its own remedy, so each is its own case. */
export type DamVersionUploadFailure =
  /** The bytes are not an image the library takes, whatever the file is called. */
  | 'unsupported'
  | 'too_large'
  /** The request itself was refused — no file, or one the server would not read. */
  | 'invalid'
  | 'forbidden'
  | 'not_found'
  /** Someone added a version at the same moment and took the number. Trying again reads the next one. */
  | 'version_taken'
  /** Storage could not be reached, or nothing could be committed. Nothing partial was left behind. */
  | 'unavailable';

export type DamVersionUploadOutcome =
  | { readonly status: 'added'; readonly version: DamAssetVersion }
  | { readonly status: 'failed'; readonly reason: DamVersionUploadFailure };

export type DamVersionUploadEvent =
  /** How much of the file has been sent, or null when the browser cannot tell. */
  | { readonly kind: 'progress'; readonly percent: number | null }
  | { readonly kind: 'done'; readonly outcome: DamVersionUploadOutcome };

/** Why an uploaded picture was not added to the library. Each has its own remedy. */
export type DamAssetUploadFailure =
  /** The bytes are not an image the library takes, whatever the file is called. */
  | 'unsupported'
  | 'too_large'
  /** The request itself was refused — a missing title, say. `fieldErrors` says which field. */
  | 'invalid'
  | 'forbidden'
  /** Storage could not be reached, or nothing could be committed. Nothing partial was left behind. */
  | 'unavailable';

export type DamAssetUploadOutcome =
  | { readonly status: 'created'; readonly asset: DamCreatedAsset }
  | {
      readonly status: 'failed';
      readonly reason: DamAssetUploadFailure;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    };

export type DamAssetUploadEvent =
  /** How much of the file has been sent, or null when the browser cannot tell. */
  | { readonly kind: 'progress'; readonly percent: number | null }
  | { readonly kind: 'done'; readonly outcome: DamAssetUploadOutcome };

/** What a creator says about a picture as they upload it. The rest is edited on the asset's own page. */
export interface DamAssetUploadDetails {
  readonly title: string;
  /** Empty when the creator gave none. Never invented here: nothing has looked at the pixels. */
  readonly altText: string;
}

export type DamAssetKeepOutcome =
  /**
   * The picture is in the library as this asset — made by this call, or by an earlier one. The route answers
   * both the same way, and a repeat applies nothing this request said; `isDamKeepRepeat` tells them apart.
   */
  | { readonly status: 'saved'; readonly asset: DamCreatedAsset }
  /** The title, alt text, tags or recipe were refused, in the server's own words, by request field. */
  | {
      readonly status: 'invalid';
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  /** The prompt was refused, and it took the asset with it: the two commit together or not at all. */
  | { readonly status: 'prompt_refused'; readonly message: string }
  /** This key was already spent on a different request. A new key is the remedy, not waiting. */
  | { readonly status: 'key_reused' }
  | { readonly status: 'forbidden' }
  /** Unknown, another workspace's, declined, or expired. One answer for all four; nothing can be saved. */
  | { readonly status: 'image_gone' }
  /** Storage could not be reached, or nothing could be committed. Nothing was saved; trying again is safe. */
  | { readonly status: 'unavailable' };

/** `IdempotencyPolicy.KeyReusedCode`. */
const KEY_REUSED_CODE = 'idempotency.key_reused';

/** Every `ContentErrorCodes.Prompt*` code: the prompt module's own refusals, whatever status they arrive as. */
const PROMPT_CODE_PREFIX = 'content.prompt.';

/** `MediaErrorCodes.AssetStaleToken`. */
const STALE_TOKEN_CODE = 'media.asset.stale.conflict';

/** `MediaErrorCodes.AssetVersionTaken`. */
const VERSION_TAKEN_CODE = 'media.asset.version_taken.conflict';

function uploadFailureOf(error: unknown): DamVersionUploadFailure {
  const code = statusCodeOf(error);

  if (code === 413) return 'too_large';
  if (code === 422) return 'unsupported';
  if (code === 400) return 'invalid';
  if (code === 403) return 'forbidden';
  if (code === 404) return 'not_found';
  if (code === 409 && problemCodeOf(error) === VERSION_TAKEN_CODE) return 'version_taken';

  return 'unavailable';
}

/** `MediaErrorCodes` — a stale cursor, whose remedy is to start the list again. */
const CURSOR_INVALID_CODE = 'media.asset_search.cursor_invalid_request';

/**
 * The typed client for the workspace's asset library: a page of it, one asset in full, its usage history, its
 * picture and the links that download it — the four changes an asset page offers: editing its metadata,
 * adding a version, logging a use, and removing it from the library (DAM-002 through DAM-010) — and keeping a
 * generated picture as a new asset (AF.4.1).
 *
 * **It also adds a creator's own picture as a new asset** (`upload`), the counterpart to keeping a generated one.
 *
 * **An asset is named by its id and never by a location.** Every URL built here is the gateway's own route
 * with a workspace slug and a guid in it; the response carries no object key or storage address to pass on.
 *
 * **The picture is fetched, not linked.** `ClientOptions.SpaContentSecurityPolicy` allows images from
 * `'self' data: blob:` only, so an `<img>` pointed at the gateway is blocked outright; `connect-src` does allow
 * the gateway, so the bytes arrive as a `Blob` over a credentialed request and the caller shows them through
 * an object URL it owns and revokes. Nothing is cached here: the bytes are private creator content.
 */
@Injectable({ providedIn: 'root' })
export class DamAssetService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** One page of the library. Follow `nextCursor` until it is null. Any member may read. */
  search(workspaceSlug: string, query: DamAssetSearchQuery): Observable<DamAssetSearchOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return of<DamAssetSearchOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true, params: encodeDamAssetSearchQuery(query) }).pipe(
      map((raw): DamAssetSearchOutcome => {
        const page = decodeDamAssetSearchPage(raw);

        return page ? { status: 'found', page } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        if (statusCodeOf(error) === 400) {
          // Two different 400s with two different remedies: a stale cursor means start again from the first
          // page, and a filter the server could not read means clear the filters.
          return of<DamAssetSearchOutcome>(
            problemCodeOf(error) === CURSOR_INVALID_CODE ? { status: 'cursor_expired' } : { status: 'invalid_request' },
          );
        }

        return of<DamAssetSearchOutcome>({ status: 'unavailable' });
      }),
    );
  }

  /**
   * The asset's current version, for showing on screen.
   *
   * The authorized render route, proxied by the server — never a storage address. The caller owns the object
   * URL it makes from these bytes and owns revoking it.
   */
  content(workspaceSlug: string, assetId: string, rendition?: PictureRendition): Observable<DamAssetContentOutcome> {
    // No rendition is the route's own default, the web-size copy when the version has one. A grid or a picker
    // asks for `thumbnail`.
    const url = this.url(workspaceSlug, `/${encodeURIComponent(assetId)}/content${renditionQuery(rendition)}`);
    if (!url) return of<DamAssetContentOutcome>({ status: 'unavailable' });

    return this.http.get(url, { withCredentials: true, responseType: 'blob' }).pipe(
      map((bytes): DamAssetContentOutcome => ({ status: 'found', bytes })),
      catchError((error: unknown) =>
        of<DamAssetContentOutcome>(statusCodeOf(error) === 404 ? { status: 'gone' } : { status: 'unavailable' }),
      ),
    );
  }

  /**
   * One numbered version of the asset, for showing on screen — what a recipe link held at an older version
   * previews.
   *
   * There is no render route for a numbered version, so this reads the same bytes the version's download
   * link serves. Nothing is saved to the creator's machine: the caller makes an object URL from the bytes and
   * owns revoking it, exactly as for {@link content}.
   */
  versionContent(
    workspaceSlug: string,
    assetId: string,
    versionNumber: number,
    rendition?: PictureRendition,
  ): Observable<DamAssetContentOutcome> {
    // The download route's own default is the original, so a smaller copy has to be asked for by name here.
    const url = this.versionDownloadUrl(workspaceSlug, assetId, versionNumber, rendition);
    if (!url) return of<DamAssetContentOutcome>({ status: 'unavailable' });

    return this.http.get(url, { withCredentials: true, responseType: 'blob' }).pipe(
      map((bytes): DamAssetContentOutcome => ({ status: 'found', bytes })),
      catchError((error: unknown) =>
        of<DamAssetContentOutcome>(statusCodeOf(error) === 404 ? { status: 'gone' } : { status: 'unavailable' }),
      ),
    );
  }

  /**
   * One asset in full: what the creator said about it, its versions and its lineage. Any member may read.
   *
   * A removed asset is not asked for — `includeDeleted` is never sent — so it answers `not_found` like an
   * unknown one, which is what a browse screen should say about it.
   */
  detail(workspaceSlug: string, assetId: string): Observable<DamAssetDetailOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(assetId)}`);
    if (!url) return of<DamAssetDetailOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): DamAssetDetailOutcome => {
        const asset = decodeDamAssetDetail(raw);

        return asset ? { status: 'found', asset } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<DamAssetDetailOutcome>(statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' }),
      ),
    );
  }

  /** One page of an asset's usage history, newest first. Follow `nextCursor` until it is null. */
  utilization(workspaceSlug: string, assetId: string, cursor: string | null): Observable<DamAssetUtilizationOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(assetId)}/utilization`);
    if (!url) return of<DamAssetUtilizationOutcome>({ status: 'unavailable' });

    // The total cannot change as a cursor is followed, so it is asked for once, with the first page.
    const params: Record<string, string> = cursor === null ? {} : { cursor, includeTotal: 'false' };

    return this.http.get<unknown>(url, { withCredentials: true, params }).pipe(
      map((raw): DamAssetUtilizationOutcome => {
        const page = decodeDamAssetUtilizationPage(raw);

        return page ? { status: 'found', page } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const code = statusCodeOf(error);
        if (code === 404) return of<DamAssetUtilizationOutcome>({ status: 'not_found' });
        if (code === 400 && problemCodeOf(error) === CURSOR_INVALID_CODE) {
          return of<DamAssetUtilizationOutcome>({ status: 'cursor_expired' });
        }

        return of<DamAssetUtilizationOutcome>({ status: 'unavailable' });
      }),
    );
  }

  /**
   * Change part of an asset's metadata (DAM-004). Contributor and above.
   *
   * `body` is the merge patch `encodeDamAssetPatch` builds: the concurrency token and only the fields that
   * changed. The key is the caller's, held for one logical edit, so a save whose response was lost replays
   * instead of being answered as a conflict with itself.
   */
  async patchMetadata(
    workspaceSlug: string,
    assetId: string,
    body: Record<string, unknown>,
    idempotencyKey: string,
  ): Promise<DamAssetPatchOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(assetId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.patch<unknown>(url, body, {
          withCredentials: true,
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const asset = decodeDamAssetDetail(raw);

      return asset ? { status: 'updated', asset } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);

      if (code === 409) {
        // Only the stale-token conflict means "someone else changed it"; any other 409 is not something
        // re-reading would fix, so it is not dressed up as one.
        return problemCodeOf(error) === STALE_TOKEN_CODE ? { status: 'stale' } : { status: 'unavailable' };
      }
      if (code === 400 || code === 422) {
        return {
          status: 'invalid',
          message: problemMessageOf(error) ?? 'Those changes could not be saved.',
          fieldErrors: decodeFieldErrors(error),
        };
      }
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  /**
   * Record that the asset was used (DAM-009). Contributor and above.
   *
   * The key is the caller's, held for one submission. It matters more here than anywhere: without one, two
   * identical calls record two uses — deliberately, because an asset really can go out twice in a day — so a
   * retry after a lost response would otherwise log a use that happened once as two.
   *
   * A removed asset answers `not_found`, exactly as an unknown one does: there is nothing to log a use against.
   */
  async logUtilization(
    workspaceSlug: string,
    assetId: string,
    body: Record<string, unknown>,
    idempotencyKey: string,
  ): Promise<DamUseLogOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(assetId)}/utilization`);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, body, {
          withCredentials: true,
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const use = decodeDamAssetUtilization(raw);

      return use ? { status: 'logged', use } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);

      if (code === 400 || code === 422) {
        return {
          status: 'invalid',
          message: problemMessageOf(error) ?? 'That use could not be logged.',
          fieldErrors: decodeFieldErrors(error),
        };
      }
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  /**
   * Remove the asset from the library (DAM-005). Editor and above.
   *
   * **Removing is not erasing.** The route tombstones the asset: no bytes are deleted, every version and usage
   * record stays, and every link is left standing. The answer says what is now pointing at it.
   *
   * `confirmed: true` is sent because the route requires a person to have decided — this is only ever called
   * after the creator has answered the confirmation. The token is the one from the read they were shown, so a
   * removal composed against an asset that has since changed is refused rather than carried out.
   */
  async remove(workspaceSlug: string, assetId: string, expectedConcurrencyToken: string): Promise<DamAssetRemovalOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(assetId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.delete<unknown>(url, {
          withCredentials: true,
          body: { confirmed: true, expectedConcurrencyToken },
        }),
      );
      const removal = decodeDamAssetRemoval(raw);

      return removal ? { status: 'removed', removal } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);

      if (code === 409) return { status: 'stale' };
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };

      return { status: 'unavailable' };
    }
  }

  /**
   * Keep a staged generated picture as a library asset (AF.4.1). Contributor and above.
   *
   * `body` is what `encodeDamKeep` builds: the picture's id, what the creator said about it, an optional recipe
   * link, and optionally the prompt that made it — which is saved in the same transaction as the asset.
   *
   * **A picture is kept exactly once, with or without the key.** A repeat answers with the asset the first call
   * made and applies nothing from the new request. The key is still sent: it is what makes a retry after a
   * lost response return the first answer whole, prompt record and all.
   */
  async keepGeneratedImage(
    workspaceSlug: string,
    body: Record<string, unknown>,
    idempotencyKey: string,
  ): Promise<DamAssetKeepOutcome> {
    const url = this.url(workspaceSlug, '/from-generated-image');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, body, {
          withCredentials: true,
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const asset = decodeDamCreatedAsset(raw);

      return asset ? { status: 'saved', asset } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      const problem = problemCodeOf(error);

      // Before the statuses: the prompt's refusals arrive as the same 400, 403, 404 and 409 the asset's do, and
      // only the code says the asset itself was fine.
      if (problem?.startsWith(PROMPT_CODE_PREFIX)) {
        return { status: 'prompt_refused', message: problemMessageOf(error) ?? 'The prompt could not be saved.' };
      }
      if (problem === KEY_REUSED_CODE) return { status: 'key_reused' };

      if (code === 400 || code === 422) {
        return {
          status: 'invalid',
          message: problemMessageOf(error) ?? 'That picture could not be saved.',
          fieldErrors: decodeFieldErrors(error),
        };
      }
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'image_gone' };

      // 409 here is a commit that failed with nothing written, and 503 is storage: both are worth trying again.
      return { status: 'unavailable' };
    }
  }

  /**
   * Add a creator's own picture to the library as a new asset. Contributor and above.
   *
   * The same kind of asset keeping a generated picture makes, from bytes the creator supplies instead. The
   * server decides what the file is from its bytes; the name and type a browser reports are not sent as facts.
   *
   * A stream, for the reason `addVersion` is one: `progress` events say how much has left this browser, the
   * last event is the `done` one, and **unsubscribing aborts the request** without being able to promise the
   * server had not already committed.
   *
   * **The key is worth sending.** Nothing else stops a retry after a lost response from adding the same
   * picture twice, because uploading one photograph as two assets is a legitimate thing to do.
   */
  upload(
    workspaceSlug: string,
    file: File,
    details: DamAssetUploadDetails,
    idempotencyKey: string,
  ): Observable<DamAssetUploadEvent> {
    const failed = (reason: DamAssetUploadFailure, fieldErrors: Readonly<Record<string, readonly string[]>> = {}) =>
      of<DamAssetUploadEvent>({ kind: 'done', outcome: { status: 'failed', reason, fieldErrors } });

    const url = this.url(workspaceSlug);
    if (!url) return failed('unavailable');

    const form = new FormData();
    form.append('file', file, file.name);
    form.append('title', details.title);
    if (details.altText !== '') form.append('altText', details.altText);

    return this.http
      .post<unknown>(url, form, {
        withCredentials: true,
        headers: { 'Idempotency-Key': idempotencyKey },
        reportProgress: true,
        observe: 'events',
      })
      .pipe(
        map((event): DamAssetUploadEvent | null => {
          if (event.type === HttpEventType.UploadProgress) {
            // No total means the browser cannot say how far along it is; reported as unknown rather than 0%.
            const percent = event.total ? Math.min(100, Math.round((event.loaded / event.total) * 100)) : null;

            return { kind: 'progress', percent };
          }
          if (event.type !== HttpEventType.Response) return null;

          const asset = decodeDamCreatedAsset(event.body);

          return {
            kind: 'done',
            outcome: asset ? { status: 'created', asset } : { status: 'failed', reason: 'unavailable', fieldErrors: {} },
          };
        }),
        filter((event): event is DamAssetUploadEvent => event !== null),
        catchError((error: unknown) => {
          const code = statusCodeOf(error);

          if (code === 413) return failed('too_large');
          if (code === 422) return failed('unsupported');
          if (code === 400) return failed('invalid', decodeFieldErrors(error));
          if (code === 403) return failed('forbidden');

          return failed('unavailable');
        }),
      );
  }

  /**
   * Add a new version of the asset from a file (DAM-010). Contributor and above.
   *
   * **Bytes only, and nothing is overwritten**: the route takes one field, the file, and stores it as the next
   * numbered version beside every earlier one.
   *
   * A stream rather than a promise, because an upload has something to say before it ends: each `progress`
   * event is how much of the file has left this browser, and the last event is the `done` one. **Unsubscribing
   * aborts the request** — which stops the transfer but cannot promise the server had not already committed,
   * so a caller that cancels should re-read the asset rather than assume nothing was added.
   */
  addVersion(
    workspaceSlug: string,
    assetId: string,
    file: File,
    idempotencyKey: string,
  ): Observable<DamVersionUploadEvent> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(assetId)}/versions`);
    if (!url) return of<DamVersionUploadEvent>({ kind: 'done', outcome: { status: 'failed', reason: 'unavailable' } });

    const form = new FormData();
    form.append('file', file, file.name);

    return this.http
      .post<unknown>(url, form, {
        withCredentials: true,
        headers: { 'Idempotency-Key': idempotencyKey },
        reportProgress: true,
        observe: 'events',
      })
      .pipe(
        map((event): DamVersionUploadEvent | null => {
          if (event.type === HttpEventType.UploadProgress) {
            // No total means the browser cannot say how far along it is; reported as unknown rather than 0%.
            const percent = event.total ? Math.min(100, Math.round((event.loaded / event.total) * 100)) : null;

            return { kind: 'progress', percent };
          }
          if (event.type !== HttpEventType.Response) return null;

          const version = decodeDamAssetVersion(event.body);

          return {
            kind: 'done',
            outcome: version ? { status: 'added', version } : { status: 'failed', reason: 'unavailable' },
          };
        }),
        filter((event): event is DamVersionUploadEvent => event !== null),
        catchError((error: unknown) =>
          of<DamVersionUploadEvent>({ kind: 'done', outcome: { status: 'failed', reason: uploadFailureOf(error) } }),
        ),
      );
  }

  /**
   * The link a browser follows to download the current version, or null while the gateway address is not known.
   *
   * Only the link. The server names the file from the title, the version and the stored media type, so a name
   * built here would be a second guess at it.
   */
  downloadUrl(workspaceSlug: string, assetId: string, rendition?: PictureRendition): string | null {
    // With no rendition a download is the original, as it has always been; `web` asks for the smaller copy.
    return this.url(workspaceSlug, `/${encodeURIComponent(assetId)}/download${renditionQuery(rendition)}`);
  }

  /** The link that downloads one numbered version of this asset. The number must be one the detail read listed. */
  versionDownloadUrl(
    workspaceSlug: string,
    assetId: string,
    versionNumber: number,
    rendition?: PictureRendition,
  ): string | null {
    return this.url(
      workspaceSlug,
      `/${encodeURIComponent(assetId)}/versions/${encodeURIComponent(String(versionNumber))}/download${renditionQuery(rendition)}`,
    );
  }

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/dam-assets${suffix}`);
  }
}
