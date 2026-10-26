import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import {
  CpButtonComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpGeneratedLabelComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  LONG_FORM_HEADING_MAX_CHARACTERS,
  LONG_FORM_INTENT_MAX_CHARACTERS,
  LONG_FORM_ROLE_LABELS,
  LongFormDraft,
  LongFormKind,
  LongFormPlanSectionInput,
  LongFormSectionRole,
  longFormFindingText,
  longFormPlanIsOpen,
  longFormRolesAvailable,
} from '../../models/long-form-draft.models';
import { AiAllowanceState } from '../../services/ai-usage.service';
import { LongFormDraftService, LongFormPlanWriteOutcome } from '../../services/long-form-draft.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../shared/allowance-gate';
import { AiOperationTracker } from '../ai/ai-operation-tracker';

/** One section of the plan as the creator is editing it. */
interface WorkingSection {
  /** Stable on this screen for as long as the section is on it, so focus and errors follow a moved section. */
  readonly key: string;
  /** The server's id, or null for a section added here and not saved yet. */
  readonly id: string | null;
  readonly role: LongFormSectionRole;
  readonly heading: string;
  readonly intent: string;
  readonly sourceReferenceId: string | null;
}

type LoadState = 'loading' | 'ready' | 'not_found' | 'forbidden' | 'unavailable';

type Busy = 'idle' | 'saving' | 'approving' | 'asking';

/** What went wrong with the last write, and whether reloading is the way on. */
interface WriteProblem {
  readonly text: string;
  readonly reload: boolean;
}

/**
 * The plan-review surface for one long-form draft (LF.2.3, baseline B-33): the creator reads the proposed
 * sections, renames them, reorders them, removes one, adds one, and approves the plan. Nothing is written until
 * they do.
 *
 * **Where it sits.** Not a page. It belongs in a flow page's step card — the long-form pipeline's "Outline"
 * step (LF.6.2) — and renders that card's `cp-form-section`s, its save status and its own actions. The card's
 * heading, the legend and Back / Continue are the shell's.
 *
 * **A working copy over a baseline.** The plan is edited here and nowhere else until it is saved: a failed
 * save, a refusal and a plan that changed elsewhere all leave the creator's edits exactly as they were.
 * `hasUnsavedWork` is the difference between the two, and {@link confirmDiscardIfDirty} is what a route guard
 * asks before navigating away.
 *
 * **It judges nothing.** Whether a plan is ready is the server's answer — `planCheck` on a saved plan, and
 * the refusal of an approval. The role list it offers when adding a section is a convenience from a mirrored
 * table; the server still decides.
 *
 * **Nothing is approved for the creator.** Approval is one explicit press. If there are unsaved edits it
 * saves them first, and then approves the revision that save produced.
 */
@Component({
  selector: 'cp-long-form-plan-review',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    AiAllowanceNoticeComponent,
    CpButtonComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpGeneratedLabelComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './long-form-plan-review.component.html',
  styleUrl: './long-form-plan-review.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LongFormPlanReviewComponent {
  private readonly service = inject(LongFormDraftService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();
  readonly draftId = input.required<string>();

  /** The outline request this draft is waiting on, when the shell has just made one. */
  readonly outlineRequestId = input<string | null>(null);
  readonly allowance = input.required<AiAllowanceState>();
  readonly idPrefix = input('cp-long-form-plan');

  /** The draft, once its plan has been approved. Emitted on the approval and not before. */
  readonly approved = output<LongFormDraft>();

  protected readonly tracker = new AiOperationTracker((requestId) =>
    this.service.watchOutline(this.workspaceSlug(), this.draftId(), requestId),
  );

  protected readonly load = signal<LoadState>('loading');
  protected readonly draft = signal<LongFormDraft | null>(null);

  /** The last saved plan. A signal, so "unsaved" is recomputed the moment a save lands. */
  private readonly baseline = signal<readonly WorkingSection[]>([]);
  protected readonly working = signal<readonly WorkingSection[]>([]);

  protected readonly busy = signal<Busy>('idle');
  protected readonly problem = signal<WriteProblem | null>(null);

  /** What the server said about each section when it refused a plan, by the section's key. */
  private readonly refusals = signal<Readonly<Record<string, readonly string[]>>>({});

  /** The role chosen in "Add a section". Empty until the creator picks one. */
  protected readonly roleToAdd = signal<LongFormSectionRole | ''>('');

  /** Said to a screen reader through one polite live region: a move, a removal, a save, an approval. */
  protected readonly announcement = signal('');

  /** True once the plan on screen has just been approved by this creator, here. */
  protected readonly justApproved = signal(false);

  private followedKey: string | null = null;
  private gone = false;
  private nextKey = 0;

  protected readonly roleLabels = LONG_FORM_ROLE_LABELS;
  protected readonly headingHint = `Up to ${LONG_FORM_HEADING_MAX_CHARACTERS} characters.`;
  protected readonly intentHint = `One line, up to ${LONG_FORM_INTENT_MAX_CHARACTERS} characters. It guides the writing and is not published.`;

  readonly hasUnsavedWork = computed(() => !samePlan(this.working(), this.baseline()));

  protected readonly kind = computed<LongFormKind>(() => this.draft()?.kind ?? 'BlogArticle');

  protected readonly hasPlan = computed(() => this.draft()?.latest != null);

  /** True once sections are being written: the plan is shown and can no longer be changed. */
  protected readonly closed = computed(() => {
    const draft = this.draft();
    return draft !== null && !longFormPlanIsOpen(draft);
  });

  protected readonly readOnly = computed(() => this.closed() || this.busy() !== 'idle');

  protected readonly planning = computed(() => this.tracker.busy() || this.busy() === 'asking');

  /**
   * True when the poll ended with the request still unfinished. The tracker's own `stopped` is also set by a
   * poll that ended because the plan arrived, which is not something to offer "Check again" for.
   */
  protected readonly gaveUpChecking = computed(
    () => this.tracker.stopped() && this.tracker.phase().kind === 'watching',
  );

  protected readonly blocked = computed(() => allowanceBlocksNewRequests(this.allowance()));

  protected readonly refusal = computed(() => {
    const phase = this.tracker.phase();
    return phase.kind === 'blocked' ? phase.refusal : null;
  });

  /** Why the last outline request produced no plan, in the creator's terms; empty when it did, or is running. */
  protected readonly outlineProblem = computed(() => {
    const phase = this.tracker.phase();

    switch (phase.kind) {
      case 'failed':
        return phase.operation.failureCategory === 'Cancelled'
          ? 'Planning was cancelled before it finished. Nothing was changed.'
          : 'A plan could not be made this time. Nothing was changed.';
      case 'refused':
        return phase.message;
      case 'not_enabled':
        return 'Planning a piece is switched off for this workspace.';
      case 'forbidden':
        return 'You need to be a contributor to ask for a plan.';
      case 'key_reused':
        return 'That request was already used for something else. Ask again.';
      case 'gone':
        return 'That request is no longer here. Ask again.';
      case 'unavailable':
        return 'We couldn’t reach the planner. Check your connection and try again.';
      default:
        return '';
    }
  });

  /** Whether the plan on screen is still exactly what was suggested, so it is marked as such. */
  protected readonly isSuggestion = computed(
    () => this.draft()?.latest?.source === 'AiGenerated' && !this.hasUnsavedWork(),
  );

  protected readonly statusPill = computed<{ readonly tone: CpStatusPillTone; readonly word: string }>(() => {
    if (this.hasUnsavedWork()) return { tone: 'warning', word: 'Unsaved changes' };
    if (this.draft()?.status === 'Outlined') return { tone: 'success', word: 'Approved' };
    if (this.closed()) return { tone: 'neutral', word: 'Being written' };

    return { tone: 'progress', word: 'Not approved yet' };
  });

  /** The roles this plan still has room for. Empty when it has every section its kind allows. */
  protected readonly rolesAvailable = computed(() =>
    longFormRolesAvailable(
      this.kind(),
      this.working().map((section) => section.role),
    ),
  );

  /**
   * What stands between each section and approval: the server's check of the saved plan, until the creator
   * edits that section; then whatever an approval's refusal said about it.
   */
  protected readonly sectionProblems = computed(() => {
    const check = this.draft()?.planCheck ?? null;
    const saved = new Map(this.baseline().map((section) => [section.key, section]));
    const refusals = this.refusals();
    const problems: Record<string, SectionProblems> = {};

    for (const section of this.working()) {
      const fromRefusal = refusals[section.key] ?? [];
      const unchanged = saved.get(section.key);
      const stillAsSaved =
        unchanged !== undefined &&
        unchanged.heading === section.heading &&
        unchanged.intent === section.intent &&
        section.id !== null;

      const codes =
        stillAsSaved && check !== null
          ? check.findings.filter((finding) => finding.sectionId === section.id).map((finding) => finding.code)
          : [];

      problems[section.key] = {
        heading: codes.filter((code) => code.includes('.heading.')).map(longFormFindingText).join(' '),
        intent: codes.filter((code) => code.includes('.intent.')).map(longFormFindingText).join(' '),
        section: [
          ...codes
            .filter((code) => !code.includes('.heading.') && !code.includes('.intent.'))
            .map(longFormFindingText),
          ...fromRefusal,
        ].join(' '),
      };
    }

    return problems;
  });

  /** What the server's check says about the plan as a whole, not about one section of it. */
  protected readonly planProblems = computed(() => {
    if (this.hasUnsavedWork()) return [];

    const codes = (this.draft()?.planCheck?.findings ?? [])
      .filter((finding) => finding.sectionId === null)
      .map((finding) => finding.code);

    return [...new Set(codes)].map(longFormFindingText);
  });

  /** Whether asking again could help: not when the allowance, a switch or the creator's role is what refused. */
  protected readonly canAskAgain = computed(() => {
    const kind = this.tracker.phase().kind;
    return !this.blocked() && kind !== 'not_enabled' && kind !== 'forbidden';
  });

  protected readonly canSave = computed(() => this.busy() === 'idle' && !this.closed() && this.hasUnsavedWork());

  /** An approved plan with nothing changed has nothing left to approve. */
  protected readonly canApprove = computed(
    () =>
      this.busy() === 'idle' &&
      !this.closed() &&
      this.working().length > 0 &&
      (this.hasUnsavedWork() || this.draft()?.status !== 'Outlined'),
  );

  /** How many things the saved plan needs before it can be approved. Zero while there are unsaved edits. */
  protected readonly blockingCount = computed(() => {
    if (this.hasUnsavedWork()) return 0;

    return this.draft()?.planCheck?.findings.length ?? 0;
  });

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.gone = true;
      this.tracker.destroy();
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
    // rather than rebuilt cannot show one workspace's plan under another.
    effect(() => {
      const key = `${this.workspaceSlug()}|${this.draftId()}`;
      if (key === this.followedKey) return;

      this.followedKey = key;
      untracked(() => void this.read());
    });

    // An outline request the shell has just made, picked up — and picked back up after a refresh.
    effect(() => {
      const requestId = this.outlineRequestId();
      if (requestId === null || this.tracker.requestId() === requestId) return;

      untracked(() => this.tracker.resume(requestId));
    });

    // The plan a finished request wrote is on the server, so the draft is read again rather than assembled
    // here. Announced too: a creator who looked away is told the plan has arrived.
    effect(() => {
      if (this.tracker.phase().kind !== 'ready') return;

      untracked(() => {
        // Never over unsaved edits: a plan that landed while the creator was typing is shown as a change
        // made elsewhere, and they choose when to take it.
        if (this.hasUnsavedWork()) {
          this.problem.set({
            text: 'A new plan has arrived. Reload to see it — the changes you have not saved will be lost.',
            reload: true,
          });
          return;
        }

        void this.read().then(() => this.announce('Your plan is ready to review.'));
      });
    });
  }

  /**
   * Asked before leaving: true to go, false to stay. Unsaved edits are confirmed away, never dropped silently.
   */
  confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.hasUnsavedWork()) return Promise.resolve(true);

    return this.confirm.confirm({
      title: 'Leave without saving?',
      message: 'The changes you made to this plan have not been saved and will be lost.',
      confirmLabel: 'Leave and lose changes',
      cancelLabel: 'Stay',
      tone: 'danger',
    });
  }

  protected sectionId(section: WorkingSection, part: string): string {
    return `${this.idPrefix()}-${section.key}-${part}`;
  }

  protected sectionHeading(index: number, section: WorkingSection): string {
    return `Section ${index + 1} of ${this.working().length}: ${LONG_FORM_ROLE_LABELS[section.role]}`;
  }

  protected onHeading(section: WorkingSection, event: Event): void {
    this.change(section, { heading: (event.target as HTMLInputElement).value });
  }

  protected onIntent(section: WorkingSection, event: Event): void {
    this.change(section, { intent: (event.target as HTMLInputElement).value });
  }

  protected onRoleChosen(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.roleToAdd.set(this.rolesAvailable().find((role) => role === value) ?? '');
  }

  /** Moves a section one place. Announced, and focus stays with the section that moved. */
  protected move(section: WorkingSection, by: -1 | 1): void {
    if (this.readOnly()) return;

    const sections = [...this.working()];
    const from = sections.findIndex((candidate) => candidate.key === section.key);
    const to = from + by;
    if (from < 0 || to < 0 || to >= sections.length) return;

    sections.splice(from, 1);
    sections.splice(to, 0, section);
    this.working.set(sections);
    this.justApproved.set(false);
    this.announce(`Moved ${this.nameOf(section)} to position ${to + 1} of ${sections.length}.`);

    // The button just pressed is disabled once its section reaches an end, so focus goes to its twin there.
    const atEnd = by === -1 ? to === 0 : to === sections.length - 1;
    const pressed = by === -1 ? 'up' : 'down';
    const twin = by === -1 ? 'down' : 'up';
    this.focusAfterRender(this.sectionId(section, atEnd ? twin : pressed));
  }

  protected remove(section: WorkingSection): void {
    if (this.readOnly() || section.role === 'Item') return;

    const sections = this.working();
    const index = sections.findIndex((candidate) => candidate.key === section.key);
    if (index < 0) return;

    const remaining = sections.filter((candidate) => candidate.key !== section.key);
    this.working.set(remaining);
    this.justApproved.set(false);
    this.announce(`Removed ${this.nameOf(section)}. It is not gone until you save.`);

    // Focus goes to the section that took its place, or the last one, or the add control when none is left.
    const neighbour = remaining[Math.min(index, remaining.length - 1)];
    this.focusAfterRender(
      neighbour ? this.sectionId(neighbour, 'heading') : `${this.idPrefix()}-add-role`,
    );
  }

  protected add(): void {
    const role = this.roleToAdd();
    if (this.readOnly() || role === '' || !this.rolesAvailable().includes(role)) return;

    const section: WorkingSection = {
      key: this.mintKey(),
      id: null,
      role,
      heading: '',
      intent: '',
      sourceReferenceId: null,
    };

    this.working.set([...this.working(), section]);
    this.roleToAdd.set('');
    this.justApproved.set(false);
    this.announce(`Added a ${LONG_FORM_ROLE_LABELS[role]} section at the end. Give it a heading.`);
    this.focusAfterRender(this.sectionId(section, 'heading'));
  }

  protected async discard(): Promise<void> {
    if (!this.canSave()) return;

    const confirmed = await this.confirm.confirm({
      title: 'Discard your changes?',
      message: 'The plan will go back to how it was when you last saved it.',
      confirmLabel: 'Discard changes',
      cancelLabel: 'Keep editing',
      tone: 'danger',
    });
    if (!confirmed || this.gone) return;

    this.working.set(this.baseline());
    this.refusals.set({});
    this.problem.set(null);
    this.announce('Your changes were discarded.');
  }

  /** Saves the plan as it stands, without approving it. */
  protected async save(): Promise<void> {
    if (!this.canSave()) return;

    this.busy.set('saving');
    const saved = await this.send();
    if (this.gone) return;

    this.busy.set('idle');
    if (saved) this.announce('Your plan is saved.');
  }

  /**
   * Approves the plan. One explicit press: any unsaved edits are saved first, and the revision that save
   * produced is the one approved.
   */
  protected async approve(): Promise<void> {
    if (!this.canApprove()) return;

    this.busy.set('approving');

    if (this.hasUnsavedWork() && !(await this.send())) {
      if (!this.gone) this.busy.set('idle');
      return;
    }

    const draft = this.draft();
    if (this.gone || draft === null || draft.latest === null) {
      if (!this.gone) this.busy.set('idle');
      return;
    }

    const sent = this.working();
    const outcome = await this.service.approvePlan(this.workspaceSlug(), draft.id, {
      expectedConcurrencyToken: draft.concurrencyToken,
      revisionNumber: draft.latest.revisionNumber,
    });
    if (this.gone) return;

    this.busy.set('idle');

    if (this.apply(outcome, sent)) {
      this.justApproved.set(true);
      this.announce('Plan approved. Nothing has been written yet.');
      this.approved.emit(this.draft()!);
    }
  }

  /** Asks for a plan: the first one for a draft that has none, or another after one failed. */
  protected async ask(): Promise<void> {
    if (this.busy() !== 'idle' || this.tracker.busy() || this.blocked()) return;

    this.busy.set('asking');
    this.problem.set(null);

    const slug = this.workspaceSlug();
    const draftId = this.draftId();
    const key = crypto.randomUUID();

    await this.tracker.submit(() => this.service.requestOutline(slug, draftId, {}, key));
    if (!this.gone) this.busy.set('idle');
  }

  /** Reloads the draft, replacing whatever is on screen. Unsaved edits are confirmed away first. */
  protected async reload(): Promise<void> {
    if (this.hasUnsavedWork()) {
      const confirmed = await this.confirm.confirm({
        title: 'Reload this plan?',
        message: 'The changes you have not saved will be lost.',
        confirmLabel: 'Reload and lose changes',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      });
      if (!confirmed || this.gone) return;
    }

    await this.read();
  }

  /** Starts checking again on a request this screen had stopped asking about. */
  protected recheck(): void {
    const requestId = this.tracker.requestId();
    if (requestId !== null) this.tracker.resume(requestId);
  }

  protected retryRead(): void {
    void this.read();
  }

  private async read(): Promise<void> {
    const slug = this.workspaceSlug();
    const draftId = this.draftId();

    this.load.set('loading');
    const outcome = await this.service.read(slug, draftId);

    // The inputs moved on while this was in flight, or the screen has gone: the answer is for another draft.
    if (this.gone || slug !== this.workspaceSlug() || draftId !== this.draftId()) return;

    if (outcome.status !== 'found') {
      this.load.set(outcome.status);
      return;
    }

    this.adopt(outcome.draft);
    this.problem.set(null);
    this.justApproved.set(false);
    this.load.set('ready');
  }

  /** Sends the working plan. True when it was saved; otherwise the reason is on screen and the edits stay. */
  private async send(): Promise<boolean> {
    const draft = this.draft();
    if (draft === null) return false;

    const sent = this.working();
    const outcome = await this.service.patchPlan(this.workspaceSlug(), draft.id, {
      expectedConcurrencyToken: draft.concurrencyToken,
      sections: sent.map(toInput),
    });
    if (this.gone) return false;

    return this.apply(outcome, sent);
  }

  /**
   * Takes one write's outcome. On a save the server's plan becomes the baseline; on anything else the
   * creator's edits are left exactly as they were, and the reason is said.
   */
  private apply(outcome: LongFormPlanWriteOutcome, sent: readonly WorkingSection[]): boolean {
    switch (outcome.status) {
      case 'saved':
        this.adopt(outcome.draft, sent);
        this.problem.set(null);
        return true;

      case 'stale':
        this.problem.set({
          text: 'This plan was changed somewhere else since you opened it. Reload to see it — your changes here will be lost.',
          reload: true,
        });
        return false;

      case 'locked':
      case 'not_started':
        this.problem.set({ text: outcome.message, reload: true });
        return false;

      case 'refused': {
        const bySection = sectionRefusals(outcome.fieldErrors, sent);
        this.refusals.set(bySection.sections);
        this.problem.set({
          text: bySection.general || 'This plan is not ready yet. See what each section needs below.',
          reload: false,
        });

        // The first section the server named, so the creator lands on the thing to fix.
        const first = sent.find((section) => bySection.sections[section.key] !== undefined);
        if (first) this.focusAfterRender(this.sectionId(first, 'heading'));
        return false;
      }

      case 'not_found':
        this.problem.set({ text: 'This draft is no longer here.', reload: false });
        return false;

      case 'forbidden':
        this.problem.set({ text: 'You need to be a contributor to change this plan.', reload: false });
        return false;

      default:
        this.problem.set({
          text: 'We couldn’t save that. Your changes are still here — check your connection and try again.',
          reload: false,
        });
        return false;
    }
  }

  /**
   * Makes the server's draft the one on screen. Keys carry over from what was sent, position by position, so a
   * section the creator was looking at keeps its place, its focus and its ids across a save.
   */
  private adopt(draft: LongFormDraft, sent: readonly WorkingSection[] = []): void {
    const sections = (draft.latest?.sections ?? []).map(
      (section, index): WorkingSection => ({
        key: sent[index]?.key ?? this.mintKey(),
        id: section.id,
        role: section.role,
        heading: section.heading,
        intent: section.intent,
        sourceReferenceId: section.sourceReferenceId,
      }),
    );

    this.draft.set(draft);
    this.baseline.set(sections);
    this.working.set(sections);
    this.refusals.set({});
  }

  private change(section: WorkingSection, patch: Partial<Pick<WorkingSection, 'heading' | 'intent'>>): void {
    this.working.set(
      this.working().map((candidate) => (candidate.key === section.key ? { ...candidate, ...patch } : candidate)),
    );
    this.justApproved.set(false);

    // What the server said about this section was about the words it was sent, which these no longer are.
    if (this.refusals()[section.key] !== undefined) {
      this.refusals.set(
        Object.fromEntries(Object.entries(this.refusals()).filter(([key]) => key !== section.key)),
      );
    }
  }

  protected sectionName(section: WorkingSection): string {
    return this.nameOf(section);
  }

  private nameOf(section: WorkingSection): string {
    const heading = section.heading.trim();
    return heading === '' ? `the ${LONG_FORM_ROLE_LABELS[section.role]} section` : `“${heading}”`;
  }

  private announce(text: string): void {
    this.announcement.set(text);
  }

  private mintKey(): string {
    this.nextKey += 1;
    return `s${this.nextKey}`;
  }

  /** Focus, once the move or the new section has been rendered. Found in the document, not in this host. */
  private focusAfterRender(id: string): void {
    afterNextRender(
      () => {
        if (!this.gone) document.getElementById(id)?.focus();
      },
      { injector: this.injector },
    );
  }
}

interface SectionProblems {
  readonly heading: string;
  readonly intent: string;
  readonly section: string;
}

function toInput(section: WorkingSection): LongFormPlanSectionInput {
  return {
    id: section.id,
    role: section.role,
    heading: section.heading,
    intent: section.intent,
    sourceReferenceId: section.sourceReferenceId,
  };
}

/** Two plans are the same when they hold the same sections, saying the same things, in the same order. */
function samePlan(left: readonly WorkingSection[], right: readonly WorkingSection[]): boolean {
  return (
    left.length === right.length &&
    left.every((section, index) => {
      const other = right[index]!;
      return (
        section.id === other.id &&
        section.key === other.key &&
        section.role === other.role &&
        section.heading === other.heading &&
        section.intent === other.intent &&
        section.sourceReferenceId === other.sourceReferenceId
      );
    })
  );
}

/**
 * The server keys a refusal by a section's position in the plan it was sent — `sections[2]`,
 * `sections[2].id` — so each is put beside the section that was at that position when it was sent, wherever
 * that section is now. Anything not about one section is the plan's own.
 */
function sectionRefusals(
  fieldErrors: Readonly<Record<string, readonly string[]>>,
  sent: readonly WorkingSection[],
): { readonly sections: Record<string, readonly string[]>; readonly general: string } {
  const sections: Record<string, string[]> = {};
  const general: string[] = [];

  for (const [field, messages] of Object.entries(fieldErrors)) {
    const match = /^sections\[(\d+)\]/.exec(field);
    const section = match ? sent[Number(match[1])] : undefined;

    if (section === undefined) {
      general.push(...messages);
      continue;
    }

    (sections[section.key] ??= []).push(...messages);
  }

  return { sections, general: general.join(' ') };
}
