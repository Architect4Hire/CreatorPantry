import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import {
  CreativeContextDraft,
  CreativeContextSource,
  creativeContextSourceKey,
} from '../../models/creative-context.models';
import { CreativeContextCreateOutcome, CreativeContextService } from '../../services/creative-context.service';
import { HandoffDestination } from './handoff-destinations';

/** What the creator already said about the piece, carried into the new context so they are not asked again. */
export type HandoffSeed = Omit<CreativeContextDraft, 'from'>;

/** Why a hand-off did not happen. */
export type HandoffFailure = 'source_unavailable' | 'forbidden' | 'unavailable';

export interface HandoffCompleted {
  readonly destinationKey: string;
  readonly contextId: string;
}

export interface HandoffFailed {
  readonly destinationKey: string;
  readonly reason: HandoffFailure;
}

let nextId = 0;

/**
 * "Use this in…": hands the thing on screen — a concept, a recipe, a picture, a prompt — to another AI surface.
 *
 * Choosing a destination starts a creative context from {@link source} and opens the destination with its id
 * in the route. Nothing about the source is copied: the context points at it, and the destination reads it.
 *
 * **It offers only the destinations it is given**, in the order given, and renders nothing when given none. It
 * does not know what Image Studio or the Content Pipeline are; a destination is a label and a route.
 *
 * **A failure changes nothing.** There is no navigation, the buttons come back, and the creator is where they
 * were with their work as they left it. The control says what happened and the same buttons try again.
 *
 * **One piece of work per source.** The idempotency key is held for as long as the source and seed are the
 * same, so a retry after a lost response opens the context the first attempt made, and so does choosing a
 * second destination for the same source — the alternative is a duplicate context for every click.
 *
 * A composition of `@creator-pantry/ui` exports, kept in the app because it knows about creative contexts.
 */
@Component({
  selector: 'cp-use-this-in',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent],
  templateUrl: './use-this-in.component.html',
  styleUrl: './use-this-in.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UseThisInComponent {
  private readonly contexts = inject(CreativeContextService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();

  /** What is being handed over. */
  readonly source = input.required<CreativeContextSource>();

  /** Where it may go. Only these are offered. */
  readonly destinations = input.required<readonly HandoffDestination[]>();

  readonly seed = input<HandoffSeed | null>(null);

  readonly heading = input('Use this in…');

  /** For a host that is itself busy or read-only. The buttons stay focusable and say they are unavailable. */
  readonly disabled = input(false);

  /** Emitted once the context exists, just before navigating. */
  readonly handedOff = output<HandoffCompleted>();

  readonly failed = output<HandoffFailed>();

  protected readonly headingId = `cp-use-this-in-${nextId++}`;

  /** The destination being opened, or null while idle. */
  protected readonly opening = signal<HandoffDestination | null>(null);

  protected readonly failure = signal<HandoffFailure | null>(null);

  protected readonly blocked = computed(() => this.disabled() || this.opening() !== null);

  /** What a screen reader hears while the context is being made. Empty when there is nothing to say. */
  protected readonly progress = computed(() => {
    const destination = this.opening();

    return destination === null ? '' : `Opening ${destination.label}…`;
  });

  /** The key for the attempt in hand, and what it was issued for. */
  private attempt: { readonly signature: string; readonly key: string } | null = null;

  protected detailId(destination: HandoffDestination): string | null {
    return destination.detail ? `${this.headingId}-${destination.key}` : null;
  }

  protected choose(destination: HandoffDestination): void {
    // `aria-disabled` rather than `disabled`, so focus is not thrown away while the request runs — which means
    // a key press still arrives here and has to be refused here.
    if (this.blocked()) return;

    this.opening.set(destination);
    this.failure.set(null);

    const slug = this.workspaceSlug();

    this.contexts
      .create(slug, { ...(this.seed() ?? {}), from: this.source() }, this.keyFor(slug))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((outcome) => void this.settle(slug, destination, outcome));
  }

  private async settle(
    slug: string,
    destination: HandoffDestination,
    outcome: CreativeContextCreateOutcome,
  ): Promise<void> {
    if (outcome.status !== 'created') {
      // A reused key means this attempt's key now names a different request; the next try needs a new one.
      if (outcome.status === 'key_reused') this.attempt = null;

      const reason: HandoffFailure =
        outcome.status === 'source_unavailable' || outcome.status === 'forbidden' ? outcome.status : 'unavailable';

      this.failure.set(reason);
      this.opening.set(null);
      this.failed.emit({ destinationKey: destination.key, reason });

      return;
    }

    this.handedOff.emit({ destinationKey: destination.key, contextId: outcome.context.id });

    try {
      await this.router.navigate(['/', slug, ...destination.route(outcome.context.id)]);
    } catch {
      // A route that fails to load is not this control's to explain; the buttons come back below.
    }

    // The buttons come back whatever happened. A successful navigation destroys this control with its host; one
    // a guard declined leaves the creator exactly where they were, which is not a failure to report — and the
    // key still names the context that now exists, so choosing again opens the same piece of work.
    this.opening.set(null);
  }

  /** The same key for the same source and seed in the same workspace; a new one when any of them changes. */
  private keyFor(slug: string): string {
    const signature = JSON.stringify([slug, creativeContextSourceKey(this.source()), this.seed() ?? null]);

    if (this.attempt?.signature !== signature) this.attempt = { signature, key: crypto.randomUUID() };

    return this.attempt.key;
  }
}
