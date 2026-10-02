import { ChangeDetectionStrategy, Component, DestroyRef, InjectionToken, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { CpButtonComponent, CpFieldComponent, CpFormSectionComponent, CpNoticeComponent, CpStatusPillTone } from '@creator-pantry/ui';

import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSourceDocumentDetail } from '../../../models/brand-source-document.models';
import { BRAND_SOURCE_REASON_MAX, BrandSourceExtraction } from '../../../models/brand-source-extraction.models';
import { BrandSourceDocumentService, BrandSourceShelfOutcome } from '../../../services/brand-source-document.service';
import {
  BrandSourceExtractionFailure,
  BrandSourceExtractionService,
  BrandSourceExtractionWriteOutcome,
} from '../../../services/brand-source-extraction.service';
import { BrandSetupExampleComponent } from './brand-setup-example.component';
import { formatSize, kindOption } from './brand-setup-examples';
import {
  CardKind,
  ConfirmedText,
  FAILURE_COPY,
  NEEDS_DECISION,
  REASON_CORRECTED,
  REASON_TYPED,
  ReviewCard,
  SHELF_COPY,
  cardKindOf,
  correctionProblem,
  decodeConfirmed,
  previewOf,
  sourcesOf,
  visualCountOf,
} from './brand-setup-review-text';

/** How long to wait between checks while a read is still going. Overridable so tests need not wait. */
export const BRAND_SETUP_REVIEW_POLL_MS = new InjectionToken<number>('BRAND_SETUP_REVIEW_POLL_MS', {
  providedIn: 'root',
  factory: () => 4000,
});

/** How many checks to make on its own before it leaves "Check again" to the creator. */
export const BRAND_SETUP_REVIEW_MAX_POLLS = new InjectionToken<number>('BRAND_SETUP_REVIEW_MAX_POLLS', {
  providedIn: 'root',
  factory: () => 15,
});

interface Editor {
  readonly original: string;
  readonly text: string;
  readonly note: string;
  readonly saving: boolean;
  readonly problem: string;
  /** The text changed under the creator; their version is kept and they choose what happens next. */
  readonly conflict: boolean;
  /** Reused for a repeat of the same save, replaced as soon as the text or the note changes. */
  readonly key: string;
  readonly typed: boolean;
}

/**
 * Step 4 of "Create my voice": the creator checks the words we read from the text examples they added. Nothing is
 * used until they say it looks right — by confirming it, correcting it, or leaving that example out. A scan, a
 * failure or a partial read is never passed over quietly. The shell owns Continue and Skip; this step reports
 * whether anything still needs a decision, and the ids and labels to remember. Corrected text goes to the server
 * when it is saved, never into the draft.
 */
@Component({
  selector: 'cp-brand-setup-review-text-step',
  standalone: true,
  imports: [BrandSetupExampleComponent, CpButtonComponent, CpFieldComponent, CpFormSectionComponent, CpNoticeComponent],
  templateUrl: './brand-setup-review-text-step.component.html',
  styleUrl: './brand-setup-review-text-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupReviewTextStepComponent implements OnInit {
  readonly step = input.required<BrandSetupStepDefinition>();
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  /** The previous step's saved slice: which examples the creator added. */
  readonly sources = input<Readonly<Record<string, unknown>> | null>(null);
  readonly workspaceSlug = input.required<string>();
  readonly reported = output<BrandSetupStepReport>();

  private readonly documents = inject(BrandSourceDocumentService);
  private readonly extractions = inject(BrandSourceExtractionService);
  private readonly pollMs = inject(BRAND_SETUP_REVIEW_POLL_MS);
  private readonly maxPolls = inject(BRAND_SETUP_REVIEW_MAX_POLLS);

  protected readonly reasonMax = BRAND_SOURCE_REASON_MAX;
  protected readonly cards = signal<readonly ReviewCard[]>([]);
  protected readonly confirmed = signal<readonly ConfirmedText[]>([]);
  protected readonly editors = signal<Readonly<Record<string, Editor>>>({});
  protected readonly busy = signal<Readonly<Record<string, string>>>({});
  protected readonly problems = signal<Readonly<Record<string, string>>>({});
  protected readonly note = signal('');
  protected readonly announcement = signal('');
  protected readonly visualCount = signal(0);
  protected readonly loaded = signal(false);
  protected readonly gaveUpWaiting = signal(false);

  private baseline = '[]';
  private ready = false;
  private edited = false;
  private hadSavedDraft = false;
  private polls = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;
  private readonly retryKeys = new Map<string, { expected: string | null; key: string }>();

  protected readonly entries = computed(() =>
    this.cards().map((card) => ({ card, kind: cardKindOf(card, this.confirmed()) })),
  );
  protected readonly undecided = computed(() => this.entries().filter((e) => NEEDS_DECISION.has(e.kind)).length);
  protected readonly dirtyEditors = computed(() => Object.values(this.editors()).filter((e) => e.text !== e.original || e.saving).length);
  protected readonly anyBusy = computed(() => Object.keys(this.busy()).length > 0);

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      if (this.timer) clearTimeout(this.timer);
    });

    effect(() => {
      const confirmed = this.confirmed();
      const undecided = this.undecided();
      const dirtyEditors = this.dirtyEditors();
      const busy = this.anyBusy();
      const loaded = this.loaded();
      if (!this.ready) return;

      const serialised = JSON.stringify(confirmed);
      if (serialised !== this.baseline) this.edited = true;
      const draft = this.edited || this.hadSavedDraft ? ({ confirmed } as Record<string, unknown>) : null;
      this.reported.emit({
        // Not while something is still being decided, being saved, or typed and not yet saved.
        canContinue: loaded && undecided === 0 && dirtyEditors === 0 && !busy,
        isDirty: serialised !== this.baseline || dirtyEditors > 0,
        draft,
      });
    });
  }

  ngOnInit(): void {
    const saved = decodeConfirmed(this.draft());
    this.hadSavedDraft = this.draft() !== null && Object.keys(this.draft()!).length > 0;
    this.baseline = JSON.stringify(saved);
    this.confirmed.set(saved);
    this.visualCount.set(visualCountOf(this.sources()));
    this.ready = true;

    const sources = sourcesOf(this.sources());
    this.cards.set(sources.map((s) => ({ documentId: s.documentId, kind: s.kind, detail: null, extraction: null, load: 'loading' as const })));
    void this.loadAll();
  }

  // ---- Loading ----

  private async loadAll(): Promise<void> {
    const ids = this.cards().map((c) => c.documentId);
    await Promise.all(ids.map((id) => this.loadCard(id)));

    // An example removed elsewhere is not something to check, and not something to block on.
    const gone = this.cards().filter((c) => c.load === 'ready' && c.detail === null).length;
    if (gone > 0) {
      this.cards.update((all) => all.filter((c) => !(c.load === 'ready' && c.detail === null)));
      this.note.set(gone === 1 ? 'An example you added earlier is no longer available, so it was left off.' : 'Some examples you added earlier are no longer available, so they were left off.');
    }
    this.pruneConfirmed();
    this.loaded.set(true);
    this.schedulePoll();
  }

  /** (Re)reads one example's document and its text. */
  private async loadCard(documentId: string): Promise<void> {
    const slug = this.workspaceSlug();
    const detail = await this.documents.get(slug, documentId);
    if (detail.status === 'not_found') {
      this.patchCard(documentId, { load: 'ready', detail: null, extraction: null });
      return;
    }
    if (detail.status === 'unavailable') {
      this.patchCard(documentId, { load: 'unreachable' });
      return;
    }
    const extraction = await this.extractions.get(slug, documentId, detail.document.versionNumber);
    if (extraction.status === 'not_found') {
      this.patchCard(documentId, { load: 'ready', detail: null, extraction: null });
    } else if (extraction.status === 'unavailable') {
      this.patchCard(documentId, { load: 'unreachable', detail: detail.document });
    } else {
      this.patchCard(documentId, { load: 'ready', detail: detail.document, extraction: extraction.extraction });
    }
  }

  private schedulePoll(): void {
    if (this.destroyed || this.timer) return;
    const waiting = this.entries().filter((e) => e.kind === 'waiting');
    if (waiting.length === 0) {
      this.gaveUpWaiting.set(false);
      return;
    }
    if (this.polls >= this.maxPolls) {
      this.gaveUpWaiting.set(true);
      return;
    }
    this.timer = setTimeout(async () => {
      this.timer = null;
      this.polls += 1;
      await Promise.all(this.entries().filter((e) => e.kind === 'waiting').map((e) => this.loadCard(e.card.documentId)));
      this.pruneConfirmed();
      this.schedulePoll();
    }, this.pollMs);
  }

  // ---- Decisions ----

  protected confirm(card: ReviewCard): void {
    const id = card.extraction?.id;
    if (!id) return;
    this.confirmed.update((all) => [...all.filter((c) => c.documentId !== card.documentId), { documentId: card.documentId, extractionId: id }]);
    this.announce(`${this.titleOf(card)} marked as looking right.`);
  }

  protected async checkAgain(card: ReviewCard): Promise<void> {
    this.setBusy(card.documentId, 'check');
    this.polls = 0;
    this.gaveUpWaiting.set(false);
    await this.loadCard(card.documentId);
    this.pruneConfirmed();
    this.clearBusy(card.documentId);
    this.schedulePoll();
  }

  protected async retryRead(card: ReviewCard): Promise<void> {
    const extraction = card.extraction;
    if (!extraction) return;
    this.setProblem(card.documentId, '');
    this.setBusy(card.documentId, 'retry');

    // One key per attempt at the same artifact, so a repeat of the same click cannot queue a second read.
    const previous = this.retryKeys.get(card.documentId);
    const attempt = previous && previous.expected === extraction.id ? previous : { expected: extraction.id, key: crypto.randomUUID() };
    this.retryKeys.set(card.documentId, attempt);

    const outcome = await this.extractions.retry(this.workspaceSlug(), card.documentId, extraction.versionNumber, extraction.id, attempt.key);
    this.clearBusy(card.documentId);

    if (outcome.status === 'ok') {
      this.retryKeys.delete(card.documentId);
      this.patchCard(card.documentId, { extraction: outcome.extraction });
      this.polls = 0;
      this.gaveUpWaiting.set(false);
      this.announce(`Reading ${this.titleOf(card)} again.`);
      this.schedulePoll();
      return;
    }
    this.afterFailure(card, outcome.reason);
  }

  // ---- Editing ----

  protected startEdit(card: ReviewCard): void {
    const text = card.extraction?.text ?? '';
    this.setEditor(card.documentId, { original: text, text, note: '', saving: false, problem: '', conflict: false, key: crypto.randomUUID(), typed: text === '' });
  }

  protected cancelEdit(card: ReviewCard): void {
    this.editors.update((all) => {
      const next = { ...all };
      delete next[card.documentId];
      return next;
    });
  }

  protected setText(card: ReviewCard, event: Event): void {
    const text = (event.target as HTMLTextAreaElement).value;
    this.updateEditor(card.documentId, { text, problem: '', key: crypto.randomUUID() });
  }

  protected setNote(card: ReviewCard, event: Event): void {
    const note = (event.target as HTMLInputElement).value;
    this.updateEditor(card.documentId, { note, key: crypto.randomUUID() });
  }

  protected async saveEdit(card: ReviewCard, expectedOverride?: string): Promise<void> {
    const editor = this.editors()[card.documentId];
    const extraction = card.extraction;
    if (!editor || !extraction || editor.saving) return;

    const problem = correctionProblem(editor.text);
    if (problem) {
      this.updateEditor(card.documentId, { problem });
      return;
    }
    const expected = expectedOverride ?? extraction.id;
    if (!expected) {
      this.updateEditor(card.documentId, { problem: FAILURE_COPY.pending });
      return;
    }

    this.updateEditor(card.documentId, { saving: true, problem: '', conflict: false });
    const outcome = await this.extractions.correct(
      this.workspaceSlug(),
      card.documentId,
      extraction.versionNumber,
      { expectedExtractionId: expected, text: editor.text, reason: editor.note.trim() || (editor.typed ? REASON_TYPED : REASON_CORRECTED) },
      editor.key,
    );

    if (outcome.status === 'ok') {
      this.cancelEdit(card);
      this.patchCard(card.documentId, { extraction: outcome.extraction });
      // A new artifact: anything said about the old one no longer applies.
      this.pruneConfirmed();
      this.announce(`${this.titleOf(card)} text saved.`);
      return;
    }
    this.updateEditor(card.documentId, {
      saving: false,
      conflict: outcome.reason === 'conflict',
      problem: FAILURE_COPY[outcome.reason],
    });
  }

  /** The text changed under the creator, and they choose to keep theirs: read the latest, then save theirs over it. */
  protected async keepMine(card: ReviewCard): Promise<void> {
    const editor = this.editors()[card.documentId];
    if (!editor) return;
    this.updateEditor(card.documentId, { saving: true, conflict: false, problem: '' });
    const latest = await this.extractions.get(this.workspaceSlug(), card.documentId, card.extraction?.versionNumber ?? 1);
    if (latest.status !== 'ok' || !latest.extraction.id) {
      this.updateEditor(card.documentId, { saving: false, problem: FAILURE_COPY.unavailable });
      return;
    }
    this.patchCard(card.documentId, { extraction: latest.extraction });
    this.updateEditor(card.documentId, { saving: false, key: crypto.randomUUID() });
    await this.saveEdit({ ...card, extraction: latest.extraction }, latest.extraction.id);
  }

  /** The text changed under the creator, and they choose the newest: replace their version with it. */
  protected async startFromNewest(card: ReviewCard): Promise<void> {
    this.updateEditor(card.documentId, { saving: true, conflict: false, problem: '' });
    const latest = await this.extractions.get(this.workspaceSlug(), card.documentId, card.extraction?.versionNumber ?? 1);
    if (latest.status !== 'ok') {
      this.updateEditor(card.documentId, { saving: false, problem: FAILURE_COPY.unavailable });
      return;
    }
    this.patchCard(card.documentId, { extraction: latest.extraction });
    const text = latest.extraction.text ?? '';
    this.setEditor(card.documentId, { original: text, text, note: '', saving: false, problem: '', conflict: false, key: crypto.randomUUID(), typed: text === '' });
  }

  // ---- Leaving out and bringing back ----

  protected async leaveOut(card: ReviewCard): Promise<void> {
    await this.shelve(card, 'archive');
  }

  protected async bringBack(card: ReviewCard): Promise<void> {
    await this.shelve(card, 'unarchive');
    // Back on the list it needs a decision again, so read it fresh.
    await this.loadCard(card.documentId);
    this.schedulePoll();
  }

  private async shelve(card: ReviewCard, command: 'archive' | 'unarchive'): Promise<void> {
    const detail = card.detail;
    if (!detail) return;
    this.setProblem(card.documentId, '');
    this.setBusy(card.documentId, command);
    const slug = this.workspaceSlug();
    const run = (token: string): Promise<BrandSourceShelfOutcome> =>
      command === 'archive' ? this.documents.archive(slug, card.documentId, token) : this.documents.unarchive(slug, card.documentId, token);

    let outcome = await run(detail.concurrencyToken);
    if (outcome.status === 'conflict') {
      // The document changed since it was read. Take its new token once and try again.
      const fresh = await this.documents.get(slug, card.documentId);
      if (fresh.status === 'ok') outcome = await run(fresh.document.concurrencyToken);
    }
    this.clearBusy(card.documentId);

    if (outcome.status === 'done') {
      this.patchCard(card.documentId, { detail: outcome.document });
      this.cancelEdit(card);
      this.announce(command === 'archive' ? `${this.titleOf(card)} left out.` : `${this.titleOf(card)} brought back.`);
      return;
    }
    this.setProblem(
      card.documentId,
      outcome.status === 'forbidden' ? SHELF_COPY.forbidden : command === 'archive' ? SHELF_COPY.leaveOut : SHELF_COPY.bringBack,
    );
  }

  // ---- Presentation helpers ----

  protected titleOf(card: ReviewCard): string {
    return card.detail?.title ?? 'This example';
  }

  protected subtitleOf(card: ReviewCard): string {
    const kind = kindOption(card.kind).label;
    return card.detail ? `${kind}, ${card.detail.fileName}, ${formatSize(card.detail.sizeBytes)}` : kind;
  }

  protected preview(extraction: BrandSourceExtraction | null): string {
    return extraction?.text ? previewOf(extraction.text) : '';
  }

  protected isBusy(documentId: string, what?: string): boolean {
    const current = this.busy()[documentId];
    return what ? current === what : current !== undefined;
  }

  /**
   * What each state is called and what it means, in one place. Previously a `@switch` of nine markup blocks,
   * which is how the pill tone and the sentence got to disagree about a stopped read.
   */
  private static readonly STATES: Readonly<Record<CardKind, { tone: CpStatusPillTone; status: string; note: string }>> = {
    checking: { tone: 'progress', status: 'Checking…', note: '' },
    waiting: { tone: 'progress', status: 'Reading…', note: "We're still reading this one." },
    stopped: { tone: 'error', status: "Couldn't read", note: "We couldn't finish reading this one." },
    failed: { tone: 'error', status: "Couldn't read", note: "We couldn't read this one." },
    scan: {
      tone: 'warning',
      status: 'Needs your help',
      note: "This looks like a scan or a picture, so there's no text to read. We can't read pictures yet.",
    },
    read: { tone: 'warning', status: 'Needs your OK', note: 'We read this from your file. Check that it looks right.' },
    partial: {
      tone: 'warning',
      status: 'Only part of it',
      note: 'We only kept the beginning of this one. It had more text than we can hold.',
    },
    confirmed: { tone: 'success', status: 'Checked', note: '' },
    'left-out': { tone: 'neutral', status: 'Left out', note: "Left out. It stays in your library, but we won't use it here." },
    unreachable: { tone: 'error', status: "Couldn't check", note: "We couldn't reach this one just now." },
  };

  protected toneOf(kind: CardKind): CpStatusPillTone {
    return BrandSetupReviewTextStepComponent.STATES[kind].tone;
  }

  protected statusOf(kind: CardKind): string {
    return BrandSetupReviewTextStepComponent.STATES[kind].status;
  }

  protected noteOf(kind: CardKind, card: ReviewCard): string {
    if (kind === 'confirmed') {
      return this.corrected(card) ? 'You corrected this text.' : 'You said this looks right.';
    }

    return BrandSetupReviewTextStepComponent.STATES[kind].note;
  }

  /** The three states that have text worth showing: read, partly read, and settled. */
  protected showsText(kind: CardKind): boolean {
    return kind === 'read' || kind === 'partial' || kind === 'confirmed';
  }

  /** Every state except one already set aside, and one still being looked up. */
  protected canLeaveOut(kind: CardKind): boolean {
    return kind !== 'left-out' && kind !== 'checking';
  }

  protected corrected(card: ReviewCard): boolean {
    return card.extraction?.origin === 'Corrected';
  }


  // ---- Internals ----

  private afterFailure(card: ReviewCard, reason: BrandSourceExtractionFailure): void {
    this.setProblem(card.documentId, FAILURE_COPY[reason]);
    // The page the creator is looking at is out of date for these; show what is true now.
    if (reason === 'conflict' || reason === 'not_retryable' || reason === 'pending') void this.loadCard(card.documentId);
  }

  private pruneConfirmed(): void {
    // Keep only a confirmation that still names the artifact now current for that example.
    const current = new Map(this.cards().map((c) => [c.documentId, c.extraction?.id ?? null]));
    this.confirmed.update((all) => {
      const kept = all.filter((c) => current.get(c.documentId) === c.extractionId || !current.has(c.documentId) || current.get(c.documentId) === null);
      return kept.length === all.length ? all : kept;
    });
  }

  private patchCard(documentId: string, change: Partial<ReviewCard>): void {
    this.cards.update((all) => all.map((c) => (c.documentId === documentId ? { ...c, ...change } : c)));
  }

  private setBusy(documentId: string, what: string): void {
    this.busy.update((all) => ({ ...all, [documentId]: what }));
  }

  private clearBusy(documentId: string): void {
    this.busy.update((all) => {
      const next = { ...all };
      delete next[documentId];
      return next;
    });
  }

  private setProblem(documentId: string, message: string): void {
    this.problems.update((all) => ({ ...all, [documentId]: message }));
  }

  private setEditor(documentId: string, editor: Editor): void {
    this.editors.update((all) => ({ ...all, [documentId]: editor }));
  }

  private updateEditor(documentId: string, change: Partial<Editor>): void {
    this.editors.update((all) => (all[documentId] ? { ...all, [documentId]: { ...all[documentId], ...change } } : all));
  }

  /** Clears first so the same sentence twice in a row is still announced. */
  private announce(message: string): void {
    this.announcement.set('');
    queueMicrotask(() => this.announcement.set(message));
  }
}
