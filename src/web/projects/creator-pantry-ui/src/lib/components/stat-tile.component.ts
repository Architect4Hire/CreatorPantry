import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { CpCardComponent } from './card.component';

/**
 * One figure a creator reads at a glance (AU.3.3, APP-008): the number, what it counts in words beside it, an
 * optional sentence of context, and an optional link to where it is dealt with.
 *
 * **Read, not declaimed.** The figure is bold `--cp-font-sans` at `--cp-font-size-2xl`, in ink, never the display face
 * or a tone: on a toned page it is still the number that matters, not the tool.
 *
 * **Never a bare number.** The figure and its label are one sentence — "12 recipes waiting for review" — read as one
 * by a screen reader and wrapped as one at a narrow width, so the number is never left on a line saying nothing.
 *
 * **The figure is the server's, as the feature formats it.** `value` is text: the feature formats the count the read
 * model sent (`1,284`, `3 of 40`) and the library adds no precision, no unit and no arithmetic. There is no trend
 * arrow or delta: a comparison is shown only when the server sends one, and then as words in `context`.
 *
 * **The link is the feature's own `<a>`**, projected into `[cpStatTileLink]`, so routing stays in the feature and the
 * library names no route. Name it by what it opens ("Review the 12"), not "See more".
 *
 * **What it does not own.** What is counted, where the link goes, and what to say when the figure could not be read
 * — the feature projects a `cp-notice` into the default slot and leaves `value` empty.
 */
@Component({
  selector: 'cp-stat-tile',
  standalone: true,
  imports: [CpCardComponent],
  template: `<cp-card>
    <div class="tile">
      @if (value()) {
        <!-- The space is interpolated: Angular drops whitespace between two elements, and "12recipes" is one word
             to a screen reader however the gap looks. -->
        <p class="figure"><span class="value">{{ value() }}</span>{{ ' ' }}<span class="label">{{ label() }}</span></p>
      } @else {
        <p class="figure"><span class="label">{{ label() }}</span></p>
      }
      @if (context()) {
        <p class="context">{{ context() }}</p>
      }
      <ng-content />
      <div class="link"><ng-content select="[cpStatTileLink]" /></div>
    </div>
  </cp-card>`,
  styles: [
    `
      /* Full height, so a row of tiles is as tall as the tallest and their links line up. A size container, so the
         figure answers to the tile's width rather than the window's. */
      :host {
        display: block;
        height: 100%;
        container-type: inline-size;
      }
      cp-card {
        height: 100%;
      }
      .tile {
        display: flex;
        flex-direction: column;
        gap: var(--cp-space-2);
        height: 100%;
      }
      /* Inline flow, not flex: a flex row does not render the space between the figure and its label, and that space
         is what keeps "12 recipes" two words. The label wraps after the figure at a narrow width. */
      .figure {
        margin: 0;
      }
      /* Bold sans, not the display face: a figure is read, not declaimed (DESIGN-SYSTEM.md, "Meters and figures").
         --cp-font-size-2xl follows the window, so it is the ceiling and the tile's width the driver: at 22cqi a
         five-character count fits a 12rem tile. Proportional figures, which a standalone number wants. */
      .value {
        font: 700 min(var(--cp-font-size-2xl), 22cqi) / 1 var(--cp-font-sans);
        color: var(--cp-text);
        overflow-wrap: anywhere;
      }
      .label {
        line-height: var(--cp-leading-tight);
        font-size: var(--cp-font-size-md);
        font-weight: 600;
        color: var(--cp-text);
        overflow-wrap: anywhere;
      }
      .context {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
        overflow-wrap: anywhere;
      }
      /* At the foot, so the links of a row of tiles line up. */
      .link {
        margin-top: auto;
        padding-top: var(--cp-space-1);
        font-size: var(--cp-font-size-sm);
      }
      .link:not(:has(> *)) {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpStatTileComponent {
  /** The figure, as the feature formats what the server sent. Empty when it could not be read. */
  readonly value = input('');
  /** What the figure counts, in words: "recipes waiting for review". Never left out. */
  readonly label = input.required<string>();
  /** One sentence of context — what the figure covers, or the comparison the server sent. */
  readonly context = input('');
}
