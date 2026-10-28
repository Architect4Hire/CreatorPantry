import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { PlanCatalogue, decodePlanCatalogue } from '../models/plan-catalogue.models';

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
}
