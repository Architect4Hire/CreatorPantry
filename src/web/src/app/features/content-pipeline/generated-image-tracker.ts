import { Signal, computed, signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

import { AiStatusConnection } from '../ai/ai-operation-status.component';
import { pollOperation } from '../ai/ai-poll';
import {
  GeneratedImageOperationDetail,
  isGeneratedImageOperationFailed,
  isGeneratedImageOperationFinished,
} from '../../models/generated-image.models';
import { GeneratedImageRequestOutcome, GeneratedImageWatchOutcome } from '../../services/generated-image.service';

/**
 * Where one image run has got to, from the step's point of view.
 *
 * `watching` carries the last reading rather than nothing, so a half-full contact sheet stays on screen while
 * the next variant is asked after. `ready` covers `PartiallySucceeded` as well as `Succeeded`: some pictures
 * landed, and those are what the creator came for — the shortfall is a sentence, not a failure state.
 */
export type GeneratedImageRunPhase =
  | { readonly kind: 'idle' }
  | { readonly kind: 'submitting' }
  | { readonly kind: 'watching'; readonly operation: GeneratedImageOperationDetail | null }
  | { readonly kind: 'ready'; readonly operation: GeneratedImageOperationDetail }
  /** Finished with nothing to show. `failureSummary` says why, in the application's own words. */
  | { readonly kind: 'failed'; readonly operation: GeneratedImageOperationDetail }
  /** The run is no longer there, or was never this workspace's. One answer for both (tenancy.md). */
  | { readonly kind: 'gone' }
  | { readonly kind: 'forbidden' }
  /** The ask was the wrong shape, in the server's own words. */
  | {
      readonly kind: 'refused';
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  /** Asked for too fast. Waiting is the remedy, and it is the creator's. */
  | { readonly kind: 'rate_limited' }
  | { readonly kind: 'unavailable' };

/** How a tracker reaches its run. One function, so the tracker names no route and no service. */
export type GeneratedImageWatch = (operationId: string) => Observable<GeneratedImageWatchOutcome>;

/**
 * Submit-then-poll, for one image run.
 *
 * **Why not `AiOperationTracker`.** That one is typed to `AiProposalStatus`, reads a `proposal` off it, and
 * models the AI allowance refusals — none of which this route has: image generation has no quota check, no
 * task-enablement code and no idempotency conflict, and what it produces is pictures rather than a proposal.
 * What is shared is the *discipline*, which is reproduced here deliberately: a generation counter so an answer
 * from an abandoned ask never lands on the run the creator is looking at, a degraded-connection signal so a
 * stale reading is never passed off as a live one, and one poll loop (`pollOperation`) rather than a second
 * copy of the timing rules.
 *
 * **It owns no injection context**, so a component can hold one in a field initialiser. {@link destroy} is the
 * caller's job, through its `DestroyRef`.
 */
export class GeneratedImageTracker {
  private readonly phaseSignal = signal<GeneratedImageRunPhase>({ kind: 'idle' });
  private readonly operationIdSignal = signal<string | null>(null);
  private readonly failuresSignal = signal(0);
  private readonly stoppedSignal = signal(false);

  private subscription: Subscription | null = null;

  /**
   * Which ask, resume or re-read the current poll belongs to.
   *
   * Bumped on every one of them and checked before anything is applied, so a late answer about a run the
   * creator has replaced cannot overwrite the one on screen.
   */
  private generation = 0;

  readonly phase: Signal<GeneratedImageRunPhase> = this.phaseSignal.asReadonly();
  readonly operationId: Signal<string | null> = this.operationIdSignal.asReadonly();

  /** True once the loop has given up on its own, so the step can offer to start checking again. */
  readonly stopped: Signal<boolean> = this.stoppedSignal.asReadonly();

  /** `degraded` after a failed poll, so the status line says the reading is stale rather than implying it is live. */
  readonly connection: Signal<AiStatusConnection> = computed(() =>
    this.failuresSignal() > 0 ? 'degraded' : 'live',
  );

  /** The last reading of the run, in whichever phase carries one. */
  readonly detail: Signal<GeneratedImageOperationDetail | null> = computed(() => {
    const phase = this.phaseSignal();

    return phase.kind === 'watching' || phase.kind === 'ready' || phase.kind === 'failed'
      ? phase.operation
      : null;
  });

  /** True while the creator is waiting, so an action that would start another run can be held. */
  readonly busy: Signal<boolean> = computed(() => {
    const kind = this.phaseSignal().kind;

    return kind === 'submitting' || kind === 'watching';
  });

  /** True once {@link destroy} has run. Nothing starts after that. */
  private destroyed = false;

  constructor(private readonly watch: GeneratedImageWatch) {}

  /**
   * Ask for the pictures, then watch for them.
   *
   * Returns the operation id when the server accepted, and null otherwise — which is what the caller keeps in
   * its own draft so the run can be found again after a refresh.
   */
  async submit(ask: () => Promise<GeneratedImageRequestOutcome>): Promise<string | null> {
    if (this.destroyed) return null;

    const generation = this.begin();
    this.phaseSignal.set({ kind: 'submitting' });

    const outcome = await ask();
    if (generation !== this.generation) return null;

    switch (outcome.status) {
      case 'accepted': {
        const operationId = outcome.operation.id;
        this.operationIdSignal.set(operationId);
        // Straight to watching with nothing in hand: the accepted body carries no image identities at all, and
        // the first poll fires immediately, so there is no gap to fill with a shape that cannot show pictures.
        this.phaseSignal.set({ kind: 'watching', operation: null });
        this.startPolling(operationId, generation);

        return operationId;
      }
      case 'refused':
        this.phaseSignal.set({ kind: 'refused', message: outcome.message, fieldErrors: outcome.fieldErrors });
        return null;
      case 'key_required':
        // A client defect rather than anything a creator did, so it reads as an outage: there is nothing for
        // them to correct, and naming a header they have never heard of would explain nothing.
        this.phaseSignal.set({ kind: 'unavailable' });
        return null;
      case 'forbidden':
        this.phaseSignal.set({ kind: 'forbidden' });
        return null;
      case 'rate_limited':
        this.phaseSignal.set({ kind: 'rate_limited' });
        return null;
      default:
        this.phaseSignal.set({ kind: 'unavailable' });
        return null;
    }
  }

  /** Pick a run back up by its id — on a refresh, or when a creator returns to the step. */
  resume(operationId: string): void {
    if (this.destroyed) return;

    const generation = this.begin();
    this.operationIdSignal.set(operationId);
    this.phaseSignal.set({ kind: 'watching', operation: null });
    this.startPolling(operationId, generation);
  }

  /**
   * Ask again about the run already on screen, keeping that reading while the answer is in flight.
   *
   * Used after a decline, where the server's state has changed and the client's copy of it has not. It is not
   * {@link resume}, which starts from nothing: blanking a contact sheet a creator is reading in order to
   * confirm one tile's new status would be a worse answer than a moment of staleness.
   */
  reread(): void {
    if (this.destroyed) return;

    const operationId = this.operationIdSignal();
    if (operationId === null) return;

    const current = this.detail();
    const generation = this.begin();
    this.phaseSignal.set({ kind: 'watching', operation: current });
    this.startPolling(operationId, generation);
  }

  /**
   * Stop asking.
   *
   * It does **not** cancel the generation: the routes offer asking, reading, looking and declining, and nothing
   * else, so a control claiming to cancel would promise what the server cannot honour. What it stops is this
   * screen's polling.
   */
  stopChecking(): void {
    this.begin();
    this.stoppedSignal.set(true);
  }

  /** Back to the beginning, with nothing in flight and nothing kept. */
  reset(): void {
    this.begin();
    this.operationIdSignal.set(null);
    this.phaseSignal.set({ kind: 'idle' });
  }

  /**
   * Done with this tracker.
   *
   * **It abandons what is in flight as well as unsubscribing**, and the difference matters: a {@link submit}
   * awaiting its answer when the screen goes would otherwise pass the generation check on the way back and
   * open a poll loop with nothing left to tear it down — and then call back into a destroyed component.
   * Being destroyed *is* abandonment, so it goes through the same gate every other abandonment does.
   *
   * **And it is final.** A screen's own continuation can outlive the screen — a decline awaiting its answer, a
   * confirmation still open when the creator pressed Back — and calling {@link reread}, {@link resume} or
   * {@link submit} from one would otherwise start a poll, or ask for pictures, on behalf of a screen that is gone.
   */
  destroy(): void {
    this.destroyed = true;
    this.begin();
  }

  /** Abandons whatever is in flight and returns the generation the caller now owns. */
  private begin(): number {
    this.subscription?.unsubscribe();
    this.subscription = null;
    this.failuresSignal.set(0);
    this.stoppedSignal.set(false);

    return ++this.generation;
  }

  private startPolling(operationId: string, generation: number): void {
    this.subscription = pollOperation(
      () => this.watch(operationId),
      (operation) => !isGeneratedImageOperationFinished(operation.status),
      () => {
        if (generation === this.generation) this.stoppedSignal.set(true);
      },
    ).subscribe((outcome) => this.applyWatch(outcome, generation));
  }

  private applyWatch(outcome: GeneratedImageWatchOutcome, generation: number): void {
    if (generation !== this.generation) return;

    switch (outcome.status) {
      case 'found':
        this.failuresSignal.set(0);
        this.apply(outcome.operation, generation);
        break;
      case 'unavailable':
        // The last reading stays on screen. One failed poll must not blank pictures a creator is looking at.
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

  private apply(operation: GeneratedImageOperationDetail, generation: number): void {
    if (generation !== this.generation) return;

    if (!isGeneratedImageOperationFinished(operation.status)) {
      this.phaseSignal.set({ kind: 'watching', operation });
      return;
    }

    // `Failed` and `Cancelled` have nothing to show; `PartiallySucceeded` has fewer pictures than were asked
    // for and is still a run with pictures in it, so it lands in `ready` with the shortfall left to the step
    // to say.
    this.phaseSignal.set(
      isGeneratedImageOperationFailed(operation.status)
        ? { kind: 'failed', operation }
        : { kind: 'ready', operation },
    );
  }
}
