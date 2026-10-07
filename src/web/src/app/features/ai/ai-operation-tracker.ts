import { Signal, computed, signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

import { AiProposalDetail, AiProposalStatus } from '../../models/ai-proposal.models';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { AiStatusConnection } from './ai-operation-status.component';
import { pollAiOperation } from './ai-poll';

/**
 * Where one AI operation has got to, from a screen's point of view.
 *
 * `watching` carries the last reading rather than nothing, so a status line can say what the server last said
 * instead of blanking while it asks again. `ready` is the end of the road for these three capabilities: there is
 * no disposition route for a concept, a composed prompt or a reference reading.
 */
export type AiOperationPhase =
  | { readonly kind: 'idle' }
  | { readonly kind: 'submitting' }
  | { readonly kind: 'watching'; readonly operation: AiProposalStatus | null }
  | { readonly kind: 'ready'; readonly operation: AiProposalStatus }
  | { readonly kind: 'failed'; readonly operation: AiProposalStatus }
  /** The request is no longer there, or was never this workspace's. One answer for both. */
  | { readonly kind: 'gone' }
  | { readonly kind: 'forbidden' }
  /** Something the request named cannot be used, in the server's own words. */
  | {
      readonly kind: 'refused';
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  /** A real task, switched off for this deployment. A configuration change, not a client fix. */
  | { readonly kind: 'not_enabled' }
  /**
   * The idempotency key was reused for a different request.
   *
   * Its own phase rather than folded into `unavailable`, because the remedy differs and is the creator's: ask
   * again, which mints a fresh key. Reported as an outage it would look like something that might fix itself.
   */
  | { readonly kind: 'key_reused' }
  /** The account's allowance will not cover this, or AI is switched off for it. */
  | { readonly kind: 'blocked'; readonly refusal: AiQuotaRefusal }
  | { readonly kind: 'unavailable' };

/** How a tracker reaches its operation. One function, so the tracker names no route and no service. */
export type AiOperationWatch = (requestId: string) => Observable<AiWatchOperationOutcome>;

/**
 * Submit-then-poll, for one AI operation.
 *
 * **Why a class rather than three copies of the same component logic.** The Content Pipeline's prompt step runs
 * three operations side by side, and each needs the same six things: a phase, a request id worth keeping, a poll
 * loop, a degraded-connection signal, a way to stop checking, and the discipline that a late answer from an
 * abandoned request never lands. Written once, and it is the part worth testing on its own.
 *
 * **It owns no injection context**, so a component can hold several of these in field initialisers. The poll is
 * an explicit subscription and {@link destroy} is the caller's job — through its `DestroyRef`.
 */
export class AiOperationTracker {
  private readonly phaseSignal = signal<AiOperationPhase>({ kind: 'idle' });
  private readonly requestIdSignal = signal<string | null>(null);
  private readonly failuresSignal = signal(0);
  private readonly stoppedSignal = signal(false);

  private subscription: Subscription | null = null;

  /**
   * Which submission or resume the current poll belongs to.
   *
   * Bumped on every submit, resume, stop and reset, and checked before anything is applied — so an answer from a
   * request the creator has moved on from cannot overwrite the one they are looking at.
   */
  private generation = 0;

  readonly phase: Signal<AiOperationPhase> = this.phaseSignal.asReadonly();
  readonly requestId: Signal<string | null> = this.requestIdSignal.asReadonly();

  /** True once the loop has given up on its own, so a screen can offer to start checking again. */
  readonly stopped: Signal<boolean> = this.stoppedSignal.asReadonly();

  /** `degraded` after a failed poll, so a status line says the reading is stale rather than implying it is live. */
  readonly connection: Signal<AiStatusConnection> = computed(() =>
    this.failuresSignal() > 0 ? 'degraded' : 'live',
  );

  /** The proposal, once there is one. Null in every other phase. */
  readonly proposal: Signal<AiProposalDetail | null> = computed(() => {
    const phase = this.phaseSignal();
    return phase.kind === 'ready' ? phase.operation.proposal : null;
  });

  /** True while the creator is waiting on this operation, so an action that would start another can be held. */
  readonly busy: Signal<boolean> = computed(() => {
    const kind = this.phaseSignal().kind;
    return kind === 'submitting' || kind === 'watching';
  });

  constructor(private readonly watch: AiOperationWatch) {}

  /**
   * Ask for the operation, then watch it.
   *
   * Returns the request id when the server accepted, and null otherwise — which is what the caller keeps in its
   * own draft so the operation can be found again after a refresh.
   */
  async submit(ask: () => Promise<AiRequestOutcome>): Promise<string | null> {
    const generation = this.begin();
    this.phaseSignal.set({ kind: 'submitting' });

    const outcome = await ask();
    if (generation !== this.generation) return null;

    switch (outcome.status) {
      case 'accepted': {
        const requestId = outcome.operation.aiProposalRequestId;
        this.requestIdSignal.set(requestId);
        this.apply(outcome.operation, generation);
        // A replayed request can arrive already `Proposed`, and watching one would be a wasted poll.
        if (this.phaseSignal().kind === 'watching') this.startPolling(requestId, generation);
        return requestId;
      }
      case 'task_not_enabled':
        this.phaseSignal.set({ kind: 'not_enabled' });
        return null;
      case 'validation_failed':
        this.phaseSignal.set({ kind: 'refused', message: '', fieldErrors: outcome.fieldErrors });
        return null;
      case 'refused':
        this.phaseSignal.set({ kind: 'refused', message: outcome.message, fieldErrors: {} });
        return null;
      case 'idempotency_key_conflict':
        this.phaseSignal.set({ kind: 'key_reused' });
        return null;
      case 'forbidden':
        this.phaseSignal.set({ kind: 'forbidden' });
        return null;
      case 'quota_exhausted':
      case 'account_suspended':
        this.phaseSignal.set({ kind: 'blocked', refusal: outcome });
        return null;
      default:
        this.phaseSignal.set({ kind: 'unavailable' });
        return null;
    }
  }

  /** Pick an operation back up by its id — on a refresh, or when a creator returns to the step. */
  resume(requestId: string): void {
    const generation = this.begin();
    this.requestIdSignal.set(requestId);
    this.phaseSignal.set({ kind: 'watching', operation: null });
    this.startPolling(requestId, generation);
  }

  /**
   * Stop asking.
   *
   * It does **not** cancel the generation: these routes offer asking and reading and nothing else, so a button
   * claiming to cancel would promise what the server cannot honour. What it stops is this screen's polling.
   */
  stopChecking(): void {
    this.begin();
    this.stoppedSignal.set(true);
  }

  /** Back to the beginning, with nothing in flight and nothing kept. */
  reset(): void {
    this.begin();
    this.requestIdSignal.set(null);
    this.phaseSignal.set({ kind: 'idle' });
  }

  destroy(): void {
    this.subscription?.unsubscribe();
    this.subscription = null;
  }

  /** Abandons whatever is in flight and returns the generation the caller now owns. */
  private begin(): number {
    this.subscription?.unsubscribe();
    this.subscription = null;
    this.failuresSignal.set(0);
    this.stoppedSignal.set(false);

    return ++this.generation;
  }

  private startPolling(requestId: string, generation: number): void {
    this.subscription = pollAiOperation(
      () => this.watch(requestId),
      () => {
        if (generation === this.generation) this.stoppedSignal.set(true);
      },
    ).subscribe((outcome) => this.applyWatch(outcome, generation));
  }

  private applyWatch(outcome: AiWatchOperationOutcome, generation: number): void {
    if (generation !== this.generation) return;

    switch (outcome.status) {
      case 'found':
        this.failuresSignal.set(0);
        this.apply(outcome.operation, generation);
        break;
      case 'unavailable':
        // The last reading stays on screen. One failed poll must not blank content a creator is reading.
        this.failuresSignal.update((count) => count + 1);
        break;
      case 'forbidden':
        this.phaseSignal.set({ kind: 'forbidden' });
        break;
      default:
        this.phaseSignal.set({ kind: 'gone' });
        break;
    }
  }

  private apply(operation: AiProposalStatus, generation: number): void {
    if (generation !== this.generation) return;

    if (operation.status === 'Proposed') {
      // A proposal is what `Proposed` means, so one without it is a shape this client cannot use. Reported as
      // unavailable, with a retry — polling has already stopped at `Proposed`, so leaving it `watching` would
      // strand the screen on "Check again" for something that will never change.
      this.phaseSignal.set(
        operation.proposal !== null ? { kind: 'ready', operation } : { kind: 'unavailable' },
      );
      return;
    }

    if (operation.status === 'Failed' || operation.status === 'Rejected' || operation.status === 'Expired') {
      this.phaseSignal.set({ kind: 'failed', operation });
      return;
    }

    this.phaseSignal.set({ kind: 'watching', operation });
  }
}
