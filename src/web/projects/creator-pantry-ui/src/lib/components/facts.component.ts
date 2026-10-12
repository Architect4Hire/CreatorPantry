import { booleanAttribute, ChangeDetectionStrategy, Component, input } from '@angular/core';

/** How a fact's term sits against its value. */
export type CpFactsLayout = 'stacked' | 'inline';

/**
 * A list of facts about one thing: what kind of file, how many versions, when it was added.
 *
 * **Why a component.** Nineteen feature stylesheets declared a `.facts`. This is DAM's two, which are the two
 * there are: `stacked` on a page about the thing, where a small muted term sits over its value and a value
 * may run to a paragraph; and `inline` on a card in a list, where term and value share a line and there are
 * only ever a few short ones.
 *
 * **A real description list.** An attribute on the consumer's own `<dl>`, with `cpFact` on each `<div>`
 * inside it, because a `<dl>` may hold only `<div>`, `<dt>` and `<dd>` — a custom element between the list
 * and its terms would stop a screen reader announcing them as term and value.
 */
@Component({
  selector: 'dl[cpFacts]',
  standalone: true,
  template: '<ng-content />',
  host: { '[attr.data-layout]': 'layout()' },
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
        margin: 0;
      }
      :host([data-layout='inline']) {
        gap: var(--cp-space-1);
        font-size: var(--cp-font-size-sm);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpFactsComponent {
  /** `stacked` for a page about the thing; `inline` for a card in a list. */
  readonly layout = input<CpFactsLayout>('stacked');
}

/**
 * One fact in a `cpFacts` list: a term, and its value.
 *
 * The value is projected, so it can be a date through a pipe, a link, or a list of badges. `prose` keeps the
 * line breaks of a value that is the creator's own words; without it they collapse as any text's do.
 */
@Component({
  selector: 'div[cpFact]',
  standalone: true,
  template: `<dt>{{ label() }}</dt>
    <dd><ng-content /></dd>`,
  host: { '[class.cp-fact--prose]': 'prose()' },
  styles: [
    `
      dt {
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-xs);
      }
      dd {
        margin: 0;
        overflow-wrap: anywhere;
      }
      :host(.cp-fact--prose) dd {
        white-space: pre-wrap;
      }
      /* In a card the term and the value share a line and wrap together; the term takes the list's size
         rather than a smaller one, since it is already beside the thing it names. */
      :host-context(dl[data-layout='inline']) {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
      }
      :host-context(dl[data-layout='inline']) dt {
        font-size: inherit;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpFactComponent {
  /** The term: what this fact is. */
  readonly label = input.required<string>();
  /** Keeps the value's own line breaks, for the creator's words. A bare `prose` attribute is enough. */
  readonly prose = input(false, { transform: booleanAttribute });
}
