import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpFormSectionComponent } from '@creator-pantry/ui';

import { RecipeFormState } from './recipe-form-state';

/**
 * The recipe's essentials: title, description, headnote, attribution, source and tags.
 *
 * Draws the fields and writes them to the form the editor provides; it loads and saves nothing itself, so the
 * tabbed page and a hosted step place the same markup.
 */
@Component({
  selector: 'cp-recipe-details-section',
  standalone: true,
  imports: [CpButtonComponent, CpFieldComponent, CpFormSectionComponent, FormsModule],
  templateUrl: './recipe-details-section.component.html',
  styleUrl: './recipe-details-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeDetailsSectionComponent {
  protected readonly form = inject(RecipeFormState);
}
