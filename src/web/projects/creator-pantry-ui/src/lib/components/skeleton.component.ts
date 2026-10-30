import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { CpSpinnerComponent } from './spinner.component';

export type CpSkeletonShape = 'line' | 'block' | 'circle' | 'card';

/** The widths a block of lines cycles through, so it reads as a paragraph arriving rather than a table. */
const LINE_WIDTHS = ['100%', '92%', '78%', '86%'];
/** The last line of a block, shorter, as a paragraph's last line is. */
const LAST_LINE = '60%';
/** A media card's lines under its picture: the title, then two short facts. */
const CARD_LINES = [
  { width: '70%', title: true },
  { width: '45%', title: false },
  { width: '58%', title: false },
];
/** More than a page of skeleton says nothing more than a page of it does. */
const MOST = 24;

/**
 * What is about to arrive, while it loads: the space it will take, kept clear, with one `cp-spinner` turning in it
 * — centred, or held near the top of a tall space so it is not below the fold. A page shows that something is
 * coming, where it will be, rather than a sentence saying so.
 *
 * **It holds the space, so nothing moves.** It still lays out the shape of what is coming — a line of text, a
 * panel, a disc, or a `cp-media-card`'s silhouette — but draws none of it: the shapes are there only for their size,
 * so the content that replaces them lands where the spinner was and the page around it does not jump.
 *
 * **One spinner, in muted ink.** Text-sized where it stands in for one line — a title, a figure — and larger
 * everywhere else. It is `cp-spinner`, the one a busy button draws, so a wait looks the same wherever it is met, and
 * it stands still under reduced motion.
 *
 * **It says nothing.** The host is `aria-hidden` and takes no focus. Telling a screen reader that something is
 * loading is the job of the region that is loading — `aria-busy` on it and one polite status sentence, which is
 * what `cp-list-shell` does — so a skeleton never adds a second announcement.
 *
 * `card` with a `count` lays its space out as `ul[cpCardGrid]` does — as many columns as fit — so the grid that
 * replaces it takes the same room. Its picture is 4:3, a media card's, unless `pictureAspect` says the pictures
 * coming are another shape — a run of generated pictures, held at the shape of the ones already made. A `block`
 * given a `pictureAspect` is a picture of that shape too, rather than a panel 8rem tall, for a consumer whose own
 * tile frames the picture.
 */
@Component({
  selector: 'cp-skeleton',
  standalone: true,
  imports: [CpSpinnerComponent],
  host: {
    'aria-hidden': 'true',
    '[attr.data-shape]': 'shape()',
    '[attr.data-compact]': 'compact() ? "" : null',
    '[style.--cp-skeleton-picture-aspect]': 'pictureAspect()',
    '[attr.data-aspect]': 'pictureAspect() ? "" : null',
  },
  template: `
    @for (item of items(); track $index) {
      @switch (shape()) {
        @case ('card') {
          <span class="card">
            <span class="bone picture"></span>
            @for (line of cardLines; track $index) {
              <span [class]="['bone', 'line', line.title ? 'title' : 'short']" [style.width]="line.width"></span>
            }
          </span>
        }
        @case ('line') {
          <span class="bone line" [style.width]="item"></span>
        }
        @default {
          <span [class]="['bone', shape()]"></span>
        }
      }
    }
    <cp-spinner class="spinner" />
  `,
  styles: [
    `
      :host {
        position: relative;
        display: grid;
        gap: var(--cp-space-3);
      }
      :host([data-shape='card']) {
        grid-template-columns: repeat(auto-fill, minmax(min(16rem, 100%), 1fr));
        gap: var(--cp-space-4);
      }
      :host([data-shape='circle']) {
        grid-auto-flow: column;
        justify-content: start;
      }

      /* The shapes hold the space and draw nothing. */
      .bone,
      .card {
        visibility: hidden;
      }
      .bone {
        display: block;
      }
      /* Centred in a short space; in a tall one — a grid of cards stacked on a phone — held near the top, where it
         is seen, rather than in a middle below the fold. */
      .spinner {
        position: absolute;
        inset-inline: 0;
        top: min(calc(50% - 0.5em), 5rem);
        margin-inline: auto;
        font-size: 1.75rem;
        color: var(--cp-text-muted);
      }
      /* One line is a value about to be written — a title, a figure — so the spinner sits where its words begin. */
      :host([data-compact]) .spinner {
        font-size: 1rem;
        margin-inline: 0;
      }
      .line {
        height: 0.875rem;
      }
      .title {
        height: 1.125rem;
      }
      .short {
        height: 0.75rem;
      }
      .block {
        height: 8rem;
      }
      :host([data-aspect]) .block {
        height: auto;
        aspect-ratio: var(--cp-skeleton-picture-aspect);
      }
      .circle {
        width: 2.5rem;
        height: 2.5rem;
      }
      /* cp-card's padding and edge, so the space is the card that replaces it. */
      .card {
        display: grid;
        gap: var(--cp-space-2);
        padding: var(--cp-space-6);
        border: 1px solid transparent;
      }
      .picture {
        aspect-ratio: var(--cp-skeleton-picture-aspect, 4 / 3);
        margin-bottom: var(--cp-space-2);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpSkeletonComponent {
  /** What is coming: a line of text, a panel, a disc, or a media card. */
  readonly shape = input<CpSkeletonShape>('line');
  /** How many of it, from one to a page's worth. A block of lines varies in width and ends short. */
  readonly count = input(1);
  /** A `card`'s picture, or a `block`, as a CSS ratio ("1 / 1", "1024 / 768"); null keeps 4:3 and 8rem. */
  readonly pictureAspect = input<string | null>(null);

  protected readonly cardLines = CARD_LINES;

  /** One line standing in for a title or a figure: the spinner is text-sized there, not a panel's. */
  protected readonly compact = computed(() => this.shape() === 'line' && this.items().length === 1);

  /** One entry per item; for lines, each one's width. */
  protected readonly items = computed(() => {
    const asked = this.count();
    const count = Number.isFinite(asked) ? Math.min(MOST, Math.max(1, Math.floor(asked))) : 1;
    return Array.from({ length: count }, (_, index) =>
      count > 1 && index === count - 1 ? LAST_LINE : LINE_WIDTHS[index % LINE_WIDTHS.length],
    );
  });
}
