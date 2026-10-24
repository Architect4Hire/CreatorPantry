import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  CpBackLinkComponent,
  CpButtonComponent,
  CpCardComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpProgressComponent,
  CpSkeletonComponent,
  CpStep,
  CpStepperComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';
import { combineLatest, map } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { injectRouteTitle } from '../../core/route-title';
import { injectFlowNarrow } from '../../shared/workflow-shell/flow-narrow';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { linkedRecipeOf } from '../../models/creative-context.models';
import { LongFormAssembledDocument } from '../../models/long-form-document.models';
import { LongFormDraft, LongFormDraftStatus, LongFormKind } from '../../models/long-form-draft.models';
import {
  LONG_FORM_PIPELINE_STEPS,
  LONG_FORM_PIPELINE_STEP_COUNT,
  LONG_FORM_PIPELINE_STEP_SLUGS,
  LongFormPipelinePlace,
  LongFormPipelineStepSlug,
  canOpenLongFormPipelineStep,
  furthestLongFormPipelineStep,
  isLongFormPipelineStepSlug,
  longFormPipelineStepIndex,
  longFormPipelineStepStatus,
} from '../../models/long-form-pipeline.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { CreativeContextFiling, CreativeContextFilingStore, DeviceDraftOwner } from '../../services/device-draft-store';
import { LongFormDraftService } from '../../services/long-form-draft.service';
import { LongFormPipelinePlaceService } from '../../services/long-form-pipeline-place.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { CreativeContextSaveNoticeComponent } from '../../shared/creative-context-save-notice/creative-context-save-notice.component';
import { LongFormAboutStepComponent } from './long-form-about-step.component';
import { LongFormDocumentReviewComponent } from './long-form-document-review.component';
import { LongFormPlanReviewComponent } from './long-form-plan-review.component';
import { LongFormTeasersComponent } from './long-form-teasers.component';

type Phase =
  | 'loading'
  | 'unavailable'
  | 'not_found'
  | 'read_only'
  | 'piece_not_found'
  | 'piece_unavailable'
  | 'resumable'
  | 'editing';

/** Where the draft the address names stands with this screen. */
type DraftRead = 'none' | 'loading' | 'ready' | 'not_found' | 'unavailable';

export const LONG_FORM_PIPELINE_HEADING_ID = 'cp-long-form-step-heading';

/** The route segments of the pipeline, relative to `workflows`. */
export const LONG_FORM_PIPELINE_SEGMENT = 'long-form';

/**
 * A filing attempt held for the life of the page and nowhere else.
 *
 * The session asks where to remember the one request that creates a context, so a retry replays it. The
 * short-form pipeline keeps that on the device; here nothing but the step reached is the device's (LF.6.2), so
 * it is kept in memory. A reload between a lost reply and its retry can therefore file a second, empty piece
 * of work — the cost of the rule, and a small one.
 */
function memoryFiling(): CreativeContextFilingStore {
  let held: CreativeContextFiling | null = null;

  return {
    read: () => held,
    write: (filing) => (held = filing),
    clear: () => (held = null),
  };
}

/**
 * The long-form pipeline (LF.6.2, B-38): one guided journey from what a piece is about to an accepted article
 * or newsletter issue.
 *
 * **A flow page, and a shell.** It owns the page head, the progress, the step list and the one card a step
 * sits in. It writes none of the steps' content: the outline is `cp-long-form-plan-review`, and writing and
 * acceptance are the two halves of `cp-long-form-document-review`. Those surfaces are composed, not copied.
 *
 * **The journey's state is the server's.** What the piece is about is on its creative context; its plan, its
 * text and where it stands are on the draft. Which steps may be opened is worked out from the draft's status
 * each time it is read. This device keeps one thing through {@link LongFormPipelinePlaceService}: the step
 * the creator last had open, so the bare address can offer to pick the piece up.
 *
 * **Nothing is done for the creator.** Asking for an outline is a button that says it uses their allowance.
 * Approving the plan, writing, and accepting are each the composed surface's own action; this shell never
 * approves, accepts or publishes, and moving to a step changes nothing about the piece.
 *
 * **Unsaved text is never dropped silently.** Both surfaces hold text the creator has typed and not saved.
 * Every way out of a step — Back, Continue, Save and exit, the browser's own Back — asks the surface on screen
 * first ({@link confirmLeaveIfUnsaved}), through this shell's buttons or the route's guard.
 */
@Component({
  selector: 'cp-long-form-pipeline-shell',
  standalone: true,
  imports: [
    RouterLink,
    AiAllowanceNoticeComponent,
    CpBackLinkComponent,
    CpButtonComponent,
    CpCardComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpProgressComponent,
    CpSkeletonComponent,
    CpStepperComponent,
    CpTopicCardComponent,
    CreativeContextSaveNoticeComponent,
    LongFormAboutStepComponent,
    LongFormDocumentReviewComponent,
    LongFormPlanReviewComponent,
    LongFormTeasersComponent,
  ],
  providers: [CreativeContextSession],
  templateUrl: './long-form-pipeline-shell.component.html',
  styleUrl: './long-form-pipeline-shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LongFormPipelineShellComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly drafts = inject(LongFormDraftService);
  private readonly places = inject(LongFormPipelinePlaceService);
  private readonly usage = inject(AiUsageService);
  private readonly confirm = inject(ConfirmService);
  private readonly injector = inject(Injector);

  protected readonly session = inject(CreativeContextSession);

  protected readonly pageTitle = injectRouteTitle('Long-form');
  protected readonly headingId = LONG_FORM_PIPELINE_HEADING_ID;
  protected readonly total = LONG_FORM_PIPELINE_STEP_COUNT;
  /** True where the stepper folds into a disclosure, at the width a workflow run's does. */
  protected readonly narrow = injectFlowNarrow();
  protected readonly allowance = this.usage.allowance;

  readonly workspaceSlug = signal('');
  private readonly routeContextId = signal<string | null>(null);
  private readonly routeDraftId = signal<string | null>(null);
  private readonly routeStep = signal<string | null>(null);

  private readonly draft = signal<LongFormDraft | null>(null);
  private readonly draftRead = signal<DraftRead>('none');

  /**
   * Where the piece stands as the writing surface last read it. Writing a section moves a draft on without
   * this shell being told any other way, so the newer of the two answers is the one believed.
   */
  private readonly documentStatus = signal<LongFormDraftStatus | null>(null);

  private readonly opened = signal(false);
  private readonly resumeOffer = signal<LongFormPipelinePlace | null>(null);

  protected readonly kind = signal<LongFormKind>('BlogArticle');
  protected readonly targetWords = signal<number | null>(null);
  protected readonly outlineRequestId = signal<string | null>(null);
  protected readonly planning = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly refusal = signal<AiQuotaRefusal | null>(null);
  protected readonly announcement = signal('');

  private readonly plan = viewChild(LongFormPlanReviewComponent);
  private readonly review = viewChild(LongFormDocumentReviewComponent);

  private pendingKey: string | null = null;
  private openedKey = '';
  private openTicket = 0;

  /**
   * Whose piece is held: the workspace and member it was read for. Two workspaces can use the same ids, so
   * "this draft is already open" is only true for the owner it was opened under.
   */
  private heldFor = '';

  /** Set for a move this shell has already asked about, so the route's guard does not ask a second time. */
  private leaveSettled = false;

  protected readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  private readonly owner = computed<DeviceDraftOwner | null>(() => {
    const membership = this.membership();

    return membership === null ? null : { workspaceId: membership.workspaceId, membershipId: membership.membershipId };
  });

  protected readonly ownerKey = computed(() => {
    const owner = this.owner();

    return owner === null ? '' : `${owner.workspaceId}.${owner.membershipId}`;
  });

  protected readonly draftId = computed(() => this.draft()?.id ?? null);

  protected readonly contextId = computed(() => this.session.context()?.id ?? this.routeContextId());

  /** Where the piece stands: the surface's reading where there is one, the draft's otherwise. */
  private readonly status = computed<LongFormDraftStatus | null>(() => this.documentStatus() ?? this.draft()?.status ?? null);

  protected readonly furthest = computed(() => furthestLongFormPipelineStep(this.status()));

  protected readonly isAccepted = computed(() => this.status() === 'Accepted' || this.status() === 'NeedsReview');

  /**
   * The step on screen: the one the address names when the piece has reached it, and the furthest it has
   * reached otherwise. Derived, so the screen cannot show a step the server says is not open.
   */
  protected readonly current = computed<LongFormPipelineStepSlug>(() => {
    const asked = this.routeStep();
    const furthest = this.furthest();

    return isLongFormPipelineStepSlug(asked) && canOpenLongFormPipelineStep(asked, furthest) ? asked : asked === null ? 'about' : furthest;
  });

  protected readonly step = computed(() => LONG_FORM_PIPELINE_STEPS[longFormPipelineStepIndex(this.current())]!);

  protected readonly stepNumber = computed(() => longFormPipelineStepIndex(this.current()) + 1);

  /** The step list is the whole journey: all four, each with where it stands. */
  protected readonly stepperSteps = computed<readonly CpStep[]>(() =>
    LONG_FORM_PIPELINE_STEPS.map((step) => ({
      id: step.slug,
      label: step.label,
      status: longFormPipelineStepStatus(step.slug, this.current(), this.furthest(), this.isAccepted()),
    })),
  );

  /**
   * The progress figure is the truth about the journey: the step on screen out of four, and all four only
   * once the piece is accepted — being on the last step is not having finished it.
   */
  protected readonly percent = computed(() =>
    this.isAccepted() ? 100 : Math.round(((this.stepNumber() - 1) / this.total) * 100),
  );

  protected readonly progressText = computed(() =>
    this.isAccepted() ? 'Accepted' : `Step ${this.stepNumber()} of ${this.total}`,
  );

  protected readonly isFirst = computed(() => this.current() === 'about');
  protected readonly isLast = computed(() => this.current() === 'review');

  /** What the first step has to go on: a linked recipe, a title, or a few words. Any one is enough. */
  protected readonly hasSomethingToPlanFrom = computed(() => {
    const fields = this.session.fields();

    return (
      linkedRecipeOf(this.session.context()) !== null ||
      fields.workingTitle.trim() !== '' ||
      fields.pictureBrief.trim() !== ''
    );
  });

  /** Whether the step after this one is open. Never true on the last step. */
  protected readonly canContinue = computed(() => {
    const next = LONG_FORM_PIPELINE_STEP_SLUGS[longFormPipelineStepIndex(this.current()) + 1];

    return next !== undefined && canOpenLongFormPipelineStep(next, this.furthest());
  });

  /** What the step still needs before the next one opens, said above the buttons it is about. */
  protected readonly needed = computed<string | null>(() => {
    if (this.canContinue()) return null;

    switch (this.current()) {
      case 'about':
        return this.hasSomethingToPlanFrom()
          ? null
          : 'Link a recipe, or say what the piece is called or what it should say, to plan an outline.';
      case 'outline':
        return 'Approve the plan to start writing.';
      case 'write':
        return 'Write at least one section to read the piece whole.';
      default:
        return null;
    }
  });

  protected readonly resumeStep = computed(() => {
    const place = this.resumeOffer();

    return place === null ? null : LONG_FORM_PIPELINE_STEPS[longFormPipelineStepIndex(place.step)]!;
  });

  /**
   * Derived rather than set, so the screen cannot be left claiming a phase the data has moved past.
   *
   * An unknown workspace and one this creator is not in answer alike, and so do an unknown piece and another
   * workspace's: telling them apart would disclose that the other exists (.claude/rules/tenancy.md).
   */
  protected readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';

    const membership = this.membership();
    if (membership === null) return 'not_found';

    // A Viewer may read a piece, and is stopped here because every step of this journey writes one.
    if (membership.role === 'Viewer') return 'read_only';

    if (this.routeContextId() !== null) {
      const open = this.session.open();
      if (open === 'not_found') return 'piece_not_found';
      if (open === 'unavailable') return 'piece_unavailable';
    }

    if (this.draftRead() === 'not_found') return 'piece_not_found';
    if (this.draftRead() === 'unavailable') return 'piece_unavailable';
    if (!this.opened()) return 'loading';

    return this.resumeOffer() !== null ? 'resumable' : 'editing';
  });

  constructor() {
    void this.memberships.ensureLoaded();
    void this.usage.ensureLoaded();

    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    combineLatest(nodes.map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          const read = (key: string): string | null => {
            for (const params of maps) {
              const value = params.get(key);
              if (value) return value;
            }

            return null;
          };

          return { slug: read('workspaceSlug'), contextId: read('contextId'), draftId: read('draftId'), step: read('step') };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, contextId, draftId, step }) => {
        if (slug === null) throw new Error('LongFormPipelineShellComponent route is missing a workspaceSlug segment.');

        const stepChanged = step !== this.routeStep();

        this.workspaceSlug.set(slug);
        this.routeContextId.set(contextId);
        this.routeDraftId.set(draftId);
        this.routeStep.set(step);

        // Only for the piece already open here. An address naming another piece is read first, and settles
        // on its own step once it has been: deciding now would judge it by the piece it is replacing.
        const samePiece =
          this.heldFor === this.ownerKey() &&
          (this.draft()?.id ?? null) === draftId &&
          (this.session.context()?.id ?? null) === contextId;

        if (stepChanged && this.opened() && samePiece) this.arrive(true);
      });

    // The piece is read once the memberships say who is asking, and again when the workspace, the person or
    // the piece the address names changes.
    effect(() => {
      const owner = this.owner();
      const contextId = this.routeContextId();
      const draftId = this.routeDraftId();
      const key = `${this.ownerKey()}#${contextId ?? ''}#${draftId ?? ''}`;
      if (owner === null || key === this.openedKey) return;

      // A draft this shell has just started is already held; the address catching up is not a new piece.
      // Held for this owner: the same ids under another workspace or member are another piece entirely.
      if (
        untracked(
          () =>
            this.opened() &&
            this.heldFor === this.ownerKey() &&
            (this.draft()?.id ?? null) === draftId &&
            (this.session.context()?.id ?? null) === contextId,
        )
      ) {
        this.openedKey = key;
        return;
      }

      this.openedKey = key;
      untracked(() => void this.open(owner, contextId, draftId));
    });

    // The first words typed put the work on a context, so they are on the server and not only on this page.
    // Linking a recipe does the same through the picker. What is typed after that is saved as an edit.
    effect(() => {
      const fields = this.session.fields();
      if (this.session.open() !== 'ready' || this.session.context() !== null) return;
      if (fields.workingTitle.trim() === '' && fields.pictureBrief.trim() === '') return;

      untracked(() => void this.session.file());
    });

    // Work with no context gets one on its first answer. The address follows, in place.
    effect(() => {
      const filed = this.session.context()?.id ?? null;
      if (filed === null || untracked(() => this.routeContextId()) !== null) return;

      untracked(() => void this.go('about', true));
    });
  }

  /**
   * Asked before leaving the step on screen: true to go, false to stay.
   *
   * The surface on screen holds whatever is unsaved and is the one that asks. The first step holds nothing:
   * what is typed there is sent as it is typed.
   */
  async confirmLeaveIfUnsaved(): Promise<boolean> {
    if (this.leaveSettled) {
      this.leaveSettled = false;
      return true;
    }

    const surface = this.plan() ?? this.review();

    return surface === undefined ? true : surface.confirmDiscardIfDirty();
  }

  // ---- reading the piece ---------------------------------------------------------------------------------

  private async open(owner: DeviceDraftOwner, contextId: string | null, draftId: string | null): Promise<void> {
    const ticket = ++this.openTicket;
    const slug = this.workspaceSlug();

    // Everything held for the piece before, gone before the first thing about this one is read or sent.
    this.heldFor = `${owner.workspaceId}.${owner.membershipId}`;
    this.opened.set(false);
    this.draft.set(null);
    this.draftRead.set('none');
    this.documentStatus.set(null);
    this.resumeOffer.set(null);
    this.outlineRequestId.set(null);
    this.problem.set(null);
    this.refusal.set(null);
    this.planning.set(false);
    this.kind.set('BlogArticle');
    this.targetWords.set(null);
    this.pendingKey = null;

    if (contextId === null) {
      // The bare address: offer the piece this creator last had open here, or start a new one. Nothing is
      // created until they answer something.
      const start = this.startingPoint();
      const place = start === null ? this.places.read(owner).draft : null;

      this.session.begin(slug, memoryFiling());

      // Asked to start from a recipe or a brief (LF.6.3): the creator has just said this is a new piece, so
      // the last one is not offered in its place — and focus goes to where they said they would begin.
      this.resumeOffer.set(place);
      this.opened.set(true);
      if (start !== null) this.focusStart(start);
      return;
    }

    this.draftRead.set(draftId === null ? 'none' : 'loading');
    await this.session.load(slug, contextId);
    if (ticket !== this.openTicket || this.session.open() !== 'ready') return;

    const found = draftId === null ? await this.latestDraftOf(slug, contextId) : await this.readDraft(slug, draftId, contextId);
    if (ticket !== this.openTicket) return;

    if (found === 'not_found' || found === 'unavailable') {
      this.draftRead.set(found);
      return;
    }

    this.draft.set(found);
    this.draftRead.set(found === null ? 'none' : 'ready');
    if (found !== null) this.kind.set(found.kind);

    this.opened.set(true);
    this.arrive(false);
  }

  /** The draft the address names — which must be this piece of work's own. */
  private async readDraft(slug: string, draftId: string, contextId: string): Promise<LongFormDraft | 'not_found' | 'unavailable'> {
    const outcome = await this.drafts.read(slug, draftId);

    if (outcome.status === 'found') {
      // A real draft under the wrong piece of work is an address that names nothing.
      return outcome.draft.creativeContextId === contextId ? outcome.draft : 'not_found';
    }

    // A Viewer is stopped before this; "forbidden" here is an address the creator cannot read, said the same.
    return outcome.status === 'unavailable' ? 'unavailable' : 'not_found';
  }

  /**
   * The piece of work's most recent draft, when the address names only the work. The server's answer, so a
   * piece started on another device is picked up here; none means the first step.
   */
  private async latestDraftOf(slug: string, contextId: string): Promise<LongFormDraft | null | 'unavailable'> {
    const page = await this.drafts.list(slug, { creativeContextId: contextId, limit: 1 });
    if (page.status !== 'found') return page.status === 'unavailable' ? 'unavailable' : null;

    const latest = page.page.items[0];
    if (latest === undefined) return null;

    const read = await this.drafts.read(slug, latest.id);

    return read.status === 'found' ? read.draft : read.status === 'unavailable' ? 'unavailable' : null;
  }

  /**
   * Settle on the step on screen: correct the address when it names a step the piece has not reached, keep
   * the place, and — on a move — put focus on the step's heading and say where the creator is.
   */
  private arrive(moved: boolean): void {
    const owner = this.owner();
    const contextId = this.contextId();
    const current = this.current();

    // An address that names the piece and no step opens where the piece has got to.
    const target = this.routeStep() === null && contextId !== null ? this.furthest() : current;

    if (contextId !== null && (this.routeStep() !== target || this.routeDraftId() !== this.draftId())) {
      void this.go(target, true);
      return;
    }

    if (owner !== null && contextId !== null) {
      this.places.write(owner, { contextId, draftId: this.draftId(), step: current });
    }

    if (!moved) return;

    this.focusHeading();
    this.announcement.set(`${this.step().label}. Step ${this.stepNumber()} of ${this.total}.`);
  }

  // ---- the first step's one action ------------------------------------------------------------------------

  /**
   * Ask for an outline: the first thing in this journey that uses the creator's allowance, and so the first
   * thing that is a button they press rather than something that happens as they go.
   */
  protected async planOutline(): Promise<void> {
    if (this.planning() || !this.hasSomethingToPlanFrom()) return;

    const slug = this.workspaceSlug();

    // The piece this was asked for. An answer that arrives after the creator has opened another piece, or
    // another workspace, is for a piece no longer on screen and is dropped at every step.
    const ticket = this.openTicket;
    const stale = (): boolean => ticket !== this.openTicket || slug !== this.workspaceSlug();

    this.planning.set(true);
    this.problem.set(null);
    this.refusal.set(null);

    // What was typed reaches the server before the plan is written from it.
    await this.session.flush();
    if (stale()) return;

    const context = await this.session.file();
    if (stale()) return;

    if (context === null) {
      this.planning.set(false);
      this.problem.set('This piece could not be saved just now, so no outline was asked for. Try again.');
      return;
    }

    this.pendingKey ??= crypto.randomUUID();

    const words = this.targetWords();
    const outcome = await this.drafts.start(
      slug,
      { creativeContextId: context.id, kind: this.kind(), ...(words === null ? {} : { targetWords: words }) },
      this.pendingKey,
    );

    if (stale()) return;
    this.planning.set(false);

    switch (outcome.status) {
      case 'accepted':
        this.pendingKey = null;
        this.draft.set(outcome.draft);
        this.draftRead.set('ready');
        this.outlineRequestId.set(outcome.operation.aiProposalRequestId);
        await this.go('outline');
        return;
      case 'quota_exhausted':
      case 'account_suspended':
        this.pendingKey = null;
        this.refusal.set(outcome);
        return;
      case 'refused':
        this.pendingKey = null;
        this.problem.set(outcome.message);
        return;
      case 'task_not_enabled':
        this.pendingKey = null;
        this.problem.set('Planning a long-form piece is not switched on here.');
        return;
      case 'forbidden':
        this.pendingKey = null;
        this.problem.set('You do not have access to start a piece here.');
        return;
      case 'idempotency_key_conflict':
      case 'validation_failed':
        // Neither is something the creator did. A fresh key makes the next try a fresh request.
        this.pendingKey = null;
        this.problem.set('That outline could not be asked for. Try again.');
        return;
      default:
        // The key is kept: the request may have been taken, and the same key finds it again.
        this.problem.set('An outline could not be asked for just now. Nothing was used. Try again.');
    }
  }

  // ---- what the composed surfaces report ------------------------------------------------------------------

  protected onPlanApproved(draft: LongFormDraft): void {
    this.draft.set(draft);

    // The plan is approved and saved, so there is nothing unsaved to ask about.
    void this.go('write', false, true);
  }

  protected onDocumentRead(document: LongFormAssembledDocument): void {
    this.documentStatus.set(document.status);
  }

  protected onAccepted(document: LongFormAssembledDocument): void {
    this.documentStatus.set(document.status);
    this.announcement.set('This piece is accepted. Nothing has been published.');
  }

  // ---- moving --------------------------------------------------------------------------------------------

  protected async back(): Promise<void> {
    const previous = LONG_FORM_PIPELINE_STEP_SLUGS[longFormPipelineStepIndex(this.current()) - 1];

    if (previous !== undefined) await this.go(previous);
  }

  protected async continue(): Promise<void> {
    const next = LONG_FORM_PIPELINE_STEP_SLUGS[longFormPipelineStepIndex(this.current()) + 1];

    if (next === undefined) return;
    // Pressed before the next step is open, Continue stays put and says why: it is never disabled, so it keeps
    // its focus and the reason is heard rather than only seen above it.
    if (!this.canContinue()) {
      this.announcement.set(this.needed() ?? 'The next step is not open yet. Finish this one to carry on.');
      return;
    }

    await this.go(next);
  }

  protected async resume(): Promise<void> {
    const place = this.resumeOffer();
    if (place === null) return;

    this.resumeOffer.set(null);
    await this.router.navigate(this.address(place.contextId, place.draftId, place.step));
  }

  /** Leave the journey. Everything is already on the server, so this saves nothing and loses nothing. */
  protected async saveAndExit(): Promise<void> {
    if (!(await this.settleLeave())) return;

    await this.session.flush();
    await this.router.navigate(['/', this.workspaceSlug(), 'workflows']);
  }

  /**
   * Begin a new piece. The one this creator had open is not deleted — it is on the server and stays there —
   * so this asks only because it stops being the one offered here.
   */
  protected async startNew(): Promise<void> {
    const sure = await this.confirm.confirm({
      title: 'Start a new piece?',
      message: 'The piece you have open is kept as it is. This starts a new one from the first step.',
      confirmLabel: 'Start a new piece',
      cancelLabel: 'Stay with this one',
    });
    if (!sure || !(await this.settleLeave())) return;

    const owner = this.owner();
    if (owner !== null) this.places.clear(owner);

    this.resumeOffer.set(null);
    this.openedKey = '';
    await this.router.navigate(['/', this.workspaceSlug(), 'workflows', LONG_FORM_PIPELINE_SEGMENT]);
    if (owner !== null) await this.open(owner, null, null);
  }

  /** From the resume offer: not this one. Nothing is asked, because nothing is lost — the piece stays where it is. */
  protected declineResume(): void {
    const owner = this.owner();
    if (owner !== null) this.places.clear(owner);

    this.resumeOffer.set(null);
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }

  protected retryPiece(): void {
    const owner = this.owner();

    if (owner !== null) void this.open(owner, this.routeContextId(), this.routeDraftId());
  }

  protected startLink(): readonly string[] {
    return ['/', this.workspaceSlug(), 'workflows', LONG_FORM_PIPELINE_SEGMENT];
  }

  /**
   * Go to a step of this piece.
   *
   * @param replace An address being corrected or catching up, not a step the creator took: it replaces the
   * history entry and asks nothing.
   * @param settled Nothing can be unsaved — the surface has just saved it.
   */
  private async go(step: LongFormPipelineStepSlug, replace = false, settled = false): Promise<void> {
    const contextId = this.contextId();
    if (contextId === null) return;

    if (!replace && !settled && !(await this.settleLeave())) return;
    if (replace || settled) this.leaveSettled = true;

    const moved = await this.router.navigate(this.address(contextId, this.draftId(), step), { replaceUrl: replace });

    // The router did not move — the address was already this one — so the guard never ran to clear this.
    if (!moved) this.leaveSettled = false;
  }

  /** Ask the surface on screen once, and remember the answer for the guard the navigation then meets. */
  private async settleLeave(): Promise<boolean> {
    const leave = await this.confirmLeaveIfUnsaved();
    if (leave) this.leaveSettled = true;

    return leave;
  }

  private address(contextId: string, draftId: string | null, step: LongFormPipelineStepSlug): readonly string[] {
    const base = ['/', this.workspaceSlug(), 'workflows', LONG_FORM_PIPELINE_SEGMENT, 'context', contextId];

    return draftId === null ? [...base, step] : [...base, 'draft', draftId, step];
  }

  /**
   * Where the Workflows Hub said this piece starts: `?from=recipe` or `?from=brief`. Anything else is no
   * starting point at all. It chooses where focus lands and nothing more — arriving creates nothing.
   *
   * A deliberate exception to "focus goes to the heading when a page replaces what was there"
   * (DESIGN-SYSTEM.md): the creator has just pressed a button naming the field they mean to begin in, so
   * that field is where they are put. An arrival with no starting point leaves focus alone, as before.
   */
  /**
   * The recipe the link that opened the writer offers, from `?recipe=` — a finished guided run's recipe, say (13A.18).
   * Only an id is taken, and it is only offered: the recipe picker shows it as one button, and it is linked when the
   * creator presses it, never on arrival.
   */
  protected readonly suggestedRecipe = toSignal(this.route.queryParamMap.pipe(map((query) => idFrom(query.get('recipe')))), {
    initialValue: idFrom(this.route.snapshot.queryParamMap.get('recipe')),
  });

  private startingPoint(): 'recipe' | 'brief' | null {
    const from = this.route.snapshot.queryParamMap.get('from');

    return from === 'recipe' || from === 'brief' ? from : null;
  }

  private focusStart(start: 'recipe' | 'brief'): void {
    const id = start === 'recipe' ? 'cp-long-form-about-recipe-search' : 'cp-long-form-about-brief';

    afterNextRender(() => document.getElementById(id)?.focus(), { injector: this.injector });
  }

  /**
   * Move focus to the step's heading after a step change. Without it a keyboard or screen-reader user stays
   * where the button was and has to find the new step by hand.
   */
  private focusHeading(): void {
    afterNextRender(() => document.getElementById(LONG_FORM_PIPELINE_HEADING_ID)?.focus(), { injector: this.injector });
  }
}

const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A well-formed id from a query parameter's value, or null. */
function idFrom(value: string | null): string | null {
  return value && ID.test(value) ? value : null;
}
