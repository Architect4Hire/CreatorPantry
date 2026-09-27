import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** One destination in a {@link CpAnchorNavComponent}. */
export interface CpAnchorNavItem {
  /** The id of the element this jumps to, without the `#`. The consumer owns that id. */
  readonly targetId: string;
  /** What to call the destination: a heading, or a name for one that has none. */
  readonly label: string;
  /** An optional second line — "4 lines", "3 steps", "2 unresolved" — read before deciding to jump. */
  readonly detail?: string;
}

/**
 * A vertical list of in-page destinations: ordinary navigation over a document that already works without it.
 *
 * Every item is a plain tabbable link, in order, with no roving tabindex and nothing intercepting an arrow
 * key — deliberately *not* a tablist. A tablist promises that its panels are alternatives and that only one is
 * present; this promises the opposite, that everything is there and this only moves you through it. Use it
 * where a long form has named sections a reader would otherwise scroll past.
 *
 * Presentation only, in two ways worth stating:
 *
 * - It does not scroll and does not move focus. It emits {@link activated} and the consumer decides what to
 *   bring into view and which of its own controls deserves focus — which it can name and this cannot.
 * - It does not track what is in view. {@link activeTargetId} is given, so a consumer may mark the destination
 *   last jumped to, the one scrolled into view, or nothing at all.
 *
 * It does click-handling itself, because that part is not a decision: `preventDefault()` keeps an in-page
 * anchor from reaching a router, where a fragment would become a navigation and any unsaved-changes guard
 * would be asked about it. The `href` stays for middle-click and copy-link.
 *
 * With no items it renders nothing — no empty shell, no bare landmark for a screen reader to announce.
 *
 * Orientation is the consumer's, since only they know how much room this has: set
 * `--cp-anchor-nav-direction: column` on the host (from inside a container query, typically) to stack the
 * items in a gutter. The default is a wrapping row, which is what fits above the content it names on a narrow
 * screen and at 200% zoom.
 */
@Component({
  selector: 'cp-anchor-nav', standalone: true,
  template: `@if (items().length > 0) {
    <nav [attr.aria-label]="ariaLabel()">
      <ul>
        @for (item of items(); track item.targetId) {
          <li>
            <a
              [href]="'#' + item.targetId"
              [attr.aria-current]="item.targetId === activeTargetId() ? 'true' : null"
              (click)="onActivate($event, item)"
            >
              <span class="label">{{ item.label }}</span>
              @if (item.detail) { <span class="detail">{{ item.detail }}</span> }
            </a>
          </li>
        }
      </ul>
    </nav>
  }`,
  styles: [`
    :host { display:block; }
    ul { display:flex; flex-wrap:wrap; flex-direction:var(--cp-anchor-nav-direction, row); gap:var(--cp-space-1); margin:0; padding:0; list-style:none; }
    a { display:flex; flex-direction:column; justify-content:center; min-height:2.5rem; padding:var(--cp-space-1) var(--cp-space-3); border-radius:var(--cp-radius-md); color:var(--cp-text-muted); font:600 var(--cp-font-size-sm)/var(--cp-leading-tight) var(--cp-font-sans); text-decoration:none; }
    a:hover { color:var(--cp-text); background:var(--cp-surface-subtle); }
    /* The current destination is named by aria-current for assistive tech; the colour and background are the
       visual half of the same fact, never the only carrier of it. */
    a[aria-current='true'] { color:var(--cp-primary); background:var(--cp-surface-subtle); }
    .label { overflow-wrap:anywhere; }
    .detail { font-weight:400; font-size:var(--cp-font-size-xs); color:var(--cp-text-muted); }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpAnchorNavComponent {
  readonly items = input.required<readonly CpAnchorNavItem[]>();
  readonly ariaLabel = input.required<string>();

  /** The destination to mark with `aria-current`, or null for none. */
  readonly activeTargetId = input<string | null>(null);

  readonly activated = output<CpAnchorNavItem>();

  protected onActivate(event: MouseEvent, item: CpAnchorNavItem): void {
    event.preventDefault();
    this.activated.emit(item);
  }
}
