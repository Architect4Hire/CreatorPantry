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
  /**
   * The account's credit packs, spent after `remaining` and never expiring (B-50). Beside it, never added to it:
   * a meter of the allowance does not count them.
   */
  readonly packRemaining: number;
}

/**
 * The low-credit notice, when there is one to show (PRICE-011 as amended, B-59).
 *
 * <strong>One derivation, so the shell and the allowance page cannot come to different answers.</strong> The
 * server says whether the account crossed the line, whether the creator put the notice away and whether they
 * want it at all; what is left to decide here is whether something stronger is already being said, and an
 * exhausted or suspended account is told that instead.
 */
export interface AiLowCreditNotice {
  /** What is left to spend — the allowance and the packs together, as the server summed them. */
  readonly spendable: number;
  readonly unit: AiQuotaUnit;
  readonly resetsAt: string;
  readonly timeZoneId: string;
  /** False for a trial's credits, which are granted once: never promise them back (B-48). */
  readonly allowanceRenews: boolean;
}

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

  /**
   * A dismissal this tab has made, held until the next read agrees with it.
   *
   * The notice goes the moment it is clicked away rather than a round trip later, and a request that fails
   * leaves it hidden for this session — the next read brings it back, which is the right way round for
   * something whose whole job is to be noticed.
   */
  private readonly dismissedHere = signal(false);

  /** The period the local dismissal above belongs to, so a roll mid-session does not inherit it. */
  private periodStartedAt: string | null = null;

  /** The allowance as the surfaces talk about it. One derivation, so the page and the notice cannot disagree. */
  readonly allowance = computed<AiAllowanceState>(() => {
    const state = this.stateSignal();

    if (state.status === 'loading' || state.status === 'error') return { kind: 'unknown' };

    const period = figuresOf(state.usage);

    if (state.status === 'degraded') return { kind: 'degraded', period };
    if (state.usage.isSuspended) return { kind: 'suspended', period };

    // Spent only when the packs are too: admission takes from them once the allowance is gone (AP.5.2), so a
    // spent allowance with packs left must not disable a request the server would accept. Two of the server's
    // figures compared, never summed.
    if (period.remaining <= 0 && period.packRemaining <= 0) return { kind: 'exhausted', period };

    // The server's line, not one kept here (AP.2.6): the page, the notice and an image quote all read the same
    // answer, so none of them can come to warn at a different point from the others.
    return state.usage.isNearlySpent ? { kind: 'nearly-spent', period } : { kind: 'healthy', period };
  });

  /** The notice to show about running low, or null when there is nothing to say. */
  readonly lowCredit = computed<AiLowCreditNotice | null>(() => {
    const state = this.stateSignal();

    // Nothing read, a failed read, or figures we are no longer sure of: a notice offering a pack on ignorance
    // would be worse than silence, which is the rule the in-context allowance notice already follows.
    if (state.status !== 'ready') return null;

    const { lowCredit, period, isSuspended, packRemaining, plan } = state.usage;

    if (!lowCredit.isNoticed || lowCredit.isDismissed || !lowCredit.noticeEnabled) return null;
    if (this.dismissedHere()) return null;

    // Both say something stronger and both say it for themselves: "spent" offers the same pack, and
    // "switched off" has no pack to offer at all.
    if (isSuspended) return null;
    if (period.remaining <= 0 && packRemaining <= 0) return null;

    return {
      spendable: lowCredit.spendable,
      unit: period.unit,
      resetsAt: period.resetsAt,
      timeZoneId: period.timeZoneId,
      allowanceRenews: plan?.allowanceRenews ?? true,
    };
  });

  /** Whether the account wants the notice at all. True while nothing has been read, which is the default. */
  readonly lowCreditNoticeEnabled = computed(() => {
    const state = this.stateSignal();

    return state.status === 'loading' || state.status === 'error' ? true : state.usage.lowCredit.noticeEnabled;
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

        // A read that brings a period this tab has not dismissed anything in clears the local hide, so a roll
        // mid-session shows the next period's notice.
        if (this.periodStartedAt !== usage.period.startsAt) {
          this.periodStartedAt = usage.period.startsAt;
          this.dismissedHere.set(false);
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

  /**
   * Puts this period's notice away: hidden at once, then recorded.
   *
   * Idempotent on the server, and a failure is not reported — the creator asked for the notice to go, it has
   * gone, and nothing they typed depends on the request landing.
   */
  async dismissLowCreditNotice(): Promise<void> {
    this.dismissedHere.set(true);

    const url = this.apiBase.url('/api/v1/me/ai-usage/low-credit-notice/dismissal');
    if (!url) return;

    try {
      await firstValueFrom(this.http.post<void>(url, null, { withCredentials: true }));
    } catch {
      // Left hidden for this session. The next read is what decides whether it comes back.
    }
  }

  /**
   * Says whether this account wants the notice at all, and re-reads so every surface agrees.
   *
   * @returns whether it was recorded. The caller owns telling the creator when it was not, because the
   * control they just moved has to go back if it did not take.
   */
  async setLowCreditNoticeEnabled(enabled: boolean): Promise<boolean> {
    const url = this.apiBase.url('/api/v1/me/ai-usage/low-credit-notice');
    if (!url) return false;

    try {
      await firstValueFrom(this.http.put<void>(url, { enabled }, { withCredentials: true }));
    } catch {
      return false;
    }

    // Turning it back on should show a notice the account is owed and has not already put away, so the
    // figures are re-read rather than patched here. A dismissal is the server's and is not cleared by this:
    // a creator who has read this period's notice has read it, whatever they then do with the setting.
    this.dismissedHere.set(false);
    await this.load();

    return true;
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
    packRemaining: usage.packRemaining,
  };
}
