import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpFieldComponent, CpFormSectionComponent } from '@creator-pantry/ui';

import { RecipeFormState } from './recipe-form-state';

/** The recipe's notes and storage notes, written to the form the editor provides. */
@Component({
  selector: 'cp-recipe-notes-section',
  standalone: true,
  imports: [CpFieldComponent, CpFormSectionComponent, FormsModule],
  templateUrl: './recipe-notes-section.component.html',
  styleUrl: './recipe-notes-section.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeNotesSectionComponent {
  protected readonly form = inject(RecipeFormState);
}
