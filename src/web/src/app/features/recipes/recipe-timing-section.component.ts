import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpFieldComponent, CpFieldRowComponent, CpFormSectionComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { RecipeFormState } from './recipe-form-state';

/**
 * Prep, cook and rest time, and the total they add up to.
 *
 * The total is derived by the form and has no control here; this section only shows it, and says when saving
 * would change the total the recipe is stored with.
 */
@Component({
  selector: 'cp-recipe-timing-section',
  standalone: true,
  imports: [CpFieldComponent, CpFieldRowComponent, CpFormSectionComponent, CpNoticeComponent, FormsModule],
  templateUrl: './recipe-timing-section.component.html',
  styleUrl: './recipe-timing-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeTimingSectionComponent {
  protected readonly form = inject(RecipeFormState);
}
