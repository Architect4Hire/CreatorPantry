import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import {
  CpButtonComponent,
  CpDialogComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpUploadItem,
  CpUploaderComponent,
} from '@creator-pantry/ui';

import { DAM_ASSET_LIMITS, DAM_IMAGE_ACCEPT, DamCreatedAsset } from '../../models/dam-asset.models';
import { DamAssetService, DamAssetUploadFailure, DamAssetUploadOutcome } from '../../services/dam-asset.service';

type UploadState =
  | { readonly status: 'choosing' }
  /** `percent` is null until the browser can say how far along it is. */
  | { readonly status: 'uploading'; readonly percent: number | null }
  | { readonly status: 'failed'; readonly message: string };

/**
 * Why a picture was not added, in the creator's terms. Exhaustive by type, so a failure the service learns to
 * report has to be given words rather than reaching a creator as a blank.
 */
const FAILURE_MESSAGE: Readonly<Record<DamAssetUploadFailure, string>> = {
  unsupported:
    'That file is not a picture the library takes. Try a PNG, JPEG, WebP or GIF. Nothing was stored.',
  too_large: 'That file is larger than 32 MB, which is the most the library takes. Nothing was stored.',
  invalid: 'That picture could not be accepted. Check the details below and try again.',
  forbidden: 'You need Contributor access in this workspace to add a picture. Nothing was stored.',
  unavailable: 'The picture could not be stored just now, and nothing was left behind. Your file is still chosen, so try again.',
};

/**
 * Adds a creator's own picture to the library as a new asset.
 *
 * **The same kind of asset a kept generated picture becomes.** It lands in the library with a title and,
 * optionally, alt text, and the page it opens on is the one every asset has — where the rest of its details,
 * its recipe links, its versions and its uses are edited. This dialog asks only for what the library cannot
 * do without.
 *
 * **The title is the creator's, never the filename.** A filename is untrusted display text and the server
 * refuses an asset without a title rather than borrow one.
 *
 * **Alt text is theirs too, and optional.** Nothing has looked at the pixels, so nothing here suggests what
 * the picture shows.
 *
 * **What a file is stays the server's decision.** Only the 32 MB ceiling is checked here, to spare a creator a
 * transfer that cannot succeed.
 *
 * **Progress is the real transfer**, **Cancel aborts it**, and **closing is refused while an upload runs**, so
 * a creator always sees how it ended.
 */
@Component({
  selector: 'cp-dam-asset-upload',
  standalone: true,
  imports: [CpButtonComponent, CpDialogComponent, CpFieldComponent, CpFormSectionComponent, CpUploaderComponent],
  template: `
    <cp-dialog
      [open]="open()"
      title="Upload a picture"
      description="Add one of your own pictures to the library."
      titleId="cp-dam-upload-title"
      (closed)="close()"
      (keydown.escape)="close()"
    >
      <p class="legend">Title is required. Alt text is optional, and you can add the rest of the details afterwards.</p>

      <cp-form-section heading="The picture" [problem]="failure()">
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

      <cp-form-section heading="About it">
        <cp-field label="Title" forId="cp-dam-upload-title-field" [required]="true" [error]="titleError()">
          <input
            type="text"
            id="cp-dam-upload-title-field"
            autocomplete="off"
            [attr.maxlength]="limits.titleMaxLength"
            [disabled]="isUploading()"
            [value]="title()"
            (input)="setTitle($event)"
          />
        </cp-field>

        <cp-field
          label="Alt text"
          forId="cp-dam-upload-alt-field"
          hint="What the picture shows, in your own words, for anyone who cannot see it."
          [error]="altTextError()"
        >
          <textarea
            rows="3"
            id="cp-dam-upload-alt-field"
            [attr.maxlength]="limits.altTextMaxLength"
            [disabled]="isUploading()"
            [value]="altText()"
            (input)="setAltText($event)"
          ></textarea>
        </cp-field>
      </cp-form-section>

      <div cpDialogActions>
        <!-- "Close", not "Cancel": the uploader's own per-file control is already labelled Cancel. -->
        <button cpButton variant="secondary" type="button" [disabled]="isUploading()" (click)="close()">Close</button>
        <button cpButton type="button" [disabled]="isUploading()" (click)="upload()">
          {{ isUploading() ? 'Uploading…' : 'Add to the library' }}
        </button>
      </div>
    </cp-dialog>
  `,
  styles: [
    `
      .legend {
        margin: 0 0 var(--cp-space-4);
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
export class DamAssetUploadComponent {
  private readonly assets = inject(DamAssetService);
  private readonly destroyRef = inject(DestroyRef);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();

  readonly closed = output<void>();
  /** The asset the server created. */
  readonly uploaded = output<DamCreatedAsset>();
  /** An upload was aborted part-way. It may or may not have landed, so the page should read the library again. */
  readonly cancelled = output<void>();

  protected readonly accept = DAM_IMAGE_ACCEPT;
  protected readonly limits = DAM_ASSET_LIMITS;

  protected readonly file = signal<File | null>(null);
  protected readonly fileError = signal('');
  protected readonly title = signal('');
  protected readonly titleError = signal('');
  protected readonly altText = signal('');
  protected readonly altTextError = signal('');
  protected readonly state = signal<UploadState>({ status: 'choosing' });

  /**
   * The key this upload is made under. Held across retries of the same file and details — so a retry after a
   * lost response returns the first asset instead of adding the picture twice — and dropped when either
   * changes, because that makes it a different request.
   */
  private idempotencyKey: string | null = null;

  private inFlight: Subscription | null = null;

  /** True while an upload is running. Public, so the page can warn before navigating away from one. */
  readonly isUploading = computed(() => this.state().status === 'uploading');

  protected readonly failure = computed(() => {
    const state = this.state();

    return state.status === 'failed' ? state.message : '';
  });

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

    // Each opening starts clean: a picture chosen in one visit must not still be sitting here for the next.
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

    this.file.set(null);
    this.fileError.set('');
    this.idempotencyKey = null;
    this.state.set({ status: 'choosing' });
  }

  /** The uploader's Cancel: aborts the upload while one runs, and otherwise drops the chosen file. */
  protected onCancelItem(): void {
    if (this.inFlight === null) {
      this.onRemoveFile();
      return;
    }

    // Unsubscribing aborts the request. The key goes with it: whether the server stored the picture is not
    // known here, so the next attempt is a fresh upload and the page is asked to read the truth.
    this.inFlight.unsubscribe();
    this.inFlight = null;
    this.idempotencyKey = null;
    this.state.set({
      status: 'failed',
      message: 'That upload was cancelled. Your file and details are still here if you want to try again.',
    });
    this.cancelled.emit();
  }

  protected setTitle(event: Event): void {
    this.title.set((event.target as HTMLInputElement).value);
    this.titleError.set('');
    this.idempotencyKey = null;
  }

  protected setAltText(event: Event): void {
    this.altText.set((event.target as HTMLTextAreaElement).value);
    this.altTextError.set('');
    this.idempotencyKey = null;
  }

  protected upload(): void {
    if (this.isUploading()) return;

    const file = this.file();
    const title = this.title().trim();

    // Both said at once, so a creator fixes the form in one pass rather than one refusal at a time.
    this.fileError.set(file ? '' : 'Choose the picture to add.');
    this.titleError.set(title === '' ? 'Give the picture a title, so it can be found in the library.' : '');
    if (!file || title === '') return;

    this.idempotencyKey ??= crypto.randomUUID();
    this.state.set({ status: 'uploading', percent: null });

    this.inFlight = this.assets
      .upload(this.workspaceSlug(), file, { title, altText: this.altText().trim() }, this.idempotencyKey)
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

  private finish(outcome: DamAssetUploadOutcome): void {
    if (outcome.status === 'created') {
      this.uploaded.emit(outcome.asset);
      this.reset();
      this.closed.emit();
      return;
    }

    this.titleError.set(outcome.fieldErrors['title']?.[0] ?? '');
    this.altTextError.set(outcome.fieldErrors['altText']?.[0] ?? '');
    this.state.set({ status: 'failed', message: FAILURE_MESSAGE[outcome.reason] });
  }

  private reset(): void {
    this.file.set(null);
    this.fileError.set('');
    this.title.set('');
    this.titleError.set('');
    this.altText.set('');
    this.altTextError.set('');
    this.idempotencyKey = null;
    this.state.set({ status: 'choosing' });
  }
}
