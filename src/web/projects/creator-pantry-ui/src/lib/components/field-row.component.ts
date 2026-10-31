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
 *
 * That leaves the row itself as wide as its container, which is the wrong shape for a form whose fields all
 * stop at the measure: a row of short values reaches past the right edge every field above it keeps to, and
 * what it holds lines up with nothing. {@link columns} is the fix, and it is opt-in so a row that wants the
 * old behaviour keeps it. A row with `columns` set is held to the same measure as a field and divided into
 * that many equal columns, so a quantity in it is exactly a fraction of the form's line — the same width, and
 * the same left edge, in every row that uses the same count. It still gives up columns before it lets a field
 * get narrower than {@link minColumn}, so a narrow window and 200% zoom wrap it rather than squeeze it.
 */
@Component({
  selector: 'cp-field-row',
  standalone: true,
  template: '<ng-content />',
  host: {
    '[style.--cp-field-row-min]': 'minColumn()',
    '[style.--cp-field-row-columns]': 'columns()',
    '[class.cp-field-row--fixed]': 'columns() !== null',
  },
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

      /*
       * A row with a column count: held to the measure, and its columns are the larger of the floor and an
       * equal share of the row. At full width the share is the larger and there are exactly that many columns;
       * as the row narrows the floor takes over and a column drops at a time. The half pixel is for rounding,
       * which would otherwise cost the last column its place at exactly the measure.
       *
       * auto-fill and not auto-fit, deliberately: auto-fit collapses the columns nothing is in, so a row holding
       * one quantity would hand it the whole measure — the width this exists to stop.
       *
       * A container, so a child can ask how much room the row has and not how much the window has.
       */
      :host(.cp-field-row--fixed) {
        max-width: var(--cp-field-row-measure, var(--cp-measure-field));
        container-type: inline-size;
        grid-template-columns: repeat(
          auto-fill,
          minmax(
            max(
              min(var(--cp-field-row-min, 14rem), 100%),
              calc((100% - (var(--cp-field-row-columns) - 1) * var(--cp-space-4)) / var(--cp-field-row-columns) - 0.5px)
            ),
            1fr
          )
        );
      }

      /*
       * A field that takes two columns, for a control that needs the room (a unit picker and its Clear
       * button). Only while the row is wide enough to have two to give: in a single-column row a span would
       * make the grid invent a second track and push the row sideways.
       */
      @container (min-width: 20rem) {
        :host(.cp-field-row--fixed) ::ng-deep > .cp-span-2 {
          grid-column: span 2;
        }
      }

      /*
       * A field that takes three, for a short line of text that reads with a quantity beside it (the yield's
       * own wording and the serving count). Wide enough for three tracks, which is when it can have them; below
       * that a row is a phone's or a zoomed window's and the field has the whole row, see cp-stack-narrow.
       */
      @container (min-width: 24rem) {
        :host(.cp-field-row--fixed) ::ng-deep > .cp-span-3 {
          grid-column: span 3;
        }
      }

      /*
       * A child that takes the whole row at every width, for a field that belongs on a line of its own beneath
       * the others (an ingredient line's preparation note).
       */
      :host(.cp-field-row--fixed) ::ng-deep > .cp-span-full {
        grid-column: 1 / -1;
      }

      /*
       * A child that takes the whole row once the row is too narrow for its columns. For the fields of a row
       * that wraps unevenly there — a span-3 field has no three columns to take, and the quantity that
       * follows it would otherwise sit in half a row of its own. Opt-in, because a row of quantities such as
       * Timing is better as two across than as four stacked.
       */
      @container (width < 24rem) {
        :host(.cp-field-row--fixed) ::ng-deep > .cp-stack-narrow {
          grid-column: 1 / -1;
        }
      }

      /*
       * A row too narrow for its columns — a phone, or 200% zoom — keeps its empty columns (see auto-fill
       * above), so a lone field would sit in half the row with nothing beside it and a hint wrapped to fit.
       * There it takes the whole row, the same width as the field above it and the hint under it. With room
       * for the columns it stays one of them, which is the point of the count.
       */
      @container (width < 24rem) {
        :host(.cp-field-row--fixed) ::ng-deep > :only-child {
          grid-column: 1 / -1;
        }
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpFieldRowComponent {
  /** The narrowest a column may get before the row wraps. Widen it for fields that need more room. */
  readonly minColumn = input('14rem');

  /**
   * Holds the row to the form's measure and divides it into this many equal columns. Leave it unset for a row
   * that shares whatever width it is given. A child with the `cp-span-2` or `cp-span-3` class takes that many columns while there is room for them, one with `cp-span-full` takes the whole row, and one with `cp-stack-narrow` takes the whole row below 24rem.
   */
  readonly columns = input<number | null>(null);
}
