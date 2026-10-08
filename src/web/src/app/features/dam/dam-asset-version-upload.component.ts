import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import {
  CpButtonComponent,
  CpDialogComponent,
  CpFormSectionComponent,
  CpUploadItem,
  CpUploaderComponent,
} from '@creator-pantry/ui';

import { DAM_ASSET_LIMITS, DAM_IMAGE_ACCEPT, DamAssetVersion } from '../../models/dam-asset.models';
import { DamAssetService, DamVersionUploadFailure, DamVersionUploadOutcome } from '../../services/dam-asset.service';

type UploadState =
  | { readonly status: 'choosing' }
  /** `percent` is null until the browser can say how far along it is. */
  | { readonly status: 'uploading'; readonly percent: number | null }
  | { readonly status: 'failed'; readonly message: string };

/**
 * Why a version was not added, in the creator's terms. Exhaustive by type, so a failure the service learns to
 * report has to be given words rather than reaching a creator as a blank.
 */
const FAILURE_MESSAGE: Readonly<Record<DamVersionUploadFailure, string>> = {
  unsupported:
    'That file is not a picture the library takes. Try a PNG, JPEG, WebP or GIF. Nothing was stored.',
  too_large: 'That file is larger than 32 MB, which is the most the library takes. Nothing was stored.',
  invalid: 'That file could not be accepted. Check it and try again.',
  forbidden: 'You need Contributor access in this workspace to add a version. Nothing was stored.',
  not_found: 'This picture is no longer in the library, so a version cannot be added to it.',
  version_taken:
    'Someone else added a version at the same moment, so yours was not stored. Your file is still chosen — try again and it will take the next number.',
  unavailable: 'The version could not be stored just now, and nothing was left behind. Your file is still chosen, so try again.',
};

/**
 * Adds a new version of one library asset from a file (DAM-UI-004).
 *
 * **Nothing is overwritten, and this dialog says so.** The file becomes the next numbered version; every
 * earlier one keeps its row, its bytes and its own download. That is the server's behaviour — a version's
 * storage key carries its number and the store refuses to overwrite — not a promise made here.
 *
 * **Bytes only.** The route takes one field. What the creator said about the picture, alt text included, is
 * left exactly as it is, which the dialog points out: a new picture may no longer match its old description.
 *
 * **What a file is stays the server's decision.** Only the 32 MB ceiling is checked here, to spare a creator a
 * transfer that cannot succeed. The server reads the bytes; the name and the type a browser reports are not
 * evidence of anything.
 *
 * **Progress is the real transfer**, and **Cancel aborts it**. A cancelled request may already have reached
 * the server, so cancelling tells the page to read the asset again rather than assuming nothing was added.
 *
 * **Closing is refused while an upload runs**, so a creator always sees how it ended.
 */
@Component({
  selector: 'cp-dam-asset-version-upload',
  standalone: true,
  imports: [CpButtonComponent, CpDialogComponent, CpFormSectionComponent, CpUploaderComponent],
  template: `
    <cp-dialog
      [open]="open()"
      title="Add a version"
      description="The new file becomes the next version. Nothing already here is overwritten."
      titleId="cp-dam-version-upload-title"
      (closed)="close()"
      (keydown.escape)="close()"
    >
      <cp-form-section heading="The new file" [problem]="failure()">
        <!-- Said plainly, because adding a version sounds like replacing one and is not. -->
        <p class="standing">
          Version {{ currentVersionNumber() }} stays downloadable, as does every version before it. The file you
          choose becomes version {{ nextVersionNumber() }} and is the one shown from then on.
        </p>
        <p class="standing">
          Your title, alt text and other details are not changed. If the new picture looks different, check that
          the alt text still describes it.
        </p>

        <cp-uploader
          label="Choose the picture"
          hint="PNG, JPEG, WebP or GIF, up to 32 MB."
          [accept]="accept"
          [items]="uploadItems()"
          (filesSelected)="onFilesSelected($event)"
          (remove)="onRemoveFile()"
          (cancel)="onCancelItem()"
          (retry)="upload()"
        />

        @if (fileError()) {
          <p class="file-error" role="alert">{{ fileError() }}</p>
        }
      </cp-form-section>

      <div cpDialogActions>
        <!-- "Close", not "Cancel": the uploader's own per-file control is already labelled Cancel. -->
        <button cpButton variant="secondary" type="button" [disabled]="isUploading()" (click)="close()">Close</button>
        <button cpButton type="button" [disabled]="isUploading() || file() === null" (click)="upload()">
          {{ isUploading() ? 'Adding…' : 'Add as version ' + nextVersionNumber() }}
        </button>
      </div>
    </cp-dialog>
  `,
  styles: [
    `
      .standing {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
      .file-error {
        margin: 0;
        color: var(--cp-danger);
        font-size: var(--cp-font-size-sm);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamAssetVersionUploadComponent {
  private readonly assets = inject(DamAssetService);
  private readonly destroyRef = inject(DestroyRef);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  readonly assetId = input.required<string>();
  /** The number of the version currently shown, so the dialog can say which number the new one takes. */
  readonly currentVersionNumber = input(1);

  readonly closed = output<void>();
  /** The version the server stored. The page re-reads the asset: the picture, the list and the token all moved. */
  readonly added = output<DamAssetVersion>();
  /** An upload was aborted part-way. It may or may not have landed, so the page should read the asset again. */
  readonly cancelled = output<void>();

  protected readonly accept = DAM_IMAGE_ACCEPT;

  protected readonly file = signal<File | null>(null);
  protected readonly fileError = signal('');
  protected readonly state = signal<UploadState>({ status: 'choosing' });

  /**
   * The key this upload is made under. Held across retries of the same file — so a retry after a lost response
   * returns the first version instead of adding the same bytes twice — and dropped when the file changes or
   * the server says the number was taken, because either makes it a different upload.
   */
  private idempotencyKey: string | null = null;

  private inFlight: Subscription | null = null;

  /** True while an upload is running. Public, so the page can warn before navigating away from one. */
  readonly isUploading = computed(() => this.state().status === 'uploading');

  protected readonly failure = computed(() => {
    const state = this.state();

    return state.status === 'failed' ? state.message : '';
  });

  protected readonly nextVersionNumber = computed(() => this.currentVersionNumber() + 1);

  protected readonly uploadItems = computed<CpUploadItem[]>(() => {
    const file = this.file();
    if (!file) return [];

    const state = this.state();
    if (state.status === 'uploading') {
      return [{ id: 'file', name: file.name, status: 'uploading', ...(state.percent === null ? {} : { progress: state.percent }) }];
    }
    if (state.status === 'failed') return [{ id: 'file', name: file.name, status: 'error', errorMessage: state.message }];

    return [{ id: 'file', name: file.name, status: 'queued' }];
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.inFlight?.unsubscribe());

    // Each opening starts clean: a file chosen for one asset must not still be sitting here for the next.
    effect(() => {
      if (!this.open()) return;

      untracked(() => {
        if (!this.isUploading()) this.reset();
      });
    });
  }

  protected onFilesSelected(files: FileList): void {
    if (this.isUploading()) return;

    const file = files.item(0);
    if (!file) return;

    this.fileError.set('');
    this.state.set({ status: 'choosing' });
    this.idempotencyKey = null;

    if (file.size > DAM_ASSET_LIMITS.imageMaxBytes) {
      this.file.set(null);
      this.fileError.set('That file is larger than 32 MB, which is the most the library takes.');
      return;
    }

    this.file.set(file);
  }

  protected onRemoveFile(): void {
    if (this.isUploading()) return;

    this.reset();
  }

  /** The uploader's Cancel: aborts the upload while one runs, and otherwise drops the chosen file. */
  protected onCancelItem(): void {
    if (this.inFlight === null) {
      this.onRemoveFile();
      return;
    }

    // Unsubscribing aborts the request. The key goes with it: whether the server stored the file is not known
    // here, so the next attempt is a fresh upload and the page is asked to read the truth.
    this.inFlight.unsubscribe();
    this.inFlight = null;
    this.idempotencyKey = null;
    this.state.set({
      status: 'failed',
      message: 'That upload was cancelled. Your file is still chosen if you want to try again.',
    });
    this.cancelled.emit();
  }

  protected upload(): void {
    const file = this.file();
    if (this.isUploading()) return;
    if (!file) {
      this.fileError.set('Choose the picture that should become the next version.');
      return;
    }

    this.idempotencyKey ??= crypto.randomUUID();
    this.fileError.set('');
    this.state.set({ status: 'uploading', percent: null });

    this.inFlight = this.assets
      .addVersion(this.workspaceSlug(), this.assetId(), file, this.idempotencyKey)
      .subscribe((event) => {
        if (event.kind === 'progress') {
          this.state.set({ status: 'uploading', percent: event.percent });
          return;
        }

        this.inFlight = null;
        this.finish(event.outcome);
      });
  }

  /** Close without uploading. Refused while one runs, so the creator sees how it ended. */
  protected close(): void {
    if (this.isUploading()) return;

    this.reset();
    this.closed.emit();
  }

  private finish(outcome: DamVersionUploadOutcome): void {
    if (outcome.status === 'added') {
      this.added.emit(outcome.version);
      this.reset();
      this.closed.emit();
      return;
    }

    // The number this upload would have taken is gone, so a retry is a different request and needs its own key.
    if (outcome.reason === 'version_taken') this.idempotencyKey = null;

    this.state.set({ status: 'failed', message: FAILURE_MESSAGE[outcome.reason] });
  }

  private reset(): void {
    this.file.set(null);
    this.fileError.set('');
    this.idempotencyKey = null;
    this.state.set({ status: 'choosing' });
  }
}
