import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  GeneratedImageOperation,
  GeneratedImageOperationDetail,
  IDEMPOTENCY_KEY_REQUIRED_CODE,
  RequestGeneratedImagesRequest,
  decodeGeneratedImageOperation,
  decodeGeneratedImageOperationDetail,
  encodeRequestGeneratedImages,
} from '../models/generated-image.models';
import { PictureRendition, renditionQuery } from '../models/picture-rendition.models';
import { decodeFieldErrors, problemCodeOf, problemMessageOf, statusCodeOf } from './ai-request';

/** What asking for pictures came back with. */
export type GeneratedImageRequestOutcome =
  /** `202`: the operation is durable and queued. Nothing is generated yet — watch it. */
  | { readonly status: 'accepted'; readonly operation: GeneratedImageOperation }
  /** The request was the wrong shape, in the server's own words. */
  | {
      readonly status: 'refused';
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  /** The `Idempotency-Key` header was missing. A client defect, and its own case so it reads as one. */
  | { readonly status: 'key_required' }
  /** Asking for pictures needs the Contributor role. */
  | { readonly status: 'forbidden' }
  /** The edge turned this away for coming too fast. Waiting is the remedy. */
  | { readonly status: 'rate_limited' }
  | { readonly status: 'unavailable' };

/** Where one run has got to. Never an error, so one failed read cannot kill a polling loop. */
export type GeneratedImageWatchOutcome =
  | { readonly status: 'found'; readonly operation: GeneratedImageOperationDetail }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** One picture's bytes, or the one reason they did not arrive. */
export type StagedImagePreviewOutcome =
  | { readonly status: 'found'; readonly bytes: Blob }
  /** The picture is not there: unknown, another workspace's, or already collected. One answer for all three. */
  | { readonly status: 'gone' }
  /** The metadata exists and the bytes could not be read. Retrying is the remedy, which the route states. */
  | { readonly status: 'unavailable' };

export type StagedImageRejectOutcome =
  /** `204`, whether this call declined it or a previous one already had. */
  | { readonly status: 'declined' }
  /** Already kept or already expired — every state but staged is terminal. */
  | { readonly status: 'conflict' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for the staged images one generation produced: asking for them (IMG-003), reading the run,
 * fetching one to look at, downloading one, and declining one (IMG-005/006).
 *
 * **A picture is named by its id and never by its location.** No member here takes or produces a path, a key,
 * a filename or a provider address — every URL it builds is the gateway's own route with a workspace slug and
 * a guid in it, which is what `media.md` asks of a surface that shows private creator content.
 *
 * **The bytes are fetched, not linked.** `ClientOptions.SpaContentSecurityPolicy` allows images from
 * `'self' data: blob:` only, so an `<img>` pointed at the gateway is blocked outright; `connect-src` does allow
 * the gateway, so the preview arrives as a `Blob` the caller renders through an object URL. The one exception
 * is {@link downloadUrl}: a download is a navigation, and the server names the file and sets the disposition,
 * so reproducing either here would be a second copy of a decision the route already makes.
 *
 * **No cancel, because no route cancels.** Asking, reading, looking and declining are the whole contract.
 */
@Injectable({ providedIn: 'root' })
export class GeneratedImageService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Ask for one to four pictures.
   *
   * The key is the caller's and is required by the route: image generation is the most expensive call this
   * product makes, and a request that committed with a lost response must replay rather than buy a second set.
   * A repeated key answers with the first operation, so there is no conflict case to handle here.
   */
  async request(
    workspaceSlug: string,
    request: RequestGeneratedImagesRequest,
    idempotencyKey: string,
  ): Promise<GeneratedImageRequestOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestGeneratedImages(request), {
          withCredentials: true,
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const operation = decodeGeneratedImageOperation(raw);

      return operation ? { status: 'accepted', operation } : { status: 'unavailable' };
    } catch (error) {
      return mapRequestError(error);
    }
  }

  /** Read the run and the pictures it has produced so far. Any member may. */
  watch(workspaceSlug: string, operationId: string): Observable<GeneratedImageWatchOutcome> {
    const url = this.url(workspaceSlug, `/operations/${encodeURIComponent(operationId)}`);
    if (!url) return of<GeneratedImageWatchOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): GeneratedImageWatchOutcome => {
        const operation = decodeGeneratedImageOperationDetail(raw);

        return operation ? { status: 'found', operation } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => of(mapWatchError(error))),
    );
  }

  /**
   * One picture's bytes, for showing on screen.
   *
   * The caller owns the object URL it makes from these and owns revoking it. Nothing is cached here: the route
   * answers `no-store` because a staged picture is private creator content, and a service holding four images'
   * bytes for the lifetime of the application would be quietly undoing that.
   */
  preview(
    workspaceSlug: string,
    generatedImageId: string,
    rendition?: PictureRendition,
  ): Observable<StagedImagePreviewOutcome> {
    // No rendition is the route's own default, which is the web-size copy when the picture has one. A grid
    // asks for `thumbnail`; nothing asks for `original` just to look at a picture.
    const url = this.url(
      workspaceSlug,
      `/${encodeURIComponent(generatedImageId)}/preview${renditionQuery(rendition)}`,
    );
    if (!url) return of<StagedImagePreviewOutcome>({ status: 'unavailable' });

    return this.http.get(url, { withCredentials: true, responseType: 'blob' }).pipe(
      map((bytes): StagedImagePreviewOutcome => ({ status: 'found', bytes })),
      catchError((error: unknown) =>
        of<StagedImagePreviewOutcome>(statusCodeOf(error) === 404 ? { status: 'gone' } : { status: 'unavailable' }),
      ),
    );
  }

  /**
   * The link a browser follows to download one picture, or null while the gateway address is not known.
   *
   * Only the link. The server sends `Content-Disposition: attachment` and names the file `generated-{n}.{ext}`
   * from the media type it established from the bytes, so the name describes what is actually there — and
   * building a name here would be a second guess at it.
   */
  downloadUrl(workspaceSlug: string, generatedImageId: string, rendition?: PictureRendition): string | null {
    // Stated by the caller rather than left to the route's default, which is the web-size copy: a creator
    // who presses "Original" has to get the picture exactly as it was made.
    return this.url(
      workspaceSlug,
      `/${encodeURIComponent(generatedImageId)}/content${renditionQuery(rendition)}`,
    );
  }

  /** Decline one picture, so retention may remove it. Contributor and above. */
  async reject(workspaceSlug: string, generatedImageId: string): Promise<StagedImageRejectOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(generatedImageId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      await firstValueFrom(this.http.delete<void>(url, { withCredentials: true }));

      return { status: 'declined' };
    } catch (error) {
      return mapRejectError(error);
    }
  }

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/generated-images${suffix}`,
    );
  }
}

/**
 * The one outcome an error from the ask means.
 *
 * `idempotency.key_required` is split out of the 400 rather than reported as a refusal: it is this client's own
 * defect, and showing a creator a field error for a header they have never heard of explains nothing.
 */
function mapRequestError(error: unknown): GeneratedImageRequestOutcome {
  const code = statusCodeOf(error);

  if (code === 400) {
    return problemCodeOf(error) === IDEMPOTENCY_KEY_REQUIRED_CODE
      ? { status: 'key_required' }
      : {
          status: 'refused',
          message: problemMessageOf(error) ?? 'The pictures could not be asked for.',
          fieldErrors: decodeFieldErrors(error),
        };
  }

  // Well formed and still not something the server will do: a prompt proposal that is not this workspace's,
  // or a key already spent on a different request. Neither is fixed by waiting, so neither is reported as
  // unavailable — that would invite a retry that can only be refused again.
  if (code === 422) {
    return {
      status: 'refused',
      message: problemMessageOf(error) ?? 'The pictures could not be asked for.',
      fieldErrors: decodeFieldErrors(error),
    };
  }

  if (code === 403) return { status: 'forbidden' };
  if (code === 429) return { status: 'rate_limited' };

  return { status: 'unavailable' };
}

function mapWatchError(error: unknown): GeneratedImageWatchOutcome {
  const code = statusCodeOf(error);
  if (code === 404) return { status: 'not_found' };
  if (code === 403) return { status: 'forbidden' };

  return { status: 'unavailable' };
}

function mapRejectError(error: unknown): StagedImageRejectOutcome {
  const code = statusCodeOf(error);
  if (code === 404) return { status: 'not_found' };
  if (code === 403) return { status: 'forbidden' };
  if (code === 409) return { status: 'conflict' };

  return { status: 'unavailable' };
}
