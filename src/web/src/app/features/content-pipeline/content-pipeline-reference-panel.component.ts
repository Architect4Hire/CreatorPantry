import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { first } from 'rxjs';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpFieldComponent,
  CpNoticeComponent,
  CpTabDefinition,
  CpTabPanelComponent,
  CpTabsComponent,
} from '@creator-pantry/ui';

import { AiOperationStatusComponent } from '../ai/ai-operation-status.component';
import { AiOperationTracker } from '../ai/ai-operation-tracker';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { ConfirmService } from '../../core/confirm.service';
import { ContentPipelineDocumentRef, ContentPipelinePromptState } from '../../models/content-pipeline.models';
import { pictureReferenceOf } from '../../models/creative-context.models';
import { DamAssetSummary } from '../../models/dam-asset.models';
import { StagedImage } from '../../models/generated-image.models';
import {
  REFERENCE_IMAGE_ASPECT_LABELS,
  REFERENCE_IMAGE_CONFIDENCE_LABELS,
  REFERENCE_IMAGE_NOTE_MAX_LENGTH,
  ReferenceImagePicture,
  decodeReferenceImageReading,
  referenceReadingWarnings,
} from '../../models/reference-image.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { CreativeContextReferenceOutcome, CreativeContextSession } from '../../services/creative-context-session';
import { GeneratedImageService } from '../../services/generated-image.service';
import { DamAssetPickerComponent } from '../dam/dam-asset-picker.component';
import { DamLinkedAssetComponent } from '../dam/dam-linked-asset.component';
import { ContentPipelineStagedImageComponent } from './content-pipeline-staged-image.component';
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
const CHOOSE_FAILURES: Readonly<Record<Exclude<CreativeContextReferenceOutcome, 'saved' | 'duplicate'>, string>> = {
  source_unavailable: 'This picture can no longer be chosen. It may have been removed.',
  limit: 'This piece of work already names as many sources as it can. Remove one to choose a picture.',
  forbidden: 'You do not have permission to change this piece of work.',
  held: 'This could not be done yet. Sort out the notice about your unsaved changes below, then try again.',
  unavailable: "This couldn't be done right now. Nothing was changed.",
};

@Component({
  selector: 'cp-content-pipeline-reference-panel',
  standalone: true,
  imports: [
    FormsModule,
    NgTemplateOutlet,
    CpBadgeComponent,
    CpButtonComponent,
    CpFieldComponent,
    CpNoticeComponent,
    CpTabsComponent,
    CpTabPanelComponent,
    AiOperationStatusComponent,
    AiAllowanceNoticeComponent,
    ContentPipelineDocumentPickerComponent,
    ContentPipelineStagedImageComponent,
    DamAssetPickerComponent,
    DamLinkedAssetComponent,
  ],
  templateUrl: './content-pipeline-reference-panel.component.html',
  styleUrl: './content-pipeline-reference-panel.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineReferencePanelComponent implements OnInit {
  private readonly references = inject(ReferenceImageService);
  private readonly generated = inject(GeneratedImageService);
  private readonly usage = inject(AiUsageService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly workspaceSlug = input.required<string>();
  readonly prompt = input.required<ContentPipelinePromptState>();
  /**
   * The surface's hold on the work's creative context, where a library or generated picture chosen here is
   * stored (AF.3.5). Without one the panel offers the Brand Library alone, as it did before there were three
   * places to choose from.
   */
  readonly session = input<CreativeContextSession | null>(null);
  /**
   * The level this panel's own headings sit at: one below whatever heading its host put above it. The
   * pipeline's step heads it at level four and Image Studio at three, so one fixed level here would repeat
   * the host's on one surface or skip a level on the other.
   */
  readonly headingLevel = input(5);
  /** The run of pictures this work has made, if it has made one: where "this run's pictures" come from. */
  readonly runOperationId = input<string | null>(null);
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

  /** The three places a reference picture can come from, as tabs. */
  protected readonly sourceTabs: CpTabDefinition[] = [
    { id: 'library', label: 'My library' },
    { id: 'run', label: "This run's pictures" },
    { id: 'brand', label: 'Brand Library' },
  ];
  protected readonly sourceTab = signal('library');

  /** What choosing or removing a picture is doing, and what went wrong when it did. */
  protected readonly choosing = signal<
    | { readonly status: 'idle' }
    | { readonly status: 'working'; readonly sentence: string }
    | { readonly status: 'failed'; readonly sentence: string; readonly retry: () => void }
  >({ status: 'idle' });

  /** The run's pictures a creator can still choose from: staged or kept, never declined or expired. */
  protected readonly runPictures = signal<
    | { readonly status: 'none' }
    | { readonly status: 'loading' }
    | { readonly status: 'found'; readonly images: readonly StagedImage[] }
    | { readonly status: 'unavailable' }
  >({ status: 'none' });

  /** True once the chosen library picture has been found to be gone, so no reading is offered of it. */
  protected readonly pictureGone = signal(false);

  /** The context's reference picture: a library asset or a generated image, or null. */
  protected readonly contextPicture = computed(() => pictureReferenceOf(this.session()?.context() ?? null));

  /**
   * The one picture this work takes its cues from, wherever it lives.
   *
   * One at a time. A library or generated picture is on the context; a Brand Library document has no kind of
   * reference a context can hold, so it is on the prompt state as it always was. Choosing either lets go of
   * the other, so there is never a question of which one a reading is of.
   */
  protected readonly picture = computed<ReferenceImagePicture | null>(() => {
    const onContext = this.contextPicture();

    if (onContext?.kind === 'DamAsset' && onContext.mediaAssetId !== null) {
      return {
        source: 'DamAsset',
        mediaAssetId: onContext.mediaAssetId,
        mediaAssetVersionNumber: onContext.mediaAssetVersionNumber,
      };
    }
    if (onContext?.kind === 'GeneratedImage' && onContext.generatedImageId !== null) {
      return { source: 'GeneratedImage', generatedImageId: onContext.generatedImageId };
    }

    const document = this.prompt().reference;

    return document === null ? null : { source: 'BrandDocument', referenceDocumentId: document.documentId };
  });

  /** The chosen generated picture, when it is one of this run's — which is what gives it a thumbnail. */
  protected readonly chosenRunPicture = computed(() => {
    const picture = this.picture();
    const run = this.runPictures();
    if (picture?.source !== 'GeneratedImage' || run.status !== 'found') return null;

    const index = run.images.findIndex((image) => image.id === picture.generatedImageId);

    return index === -1 ? null : { image: run.images[index], position: index + 1, total: run.images.length };
  });

  protected readonly busyChoosing = computed(() => this.choosing().status === 'working');

  /** The run whose pictures are on screen or being read, so the same one is not asked for twice. */
  private runLoadedFor: string | null = null;

  private readonly idempotency = new IdempotencyKey();

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.gone = true;
      this.tracker.destroy();
    });

    // The run's pictures are read when they are about to be looked at — its tab is open, or one of them is
    // the chosen picture — and again when it is a different run. Not before: the run is already being watched
    // by the part of the page that makes pictures, and a panel nobody has opened has no reason to ask too.
    effect(() => {
      const operationId = this.runOperationId();
      const slug = this.workspaceSlug();
      const picture = this.picture();
      const wanted = picture === null ? this.sourceTab() === 'run' && this.session() !== null : picture.source === 'GeneratedImage';
      const key = operationId === null || !wanted ? null : `${slug}|${operationId}`;
      if (key === this.runLoadedFor) return;

      this.runLoadedFor = key;
      untracked(() => this.loadRunPictures());
    });

    // A different picture is a different question about whether it is still there.
    effect(() => {
      this.picture();
      untracked(() => this.pictureGone.set(false));
    });
  }

  /** True once the page has let go of this panel, so an answer still on its way changes nothing. */
  private gone = false;

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

  protected readonly canRead = computed(
    () => this.picture() !== null && !this.pictureGone() && !this.tracker.busy() && !this.busyChoosing(),
  );

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
  /** A Brand Library document chosen, or removed, in its own picker. */
  protected async onReference(reference: ContentPipelineDocumentRef | null): Promise<void> {
    // One picture at a time: a library or generated picture already chosen is let go of first.
    if (reference !== null && !(await this.releaseContextPicture(() => void this.onReference(reference)))) return;

    this.forgetReading();
    this.changed.emit({ ...this.prompt(), reference, referenceRequestId: null });
  }

  /** A picture chosen from the library: stored on the context, pinned to the version it is at now. */
  protected chooseAsset(asset: DamAssetSummary): void {
    void this.chooseOnContext(
      { kind: 'DamAsset', mediaAssetId: asset.id, mediaAssetVersionNumber: asset.currentVersionNumber },
      `Choosing ${asset.title}…`,
      `${asset.title} is your reference picture.`,
    );
  }

  /** One of this run's own pictures, chosen as the reference for the next. */
  protected chooseRunPicture(image: StagedImage, position: number): void {
    void this.chooseOnContext(
      { kind: 'GeneratedImage', generatedImageId: image.id },
      `Choosing picture ${position}…`,
      `Picture ${position} from this run is your reference picture.`,
    );
  }

  /**
   * Stop using the chosen library or generated picture as the reference.
   *
   * Only the link goes. The picture is exactly where it was — in the library, or in the run — which is why
   * this does not ask.
   */
  protected async removePicture(): Promise<void> {
    if (!(await this.releaseContextPicture(() => void this.removePicture()))) return;

    this.forgetReading();
    this.changed.emit({ ...this.prompt(), referenceRequestId: null });
    this.announced.emit('The reference picture is removed.');
    // The button that was pressed is gone with the summary, so focus goes to the chooser that replaced it.
    this.focusAfterRender('[role="tab"][aria-selected="true"]');
  }

  /**
   * Move focus once the screen has caught up with a change that removed the control that was pressed.
   *
   * Without it a keyboard or screen-reader user is left on nothing, and has to find the part of the page that
   * just changed by hand.
   */
  private focusAfterRender(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), {
      injector: this.injector,
    });
  }

  /**
   * Put the reading aside before it shapes anything.
   *
   * Nothing was taken from a reading until the creator pressed the button that takes it, so discarding one
   * loses only what is on screen: the picture stays chosen, and can be read again.
   */
  protected discardReading(): void {
    this.forgetReading();
    this.changed.emit({ ...this.prompt(), referenceRequestId: null });
    this.announced.emit('The reading is discarded. Nothing was taken from it.');
  }

  protected onAssetResolved(asset: unknown): void {
    // The linked-asset card says so itself; this is only what stops a reading being offered of nothing.
    this.pictureGone.set(asset === null);
  }

  protected reloadRunPictures(): void {
    this.loadRunPictures();
  }

  /** True when this work has made pictures at all, whether or not they have been read here yet. */
  protected readonly hasRun = computed(() => this.runOperationId() !== null);

  private async chooseOnContext(
    source:
      | { readonly kind: 'DamAsset'; readonly mediaAssetId: string; readonly mediaAssetVersionNumber: number }
      | { readonly kind: 'GeneratedImage'; readonly generatedImageId: string },
    working: string,
    done: string,
  ): Promise<void> {
    const session = this.session();
    if (session === null || this.busyChoosing() || this.tracker.busy()) return;

    const retry = (): void => void this.chooseOnContext(source, working, done);

    this.choosing.set({ status: 'working', sentence: working });

    // One picture at a time: whatever is chosen now is let go of first, wherever it lives.
    if (!(await this.releaseContextPicture(retry))) return;
    this.choosing.set({ status: 'working', sentence: working });

    const outcome = await session.addReference(source);
    if (this.gone) return;

    if (outcome !== 'saved' && outcome !== 'duplicate') {
      this.choosing.set({ status: 'failed', sentence: CHOOSE_FAILURES[outcome], retry });
      return;
    }

    this.choosing.set({ status: 'idle' });
    this.forgetReading();
    // The Brand Library picture, if there was one, goes with it: the reading is of one picture.
    this.changed.emit({ ...this.prompt(), reference: null, referenceRequestId: null });
    this.announced.emit(done);
    // The Choose button went with the chooser, so focus goes to what it turned into.
    this.focusAfterRender('.chosen');
  }

  /** Lets go of the context's reference picture, if it has one. False when that could not be done. */
  private async releaseContextPicture(retry: () => void): Promise<boolean> {
    const session = this.session();
    const existing = this.contextPicture();
    if (session === null || existing === null) return true;

    this.choosing.set({ status: 'working', sentence: 'Removing the picture…' });
    const outcome = await session.removeReference(existing.id);
    if (this.gone) return false;

    if (outcome !== 'saved') {
      this.choosing.set({
        status: 'failed',
        sentence: CHOOSE_FAILURES[outcome === 'duplicate' ? 'unavailable' : outcome],
        retry,
      });
      return false;
    }

    this.choosing.set({ status: 'idle' });

    return true;
  }

  /** A different picture is a different question, so whatever was asked of the last one is let go of. */
  private forgetReading(): void {
    this.idempotency.clear();
    this.tracker.reset();
  }

  private loadRunPictures(): void {
    const operationId = this.runOperationId();
    const slug = this.workspaceSlug();
    const key = this.runLoadedFor;

    if (operationId === null || key === null) {
      this.runPictures.set({ status: 'none' });
      return;
    }

    this.runPictures.set({ status: 'loading' });

    // One answer is enough: this lists what there is to choose from, it does not follow the run.
    this.generated
      .watch(slug, operationId)
      .pipe(first(), takeUntilDestroyed(this.destroyRef))
      .subscribe((outcome) => {
        if (key !== this.runLoadedFor) return;

        this.runPictures.set(
          outcome.status === 'found'
            ? {
                status: 'found',
                // Declined and expired pictures are no longer the creator's to look at, so not theirs to choose.
                images: outcome.operation.images.filter((image) => image.status === 'Staged' || image.status === 'Kept'),
              }
            : outcome.status === 'not_found'
              ? { status: 'found', images: [] }
              : { status: 'unavailable' },
        );
      });
  }

  protected setNote(value: string): void {
    this.note.set(value.slice(0, REFERENCE_IMAGE_NOTE_MAX_LENGTH));
  }

  protected async read(): Promise<void> {
    const picture = this.picture();
    if (picture === null || this.pictureGone()) return;

    const requestId = await this.tracker.submit(() =>
      this.references.request(this.workspaceSlug(), { picture, note: this.note() }, this.idempotency.next()),
    );

    if (requestId === null) {
      if (this.tracker.phase().kind === 'key_reused') this.idempotency.clear();
      return;
    }

    this.idempotency.clear();
    this.changed.emit({ ...this.prompt(), referenceRequestId: requestId });
    this.announced.emit('Reading your reference picture.');
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
