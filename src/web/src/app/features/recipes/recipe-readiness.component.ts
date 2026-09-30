import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpCardComponent,
  CpDialogComponent,
  CpFieldComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  READINESS_EVIDENCE_KIND_LABELS,
  RECIPE_STATUS_LABELS,
  RecipeReadiness,
  RecipeReadinessEvidence,
  RecipeReadinessFinding,
  RecipeReadinessStatus,
  RecipeTransitionMove,
  RecipeTransitionRequest,
  TRANSITION_REASON_MAX_LENGTH,
  roleAllows,
  transitionsFrom,
} from '../../models/recipe-readiness.models';
import { RecipeDetail, RecipeStatus } from '../../models/recipe.models';
import { WorkspaceRole } from '../../models/auth.models';
import { RecipeReadinessService } from '../../services/recipe-readiness.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';

type ReadinessState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly readiness: RecipeReadiness }
  | { readonly status: 'not_found' }
  | { readonly status: 'error' };

type TransitionState =
  | { readonly status: 'idle' }
  | { readonly status: 'submitting'; readonly move: RecipeTransitionMove }
  /** The move does not exist from here, or is missing something it requires. The server's own words. */
  | { readonly status: 'invalid'; readonly message: string; readonly detail: string }
  | { readonly status: 'forbidden'; readonly message: string }
  /** Approval refused: readiness is not clear. The rule ids are named against the findings above. */
  | { readonly status: 'blocked'; readonly message: string; readonly ruleIds: readonly string[] }
  | { readonly status: 'conflict' }
  /**
   * The recipe came back in the state it was already in, so nothing happened.
   *
   * Carries the state it came back in rather than the target that was asked for. Those differ: the server
   * treats a move to the current state as a repeat, but this client never offers one, so the way this is
   * actually reached is a response that did not move the recipe — and naming the target would then report a
   * state the recipe is not in.
   */
  | { readonly status: 'no_change'; readonly unchangedStatus: RecipeStatus }
  | { readonly status: 'not_found' }
  | { readonly status: 'key_conflict' }
  | { readonly status: 'unavailable' };

/**
 * The order the checklist reads in: what is in the way, what would be worth doing, then what is already done.
 *
 * Satisfied and inapplicable rules are last and collapsed rather than dropped, because a creator needs to see
 * what they have cleared as much as what they have not — which is why the server sends every rule that ran.
 */
const GROUP_ORDER: readonly RecipeReadinessStatus[] = ['Blocker', 'Recommendation', 'Satisfied', 'NotApplicable'];

const GROUP_HEADINGS: Readonly<Record<RecipeReadinessStatus, string>> = {
  Blocker: 'In the way of approval',
  Recommendation: 'Worth doing first',
  Satisfied: 'Already done',
  // Never "passed": a rule that did not apply has not been met, it was not asked.
  NotApplicable: 'Did not apply',
};

const GROUP_TONES: Readonly<Record<RecipeReadinessStatus, CpStatusPillTone>> = {
  Blocker: 'error',
  Recommendation: 'warning',
  Satisfied: 'success',
  NotApplicable: 'neutral',
};

let nextInstance = 0;

/**
 * The readiness checklist and the editorial state machine's controls (TEST-UI-003).
 *
 * **Nothing here evaluates a rule.** Every verdict, sentence, count and piece of evidence comes from
 * `GET .../readiness`, and this component groups and renders them. It does not derive `hasBlockers`, does not
 * re-word a finding, and does not cache a verdict as a badge — an evaluation describes the recipe at the
 * moment it was read, and a stored one goes stale on the next edit.
 *
 * **It never implies an approval before the server has confirmed one.** No optimistic status change: the pill
 * shows the status the host gave it, and only a returned `RecipeDetail` moves it. While a move is in flight the
 * control reads "Approving…", never "Approved".
 *
 * **It is not a safety, allergen, nutrition or dietary clearance**, and no set of findings makes it one. The
 * copy says so where a reader will see it.
 *
 * **Which controls it offers is mirrored from the machine, not guessed and not asked for.** No route publishes
 * a recipe's legal targets, so `RECIPE_TRANSITIONS` holds a copy for presentation; the server refuses anything
 * that copy gets wrong, and a `400` naming the real targets is surfaced rather than swallowed.
 */
@Component({
  selector: 'cp-recipe-readiness',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpCardComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-readiness.component.html',
  styleUrl: './recipe-readiness.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeReadinessComponent {
  private readonly readinessService = inject(RecipeReadinessService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  private readonly confirmService = inject(ConfirmService);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** Where the recipe is now, from the host. The server's answer, never this component's guess. */
  readonly status = input.required<RecipeStatus>();

  /**
   * The recipe's `concurrencyToken`, from the host.
   *
   * Two jobs: it is quoted on every transition, and comparing it with the evaluation's token is how this
   * component knows the evaluation describes words that have since moved.
   */
  readonly concurrencyToken = input.required<string>();

  /** The recipe as it now stands, after a move the server confirmed. */
  readonly transitioned = output<RecipeDetail>();

  /**
   * A piece of evidence the reader activated.
   *
   * An output rather than a `routerLink`, because only the host knows where — if anywhere — a given record can
   * be shown. Several evidence kinds have no surface in the app yet, and a dead link is worse than a named
   * record.
   */
  readonly evidenceActivated = output<RecipeReadinessEvidence>();

  readonly idPrefix = `cp-readiness-${nextInstance++}`;
  readonly reasonMaxLength = TRANSITION_REASON_MAX_LENGTH;
  readonly groupOrder = GROUP_ORDER;
  readonly statusLabels = RECIPE_STATUS_LABELS;

  private readonly readinessStateSignal = signal<ReadinessState>({ status: 'loading' });
  readonly readinessState = this.readinessStateSignal.asReadonly();

  private readonly transitionStateSignal = signal<TransitionState>({ status: 'idle' });
  readonly transitionState = this.transitionStateSignal.asReadonly();

  /** The move waiting on its dialog, or null when none is open. */
  private readonly pendingMoveSignal = signal<RecipeTransitionMove | null>(null);
  readonly pendingMove = this.pendingMoveSignal.asReadonly();

  readonly reason = signal('');

  /** Whether the satisfied and inapplicable groups are open. Collapsed by default; per-viewer, so local. */
  private readonly expandedGroups = signal<ReadonlySet<RecipeReadinessStatus>>(new Set());

  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  constructor() {
    void this.membershipService.ensureLoaded();

    // Re-reads when the host points this at a different recipe. `untracked` so that taking the evaluation
    // does not make this effect depend on anything the load itself writes.
    effect(() => {
      const recipeId = this.recipeId();
      const workspaceSlug = this.workspaceSlug();
      untracked(() => void this.load(workspaceSlug, recipeId));
    });
  }

  // -------------------------------------------------------------------------
  // The evaluation
  // -------------------------------------------------------------------------

  readonly readiness = computed(() => {
    const state = this.readinessState();
    return state.status === 'ready' ? state.readiness : null;
  });

  readonly loading = computed(() => this.readinessState().status === 'loading');

  /**
   * Whether the evaluation describes content the recipe has since moved past.
   *
   * The comparison is on the token, which is what the server compares too: an approval whose evaluation
   * describes older words is refused as a conflict however clear it was.
   */
  readonly isStale = computed(() => {
    const readiness = this.readiness();
    return readiness !== null && readiness.concurrencyToken !== this.concurrencyToken();
  });

  readonly findingsByGroup = computed<ReadonlyMap<RecipeReadinessStatus, readonly RecipeReadinessFinding[]>>(() => {
    const grouped = new Map<RecipeReadinessStatus, RecipeReadinessFinding[]>();
    for (const group of GROUP_ORDER) grouped.set(group, []);

    // Server order is kept inside each group: the catalogue decides which rule reads first, not this screen.
    for (const finding of this.readiness()?.findings ?? []) {
      grouped.get(finding.status)?.push(finding);
    }

    return grouped;
  });

  findingsIn(group: RecipeReadinessStatus): readonly RecipeReadinessFinding[] {
    return this.findingsByGroup().get(group) ?? [];
  }

  headingFor(group: RecipeReadinessStatus): string {
    return GROUP_HEADINGS[group];
  }

  toneFor(group: RecipeReadinessStatus): CpStatusPillTone {
    return GROUP_TONES[group];
  }

  /** Blockers and recommendations are always open; what is already settled starts closed. */
  isCollapsible(group: RecipeReadinessStatus): boolean {
    return group === 'Satisfied' || group === 'NotApplicable';
  }

  isExpanded(group: RecipeReadinessStatus): boolean {
    return !this.isCollapsible(group) || this.expandedGroups().has(group);
  }

  toggleGroup(group: RecipeReadinessStatus): void {
    this.expandedGroups.update((groups) => {
      const next = new Set(groups);
      if (next.has(group)) next.delete(group);
      else next.add(group);
      return next;
    });
  }

  /** The pill tone for a whole count: green only when there is nothing outstanding. */
  countTone(count: number, tone: CpStatusPillTone): CpStatusPillTone {
    return count === 0 ? 'success' : tone;
  }

  evidenceKindLabel(kind: string): string {
    // A kind this app has no words for is humanised rather than shown raw or dropped: the server may add one
    // without this client changing, and the record is still worth naming.
    return READINESS_EVIDENCE_KIND_LABELS[kind] ?? kind.replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  findingElementId(ruleId: string): string {
    return `${this.idPrefix}-finding-${ruleId}`;
  }

  activateEvidence(evidence: RecipeReadinessEvidence): void {
    this.evidenceActivated.emit(evidence);
  }

  async reevaluate(): Promise<void> {
    this.transitionStateSignal.set({ status: 'idle' });
    await this.load(this.workspaceSlug(), this.recipeId());
  }

  private async load(workspaceSlug: string, recipeId: string): Promise<void> {
    this.readinessStateSignal.set({ status: 'loading' });

    const outcome = await this.readinessService.getReadiness(workspaceSlug, recipeId);

    switch (outcome.status) {
      case 'found':
        this.readinessStateSignal.set({ status: 'ready', readiness: outcome.readiness });
        return;
      case 'not_found':
        this.readinessStateSignal.set({ status: 'not_found' });
        return;
      default:
        this.readinessStateSignal.set({ status: 'error' });
    }
  }

  // -------------------------------------------------------------------------
  // Blockers named by a refused approval
  // -------------------------------------------------------------------------

  /**
   * The findings a refused approval named, matched back by `ruleId`.
   *
   * The refusal carries ids and not sentences, deliberately — the sentences are this screen's, and repeating
   * them in the error body would be two places for one wording. An id with no matching finding is still shown
   * by its id rather than dropped: the evaluation may have moved on, and a blocker nobody can see is worse
   * than one named awkwardly.
   */
  readonly blockedRules = computed<readonly { readonly ruleId: string; readonly summary: string | null }[]>(() => {
    const state = this.transitionState();
    if (state.status !== 'blocked') return [];

    const byRuleId = new Map(this.readiness()?.findings.map((finding) => [finding.ruleId, finding]) ?? []);

    return state.ruleIds.map((ruleId) => ({ ruleId, summary: byRuleId.get(ruleId)?.summary ?? null }));
  });

  /** Sends focus to the finding a refusal named, so a keyboard reader lands on the thing to fix. */
  revealFinding(ruleId: string): void {
    const target = this.elementRef.nativeElement.querySelector<HTMLElement>(
      `[id="${this.findingElementId(ruleId)}"]`,
    );
    if (target === null) return;

    target.scrollIntoView({ block: 'center' });
    target.focus();
  }

  // -------------------------------------------------------------------------
  // Transition controls
  // -------------------------------------------------------------------------

  readonly role = computed<WorkspaceRole | null>(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role ?? null;
  });

  /**
   * The moves this caller may make from where the recipe is.
   *
   * Filtered by role as well as by state, because offering a control that answers `403` is worse than not
   * offering it. The server remains the authority on both — a stale role or a changed machine comes back as a
   * refusal this component surfaces.
   */
  readonly availableMoves = computed<readonly RecipeTransitionMove[]>(() =>
    transitionsFrom(this.status()).filter((move) => roleAllows(this.role(), move.minimumRole)),
  );

  readonly submitting = computed(() => this.transitionState().status === 'submitting');

  submittingMove(): RecipeTransitionMove | null {
    const state = this.transitionState();
    return state.status === 'submitting' ? state.move : null;
  }

  /**
   * Why a move cannot be attempted right now, or `''` when it can.
   *
   * Only ever about facts this client holds — an evaluation that has gone stale, or a move already in flight.
   * Whether readiness is *clear* is never judged here; that is the server's gate, and a refusal explains it.
   */
  blockedReason(move: RecipeTransitionMove): string {
    if (this.submitting()) return 'Another change is being saved.';

    if (move.requiresReadinessClear) {
      if (this.readiness() === null) {
        return 'The readiness check has not been read yet.';
      }

      if (this.isStale()) {
        return 'This recipe has changed since it was checked. Check it again before approving.';
      }
    }

    return '';
  }

  canAttempt(move: RecipeTransitionMove): boolean {
    return this.blockedReason(move) === '';
  }

  /** What a move's control says while it is in flight. Never the past tense. */
  busyLabelFor(move: RecipeTransitionMove): string {
    switch (move.to) {
      case 'Approved':
        return 'Approving…';
      case 'Archived':
        return 'Archiving…';
      default:
        return 'Saving…';
    }
  }

  async attempt(move: RecipeTransitionMove): Promise<void> {
    if (!this.canAttempt(move)) return;

    this.transitionStateSignal.set({ status: 'idle' });

    switch (move.confirmation) {
      case 'dialog':
        this.reason.set('');
        this.pendingMoveSignal.set(move);
        return;

      case 'destructive': {
        const confirmed = await this.confirmService.confirm({
          title: 'Archive this recipe?',
          message:
            'It leaves everyone’s library and stops accepting content changes until it is brought back. '
            + 'Nothing is deleted — every version, note and test stays where it is.',
          confirmLabel: 'Archive it',
          cancelLabel: 'Leave it where it is',
          tone: 'danger',
        });

        if (confirmed) await this.send(move, null);
        return;
      }

      default:
        await this.send(move, null);
    }
  }

  /** Whether the open dialog can be submitted. A reopen needs its sentence; nothing else does. */
  readonly canConfirmDialog = computed(() => {
    const move = this.pendingMove();
    if (move === null || this.submitting()) return false;

    return !move.requiresReason || this.reason().trim().length > 0;
  });

  async confirmDialog(): Promise<void> {
    const move = this.pendingMove();
    if (move === null || !this.canConfirmDialog()) return;

    const reason = this.reason().trim();
    this.pendingMoveSignal.set(null);
    await this.send(move, reason.length === 0 ? null : reason);
  }

  closeDialog(): void {
    this.pendingMoveSignal.set(null);
  }

  private async send(move: RecipeTransitionMove, reason: string | null): Promise<void> {
    const request: RecipeTransitionRequest = {
      targetStatus: move.to,
      reason,
      expectedConcurrencyToken: this.concurrencyToken(),
    };

    const previousStatus = this.status();
    this.transitionStateSignal.set({ status: 'submitting', move });

    const outcome = await this.readinessService.transition(
      this.workspaceSlug(),
      this.recipeId(),
      request,
      this.idempotencyKeyFor(request),
    );

    switch (outcome.status) {
      case 'moved':
        this.transitionStateSignal.set(
          // A move that changed nothing succeeds and looks identical to one that did: the status it came back
          // with is the only way to tell. Saying "moved" then would claim something happened.
          outcome.recipe.status === previousStatus
            ? { status: 'no_change', unchangedStatus: outcome.recipe.status }
            : { status: 'idle' },
        );
        this.pendingIdempotencyKey = null;
        this.pendingRequestSignature = null;

        // The host owns the recipe, so it learns the new status from here and hands it back as `status`. This
        // component shows no new state of its own until it does.
        this.transitioned.emit(outcome.recipe);

        // The evaluation described the words as they were before this move. An approval writes a version, so
        // re-reading is the only way the checklist stays true.
        await this.load(this.workspaceSlug(), this.recipeId());
        return;

      case 'invalid':
        // The mirrored machine and the server disagree. The server's sentence names the real targets, and
        // re-reading keeps the rest of the screen honest.
        this.transitionStateSignal.set({
          status: 'invalid',
          message: outcome.message,
          detail: outcome.detail,
        });
        return;

      case 'forbidden':
        this.transitionStateSignal.set({ status: 'forbidden', message: outcome.message });
        return;

      case 'blocked':
        this.transitionStateSignal.set({
          status: 'blocked',
          message: outcome.message,
          ruleIds: outcome.blockingRuleIds,
        });
        return;

      case 'conflict':
        this.transitionStateSignal.set({ status: 'conflict' });
        return;

      case 'not_found':
        this.transitionStateSignal.set({ status: 'not_found' });
        return;

      case 'idempotency_key_conflict':
        // Bound to a different body server-side, so a repeat of this key can only ever be refused.
        this.pendingIdempotencyKey = null;
        this.pendingRequestSignature = null;
        this.transitionStateSignal.set({ status: 'key_conflict' });
        return;

      default:
        this.transitionStateSignal.set({ status: 'unavailable' });
    }
  }

  /** Reuses the in-flight key for a retry of the identical move; a different move is a different operation. */
  private idempotencyKeyFor(request: RecipeTransitionRequest): string {
    const signature = JSON.stringify([this.recipeId(), request]);

    if (this.pendingIdempotencyKey !== null && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }

    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;
    return key;
  }

  // -------------------------------------------------------------------------
  // Messages
  // -------------------------------------------------------------------------

  /** Whether trying the same move again could ever succeed. */
  readonly canRetryTransition = computed(() => {
    switch (this.transitionState().status) {
      case 'blocked':
      case 'forbidden':
      case 'invalid':
      case 'not_found':
        return false;
      default:
        return true;
    }
  });

  /**
   * One sentence about the last attempt, or `''` while nothing needs saying.
   *
   * The server's own words are used verbatim where it sent some: it knows which two states and which role were
   * involved, and a second copy of those sentences here would be one more thing to keep in step.
   */
  readonly transitionMessage = computed(() => {
    const state = this.transitionState();

    switch (state.status) {
      case 'invalid':
        return `${state.message} ${state.detail}`.trim();
      case 'forbidden':
        return state.message;
      case 'blocked':
        return state.message;
      case 'conflict':
        return 'This recipe moved while you were looking at it. Nothing was changed — check it again and try once more.';
      case 'no_change':
        return `This recipe was already ${this.statusLabels[state.unchangedStatus]}, so nothing changed.`;
      case 'not_found':
        return 'This recipe is no longer there, or you cannot read it.';
      case 'key_conflict':
        return 'That change was already used for something else. Try again.';
      case 'unavailable':
        return 'The change could not be saved. Nothing has been written — try again.';
      default:
        return '';
    }
  });

  readonly readinessMessage = computed(() => {
    switch (this.readinessState().status) {
      case 'not_found':
        return 'This recipe is no longer there, or you cannot read it.';
      case 'error':
        return "The readiness check couldn't be read. Nothing about the recipe has changed.";
      default:
        return '';
    }
  });
}
