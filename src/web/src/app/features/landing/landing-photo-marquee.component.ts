import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, afterNextRender, computed, inject, input, signal } from '@angular/core';

import { MARQUEE_ORDER, foodPhoto } from '../../shared/food-photos';

/** Motion is opt-in: without this preference the strip never animates and has nothing to pause. */
export const MARQUEE_MOTION_QUERY = '(prefers-reduced-motion: no-preference)';

/**
 * A slow, endless strip of the page's food photography, drifting sideways (LP.11). It replaces the two image
 * trios, which showed three photographs picked at random out of twelve; this shows all twelve, in a fixed
 * order, and the movement is what makes the page feel alive rather than the reshuffle did.
 *
 * **Decorative.** The page makes no claim about what any of these show, so the track is `aria-hidden` and every
 * photograph has empty alt. The pause control sits outside the track, because a control inside a hidden subtree
 * is a control nobody can reach.
 *
 * **It stops.** WCAG 2.2.2: motion that starts by itself and runs longer than five seconds needs a way to stop
 * it. There are three here — the control, the pointer (`:hover`) and the keyboard (`:focus-within`) — and under
 * `prefers-reduced-motion` the strip never moves in the first place, so the control is not rendered at all.
 * Leaving it rendered and hidden in CSS would put a button that does nothing into the accessibility tree.
 *
 * **Nothing shifts.** The strip reserves its height, every photograph reserves its 2:3 box, and all of them are
 * lazy: both strips sit far below the fold, and LP.11's budget is about what the first screen costs.
 */
@Component({
  selector: 'cp-landing-photo-marquee',
  standalone: true,
  templateUrl: './landing-photo-marquee.component.html',
  styleUrl: './landing-photo-marquee.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingPhotoMarqueeComponent {
  /** Runs the photographs the other way round, and drifts the other way, so two strips never read as one. */
  readonly reverse = input(false);

  /** What the strip is drawn at, so a phone picks the 480 px rendition rather than the 960 px one. */
  protected readonly SIZES = '(min-width: 64rem) 11rem, 18vw';

  protected readonly photos = computed(() => {
    const order = this.reverse() ? [...MARQUEE_ORDER].reverse() : MARQUEE_ORDER;
    return order.map((id) => foodPhoto(id));
  });

  private readonly stopped = signal(false);
  private readonly motionAllowed = signal(false);

  /** Whether the strip animates at all. False under reduced motion, and before the first render decides. */
  protected readonly animated = this.motionAllowed.asReadonly();

  /** Whether it is stopped right now. Always true when nothing is animating, so the CSS needs no second rule. */
  protected readonly paused = computed(() => !this.motionAllowed() || this.stopped());

  private readonly window = inject(DOCUMENT).defaultView;
  private query: MediaQueryList | null = null;
  private readonly follow = () => this.motionAllowed.set(this.query?.matches ?? false);

  constructor() {
    afterNextRender(() => {
      if (!this.window?.matchMedia) return;
      this.query = this.window.matchMedia(MARQUEE_MOTION_QUERY);
      this.query.addEventListener('change', this.follow);
      this.follow();
    });
    inject(DestroyRef).onDestroy(() => this.query?.removeEventListener('change', this.follow));
  }

  protected toggle(): void {
    this.stopped.update((stopped) => !stopped);
  }
}
