import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { GeneratedImageQualityClass } from '../models/generated-image.models';
import { PlanCatalogue, PlanEstimate, decodePlanCatalogue, decodePlanEstimate } from '../models/plan-catalogue.models';

/**
 * The published plan catalogue (LP.6). Anonymous: the landing page reads it before anyone has signed in, and
 * the route carries nothing about an account.
 *
 * Every failure answers the same way — an empty catalogue — because the page's only question is whether there
 * is pricing to show. A deployment that publishes none, a gateway that is down and a body that does not decode
 * are all "nothing to show" to a visitor, and none of them is worth a marketing page showing an error.
 */
@Injectable({ providedIn: 'root' })
export class PlanCatalogueService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Nothing published. The currency is blank rather than a guess: no figure in this bundle is ever shown, and
   * a page with no plans never formats money anyway.
   */
  static readonly EMPTY: PlanCatalogue = {
    currency: '',
    plans: [],
    packs: [],
    rates: null,
    pictureClasses: [],
    defaultPictureClass: null,
  };

  /**
   * The catalogue, or null when it could not be read — for a page that must tell "nothing published" from "could not
   * ask" (AP.6.5's billing page), which the landing page deliberately does not.
   */
  read(): Observable<PlanCatalogue | null> {
    const url = this.apiBase.url('/api/v1/plans');
    if (url === null) return of(null);

    return this.http.get<unknown>(url).pipe(
      map((body) => decodePlanCatalogue(body)),
      catchError(() => of(null)),
    );
  }

  catalogue(): Observable<PlanCatalogue> {
    const url = this.apiBase.url('/api/v1/plans');
    if (url === null) return of(PlanCatalogueService.EMPTY);

    // No credentials: the catalogue is the same for everyone, the gateway strips the cookie on this route,
    // and a request that carried one would invite a shared cache to hold a personalised answer.
    return this.http.get<unknown>(url).pipe(
      map((body) => decodePlanCatalogue(body) ?? PlanCatalogueService.EMPTY),
      catchError(() => of(PlanCatalogueService.EMPTY)),
    );
  }

  /**
   * What a month of pictures and writing comes to, and the plan that carries it (LP.7, AP.7.2) — worked out by the
   * server, so the estimator formats an answer rather than computing one (B-55). Null when there is no answer to
   * show: nothing published, a gateway that is down, or a body that does not decode.
   */
  estimate(
    pictures: number,
    writing: number,
    qualityClass: GeneratedImageQualityClass | null,
  ): Observable<PlanEstimate | null> {
    const url = this.apiBase.url('/api/v1/plans/estimate');
    if (url === null) return of(null);

    let params = new HttpParams().set('pictures', pictures).set('writing', writing);
    if (qualityClass !== null) params = params.set('qualityClass', qualityClass);

    // No credentials, for the reason the catalogue sends none: the answer is the same for everyone who asks it.
    return this.http.get<unknown>(url, { params }).pipe(
      map((body) => decodePlanEstimate(body)),
      catchError(() => of(null)),
    );
  }
}
