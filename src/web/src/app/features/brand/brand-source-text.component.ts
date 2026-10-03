import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import {
  CpButtonComponent,
  CpDialogComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import {
  BRAND_SOURCE_CORRECTION_MAX_BYTES,
  BRAND_SOURCE_REASON_MAX,
  BrandSourceExtraction,
  BrandSourceExtractionState,
} from '../../models/brand-source-extraction.models';
import { BrandSourceExtractionFailure, BrandSourceExtractionService } from '../../services/brand-source-extraction.service';
import { EXTRACTION_STATE_LABELS } from './brand-library-labels';

type ReadState =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'error' }
  | { readonly status: 'ready'; readonly extraction: BrandSourceExtraction };

/** A tone per state, exhaustive by type. `Unsupported` is a review state, not a fault. */
const STATE_TONE: Record<BrandSourceExtractionState, CpStatusPillTone> = {
  NotExtracted: 'neutral',
  Succeeded: 'success',
  Unsupported: 'warning',
  Failed: 'error',
};

/**
 * Why a retry or a correction did not happen, in the creator's terms. Exhaustive by type, so a failure the
 * service learns to report has to be given words rather than reaching a creator as a blank.
 */
const FAILURE_MESSAGE: Record<BrandSourceExtractionFailure, string> = {
  conflict: 'The text changed while you were working on it, so nothing was saved. Your words are still here — reread the latest, then save again.',
  not_retryable: 'Reading this again could not change the answer.',
  archived: 'This example is on the shelf. Bring it back before changing its text.',
  superseded: 'A newer file has replaced this one, so only the current version’s text can be changed.',
  pending: 'The first read has not finished yet. Wait for it, then correct the text.',
  invalid: 'That text could not be saved. It may be too long, or carry characters that cannot be kept.',
  forbidden: 'You need Editor access in this workspace to change the text.',
  not_found: 'This example could not be found. It may have been removed.',
  key_reused: 'That save was started with different text. Try saving again.',
  unavailable: 'The text could not be saved just now, and nothing was stored. Your words are still here, so try again.',
};

/**
 * One version's extracted text: where the read stands, the text itself, and the two ways a creator changes
 * it — ask for another read, or type the text in themselves.
 *
 * **Correcting an unreadable file is the point, not an edge case.** A scanned PDF or an image has no text to
 * extract, so typing it in is how that document becomes usable as grounding at all.
 *
 * **Only the current version's text can be changed.** An older version's text stays readable — it is what was
 * read from the file that was there — but the API refuses a retry or a correction against it, because only the
 * current version's text has consumers. This component states that rather than offering a control that 409s.
 *
 * **A creator's typed text is never merged into something they have not seen.** A correction names the
 * artifact it was composed against; if that has moved, the save is refused and the words stay in this form.
 */
@Component({
  selector: 'cp-brand-source-text',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './brand-source-text.component.html',
  styleUrl: './brand-source-text.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSourceTextComponent {
  private readonly extractions = inject(BrandSourceExtractionService);

  readonly workspaceSlug = input.required<string>();
  readonly documentId = input.required<string>();
  readonly versionNumber = input.required<number>();

  /** Whether this is the document's current version. Only the current one accepts a retry or a correction. */
  readonly isCurrentVersion = input(true);

  /** Whether the document is on the shelf. Archived documents read, but take no change. */
  readonly isArchived = input(false);

  /** Raised after a change lands, so the surface around this one can re-read what it says about the text. */
  readonly changed = output<void>();

  protected readonly reasonMax = BRAND_SOURCE_REASON_MAX;
  protected readonly stateLabels = EXTRACTION_STATE_LABELS;

  private readonly readSignal = signal<ReadState>({ status: 'loading' });
  protected readonly read = this.readSignal.asReadonly();

  protected readonly busy = signal<'retry' | 'correct' | null>(null);
  protected readonly failure = signal('');

  // ---- The correction form ----

  protected readonly correctOpen = signal(false);
  protected readonly draft = signal('');
  protected readonly reason = signal('');
  private readonly submitted = signal(false);

  /** Held across retries of one logical save, so a dropped response cannot write a second artifact. */
  private readonly correctionKey = signal<string | null>(null);

  constructor() {
    // Re-reads whenever the version being looked at changes, which is how the history tab's selection moves
    // this panel. `untracked` around the write so the read is not itself a dependency.
    effect(() => {
      const slug = this.workspaceSlug();
      const documentId = this.documentId();
      const versionNumber = this.versionNumber();

      untracked(() => void this.load(slug, documentId, versionNumber));
    });
  }

  // ---- Rendering ----

  protected readonly extraction = computed(() => {
    const state = this.read();
    return state.status === 'ready' ? state.extraction : null;
  });

  protected readonly tone = computed<CpStatusPillTone>(() => {
    const extraction = this.extraction();
    return extraction ? STATE_TONE[extraction.state] : 'neutral';
  });

  /**
   * Whether asking for another read could change anything. The server decides too, and refuses what it
   * cannot do; this only keeps a control off the screen when it certainly cannot work.
   */
  protected readonly canRetry = computed(() => {
    const extraction = this.extraction();
    if (!extraction || !this.isCurrentVersion() || this.isArchived()) return false;

    // A read that failed, or a version nothing has read yet. A success, an unreadable format and a creator's
    // own correction are all answers a reread cannot improve on.
    return extraction.state === 'Failed' || extraction.state === 'NotExtracted';
  });

  /** A correction needs an artifact to name, so it waits for the first read to settle. */
  protected readonly canCorrect = computed(() => {
    const extraction = this.extraction();
    if (!extraction || !this.isCurrentVersion() || this.isArchived()) return false;

    return extraction.id !== null;
  });

  /** Said once, so a creator is not left wondering why there are no controls. */
  protected readonly whyNoActions = computed(() => {
    if (this.read().status !== 'ready') return '';
    if (this.isArchived()) return 'This example is on the shelf, so its text cannot be changed. Bring it back first.';
    if (!this.isCurrentVersion()) {
      return 'This is an earlier version. Its text is what was read from the file that was there, and only the current version’s text can be changed.';
    }
    if (this.extraction()?.isReading === true) return 'A read is running. The text will appear here once it settles.';

    // A correction names the artifact it was composed against, so there has to be one. Said even when a
    // reread is on offer, because otherwise the absent control is unexplained.
    if (!this.canCorrect()) return 'Typing the text in yourself becomes possible once a first read has finished.';

    return '';
  });

  protected readonly draftBytes = computed(() => new TextEncoder().encode(this.draft()).length);

  protected readonly draftError = computed(() => {
    if (!this.submitted()) return '';
    if (this.draft().trim().length === 0) return 'Type the text this file should have.';

    // Bytes of UTF-8, which is how the server counts it: a 4 MB limit in characters would accept text the
    // server refuses the moment it carries anything outside ASCII.
    return this.draftBytes() > BRAND_SOURCE_CORRECTION_MAX_BYTES ? 'That text is longer than the four megabytes the library keeps.' : '';
  });

  protected readonly reasonError = computed(() => {
    if (!this.submitted()) return '';
    const text = this.reason().trim();
    if (text.length === 0) return 'Say briefly why you are changing this, so a later reader knows.';

    return text.length > this.reasonMax ? `Keep this to ${this.reasonMax} characters or fewer.` : '';
  });

  // ---- Actions ----

  protected retry(): void {
    void this.runRetry();
  }

  protected openCorrect(): void {
    const extraction = this.extraction();

    // Seeded with whatever is on record, so a correction starts from the read rather than from nothing.
    this.draft.set(extraction?.text ?? '');
    this.reason.set('');
    this.submitted.set(false);
    this.correctionKey.set(null);
    this.failure.set('');
    this.correctOpen.set(true);
  }

  protected closeCorrect(): void {
    if (this.busy() === 'correct') return;

    this.correctOpen.set(false);
  }

  protected onDraft(event: Event): void {
    this.draft.set(event.target instanceof HTMLTextAreaElement ? event.target.value : '');
    this.correctionKey.set(null);
  }

  protected onReason(event: Event): void {
    this.reason.set(event.target instanceof HTMLInputElement ? event.target.value : '');
    this.correctionKey.set(null);
  }

  protected save(): void {
    void this.runSave();
  }

  protected reload(): void {
    void this.load(this.workspaceSlug(), this.documentId(), this.versionNumber());
  }

  // ---- Internals ----

  /**
   * Deliberately leaves `failure` alone. A conflict reads again straight away so the panel is not showing
   * something stale, and clearing the message here would take away the one sentence explaining why the save
   * did not happen. Each action clears it when it starts instead.
   */
  private async load(workspaceSlug: string, documentId: string, versionNumber: number): Promise<void> {
    this.readSignal.set({ status: 'loading' });

    const outcome = await this.extractions.get(workspaceSlug, documentId, versionNumber);

    this.readSignal.set(
      outcome.status === 'ok'
        ? { status: 'ready', extraction: outcome.extraction }
        : outcome.status === 'not_found'
          ? { status: 'missing' }
          : { status: 'error' },
    );
  }

  private async runRetry(): Promise<void> {
    const extraction = this.extraction();
    if (!extraction || this.busy()) return;

    this.busy.set('retry');
    this.failure.set('');

    // A fresh key per press: a retry that failed is a new request, not a replay of the queued one.
    const outcome = await this.extractions.retry(
      this.workspaceSlug(),
      this.documentId(),
      this.versionNumber(),
      extraction.id,
      crypto.randomUUID(),
    );

    this.busy.set(null);

    if (outcome.status === 'ok') {
      this.readSignal.set({ status: 'ready', extraction: outcome.extraction });
      this.changed.emit();
      return;
    }

    this.failure.set(FAILURE_MESSAGE[outcome.reason]);

    // A conflict or a supersession means this panel is looking at something stale. Reading again is the
    // remedy, and it costs one request rather than leaving a wrong screen on display.
    if (outcome.reason === 'conflict' || outcome.reason === 'superseded') this.reload();
  }

  private async runSave(): Promise<void> {
    this.submitted.set(true);

    const extraction = this.extraction();
    if (!extraction?.id || this.busy() || this.draftError() || this.reasonError()) return;

    this.busy.set('correct');
    this.failure.set('');

    const key = this.correctionKey() ?? crypto.randomUUID();
    this.correctionKey.set(key);

    const outcome = await this.extractions.correct(
      this.workspaceSlug(),
      this.documentId(),
      this.versionNumber(),
      { expectedExtractionId: extraction.id, text: this.draft(), reason: this.reason().trim() },
      key,
    );

    this.busy.set(null);

    if (outcome.status === 'ok') {
      this.readSignal.set({ status: 'ready', extraction: outcome.extraction });
      this.correctOpen.set(false);
      this.changed.emit();
      return;
    }

    this.failure.set(FAILURE_MESSAGE[outcome.reason]);

    // The dialog stays open on every failure, because the creator's typed text lives in it. A refused save
    // is the one case where closing would lose work the server never took.
    if (outcome.reason === 'conflict') {
      // The artifact this was composed against has moved, so the key names a save that can no longer happen.
      this.correctionKey.set(null);
      this.reload();
    }
  }
}
