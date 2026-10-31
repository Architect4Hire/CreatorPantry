import { DOCUMENT } from '@angular/common';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  BillingAccount,
  BillingCheckoutStatus,
  BillingCycle,
  BillingRedirect,
  decodeBillingAccount,
  decodeBillingCheckoutStatus,
  decodeBillingRedirect,
} from '../models/billing.models';
import { problemCodeOf, problemMessageOf, statusCodeOf } from './ai-request';

/** What a billing command came back with. */
export type BillingCommandOutcome =
  /** Go to the provider's page, or — for a change at period end — nothing to pay: it is scheduled. */
  | { readonly status: 'redirect'; readonly redirect: BillingRedirect }
  /** Done, with nothing to show: a scheduled change withdrawn. */
  | { readonly status: 'done' }
  /**
   * The server said no, in its own words: no plan to change yet, already subscribed, a trial that has ended, an
   * unknown plan. `reread` means the page is out of date and should be read again before offering anything.
   */
  | { readonly status: 'refused'; readonly code: string | null; readonly message: string; readonly reread: boolean }
  /** Payments are not available here or right now. Nothing was charged. */
  | { readonly status: 'unavailable' };

/**
 * The signed-in account's billing (AP.6.5): its read, and the Checkout, portal and plan-change commands.
 *
 * **Nothing here buys anything.** A command answers with the payment provider's page to go to; the plan moves and the
 * credits arrive when the provider's webhook does, which `checkout()` reports. The caller owns each command's
 * `Idempotency-Key`, so a retried click is answered with the first click's page rather than a second Checkout.
 *
 * The page's own origin travels with each command so the provider sends the creator back to it; the server uses it
 * only when it lists that origin.
 */
@Injectable({ providedIn: 'root' })
export class BillingService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);
  private readonly document = inject(DOCUMENT);

  /** The account's billing, or null when it could not be read. */
  async read(): Promise<BillingAccount | null> {
    const url = this.apiBase.url('/api/v1/me/billing');
    if (url === null) return null;

    try {
      return decodeBillingAccount(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));
    } catch {
      return null;
    }
  }

  startPlanCheckout(planKey: string, cycle: BillingCycle, idempotencyKey: string): Promise<BillingCommandOutcome> {
    return this.command('/api/v1/me/billing/checkout/subscription', { planKey, cycle }, idempotencyKey);
  }

  startPackCheckout(packKey: string, idempotencyKey: string): Promise<BillingCommandOutcome> {
    return this.command('/api/v1/me/billing/checkout/pack', { packKey }, idempotencyKey);
  }

  changePlan(planKey: string, cycle: BillingCycle, idempotencyKey: string): Promise<BillingCommandOutcome> {
    return this.command('/api/v1/me/billing/plan-change', { planKey, cycle }, idempotencyKey);
  }

  openPortal(): Promise<BillingCommandOutcome> {
    return this.command('/api/v1/me/billing/portal', {}, null);
  }

  async withdrawPlanChange(idempotencyKey: string): Promise<BillingCommandOutcome> {
    const url = this.apiBase.url('/api/v1/me/billing/plan-change');
    if (url === null) return { status: 'unavailable' };

    try {
      await firstValueFrom(
        this.http.delete(url, { withCredentials: true, headers: { 'Idempotency-Key': idempotencyKey } }),
      );
      return { status: 'done' };
    } catch (error) {
      return refusalOf(error);
    }
  }

  /**
   * Leaves for the provider's page. Here rather than in the page, so the one navigation away from the app that a
   * purchase makes has one owner — and a spec can stand in for it without the browser leaving.
   */
  leaveFor(url: string): void {
    this.document.defaultView?.location.assign(url);
  }

  /** Whether what a Checkout bought has landed; null when it could not be asked. */
  async checkout(sessionId: string, kind: 'Plan' | 'Pack'): Promise<BillingCheckoutStatus | null> {
    const url = this.apiBase.url(`/api/v1/me/billing/checkout/${encodeURIComponent(sessionId)}`);
    if (url === null) return null;

    try {
      return decodeBillingCheckoutStatus(
        await firstValueFrom(
          this.http.get<unknown>(url, { withCredentials: true, params: new HttpParams().set('kind', kind) }),
        ),
      );
    } catch {
      return null;
    }
  }

  private async command(path: string, body: object, idempotencyKey: string | null): Promise<BillingCommandOutcome> {
    const url = this.apiBase.url(path);
    if (url === null) return { status: 'unavailable' };

    const returnOrigin = this.document.defaultView?.location.origin ?? null;

    try {
      const answer = await firstValueFrom(
        this.http.post<unknown>(
          url,
          { ...body, returnOrigin },
          {
            withCredentials: true,
            headers: idempotencyKey === null ? {} : { 'Idempotency-Key': idempotencyKey },
          },
        ),
      );
      const redirect = decodeBillingRedirect(answer);
      return redirect === null ? { status: 'unavailable' } : { status: 'redirect', redirect };
    } catch (error) {
      return refusalOf(error);
    }
  }
}

/** A 503 or no answer is "unavailable"; anything else the server refused is said in its own words. */
function refusalOf(error: unknown): BillingCommandOutcome {
  const code = statusCodeOf(error);
  if (code === null || code === 0 || code === 503 || code >= 500) return { status: 'unavailable' };

  return {
    status: 'refused',
    code: problemCodeOf(error),
    message: problemMessageOf(error) ?? 'That couldn’t be done. Nothing was charged.',
    reread: code === 409,
  };
}
