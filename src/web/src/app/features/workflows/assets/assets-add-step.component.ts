import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { keeperReferencesOf } from '../../../models/creative-context.models';
import { DamAssetDetail, DamAssetSummary, DamAssetVersion, DamCreatedAsset } from '../../../models/dam-asset.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { CreativeContextService } from '../../../services/creative-context.service';
import { DamAssetService } from '../../../services/dam-asset.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { SaveToLibraryDialogComponent } from '../../../shared/save-to-library/save-to-library-dialog.component';
import { DamAssetPickerComponent } from '../../dam/dam-asset-picker.component';
import { DamAssetUploadComponent } from '../../dam/dam-asset-upload.component';
import { DamAssetVersionUploadComponent } from '../../dam/dam-asset-version-upload.component';
import { DamLinkedAssetComponent } from '../../dam/dam-linked-asset.component';
import { assetPointer, runAssetId } from './assets-run';
import { AssetsWaitingPictureComponent } from './assets-waiting-picture.component';

type Way = 'upload' | 'library' | 'waiting';

type Chosen =
  | { readonly status: 'none' }
  | { readonly status: 'loading'; readonly assetId: string }
  | { readonly status: 'gone'; readonly assetId: string }
  | { readonly status: 'unavailable'; readonly assetId: string }
  | { readonly status: 'ready'; readonly asset: DamAssetDetail };

/** A kept generated picture not yet in the library, and the piece of work it was kept on. */
interface Waiting {
  readonly generatedImageId: string;
  readonly contextId: string;
  readonly workName: string;
}

type WaitingRead =
  | { readonly status: 'idle' }
  | { readonly status: 'loading' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly pictures: readonly Waiting[] };

/** How many recent pieces of work are looked through for pictures waiting to be saved. */
export const WAITING_LOOKBACK = 10;

const WAYS: readonly CpChoiceOption[] = [
  {
    value: 'upload',
    label: 'Upload a new picture',
    hint: 'From your device. Your original is kept exactly as you upload it.',
  },
  { value: 'library', label: 'Choose one from your library', hint: 'A picture already saved in this workspace.' },
  {
    value: 'waiting',
    label: 'Save one you kept but haven’t saved yet',
    hint: 'A picture you kept while making pictures, still waiting to go in your library.',
  },
];

/**
 * "Upload a picture or choose one", the first step of "Organize your pictures" (13A.21, DAM-001).
 *
 * <p><strong>Orchestration only.</strong> Uploading is the library's own upload, choosing is its own picker, and
 * saving a waiting picture is the same save-to-library every picture-making screen uses. Each makes or names one
 * library asset; nothing here writes one.</p>
 *
 * <p><strong>Waiting pictures are looked for on recent work.</strong> No route lists the generated pictures kept
 * and not yet saved, so the step reads the {@link WAITING_LOOKBACK} most recent pieces of work and says so. One
 * already saved since is simply that library picture: saving it again answers with it.</p>
 *
 * <p><strong>A new version never replaces the original.</strong> Adding one is the library's version upload, which
 * makes the next numbered version and keeps every earlier one, bytes and all. There is no choosing an older
 * version back: the newest is the one in use.</p>
 */
@Component({
  selector: 'cp-assets-add-step',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    DamAssetPickerComponent,
    DamAssetUploadComponent,
    DamAssetVersionUploadComponent,
    DamLinkedAssetComponent,
    SaveToLibraryDialogComponent,
    AssetsWaitingPictureComponent,
    CpSkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{ chosen().status === 'loading' ? 'Reading your picture…' : '' }}</p>
    @switch (chosen().status) {
      @case ('loading') {
        <!-- The picture arriving, in its own shape. -->
        <div aria-busy="true"><cp-skeleton shape="block" pictureAspect="4 / 3" /></div>
      }
      @case ('gone') {
        <cp-notice tone="warning" role="status">
          The picture this workflow was about isn’t in your library any more. Choose another.
        </cp-notice>
        <ng-container [ngTemplateOutlet]="ways" />
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your picture couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="reload()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (asset(); as shown) {
          <cp-form-section heading="Your picture" [intro]="versionText()">
            <cp-dam-linked-asset [workspaceSlug]="workspaceSlug()" [assetId]="shown.id" />
            <div class="cp-actions">
              <button cpButton type="button" variant="secondary" (click)="addVersion($event)">Add a new version</button>
              <button cpButton type="button" variant="text" (click)="chooseAgain()">Choose a different picture</button>
            </div>
          </cp-form-section>

          <cp-dam-asset-version-upload
            [open]="versioning()"
            [workspaceSlug]="workspaceSlug()"
            [assetId]="shown.id"
            [currentVersionNumber]="shown.currentVersion?.versionNumber ?? 1"
            (added)="onVersionAdded($event)"
            (cancelled)="onVersionCancelled()"
            (closed)="versioning.set(false)"
          />
        }
      }
      @default {
        <ng-container [ngTemplateOutlet]="ways" />
      }
    }

    @if (keepFailed()) {
      <cp-notice tone="warning" role="status">
        Your picture is in your library, but this workflow couldn’t be pointed at it just now. Continue will still name
        it.
      </cp-notice>
    }

    <ng-template #ways>
      <cp-form-section heading="Add it">
        <cp-choice-group
          label="How do you want to add it?"
          idPrefix="assets-add-"
          [options]="wayOptions"
          [value]="way() ? [way()!] : []"
          (valueChange)="chooseWay($event)"
        />
      </cp-form-section>

      @switch (way()) {
        @case ('upload') {
          <div class="cp-actions">
            <button cpButton type="button" variant="primary" (click)="uploading.set(true)">Upload a picture</button>
          </div>
          <cp-dam-asset-upload
            [open]="uploading()"
            [workspaceSlug]="workspaceSlug()"
            (uploaded)="onUploaded($event)"
            (cancelled)="uploading.set(false)"
            (closed)="uploading.set(false)"
          />
        }
        @case ('library') {
          <cp-dam-asset-picker [workspaceSlug]="workspaceSlug()" [active]="true" (chosen)="onPicked($event)" />
        }
        @case ('waiting') {
          <!-- Always present while this way in is open (APP-003): the one place it says it is still looking. -->
          <p class="cp-sr-only" role="status">{{
            waiting().status === 'loading' ? 'Looking through your recent work…' : ''
          }}</p>
          @switch (waiting().status) {
            @case ('loading') {
              <!-- The recent work arriving, as a few rows. -->
              <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
            }
            @case ('unavailable') {
              <cp-notice tone="error" role="alert">
                Your recent work couldn’t be read just now.
                <button cpButton type="button" variant="secondary" size="sm" (click)="readWaiting()">Try again</button>
              </cp-notice>
            }
            @case ('ready') {
              @if (waitingPictures().length === 0) {
                <p class="plain">Nothing is waiting to be saved from your {{ lookback }} most recent pieces of work.</p>
              } @else {
                <p class="plain cp-muted">From your {{ lookback }} most recent pieces of work.</p>
                <ul class="waiting">
                  @for (picture of waitingPictures(); track picture.generatedImageId; let index = $index) {
                    <li>
                      <cp-assets-waiting-picture
                        [workspaceSlug]="workspaceSlug()"
                        [generatedImageId]="picture.generatedImageId"
                        (gone)="dropWaiting(picture.generatedImageId)"
                      />
                      <span>From “{{ picture.workName }}”</span>
                      <button
                        cpButton
                        type="button"
                        variant="secondary"
                        [attr.aria-label]="'Save this one: picture ' + (index + 1) + ' from ' + picture.workName"
                        (click)="saveWaiting(picture, $event)"
                      >
                        Save this one
                      </button>
                    </li>
                  }
                </ul>
              }
            }
          }

          @if (saving(); as picture) {
            <cp-save-to-library-dialog
              [open]="true"
              [workspaceSlug]="workspaceSlug()"
              [generatedImageId]="picture.generatedImageId"
              [defaultTitle]="picture.workName"
              [creativeContextId]="picture.contextId"
              (saved)="onSavedWaiting($event)"
              (imageGone)="dropWaiting(picture.generatedImageId)"
              (closed)="closeSave()"
            />
          }
        }
      }
    </ng-template>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .plain {
      margin: 0;
    }

    .waiting {
      display: grid;
      gap: var(--cp-space-3);
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .waiting li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--cp-space-3);
    }
  `,
})
export class AssetsAddStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly assets = inject(DamAssetService);
  private readonly contexts = inject(CreativeContextService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly wayOptions = WAYS;
  protected readonly lookback = WAITING_LOOKBACK;

  protected readonly chosen = signal<Chosen>({ status: 'none' });
  protected readonly way = signal<Way | null>(null);
  protected readonly uploading = signal(false);
  protected readonly versioning = signal(false);
  protected readonly waiting = signal<WaitingRead>({ status: 'idle' });
  protected readonly saving = signal<Waiting | null>(null);
  protected readonly keepFailed = signal(false);
  /** Waiting pictures the server no longer serves, so they are not offered. */
  private readonly dropped = signal<ReadonlySet<string>>(new Set());

  /** The button a dialog was opened from, so focus goes back there when it closes. */
  private opener: HTMLElement | null = null;
  private keeping: Promise<boolean> = Promise.resolve(true);
  private gone = false;

  protected readonly asset = computed(() => {
    const chosen = this.chosen();
    return chosen.status === 'ready' ? chosen.asset : null;
  });

  private readonly assetId = computed(() => {
    const chosen = this.chosen();
    return chosen.status === 'ready' ? chosen.asset.id : chosen.status === 'none' ? null : chosen.assetId;
  });

  protected readonly waitingPictures = computed(() => {
    const read = this.waiting();
    const dropped = this.dropped();
    return read.status === 'ready' ? read.pictures.filter((picture) => !dropped.has(picture.generatedImageId)) : [];
  });

  protected readonly versionText = computed(() => {
    const current = this.asset()?.currentVersion?.versionNumber ?? null;
    if (current === null) return 'Its current version can’t be read just now.';
    return current === 1
      ? 'This is its first version. A new version is added beside it; the original is never replaced.'
      : `Version ${current} is the one in use. Every earlier version is kept, each with its own download.`;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    const unregister = this.port.register({
      // Every way in is a command the server answers; there is nothing held here to keep before a move.
      unsaved: computed(() => this.uploading() || this.versioning()),
      flush: async () => {
        await this.keeping;
        return true;
      },
      draft: computed(() => {
        const asset = this.asset();
        return asset === null ? null : assetPointer(asset.id);
      }),
      results: computed(() => {
        const asset = this.asset();
        return asset === null ? [] : [assetPointer(asset.id)];
      }),
      ready: computed(() => this.asset() !== null),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    const assetId = runAssetId(this.run());
    if (assetId !== null) void this.open(assetId, false);
  }

  reload(): void {
    const assetId = this.assetId();
    if (assetId !== null) void this.open(assetId, false);
  }

  protected chooseWay(values: readonly string[]): void {
    const way = (values[0] ?? null) as Way | null;
    this.way.set(way);
    if (way === 'waiting' && this.waiting().status !== 'ready') void this.readWaiting();
  }

  protected chooseAgain(): void {
    this.chosen.set({ status: 'none' });
    this.way.set(null);
    this.port.announce('Choose a different picture.');
  }

  protected onUploaded(asset: DamCreatedAsset): void {
    this.uploading.set(false);
    void this.open(asset.id, true, `“${asset.title}” is in your library.`);
  }

  protected onPicked(asset: DamAssetSummary): void {
    void this.open(asset.id, true, `“${asset.title}” is chosen.`);
  }

  protected saveWaiting(picture: Waiting, event: Event): void {
    this.opener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    this.saving.set(picture);
  }

  protected onSavedWaiting(asset: DamCreatedAsset): void {
    this.saving.set(null);
    void this.open(asset.id, true, `“${asset.title}” is in your library.`);
  }

  protected closeSave(): void {
    this.saving.set(null);
    this.opener?.focus();
  }

  protected dropWaiting(generatedImageId: string): void {
    this.dropped.update((ids) => new Set([...ids, generatedImageId]));
  }

  protected addVersion(event: Event): void {
    this.opener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    this.versioning.set(true);
  }

  protected onVersionAdded(version: DamAssetVersion): void {
    this.versioning.set(false);
    this.reload();
    this.port.announce(`Version ${version.versionNumber} added. Every earlier version is kept.`);
    this.opener?.focus();
  }

  /** A cancelled upload may already have reached the server, so the picture is read again rather than assumed. */
  protected onVersionCancelled(): void {
    this.versioning.set(false);
    this.reload();
    this.opener?.focus();
  }

  /** Looks through recent work for kept pictures not yet saved — the one place they can be found. */
  async readWaiting(): Promise<void> {
    const slug = this.workspaceSlug();
    this.waiting.set({ status: 'loading' });

    const list = await firstValueFrom(this.contexts.list(slug, null, WAITING_LOOKBACK));
    if (this.gone) return;
    if (list.status !== 'found') {
      this.waiting.set({ status: 'unavailable' });
      return;
    }

    const read = await Promise.all(
      list.page.items.map((summary) => firstValueFrom(this.contexts.get(slug, summary.id))),
    );
    if (this.gone) return;

    const pictures: Waiting[] = [];
    for (const outcome of read) {
      if (outcome.status !== 'found') continue;
      const workName = outcome.context.workingTitle?.trim() || 'Untitled work';
      for (const keeper of keeperReferencesOf(outcome.context)) {
        if (keeper.kind === 'GeneratedImage' && keeper.generatedImageId !== null) {
          pictures.push({ generatedImageId: keeper.generatedImageId, contextId: outcome.context.id, workName });
        }
      }
    }

    this.waiting.set({ status: 'ready', pictures });
  }

  /** Reads the picture, and — for one just added or chosen — keeps it on the run so coming back finds it. */
  private async open(assetId: string, keep: boolean, said?: string): Promise<void> {
    this.chosen.set({ status: 'loading', assetId });

    const outcome = await firstValueFrom(this.assets.detail(this.workspaceSlug(), assetId));
    if (this.gone) return;

    if (outcome.status !== 'found') {
      this.chosen.set({ status: outcome.status === 'not_found' ? 'gone' : 'unavailable', assetId });
      return;
    }

    this.chosen.set({ status: 'ready', asset: outcome.asset });
    if (said) this.port.announce(said);
    if (!keep) return;

    this.keepFailed.set(false);
    this.keeping = this.port.keepDraft(assetPointer(assetId)).then((kept) => {
      if (!this.gone) this.keepFailed.set(!kept);
      return kept;
    });
  }
}
