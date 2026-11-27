import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * A sheet of paper to read down: the surface a recipe's ingredients and method sit on, like an index card
 * pinned to the page.
 *
 * **Paper with a picture-book edge, not a notepad.** A sheet filled with `--cp-surface-subtle` — warm paper in
 * the light theme and a slightly lifted charcoal in the dark one, which stays neutral by design — with a chunky
 * outline in `--cp-border-strong`, a hard un-blurred edge under it, rounded corners, a margin rule down the left
 * and a few punched holes in it, like a page from a ring binder. That is as far as it goes: no tear, texture or
 * handwriting, and the holes are the surface behind the sheet, so they are decoration the creator can read past.
 * The rows on it are ruled by {@link CpRuledListComponent}.
 *
 * **It holds, it does not lay out.** Content is projected and the feature places it — two columns of lists, a
 * notes block under them — so the sheet knows nothing of recipes. It is the surface only, and says nothing to a
 * screen reader: whatever it holds names itself.
 *
 * Every colour is a semantic `--cp-*` token, so it follows the theme with no second set of markup.
 */
@Component({
  selector: 'cp-sheet',
  standalone: true,
  templateUrl: './sheet.component.html',
  styleUrl: './sheet.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpSheetComponent {}
