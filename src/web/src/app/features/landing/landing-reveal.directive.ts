import { DOCUMENT } from '@angular/common';
import { DestroyRef, Directive, ElementRef, afterNextRender, inject, input } from '@angular/core';

/** Motion is opt-in. Without this preference the directive touches nothing at all. */
export const REVEAL_MOTION_QUERY = '(prefers-reduced-motion: no-preference)';

/** How far a section rises as it arrives, and how long each child waits behind the one before it. */
const RISE = 'var(--cp-space-2)';
const STEP_MS = 60;

/** Past this many children the stagger stops reading as a cascade and starts reading as a page that is slow. */
const MAX_STEPS = 6;

/**
 * Comfortably past the longest transition this can start — `--cp-duration-normal` plus the last child's delay —
 * after which the inline properties are taken off again.
 */
const SETTLED_MS = 250 + MAX_STEPS * STEP_MS + 250;

/**
 * Fades a landing section in and lifts it a few pixels as it comes into view (LP.11). With a selector, the
 * children it names arrive one after another rather than the section arriving as one block.
 *
 * ```html
 * <cp-landing-value-props cpReveal="li" />
 * ```
 *
 * **It styles the element itself, and only once it has decided to.** There is no stylesheet behind this, and
 * nothing is hidden by a class that CSS applies — so a visitor who has asked for reduced motion, or whose
 * browser has no `IntersectionObserver`, is left with a section that was never hidden in the first place.
 * Done the usual way round, with CSS hiding everything and script revealing it, either of those is a page of
 * blank space. (It buys nothing against JavaScript failing outright: this is an Angular application, and
 * `index.html` holds one empty `<cp-root>`, so there is no page to leave unhidden in that case.)
 *
 * The starting state is written in the constructor, before the element has been painted, so a section already
 * in view fades in rather than flashing and then hiding.
 *
 * The element has to have a box of its own: one laid out with `display: contents` generates no box, so it can
 * never intersect anything and would stay hidden for good. Every section this is used on is `display: block`.
 *
 * Never put this on the hero. It is the first screen and holds the page's LCP element; delaying its paint to
 * animate it would spend the budget LP.11 exists to protect.
 */
@Directive({ selector: '[cpReveal]', standalone: true })
export class LandingRevealDirective {
  /**
   * A selector for the children that arrive one after another, resolved inside this element. Empty — the
   * default — means the section arrives as one.
   */
  readonly cpReveal = input('');

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly window = inject(DOCUMENT).defaultView;
  private observer: IntersectionObserver | null = null;

  constructor() {
    const allowed = this.window?.matchMedia?.(REVEAL_MOTION_QUERY).matches ?? false;
    if (!allowed) return;

    // Before first paint: the element exists but has not been drawn, so hiding it here is never seen.
    this.hide(this.host, 0);

    afterNextRender(() => {
      // The children only exist once the section's own template has rendered.
      this.staggered().forEach((child, index) => this.hide(child, Math.min(index, MAX_STEPS) * STEP_MS));

      if (!this.window || !('IntersectionObserver' in this.window)) {
        // No observer, no scrolling to listen to: show it rather than leave it hidden for good.
        this.show();
        return;
      }

      this.observer = new IntersectionObserver(
        ([entry]) => {
          if (!entry.isIntersecting) return;
          this.show();
        },
        // Ten percent up from the bottom of the window, so a section arrives having been seen, not on its
        // first pixel. No threshold: a section taller than the window never reaches a fraction of itself.
        { rootMargin: '0px 0px -10% 0px' },
      );
      this.observer.observe(this.host);
    });

    this.window?.addEventListener('beforeprint', this.reveal);
    inject(DestroyRef).onDestroy(() => {
      this.observer?.disconnect();
      this.window?.removeEventListener('beforeprint', this.reveal);
    });
  }

  /**
   * Shows everything at once, with no transition, for a print or a full-page capture: both of those render the
   * whole document without scrolling it, so a section still waiting to be scrolled to would come out blank.
   */
  private readonly reveal = () => this.show();

  private clear(): void {
    for (const element of [this.host, ...this.staggered()]) {
      element.style.removeProperty('opacity');
      element.style.removeProperty('translate');
      element.style.removeProperty('transition');
    }
  }

  private staggered(): HTMLElement[] {
    const selector = this.cpReveal();
    return selector ? Array.from(this.host.querySelectorAll<HTMLElement>(selector)) : [];
  }

  private hide(element: HTMLElement, delayMs: number): void {
    element.style.opacity = '0';
    element.style.translate = `0 ${RISE}`;
    element.style.transition =
      `opacity var(--cp-duration-normal) var(--cp-ease) ${delayMs}ms, translate var(--cp-duration-normal) var(--cp-ease) ${delayMs}ms`;
  }

  private show(): void {
    this.observer?.disconnect();
    this.observer = null;
    for (const element of [this.host, ...this.staggered()]) {
      element.style.opacity = '1';
      element.style.translate = '0 0';
    }

    // Once the transition has run, take the properties off entirely. Any `translate` other than `none` makes an
    // element a stacking context and a containing block for `position: fixed` inside it, and a section that has
    // finished arriving should leave nothing of the arrival behind.
    this.window?.setTimeout(() => this.clear(), SETTLED_MS);
  }
}
