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
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CpButtonComponent, CpNoticeComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { ContentPipelineDraft } from '../../models/content-pipeline.models';
import { CreativeContextReference, keeperReferencesOf, linkedRecipeOf } from '../../models/creative-context.models';
import { DamCreatedAsset, DamKeepPrompt, DamKeepRecipe } from '../../models/dam-asset.models';
import { GeneratedImageOperationDetail, StagedImage } from '../../models/generated-image.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { matchSavedPictures } from '../../services/dam-saved-pictures';
import { CreativeContextSession } from '../../services/creative-context-session';
import { GeneratedImageService } from '../../services/generated-image.service';
import { ImagePromptService } from '../../services/image-prompt.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import {
  SaveToLibraryDialogComponent,
  SaveToLibraryProgress,
} from '../../shared/save-to-library/save-to-library-dialog.component';
import {
  STAGED_IMAGE_STATUS_LABELS,
  STAGED_IMAGE_STATUS_TONES,
  StagedImageSave,
  fileSizeText,
  imageFormatText,
} from './content-pipeline-image-presentation';
import {
  PROMPT_LINEAGE_SENTENCES,
  PromptLineageOutcome,
  generatedPromptFor,
  manualPromptFor,
  promptOriginOf,
} from './content-pipeline-prompt-lineage';

/** How reading the run this step files from can end. */
type Phase = 'loading' | 'ready' | 'unavailable';

/** One keeper, with everything its row shows worked out once rather than in the template. */
interface KeeperRow {
  readonly generatedImageId: string;
  readonly position: number;
  readonly total: number;
  /** The picture as the run reports it, or null when the run no longer lists it at all. */
  readonly image: StagedImage | null;
  readonly save: StagedImageSave;
  readonly statusLabel: string;
  readonly statusTone: (typeof STAGED_IMAGE_STATUS_TONES)[keyof typeof STAGED_IMAGE_STATUS_TONES];
  readonly facts: string;
  /** Where the asset is, for a saved picture whose asset is known. */
  readonly assetLink: readonly string[] | null;
  /** False for a picture that can no longer be saved: declined, expired, or no longer in the run. */
  readonly canSave: boolean;
  /** Why it cannot be saved, when it cannot. */
  readonly blocked: string;
}

/** One picture's save, as this step holds it between the form and the screen. */
interface SaveRecord {
  readonly state: 'saving' | 'saved' | 'failed';
  readonly assetId: string | null;
  readonly title: string | null;
  readonly problem: string;
}

const STAGED_SAVE: StagedImageSave = { state: 'staged', assetId: null, title: null, problem: '' };

/**
 * Step 6 of the Content Pipeline: the keepers, filed in the library (PIPE-UI-006's DAM and prompt-lineage
 * half; FLU-002).
 *
 * **One row per keeper, each saved on its own through the shared save form.** Saving is the same action here
 * as in the Image Studio — the same route, the same form, the same idempotency — so a picture filed from a
 * pipeline run and one filed from the studio are the same kind of thing, and the title and alt text stay the
 * creator's per picture rather than being generated for a batch (.claude/rules/media.md).
 *
 * **Nothing about progress is stored, because the server already knows it.** A keeper is saved when its
 * picture's status is `Kept`, and the asset it became is matched back through the work's own `Keeper`
 * references. So resuming retries exactly what is not saved, replaying costs nothing — the route keeps a
 * picture once and answers with the asset the first save made — and one failure neither undoes nor blocks the
 * others, because each save is its own request.
 *
 * **The prompt is kept with the picture, or not at all, and the step says which.** A prompt record is
 * immutable and declares its own provenance, so it is assembled from the generation that drafted it rather
 * than claimed; where that cannot be done the picture is still saved and the reason is on screen.
 *
 * **No board card and no scheduling.** Those are a separate decision about publishing, and they stay with
 * master 13.7a.
 */
@Component({
  selector: 'cp-content-pipeline-library-step',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    SaveToLibraryDialogComponent,
  ],
  templateUrl: './content-pipeline-library-step.component.html',
  styleUrl: './content-pipeline-library-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineLibraryStepComponent {
  private readonly images = inject(GeneratedImageService);
  private readonly assets = inject(DamAssetService);
  private readonly prompts = inject(ImagePromptService);
  private readonly references = inject(ReferenceImageService);

  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  /** The work's hold on its creative context: where the keepers are, and where a saved asset is named. */
  readonly session = input.required<CreativeContextSession>();

  readonly announced = output<string>();

  private readonly readPhase = signal<Phase>('loading');

  /** True when the work this step is holding is the work of the workspace on screen. */
  private readonly holdsThisWorkspace = computed(() => this.session().workspace() === this.workspaceSlug());

  /**
   * Where the step stands.
   *
   * Reads as loading while the session is still pointed at another workspace: one workspace's keepers must
   * never be drawn on another's screen, however briefly (.claude/rules/tenancy.md).
   */
  protected readonly phase = computed<Phase>(() => (this.holdsThisWorkspace() ? this.readPhase() : 'loading'));
  /** The run the keepers belong to, as the server reports it. */
  private readonly run = signal<GeneratedImageOperationDetail | null>(null);
  private readonly saveRecords = signal<ReadonlyMap<string, SaveRecord>>(new Map());
  protected readonly saveTarget = signal<string | null>(null);
  /** The prompt as it can be kept, or why it cannot. Null until it has been worked out. */
  protected readonly lineage = signal<PromptLineageOutcome | null>(null);

  /** True once this screen has gone, so an answer still on its way changes nothing. */
  private gone = false;
  /** What was last read, so the same run and references are not read twice. */
  private readKey: string | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.gone = true));

    // One read per run, per workspace, per set of references. The references are in the key because a save
    // adds one, and that is how the asset behind a picture becomes findable.
    //
    // **Nothing is read while the work in hand is not this workspace's.** Between a change of workspace and
    // the session's next read, the keepers on screen are still the previous workspace's — and asking this
    // workspace about those ids would be asking one workspace about another's records. This step does not
    // depend on its caller for that, the way the run does not (.claude/rules/tenancy.md).
    effect(() => {
      const slug = this.workspaceSlug();
      const operationId = this.draft().images.operationId;
      if (this.session().workspace() !== slug) return;

      const names = this.keeperReferences().map((reference) => reference.id).join(',');
      const key = `${slug}|${operationId ?? ''}|${names}`;
      if (key === this.readKey) return;

      this.readKey = key;
      untracked(() => void this.read(slug, operationId));
    });

    // The prompt's provenance, worked out once per prompt rather than per picture: it is the same prompt for
    // every keeper of the run, and each saved picture records its own copy of it.
    effect(() => {
      const slug = this.workspaceSlug();
      const prompt = this.draft().prompt;
      const channelKey = this.draft().config.channelKey;

      untracked(() => void this.resolveLineage(slug, prompt, channelKey));
    });
  }

  // ---- What the step is about ----

  protected readonly keeperReferences = computed<readonly CreativeContextReference[]>(() =>
    this.holdsThisWorkspace() ? keeperReferencesOf(this.session().context()) : [],
  );

  /** The keepers of this run, in the order the creator marked them. */
  private readonly keeperIds = computed<readonly string[]>(() =>
    this.keeperReferences()
      .map((reference) => reference.generatedImageId)
      .filter((id): id is string => id !== null),
  );

  protected readonly rows = computed<readonly KeeperRow[]>(() => {
    const ids = this.keeperIds();
    const pictures = this.run()?.images ?? [];
    const records = this.saveRecords();
    const slug = this.workspaceSlug();
    const total = ids.length;

    return ids.map((generatedImageId, index) => {
      const image = pictures.find((each) => each.id === generatedImageId) ?? null;
      const save = saveOf(records.get(generatedImageId), image);
      const status = image?.status ?? 'Unspecified';

      return {
        generatedImageId,
        position: index + 1,
        total,
        image,
        save,
        statusLabel: STAGED_IMAGE_STATUS_LABELS[status],
        statusTone: STAGED_IMAGE_STATUS_TONES[status],
        facts:
          image === null
            ? ''
            : `${imageFormatText(image.mediaType)} · ${image.width} × ${image.height} · ${fileSizeText(image.sizeBytes)}`,
        assetLink: save.assetId === null ? null : ['/', slug, 'dam', save.assetId],
        canSave: image !== null && image.status === 'Staged' && save.state !== 'saving',
        blocked: blockedReason(image),
      };
    });
  });

  protected readonly savedCount = computed(() => this.rows().filter((row) => row.save.state === 'saved').length);
  protected readonly unsaved = computed(() => this.rows().filter((row) => row.save.state !== 'saved'));
  protected readonly allSaved = computed(() => this.rows().length > 0 && this.unsaved().length === 0);

  protected readonly countText = computed(() => {
    const saved = this.savedCount();
    const total = this.rows().length;
    if (total === 0) return '';

    return saved === total
      ? saved === 1
        ? 'One picture is in your library.'
        : `All ${total} pictures are in your library.`
      : `${saved} of ${total} in your library.`;
  });

  protected readonly libraryLink = computed(() => ['/', this.workspaceSlug(), 'dam']);
  protected readonly imagesLink = computed(() => ['/', this.workspaceSlug(), 'content-pipeline', 'images']);

  /** What the step says about the prompt, or empty when it is being kept without comment. */
  protected readonly lineageNote = computed(() => {
    const outcome = this.lineage();

    return outcome === null || outcome.status === 'ready' ? '' : PROMPT_LINEAGE_SENTENCES[outcome.reason];
  });

  /** The prompt handed to each save, or null when it cannot be kept. */
  protected readonly keepPrompt = computed<DamKeepPrompt | null>(() => {
    const outcome = this.lineage();

    return outcome?.status === 'ready' ? outcome.prompt : null;
  });

  /** The recipe this work is about, offered as the asset's link. */
  protected readonly recipe = computed<DamKeepRecipe | null>(() => {
    const linked = linkedRecipeOf(this.session().context());
    const title = this.session().context()?.workingTitle?.trim() ?? '';

    // The context names the recipe by id; this step has no recipe read of its own, so the work's own title is
    // what the link is shown as when there is one. A title is never composed from the prompt.
    return linked === null ? null : { id: linked.recipeId, title: title === '' ? 'The linked recipe' : title };
  });

  protected readonly defaultTitle = computed(() => this.session().context()?.workingTitle?.trim() ?? '');

  protected readonly saveTargetId = computed(() => this.saveTarget() ?? '');

  // ---- Saving ----

  protected requestSave(generatedImageId: string): void {
    const row = this.rows().find((each) => each.generatedImageId === generatedImageId);
    if (row === undefined || !row.canSave || row.save.state === 'saved') return;

    this.saveTarget.set(generatedImageId);
  }

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
   * One picture is in the library.
   *
   * The asset is named on the work, so the next visit finds it without this screen's help, and the run is read
   * again — the picture's own row says `Kept` from now on, which is what makes a resume retry only the rest.
   */
  protected async onSaved(asset: DamCreatedAsset): Promise<void> {
    const id = this.saveTarget();
    if (id === null) return;

    this.record(id, { state: 'saved', assetId: asset.id, title: asset.title, problem: '' });
    this.announced.emit(`Saved to your library as “${asset.title}”.`);

    const outcome = await this.session().addReference({
      kind: 'DamAsset',
      purpose: 'Keeper',
      mediaAssetId: asset.id,
      mediaAssetVersionNumber: asset.currentVersionNumber,
    });
    if (this.gone) return;

    if (outcome !== 'saved' && outcome !== 'duplicate') {
      this.announced.emit(
        `“${asset.title}” is in your library, but this piece of work could not be made to point at it just now.`,
      );
    }
  }

  protected onSaveImageGone(): void {
    const id = this.saveTarget();
    if (id !== null) this.record(id, null);

    void this.read(this.workspaceSlug(), this.draft().images.operationId);
  }

  protected onSaveClosed(): void {
    const id = this.saveTarget();
    this.saveTarget.set(null);

    // Cancel is held while a save is in flight, so this is not something a creator can do; if it happens
    // anyway, the picture goes back to offering a save rather than claiming one is running.
    if (id !== null && this.saveRecords().get(id)?.state === 'saving') this.record(id, null);
  }

  protected retryRead(): void {
    void this.read(this.workspaceSlug(), this.draft().images.operationId);
  }

  private record(id: string, save: SaveRecord | null): void {
    this.saveRecords.update((records) => {
      const next = new Map(records);
      if (save === null) next.delete(id);
      else next.set(id, save);

      return next;
    });
  }

  // ---- Reading ----

  /**
   * Read the run, then find the assets behind whatever it says is already in the library.
   *
   * Both answers come from the server rather than from anything this step stored, which is what makes a
   * resume honest: it retries exactly the pictures that are not `Kept`.
   */
  private async read(slug: string, operationId: string | null): Promise<void> {
    if (operationId === null) {
      this.run.set(null);
      this.readPhase.set('ready');
      return;
    }

    this.readPhase.set('loading');

    const outcome = await firstValueFrom(this.images.watch(slug, operationId));
    if (this.gone || this.workspaceSlug() !== slug) return;

    if (outcome.status !== 'found') {
      // A run that is gone is not an error about the keepers: each row says what it knows, which for a
      // picture nothing can account for is that it is no longer there.
      this.run.set(null);
      this.readPhase.set(outcome.status === 'not_found' ? 'ready' : 'unavailable');
      return;
    }

    this.run.set(outcome.operation);
    this.readPhase.set('ready');

    const wanted = new Set(
      outcome.operation.images
        .filter((image) => image.status === 'Kept' && this.keeperIds().includes(image.id))
        .map((image) => image.id)
        .filter((id) => (this.saveRecords().get(id)?.assetId ?? null) === null),
    );

    const found = await matchSavedPictures(
      this.assets,
      slug,
      this.keeperReferences(),
      wanted,
      () => !this.gone && this.workspaceSlug() === slug,
    );

    for (const [generatedImageId, saved] of found) {
      this.record(generatedImageId, { state: 'saved', assetId: saved.assetId, title: saved.title, problem: '' });
    }
  }

  /** Read back the generation that drafted the prompt, so the saved prompt can say where it came from. */
  private async resolveLineage(
    slug: string,
    prompt: ContentPipelineDraft['prompt'],
    channelKey: string | null,
  ): Promise<void> {
    const origin = promptOriginOf(prompt);

    if (origin === null) {
      this.lineage.set(manualPromptFor(prompt, channelKey));
      return;
    }

    this.lineage.set(null);

    const outcome = await firstValueFrom(
      origin.source === 'ImagePromptComposition'
        ? this.prompts.watch(slug, origin.requestId)
        : this.references.watch(slug, origin.requestId),
    );
    if (this.gone || this.workspaceSlug() !== slug) return;

    const proposal = outcome.status === 'found' ? outcome.operation.proposal : null;
    this.lineage.set(generatedPromptFor(prompt, channelKey, origin, proposal));
  }
}

/**
 * Where one picture stands with the library.
 *
 * **The server's `Kept` decides.** A record adds the way to the asset and is the only account of the two
 * states no row can carry — a save in flight, and one that did not land — and `Kept` outranks a failure,
 * which is what makes a save whose answer never arrived read correctly on the next visit.
 */
function saveOf(record: SaveRecord | undefined, image: StagedImage | null): StagedImageSave {
  if (record?.state === 'saving') return { state: 'saving', assetId: null, title: null, problem: '' };

  if (record?.state === 'saved' || image?.status === 'Kept') {
    return { state: 'saved', assetId: record?.assetId ?? null, title: record?.title ?? null, problem: '' };
  }

  if (record?.state === 'failed') return { state: 'failed', assetId: null, title: null, problem: record.problem };

  return STAGED_SAVE;
}

/** Why a keeper cannot be saved, in the creator's terms. Empty when it can. */
function blockedReason(image: StagedImage | null): string {
  if (image === null) {
    return 'This picture is no longer part of the run, so it cannot be saved.';
  }
  if (image.status === 'Rejected') return 'This picture was declined, so it cannot be saved.';
  if (image.status === 'Expired') {
    return 'This picture was held past the time generated pictures are kept for, so it cannot be saved.';
  }

  return '';
}
