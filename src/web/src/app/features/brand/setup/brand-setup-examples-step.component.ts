import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import {
  CpButtonComponent,
  CpCheckboxComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpStatusPillComponent,
  CpUploaderComponent,
} from '@creator-pantry/ui';

import { BrandSourceDocumentSummary, BRAND_SOURCE_ACCEPT, BRAND_SOURCE_AUDIENCE_MAX, BRAND_SOURCE_TEXT_MAX_BYTES, BRAND_SOURCE_TITLE_MAX } from '../../../models/brand-source-document.models';
import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';
import {
  BrandSourceAddFailure,
  BrandSourceAddOutcome,
  BrandSourceDocumentService,
} from '../../../services/brand-source-document.service';
import {
  EXAMPLE_KINDS,
  ExampleKind,
  FAILURE_COPY,
  SavedExample,
  canRetry,
  decodeExamples,
  formatSize,
  kindOfDocument,
  kindOption,
  pastedTitle,
  precheckFile,
} from './brand-setup-examples';

type EntryStatus = 'adding' | 'added' | 'failed';

interface Entry {
  readonly id: string;
  readonly documentId: string | null;
  readonly name: string;
  readonly detail: string;
  readonly kind: ExampleKind;
  readonly status: EntryStatus;
  readonly failure: BrandSourceAddFailure | null;
}

/** What a retry needs to send the same request again, under the same key. */
interface Job {
  readonly kind: ExampleKind;
  readonly audience: string;
  readonly key: string;
  readonly file?: File;
  readonly text?: string;
  readonly title: string;
  readonly abort: AbortController;
}

type Library = 'loading' | 'ready' | 'degraded';

/**
 * Step 3 of "Create my voice": the creator adds examples of their writing (and looks) — by uploading a file,
 * pasting text, or picking something already in their library — and says what each one shows. Everything is
 * optional. This step adds documents and nothing else: it reads no text out of them and generates nothing.
 * The shell owns Continue, Skip and the autosave; this step reports only the ids and labels to remember.
 */
@Component({
  selector: 'cp-brand-setup-examples-step',
  standalone: true,
  imports: [CpButtonComponent, CpCheckboxComponent, CpFieldComponent, CpFormSectionComponent, CpStatusPillComponent, CpUploaderComponent],
  templateUrl: './brand-setup-examples-step.component.html',
  styleUrl: './brand-setup-examples-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupExamplesStepComponent implements OnInit {
  readonly step = input.required<BrandSetupStepDefinition>();
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  readonly workspaceSlug = input.required<string>();
  readonly reported = output<BrandSetupStepReport>();

  private readonly documents = inject(BrandSourceDocumentService);

  protected readonly kinds = EXAMPLE_KINDS;
  protected readonly accept = BRAND_SOURCE_ACCEPT;
  protected readonly titleMax = BRAND_SOURCE_TITLE_MAX;
  protected readonly audienceMax = BRAND_SOURCE_AUDIENCE_MAX;

  protected readonly kind = signal<ExampleKind | null>(null);
  protected readonly entries = signal<readonly Entry[]>([]);
  protected readonly library = signal<Library>('loading');
  protected readonly libraryDocs = signal<readonly BrandSourceDocumentSummary[]>([]);
  protected readonly note = signal('');
  protected readonly announcement = signal('');

  protected readonly audience = signal('');
  protected readonly pasteOpen = signal(false);
  protected readonly pasteText = signal('');
  protected readonly pasteTitle = signal('');
  protected readonly pasteProblem = signal('');
  protected readonly pickerOpen = signal(false);
  protected readonly picked = signal<ReadonlySet<string>>(new Set());

  private readonly jobs = new Map<string, Job>();
  private baseline = '[]';
  private ready = false;
  private edited = false;
  private hadSavedDraft = false;
  private counter = 0;

  protected readonly adding = computed(() => this.entries().some((e) => e.status === 'adding'));
  protected readonly addedCount = computed(() => this.entries().filter((e) => e.status === 'added').length);
  /** Existing documents the creator has not already used here. */
  protected readonly pickable = computed(() => {
    const used = new Set(this.entries().map((e) => e.documentId));
    return this.libraryDocs().filter((d) => !used.has(d.id));
  });
  protected readonly canAdd = computed(() => this.kind() !== null);
  protected readonly selectedKind = computed(() => (this.kind() === null ? null : kindOption(this.kind()!)));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.jobs.forEach((job) => job.abort.abort()));

    effect(() => {
      const entries = this.entries();
      const adding = this.adding();
      if (!this.ready) return;
      const saved: SavedExample[] = entries
        .filter((e): e is Entry & { documentId: string } => e.status === 'added' && e.documentId !== null)
        .map((e) => ({ documentId: e.documentId, kind: e.kind }));
      const serialised = JSON.stringify(saved);
      const dirty = serialised !== this.baseline || adding;
      if (serialised !== this.baseline) this.edited = true;
      // Once anything has been added or removed, every later state is reported, including a return to empty.
      const draft = this.edited || this.hadSavedDraft ? ({ items: saved } as Record<string, unknown>) : null;
      this.reported.emit({ canContinue: !adding, isDirty: dirty, draft });
    });
  }

  ngOnInit(): void {
    const saved = decodeExamples(this.draft());
    this.hadSavedDraft = this.draft() !== null && Object.keys(this.draft()!).length > 0;
    this.baseline = JSON.stringify(saved);
    this.pasteTitle.set(pastedTitle(new Date()));
    this.ready = true;
    // Until the library answers, remembered examples show by id so nothing the creator added is lost.
    this.entries.set(saved.map((s) => this.entryForSaved(s)));
    void this.loadLibrary(saved);
  }

  private async loadLibrary(saved: readonly SavedExample[]): Promise<void> {
    const outcome = await this.documents.list(this.workspaceSlug());
    if (outcome.status !== 'ok') {
      this.library.set('degraded');
      return;
    }
    const docs = outcome.page.items;
    this.libraryDocs.set(docs);
    this.library.set('ready');

    const byId = new Map(docs.map((d) => [d.id, d]));
    const kept = saved.filter((s) => byId.has(s.documentId));
    const lost = saved.length - kept.length;
    // Rebuild from the server so names and sizes are real; examples that were removed elsewhere drop off.
    this.entries.update((current) => [
      ...kept.map((s) => this.entryForDocument(byId.get(s.documentId)!, s.kind)),
      ...current.filter((e) => e.documentId === null || !saved.some((s) => s.documentId === e.documentId)),
    ]);
    if (lost > 0) {
      this.note.set(lost === 1 ? 'An example you added earlier is no longer available, so it was left off.' : 'Some examples you added earlier are no longer available, so they were left off.');
    }
  }

  // ---- Choosing what an example shows ----

  protected chooseKind(key: ExampleKind): void {
    this.kind.set(key);
  }

  // ---- Files ----

  protected onFiles(files: FileList): void {
    const kind = this.kind();
    if (kind === null) return;
    for (const file of Array.from(files)) this.startFile(file, kind);
  }

  private startFile(file: File, kind: ExampleKind): void {
    const id = this.nextId();
    const failure = precheckFile(file);
    const entry: Entry = { id, documentId: null, name: file.name, detail: formatSize(file.size), kind, status: failure ? 'failed' : 'adding', failure };
    this.entries.update((all) => [...all, entry]);
    if (failure) {
      this.announce(`${file.name} was not added. ${FAILURE_COPY[failure]}`);
      return;
    }
    this.jobs.set(id, { kind, audience: this.audience().trim(), key: crypto.randomUUID(), file, title: file.name, abort: new AbortController() });
    void this.run(id);
  }

  // ---- Pasted text ----

  protected togglePaste(): void {
    this.pasteOpen.update((open) => !open);
    this.pasteProblem.set('');
  }

  protected setPasteText(event: Event): void {
    this.pasteText.set((event.target as HTMLTextAreaElement).value);
    this.pasteProblem.set('');
  }

  protected setPasteTitle(event: Event): void {
    this.pasteTitle.set((event.target as HTMLInputElement).value);
  }

  protected addPasted(): void {
    const kind = this.kind();
    const text = this.pasteText();
    if (kind === null) return;
    if (text.trim() === '') {
      this.pasteProblem.set('Paste some text to add.');
      return;
    }
    if (new TextEncoder().encode(text).length > BRAND_SOURCE_TEXT_MAX_BYTES) {
      this.pasteProblem.set('That is more than 5 MB of text. Try pasting a shorter piece.');
      return;
    }
    const title = this.pasteTitle().trim() || pastedTitle(new Date());
    const id = this.nextId();
    this.entries.update((all) => [...all, { id, documentId: null, name: title, detail: 'Pasted text', kind, status: 'adding', failure: null }]);
    this.jobs.set(id, { kind, audience: this.audience().trim(), key: crypto.randomUUID(), text, title, abort: new AbortController() });
    this.pasteText.set('');
    this.pasteTitle.set(pastedTitle(new Date()));
    this.pasteOpen.set(false);
    void this.run(id);
  }

  // ---- Running, retrying, cancelling, removing ----

  private async run(id: string): Promise<void> {
    const job = this.jobs.get(id);
    if (!job) return;
    this.patch(id, { status: 'adding', failure: null });
    const option = kindOption(job.kind);
    const description = {
      title: job.title,
      documentType: option.documentType,
      purpose: option.purpose,
      ...(job.audience ? { audience: job.audience } : {}),
    };
    const outcome: BrandSourceAddOutcome = job.file
      ? await this.documents.upload(this.workspaceSlug(), job.file, description, job.key, job.abort.signal)
      : await this.documents.pasteText(this.workspaceSlug(), { ...description, text: job.text ?? '' }, job.key, job.abort.signal);

    // Removed while it was in flight: nothing more to show.
    if (!this.entries().some((e) => e.id === id)) return;

    if (outcome.status === 'added') {
      this.jobs.delete(id);
      this.patch(id, { status: 'added', documentId: outcome.document.id, name: outcome.document.title, failure: null });
      this.libraryDocs.update((docs) => (docs.some((d) => d.id === outcome.document.id) ? docs : [outcome.document, ...docs]));
      this.announce(`${outcome.document.title} added.`);
    } else if (outcome.reason === 'cancelled') {
      this.removeEntry(id);
    } else {
      this.patch(id, { status: 'failed', failure: outcome.reason });
      this.announce(`${this.entries().find((e) => e.id === id)?.name ?? 'That example'} was not added. ${FAILURE_COPY[outcome.reason]}`);
    }
  }

  protected retry(id: string): void {
    void this.run(id);
  }

  protected cancel(id: string): void {
    this.jobs.get(id)?.abort.abort();
  }

  /** Takes an example off this step. The document, if one was made, stays in the library. */
  protected remove(id: string): void {
    this.jobs.get(id)?.abort.abort();
    this.jobs.delete(id);
    const name = this.entries().find((e) => e.id === id)?.name;
    this.removeEntry(id);
    if (name) this.announce(`${name} removed from this step.`);
  }

  // ---- Picking from the library ----

  protected togglePicker(): void {
    this.pickerOpen.update((open) => !open);
    this.picked.set(new Set());
  }

  protected setPicked(id: string, on: boolean): void {
    this.picked.update((current) => {
      const next = new Set(current);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  protected usePicked(): void {
    const chosen = this.pickable().filter((d) => this.picked().has(d.id));
    if (chosen.length === 0) return;
    this.entries.update((all) => [...all, ...chosen.map((d) => this.entryForDocument(d, kindOfDocument(d)))]);
    this.announce(chosen.length === 1 ? `${chosen[0].title} added.` : `${chosen.length} examples added.`);
    this.picked.set(new Set());
    this.pickerOpen.set(false);
  }

  // ---- Presentation helpers ----

  protected kindLabel(key: ExampleKind): string {
    return kindOption(key).label;
  }

  protected failureText(entry: Entry): string {
    return entry.failure ? FAILURE_COPY[entry.failure] : '';
  }

  protected retryable(entry: Entry): boolean {
    return entry.status === 'failed' && canRetry(entry.failure) && this.jobs.has(entry.id);
  }

  protected kindFor(doc: BrandSourceDocumentSummary): string {
    return kindOption(kindOfDocument(doc)).label;
  }

  protected size(bytes: number): string {
    return formatSize(bytes);
  }

  protected setAudience(event: Event): void {
    this.audience.set((event.target as HTMLInputElement).value);
  }

  // ---- Internals ----

  private entryForSaved(saved: SavedExample): Entry {
    return { id: this.nextId(), documentId: saved.documentId, name: 'Saved example', detail: '', kind: saved.kind, status: 'added', failure: null };
  }

  private entryForDocument(doc: BrandSourceDocumentSummary, kind: ExampleKind): Entry {
    return { id: this.nextId(), documentId: doc.id, name: doc.title, detail: `${doc.fileName}, ${formatSize(doc.sizeBytes)}`, kind, status: 'added', failure: null };
  }

  private patch(id: string, change: Partial<Entry>): void {
    this.entries.update((all) => all.map((e) => (e.id === id ? { ...e, ...change } : e)));
  }

  private removeEntry(id: string): void {
    this.entries.update((all) => all.filter((e) => e.id !== id));
  }

  private nextId(): string {
    this.counter += 1;
    return `ex-${this.counter}`;
  }

  /** Clears first so the same sentence twice in a row is still announced. */
  private announce(message: string): void {
    this.announcement.set('');
    queueMicrotask(() => this.announcement.set(message));
  }
}
