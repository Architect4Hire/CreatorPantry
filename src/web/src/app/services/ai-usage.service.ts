import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AccountAiUsage, AiQuotaPeriodLength, AiQuotaUnit, decodeAccountAiUsage } from '../models/ai-usage.models';

/**
 * What the app knows about the signed-in account's allowance.
 *
 * `degraded` is the state worth naming: a refresh that fails keeps the last answer on screen rather than
 * blanking figures a creator is reading, the same way the AI operation panel keeps the last operation state.
 * `error` is the first failure with nothing held.
 */
export type AiUsageState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly usage: AccountAiUsage }
  | { readonly status: 'degraded'; readonly usage: AccountAiUsage }
  | { readonly status: 'error' };

/**
 * The allowance, as a surface needs to talk about it.
 *
 * <strong>Seven states, and only three of them may stop a creator doing anything.</strong> `unknown` and
 * `degraded` mean "we do not know", and a surface that refused on ignorance would lock somebody out of AI
 * because a config fetch blipped. The server is the authority and already refuses properly; the client's
 * disable is a courtesy.
 */
export type AiAllowanceState =
  /** Nothing read yet, or read and failed. Say nothing; block nothing. */
  | { readonly kind: 'unknown' }
  /** Read once, then a refresh failed. The figures are the last ones we had. */
  | { readonly kind: 'degraded'; readonly period: AllowanceFigures }
  | { readonly kind: 'healthy'; readonly period: AllowanceFigures }
  | { readonly kind: 'nearly-spent'; readonly period: AllowanceFigures }
  | { readonly kind: 'exhausted'; readonly period: AllowanceFigures }
  /** Switched off administratively. No reset to wait for, which is why it is not `exhausted`. */
  | { readonly kind: 'suspended'; readonly period: AllowanceFigures };

/** The figures every non-`unknown` state carries, so a consumer never re-derives them. */
export interface AllowanceFigures {
  /** Typed, not widened to `string`: a renamed or added unit should be a compile error at each comparison. */
  readonly unit: AiQuotaUnit;
  readonly allowance: number;
  readonly remaining: number;
  readonly consumed: number;
  /** What rolled in from the period before, so a roll can be shown rather than folded into the total. */
  readonly carriedOver: number;
  readonly periodLength: AiQuotaPeriodLength;
  readonly resetsAt: string;
  readonly timeZoneId: string;
  /**
   * How much of the allowance is spoken for, 0-100, for a meter.
   *
   * Consumed <strong>and</strong> reserved, because that is what `remaining` is the complement of. A bar
   * drawn from `consumed` alone beside the words "640 left of 1,000" would disagree with them by exactly the
   * amount runs in flight are holding — and this refreshes right after a request is accepted, which is
   * precisely when something is in flight.
   */
  readonly usedPercent: number;
}

/**
 * When an allowance counts as nearly spent.
 *
 * <strong>A product decision, not a mirrored one.</strong> The server publishes no threshold, no percentage
 * and no "nearly" signal of any kind — it says what is left and refuses when there is not enough. A fifth is
 * the point at which telling somebody is still useful rather than merely alarming.
 */
export const NEARLY_SPENT_FRACTION = 0.2;

/**
 * The typed client for `GET /api/v1/me/ai-usage` — the signed-in account's own allowance, across every
 * workspace it works in (USAGE-008).
 *
 * <strong>Account-scoped, and there is nowhere to name an account.</strong> No method takes an id, and the
 * route carries none: the server reads it from the validated session. That is what makes "only the caller's
 * own figures" structural rather than checked.
 */
@Injectable({ providedIn: 'root' })
export class AiUsageService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private readonly stateSignal = signal<AiUsageState>({ status: 'loading' });
  readonly state = this.stateSignal.asReadonly();

  private inFlight: Promise<void> | null = null;
  private loadGeneration = 0;

  /** The allowance as the surfaces talk about it. One derivation, so the page and the notice cannot disagree. */
  readonly allowance = computed<AiAllowanceState>(() => {
    const state = this.stateSignal();

    if (state.status === 'loading' || state.status === 'error') return { kind: 'unknown' };

    const period = figuresOf(state.usage);

    if (state.status === 'degraded') return { kind: 'degraded', period };
    if (state.usage.isSuspended) return { kind: 'suspended', period };
    if (period.remaining <= 0) return { kind: 'exhausted', period };

    // Guard the divide: an allowance of zero is spent by definition, and is handled above.
    const fraction = period.allowance > 0 ? period.remaining / period.allowance : 0;

    return fraction <= NEARLY_SPENT_FRACTION ? { kind: 'nearly-spent', period } : { kind: 'healthy', period };
  });

  /** Loads once. A prior failure is retried on the next call rather than cached forever. */
  ensureLoaded(): Promise<void> {
    if (this.inFlight) return this.inFlight;

    const status = this.stateSignal().status;
    if (status === 'loading' || status === 'error') return this.load();

    return Promise.resolve();
  }

  /**
   * Re-reads the allowance. Called after an accepted AI request so the figure moves without a page reload.
   */
  refresh(): Promise<void> {
    return this.load();
  }

  /**
   * If a newer call starts before this one's response arrives, this call's result is discarded — the most
   * recently *started* load always wins, regardless of which resolves first.
   */
  async load(): Promise<void> {
    const generation = ++this.loadGeneration;
    const isCurrent = () => generation === this.loadGeneration;

    // The state is not reset to `loading` here. A refresh must not blank figures a creator is already
    // reading; the first read starts in `loading` and that is the only time the surface has nothing to show.
    const request = (async () => {
      const url = this.apiBase.url('/api/v1/me/ai-usage');
      if (!url) {
        if (isCurrent()) this.fail();
        return;
      }

      try {
        const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
        if (!isCurrent()) return;

        const usage = decodeAccountAiUsage(raw);
        if (usage === null) {
          this.fail();
          return;
        }

        this.stateSignal.set({ status: 'ready', usage });
      } catch {
        if (isCurrent()) this.fail();
      }
    })();

    this.inFlight = request;
    try {
      await request;
    } finally {
      if (this.inFlight === request) this.inFlight = null;
    }
  }

  /** Degrade if we have something to degrade to; otherwise there is genuinely nothing to show. */
  private fail(): void {
    const current = this.stateSignal();
    const held = current.status === 'ready' || current.status === 'degraded' ? current.usage : null;

    this.stateSignal.set(held ? { status: 'degraded', usage: held } : { status: 'error' });
  }
}

function figuresOf(usage: AccountAiUsage): AllowanceFigures {
  const { unit, allowance, remaining, consumed, carriedOver, reserved, periodLength, resetsAt, timeZoneId } =
    usage.period;

  const spokenFor = consumed + reserved;

  return {
    unit,
    allowance,
    remaining,
    consumed,
    carriedOver,
    periodLength,
    resetsAt,
    timeZoneId,

    // Clamped, because the total may legitimately exceed the allowance: settlement charges what a run
    // actually cost and the next admission refuses, rather than the total being capped on the way in.
    usedPercent: allowance > 0 ? Math.min(100, Math.max(0, Math.round((spokenFor / allowance) * 100))) : 100,
  };
}
