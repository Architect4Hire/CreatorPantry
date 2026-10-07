import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { AiOperationStatusComponent } from '../ai/ai-operation-status.component';
import { AiOperationTracker } from '../ai/ai-operation-tracker';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { ConfirmService } from '../../core/confirm.service';
import { ContentPipelineDocumentRef, ContentPipelinePromptState } from '../../models/content-pipeline.models';
import {
  REFERENCE_IMAGE_ASPECT_LABELS,
  REFERENCE_IMAGE_CONFIDENCE_LABELS,
  REFERENCE_IMAGE_NOTE_MAX_LENGTH,
  decodeReferenceImageReading,
  referenceReadingWarnings,
} from '../../models/reference-image.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { ContentPipelineDocumentPickerComponent } from './content-pipeline-document-picker.component';
import { IdempotencyKey } from './content-pipeline-idempotency';

/**
 * A photograph the creator already has, read back to them (IMG-004).
 *
 * **Observations and a prompt, not one or the other.** A prompt alone would be a black box: the creator could
 * not tell which parts of their reference had actually been read, and would have no way to correct a misreading
 * except by rewriting the whole thing.
 *
 * **Every observation shows how sure it is**, in words. A reading the model was unsure of, rendered as plainly
 * as one it was sure of, is a misreading waiting to be believed — and `Not stated` says it is unstated rather
 * than quietly reading as certain (.claude/rules/ai.md).
 *
 * **The prompt it draws is an offer, never a replacement.** Taking it replaces the creator's own final prompt,
 * so where there is something there it asks first.
 */
@Component({
  selector: 'cp-content-pipeline-reference-panel',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpFieldComponent,
    CpNoticeComponent,
    AiOperationStatusComponent,
    AiAllowanceNoticeComponent,
    ContentPipelineDocumentPickerComponent,
  ],
  templateUrl: './content-pipeline-reference-panel.component.html',
  styleUrl: './content-pipeline-reference-panel.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineReferencePanelComponent implements OnInit {
  private readonly references = inject(ReferenceImageService);
  private readonly usage = inject(AiUsageService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly prompt = input.required<ContentPipelinePromptState>();
  readonly changed = output<ContentPipelinePromptState>();
  readonly announced = output<string>();

  protected readonly tracker = new AiOperationTracker((requestId) =>
    this.references.watch(this.workspaceSlug(), requestId),
  );

  protected readonly allowance = this.usage.allowance;
  protected readonly noteMaxLength = REFERENCE_IMAGE_NOTE_MAX_LENGTH;
  protected readonly aspectLabels = REFERENCE_IMAGE_ASPECT_LABELS;
  protected readonly confidenceLabels = REFERENCE_IMAGE_CONFIDENCE_LABELS;

  /** Untrusted text, and the creator's own. Not kept in the draft: it describes one asking, not the run. */
  protected readonly note = signal('');

  private readonly idempotency = new IdempotencyKey();

  constructor() {
    this.destroyRef.onDestroy(() => this.tracker.destroy());
  }

  ngOnInit(): void {
    void this.usage.ensureLoaded();

    const requestId = this.prompt().referenceRequestId;
    if (requestId !== null) this.tracker.resume(requestId);
  }

  protected readonly reading = computed(() => decodeReferenceImageReading(this.tracker.proposal()));

  /**
   * What the creator should know about this reading.
   *
   * Read off the proposal, not off the reading, so a warning explaining why nothing could be read is shown
   * instead of being dropped along with it (.claude/rules/ai.md).
   */
  protected readonly warnings = computed(() => referenceReadingWarnings(this.tracker.proposal()));

  /** True when a reading arrived and had nothing in it, which is different from no reading at all. */
  protected readonly readNothing = computed(() => {
    const reading = this.reading();

    return reading !== null && reading.observations.length === 0;
  });

  protected readonly canRead = computed(() => this.prompt().reference !== null && !this.tracker.busy());

  protected readonly quotaRefusal = computed(() => {
    const phase = this.tracker.phase();

    return phase.kind === 'blocked' ? phase.refusal : null;
  });

  protected readonly refusalMessage = computed(() => {
    const phase = this.tracker.phase();
    if (phase.kind !== 'refused') return '';

    const fields = Object.values(phase.fieldErrors).flat();

    return phase.message || fields[0] || 'That reference could not be read.';
  });

  /** Choosing a different reference abandons the reading of the last one, which was about a different picture. */
  protected onReference(reference: ContentPipelineDocumentRef | null): void {
    // A different photograph is a different question, so it takes its own key.
    this.idempotency.clear();
    this.tracker.reset();
    this.changed.emit({ ...this.prompt(), reference, referenceRequestId: null });
  }

  protected setNote(value: string): void {
    this.note.set(value.slice(0, REFERENCE_IMAGE_NOTE_MAX_LENGTH));
  }

  protected async read(): Promise<void> {
    const reference = this.prompt().reference;
    if (reference === null) return;

    const requestId = await this.tracker.submit(() =>
      this.references.request(
        this.workspaceSlug(),
        { referenceDocumentId: reference.documentId, note: this.note() },
        this.idempotency.next(),
      ),
    );

    if (requestId === null) {
      if (this.tracker.phase().kind === 'key_reused') this.idempotency.clear();
      return;
    }

    this.idempotency.clear();
    this.changed.emit({ ...this.prompt(), referenceRequestId: requestId });
    this.announced.emit('Reading your reference photograph.');
  }

  /**
   * Take the prompt this reading drew, as the starting point for the final prompt.
   *
   * Asks first when the creator has words of their own there, because this replaces them
   * (.claude/rules/recipes.md, EASE-005). Accepting marks the prompt as the creator's: it is now what they chose
   * to start from, and a later recomposition has to ask before replacing it too.
   */
  protected async useAsPrompt(): Promise<void> {
    const reading = this.reading();
    if (reading === null) return;

    if (this.prompt().finalPrompt.trim() !== '') {
      const replace = await this.confirm.confirm({
        title: 'Replace the prompt you have?',
        message: 'This will put the wording from your reference in place of the prompt currently written.',
        confirmLabel: 'Replace the prompt',
        cancelLabel: 'Keep the prompt',
      });
      if (!replace) return;
    }

    // Re-read after the dialog rather than reusing a snapshot from before it: the creator could have typed
    // behind a modal, and emitting the stale state would quietly undo that.
    const latest = this.prompt();
    // 'reference' rather than 'creator': they chose this wording, but they did not write it, so it is still
    // labelled as generated and still asks before being replaced.
    this.changed.emit({ ...latest, finalPrompt: reading.prompt, promptSource: 'reference' });
    this.announced.emit('The reference wording is now your prompt.');
  }

  protected stopChecking(): void {
    this.tracker.stopChecking();
  }

  protected resume(): void {
    const requestId = this.tracker.requestId();
    if (requestId !== null) this.tracker.resume(requestId);
  }
}
