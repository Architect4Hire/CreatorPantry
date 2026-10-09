import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CpBadgeComponent, CpFieldComponent, CpFormSectionComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { FormsModule } from '@angular/forms';

import {
  CONTENT_PIPELINE_BRIEF_SOURCE_LABELS,
  CONTENT_PIPELINE_LIMITS,
  ContentPipelineDocumentRef,
  ContentPipelineDraft,
  ContentPipelinePromptState,
  isContentPipelineBriefEdited,
  linkedRecipeKey,
} from '../../models/content-pipeline.models';
import { LinkedRecipe } from '../../models/creative-context.models';
import { CreativeContextSession } from '../../services/creative-context-session';
import { ContentPipelineConceptPanelComponent } from './content-pipeline-concept-panel.component';
import { ContentPipelineDocumentPickerComponent } from './content-pipeline-document-picker.component';
import { ContentPipelinePromptPanelComponent } from './content-pipeline-prompt-panel.component';
import { ContentPipelineReferencePanelComponent } from './content-pipeline-reference-panel.component';

/**
 * Step 3 of the Content Pipeline: from an idea to the words that describe the picture (PIPE-UI-003).
 *
 * **The brief comes first** (AF.3.2): what the creator chose to plan the picture from, shown and editable at
 * the head of the step, and the one thing the looks below are planned from.
 *
 * **Then three sections in the order the work happens** — the look, the extras that refine it, and the prompt — on
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
    FormsModule,
    CpBadgeComponent,
    CpFieldComponent,
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
  /** The recipe linked to this run, with its pinned version, or null. Handed on to both requests (AF.3.3). */
  readonly recipe = input<LinkedRecipe | null>(null);
  /** The shell's hold on the run's creative context, handed on to the reference panel (AF.3.5). */
  readonly session = input<CreativeContextSession | null>(null);
  readonly changed = output<ContentPipelineDraft>();
  readonly announced = output<string>();

  protected readonly prompt = computed(() => this.draft().prompt);
  protected readonly config = computed(() => this.draft().config);

  /** The brief the creator chose on the step before: what the looks are planned from, and nothing else is. */
  protected readonly brief = computed(() => this.config().brief);

  /** Where that brief came from, in the words the choice was offered in. Null when none was chosen. */
  protected readonly briefSourceLabel = computed(() => {
    const source = this.config().briefSource;

    return source === null ? null : CONTENT_PIPELINE_BRIEF_SOURCE_LABELS[source];
  });

  protected readonly briefEdited = computed(() => isContentPipelineBriefEdited(this.draft()));
  protected readonly briefMaxLength = CONTENT_PIPELINE_LIMITS.conceptMaxLength;

  /** Said beside the box, so a creator is told while writing rather than by a look that cannot be planned. */
  protected readonly briefError = computed(() =>
    this.brief().trim().length > this.briefMaxLength
      ? `That is ${this.brief().trim().length} characters. Keep the brief to ${this.briefMaxLength} or fewer.`
      : '',
  );

  /** The brief is the creator's to change. Changing it never touches their description or the idea. */
  protected onBriefText(brief: string): void {
    const draft = this.draft();
    this.changed.emit({ ...draft, config: { ...draft.config, brief } });
  }

  /**
   * The linked recipe's title, where this step already knows it: the picked idea carries it when it was built
   * around the same recipe. Null otherwise — the recipe is still what the looks are planned around, and the
   * line says so without a name rather than reading the recipe again for one.
   */
  protected readonly recipeTitle = computed(() => {
    const built = this.draft().seed.accepted?.recipe ?? null;

    return built !== null && linkedRecipeKey(built) === linkedRecipeKey(this.recipe()) ? built.title : null;
  });

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
