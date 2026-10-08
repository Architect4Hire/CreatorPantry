import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { ContentPipelineDraft, ContentPipelineImagesState } from '../../models/content-pipeline.models';
import { avoidTextFor } from '../../models/generated-image.models';
import { GeneratedImageRunComponent } from './generated-image-run.component';

/**
 * Step 4 of the Content Pipeline: from a prompt to the pictures worth keeping (PIPE-UI-004).
 *
 * **The work is `GeneratedImageRunComponent`'s**, which the Image Studio shares: asking, watching, choosing,
 * declining and tidying up are the same on both screens, so they are written once. What is this step's own is
 * where a run's three inputs come from — the prompt the previous step settled, the avoid list that came with
 * its composition, and the count from the first step — and where its state goes.
 *
 * A controlled step, like the three before it: it renders the draft handed in and emits the next one, so the
 * shell stays the single source of truth and what is on screen is always what is kept.
 */
@Component({
  selector: 'cp-content-pipeline-images-step',
  standalone: true,
  imports: [GeneratedImageRunComponent],
  template: `<cp-generated-image-run
    [workspaceSlug]="workspaceSlug()"
    [promptText]="promptText()"
    [avoidText]="avoidText()"
    [variantCount]="draft().config.variantCount"
    [state]="draft().images"
    (changed)="onImages($event)"
    (announced)="announced.emit($event)"
  />`,
  styles: [':host { display: block; }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineImagesStepComponent {
  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  readonly changed = output<ContentPipelineDraft>();
  readonly announced = output<string>();

  /** The prompt as it will be sent: the creator's own words, which is what the previous step settled. */
  protected readonly promptText = computed(() => this.draft().prompt.finalPrompt);

  /**
   * What the request will tell the provider to avoid, or null.
   *
   * It came with the composition the creator was offered, so it is a choice they already saw.
   */
  protected readonly avoidText = computed(() => avoidTextFor(this.draft().prompt.generated?.avoid ?? []));

  protected onImages(images: ContentPipelineImagesState): void {
    this.changed.emit({ ...this.draft(), images });
  }
}
