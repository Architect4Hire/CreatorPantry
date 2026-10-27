import { booleanAttribute, ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'cp-card',
  standalone: true,
  template: '<ng-content />',
  host: {
    '[class.cp-card--interactive]': 'interactive()',
    '[class.cp-card--flush]': 'flush()',
    '[class.cp-card--featured]': 'featured()',
  },
  styles: [
    `
      :host {
        display: block;
        padding: var(--cp-space-6);
        border: 1px solid var(--cp-card-edge);
        border-radius: var(--cp-radius-lg);
        background: var(--cp-surface);
        color: var(--cp-text);
        box-shadow: var(--cp-shadow-sm);
      }
      /*
     * A card is a sheet in either theme and re-declares no token, so everything inside reads the page's own ink,
     * accents, ring and field colours at the ratios themes.css holds them to (AU.2.2, D2). Its edge is
     * --cp-card-edge, shared with cp-dialog and cp-lightbox: a hairline on the light paper, a stronger line on the
     * dark page, where the sheet's fill is only 1.14:1 off the page. The theme decides that, not this component.
     * The dark card used to be parchment, which put every accent, the danger text and anything on a tint out of
     * reach inside it; DESIGN-SYSTEM.md, "The focus ring", has the history.
     */
      :host.cp-card--flush {
        padding: 0;
        overflow: hidden;
      }
      /*
     * The one topic on a page that matters most (AU.3.3): the tool's tint, and the primary's on an untoned page, edged
     * in the tool's colour. At most one per page — DESIGN-SYSTEM.md, "Warmth inside the app" — or it features nothing.
     * The tint is mixed four parts to one with the sheet: on the pure tint an evergreen link was 4.41:1 on light pink
     * and 4.44:1 on light purple; mixed, it is 4.6:1 or better on every tint (design-system.spec.ts measures it).
     */
      :host.cp-card--featured {
        background: color-mix(in srgb, var(--cp-tone-soft) 80%, var(--cp-surface));
        border-color: var(--cp-tone);
      }
      /* Forced colours drop the tint and repaint the edge, which would leave a featured card a plain one. */
      @media (forced-colors: active) {
        :host.cp-card--featured {
          border-width: 2px;
        }
      }
      :host.cp-card--interactive {
        transition:
          transform var(--cp-duration-fast) var(--cp-ease),
          border-color var(--cp-duration-fast),
          box-shadow var(--cp-duration-fast);
        cursor: pointer;
      }
      :host.cp-card--interactive:hover {
        transform: translateY(-2px);
        border-color: color-mix(in srgb, var(--cp-primary) 45%, var(--cp-border));
        box-shadow: var(--cp-shadow-md);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpCardComponent {
  readonly interactive = input(false);
  readonly flush = input(false);
  /** The one topic on the page that matters most. At most one per page. A bare `featured` attribute turns it on. */
  readonly featured = input(false, { transform: booleanAttribute });
}
