import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  WritableSignal,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpBadgeComponent,
  CpButtonComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpGeneratedLabelComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import {
  LONG_FORM_INSTRUCTION_MAX_LENGTH,
  LongFormAcceptanceIssueDetail,
  LongFormAssembledDocument,
  LongFormDocumentReview,
  LongFormDocumentSection,
  LongFormSectionRequestSummary,
  LongFormSectionWarning,
  longFormDocumentIsOpen,
  longFormEditorialPartLabel,
} from '../../models/long-form-document.models';
import {
  LONG_FORM_NOTE_MAX_CHARACTERS,
  LONG_FORM_ROLE_LABELS,
  LONG_FORM_SUBJECT_MAX_CHARACTERS,
  LongFormAcceptanceIssue,
} from '../../models/long-form-draft.models';
import { AiAllowanceState } from '../../services/ai-usage.service';
import {
  LongFormDocumentService,
  LongFormExportFormat,
  LongFormSectionWriteOutcome,
  LongFormWritingOutcome,
} from '../../services/long-form-document.service';
import { LongFormDraftService, LongFormPlanWriteOutcome } from '../../services/long-form-draft.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../shared/allowance-gate';
import { LongFormSeoSuggestionsComponent } from './long-form-seo-suggestions.component';

type LoadState = 'loading' | 'ready' | 'not_found' | 'forbidden' | 'unavailable';

/** What one section is doing right now on this screen. One thing at a time for each section. */
type SectionBusy = 'saving' | 'asking' | 'settling';

/** How often the screen asks again while a section is being written. */
export const LONG_FORM_REVIEW_POLL_MS = 2500;

/** One section as the template draws it: the server's section, and what this screen holds about it. */
interface SectionCard {
  readonly section: LongFormDocumentSection;
  readonly position: number;
  readonly roleLabel: string;
  readonly anchorId: string;
  readonly state: { readonly tone: CpStatusPillTone; readonly word: string };
  readonly isGenerated: boolean;
  readonly waiting: boolean;
  /** The creator's unsaved text for this section, or null when they are not editing it. */
  readonly draft: string | null;
  readonly dirty: boolean;
  readonly draftCharacters: number;
  readonly draftOverCeiling: boolean;
  readonly asking: boolean;
  readonly instruction: string;
  readonly busy: SectionBusy | null;
  readonly problem: string;
  readonly notice: string;
  readonly warnings: readonly LongFormSectionWarning[];
  readonly attribution: string;
  /**
   * For a newsletter item: where it stands among the issue's items, and whether the piece of work still names
   * the recipe it covers. Null for every other section.
   */
  readonly item: { readonly position: number; readonly of: number; readonly unresolved: boolean } | null;
}

/** The subject line and the note as the creator is typing them. */
interface FramingDraft {
  readonly subject: string;
  readonly note: string;
}

/**
 * The whole-document review surface for one long-form draft (LF.3.4, baseline B-33): the creator reads the
 * piece in order, jumps to a section, edits it, has it written again, marks it settled, and accepts the
 * document once.
 *
 * **Where it sits.** Not a page. It belongs in a flow page's step card — the long-form pipeline's last step
 * (LF.6.2) — and renders that card's sections and its one closing action.
 *
 * **One read, shown as it is.** The document, what was found about each generated section and where each
 * writing request stands all come from one server answer. This screen computes none of it: not the word
 * count, not what is wrong, not what a section's text is.
 *
 * **The creator's unsaved text is theirs until they say otherwise.** It is held here per section, outside
 * the server's answer, so a failed save, another section being written, a refresh of the document and a
 * refused request all leave it exactly as typed. Only saving it, or confirming a discard, lets it go.
 *
 * **Text is shown as written.** A section's Markdown is displayed as the text it is, line breaks kept, and
 * edited in a plain text box (B-37). There is no formatted preview here.
 *
 * **Accepting is one explicit act.** When the server says something is wrong with the document, the creator
 * is told what, by name, and must say yes to it before the acceptance is sent.
 *
 * **A newsletter issue is its subject, its note and its items in order (LF.4.2).** The subject and the note
 * are the creator's own words in two plain fields with their own Save; nothing here writes either. Items
 * change places among themselves by the recipe each covers, and an item whose recipe is no longer part of the
 * piece says so and is not offered to the writer. Nothing on this screen sends an issue anywhere.
 */
@Component({
  selector: 'cp-long-form-document-review',
  standalone: true,
  imports: [
    AiAllowanceNoticeComponent,
    CpAnchorNavComponent,
    CpBadgeComponent,
    CpButtonComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpGeneratedLabelComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    LongFormSeoSuggestionsComponent,
  ],
  templateUrl: './long-form-document-review.component.html',
  styleUrl: './long-form-document-review.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LongFormDocumentReviewComponent {
  private readonly service = inject(LongFormDocumentService);
  private readonly issue = inject(LongFormDraftService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();
  readonly draftId = input.required<string>();
  readonly allowance = input.required<AiAllowanceState>();
  readonly idPrefix = input('cp-long-form-document');

  /**
   * Which half of the screen to show (LF.6.2). A flow gives writing and acceptance a step each; on its own
   * the screen shows both, which is `all`.
   *
   * - `write`: the sections, each with its controls, and "write the rest". Nothing about accepting.
   * - `accept`: the same sections to read, with no control that changes one, then acceptance and what an
   *   accepted piece offers — its downloads and its SEO suggestions.
   *
   * One screen either way: the same read, the same poll, the same unsaved-edit guard.
   */
  readonly show = input<'all' | 'write' | 'accept'>('all');

  /** The document, once it has been accepted. Emitted on the acceptance and not before. */
  readonly accepted = output<LongFormAssembledDocument>();

  /**
   * The piece as it was last read, each time that changes (LF.6.2). For a flow that has to know where the
   * piece stands — writing begun, accepted — without reading it a second time itself.
   */
  readonly documentRead = output<LongFormAssembledDocument>();

  protected readonly load = signal<LoadState>('loading');
  protected readonly review = signal<LongFormDocumentReview | null>(null);

  /** The creator's unsaved text, by section. A section is being edited exactly when it has an entry here. */
  private readonly drafts = signal<Readonly<Record<string, string>>>({});

  /** What the creator has typed about what to change, by section, while "write it again" is open. */
  private readonly instructions = signal<Readonly<Record<string, string>>>({});

  private readonly busy = signal<Readonly<Record<string, SectionBusy>>>({});
  private readonly problems = signal<Readonly<Record<string, string>>>({});

  /** The key of a request that may or may not have reached the server, by section, so a retry is a retry. */
  private readonly pendingKeys = new Map<string, string>();

  /** The creator's unsaved subject and note. Null until they type in either, and again once it is saved. */
  private readonly framingDraft = signal<FramingDraft | null>(null);

  protected readonly framingBusy = signal(false);
  protected readonly framingProblem = signal('');
  protected readonly framingErrors = signal<Readonly<Record<string, readonly string[]>>>({});

  /** True while an order of items is being saved, so two moves cannot be sent against one reading. */
  protected readonly moving = signal(false);

  protected readonly subjectMax = LONG_FORM_SUBJECT_MAX_CHARACTERS;
  protected readonly noteMax = LONG_FORM_NOTE_MAX_CHARACTERS;

  protected readonly accepting = signal(false);
  protected readonly writingRest = signal(false);

  /** What went wrong with something about the whole document: the rest, an acceptance, a refresh. */
  protected readonly problem = signal('');

  /** An allowance refusal the server has returned, shown through the shared allowance notice. */
  protected readonly refusal = signal<AiQuotaRefusal | null>(null);

  /** Said to a screen reader through one polite live region: a save, a settle, an acceptance. */
  protected readonly announcement = signal('');

  /** True when a refresh of a document already on screen failed, so what is shown may be behind. */
  protected readonly behind = signal(false);

  protected readonly instructionMax = LONG_FORM_INSTRUCTION_MAX_LENGTH;

  private followedKey: string | null = null;
  private gone = false;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly document = computed(() => this.review()?.document ?? null);

  protected readonly open = computed(() => {
    const document = this.document();
    return document !== null && longFormDocumentIsOpen(document);
  });

  protected readonly isAccepted = computed(() => this.document()?.status === 'Accepted');

  /** Whether the controls that write or change a section are offered. */
  protected readonly showsWriting = computed(() => this.show() !== 'accept');

  /** Whether acceptance, and what follows it, is offered. */
  protected readonly showsAcceptance = computed(() => this.show() !== 'write');

  /**
   * The piece was accepted and something it was written from has since changed. It is still the creator's
   * accepted article, and still theirs to download as they accepted it.
   */
  protected readonly needsReview = computed(() => this.document()?.status === 'NeedsReview');

  /**
   * Which of the sources the accepted piece was written from have changed, in the creator's terms (LF.6.1).
   * One sentence to a source; a reason this page does not know still says that something changed.
   */
  protected readonly staleSources = computed<readonly string[]>(() => {
    if (!this.needsReview()) return [];

    const reasons = this.document()?.staleReasons ?? [];
    const sentences = reasons.map((reason) => STALE_SOURCES[reason] ?? 'Something this piece was written from has changed.');

    return sentences.length > 0 ? [...new Set(sentences)] : ['Something this piece was written from has changed.'];
  });

  /**
   * The accepted revision an SEO package may be written about (LF.5.3), or null when there is none: the piece
   * is not accepted, or was and its sources have since moved.
   */
  protected readonly seoRevision = computed(() => (this.isAccepted() ? (this.document()?.acceptedRevisionNumber ?? null) : null));

  /**
   * The downloads of the accepted piece (LF.5.2): plain links the browser follows, offered only once there is
   * an accepted article to download. Empty while the gateway's address is not known.
   */
  protected readonly downloads = computed<readonly { readonly label: string; readonly href: string }[]>(() => {
    if (!this.isAccepted() && !this.needsReview()) return [];

    return EXPORTS.flatMap(({ format, label }) => {
      const href = this.service.exportUrl(this.workspaceSlug(), this.draftId(), format);

      return href ? [{ label, href }] : [];
    });
  });

  protected readonly isIssue = computed(() => this.document()?.kind === 'NewsletterIssue');

  /** The subject and note as saved, with an empty string for one the issue does not have. */
  private readonly savedFraming = computed<FramingDraft>(() => {
    const framing = this.document()?.framing ?? null;
    return { subject: framing?.subject ?? '', note: framing?.note ?? '' };
  });

  /** What the two fields show: what the creator is typing, or what is saved. */
  protected readonly framing = computed<FramingDraft>(() => this.framingDraft() ?? this.savedFraming());

  protected readonly framingDirty = computed(() => {
    const draft = this.framingDraft();
    const saved = this.savedFraming();

    return draft !== null && (draft.subject !== saved.subject || draft.note !== saved.note);
  });

  protected readonly blocked = computed(() => allowanceBlocksNewRequests(this.allowance()));

  /** True while any section's writing request is still queued or running. */
  protected readonly anyWaiting = computed(
    () => this.review()?.requests.some((request) => request.outcome === 'Waiting') ?? false,
  );

  readonly hasUnsavedWork = computed(() => {
    const sections = this.document()?.sections ?? [];
    const drafts = this.drafts();

    return (
      this.framingDirty() ||
      sections.some((section) => drafts[section.id] !== undefined && drafts[section.id] !== (section.body ?? ''))
    );
  });

  protected readonly cards = computed<readonly SectionCard[]>(() => {
    const review = this.review();
    if (review === null) return [];

    const drafts = this.drafts();
    const instructions = this.instructions();
    const busy = this.busy();
    const problems = this.problems();
    const requests = new Map(review.requests.map((request) => [request.sectionId, request]));
    const ceiling = review.document.sectionMaxCharacters;
    const items = review.document.sections.filter((section) => section.role === 'Item');

    return review.document.sections.map((section, index): SectionCard => {
      const request = requests.get(section.id) ?? null;
      const draft = drafts[section.id] ?? null;
      const draftCharacters = draft === null ? 0 : [...draft].length;

      return {
        section,
        position: index + 1,
        roleLabel: LONG_FORM_ROLE_LABELS[section.role],
        anchorId: `${this.idPrefix()}-section-${section.id}`,
        state: stateOf(section, request),
        isGenerated: section.isWritten && section.writtenBy === 'AiGenerated',
        waiting: request?.outcome === 'Waiting',
        draft,
        dirty: draft !== null && draft !== (section.body ?? ''),
        draftCharacters,
        draftOverCeiling: draftCharacters > ceiling,
        asking: instructions[section.id] !== undefined,
        instruction: instructions[section.id] ?? '',
        busy: busy[section.id] ?? null,
        problem: problems[section.id] ?? '',
        notice: noticeOf(request),
        // What the scanner found in the text first, then what the writer said about its own work: the same
        // order the server keeps, so a caution found by code is never below one a model offered.
        warnings: [
          ...section.findings.map(
            (finding): LongFormSectionWarning => ({
              sectionId: section.id,
              kind: finding.kind,
              code: finding.code,
              message: finding.message,
              fromModel: false,
            }),
          ),
          ...review.warnings.filter((warning) => warning.sectionId === section.id),
        ],
        attribution: attributionOf(section),
        item:
          section.role === 'Item'
            ? { position: items.indexOf(section) + 1, of: items.length, unresolved: section.sourceState === 'Unresolved' }
            : null,
      };
    });
  });

  protected readonly anchors = computed<readonly CpAnchorNavItem[]>(() =>
    this.cards().map((card) => ({
      targetId: card.anchorId,
      label: card.section.heading,
      detail: card.dirty ? 'Unsaved changes' : card.state.word,
    })),
  );

  /** How long the piece is, said in words against what a piece of its kind usually runs to. */
  protected readonly length = computed<{ readonly tone: CpStatusPillTone; readonly word: string; readonly text: string } | null>(() => {
    const document = this.document();
    if (document === null) return null;

    const words = `${document.wordCount.toLocaleString('en')} words`;
    const usual = `A piece like this usually runs ${document.targetWords.min.toLocaleString('en')} to ${document.targetWords.max.toLocaleString('en')}.`;

    switch (document.wordTarget) {
      case 'Within':
        return { tone: 'success', word: 'A usual length', text: `${words}. ${usual}` };
      case 'Under':
        return { tone: 'warning', word: 'Shorter than usual', text: `${words}. ${usual}` };
      case 'Over':
        return { tone: 'warning', word: 'Longer than usual', text: `${words}. ${usual}` };
      default:
        return { tone: 'neutral', word: 'Not all written', text: `${words} so far. ${usual}` };
    }
  });

  /** What is wrong with the document, in sentences that name the sections. Empty when nothing is. */
  protected readonly issues = computed(() => {
    const document = this.document();
    return document === null ? [] : document.needsConfirmation.map((issue) => describeIssue(issue, document));
  });

  /** Whether "write the rest" has anything to write. */
  protected readonly restCount = computed(() => {
    const review = this.review();
    if (review === null) return 0;

    const waiting = new Set(review.requests.filter((request) => request.outcome === 'Waiting').map((request) => request.sectionId));

    // An item whose recipe is gone is not something the writer can be asked for, so it is not counted as left.
    return review.document.sections.filter(
      (section) =>
        !section.isWritten && !section.settled && !waiting.has(section.id) && section.sourceState !== 'Unresolved',
    ).length;
  });

  protected readonly canAccept = computed(
    () =>
      this.open() &&
      this.document()?.revisionNumber != null &&
      !this.accepting() &&
      !this.writingRest() &&
      !this.hasUnsavedWork() &&
      !this.anyWaiting() &&
      (this.document()?.sections.some((section) => section.isWritten) ?? false),
  );

  /** Why the piece cannot be accepted right now, when it cannot. Said beside the button, never left to guess. */
  protected readonly acceptHint = computed(() => {
    if (!this.open() || this.accepting()) return '';
    if (this.hasUnsavedWork()) return 'Save or discard your changes before accepting.';
    if (this.anyWaiting()) return 'A section is still being written.';
    if (!(this.document()?.sections.some((section) => section.isWritten) ?? false)) return 'Nothing has been written yet.';

    return '';
  });

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.gone = true;
      this.stopPolling();
    });

    // A browser close, refresh or full navigation cannot be confirmed by any library; the browser shows its
    // own message. Router navigation is the shell's guard, through confirmDiscardIfDirty.
    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (!this.hasUnsavedWork()) return;
      event.preventDefault();
    };
    window.addEventListener('beforeunload', beforeUnload);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', beforeUnload));

    // One effect for arriving and for a change of workspace or draft, keyed on both, so a component reused
    // rather than rebuilt cannot show one draft's text — or keep one draft's unsaved edits — under another.
    effect(() => {
      const key = `${this.workspaceSlug()}|${this.draftId()}`;
      if (key === this.followedKey) return;

      this.followedKey = key;
      untracked(() => {
        this.drafts.set({});
        this.instructions.set({});
        this.busy.set({});
        this.problems.set({});
        this.framingDraft.set(null);
        this.framingProblem.set('');
        this.framingErrors.set({});
        this.pendingKeys.clear();
        this.review.set(null);
        this.load.set('loading');
        void this.refresh();
      });
    });

    effect(() => {
      const document = this.document();
      if (document !== null) untracked(() => this.documentRead.emit(document));
    });

    // While something is being written, ask again after a pause; when nothing is, stop asking.
    effect(() => {
      const waiting = this.anyWaiting();
      untracked(() => (waiting ? this.schedulePoll() : this.stopPolling()));
    });
  }

  /**
   * Asked before leaving: true to go, false to stay. Unsaved text is confirmed away, never dropped silently.
   */
  confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.hasUnsavedWork()) return Promise.resolve(true);

    return this.confirm.confirm({
      title: 'Leave without saving?',
      message: 'The changes you made to this piece have not been saved and will be lost.',
      confirmLabel: 'Leave and lose changes',
      cancelLabel: 'Stay',
      tone: 'danger',
    });
  }

  protected fieldId(card: SectionCard, part: string): string {
    return `${this.idPrefix()}-${card.section.id}-${part}`;
  }

  /** A jump to a section: scrolled to and focused, so a keyboard and a screen reader both land on it. */
  protected jump(item: CpAnchorNavItem): void {
    const target = document.getElementById(item.targetId);
    target?.scrollIntoView({ block: 'start' });
    target?.focus({ preventScroll: true });
  }

  protected onSubject(event: Event): void {
    this.framingDraft.set({ ...this.framing(), subject: (event.target as HTMLInputElement).value });
  }

  protected onNote(event: Event): void {
    this.framingDraft.set({ ...this.framing(), note: (event.target as HTMLTextAreaElement).value });
  }

  /** Saves the subject and the note. On anything but a save, both stay in their boxes as typed. */
  protected async saveFraming(): Promise<void> {
    const typed = this.framingDraft();
    if (typed === null || !this.framingDirty() || this.framingBusy() || !this.open()) return;

    this.framingBusy.set(true);
    this.framingProblem.set('');
    this.framingErrors.set({});

    const send = (): Promise<LongFormPlanWriteOutcome> =>
      this.issue.putFraming(this.workspaceSlug(), this.draftId(), {
        expectedConcurrencyToken: this.document()?.concurrencyToken ?? '',
        subject: typed.subject.trim() === '' ? null : typed.subject,
        note: typed.note.trim() === '' ? null : typed.note,
      });

    let outcome = await send();

    // The token moves when a section lands. That is not these words being out of date, so the piece is read
    // again and the save is sent once more.
    if (outcome.status === 'stale') {
      await this.refresh();
      if (this.gone) return;
      outcome = await send();
    }

    if (this.gone) return;
    this.framingBusy.set(false);

    if (outcome.status === 'saved') {
      // Only if they have not typed more while it was saving.
      if (this.framingDraft() === typed) this.framingDraft.set(null);

      await this.refresh();
      this.announce('Saved the subject and note.');
      return;
    }

    if (outcome.status === 'refused') this.framingErrors.set(outcome.fieldErrors);
    this.framingProblem.set(issueProblem(outcome, 'Your words are still here'));
  }

  /** Puts the subject and note back to what is saved. Confirmed first: they are the creator's own words. */
  protected async discardFraming(): Promise<void> {
    if (!this.framingDirty()) return;

    const confirmed = await this.confirm.confirm({
      title: 'Discard your changes?',
      message: 'The subject and note will go back to how they were when you last saved them.',
      confirmLabel: 'Discard changes',
      cancelLabel: 'Keep editing',
      tone: 'danger',
    });
    if (!confirmed || this.gone) return;

    this.framingDraft.set(null);
    this.framingProblem.set('');
    this.framingErrors.set({});
    this.focusAfterRender(`${this.idPrefix()}-subject`);
  }

  /**
   * Moves one item a place among the issue's items. The order sent names each item by the recipe it covers,
   * so a move can only rearrange them; every other section stays where it is.
   */
  protected async move(card: SectionCard, by: -1 | 1): Promise<void> {
    if (card.item === null || this.moving() || !this.open()) return;

    const order = (): string[] | null => {
      const items = (this.document()?.sections ?? []).filter((section) => section.role === 'Item');
      const from = items.findIndex((section) => section.id === card.section.id);
      const to = from + by;

      // Every item must say what it covers, or there is no order to name.
      if (from < 0 || to < 0 || to >= items.length || items.some((section) => section.sourceReferenceId === null)) {
        return null;
      }

      const ids = items.map((section) => section.sourceReferenceId as string);
      [ids[from], ids[to]] = [ids[to]!, ids[from]!];

      return ids;
    };

    const send = (ids: string[]): Promise<LongFormPlanWriteOutcome> =>
      this.issue.reorderItems(this.workspaceSlug(), this.draftId(), {
        expectedConcurrencyToken: this.document()?.concurrencyToken ?? '',
        sourceReferenceIds: ids,
      });

    const first = order();
    if (first === null) return;

    this.moving.set(true);
    this.clearProblem(card.section.id);

    let outcome = await send(first);

    if (outcome.status === 'stale') {
      await this.refresh();
      if (this.gone) return;

      // Worked out again from the piece as it now reads: the move is "this item, one place", not a list.
      const again = order();
      outcome = again === null ? outcome : await send(again);
    }

    if (this.gone) return;
    this.moving.set(false);

    if (outcome.status === 'saved') {
      await this.refresh();
      this.announce(`Moved “${card.section.heading}” ${by === -1 ? 'up' : 'down'}.`);
      this.focusAfterRender(this.fieldId(card, by === -1 ? 'up' : 'down'));
      return;
    }

    this.setIn(this.problems, card.section.id, issueProblem(outcome, 'Nothing was moved'));
  }

  protected edit(card: SectionCard): void {
    if (!this.open() || card.draft !== null) return;

    this.setIn(this.drafts, card.section.id, card.section.body ?? '');
    this.clearProblem(card.section.id);
    this.focusAfterRender(this.fieldId(card, 'body'));
  }

  protected onTyped(card: SectionCard, event: Event): void {
    this.setIn(this.drafts, card.section.id, (event.target as HTMLTextAreaElement).value);
  }

  /** Stops editing. Text that differs from what is saved is confirmed away first. */
  protected async cancelEdit(card: SectionCard): Promise<void> {
    if (card.dirty) {
      const confirmed = await this.confirm.confirm({
        title: 'Discard your changes?',
        message: 'This section will go back to how it was when you last saved it.',
        confirmLabel: 'Discard changes',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      });
      if (!confirmed || this.gone) return;
    }

    this.removeFrom(this.drafts, card.section.id);
    this.clearProblem(card.section.id);
    this.focusAfterRender(this.fieldId(card, 'edit'));
  }

  /** Saves the creator's text for one section. On anything but a save, the text stays in the box. */
  protected async save(card: SectionCard): Promise<void> {
    const id = card.section.id;
    const text = this.drafts()[id];
    if (text === undefined || this.busy()[id] !== undefined) return;

    this.setIn(this.busy, id, 'saving');
    this.clearProblem(id);

    let outcome = await this.sendEdit(id, text);

    // The draft's token moves whenever anything about it does — another section landing is enough. That is
    // not the creator's text being out of date, so the document is read again and the save is sent once more.
    if (outcome.status === 'stale') {
      await this.refresh();
      if (this.gone) return;
      outcome = await this.sendEdit(id, text);
    }

    if (this.gone) return;
    this.removeFrom(this.busy, id);

    if (outcome.status === 'saved') {
      // Only now, and only if they have not typed more while it was saving.
      if (this.drafts()[id] === text) this.removeFrom(this.drafts, id);

      await this.refresh();
      this.announce(`Saved “${card.section.heading}”.`);
      this.focusAfterRender(this.fieldId(card, 'edit'));
      return;
    }

    this.setIn(this.problems, id, writeProblem(outcome));
  }

  protected openAsk(card: SectionCard): void {
    if (!this.open() || this.blocked()) return;

    this.setIn(this.instructions, card.section.id, '');
    this.clearProblem(card.section.id);
    this.focusAfterRender(this.fieldId(card, 'instruction'));
  }

  protected closeAsk(card: SectionCard): void {
    this.removeFrom(this.instructions, card.section.id);
    this.focusAfterRender(this.fieldId(card, 'again'));
  }

  protected onInstruction(card: SectionCard, event: Event): void {
    this.setIn(this.instructions, card.section.id, (event.target as HTMLTextAreaElement).value);
  }

  /** Asks for one section to be written, or written again. Another section's unsaved text is not touched. */
  protected async write(card: SectionCard): Promise<void> {
    const id = card.section.id;
    const document = this.document();
    if (document === null || document.revisionNumber === null || this.busy()[id] !== undefined || this.blocked()) return;

    // Writing over text the creator is in the middle of changing would lose it. They are asked first.
    if (card.dirty) {
      const confirmed = await this.confirm.confirm({
        title: 'Write this section again?',
        message: 'The changes you have not saved to this section will be lost.',
        confirmLabel: 'Write it again',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      });
      if (!confirmed || this.gone) return;
    }

    this.setIn(this.busy, id, 'asking');
    this.clearProblem(id);
    this.refusal.set(null);

    // Kept until the server has answered for it, so a request that may have arrived is retried as itself.
    const key = this.pendingKeys.get(id) ?? crypto.randomUUID();
    this.pendingKeys.set(id, key);

    const outcome = await this.service.requestSection(
      this.workspaceSlug(),
      this.draftId(),
      id,
      { revisionNumber: document.revisionNumber, instruction: this.instructions()[id] ?? null },
      key,
    );
    if (this.gone) return;

    this.removeFrom(this.busy, id);
    if (outcome.status !== 'unavailable') this.pendingKeys.delete(id);

    if (outcome.status === 'accepted') {
      this.removeFrom(this.drafts, id);
      this.removeFrom(this.instructions, id);
      await this.refresh();
      this.announce(`“${card.section.heading}” is being written.`);

      // The button that was pressed is gone — the section is being written now — so focus goes to the
      // section itself rather than being dropped to the top of the page.
      this.focusAfterRender(card.anchorId);
      return;
    }

    if (outcome.status === 'blocked') this.refusal.set(outcome.refusal);
    this.setIn(this.problems, id, writingProblem(outcome));
  }

  /** Asks for every section not yet written. All of them are queued, or none is. */
  protected async writeRest(): Promise<void> {
    const document = this.document();
    if (document === null || document.revisionNumber === null || this.writingRest() || this.blocked()) return;

    this.writingRest.set(true);
    this.problem.set('');
    this.refusal.set(null);

    const key = this.pendingKeys.get('') ?? crypto.randomUUID();
    this.pendingKeys.set('', key);

    const outcome = await this.service.requestRest(this.workspaceSlug(), this.draftId(), document.revisionNumber, key);
    if (this.gone) return;

    this.writingRest.set(false);
    if (outcome.status !== 'unavailable') this.pendingKeys.delete('');

    if (outcome.status === 'accepted') {
      await this.refresh();
      this.announce(
        outcome.requests.length === 0
          ? 'There was nothing left to write.'
          : `${outcome.requests.length} ${outcome.requests.length === 1 ? 'section is' : 'sections are'} being written.`,
      );
      return;
    }

    if (outcome.status === 'blocked') this.refusal.set(outcome.refusal);
    this.problem.set(writingProblem(outcome));
  }

  protected async settle(card: SectionCard, settled: boolean): Promise<void> {
    const id = card.section.id;
    if (this.busy()[id] !== undefined || !this.open()) return;

    this.setIn(this.busy, id, 'settling');
    this.clearProblem(id);

    let outcome = await this.sendSettle(id, settled);

    if (outcome.status === 'stale') {
      await this.refresh();
      if (this.gone) return;
      outcome = await this.sendSettle(id, settled);
    }

    if (this.gone) return;
    this.removeFrom(this.busy, id);

    if (outcome.status === 'saved') {
      await this.refresh();
      this.announce(settled ? `Marked “${card.section.heading}” as settled.` : `“${card.section.heading}” is no longer marked as settled.`);
      this.focusAfterRender(this.fieldId(card, 'settle'));
      return;
    }

    this.setIn(this.problems, id, writeProblem(outcome));
  }

  /**
   * Accepts the document. When the server says something is wrong with it, the creator is told what, by
   * name, and the acceptance is sent only if they say yes — carrying exactly the issues they were shown.
   */
  protected async accept(): Promise<void> {
    const document = this.document();
    if (document === null || document.revisionNumber === null || !this.canAccept()) return;

    const shown = document.needsConfirmation;

    if (shown.length > 0) {
      const confirmed = await this.confirm.confirm({
        title: 'Accept this piece as it is?',
        message: `${shown.map((issue) => describeIssue(issue, document)).join(' ')} You can still accept it as it stands.`,
        confirmLabel: 'Accept as it is',
        cancelLabel: 'Keep working',
        tone: 'neutral',
      });
      if (!confirmed || this.gone) return;
    }

    this.accepting.set(true);
    this.problem.set('');

    const outcome = await this.service.accept(this.workspaceSlug(), this.draftId(), {
      expectedConcurrencyToken: document.concurrencyToken,
      revisionNumber: document.revisionNumber,
      confirmed: [...new Set(shown.map((issue): LongFormAcceptanceIssue => issue.issue))],
    });
    if (this.gone) return;

    this.accepting.set(false);

    switch (outcome.status) {
      case 'accepted':
        await this.refresh();
        this.announce('This piece is accepted. Nothing has been published.');

        // Every action on the screen has just gone. Focus goes to the top of the piece, where it says so.
        this.focusAfterRender(`${this.idPrefix()}-summary`);
        this.accepted.emit(outcome.document);
        return;

      case 'confirmation_required':
      case 'stale':
        // The document is not the one that was confirmed. It is read again and nothing is assumed: the
        // creator sees what it is now, and decides again.
        await this.refresh();
        this.problem.set('This piece changed while you were deciding. Look at it again, then accept it.');
        return;

      case 'refused':
        this.problem.set(outcome.message);
        return;

      case 'not_found':
        this.problem.set('This draft is no longer here.');
        return;

      case 'forbidden':
        this.problem.set('You need to be a contributor to accept this piece.');
        return;

      default:
        this.problem.set('We couldn’t accept that just now. Nothing was changed — try again in a moment.');
    }
  }

  protected retry(): void {
    void this.refresh();
  }

  /**
   * Reads the document again. What is on screen is replaced; the creator's unsaved text is not part of it and
   * is left alone. A failed refresh of a document already shown keeps it shown and says it may be behind.
   */
  private async refresh(): Promise<void> {
    const slug = this.workspaceSlug();
    const draftId = this.draftId();
    const outcome = await this.service.read(slug, draftId);

    // The inputs moved on while this was in flight, or the screen has gone: the answer is for another draft.
    if (this.gone || slug !== this.workspaceSlug() || draftId !== this.draftId()) return;

    if (outcome.status === 'found') {
      this.review.set(outcome.review);
      this.behind.set(false);
      this.load.set('ready');

      // The effect that starts polling watches for a change; a refresh that found things still waiting has
      // changed nothing it can see, so the next ask is arranged here.
      if (this.anyWaiting()) this.schedulePoll();
      return;
    }

    if (this.review() !== null && outcome.status === 'unavailable') {
      this.behind.set(true);
      if (this.anyWaiting()) this.schedulePoll();
      return;
    }

    this.load.set(outcome.status);
  }

  private sendEdit(sectionId: string, body: string): Promise<LongFormSectionWriteOutcome> {
    return this.service.editSection(this.workspaceSlug(), this.draftId(), sectionId, {
      expectedConcurrencyToken: this.document()?.concurrencyToken ?? '',
      body,
    });
  }

  private sendSettle(sectionId: string, settled: boolean): Promise<LongFormSectionWriteOutcome> {
    return this.service.settleSection(this.workspaceSlug(), this.draftId(), sectionId, {
      expectedConcurrencyToken: this.document()?.concurrencyToken ?? '',
      settled,
    });
  }

  private schedulePoll(): void {
    if (this.pollTimer !== null || this.gone) return;

    this.pollTimer = setTimeout(() => {
      this.pollTimer = null;
      void this.refresh();
    }, LONG_FORM_REVIEW_POLL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  private announce(text: string): void {
    this.announcement.set(text);
  }

  private clearProblem(sectionId: string): void {
    this.removeFrom(this.problems, sectionId);
  }

  private setIn<T>(target: WritableSignal<Readonly<Record<string, T>>>, key: string, value: T): void {
    target.set({ ...target(), [key]: value });
  }

  private removeFrom<T>(target: WritableSignal<Readonly<Record<string, T>>>, key: string): void {
    if (target()[key] === undefined) return;

    target.set(Object.fromEntries(Object.entries(target()).filter(([candidate]) => candidate !== key)));
  }

  /** Focus, once the change has been rendered. Found in the document, not in this host. */
  private focusAfterRender(id: string): void {
    afterNextRender(
      () => {
        if (!this.gone) document.getElementById(id)?.focus();
      },
      { injector: this.injector },
    );
  }
}

/** Where one section stands, in a word and a tone. Never a colour alone: the pill carries a glyph too. */
function stateOf(
  section: LongFormDocumentSection,
  request: LongFormSectionRequestSummary | null,
): { readonly tone: CpStatusPillTone; readonly word: string } {
  if (request?.outcome === 'Waiting') return { tone: 'progress', word: 'Being written' };
  if (!section.isWritten) return { tone: 'neutral', word: 'Not written yet' };
  if (section.overCeiling) return { tone: 'warning', word: 'Too long' };
  if (section.settled) return { tone: 'success', word: 'Settled' };

  return { tone: 'neutral', word: 'Written' };
}

/** What the latest writing request did, when the creator should be told. Empty when there is nothing to say. */
function noticeOf(request: LongFormSectionRequestSummary | null): string {
  if (request === null) return '';

  if (request.outcome === 'Failed') {
    return request.failureCategory === 'Cancelled'
      ? 'Writing this section was cancelled. Nothing was changed.'
      : request.failureCategory === 'Quota'
        ? 'Your allowance ran out before this section could be written. Nothing was changed.'
        : 'This section could not be written this time. Nothing was changed.';
  }

  return '';
}

/** What moved, said the way a creator would say it. Keyed by the server's stale reason. */
const STALE_SOURCES: Readonly<Record<string, string>> = {
  RecipeChanged: 'A recipe this piece was written from has changed.',
  VoiceChanged: 'Your brand style guide has changed.',
};

const EXPORTS: readonly { readonly format: LongFormExportFormat; readonly label: string }[] = [
  { format: 'markdown', label: 'Download Markdown' },
  { format: 'html', label: 'Download HTML' },
];

/** Whose words a section began as, when it was written from copy the creator had already accepted (B-36). */
function attributionOf(section: LongFormDocumentSection): string {
  if (section.reusedEditorialParts.length === 0) return '';

  const parts = section.reusedEditorialParts.map(longFormEditorialPartLabel);
  const named = parts.length === 1 ? parts[0] : `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`;

  return `Written from the ${named} you already accepted for this recipe.`;
}

/** One thing wrong with the document, as a sentence naming the sections it is about. */
function describeIssue(issue: LongFormAcceptanceIssueDetail, document: LongFormAssembledDocument): string {
  const headings = issue.sectionIds
    .map((id) => document.sections.find((section) => section.id === id)?.heading)
    .filter((heading): heading is string => heading !== undefined)
    .map((heading) => `“${heading}”`);
  const named = headings.length <= 1 ? (headings[0] ?? '') : `${headings.slice(0, -1).join(', ')} and ${headings[headings.length - 1]}`;

  if (issue.issue === 'UnwrittenSections') {
    return headings.length === 1 ? `${named} has not been written.` : `${headings.length} sections have not been written: ${named}.`;
  }

  if (headings.length === 0) return 'The piece as a whole is longer than it may be.';

  return headings.length === 1 ? `${named} is longer than a section may be.` : `${headings.length} sections are longer than a section may be: ${named}.`;
}

function writeProblem(outcome: Exclude<LongFormSectionWriteOutcome, { readonly status: 'saved' }>): string {
  switch (outcome.status) {
    case 'stale':
      return 'This piece changed while that was being saved. Your text is still here — try again.';
    case 'refused':
      return outcome.fieldErrors['body']?.[0] ?? outcome.message;
    case 'not_found':
      return 'This section is no longer part of the piece.';
    case 'forbidden':
      return 'You need to be a contributor to change this piece.';
    default:
      return 'We couldn’t save that. Your text is still here — check your connection and try again.';
  }
}

/** Why a save of the subject and note, or a move of an item, did not happen. */
function issueProblem(outcome: Exclude<LongFormPlanWriteOutcome, { readonly status: 'saved' }>, kept: string): string {
  switch (outcome.status) {
    case 'stale':
      return `This piece changed while that was being saved. ${kept} — try again.`;
    case 'refused':
    case 'locked':
    case 'not_started':
      return outcome.message;
    case 'not_found':
      return 'This draft is no longer here.';
    case 'forbidden':
      return 'You need to be a contributor to change this piece.';
    default:
      return `We couldn’t save that. ${kept} — check your connection and try again.`;
  }
}

function writingProblem(outcome: Exclude<LongFormWritingOutcome, { readonly status: 'accepted' }>): string {
  switch (outcome.status) {
    case 'blocked':
      return outcome.sectionsRequested !== null && outcome.sectionsCoverable !== null
        ? `Your allowance covers ${outcome.sectionsCoverable} of the ${outcome.sectionsRequested} sections left, so none was started. Write them one at a time, or wait for your allowance to come back.`
        : 'Your allowance does not cover this just now. Nothing was started.';
    case 'refused':
      return outcome.message;
    case 'not_enabled':
      return 'Writing sections is switched off for this workspace.';
    case 'key_reused':
      return 'That request was already used for something else. Ask again.';
    case 'not_found':
      return 'This section is no longer part of the piece.';
    case 'forbidden':
      return 'You need to be a contributor to have a section written.';
    default:
      return 'We couldn’t reach the writer. Nothing was started — try again in a moment.';
  }
}
