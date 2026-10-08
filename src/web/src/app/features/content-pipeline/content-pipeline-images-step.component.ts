import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { CpButtonComponent, CpFormSectionComponent, CpNoticeComponent, CpProgressComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_PIPELINE_LIMITS,
  ContentPipelineDraft,
  clampVariantCount,
  keepersStillPresent,
} from '../../models/content-pipeline.models';
import {
  StagedImage,
  avoidTextFor,
  generatedImageFailureSentence,
  isStagedImageActionable,
} from '../../models/generated-image.models';
import { GeneratedImageService, StagedImageRejectOutcome } from '../../services/generated-image.service';
import { IdempotencyKey } from './content-pipeline-idempotency';
import { ContentPipelineImageGridComponent } from './content-pipeline-image-grid.component';
import { ContentPipelineImageLightboxComponent } from './content-pipeline-image-lightbox.component';
import {
  GENERATED_IMAGE_RUN_DETAIL,
  GENERATED_IMAGE_RUN_LABELS,
  GENERATED_IMAGE_RUN_TONES,
  retentionDeadlineText,
} from './content-pipeline-image-presentation';
import { GeneratedImageTracker } from './generated-image-tracker';

/** "One picture" or "3 pictures" — the count a sentence can be built around. */
function pictureCount(count: number): string {
  return count === 1 ? 'One picture' : `${count} pictures`;
}

/**
 * Step 4 of the Content Pipeline: from a prompt to the pictures worth keeping (PIPE-UI-004).
 *
 * **Two sections in the order the work happens** — asking for the pictures, then choosing among them. The
 * second appears once there is something in it, because an empty contact sheet is not a thing to choose from.
 *
 * **Keeping a picture here is a mark on the draft, not a filing.** `GeneratedImageStatus.Kept` only ever
 * arrives from the library commit, which is a later step and a separate action, so a kept picture is still
 * `Staged` server-side and retention will collect it on its own schedule. The step says so rather than letting
 * a tick imply something was put away safely.
 *
 * **Nothing here cancels.** The contract offers asking, reading, looking, downloading and declining; a control
 * claiming to cancel a generation would promise what the server cannot honour, so "Stop checking" says what it
 * actually does.
 *
 * A controlled step, like the three before it: it renders the draft handed in and emits the next one, so the
 * shell stays the single source of truth and what is on screen is always what is kept.
 */
@Component({
  selector: 'cp-content-pipeline-images-step',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpProgressComponent,
    CpStatusPillComponent,
    ContentPipelineImageGridComponent,
    ContentPipelineImageLightboxComponent,
  ],
  templateUrl: './content-pipeline-images-step.component.html',
  styleUrl: './content-pipeline-images-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineImagesStepComponent {
  private readonly service = inject(GeneratedImageService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  readonly changed = output<ContentPipelineDraft>();
  readonly announced = output<string>();

  protected readonly tracker = new GeneratedImageTracker((operationId) =>
    this.service.watch(this.workspaceSlug(), operationId),
  );

  private readonly idempotency = new IdempotencyKey();

  /** The picture open in the lightbox, or null. One place decides, so the grid and the dialog cannot disagree. */
  protected readonly lightboxId = signal<string | null>(null);

  /** True while a decline or a tidy-up is in flight, so the same picture cannot be acted on twice. */
  protected readonly working = signal(false);

  /** What the last decline or tidy-up did, in the creator's own terms. Cleared when they ask for anything else. */
  protected readonly report = signal('');

  protected readonly runLabels = GENERATED_IMAGE_RUN_LABELS;
  protected readonly runTones = GENERATED_IMAGE_RUN_TONES;
  protected readonly runDetail = GENERATED_IMAGE_RUN_DETAIL;

  /** The last phase announced, so one transition is announced once rather than on every change detection. */
  private announcedPhase = '';

  /**
   * The workspace and run this tracker is following, so the same one is not picked up twice.
   *
   * It is also what keeps this step correct **on its own** rather than by its caller's good manners. The shell
   * keys the step on the owner, so a change of workspace or of who is signed in destroys it; but the tracker's
   * poll reads the slug input on every request, so a step that was *reused* instead of rebuilt would ask the
   * new workspace for the old workspace's run. Nothing may depend on one caller for isolation
   * (.claude/rules/tenancy.md).
   */
  private followedKey: string | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => this.tracker.destroy());

    // Follow whatever run the draft names, for whichever workspace the step is pointed at. One effect rather
    // than an `ngOnInit`, so arriving with a kept run, a change of workspace and a change of run are one path.
    effect(() => {
      const key = `${this.workspaceSlug()}|${this.draft().images.operationId ?? ''}`;
      if (key === this.followedKey) return;

      this.followedKey = key;
      this.follow();
    });

    // A keeper the run can no longer account for is dropped. Retention collects staged bytes on its own
    // schedule, so coming back a day later is an ordinary way to reach this — and carrying the mark would gate
    // Continue on a picture that is not there.
    effect(() => {
      const detail = this.tracker.detail();
      if (detail === null) return;

      const images = this.draft().images;
      const live = keepersStillPresent(images.keepers, detail.images);
      if (live.length === images.keepers.length) return;

      this.emitImages({ ...images, keepers: live });
    });

    // A run that is no longer there cannot account for anything, and its marks would otherwise let the step
    // carry on with nothing on screen — the pruning above only runs when a reading arrives, and for a run
    // that is gone none ever will. The run id stays: it is the truthful answer to "which run was that".
    effect(() => {
      if (this.tracker.phase().kind !== 'gone') return;

      const images = this.draft().images;
      if (images.keepers.length === 0) return;

      this.emitImages({ ...images, keepers: [] });
    });

    // Announced rather than left to be noticed: a creator who looked away while pictures were being made is
    // told when they arrive, or when the run stopped without any.
    effect(() => {
      const phase = this.tracker.phase();
      if (phase.kind === this.announcedPhase) return;

      this.announcedPhase = phase.kind;

      if (phase.kind === 'ready') {
        this.announced.emit(`${pictureCount(phase.operation.images.length)} ready to choose from.`);
      } else if (phase.kind === 'failed') {
        this.announced.emit('Your pictures could not be made. The reason is on the step.');
      }
    });
  }

  /** Pick up the run the draft names, or let go of the one it no longer does. */
  private follow(): void {
    const operationId = this.draft().images.operationId;

    if (operationId === null) this.tracker.reset();
    else this.tracker.resume(operationId);
  }

  protected readonly imagesState = computed(() => this.draft().images);

  /** The prompt as it will be sent: the creator's own words, which is what the previous step settled. */
  protected readonly promptText = computed(() => this.draft().prompt.finalPrompt.trim());

  /**
   * What the request will tell the provider to avoid, or null.
   *
   * Shown in the recap as well as sent, so the creator can see the whole of what goes off before they press
   * anything. It came with the composition they were offered, so it is a choice they already saw.
   */
  protected readonly avoidText = computed(() => avoidTextFor(this.draft().prompt.generated?.avoid ?? []));

  protected readonly variantCount = computed(() => clampVariantCount(this.draft().config.variantCount));

  protected readonly promptTooLong = computed(
    () => this.promptText().length > CONTENT_PIPELINE_LIMITS.promptMaxLength,
  );

  protected readonly promptLengthText = computed(
    () => `${this.promptText().length} of ${CONTENT_PIPELINE_LIMITS.promptMaxLength} characters`,
  );

  /** Refused here rather than by the server, so the creator is told what to fix and where. */
  protected readonly canGenerate = computed(
    () => this.promptText() !== '' && !this.promptTooLong() && !this.tracker.busy() && !this.working(),
  );

  protected readonly detail = this.tracker.detail;

  protected readonly stagedImages = computed<readonly StagedImage[]>(() => this.detail()?.images ?? []);

  protected readonly hasPictures = computed(() => this.stagedImages().length > 0);

  protected readonly keeperCount = computed(() => this.imagesState().keepers.length);

  /** The pictures a tidy-up would decline: still staged, and not marked. */
  protected readonly nonKeepers = computed(() => {
    const keepers = this.imagesState().keepers;

    return this.stagedImages().filter(
      (image) => isStagedImageActionable(image.status) && !keepers.includes(image.id),
    );
  });

  protected readonly canTidyUp = computed(() => this.nonKeepers().length > 0 && !this.working());

  /** How far through the run is, as the count rather than a bare percentage. */
  protected readonly progressPercent = computed(() => {
    const detail = this.detail();
    if (detail === null || detail.variantCount <= 0) return 0;

    return Math.min(100, Math.round((detail.stagedCount / detail.variantCount) * 100));
  });

  protected readonly progressText = computed(() => {
    const detail = this.detail();
    if (detail === null) return '';

    return `${detail.stagedCount} of ${detail.variantCount} ready`;
  });

  /** True while the run is still working, which is when a progress figure means anything. */
  protected readonly isWatching = computed(() => this.tracker.phase().kind === 'watching');

  protected readonly failureSentence = computed(() => {
    const phase = this.tracker.phase();
    if (phase.kind !== 'failed') return '';

    return generatedImageFailureSentence(phase.operation.failureCategory, phase.operation.failureSummary);
  });

  /** What made the pictures. Provenance a creator deciding whether to publish one is entitled to see. */
  protected readonly provenance = computed(() => {
    const detail = this.detail();
    if (detail === null || detail.providerName === null) return '';

    return detail.modelName === null ? detail.providerName : `${detail.providerName}, ${detail.modelName}`;
  });

  /** When the staged pictures stop being available. Empty when there is nothing with a readable deadline. */
  protected readonly retentionText = computed(() => retentionDeadlineText(this.stagedImages()));

  /** The run asked for more than it produced, which is `PartiallySucceeded` and worth saying plainly. */
  protected readonly shortfall = computed(() => {
    const detail = this.detail();
    if (detail === null || detail.status !== 'PartiallySucceeded') return '';

    return `${pictureCount(detail.images.length)} of the ${detail.variantCount} asked for arrived.`;
  });

  protected readonly refusalMessage = computed(() => {
    const phase = this.tracker.phase();
    if (phase.kind !== 'refused') return '';

    const fields = Object.values(phase.fieldErrors).flat();

    return phase.message || fields[0] || 'The pictures could not be asked for.';
  });

  /**
   * True when this screen has no reading but the draft remembers a run.
   *
   * It is how a failed ask gets out of the way: the previous pictures are still there server-side, so the
   * creator is offered them back rather than left looking at an error with nothing behind it.
   */
  protected readonly canShowLastRun = computed(() => {
    const kind = this.tracker.phase().kind;
    const stranded = kind === 'unavailable' || kind === 'rate_limited' || kind === 'refused' || kind === 'idle';

    return stranded && this.imagesState().operationId !== null;
  });

  /** Ask for the pictures — or ask again for the same ones, after an answer that never arrived. */
  protected async generate(): Promise<void> {
    if (!this.canGenerate()) return;

    this.report.set('');

    const operationId = await this.tracker.submit(() =>
      this.service.request(
        this.workspaceSlug(),
        {
          promptText: this.promptText(),
          avoidText: this.avoidText(),
          variantCount: this.variantCount(),
        },
        // Reused across a retry on purpose: an ask that answered `unavailable` may have reached the server
        // anyway, and a fresh key would then buy a second set of pictures.
        this.idempotency.next(),
      ),
    );

    if (operationId === null) return;

    this.idempotency.clear();
    // Claimed before the draft carries it, so the effect above recognises this as the run already being
    // followed rather than resuming it a second time on top of the poll `submit` just started.
    this.followedKey = `${this.workspaceSlug()}|${operationId}`;
    // The run and the marks move together: keepers from the previous run name pictures this one does not have.
    this.emitImages({ operationId, keepers: [] });
    this.announced.emit(`Making ${this.variantCount() === 1 ? 'one picture' : this.variantCount() + ' pictures'}.`);
  }

  /**
   * A deliberately different question, so it takes a fresh key rather than replaying the last answer.
   *
   * Asks first when there are pictures on screen, because they are replaced and the marks go with them —
   * and a creator who pressed this by reflex should be able to download one first.
   */
  protected async generateFresh(): Promise<void> {
    if (!this.canGenerate()) return;

    if (this.hasPictures()) {
      const replace = await this.confirm.confirm({
        title: 'Make a new set of pictures?',
        message:
          'The pictures below are replaced by a new set, and anything you have kept here stops being kept. Download or tidy up first if you want to.',
        confirmLabel: 'Make new pictures',
        cancelLabel: 'Keep these',
        tone: 'neutral',
      });
      if (!replace) return;
    }

    this.idempotency.clear();
    await this.generate();
  }

  protected showLastRun(): void {
    const operationId = this.imagesState().operationId;
    if (operationId !== null) this.tracker.resume(operationId);
  }

  protected stopChecking(): void {
    this.tracker.stopChecking();
  }

  protected checkAgain(): void {
    this.tracker.reread();
  }

  protected onKeepToggled(change: { readonly id: string; readonly keep: boolean }): void {
    const images = this.imagesState();
    const keepers = change.keep
      ? Array.from(new Set([...images.keepers, change.id]))
      : images.keepers.filter((id) => id !== change.id);

    this.emitImages({ ...images, keepers });
    this.announced.emit(change.keep ? 'Kept.' : 'No longer kept.');
  }

  protected onOpened(id: string): void {
    this.lightboxId.set(id);
  }

  protected onLightboxClosed(): void {
    this.lightboxId.set(null);
  }

  /** Decline one picture. It asks, because the picture goes and a download is the only way to keep a copy. */
  protected async decline(id: string): Promise<void> {
    if (this.working()) return;

    const confirmed = await this.confirm.confirm({
      title: 'Decline this picture?',
      message: 'It will be removed and cannot be brought back. If you want a copy, download it first.',
      confirmLabel: 'Decline it',
      cancelLabel: 'Keep it for now',
    });
    if (!confirmed) return;

    this.working.set(true);
    const outcome = await this.service.reject(this.workspaceSlug(), id);
    this.working.set(false);

    // Un-marked whatever the outcome: a picture the server would not let us decline is still one the creator
    // has said they do not want, and leaving the mark on would carry it into the next step.
    this.unkeep(id);
    if (this.lightboxId() === id) this.lightboxId.set(null);

    const sentence = declineSentence(outcome);
    this.report.set(sentence);
    this.announced.emit(sentence);
    this.tracker.reread();
  }

  /**
   * Decline everything the creator did not keep.
   *
   * Sequential rather than four at once: the report says what happened to each picture, and a burst of deletes
   * is also the shape the edge's rate limiter exists to turn away. Each call is idempotent — the route answers
   * `204` whether this call declined the picture or an earlier one already had — so a half-finished tidy-up can
   * simply be run again.
   */
  protected async tidyUp(): Promise<void> {
    const targets = this.nonKeepers();
    if (targets.length === 0 || this.working()) return;

    const confirmed = await this.confirm.confirm({
      title: targets.length === 1 ? 'Tidy away one picture?' : `Tidy away ${targets.length} pictures?`,
      message:
        'The pictures you have not kept will be removed and cannot be brought back. Download any you want a copy of first.',
      confirmLabel: 'Tidy them away',
      cancelLabel: 'Leave them',
    });
    if (!confirmed) return;

    this.working.set(true);

    let declined = 0;
    const problems: string[] = [];

    for (const image of targets) {
      const outcome = await this.service.reject(this.workspaceSlug(), image.id);
      if (outcome.status === 'declined') declined += 1;
      else problems.push(declineSentence(outcome));
    }

    this.working.set(false);

    const parts: string[] = [];
    if (declined > 0) parts.push(`${pictureCount(declined)} ${declined === 1 ? 'was' : 'were'} declined.`);
    // The distinct reasons rather than one per picture: three identical sentences say nothing the first did not.
    parts.push(...Array.from(new Set(problems)));

    const sentence = parts.join(' ');
    this.report.set(sentence);
    this.announced.emit(sentence);
    this.tracker.reread();
  }

  private unkeep(id: string): void {
    const images = this.imagesState();
    if (!images.keepers.includes(id)) return;

    this.emitImages({ ...images, keepers: images.keepers.filter((keeper) => keeper !== id) });
  }

  private emitImages(images: ContentPipelineDraft['images']): void {
    this.changed.emit({ ...this.draft(), images });
  }
}

/** What one decline did, in a sentence. Every outcome has one, so nothing fails silently. */
function declineSentence(outcome: StagedImageRejectOutcome): string {
  switch (outcome.status) {
    case 'declined':
      return 'Declined.';
    case 'conflict':
      // Kept or expired — every state but staged is terminal, and neither is something to retry.
      return 'That one could not be declined, because it is no longer waiting on a decision.';
    case 'not_found':
      return 'That one had already gone.';
    case 'forbidden':
      return 'You do not have permission to decline pictures in this workspace.';
    default:
      return 'That one could not be declined just now. Trying again usually works.';
  }
}
