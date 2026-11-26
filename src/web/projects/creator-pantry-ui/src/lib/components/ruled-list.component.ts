import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * A list read down the page, one row to a line, with a rule under each row like ruled paper: a recipe's
 * ingredients, its steps.
 *
 * An attribute on the consumer's own `<ul>` or `<ol>`, so the list stays a list and every `<li>` stays the
 * consumer's — the words, the order and an icon or a badge inside a row are the feature's. A `<ul>` is a plain
 * run of rows. An `<ol>` numbers them in the margin, in the muted ink and tabular figures so the numbers line up,
 * from a CSS counter rather than the browser's marker, because a marker sits outside the row's box and would
 * start the rule after it instead of under it.
 *
 * **The rule belongs to the row, so the row can wrap.** A long step is as many lines as it needs and the rule
 * is under the last of them; the rule is not a background ruled at a fixed pitch, which would cut through the
 * second line of every step that wrapped. Rows sit on `--cp-border-strong`, which holds on a sheet in both
 * themes; the rule is decoration and the rows read the same without it.
 *
 * `role="list"` for the reason `ul[cpCardGrid]` gives: `list-style: none` takes the role away in Safari.
 */
@Component({
  selector: 'ul[cpRuledList], ol[cpRuledList]',
  standalone: true,
  templateUrl: './ruled-list.component.html',
  styleUrl: './ruled-list.component.scss',
  host: { role: 'list' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpRuledListComponent {}
