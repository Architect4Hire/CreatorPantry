import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

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
 * The shape of what is about to arrive, drawn while it loads (AU.2.3, APP-003): a line of text, a panel, a disc,
 * or a `cp-media-card`'s silhouette. A page shows the layout coming rather than a sentence saying it is coming.
 *
 * **It says nothing.** The host is `aria-hidden` and takes no focus. Telling a screen reader that something is
 * loading is the job of the region that is loading — `aria-busy` on it and one polite status sentence, which is
 * what `cp-list-shell` does — so a skeleton never adds a second announcement.
 *
 * **It moves by `background-position`.** A band of `--cp-skeleton-shine` sweeps across `--cp-skeleton-base` over
 * `--cp-duration-skeleton`; nothing slides with `translateX`, which the page contract keeps for the drawer. Under
 * `prefers-reduced-motion` it does not move at all and is the plain base colour. That is said here rather than left
 * to global.css, whose clamp only shortens the sweep to one instant pass: still a sweep, just one nobody sees.
 *
 * `card` with a `count` lays its cards out as `ul[cpCardGrid]` does — as many columns as fit — so a library's
 * skeleton is the grid that will replace it.
 */
@Component({
  selector: 'cp-skeleton',
  standalone: true,
  host: {
    'aria-hidden': 'true',
    '[attr.data-shape]': 'shape()',
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
  `,
  styles: [
    `
      :host {
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

      .bone {
        display: block;
        border-radius: var(--cp-radius-sm);
        background: linear-gradient(
          90deg,
          var(--cp-skeleton-base) 0%,
          var(--cp-skeleton-base) 35%,
          var(--cp-skeleton-shine) 50%,
          var(--cp-skeleton-base) 65%,
          var(--cp-skeleton-base) 100%
        );
        background-size: 300% 100%;
        animation: cp-skeleton-sweep var(--cp-duration-skeleton) linear infinite;
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
        border-radius: var(--cp-radius-md);
      }
      .circle {
        width: 2.5rem;
        height: 2.5rem;
        border-radius: var(--cp-radius-full);
      }
      /* cp-card's padding, edge and radius, so the silhouette is the card that replaces it. */
      .card {
        display: grid;
        gap: var(--cp-space-2);
        padding: var(--cp-space-6);
        border: 1px solid var(--cp-card-edge);
        border-radius: var(--cp-radius-lg);
        background: var(--cp-surface);
      }
      .picture {
        aspect-ratio: 4 / 3;
        margin-bottom: var(--cp-space-2);
        border-radius: var(--cp-radius-md);
      }

      @keyframes cp-skeleton-sweep {
        from {
          background-position: 100% 0;
        }
        to {
          background-position: 0% 0;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .bone {
          animation: none;
          background: var(--cp-skeleton-base);
        }
      }

      /* Forced colours drop backgrounds, which would leave nothing of a skeleton but the card's border. */
      @media (forced-colors: active) {
        .bone {
          animation: none;
          border: 1px solid GrayText;
        }
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

  protected readonly cardLines = CARD_LINES;

  /** One entry per item; for lines, each one's width. */
  protected readonly items = computed(() => {
    const asked = this.count();
    const count = Number.isFinite(asked) ? Math.min(MOST, Math.max(1, Math.floor(asked))) : 1;
    return Array.from({ length: count }, (_, index) =>
      count > 1 && index === count - 1 ? LAST_LINE : LINE_WIDTHS[index % LINE_WIDTHS.length],
    );
  });
}
