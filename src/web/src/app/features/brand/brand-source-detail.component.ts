import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpTabDefinition,
  CpTabPanelComponent,
  CpTabsComponent,
} from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import {
  BrandSourceDocumentDetail,
  BrandSourceHold,
  BrandSourceUsage,
  BrandSourceVersionRow,
} from '../../models/brand-source-document.models';
import { BrandSourceExtractionState } from '../../models/brand-source-extraction.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { ConfirmService } from '../../core/confirm.service';
import { DOCUMENT_TYPE_LABELS, EXTRACTION_STATE_LABELS, PURPOSE_LABELS } from './brand-library-labels';
import { BrandSourceReplaceComponent } from './brand-source-replace.component';
import { BrandSourceTextComponent } from './brand-source-text.component';

type DetailState =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'error' }
  | { readonly status: 'ready'; readonly document: BrandSourceDocumentDetail };

/** A tone per extraction state. Exhaustive by type; `Unsupported` is a review state, not a fault. */
const EXTRACTION_TONE: Record<BrandSourceExtractionState, CpStatusPillTone> = {
  NotExtracted: 'neutral',
  Succeeded: 'success',
  Unsupported: 'warning',
  Failed: 'error',
};

const COULD_NOT_LOAD = 'This example couldn’t be loaded. Check your connection and try again.';

/**
 * One brand source document in full: what it is, every version it has had, the text read from the version
 * being looked at, and what still points at it.
 *
 * **A removed document is not found, and neither is another workspace's.** The API answers all three the
 * same way on purpose, so this screen says one thing for all of them rather than guessing which happened.
 *
 * **Archiving is a shelf, not a deletion**, and the screen says so: an archived document still reads, still
 * downloads and still shows its history; what it refuses is a new version or a change to its text. The
 * controls that cannot work are not rendered as controls that 409.
 *
 * **No storage address is ever constructed.** A version's bytes are reached only through the API's own
 * download route, which streams the file with its own `Content-Disposition` — so the download is a link the
 * browser follows, never a fetch into memory and never an object URL.
 */
@Component({
  selector: 'cp-brand-source-detail',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    BrandSourceReplaceComponent,
    BrandSourceTextComponent,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    CpTabPanelComponent,
    CpTabsComponent,
  ],
  templateUrl: './brand-source-detail.component.html',
  styleUrl: './brand-source-detail.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSourceDetailComponent {
  private readonly documents = inject(BrandSourceDocumentService);
  private readonly profiles = inject(BrandProfileService);
  private readonly confirmService = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);

  readonly workspaceSlug = this.resolveRouteParam('workspaceSlug');
  readonly documentId = this.resolveRouteParam('documentId');

  private readonly stateSignal = signal<DetailState>({ status: 'loading' });
  readonly state = this.stateSignal.asReadonly();

  readonly versions = signal<readonly BrandSourceVersionRow[]>([]);
  readonly versionsCursor = signal<string | null>(null);
  readonly versionsDegraded = signal(false);

  readonly usage = signal<BrandSourceUsage | null>(null);
  readonly usageDegraded = signal(false);

  readonly channels = signal<readonly ContentChannel[]>([]);

  readonly replaceOpen = signal(false);
  readonly busy = signal<'archive' | null>(null);

  /** What the whole screen says after an action, where it is not about one panel. */
  readonly notice = signal('');

  /**
   * Which version's text the Text tab is showing. Follows the document's current version unless a creator
   * asks to read an older one, which the API allows and this screen therefore offers.
   */
  private readonly chosenVersion = signal<number | null>(null);

  readonly selectedTabId = signal('details');

  readonly typeLabels = DOCUMENT_TYPE_LABELS;
  readonly purposeLabels = PURPOSE_LABELS;
  readonly extractionLabels = EXTRACTION_STATE_LABELS;

  constructor() {
    void this.load();
    void this.loadChannels();
  }

  // ---- Rendering ----

  readonly document = computed(() => {
    const state = this.stateSignal();
    return state.status === 'ready' ? state.document : null;
  });

  readonly isArchived = computed(() => this.document()?.status === 'Archived');

  readonly viewedVersion = computed(() => this.chosenVersion() ?? this.document()?.versionNumber ?? 1);

  readonly isViewingCurrent = computed(() => this.viewedVersion() === this.document()?.versionNumber);

  readonly tabs = computed<CpTabDefinition[]>(() => [
    { id: 'details', label: 'Details' },
    { id: 'text', label: 'Text' },
    { id: 'versions', label: 'Versions' },
    { id: 'usage', label: 'Where it’s used' },
  ]);

  extractionTone(state: BrandSourceExtractionState): CpStatusPillTone {
    return EXTRACTION_TONE[state];
  }

  errorMessage(): string {
    return COULD_NOT_LOAD;
  }

  /** The channel's own display name, falling back to the stored key for a channel since retired. */
  channelLabel(key: string): string {
    return this.channels().find((channel) => channel.key === key)?.displayName ?? key;
  }

  /** A size a creator can read. Decimal units, which is how an operating system reports a file. */
  fileSize(bytes: number): string {
    if (bytes < 1000) return `${bytes} B`;
    if (bytes < 1000 * 1000) return `${Math.round(bytes / 1000)} KB`;

    return `${(bytes / (1000 * 1000)).toFixed(1)} MB`;
  }

  /** The API's own download route for one version, or null while the gateway address is not known. */
  downloadUrl(versionNumber: number): string | null {
    return this.documents.versionDownloadUrl(this.workspaceSlug, this.documentId, versionNumber);
  }

  /** How a hold reads: which guide, at which of its versions, citing which of ours. */
  holdLabel(hold: BrandSourceHold): string {
    const name = hold.holderName ?? 'A style guide';
    return `${name} (v${hold.holderVersionNumber}) cites version ${hold.sourceVersionNumber}`;
  }

  /**
   * Whether a hold names a version this document has since replaced.
   *
   * Arithmetic on two facts the API published — the cited version number and the current one — not a claim
   * about the guide. **It deliberately does not say the guide needs review**: whether a citation is stale
   * enough to act on is the guide's own state and the server's to decide. This only tells a creator that the
   * guide was written from an earlier file, which is the thing they cannot otherwise see here.
   */
  citesSupersededVersion(hold: BrandSourceHold): boolean {
    const current = this.document()?.versionNumber;
    return current !== undefined && hold.sourceVersionNumber < current;
  }

  /** True when any hold was written from a file this document has since replaced. */
  readonly hasSupersededHolds = computed(() =>
    (this.usage()?.holds ?? []).some((hold) => this.citesSupersededVersion(hold)),
  );

  hasMoreVersions(): boolean {
    return this.versionsCursor() !== null;
  }

  // ---- Actions ----

  viewVersionText(versionNumber: number): void {
    this.chosenVersion.set(versionNumber);
    this.selectedTabId.set('text');
  }

  openReplace(): void {
    this.replaceOpen.set(true);
  }

  closeReplace(): void {
    this.replaceOpen.set(false);
  }

  /**
   * A new version changes the document's number, its token and what the history says, so everything is read
   * again rather than patched from the response — and the Text tab follows the new current version.
   */
  onReplaced(): void {
    this.chosenVersion.set(null);
    this.notice.set('That file is now the current version. The one it replaced is still here, under Versions.');
    void this.load();
    void this.loadVersions();
    void this.loadUsage();
  }

  /** The document moved under a replacement. Read it again so the next attempt quotes a token that exists. */
  onReplaceConflict(): void {
    void this.load();
    void this.loadVersions();
  }

  async toggleArchive(): Promise<void> {
    const document = this.document();
    if (!document || this.busy()) return;

    const archiving = document.status !== 'Archived';

    // A confirmation, not a form: archiving takes a document out of every collaborator's library. `neutral`
    // rather than `danger` because nothing is destroyed — it is reversible, and the copy says so.
    const agreed = await this.confirmService.confirm({
      title: archiving ? 'Put this example on the shelf?' : 'Bring this example back?',
      message: archiving
        ? 'It stays here and keeps every version, but it drops out of the library and stops being offered as grounding. You can bring it back at any time.'
        : 'It returns to the library and can be offered as grounding again.',
      confirmLabel: archiving ? 'Put on the shelf' : 'Bring it back',
      cancelLabel: 'Leave it',
      tone: 'neutral',
    });

    if (!agreed) return;

    this.busy.set('archive');
    this.notice.set('');

    const outcome = archiving
      ? await this.documents.archive(this.workspaceSlug, document.id, document.concurrencyToken)
      : await this.documents.unarchive(this.workspaceSlug, document.id, document.concurrencyToken);

    this.busy.set(null);

    switch (outcome.status) {
      case 'done':
        this.stateSignal.set({ status: 'ready', document: outcome.document });
        this.notice.set(archiving ? 'This example is on the shelf.' : 'This example is back in the library.');
        return;

      case 'conflict':
        // Someone else changed it, so the token quoted is stale and nothing happened. Reading again is the
        // remedy, and it is what lets the next attempt quote a token that exists.
        this.notice.set('Someone else changed this example while you were looking at it, so nothing was changed. Here it is as it stands now.');
        void this.load();
        return;

      case 'forbidden':
        this.notice.set('You need Editor access in this workspace to change this.');
        return;

      case 'not_found':
        this.stateSignal.set({ status: 'missing' });
        return;

      default:
        this.notice.set('That couldn’t be done just now, and nothing was changed. Try again.');
    }
  }

  /** The text panel changed something, so what the header says about the text is re-read. */
  onTextChanged(): void {
    void this.load();
    void this.loadVersions();
  }

  retry(): void {
    void this.load();
    void this.loadVersions();
    void this.loadUsage();
  }

  loadMoreVersions(): void {
    void this.loadVersions(this.versionsCursor());
  }

  // ---- Internals ----

  private async load(): Promise<void> {
    const current = this.stateSignal();
    if (current.status !== 'ready') this.stateSignal.set({ status: 'loading' });

    const outcome = await this.documents.get(this.workspaceSlug, this.documentId);

    if (outcome.status === 'ok') {
      this.stateSignal.set({ status: 'ready', document: outcome.document });

      // Only on the first read: the history and the usage are their own panels after that, and reloading
      // them here would make every archive toggle fetch three things.
      if (current.status !== 'ready') {
        void this.loadVersions();
        void this.loadUsage();
      }

      return;
    }

    // Unknown, another workspace's, and removed are one answer. The screen says one thing for all three.
    this.stateSignal.set(outcome.status === 'not_found' ? { status: 'missing' } : { status: 'error' });
  }

  private async loadVersions(cursor: string | null = null): Promise<void> {
    const outcome = await this.documents.listVersions(this.workspaceSlug, this.documentId, cursor);

    if (outcome.status !== 'ok') {
      // Degraded rather than fatal: the document itself read, and a missing history is one panel short of a
      // screen rather than a screen that cannot be shown.
      this.versionsDegraded.set(true);
      return;
    }

    this.versionsDegraded.set(false);
    this.versions.update((rows) => (cursor === null ? outcome.page.items : [...rows, ...outcome.page.items]));
    this.versionsCursor.set(outcome.page.nextCursor);
  }

  private async loadUsage(): Promise<void> {
    const outcome = await this.documents.usage(this.workspaceSlug, this.documentId);

    this.usageDegraded.set(outcome.status !== 'ok');
    this.usage.set(outcome.status === 'ok' ? outcome.usage : null);
  }

  /** The channel vocabulary, read once. A failure costs a display name, not the screen. */
  private async loadChannels(): Promise<void> {
    const outcome = await this.profiles.listContentChannels();
    if (outcome.status === 'found') this.channels.set(outcome.channels);
  }

  private resolveRouteParam(name: string): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const value = node.snapshot.paramMap.get(name);
      if (value) return value;
    }

    throw new Error(`BrandSourceDetailComponent route is missing a ${name} segment.`);
  }
}
