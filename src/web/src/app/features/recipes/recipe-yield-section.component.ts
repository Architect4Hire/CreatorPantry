import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpComboboxComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { RecipeFormState } from './recipe-form-state';

/**
 * The recipe's yield: the creator's own wording, how many it serves, and — when asked for — a measured batch.
 *
 * The three are separate facts (recipes.md) and the form keeps them separate. The measured-batch fields are
 * offered rather than presented, so showing and taking them away, and the focus that follows, are this
 * section's; the values and what is left of them are the form's.
 */
@Component({
  selector: 'cp-recipe-yield-section',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpComboboxComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    FormsModule,
  ],
  templateUrl: './recipe-yield-section.component.html',
  styleUrl: './recipe-yield-section.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeYieldSectionComponent {
  protected readonly form = inject(RecipeFormState);
  private readonly confirmService = inject(ConfirmService);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);

  /**
   * The creator asked for the measured-batch fields: show them and put the cursor in the first.
   *
   * Focus moves because the button has just replaced itself with the fields it asked for, and leaving focus on
   * it would strand a keyboard user. Writes no value, so the recipe is untouched and the form does not become
   * dirty.
   */
  revealMeasuredBatch(): void {
    this.form.measuredBatchRevealed.set(true);

    afterNextRender(() => this.elementRef.nativeElement.querySelector<HTMLElement>('#recipe-yield-quantity')?.focus(), {
      injector: this.injector,
    });
  }

  /**
   * Takes the measured-batch fields away again, clearing what they held.
   *
   * They were offered rather than presented, and once on screen they stay (see
   * {@link RecipeFormState.measuredBatchRevealed}) so that a box does not vanish while it is being edited. That
   * left no way back for a creator who opened them by mistake or no longer wants a measured batch, so this is
   * the deliberate way out: batch yield, yield unit and serving size go, and so do any refusals the server named
   * on them. The creator's own wording and the serving count are not part of it.
   *
   * Asked first only when there is something to lose. Nothing is saved until Save, so this is recoverable until
   * then by leaving without saving, but typing a batch in and losing it to a click is what a confirmation is for.
   */
  async removeMeasuredBatch(): Promise<void> {
    if (this.form.holdsMeasuredBatchValues()) {
      const confirmed = await this.confirmService.confirm({
        title: 'Remove the measured batch?',
        message:
          'This clears the batch yield, the yield unit and the serving size. Your own yield wording and the serving count stay as they are. Nothing is saved until you select Save.',
        confirmLabel: 'Remove measured batch',
        cancelLabel: 'Keep it',
      });
      if (!confirmed) return;
    }

    this.form.clearMeasuredBatch();

    // The button that removed them is gone with them, so focus goes to the one that offers them back.
    afterNextRender(
      () => this.elementRef.nativeElement.querySelector<HTMLElement>('#recipe-add-measured-batch')?.focus(),
      { injector: this.injector },
    );
  }
}
