import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The link back up from a page to the one it was reached from — the breadcrumb `cp-page-head` holds.
 *
 * An attribute on the consumer's own `<a>`, so the consumer keeps `routerLink` and the words, and this names
 * no route. The link's text is the place it goes ("DAM", "Recipes"), not "Back to…": the arrow already says
 * back, and it is decoration, so it is hidden from a screen reader that will read the destination anyway.
 */
@Component({
  selector: 'a[cpBackLink]',
  standalone: true,
  template: `<span aria-hidden="true">←</span><ng-content />`,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        gap: var(--cp-space-1);
        /* 40px, so it is a real target on a touch screen though it reads as a line of small text. */
        min-height: 2.5rem;
        color: var(--cp-text-muted);
        overflow-wrap: anywhere;
      }
      :host(:focus-visible) {
        outline: 2px solid var(--cp-focus);
        outline-offset: 2px;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpBackLinkComponent {}
