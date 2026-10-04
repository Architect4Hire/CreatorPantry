import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  InjectionToken,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpCardComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpComboboxComponent,
  CpComboboxOption,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpNoticeTone,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  BrandStyleGuideCitation,
  BrandStyleGuideDetail,
  BrandStyleGuideRuleContent,
  BrandStyleGuideRuleKind,
  BrandStyleGuideSectionEditRequest,
  BrandStyleGuideVersionRow,
  BrandStyleGuideVersionSaveRequest,
} from '../../models/brand-style-guide.models';
import { BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { BrandStyleGuideService } from '../../services/brand-style-guide.service';
import { GUIDE_RULE_KIND_LABELS, GUIDE_SECTION_LABELS, guideSectionLabel } from './brand-guide-labels';
import {
  BrandGuideChange,
  BrandGuideEditState,
  applyChange,
  cite,
  composeChange,
  composeDraft,
  composeSaveRequest,
  emptyChange,
  isDirty,
  readDraft,
  seedFromVersion,
} from './brand-guide-editor-state';

type LoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** Where autosave stands, which is the one thing a creator watches while they type. */
type KeepState =
  | { readonly status: 'idle' }
  | { readonly status: 'keeping' }
  | { readonly status: 'kept'; readonly at: string }
  /** The caller's own draft moved somewhere else — a second tab, or a save that wrote a version. */
  | { readonly status: 'conflict' }
  | { readonly status: 'failed' };

type SaveState =
  | { readonly status: 'idle' }
  | { readonly status: 'saving' }
  | { readonly status: 'saved'; readonly versionNumber: number }
  /** The edit said what the guide already said, so no version was written. */
  | { readonly status: 'unchanged' }
  /** The guide gained a version while this was being composed. Nothing was written. */
  | { readonly status: 'rebase_needed'; readonly workingVersionNumber: number | null }
  | { readonly status: 'unusable_sources'; readonly sources: readonly BrandStyleGuideCitation[] }
  | { readonly status: 'archived' }
  | { readonly status: 'limit' }
  | { readonly status: 'invalid' }
  | { readonly status: 'forbidden' }
  /** The guide is no longer readable in this workspace — removed, or access withdrawn mid-edit. */
  | { readonly status: 'not_found' }
  | { readonly status: 'key_reused' }
  | { readonly status: 'unavailable' };

/** How one cited example reads: its title where we know it, and whether it has been replaced since. */
interface CitationView {
  readonly documentId: string;
  readonly versionNumber: number;
  readonly title: string;
  /** The document's current version, when it could be read. */
  readonly currentVersionNumber: number | null;
  readonly isStale: boolean;
}

const ANCHOR = 'cp-guide-part-';

/**
 * How long autosave waits after the last keystroke, in milliseconds.
 *
 * Injectable so a test can drive the editor without waiting on a real timer — a component whose behaviour can
 * only be observed after a wall-clock delay ends up tested through sleeps, which is how a suite becomes slow
 * and flaky at once. The product never overrides it.
 */
export const BRAND_GUIDE_AUTOSAVE_DELAY_MS = new InjectionToken<number>('brand guide autosave delay', {
  providedIn: 'root',
  factory: () => 1500,
});

/**
 * The editor for one brand style guide: the creator's own words, kept as they type and written as one further
 * immutable version when they say so.
 *
 * **Their edits are the guide.** Nothing here regenerates a part, and no model runs: the form starts from the
 * working version, or from the draft they left behind, and what they type is what gets written. The editor
 * never re-derives a section they have touched — the recipe editor's `Creator`-versus-`Composed` rule, with
 * no composed case, because every word in a guide is theirs.
 *
 * **Autosave is a draft, never a version.** A guide version is immutable, so saving every few seconds cannot
 * mean writing one; it means keeping a scratch copy, server-side, private to this creator. Closing the tab
 * costs nothing. `Save new version` is what makes it part of the guide, and that write clears the draft.
 *
 * **Concurrency is recoverable rather than fatal.** A version written by somebody else while this was open
 * does not cost the creator their words: the submitted change names only the parts they touched, so the editor
 * re-reads the guide and offers to apply the same change over the newer version. The creator is told which
 * version they are now building on, and nothing is rebased without them pressing the button.
 *
 * **Nothing here approves or activates.** Both are separate decisions on the history screen, behind their own
 * roles. This screen's version is a draft like any other.
 */
@Component({
  selector: 'cp-brand-guide-editor',
  standalone: true,
  imports: [
    FormsModule,
    RouterLink,
    CpAnchorNavComponent,
    CpButtonComponent,
    CpCardComponent,
    CpChoiceGroupComponent,
    CpComboboxComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './brand-guide-editor.component.html',
  styleUrl: './brand-guide-editor.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandGuideEditorComponent {
  private readonly guides = inject(BrandStyleGuideService);
  private readonly documents = inject(BrandSourceDocumentService);
  private readonly confirmService = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly keepDelayMs = inject(BRAND_GUIDE_AUTOSAVE_DELAY_MS);

  readonly workspaceSlug = this.resolveRouteParam('workspaceSlug');
  readonly guideId = this.resolveRouteParam('guideId');

  private readonly loadStateSignal = signal<LoadState>({ status: 'loading' });
  readonly loadState = this.loadStateSignal.asReadonly();

  private readonly guideSignal = signal<BrandStyleGuideDetail | null>(null);
  readonly guide = this.guideSignal.asReadonly();

  /** The working version's history row, which is the only place the stale-citation count is published. */
  private readonly rowSignal = signal<BrandStyleGuideVersionRow | null>(null);
  readonly row = this.rowSignal.asReadonly();

  /** What is in the form. */
  readonly state = signal<BrandGuideEditState>({ sections: [], rules: [], citations: [], changeReason: '' });

  /** What the form started from: the working version, or the draft the creator left. */
  private baseline: BrandGuideEditState = { sections: [], rules: [], citations: [], changeReason: '' };

  private readonly keepStateSignal = signal<KeepState>({ status: 'idle' });
  readonly keepState = this.keepStateSignal.asReadonly();

  private readonly saveStateSignal = signal<SaveState>({ status: 'idle' });
  readonly saveState = this.saveStateSignal.asReadonly();

  /** True when the draft that seeded this form was composed against an older version. */
  private readonly resumedStaleSignal = signal(false);
  readonly resumedStale = this.resumedStaleSignal.asReadonly();

  /** True when the form was seeded from a kept draft rather than from the guide. */
  private readonly resumedSignal = signal(false);
  readonly resumed = this.resumedSignal.asReadonly();

  private readonly citationViewsSignal = signal<readonly CitationView[]>([]);
  readonly citationViews = this.citationViewsSignal.asReadonly();

  /** The version the form is being composed against; what a save quotes and a rebase moves. */
  private readonly baseVersionSignal = signal(0);
  readonly baseVersion = this.baseVersionSignal.asReadonly();

  private draftRowVersion: string | null = null;
  private keepTimer: ReturnType<typeof setTimeout> | null = null;
  private keeping = false;
  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  /** Which part a creator is adding, chosen from the keys this guide does not hold yet. */
  readonly partToAdd = signal('');
  readonly channelToAdd = signal('');

  constructor() {
    void this.load();

    // A tab closed mid-edit still has whatever the last autosave kept; this flushes the one in flight rather
    // than leaving the final keystrokes behind. The browser's own dialog is the only thing that can hold a
    // close, and this is not that — it is a best-effort last keep.
    this.destroyRef.onDestroy(() => this.cancelPendingKeep());
  }

  // ---- Rendering ----

  readonly isArchived = computed(() => this.guide()?.isArchived === true);

  /**
   * What the creator has changed, as named parts.
   *
   * Everything downstream reads this rather than the form: autosave keeps it, a save sends it, and a rebase
   * applies it to a newer version. One derivation, so the three cannot disagree about what was changed.
   */
  readonly change = computed<BrandGuideChange>(() => composeChange(this.state(), this.baseline));

  readonly dirty = computed(() => isDirty(this.change()));

  readonly statusTone = computed<CpStatusPillTone>(() =>
    this.row()?.status === 'Approved' ? 'success' : 'neutral',
  );

  readonly staleSourceCount = computed(() => this.row()?.staleSourceCount ?? 0);

  readonly isActiveVersion = computed(() => {
    const guide = this.guide();

    return guide !== null && guide.activeVersionId === guide.workingVersion.id;
  });

  /** Every part on screen, in the server's section order, with its anchor and whether it says anything. */
  readonly parts = computed(() =>
    this.state().sections.map((section) => ({
      section,
      anchorId: ANCHOR + section.sectionKey + (section.channelKey ?? ''),
      label: guideSectionLabel(section.sectionKey, section.channelKey),
      written: section.body.trim().length > 0,
    })),
  );

  readonly anchors = computed<readonly CpAnchorNavItem[]>(() =>
    this.parts().map((part) => ({
      targetId: part.anchorId,
      label: part.label,
      detail: part.written ? 'Written' : 'Empty',
    })),
  );

  /** The section keys this guide does not hold yet, so a creator can add one. */
  readonly addableKeys = computed(() => {
    const held = new Set(this.state().sections.filter((each) => each.channelKey === null).map((each) => each.sectionKey));

    return Object.keys(GUIDE_SECTION_LABELS).filter((key) => key === 'ChannelVariant' || !held.has(key));
  });

  /** The addable keys as the combobox takes them: the section key is the id, its words are the label. */
  readonly addableOptions = computed<readonly CpComboboxOption[]>(() =>
    this.addableKeys().map((key) => ({ id: key, label: GUIDE_SECTION_LABELS[key] ?? key })),
  );

  readonly selectedPart = computed<CpComboboxOption | null>(() => {
    const key = this.partToAdd();

    return key.length === 0 ? null : { id: key, label: GUIDE_SECTION_LABELS[key] ?? key };
  });

  readonly addingVariant = computed(() => this.partToAdd() === 'ChannelVariant');

  /** Always or never, as the two tiles a rule's kind is chosen from. */
  readonly ruleKindOptions: readonly CpChoiceOption[] = [
    { value: 'Do', label: GUIDE_RULE_KIND_LABELS.Do, hint: 'Something the writing always does.' },
    { value: 'Dont', label: GUIDE_RULE_KIND_LABELS.Dont, hint: 'Something it never does.' },
  ];

  /** One line about where the whole screen stands, for the live region. */
  readonly statusMessage = computed(() => {
    if (this.loadState().status === 'loading') return 'Loading this guide…';

    switch (this.saveState().status) {
      case 'saving':
        return 'Saving a new version…';
      case 'saved':
        return `Saved as version ${(this.saveState() as { versionNumber: number }).versionNumber}.`;
      case 'unchanged':
        return 'Nothing had changed, so no new version was written.';
      default:
        break;
    }

    switch (this.keepState().status) {
      case 'keeping':
        return 'Keeping your changes…';
      case 'kept':
        return 'Your changes are kept.';
      case 'failed':
        return "Couldn't keep your changes.";
      case 'conflict':
        return 'Your unsaved copy changed somewhere else.';
      default:
        return this.dirty() ? 'You have unsaved changes.' : '';
    }
  });

  /** The version a save wrote, for the sentence that reports it. Zero when none has been written here. */
  readonly savedVersionNumber = computed(() => {
    const state = this.saveState();

    return state.status === 'saved' ? state.versionNumber : 0;
  });

  readonly noticeTone = computed<CpNoticeTone>(() => {
    switch (this.saveState().status) {
      case 'saved':
        return 'success';
      case 'unchanged':
        return 'neutral';
      default:
        return 'error';
    }
  });

  // ---- Reading ----

  async load(): Promise<void> {
    this.loadStateSignal.set({ status: 'loading' });

    const [detail, history, session] = await Promise.all([
      this.guides.getDetail(this.workspaceSlug, this.guideId),
      this.guides.listVersions(this.workspaceSlug, this.guideId, null, 1),
      this.guides.getEditSession(this.workspaceSlug, this.guideId),
    ]);

    if (detail.status !== 'ok') {
      this.loadStateSignal.set(
        detail.status === 'not_found' ? { status: 'not_found' } : { status: 'unavailable' },
      );

      return;
    }

    if (session.status === 'forbidden') {
      // The draft routes carry the Editor bar, and so does saving a version: a reader who cannot keep a draft
      // cannot keep a version either, so this screen has nothing to offer them.
      this.loadStateSignal.set({ status: 'forbidden' });

      return;
    }

    this.guideSignal.set(detail.guide);
    this.rowSignal.set(history.status === 'ok' ? (history.page.items[0] ?? null) : null);
    this.baseVersionSignal.set(detail.guide.workingVersion.versionNumber);

    const fromVersion = seedFromVersion(detail.guide.workingVersion);
    const resumed = session.status === 'ok' ? readDraft(session.session.draftJson) : null;

    this.draftRowVersion = session.status === 'ok' ? session.session.rowVersion : null;
    this.resumedSignal.set(resumed !== null);
    this.resumedStaleSignal.set(session.status === 'ok' && session.session.isStale);

    // The baseline is always the stored version, never the draft: a change means "differs from what is
    // saved", which is the question the Save button and the navigation warning both ask.
    this.baseline = fromVersion;

    // A kept draft is a change, so resuming is that change laid over the version as it stands now. A part
    // somebody else added since is therefore present and untouched, and a part this creator removed stays
    // removed — which a stored form could not have told apart.
    this.state.set(resumed === null ? fromVersion : applyChange(detail.guide.workingVersion, resumed));
    this.keepStateSignal.set({ status: 'idle' });
    this.saveStateSignal.set({ status: 'idle' });
    this.loadStateSignal.set({ status: 'ready' });

    void this.loadCitationTitles();
  }

  /**
   * Resolves each cited example's title, and whether the document has been replaced past the pinned version.
   *
   * The count that drives the Stale pill is the server's; this is only which rows to mark, which needs the
   * document's own current version and so a read per document. A failure costs a title, not the screen.
   */
  private async loadCitationTitles(): Promise<void> {
    const citations = this.state().citations;

    const views = await Promise.all(
      citations.map(async (citation): Promise<CitationView> => {
        const outcome = await this.documents.get(this.workspaceSlug, citation.documentId);

        if (outcome.status !== 'ok') {
          return {
            ...citation,
            title:
              outcome.status === 'not_found'
                ? 'An example that is no longer in your library'
                : "An example we couldn't look up just now",
            currentVersionNumber: null,
            isStale: outcome.status === 'not_found',
          };
        }

        return {
          ...citation,
          title: outcome.document.title,
          currentVersionNumber: outcome.document.versionNumber,
          isStale: outcome.document.versionNumber > citation.versionNumber,
        };
      }),
    );

    this.citationViewsSignal.set(views);
  }

  // ---- Editing ----

  setSectionBody(sectionKey: string, channelKey: string | null, body: string): void {
    this.state.update((state) => ({
      ...state,
      sections: state.sections.map((section) =>
        section.sectionKey === sectionKey && section.channelKey === channelKey ? { ...section, body } : section,
      ),
    }));

    this.scheduleKeep();
  }

  /** Removes a part from the form. The version it came from is untouched; saving is what records the removal. */
  async clearSection(sectionKey: string, channelKey: string | null): Promise<void> {
    const label = guideSectionLabel(sectionKey, channelKey);

    const confirmed = await this.confirmService.confirm({
      title: `Remove ${label}?`,
      message:
        'It stays in every version already saved. This takes it out of the next version you save, and you can put it back by typing it again before you save.',
      confirmLabel: 'Remove it',
      cancelLabel: 'Keep it',
      tone: 'danger',
    });

    if (!confirmed) return;

    this.state.update((state) => ({
      ...state,
      sections: state.sections.filter(
        (section) => !(section.sectionKey === sectionKey && section.channelKey === channelKey),
      ),
    }));

    this.scheduleKeep();
  }

  addSection(): void {
    const key = this.partToAdd();
    if (key.length === 0) return;

    const channel = key === 'ChannelVariant' ? this.channelToAdd().trim().toLowerCase() : '';
    if (key === 'ChannelVariant' && channel.length === 0) return;

    const channelKey = key === 'ChannelVariant' ? channel : null;
    const held = this.state().sections.some(
      (section) => section.sectionKey === key && section.channelKey === channelKey,
    );

    if (held) return;

    this.state.update((state) => ({
      ...state,
      sections: [...state.sections, { sectionKey: key, channelKey, body: '' }],
    }));

    this.partToAdd.set('');
    this.channelToAdd.set('');
    this.scheduleKeep();
    this.revealPart(ANCHOR + key + (channelKey ?? ''));
  }

  /** Which part the creator picked, or none when they cleared the box. */
  choosePart(option: CpComboboxOption | null): void {
    this.partToAdd.set(option?.id ?? '');

    if (option?.id !== 'ChannelVariant') this.channelToAdd.set('');
  }

  setRuleText(index: number, text: string): void {
    this.state.update((state) => ({
      ...state,
      rules: state.rules.map((rule, at) => (at === index ? { ...rule, text } : rule)),
    }));

    this.scheduleKeep();
  }

  setRuleKind(index: number, kind: string | undefined): void {
    // A single-answer group cannot be emptied by clicking, so an absent value means a render rather than a
    // choice — and a kind this build does not know is not a kind.
    if (kind !== 'Do' && kind !== 'Dont') return;

    this.state.update((state) => ({
      ...state,
      rules: state.rules.map((rule, at) => (at === index ? { ...rule, kind } : rule)),
    }));

    this.scheduleKeep();
  }

  addRule(): void {
    this.state.update((state) => ({ ...state, rules: [...state.rules, { kind: 'Do', text: '' }] }));
    this.scheduleKeep();
  }

  removeRule(index: number): void {
    this.state.update((state) => ({ ...state, rules: state.rules.filter((_, at) => at !== index) }));
    this.scheduleKeep();
  }

  /** Moves a rule within the list. Order is the creator's, and the comparison reports it as a move. */
  moveRule(index: number, by: -1 | 1): void {
    const to = index + by;

    this.state.update((state) => {
      if (to < 0 || to >= state.rules.length) return state;

      const rules = [...state.rules];
      [rules[index], rules[to]] = [rules[to], rules[index]];

      return { ...state, rules };
    });

    this.scheduleKeep();
  }

  /** Stops citing one example version. The example itself stays in the library. */
  uncite(citation: BrandStyleGuideCitation): void {
    this.state.update((state) => ({
      ...state,
      citations: state.citations.filter(
        (each) => !(each.documentId === citation.documentId && each.versionNumber === citation.versionNumber),
      ),
    }));

    void this.loadCitationTitles();
    this.scheduleKeep();
  }

  /**
   * Re-pins a citation to the example's current version — the ordinary fix for a stale one.
   *
   * One uncite and one cite of the same document, which is the only shape in which the creator has said they
   * meant to move it: the server never re-points a citation on its own.
   */
  repin(view: CitationView): void {
    if (view.currentVersionNumber === null) return;

    this.state.update((state) => ({
      ...state,
      citations: [
        ...state.citations.filter(
          (each) => !(each.documentId === view.documentId && each.versionNumber === view.versionNumber),
        ),
        cite(view.documentId, view.currentVersionNumber!),
      ],
    }));

    void this.loadCitationTitles();
    this.scheduleKeep();
  }

  setChangeReason(changeReason: string): void {
    this.state.update((state) => ({ ...state, changeReason }));
    this.scheduleKeep();
  }

  // ---- Keeping it (autosave) ----

  /**
   * Starts the clock on an autosave, restarting it on every further keystroke.
   *
   * Debounced rather than per-change: a creator typing a section would otherwise send a request per
   * character, and each one would have to quote the token the last reply carried.
   */
  private scheduleKeep(): void {
    if (this.isArchived() || this.keepStateSignal().status === 'conflict') return;

    this.cancelKeepTimer();
    this.keepTimer = setTimeout(() => void this.keepNow(), this.keepDelayMs);
  }

  /** Keeps what is in the form now. Called by the debounce and by `Keep now`. */
  async keepNow(): Promise<void> {
    this.cancelKeepTimer();

    if (this.keeping || this.isArchived() || this.loadStateSignal().status !== 'ready') return;
    if (this.keepStateSignal().status === 'conflict') return;

    this.keeping = true;
    this.keepStateSignal.set({ status: 'keeping' });

    const outcome = await this.guides.saveEditSession(
      this.workspaceSlug,
      this.guideId,
      this.baseVersionSignal(),
      composeDraft(this.change(), this.baseVersionSignal()),
      this.draftRowVersion,
    );

    this.keeping = false;

    switch (outcome.status) {
      case 'kept':
        this.draftRowVersion = outcome.session.rowVersion;
        this.keepStateSignal.set({ status: 'kept', at: outcome.session.updatedUtc });
        this.resumedStaleSignal.set(outcome.session.isStale);
        return;
      case 'conflict':
        // Another tab, or a version written from this guide. Autosave stops rather than looping, because
        // every further attempt would quote the same spent token.
        this.keepStateSignal.set({ status: 'conflict' });
        return;
      default:
        this.keepStateSignal.set({ status: 'failed' });
        return;
    }
  }

  /** Throws away the kept draft and starts again from the stored version. */
  async discardDraft(): Promise<void> {
    const confirmed = await this.confirmService.confirm({
      title: 'Discard your unsaved changes?',
      message:
        'The form goes back to version ' +
        this.baseVersionSignal() +
        ' as it is saved. Everything you have typed since is lost, and no version of this guide is changed.',
      confirmLabel: 'Discard them',
      cancelLabel: 'Keep editing',
      tone: 'danger',
    });

    if (!confirmed) return;

    this.cancelKeepTimer();
    await this.guides.discardEditSession(this.workspaceSlug, this.guideId);
    this.draftRowVersion = null;
    await this.load();
  }

  /** Takes the copy the server holds, which is what resolves a two-tab conflict. */
  async useKeptCopy(): Promise<void> {
    this.cancelKeepTimer();
    await this.load();
  }

  // ---- Saving a version ----

  async save(): Promise<void> {
    if (this.saveStateSignal().status === 'saving' || this.isArchived()) return;

    this.cancelKeepTimer();

    const request = composeSaveRequest(this.change(), this.baseVersionSignal());

    if (request === null) {
      // Nothing differs from the stored version, so there is nothing to send. Answered here rather than by
      // the server purely so a creator is not made to wait for a round trip to be told so.
      this.saveStateSignal.set({ status: 'unchanged' });

      return;
    }

    this.saveStateSignal.set({ status: 'saving' });

    const outcome = await this.guides.saveVersion(
      this.workspaceSlug,
      this.guideId,
      request,
      this.idempotencyKeyFor(request),
    );

    switch (outcome.status) {
      case 'saved': {
        const written: SaveState =
          outcome.result.versionNumber === null
            ? { status: 'unchanged' }
            : { status: 'saved', versionNumber: outcome.result.versionNumber };

        if (outcome.result.versionNumber !== null) {
          this.pendingIdempotencyKey = null;
          this.pendingRequestSignature = null;
        }

        // The guide has moved on, the draft is cleared server-side, and the form's baseline is now the version
        // just written. Re-reading is what makes all three true at once — and the outcome is set after it,
        // because a read resets this line and the creator still has to be told what their save did.
        await this.load();
        this.saveStateSignal.set(written);

        return;
      }
      case 'rebase_needed':
        this.saveStateSignal.set({
          status: 'rebase_needed',
          workingVersionNumber: outcome.workingVersionNumber,
        });
        return;
      case 'unusable_sources':
        this.saveStateSignal.set({ status: 'unusable_sources', sources: outcome.sources });
        void this.loadCitationTitles();
        return;
      default:
        this.saveStateSignal.set({ status: outcome.status });
        return;
    }
  }

  /**
   * Re-reads the guide and keeps the creator's edit, now built on the version somebody else wrote.
   *
   * Safe because a submitted change names only the parts it touches: their sections land on top of the newer
   * version and everything they did not touch is whatever that version says. It is still their decision, which
   * is why it is a button and not something the save does quietly.
   */
  async rebase(): Promise<void> {
    const mine = this.change();
    await this.load();

    const guide = this.guide();
    if (guide === null) return;

    // Their named parts over the version that won. Everything they did not touch is whatever that version
    // says, which is the whole reason a change rather than a form is what the editor holds.
    this.state.set(applyChange(guide.workingVersion, mine));
    this.saveStateSignal.set({ status: 'idle' });
    this.scheduleKeep();
  }

  // ---- Navigation ----

  /** What the route guard asks before leaving: anything typed and not yet part of the guide. */
  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  jump(item: CpAnchorNavItem): void {
    const target = document.getElementById(item.targetId);
    if (!target) return;

    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
    target.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
    target.focus();
  }

  private revealPart(anchorId: string): void {
    afterNextRender(
      () => {
        const target = this.elementRef.nativeElement.querySelector<HTMLElement>(`#${CSS.escape(anchorId)}`);
        target?.focus();
        target?.scrollIntoView({ block: 'nearest' });
      },
      { injector: this.injector },
    );
  }

  private cancelKeepTimer(): void {
    if (this.keepTimer !== null) {
      clearTimeout(this.keepTimer);
      this.keepTimer = null;
    }
  }

  /** On the way out: send what the debounce has not sent yet rather than losing the last few keystrokes. */
  private cancelPendingKeep(): void {
    if (this.keepTimer === null) return;

    this.cancelKeepTimer();
    void this.keepNow();
  }

  /** One logical save keeps one key across its retries; a changed request is a new operation. */
  private idempotencyKeyFor(request: BrandStyleGuideVersionSaveRequest): string {
    const signature = JSON.stringify(request);

    if (this.pendingIdempotencyKey !== null && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }

    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;

    return key;
  }

  private resolveRouteParam(name: string): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const value = node.snapshot.paramMap.get(name);
      if (value) return value;
    }

    throw new Error(`BrandGuideEditorComponent route is missing a ${name} segment.`);
  }
}
