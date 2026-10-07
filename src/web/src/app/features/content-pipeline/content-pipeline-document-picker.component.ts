import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CpButtonComponent, CpChoiceGroupComponent, CpChoiceOption, CpFieldComponent, CpNoticeComponent, CpUploadItem, CpUploaderComponent } from '@creator-pantry/ui';

import { ContentPipelineDocumentRef } from '../../models/content-pipeline.models';
import {
  BRAND_SOURCE_ACCEPT,
  BrandLibraryRow,
  DEFAULT_BRAND_LIBRARY_QUERY,
} from '../../models/brand-source-document.models';
import { BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { BRAND_SOURCE_ADD_FAILURE_MESSAGE } from '../../shared/brand-source-messages';

/** What the document is for, which decides what may be chosen and how an upload is filed. */
export type PipelineDocumentKind = 'brief' | 'reference';

/** Images only, for a reference. `.gif` is left out because the library's own accept list does not carry it. */
const IMAGE_ACCEPT = '.png,.jpg,.jpeg,.webp';

type LibraryState =
  | { readonly status: 'closed' }
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly rows: readonly BrandLibraryRow[]; readonly more: boolean }
  | { readonly status: 'unavailable' };

type UploadState =
  | { readonly status: 'idle' }
  | { readonly status: 'uploading'; readonly name: string }
  | { readonly status: 'failed'; readonly name: string; readonly message: string };

function isImage(row: BrandLibraryRow): boolean {
  return row.mediaType.startsWith('image/');
}

/**
 * Choose one of the creator's own documents, or add a new one — for a brief, or for a reference photograph.
 *
 * **Why both routes read the library and nothing else.** IMG-002 takes a `briefDocumentId` and IMG-004 a
 * `referenceDocumentId`, each resolved through the brand module's facade under the workspace filter. So
 * "authorized" is machinery that already exists, and the upload here is the Brand Library's own route — where a
 * file's signature, decoded format, dimensions and size were already inspected. A second upload surface would be
 * a second set of those checks to keep in step.
 *
 * **Nothing is filtered out of the list.** A page is a page of a server-filtered set, so narrowing it in the
 * browser would hide matches sitting on pages this screen has not fetched. A document that cannot serve as a
 * reference is shown **disabled with the reason** instead, which is what `cp-brand-visual-style-control` does
 * for the same problem.
 *
 * **Cancel really cancels.** An upload runs under an `AbortSignal`, and aborting answers `cancelled` — worded as
 * "nothing was saved", because the server may still have received the bytes.
 */
@Component({
  selector: 'cp-content-pipeline-document-picker',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFieldComponent,
    CpNoticeComponent,
    CpUploaderComponent,
  ],
  templateUrl: './content-pipeline-document-picker.component.html',
  styleUrl: './content-pipeline-document-picker.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineDocumentPickerComponent {
  private readonly documents = inject(BrandSourceDocumentService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly kind = input.required<PipelineDocumentKind>();
  readonly selected = input<ContentPipelineDocumentRef | null>(null);
  readonly disabled = input(false);
  readonly selectedChange = output<ContentPipelineDocumentRef | null>();

  protected readonly library = signal<LibraryState>({ status: 'closed' });
  protected readonly upload = signal<UploadState>({ status: 'idle' });
  protected readonly search = signal('');

  private inFlight: AbortController | null = null;
  private idempotencyKey: string | null = null;

  protected readonly imagesOnly = computed(() => this.kind() === 'reference');
  protected readonly accept = computed(() => (this.imagesOnly() ? IMAGE_ACCEPT : BRAND_SOURCE_ACCEPT));

  protected readonly uploading = computed(() => this.upload().status === 'uploading');

  constructor() {
    this.destroyRef.onDestroy(() => this.inFlight?.abort());
  }

  /** The library rows as choices. A document that cannot serve this purpose is offered disabled, with why. */
  protected readonly choices = computed<readonly CpChoiceOption[]>(() => {
    const state = this.library();
    if (state.status !== 'ready') return [];

    return state.rows.map((row) => {
      const usable = !this.imagesOnly() || isImage(row);

      return {
        value: row.id,
        label: row.title,
        hint: usable ? row.fileName : `${row.fileName} — not an image, so it cannot be read as a reference`,
        disabled: !usable,
      };
    });
  });

  protected readonly noUsableRows = computed(() => {
    const state = this.library();

    return state.status === 'ready' && state.rows.length > 0 && this.choices().every((choice) => choice.disabled);
  });

  /** The chosen file as the uploader renders it: one item, carrying the state of the add. */
  protected readonly uploadItems = computed<CpUploadItem[]>(() => {
    const state = this.upload();
    if (state.status === 'idle') return [];
    if (state.status === 'uploading') return [{ id: 'file', name: state.name, status: 'uploading' }];

    return [{ id: 'file', name: state.name, status: 'error', errorMessage: state.message }];
  });

  protected openLibrary(): void {
    void this.loadLibrary();
  }

  protected closeLibrary(): void {
    this.library.set({ status: 'closed' });
  }

  /**
   * One page of the library.
   *
   * The first page only: this is a picker, not the Brand Library, and the way past the first page of matches is
   * to type more of the name rather than to page through someone's whole collection here.
   */
  protected async loadLibrary(): Promise<void> {
    this.library.set({ status: 'loading' });

    const outcome = await firstValueFrom(
      this.documents.searchLibrary(this.workspaceSlug(), {
        ...DEFAULT_BRAND_LIBRARY_QUERY,
        search: this.search().trim(),
        limit: 25,
      }),
    );

    this.library.set(
      outcome.status === 'ok'
        ? { status: 'ready', rows: outcome.page.items, more: outcome.page.nextCursor !== null }
        : { status: 'unavailable' },
    );
  }

  protected choose(ids: readonly string[]): void {
    const state = this.library();
    if (state.status !== 'ready') return;

    const row = state.rows.find((candidate) => candidate.id === ids[0]);
    if (row === undefined) return;

    this.selectedChange.emit({ documentId: row.id, title: row.title });
    this.closeLibrary();
  }

  protected clear(): void {
    this.selectedChange.emit(null);
  }

  protected onFiles(files: FileList): void {
    const file = files.item(0);
    if (file === null) return;

    // A different file is a different add, so the key the last one would have been made under is dropped.
    this.idempotencyKey = null;
    void this.send(file);
  }

  /**
   * The uploader's Cancel.
   *
   * Aborts the add while one is running and otherwise clears the failure. A control that did nothing in one of
   * those two states would be a dead end in whichever one it was.
   */
  protected onCancelUpload(): void {
    if (this.inFlight !== null) {
      this.inFlight.abort();
      return;
    }

    this.upload.set({ status: 'idle' });
  }

  protected onRetryUpload(): void {
    // Nothing to retry from: the File object is not kept after a failure, so the creator chooses again. Said
    // plainly rather than offering a Retry that silently does nothing.
    this.upload.set({ status: 'idle' });
  }

  private async send(file: File): Promise<void> {
    const key = this.idempotencyKey ?? crypto.randomUUID();
    this.idempotencyKey = key;

    const abort = new AbortController();
    this.inFlight = abort;
    this.upload.set({ status: 'uploading', name: file.name });

    const outcome = await this.documents.upload(
      this.workspaceSlug(),
      file,
      {
        // Filed where the library already means "something that shapes how this looks", so a brief and a
        // reference sit beside each other and the Brand Library's own filters find both.
        title: titleFrom(file.name),
        documentType: 'VisualReference',
        purpose: 'VisualDirection',
      },
      key,
      abort.signal,
    );

    this.inFlight = null;

    if (outcome.status === 'added') {
      this.upload.set({ status: 'idle' });
      this.idempotencyKey = null;
      this.selectedChange.emit({ documentId: outcome.document.id, title: outcome.document.title });
      return;
    }

    this.upload.set({
      status: 'failed',
      name: file.name,
      message: BRAND_SOURCE_ADD_FAILURE_MESSAGE[outcome.reason],
    });
  }
}

/** The file's own name, without its extension, as the library title. Renaming it is the Brand Library's job. */
function titleFrom(fileName: string): string {
  const withoutExtension = fileName.replace(/\.[^.]+$/, '').trim();

  return withoutExtension === '' ? fileName : withoutExtension.slice(0, 200);
}
