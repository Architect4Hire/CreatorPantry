import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * What the two toolbar fields share: a label that is announced and not shown, wrapped around the consumer's
 * own native control so the two are associated without an id, and the control at the 40px every other control
 * in a toolbar has. How it is drawn — edge, fill, focus, a select's caret — is `native-control.css`, the same
 * sheet `cp-field` uses, so a filter in a toolbar looks like the select in a form.
 *
 * The label is hidden here rather than with the global `.cp-sr-only`, so the component does not depend on a
 * stylesheet its consumer may not have loaded.
 */
const TEMPLATE = `<label><span class="label">{{ label() }}</span><ng-content /></label>`;

const SHARED_STYLES = `
  :host {
    display: block;
    min-width: 0;
  }
  .label {
    position: absolute;
    width: 1px;
    height: 1px;
    padding: 0;
    margin: -1px;
    overflow: hidden;
    clip: rect(0, 0, 0, 0);
    white-space: nowrap;
    border: 0;
  }
  /* The field holds one native control and nothing else, so a descendant selector costs nothing here. Longhand
     padding, so a select's caret room from native-control.css is never undone by load order. */
  :host ::ng-deep :is(input, select) {
    /* 40px, the minimum target. */
    min-height: 2.5rem;
    padding-block: 0;
    padding-inline-start: var(--cp-space-3);
    font: inherit;
  }
  :host ::ng-deep input {
    padding-inline-end: var(--cp-space-3);
  }
`;

/**
 * A search box for a toolbar: the consumer's own `<input type="search">`, named by a label that is announced
 * but not shown, since the placeholder and the toolbar's place on the page already say what it searches.
 *
 * The input is projected, so its value, its `(input)` handler and its debounce stay in the feature — this
 * fetches nothing and knows nothing about what is being searched. Put `cpToolbarSearch` on the element to
 * place it in `cp-toolbar`, whose arrow-key handling still finds the native input inside.
 */
@Component({
  selector: 'cp-search-field',
  standalone: true,
  template: TEMPLATE,
  styleUrl: './native-control.scss',
  styles: [
    SHARED_STYLES,
    `
      /* Wide enough to read a placeholder in; no wider than the toolbar at 320px. */
      :host ::ng-deep input {
        min-width: min(14rem, 100%);
        max-width: 100%;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpSearchFieldComponent {
  /** What this field searches, for a screen reader. Not shown. */
  readonly label = input.required<string>();
}

/**
 * A filter or a sort for a toolbar: the consumer's own `<select>`, named by a label that is announced but not
 * shown, since its first option ("All channels", "Newest first") already reads as what it is.
 *
 * The select is projected, so its options, its value and its `(change)` handler stay in the feature. Put
 * `cpToolbarFilters` or `cpToolbarActions` on the element to place it in `cp-toolbar`.
 */
@Component({
  selector: 'cp-select-field',
  standalone: true,
  template: TEMPLATE,
  styleUrl: './native-control.scss',
  styles: [
    SHARED_STYLES,
    `
      /* A long option must not hold the select wider than the toolbar. */
      :host ::ng-deep select {
        max-width: 100%;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpSelectFieldComponent {
  /** What this field filters or sorts by, for a screen reader. Not shown. */
  readonly label = input.required<string>();
}
