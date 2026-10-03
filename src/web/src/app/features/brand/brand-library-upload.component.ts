import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import {
  CpButtonComponent,
  CpDialogComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpUploadItem,
  CpUploaderComponent,
} from '@creator-pantry/ui';

import {
  BRAND_SOURCE_ACCEPT,
  BRAND_SOURCE_AUDIENCE_MAX,
  BRAND_SOURCE_DOCUMENT_TYPES,
  BRAND_SOURCE_MAX_BYTES,
  BRAND_SOURCE_PURPOSES,
  BRAND_SOURCE_TITLE_MAX,
  BrandSourceDocumentSummary,
  BrandSourceDocumentType,
  BrandSourcePurpose,
} from '../../models/brand-source-document.models';
import { BrandSourceAddFailure, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { DOCUMENT_TYPE_LABELS, PURPOSE_LABELS } from './brand-library-labels';

type UploadState =
  | { readonly status: 'editing' }
  | { readonly status: 'uploading' }
  | { readonly status: 'failed'; readonly message: string };

/**
 * Why an add did not happen, in the creator's terms. Exhaustive by type, so a failure the service learns to
 * report has to be given words here rather than falling through to a default that would describe it wrongly.
 */
const FAILURE_MESSAGE: Record<BrandSourceAddFailure, string> = {
  unsupported:
    'That file is not one of the kinds the library can read. Try a PDF, Word document, Markdown, plain text, HTML, or an image.',
  too_large: 'That file is too big. Documents and images can be up to 20 MB, and text, Markdown and HTML up to 5 MB.',
  corrupt: 'That file looks damaged: it starts like the kind it claims to be, but could not be read through.',
  rejected: 'That file was turned away by the safety scan, so nothing was saved.',
  invalid: 'Something about this example could not be accepted. Check the name and try again.',
  forbidden: 'You need Editor access in this workspace to add an example.',
  not_found: 'This workspace could not be found. It may have been renamed.',
  key_reused: 'This upload was started with details that have since changed. Add it again.',
  cancelled: 'That upload was cancelled, so nothing was saved.',

  // Neither can reach an upload, which creates a document rather than changing one. Worded anyway because
  // the vocabulary is shared with the replacement, and an unworded case would reach a creator as a blank.
  conflict: 'This example changed while you were adding it. Open it again to see where it stands.',
  archived_conflict: 'This example is on the shelf. Bring it back before changing it.',
  unavailable: 'The example could not be saved just now, and nothing was stored. Your details are still here, so try again.',
};

/**
 * Adds one example to the Brand Library: a file, plus the creator's description of what it is and what it is
 * evidence of.
 *
 * A modal that **hosts a form** — its own fields, validation and focus order — which is what
 * `CpDialogComponent` is for. It is not a confirmation, so it does not go through `ConfirmService`.
 *
 * **What a file is stays the server's decision.** Only the outer 20 MB ceiling is checked here, to spare a
 * creator an upload that cannot succeed at any size; everything about the kind of file comes from its bytes,
 * so an extension is never treated as a fact and a refusal is reported in the server's own terms.
 */
@Component({
  selector: 'cp-brand-library-upload',
  standalone: true,
  imports: [CpButtonComponent, CpDialogComponent, CpFieldComponent, CpFormSectionComponent, CpUploaderComponent],
  templateUrl: './brand-library-upload.component.html',
  styleUrl: './brand-library-upload.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandLibraryUploadComponent {
  private readonly documents = inject(BrandSourceDocumentService);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();

  readonly closed = output<void>();
  readonly uploaded = output<BrandSourceDocumentSummary>();

  protected readonly accept = BRAND_SOURCE_ACCEPT;
  protected readonly titleMax = BRAND_SOURCE_TITLE_MAX;
  protected readonly audienceMax = BRAND_SOURCE_AUDIENCE_MAX;
  protected readonly documentTypes = BRAND_SOURCE_DOCUMENT_TYPES;
  protected readonly purposes = BRAND_SOURCE_PURPOSES;
  protected readonly typeLabels = DOCUMENT_TYPE_LABELS;
  protected readonly purposeLabels = PURPOSE_LABELS;

  protected readonly title = signal('');
  protected readonly documentType = signal<BrandSourceDocumentType | ''>('');
  protected readonly purpose = signal<BrandSourcePurpose | ''>('');
  protected readonly audience = signal('');
  protected readonly file = signal<File | null>(null);

  private readonly stateSignal = signal<UploadState>({ status: 'editing' });
  protected readonly state = this.stateSignal.asReadonly();

  /** Set once the creator has tried to add, so nothing is marked wrong before they have finished typing. */
  private readonly submitted = signal(false);

  /**
   * The key this add is being made under. Held across retries of the same logical add — so a retry after a
   * dropped response returns the first result instead of storing a second copy — and dropped the moment any
   * part of the description or the file changes, because that is a different add.
   */
  private readonly idempotencyKey = signal<string | null>(null);

  /**
   * The add that is running, so the uploader's Cancel can stop it. Aborting answers `cancelled`, which says
   * plainly that nothing was saved — the honest wording, because the server may still have received the
   * request. The key is kept, so adding again replays rather than storing a second copy.
   */
  private readonly inFlight = signal<AbortController | null>(null);

  protected readonly titleError = computed(() => {
    if (!this.submitted()) return '';
    const text = this.title().trim();
    if (text.length === 0) return 'Give this example a name you will recognise.';
    return text.length > this.titleMax ? `Keep the name to ${this.titleMax} characters or fewer.` : '';
  });

  protected readonly typeError = computed(() =>
    this.submitted() && this.documentType() === '' ? 'Say what kind of example this is.' : '',
  );

  protected readonly purposeError = computed(() =>
    this.submitted() && this.purpose() === '' ? 'Say what this example is evidence of.' : '',
  );

  protected readonly audienceError = computed(() =>
    this.submitted() && this.audience().trim().length > this.audienceMax
      ? `Keep this to ${this.audienceMax} characters or fewer.`
      : '',
  );

  /** The file problem, which belongs beside the file rather than on a field. */
  protected readonly fileError = signal('');

  /** The chosen file as the uploader renders it: one item, carrying the state of the add. */
  protected readonly uploadItems = computed<CpUploadItem[]>(() => {
    const file = this.file();
    if (!file) return [];

    const state = this.state();
    if (state.status === 'uploading') return [{ id: 'file', name: file.name, status: 'uploading' }];
    if (state.status === 'failed') return [{ id: 'file', name: file.name, status: 'error', errorMessage: state.message }];

    return [{ id: 'file', name: file.name, status: 'queued' }];
  });

  protected readonly isUploading = computed(() => this.state().status === 'uploading');

  protected readonly failure = computed(() => {
    const state = this.state();
    return state.status === 'failed' ? state.message : '';
  });

  protected onTitle(event: Event): void {
    this.title.set(event.target instanceof HTMLInputElement ? event.target.value : '');
    this.describedDifferently();
  }

  protected onAudience(event: Event): void {
    this.audience.set(event.target instanceof HTMLInputElement ? event.target.value : '');
    this.describedDifferently();
  }

  protected onDocumentType(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';

    // An unrecognised value becomes "not chosen" rather than being sent on: these are allow-lists the server
    // enforces too, and guessing here would turn a broken control into a refusal nobody can read.
    this.documentType.set(
      (this.documentTypes as readonly string[]).includes(value) ? (value as BrandSourceDocumentType) : '',
    );
    this.describedDifferently();
  }

  protected onPurpose(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';
    this.purpose.set((this.purposes as readonly string[]).includes(value) ? (value as BrandSourcePurpose) : '');
    this.describedDifferently();
  }

  protected onFilesSelected(files: FileList): void {
    const file = files.item(0);
    if (!file) return;

    this.fileError.set('');
    this.stateSignal.set({ status: 'editing' });

    if (file.size > BRAND_SOURCE_MAX_BYTES) {
      // Refused here only because 20 MB is the ceiling for every accepted kind, so no upload this large can
      // succeed. The lower limit on the text formats is the server's to apply: it knows what the file is.
      this.file.set(null);
      this.fileError.set('That file is larger than 20 MB, which is the most the library takes.');
      return;
    }

    this.file.set(file);
    this.describedDifferently();

    // A name to recognise it by, offered rather than imposed: it is editable, and a creator who has already
    // named this example keeps their own words.
    if (this.title().trim().length === 0) this.title.set(file.name);
  }

  protected onRemoveFile(): void {
    if (this.isUploading()) return;

    this.file.set(null);
    this.fileError.set('');
    this.stateSignal.set({ status: 'editing' });
    this.describedDifferently();
  }

  /**
   * The uploader's Cancel: stops the add while one is running, and otherwise drops the chosen file. A control
   * that did nothing in one of those two states would be a dead end in whichever one it was.
   */
  protected onCancelItem(): void {
    const running = this.inFlight();
    if (running) {
      running.abort();
      return;
    }

    this.onRemoveFile();
  }

  protected async add(): Promise<void> {
    this.submitted.set(true);

    const file = this.file();
    if (!file) {
      this.fileError.set('Choose the file you want to add.');
      return;
    }

    const documentType = this.documentType();
    const purpose = this.purpose();

    if (
      this.titleError() ||
      this.typeError() ||
      this.purposeError() ||
      this.audienceError() ||
      documentType === '' ||
      purpose === ''
    ) {
      return;
    }

    const key = this.idempotencyKey() ?? crypto.randomUUID();
    this.idempotencyKey.set(key);
    this.stateSignal.set({ status: 'uploading' });

    const abort = new AbortController();
    this.inFlight.set(abort);

    const audience = this.audience().trim();
    const outcome = await this.documents.upload(
      this.workspaceSlug(),
      file,
      {
        title: this.title().trim(),
        documentType,
        purpose,
        ...(audience.length > 0 ? { audience } : {}),
      },
      key,
      abort.signal,
    );

    this.inFlight.set(null);

    if (outcome.status === 'added') {
      this.uploaded.emit(outcome.document);
      this.reset();
      this.closed.emit();
      return;
    }

    this.stateSignal.set({ status: 'failed', message: FAILURE_MESSAGE[outcome.reason] });
  }

  /** Closing mid-upload would leave the creator unable to see how it ended, so it is refused while one runs. */
  protected cancel(): void {
    if (this.isUploading()) return;

    this.reset();
    this.closed.emit();
  }

  /**
   * A different description is a different add, so the key it would be made under is dropped. Replaying the
   * old key with changed details is what the server answers `idempotency.key_reused` to, and rightly.
   */
  private describedDifferently(): void {
    this.idempotencyKey.set(null);
    if (this.state().status === 'failed') this.stateSignal.set({ status: 'editing' });
  }

  private reset(): void {
    this.inFlight.set(null);
    this.title.set('');
    this.documentType.set('');
    this.purpose.set('');
    this.audience.set('');
    this.file.set(null);
    this.fileError.set('');
    this.submitted.set(false);
    this.idempotencyKey.set(null);
    this.stateSignal.set({ status: 'editing' });
  }
}
