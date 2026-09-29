import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * A row of short fields that share the width instead of each taking a line of its own.
 *
 * Three minute-durations, or a quantity and its unit, stacked one per row in a narrow column is the worst use
 * of space a form can make. This wraps them into as many columns as fit and no more.
 *
 * `min(<floor>, 100%)` rather than the floor alone: an auto-fit track whose floor is a fixed length cannot
 * shrink below it, so at a narrow width — or at 200% zoom, which narrows the container the same way — the row
 * would hold itself wider than the page and push it sideways (DESIGN-SYSTEM.md, SC 1.4.10).
 *
 * Its fields still take the reading measure, because the columns alone do not bound them: two wide fields on a
 * desktop split the row between themselves and each ends up far past a comfortable line. The measure only ever
 * binds when a column is wider than it, which is exactly the case worth catching.
 */
@Component({
  selector: 'cp-field-row',
  standalone: true,
  template: '<ng-content />',
  host: { '[style.--cp-field-row-min]': 'minColumn()' },
  styles: [
    `
      :host {
        display: grid;
        gap: var(--cp-space-4);
        grid-template-columns: repeat(auto-fit, minmax(min(var(--cp-field-row-min, 14rem), 100%), 1fr));
        align-items: start;
        min-width: 0;
      }

      /*
       * The same measure cp-form-section gives a field it holds directly, for the same reason, and on the
       * wrapper rather than the control inside it. A row holds fields and nothing else, so a descendant
       * selector costs nothing here.
       */
      :host ::ng-deep cp-field {
        max-width: var(--cp-field-row-measure, var(--cp-measure-field));
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpFieldRowComponent {
  /** The narrowest a column may get before the row wraps. Widen it for fields that need more room. */
  readonly minColumn = input('14rem');
}
