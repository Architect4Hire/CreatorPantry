import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * Something is on its way: one arc of a thin ring, turning.
 *
 * **One spinner, everywhere it is drawn.** A busy `cpButton` draws it before its label, and a `cp-skeleton` draws
 * it in the middle of the space it holds, so a wait looks the same wherever a creator meets it. A feature does not
 * draw a spinner of its own; the page contract's `spinner` rule fails one that does.
 *
 * **Sized and coloured by where it sits.** `1em` square in `currentColor`, so it takes the size and colour of the
 * text around it — a button's label, a muted placeholder — and needs no tokens of its own beyond its pace,
 * `--cp-duration-spinner`.
 *
 * **It says nothing.** `aria-hidden`: whatever is waiting says so itself — a busy button's `aria-busy` and words, a
 * loading region's `aria-busy` and its one status sentence. Under reduced motion it is a still arc; global.css only
 * shortens an animation, so it is turned off here.
 */
@Component({
  selector: 'cp-spinner',
  standalone: true,
  host: { 'aria-hidden': 'true' },
  templateUrl: './spinner.component.html',
  styleUrl: './spinner.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpSpinnerComponent {}
