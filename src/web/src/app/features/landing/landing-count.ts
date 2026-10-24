import { signal } from '@angular/core';

/** How long a figure takes to count from what it was to what it now is. */
export const COUNT_MS = 350;

/**
 * Counts figures from where they were to where they now are — a price changing with the billing period, an
 * estimate changing with a slider.
 *
 * **It is cosmetic, and built so that it cannot be anything else.** {@link shownOr} answers the real figure
 * whenever nothing is counting, so a reader who never sees a frame — reduced motion, a browser with no
 * `requestAnimationFrame`, a count interrupted — is shown exactly what the page would have shown anyway.
 *
 * **One count at a time.** A change mid-count abandons the one running rather than leaving two loops drawing
 * to different targets, where whichever finished first would clear the other's figures. A count also ends when
 * its owner is destroyed, so nothing keeps scheduling frames for a component that is gone.
 */
export class LandingCount {
  private readonly counting = signal<ReadonlyMap<string, number>>(new Map());
  private generation = 0;

  constructor(private readonly view: Window | null) {}

  /** The figure being drawn for `key`, or `actual` whenever nothing is counting — which is almost always. */
  shownOr(key: string, actual: number): number {
    return this.counting().get(key) ?? actual;
  }

  /** Abandons whatever is counting. A component calls this when it is destroyed. */
  stop(): void {
    this.generation += 1;
    this.counting.set(new Map());
  }

  /** Counts each key from `from` to `to`. With motion turned down, the new figures are simply there. */
  run(from: ReadonlyMap<string, number>, to: ReadonlyMap<string, number>): void {
    const view = this.view;
    if (!view?.requestAnimationFrame || view.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
      this.stop();
      return;
    }

    const count = (this.generation += 1);
    const started = view.performance.now();

    // Where it is starting from, written now rather than in the first frame: between the change and that frame
    // `shownOr` would otherwise answer the target, so the figure would jump to its destination and count back.
    this.counting.set(new Map(from));

    const step = () => {
      if (count !== this.generation) return;

      const progress = Math.min(1, (view.performance.now() - started) / COUNT_MS);
      if (progress >= 1) {
        this.counting.set(new Map());
        return;
      }

      const drawn = new Map<string, number>();
      for (const [key, target] of to) {
        const start = from.get(key) ?? target;
        drawn.set(key, Math.round(start + (target - start) * progress));
      }
      this.counting.set(drawn);
      view.requestAnimationFrame(step);
    };

    view.requestAnimationFrame(step);
  }
}
