import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import {
  CpButtonComponent,
  CpDialogComponent,
  CpFormSectionComponent,
  CpUploadItem,
  CpUploaderComponent,
} from '@creator-pantry/ui';

import {
  BRAND_SOURCE_ACCEPT,
  BRAND_SOURCE_MAX_BYTES,
  BrandSourceDocumentSummary,
} from '../../models/brand-source-document.models';
import { BrandSourceAddFailure, BrandSourceDocumentService } from '../../services/brand-source-document.service';

type ReplaceState =
  | { readonly status: 'editing' }
  | { readonly status: 'uploading' }
  | { readonly status: 'failed'; readonly message: string };

/**
 * Why a replacement did not happen, in the creator's terms. Exhaustive by type, so a failure the service
 * learns to report has to be given words rather than reaching a creator as a blank.
 */
const FAILURE_MESSAGE: Record<BrandSourceAddFailure, string> = {
  unsupported:
    'That file is not one of the kinds the library can read. Try a PDF, Word document, Markdown, plain text, HTML, or an image.',
  too_large: 'That file is too big. Documents and images can be up to 20 MB, and text, Markdown and HTML up to 5 MB.',
  corrupt: 'That file looks damaged: it starts like the kind it claims to be, but could not be read through.',
  rejected: 'That file was turned away by the safety scan, so nothing was saved.',
  invalid: 'That replacement could not be accepted. Check the file and try again.',
  forbidden: 'You need Editor access in this workspace to replace a file.',
  not_found: 'This example could not be found. It may have been removed.',
  key_reused: 'This replacement was started with a different file. Try again.',
  cancelled: 'That upload was cancelled, so nothing was saved and the file on record is unchanged.',

  // Both are handled as their own outcomes by the caller, so neither should reach this map. Worded because
  // the vocabulary is shared, and a silent blank would be worse than a sentence nobody sees.
  conflict: 'This example changed while you were replacing it. Reopen it and try again.',
  archived_conflict: 'This example is on the shelf. Bring it back before replacing its file.',
  unavailable: 'The file could not be replaced just now, and nothing was stored. Your file is still chosen, so try again.',
};

/**
 * Adds a new version of one brand source document from a new file.
 *
 * **Nothing is overwritten and this dialog says so.** The new file becomes the next version; every earlier
 * version keeps its row, its bytes, its download and the text that was read from it. That is the server's
 * behaviour, not a promise made here — and it is why this is a replacement of the *current* file rather than
 * an edit of the document.
 *
 * It replaces the file only. A document's description is not a field here, because the route has none: a
 * replacement never changes what the creator said the example is.
 *
 * **What a file is stays the server's decision.** Only the outer 20 MB ceiling is checked here, to spare a
 * creator an upload that cannot succeed at any size.
 */
@Component({
  selector: 'cp-brand-source-replace',
  standalone: true,
  imports: [CpButtonComponent, CpDialogComponent, CpFormSectionComponent, CpUploaderComponent],
  templateUrl: './brand-source-replace.component.html',
  styleUrl: './brand-source-replace.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSourceReplaceComponent {
  private readonly documents = inject(BrandSourceDocumentService);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  readonly documentId = input.required<string>();

  /** The token from the read this replacement is composed against. A changed token means re-read first. */
  readonly concurrencyToken = input.required<string>();

  /** What the current file is, so the dialog can say what is being replaced rather than just "the file". */
  readonly currentFileName = input('');
  readonly currentVersionNumber = input(1);

  readonly closed = output<void>();
  readonly replaced = output<BrandSourceDocumentSummary>();

  /** The document moved under this replacement. The caller re-reads; this dialog does not guess the new token. */
  readonly conflicted = output<void>();

  protected readonly accept = BRAND_SOURCE_ACCEPT;

  protected readonly file = signal<File | null>(null);
  protected readonly fileError = signal('');

  private readonly stateSignal = signal<ReplaceState>({ status: 'editing' });
  protected readonly state = this.stateSignal.asReadonly();

  /**
   * The key this replacement is being made under. Held across retries of the same logical replacement — so a
   * retry after a dropped response returns the first result instead of adding a second version — and dropped
   * the moment the file changes, because that is a different replacement.
   */
  private readonly idempotencyKey = signal<string | null>(null);

  private readonly inFlight = signal<AbortController | null>(null);

  protected readonly isUploading = computed(() => this.state().status === 'uploading');

  protected readonly failure = computed(() => {
    const state = this.state();
    return state.status === 'failed' ? state.message : '';
  });

  protected readonly nextVersionNumber = computed(() => this.currentVersionNumber() + 1);

  protected readonly uploadItems = computed<CpUploadItem[]>(() => {
    const file = this.file();
    if (!file) return [];

    const state = this.state();
    if (state.status === 'uploading') return [{ id: 'file', name: file.name, status: 'uploading' }];
    if (state.status === 'failed') return [{ id: 'file', name: file.name, status: 'error', errorMessage: state.message }];

    return [{ id: 'file', name: file.name, status: 'queued' }];
  });

  protected onFilesSelected(files: FileList): void {
    const file = files.item(0);
    if (!file) return;

    this.fileError.set('');
    this.stateSignal.set({ status: 'editing' });

    if (file.size > BRAND_SOURCE_MAX_BYTES) {
      // Refused here only because 20 MB is the ceiling for every accepted kind. The lower limit on the text
      // formats is the server's to apply: it knows what the file is.
      this.file.set(null);
      this.fileError.set('That file is larger than 20 MB, which is the most the library takes.');
      return;
    }

    this.file.set(file);
    this.idempotencyKey.set(null);
  }

  protected onRemoveFile(): void {
    if (this.isUploading()) return;

    this.file.set(null);
    this.fileError.set('');
    this.idempotencyKey.set(null);
    this.stateSignal.set({ status: 'editing' });
  }

  /** The uploader's Cancel: aborts the replacement while one runs, and otherwise drops the chosen file. */
  protected onCancelItem(): void {
    const running = this.inFlight();
    if (running) {
      running.abort();
      return;
    }

    this.onRemoveFile();
  }

  protected async replace(): Promise<void> {
    const file = this.file();
    if (!file) {
      this.fileError.set('Choose the file that should replace this one.');
      return;
    }

    const key = this.idempotencyKey() ?? crypto.randomUUID();
    this.idempotencyKey.set(key);
    this.stateSignal.set({ status: 'uploading' });

    const abort = new AbortController();
    this.inFlight.set(abort);

    const outcome = await this.documents.replace(
      this.workspaceSlug(),
      this.documentId(),
      file,
      this.concurrencyToken(),
      key,
      abort.signal,
    );

    this.inFlight.set(null);

    switch (outcome.status) {
      case 'replaced':
        this.replaced.emit(outcome.document);
        this.reset();
        this.closed.emit();
        return;

      case 'conflict':
        // The token this was composed against is stale, so the key is too: the next attempt is a different
        // replacement against a document the creator has not seen yet. The file is kept.
        this.idempotencyKey.set(null);
        this.stateSignal.set({
          status: 'failed',
          message:
            'This example changed while you were replacing it, so nothing was stored. Close this, check what changed, then replace it again — your file is still here.',
        });
        this.conflicted.emit();
        return;

      case 'archived':
        this.stateSignal.set({
          status: 'failed',
          message: 'This example is on the shelf, and nothing on the shelf takes a new file. Bring it back first.',
        });
        return;

      default:
        this.stateSignal.set({ status: 'failed', message: FAILURE_MESSAGE[outcome.reason] });
    }
  }

  /** Closing mid-upload would leave the creator unable to see how it ended, so it is refused while one runs. */
  protected cancel(): void {
    if (this.isUploading()) return;

    this.reset();
    this.closed.emit();
  }

  private reset(): void {
    this.inFlight.set(null);
    this.file.set(null);
    this.fileError.set('');
    this.idempotencyKey.set(null);
    this.stateSignal.set({ status: 'editing' });
  }
}
