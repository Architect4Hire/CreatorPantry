import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CpFormSectionComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ContentPipelineDocumentRef, ContentPipelineDraft, ContentPipelinePromptState } from '../../models/content-pipeline.models';
import { ContentPipelineConceptPanelComponent } from './content-pipeline-concept-panel.component';
import { ContentPipelineDocumentPickerComponent } from './content-pipeline-document-picker.component';
import { ContentPipelinePromptPanelComponent } from './content-pipeline-prompt-panel.component';
import { ContentPipelineReferencePanelComponent } from './content-pipeline-reference-panel.component';

/**
 * Step 3 of the Content Pipeline: from an idea to the words that describe the picture (PIPE-UI-003).
 *
 * **Three sections in the order the work happens** — the look, the extras that refine it, and the prompt — on
 * one surface, because they are one task. A creator picks a shot, optionally attaches a brief or a reference,
 * and leaves with a prompt.
 *
 * **Nothing here renders an image, writes a social post, or files anything in the library.** Those are later
 * steps and separate actions; this step ends with a prompt in the draft.
 *
 * A controlled step, like the two before it: it renders the draft handed in and emits the next one, so the shell
 * stays the single source of truth and what is on screen is always what is kept.
 */
@Component({
  selector: 'cp-content-pipeline-prompt-step',
  standalone: true,
  imports: [
    CpFormSectionComponent,
    CpNoticeComponent,
    ContentPipelineConceptPanelComponent,
    ContentPipelineDocumentPickerComponent,
    ContentPipelinePromptPanelComponent,
    ContentPipelineReferencePanelComponent,
  ],
  templateUrl: './content-pipeline-prompt-step.component.html',
  styleUrl: './content-pipeline-prompt-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelinePromptStepComponent {
  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  readonly changed = output<ContentPipelineDraft>();
  readonly announced = output<string>();

  protected readonly prompt = computed(() => this.draft().prompt);
  protected readonly config = computed(() => this.draft().config);

  /** The prompt section is only worth showing once there is a shot to write one for. */
  protected readonly hasPick = computed(() => this.prompt().chosen !== null);

  protected onPrompt(next: ContentPipelinePromptState): void {
    this.changed.emit({ ...this.draft(), prompt: next });
  }

  protected onBrief(brief: ContentPipelineDocumentRef | null): void {
    this.onPrompt({ ...this.prompt(), brief });
  }

  protected onAnnounced(sentence: string): void {
    this.announced.emit(sentence);
  }
}
