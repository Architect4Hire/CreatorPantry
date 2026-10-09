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
  untracked,
} from '@angular/core';
import { CpBadgeComponent, CpButtonComponent, CpFormSectionComponent, CpNoticeComponent, CpProgressComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_PIPELINE_LIMITS,
  ContentPipelineImagesState,
  clampVariantCount,
} from '../../models/content-pipeline.models';
import { CreativeContextReference } from '../../models/creative-context.models';
import { DamCreatedAsset, DamKeepRecipe } from '../../models/dam-asset.models';
import {
  StagedImage,
  generatedImageFailureSentence,
  isStagedImageActionable,
} from '../../models/generated-image.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { matchSavedPictures } from '../../services/dam-saved-pictures';
import { GeneratedImageService, StagedImageRejectOutcome } from '../../services/generated-image.service';
import {
  SaveToLibraryDialogComponent,
  SaveToLibraryProgress,
} from '../../shared/save-to-library/save-to-library-dialog.component';
import { IdempotencyKey } from './content-pipeline-idempotency';
import { ContentPipelineImageGridComponent } from './content-pipeline-image-grid.component';
import { ContentPipelineImageLightboxComponent } from './content-pipeline-image-lightbox.component';
import {
  GENERATED_IMAGE_RUN_DETAIL,
  GENERATED_IMAGE_RUN_LABELS,
  GENERATED_IMAGE_RUN_TONES,
  StagedImageSave,
  retentionDeadlineText,
} from './content-pipeline-image-presentation';
import { GeneratedImageTracker } from './generated-image-tracker';

/** "One picture" or "3 pictures" — the count a sentence can be built around. */
function pictureCount(count: number): string {
  return count === 1 ? 'One picture' : `${count} pictures`;
}

/**
 * The sentences that depend on where this run is shown.
 *
 * A run is the same work on a pipeline step and on the Image Studio, but "change it on the previous step" is
 * only true on one of them. Everything a caller may reword is here, so nothing else about the run can drift
 * between the two.
 */
export interface GeneratedImageRunWording {
  /** Under the first section's heading: where the prompt being shown came from. */
  readonly makeIntro: string;
  /** Shown instead of the recap when there is no prompt to send. */
  readonly noPrompt: string;
  /** Finishes "Your prompt is longer than we can send — n of m characters." with where to shorten it. */
  readonly tooLongRemedy: string;
  /** Finishes "Asking for n pictures." with where to change the count. */
  readonly countRemedy: string;
  readonly forbidden: string;
  /** The second section's heading, which differs because what the creator does there differs. */
  readonly chooseHeading: string;
  readonly chooseIntro: string;
  /** What keeping — or saving — a picture means here, said above the grid. */
  readonly keepNote: string;
  /** Finishes "These pictures stop being available after <when>," with what to do about it. */
  readonly expiryRemedy: string;
}

/** The Content Pipeline's wording, and the default — it was the first caller. */
export const PIPELINE_IMAGE_RUN_WORDING: GeneratedImageRunWording = {
  makeIntro:
    'This is the prompt from the last step, exactly as it will be sent. Change it there if it is not right yet.',
  noPrompt: 'There is no prompt yet. Go back a step and write one, or have one written for you.',
  tooLongRemedy: 'Shorten it on the previous step.',
  countRemedy: 'Change that on the first step.',
  forbidden: 'You do not have permission to make pictures in this workspace. Ask an Owner or Editor to run this step.',
  chooseHeading: 'Choose the keepers',
  chooseIntro: 'Keep the ones worth using. Downloading gives you a copy you keep yourself.',
  keepNote:
    'Keeping a picture here marks it for the steps that follow. Nothing is filed in your library on this step.',
  expiryRemedy: 'so download anything you want to keep for longer.',
};

/**
 * What a run needs to file its pictures in the library (AF.4.2). Null on a surface that files none.
 *
 * Supplying it is what turns marking a keeper into saving one: the two are the same decision, so a surface
 * that files pictures offers one control for it and not two.
 */
export interface GeneratedImageRunLibrary {
  /**
   * A title to start the save form with — the work's own, or its recipe's.
   *
   * Never one built from the prompt: a prompt describes what was asked for, which is not a fact about the
   * picture that came back (.claude/rules/media.md).
   */
  readonly defaultTitle: string;
  /** The recipe the work is about, offered as the asset's link and removable in the form. */
  readonly defaultRecipe: DamKeepRecipe | null;
  /**
   * The work's creative-context references, where a picture saved before this visit is found again.
   *
   * A staged picture's own row says it is `Kept` and never which asset holds it, so the asset is matched back
   * through the `DamAsset` references the work names — which is exactly what saving one puts there.
   */
  readonly references: readonly CreativeContextReference[];
}

/** One picture's save, as this run holds it between the form and the screen. */
interface SaveRecord {
  readonly state: 'saving' | 'saved' | 'failed';
  /** The asset it is in the library as, once that is known. */
  readonly assetId: string | null;
  readonly title: string | null;
  /** Why the last attempt did not land. Empty unless `failed`. */
  readonly problem: string;
}

const STAGED_SAVE: StagedImageSave = { state: 'staged', assetId: null, title: null, problem: '' };

/**
 * One run of pictures: from a prompt to the ones worth keeping (IMG-003, IMG-005/006).
 *
 * **Written once for its two callers** — the Content Pipeline's images step and the Image Studio. The tracker's
 * staleness discipline and the per-workspace follow key below are what keep one workspace's run out of
 * another's screen, and a second copy of them would be a second thing to keep right.
 *
 * **Two sections in the order the work happens** — asking for the pictures, then choosing among them. The
 * second appears once there is something in it, because an empty contact sheet is not a thing to choose from.
 *
 * **Two things a creator can decide about a picture, and a surface offers one of them.** Without
 * {@link GeneratedImageRunComponent.library} a mark is all this is: `GeneratedImageStatus.Kept` then never
 * arrives, a marked picture is still `Staged` server-side, and retention will collect it on its own schedule
 * — which the run says rather than letting a tick imply something was put away safely. **With a library it
 * files them** (AF.4.2): saving is the decision, there is no separate mark, and a saved picture is permanent,
 * can no longer be declined here, and carries the way to its asset.
 *
 * **A saved picture is saved because the server says so**, not because this screen remembers it. The `Kept`
 * status is the authority and survives a reload, another device and another tab; the asset's own id is the
 * one thing a staged row does not carry, so it is handed over by a save made here and matched back through
 * the work's references otherwise.
 *
 * **Nothing here cancels.** The contract offers asking, reading, looking, downloading and declining; a control
 * claiming to cancel a generation would promise what the server cannot honour, so "Stop checking" says what it
 * actually does.
 *
 * Controlled: it renders the state handed in and emits the next one, so its caller stays the single source of
 * truth and what is on screen is always what is kept.
 */
@Component({
  selector: 'cp-generated-image-run',
  standalone: true,
  imports: [
    CpBadgeComponent,
    CpButtonComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpProgressComponent,
    CpStatusPillComponent,
    ContentPipelineImageGridComponent,
    ContentPipelineImageLightboxComponent,
    SaveToLibraryDialogComponent,
  ],
  templateUrl: './generated-image-run.component.html',
  styleUrl: './generated-image-run.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GeneratedImageRunComponent {
  private readonly service = inject(GeneratedImageService);
  private readonly assets = inject(DamAssetService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  /** The prompt as the caller holds it. Trimmed here before it is shown or sent; nothing here edits it. */
  readonly promptText = input.required<string>();
  /** What the request tells the provider to avoid, or null. Shown in the recap as well as sent. */
  readonly avoidText = input<string | null>(null);
  readonly variantCount = input.required<number>();
  readonly state = input.required<ContentPipelineImagesState>();
  /**
   * The pictures the creator has marked, where this surface marks rather than files.
   *
   * An input rather than part of {@link state} since AF.4.3, because the two live in different places: the
   * run id is kept on the device, and a mark is a decision about the work and belongs to the work. The run
   * renders what it is handed and announces a change; where that change is written is the caller's business.
   */
  readonly keepers = input<readonly string[]>([]);
  readonly wording = input<GeneratedImageRunWording>(PIPELINE_IMAGE_RUN_WORDING);
  /** Prefixes the two sections' ids, so a caller with its own in-page navigation can name them. */
  readonly sectionIdPrefix = input('cp-pipeline-images');
  /** What this surface needs to file pictures in the library, or null when it files none. */
  readonly library = input<GeneratedImageRunLibrary | null>(null);

  readonly changed = output<ContentPipelineImagesState>();
  /** One picture marked or unmarked. The caller writes it wherever its keepers live. */
  readonly keepToggled = output<{ readonly id: string; readonly keep: boolean }>();
  readonly announced = output<string>();
  /**
   * A picture is in the library as this asset — saved just now, or found already there.
   *
   * The caller's to put on the work's creative context: this run shows pictures and does not own what the
   * work is about.
   */
  readonly saved = output<DamCreatedAsset>();

  protected readonly tracker = new GeneratedImageTracker((operationId) =>
    this.service.watch(this.workspaceSlug(), operationId),
  );

  private readonly idempotency = new IdempotencyKey();

  /** The request the current idempotency key was first sent with, or null when there is no attempt in flight. */
  private keyedRequest: string | null = null;

  /** The picture open in the lightbox, or null. One place decides, so the grid and the dialog cannot disagree. */
  protected readonly lightboxId = signal<string | null>(null);

  /** True while a decline or a tidy-up is in flight, so the same picture cannot be acted on twice. */
  protected readonly working = signal(false);

  /** What the last decline or tidy-up did, in the creator's own terms. Cleared when they ask for anything else. */
  protected readonly report = signal('');

  /** What this run knows about each picture's save. The server's `Kept` status is the other half. */
  private readonly saveRecords = signal<ReadonlyMap<string, SaveRecord>>(new Map());

  /** The picture the save form is open for, or null. One place decides, as with the lightbox. */
  protected readonly saveTarget = signal<string | null>(null);

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

  /** The pictures and references last matched against the library, so the same reading is not asked for twice. */
  private namedKey: string | null = null;

  /**
   * True once this run has been destroyed.
   *
   * A confirmation can still be open, and a decline still awaiting its answer, when the creator leaves — Back
   * works behind a dialog. Everything that continues after an `await` checks this first, so a screen that is
   * gone declines nothing further, asks for nothing, and reports to no one.
   */
  private gone = false;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.gone = true;
      this.tracker.destroy();
    });

    // Follow whatever run the draft names, for whichever workspace the step is pointed at. One effect rather
    // than an `ngOnInit`, so arriving with a kept run, a change of workspace and a change of run are one path.
    effect(() => {
      const key = `${this.workspaceSlug()}|${this.state().operationId ?? ''}`;
      if (key === this.followedKey) return;

      this.followedKey = key;
      this.follow();
    });

    // A keeper the run can no longer account for is **not** dropped here any more (AF.4.3). A mark is a
    // reference on the work now, and a reference points at something that may have stopped being usable
    // rather than at nothing — which is what its own row says it means. Retention collecting a staged
    // picture is the ordinary way to reach that, and the step that reads the keepers says so on the picture
    // instead of quietly forgetting the creator chose it.

    // A picture the run reads back as `Kept` was saved, here or in an earlier visit, and the asset it became
    // is the one thing its row cannot say. Matched through the work's own references, once per set of
    // pictures and references, and not at all when every saved picture already names its asset.
    effect(() => {
      const library = this.library();
      const wanted = this.unnamedSaved();
      const slug = this.workspaceSlug();
      if (library === null || wanted.length === 0) return;

      const key = `${slug}|${wanted.join(',')}|${library.references.map((reference) => reference.id).join(',')}`;
      if (key === this.namedKey) return;

      this.namedKey = key;
      untracked(() => void this.nameSaved(slug, library.references, new Set(wanted)));
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
    const operationId = this.state().operationId;

    // Saves belong to the run whose pictures they are. Another run's pictures have other ids, so carrying
    // these would at best say nothing and at worst name an asset for a picture it was never made from.
    this.saveRecords.set(new Map());
    this.saveTarget.set(null);
    this.namedKey = null;

    if (operationId === null) this.tracker.reset();
    else this.tracker.resume(operationId);
  }

  protected readonly imagesState = this.state;

  /** The prompt as it will be sent. */
  protected readonly sendText = computed(() => this.promptText().trim());

  protected readonly count = computed(() => clampVariantCount(this.variantCount()));

  protected readonly promptTooLong = computed(
    () => this.sendText().length > CONTENT_PIPELINE_LIMITS.promptMaxLength,
  );

  protected readonly promptLengthText = computed(
    () => `${this.sendText().length} of ${CONTENT_PIPELINE_LIMITS.promptMaxLength} characters`,
  );

  /** Refused here rather than by the server, so the creator is told what to fix and where. */
  protected readonly canGenerate = computed(
    () => this.sendText() !== '' && !this.promptTooLong() && !this.tracker.busy() && !this.working(),
  );

  protected readonly detail = this.tracker.detail;

  protected readonly stagedImages = computed<readonly StagedImage[]>(() => this.detail()?.images ?? []);

  protected readonly hasPictures = computed(() => this.stagedImages().length > 0);

  protected readonly keeperCount = computed(() => this.keepers().length);

  /** True on a surface that files pictures, where saving is the decision rather than marking. */
  protected readonly files = computed(() => this.library() !== null);

  /**
   * Where each picture stands with the library, or null where there is no library.
   *
   * **The server's `Kept` decides, and this screen's record only adds to it.** A picture the run reads back as
   * `Kept` is in the library whatever happened here — including a save whose answer never arrived, which is
   * why `Kept` outranks a failure. The record is the sole account of the two states no row can carry: a save
   * in flight, and one that did not land.
   */
  protected readonly saves = computed<ReadonlyMap<string, StagedImageSave> | null>(() => {
    if (!this.files()) return null;

    const records = this.saveRecords();
    const saves = new Map<string, StagedImageSave>();

    for (const image of this.stagedImages()) {
      const record = records.get(image.id);

      if (record?.state === 'saving') {
        saves.set(image.id, { state: 'saving', assetId: null, title: null, problem: '' });
      } else if (record?.state === 'saved' || image.status === 'Kept') {
        saves.set(image.id, {
          state: 'saved',
          assetId: record?.assetId ?? null,
          title: record?.title ?? null,
          problem: '',
        });
      } else if (record?.state === 'failed') {
        saves.set(image.id, { state: 'failed', assetId: null, title: null, problem: record.problem });
      } else {
        saves.set(image.id, STAGED_SAVE);
      }
    }

    return saves;
  });

  protected readonly savedCount = computed(
    () => Array.from(this.saves()?.values() ?? []).filter((save) => save.state === 'saved').length,
  );

  /** The one line under the sheet: how many are marked, or how many are in the library for good. */
  protected readonly chosenText = computed(() => {
    if (!this.files()) {
      const count = this.keeperCount();

      return count === 0 ? 'Nothing kept yet.' : count === 1 ? 'One picture kept.' : `${count} pictures kept.`;
    }

    const count = this.savedCount();
    if (count === 0) return 'Nothing saved to your library yet.';

    return count === 1 ? 'One picture is in your library.' : `${count} pictures are in your library.`;
  });

  /**
   * The pictures a tidy-up would decline.
   *
   * Still staged, and not chosen — which on a filing surface means not saved and not being saved. A picture
   * whose save failed is included, because it is a picture the creator does not have.
   */
  protected readonly nonKeepers = computed(() => {
    const keepers = this.keepers();
    const saves = this.saves();

    return this.stagedImages().filter((image) => {
      if (!isStagedImageActionable(image.status)) return false;
      if (saves === null) return !keepers.includes(image.id);

      const state = saves.get(image.id)?.state;

      return state === 'staged' || state === 'failed';
    });
  });

  protected readonly canTidyUp = computed(() => this.nonKeepers().length > 0 && !this.working());

  protected readonly tidyLabel = computed(() =>
    this.files() ? "Tidy away the ones I'm not saving" : "Tidy away the ones I didn't keep",
  );

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

  /**
   * The pictures that will go if nothing is done: still waiting on a decision, and not in the library.
   *
   * What a saved picture's bytes are is the asset's business from then on, and retention has no claim on
   * them — so a run where everything has been saved has no deadline to state, and saying one would frighten a
   * creator about work that is already permanent.
   */
  protected readonly perishable = computed<readonly StagedImage[]>(() => {
    const saves = this.saves();

    return this.stagedImages().filter(
      (image) => isStagedImageActionable(image.status) && saves?.get(image.id)?.state !== 'saved',
    );
  });

  /** When the staged pictures stop being available. Empty when there is nothing with a readable deadline. */
  protected readonly retentionText = computed(() => retentionDeadlineText(this.perishable()));

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

    const request = {
      promptText: this.sendText(),
      avoidText: this.avoidText(),
      variantCount: this.count(),
    };

    // A key names one request (12.10l). If what is being asked for has changed since the attempt that took
    // the key — the prompt was reworded, the count changed — this is a different ask and takes a new one. The
    // server refuses a key reused for a different request, so keeping it would strand the creator on a retry
    // that can never succeed.
    const asked = JSON.stringify(request);
    if (this.keyedRequest !== null && this.keyedRequest !== asked) this.idempotency.clear();
    this.keyedRequest = asked;

    const operationId = await this.tracker.submit(() =>
      this.service.request(
        this.workspaceSlug(),
        request,
        // Reused across a retry of the same request on purpose: an ask that answered `unavailable` may have
        // reached the server anyway, and a fresh key would then buy a second set of pictures.
        this.idempotency.next(),
      ),
    );

    if (operationId === null) return;

    this.idempotency.clear();
    this.keyedRequest = null;
    // Claimed before the draft carries it, so the effect above recognises this as the run already being
    // followed rather than resuming it a second time on top of the poll `submit` just started.
    this.followedKey = `${this.workspaceSlug()}|${operationId}`;
    this.emitImages({ operationId });
    this.announced.emit(`Making ${this.count() === 1 ? 'one picture' : this.count() + ' pictures'}.`);
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
      if (!replace || this.gone) return;
    }

    this.idempotency.clear();
    this.keyedRequest = null;
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
    // Announced upwards and written by the caller: a mark belongs to the work, and the run does not know
    // where the work keeps its own answers.
    this.keepToggled.emit(change);
  }

  protected onOpened(id: string): void {
    this.lightboxId.set(id);
  }

  // ---- Saving to the library (AF.4.2) ----

  /** The picture the save form describes. Empty while the form is closed, which is when it reads nothing. */
  protected readonly saveTargetId = computed(() => this.saveTarget() ?? '');

  /** Open the save form for one picture. A picture already in the library, or on its way, has nothing to ask. */
  protected requestSave(id: string): void {
    const state = this.saves()?.get(id)?.state;
    if (state !== 'staged' && state !== 'failed') return;

    this.report.set('');
    this.saveTarget.set(id);
  }

  /** The form has something to say about a save that has not finished: it is in flight, or it did not land. */
  protected onSaveProgress(progress: SaveToLibraryProgress): void {
    const id = this.saveTarget();
    if (id === null) return;

    this.record(
      id,
      progress.state === 'saving'
        ? { state: 'saving', assetId: null, title: null, problem: '' }
        : { state: 'failed', assetId: null, title: null, problem: progress.problem },
    );
  }

  /**
   * The picture is in the library.
   *
   * The asset is kept so the picture can be opened from here, handed to the caller so the work names it, and
   * the run is read again — the picture's own row says `Kept` from now on, and that is what every other
   * screen reads.
   */
  protected onSaved(asset: DamCreatedAsset): void {
    const id = this.saveTarget();
    if (id === null) return;

    this.record(id, { state: 'saved', assetId: asset.id, title: asset.title, problem: '' });
    this.saved.emit(asset);

    const sentence = `Saved to your library as “${asset.title}”.`;
    this.report.set(sentence);
    this.announced.emit(sentence);
    this.tracker.reread();
  }

  /** The form found the picture gone. Its own row is the truth about that, so the run reads it again. */
  protected onSaveImageGone(): void {
    const id = this.saveTarget();
    if (id !== null) this.record(id, null);

    this.tracker.reread();
  }

  protected onSaveClosed(): void {
    const id = this.saveTarget();
    this.saveTarget.set(null);

    // Cancel is held while a save is in flight, so a form closed mid-save is not something a creator can do;
    // if it happens anyway, the picture goes back to offering a save rather than claiming one is running.
    if (id !== null && this.saveRecords().get(id)?.state === 'saving') this.record(id, null);
  }

  private record(id: string, save: SaveRecord | null): void {
    this.saveRecords.update((records) => {
      const next = new Map(records);
      if (save === null) next.delete(id);
      else next.set(id, save);

      return next;
    });
  }

  /** The pictures the server says are in the library and whose asset this screen cannot yet name. */
  private readonly unnamedSaved = computed<readonly string[]>(() => {
    if (!this.files()) return [];

    const records = this.saveRecords();

    return this.stagedImages()
      .filter((image) => image.status === 'Kept' && (records.get(image.id)?.assetId ?? null) === null)
      .map((image) => image.id);
  });

  /** Find the assets behind pictures this run is told are already in the library. */
  private async nameSaved(
    slug: string,
    references: readonly CreativeContextReference[],
    wanted: Set<string>,
  ): Promise<void> {
    const found = await matchSavedPictures(
      this.assets,
      slug,
      references,
      wanted,
      // Checked between reads: a screen that has gone, or a workspace that has changed, stops asking, and an
      // answer about one workspace never lands on another's (.claude/rules/tenancy.md).
      () => !this.gone && this.workspaceSlug() === slug,
    );

    for (const [generatedImageId, saved] of found) {
      this.record(generatedImageId, { state: 'saved', assetId: saved.assetId, title: saved.title, problem: '' });
    }
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
    if (!confirmed || this.gone) return;

    this.working.set(true);
    const outcome = await this.service.reject(this.workspaceSlug(), id);
    this.working.set(false);
    if (this.gone) return;

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
      message: this.files()
        ? 'The pictures you have not saved will be removed and cannot be brought back. Save or download any you want to keep first.'
        : 'The pictures you have not kept will be removed and cannot be brought back. Download any you want a copy of first.',
      confirmLabel: 'Tidy them away',
      cancelLabel: 'Leave them',
    });
    if (!confirmed || this.gone) return;

    this.working.set(true);

    let declined = 0;
    const problems: string[] = [];

    for (const image of targets) {
      if (this.gone) return;

      const outcome = await this.service.reject(this.workspaceSlug(), image.id);
      if (outcome.status === 'declined') declined += 1;
      else problems.push(declineSentence(outcome));
    }

    this.working.set(false);
    if (this.gone) return;

    const parts: string[] = [];
    if (declined > 0) parts.push(`${pictureCount(declined)} ${declined === 1 ? 'was' : 'were'} declined.`);
    // The distinct reasons rather than one per picture: three identical sentences say nothing the first did not.
    parts.push(...Array.from(new Set(problems)));

    const sentence = parts.join(' ');
    this.report.set(sentence);
    this.announced.emit(sentence);
    this.tracker.reread();
  }

  /** A declined picture is not one the creator wants, whatever mark it carried. */
  private unkeep(id: string): void {
    if (this.keepers().includes(id)) this.keepToggled.emit({ id, keep: false });
  }

  private emitImages(images: ContentPipelineImagesState): void {
    this.changed.emit(images);
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
