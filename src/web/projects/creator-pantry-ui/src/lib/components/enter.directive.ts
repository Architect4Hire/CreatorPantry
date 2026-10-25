import { Directive, ElementRef, OnInit, booleanAttribute, inject, input, signal } from '@angular/core';

/** The keyframes in `global.css`. An `animationend` from anything else is a neighbour's animation, not this one. */
export const CP_ENTER_ANIMATION = 'cp-enter';

/** Past this many steps the stagger stops reading as a set landing and starts reading as a page that is slow. */
export const CP_ENTER_MAX_STEPS = 5;

/**
 * Everything created in one change-detection pass is one arrival, and takes its place in that arrival in
 * creation order. Angular creates a pass's views synchronously, so every `ngOnInit` in it runs before the
 * microtask that resets this — which is why the count can be kept here rather than passed in by a page.
 */
let cohort = 0;
let resetQueued = false;

function nextInCohort(): number {
  const place = cohort++;
  if (!resetQueued) {
    resetQueued = true;
    queueMicrotask(() => {
      cohort = 0;
      resetQueued = false;
    });
  }
  return Math.min(place, CP_ENTER_MAX_STEPS);
}

/**
 * Fades an element in and lifts it `--cp-enter-distance` the first time it renders, over
 * `--cp-duration-enter` (AU.3.4, D4). Several arriving together land `--cp-enter-stagger` apart, in creation
 * order, so a group of cards reads as a set landing rather than as three things appearing at once.
 *
 * **The library applies it; a page does not.** A list shell's ready content, a generated result and a group of
 * options carry it, because an entrance is always right there. `DESIGN-SYSTEM.md`, "Content arrives once,
 * gently", has the rule. The page contract's `enter` rule guards the entrance tokens, the `cp-enter` keyframes
 * and `data-cp-enter` — not the directive itself, which a feature template is free to carry, so that a later
 * document can hand pages the entrance without a check being loosened to let them.
 *
 * **The motion is in `global.css`, keyed on the attribute this sets.** A directive carries no styles, so the
 * keyframes have to be global; what matters is the direction. Nothing is hidden by a class that CSS applies
 * and script is trusted to take off — the hiding exists only inside an animation this directive declared — so
 * a stylesheet that never loaded, or an entrance that has finished, leaves a page that was never hidden. The
 * same sheet clears the stagger's delay under `prefers-reduced-motion`, where `global.css` shortens the
 * animation to an instant but would otherwise leave a staggered item waiting invisible for its turn, and
 * turns the animation off for print, which renders the whole document at once and may not run one at all.
 *
 * **It animates once and cannot be made to animate again.** A CSS animation declares the once; the input is
 * read in `ngOnInit` and never again, so a binding that changes later starts nothing; and the attribute comes
 * off when the animation ends, so moving the element in the DOM afterwards cannot restart it. Content that
 * arrives a second time is a new element, which is the one arrival this answers to. A cancelled animation
 * counts as ended for the same reason: hiding an element mid-arrival — a `[hidden]` tab panel that stays
 * mounted, a closed `<details>` — cancels the animation without ending it, and a mark left behind would run it
 * again when the element is shown.
 *
 * **Focus ends it early.** The element is transparent for as long as its delay lasts, so focus reaching it —
 * a creator tabbing into a list the moment it lands, a page moving focus to what just arrived — ends the
 * arrival, and nothing is ever focused while it cannot be seen. Two mechanisms, because they answer different
 * halves: `global.css`'s `:focus-within` stops the animation in the same style recalculation, synchronously,
 * while this directive's `focusin` takes the mark off for good on the change detection that follows.
 *
 * ```html
 * <div class="scroll" [cpEnter]="!hasBeenReady()">…</div>
 * ```
 *
 * `[cpEnter]="false"` writes nothing at all: no attribute, no delay, and no place in the arrival.
 */
@Directive({
  selector: '[cpEnter]',
  standalone: true,
  host: {
    '[attr.data-cp-enter]': 'arriving() ? "" : null',
    '[style.animation-delay]': 'arriving() ? delay : null',
    '(animationend)': 'settle($event)',
    '(animationcancel)': 'settle($event)',
    '(focusin)': 'settle()',
  },
})
export class CpEnterDirective implements OnInit {
  /** Whether this element arrives. False leaves the element exactly as it was written. */
  readonly cpEnter = input(true, { transform: booleanAttribute });

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  /** True from the render that starts the entrance until it ends, and false for good afterwards. */
  protected readonly arriving = signal(false);

  /** This element's wait, in stagger steps, as a length the token still owns. Null for the first to arrive. */
  protected delay: string | null = null;

  ngOnInit(): void {
    // Read once, here: the entrance belongs to this element's first render, and a binding that changes
    // afterwards must not be able to start one on something the creator has already been reading.
    if (!this.cpEnter()) return;

    const step = nextInCohort();
    this.delay = step > 0 ? `calc(var(--cp-enter-stagger) * ${step})` : null;
    this.arriving.set(true);
  }

  /** The entrance is over — ended, cancelled, or cut short by focus: with no mark there is nothing to replay. */
  protected settle(event?: AnimationEvent): void {
    // A descendant's animation — a skeleton's shimmer, a child's own entrance — ends on its own element.
    if (event && (event.target !== this.host || event.animationName !== CP_ENTER_ANIMATION)) return;
    this.arriving.set(false);
  }
}
