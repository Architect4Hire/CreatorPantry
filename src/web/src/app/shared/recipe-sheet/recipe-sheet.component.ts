import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import {
  CpBadgeComponent,
  CpIconComponent,
  CpRuledListComponent,
  CpSheetComponent,
  CpStatusPillComponent,
} from '@creator-pantry/ui';

import { RecipeSheetGroup } from './recipe-sheet.model';

let nextSheetId = 0;

/**
 * A recipe's ingredients and its method, read down a sheet: the one way every page shows them.
 *
 * **One component, every page that reads a recipe.** The Quick Recipe review and the "Create a recipe"
 * workflow's draft preview each drew this part for themselves, and had already drifted — bullets on one and
 * numbers on the other, a note in one place and not the other. They now give this the same two lists and it is
 * drawn once: the ingredients beside the method where there is room for both, one above the other where there is
 * not, on a `cp-sheet`, each row ruled by `cpRuledList`, the method numbered in the margin.
 *
 * **It shows, it does not edit.** It takes {@link RecipeSheetGroup}s — a view model, never a draft — so it knows
 * nothing of where the words came from, and a page maps its own model with `ingredientSheetGroups` and
 * `stepSheetGroups`. A page where each line is edited in place is a form, and does not use this.
 *
 * **Nothing is rewritten.** A line is shown as given. One that still carries the model's own question where its
 * value belongs is marked with a warning icon, which says nothing the words do not.
 *
 * Laid out by its own width, not the window's, so it is right in a narrow card as well as a wide one.
 */
@Component({
  selector: 'cp-recipe-sheet',
  standalone: true,
  imports: [CpBadgeComponent, CpIconComponent, CpRuledListComponent, CpSheetComponent, CpStatusPillComponent],
  templateUrl: './recipe-sheet.component.html',
  styleUrl: './recipe-sheet.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeSheetComponent {
  readonly ingredients = input.required<readonly RecipeSheetGroup[]>();
  readonly steps = input.required<readonly RecipeSheetGroup[]>();
  /** The recipe's own notes, kept as written; none, no part. */
  readonly notes = input<string | null>(null);

  /** What the part says when there is nothing in it, so a missing list is said rather than left blank. */
  readonly emptyIngredients = input('No ingredients came back.');
  readonly emptyMethod = input('No method came back.');

  /**
   * The headings' ids, which a page can point to — a link that scrolls to the method, a section nav. Each
   * heading takes focus from script (`tabindex="-1"`), so arriving there lands on its name. Unique per sheet by
   * default.
   */
  readonly ingredientsId = input(`cp-recipe-sheet-${nextSheetId}-ingredients`);
  readonly methodId = input(`cp-recipe-sheet-${nextSheetId}-method`);
  protected readonly notesId = `cp-recipe-sheet-${nextSheetId++}-notes`;
}
