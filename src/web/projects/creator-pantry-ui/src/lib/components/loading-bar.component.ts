import { ChangeDetectionStrategy, Component, effect, input, signal } from '@angular/core';

/**
 * Something on its way that has no shape to draw yet: a thin bar sweeping along an edge — the next page's code,
 * between a click and the page that answers it.
 *
 * **Not a skeleton's replacement.** A region whose layout is known draws that layout in `cp-skeleton`s
 * (DESIGN-SYSTEM.md, "Loading looks like what is coming"); this is for the moment before there is a region at all,
 * which is why it belongs to chrome — the shell's top bar — and not to a page.
 *
 * **Shown late, gone at once.** It appears only once `active` has held for `delay`, so a navigation that answers
 * quickly never flashes it, and fades out as soon as `active` ends. Presentation only: what counts as active is the
 * consumer's — the shell's router — so the library names no route.
 *
 * **Said once.** The bar is `aria-hidden`; an always-present visually hidden status says `label` while the bar
 * shows and nothing otherwise, the same pattern a loading region uses, so a screen reader hears it once and only
 * for a wait long enough to see.
 *
 * It sweeps by `translate` over `--cp-duration-sweep`, the "still coming" pace, in `--cp-primary`: the chrome
 * it sits in takes no tool's tone. Under reduced motion it is a still full-width bar.
 */
@Component({
  selector: 'cp-loading-bar',
  standalone: true,
  host: { '[attr.data-shown]': "shown() ? '' : null" },
  templateUrl: './loading-bar.component.html',
  styleUrl: './loading-bar.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpLoadingBarComponent {
  /** Something is on its way. The bar shows once this has held for {@link delay}. */
  readonly active = input(false);

  /** What a screen reader hears while the bar shows, e.g. "Opening the page". Never shown on screen. */
  readonly label = input('Loading');

  /** How long `active` must hold before the bar shows, in milliseconds. */
  readonly delay = input(200);

  private readonly shownSignal = signal(false);
  protected readonly shown = this.shownSignal.asReadonly();

  constructor() {
    effect((onCleanup) => {
      if (!this.active()) {
        this.shownSignal.set(false);
        return;
      }

      const handle = setTimeout(() => this.shownSignal.set(true), this.delay());
      onCleanup(() => clearTimeout(handle));
    });
  }
}
