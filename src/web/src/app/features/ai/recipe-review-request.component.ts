import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, defer, expand, switchMap, timer } from 'rxjs';
import { CpButtonComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus, isTerminalAiStatus } from '../../models/ai-proposal.models';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { REVIEW_ENUM_VALUE_FIELDS, REVIEW_FIELD_LABELS, REVIEW_HIDDEN_FIELDS } from '../../models/recipe-review.models';
import { REVISION_GOAL_MAX_LENGTH } from '../../models/recipe-revision.models';
import { RecipeReviewService, RequestReviewOutcome, WatchReviewOutcome } from '../../services/recipe-review.service';
import { AiUsageService } from '../../services/ai-usage.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../shared/allowance-gate';
import { AiAdvisoryResultsComponent } from './ai-advisory-results.component';
import { AiStatusConnection } from './ai-operation-status.component';
import { RecipeRevisionRequestComponent } from './recipe-revision-request.component';

/** One finding as the fix request quotes it. */
interface PickableFinding {
  readonly targetId: string;
  readonly summary: string;
}

/** What a set of picked findings becomes: the goal a revision is asked for, and how many would not fit in it. */
export interface FindingsGoal {
  readonly goal: string;
  readonly included: number;
  readonly leftOut: number;
}

const FIX_GOAL_OPENING = 'Fix these points a review of this recipe raised, and change nothing else:';

/**
 * The picked findings as one revision goal, each on its own line, in the order the review gave them.
 *
 * Bounded by the goal's own limit rather than cut mid-sentence: a finding that would not fit whole is left out and
 * counted, so the creator can be told to ask for the rest separately. The goal is still the creator's request —
 * they chose every line of it — and it reaches the model in the fenced preferences segment like any goal they type.
 */
export function goalFromFindings(
  findings: readonly PickableFinding[],
  maxLength = REVISION_GOAL_MAX_LENGTH,
): FindingsGoal {
  let goal = FIX_GOAL_OPENING;
  let included = 0;

  for (const finding of findings) {
    const line = `\n- ${finding.summary.trim()}`;
    if (goal.length + line.length > maxLength) break;
    goal += line;
    included += 1;
  }

  return { goal, included, leftOut: findings.length - included };
}

/** Asking for a review writes an operation, so it carries the same bar as editing the recipe by hand. */
const REQUEST_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

export type ReviewRequestState = 'idle' | 'submitting' | 'watching' | 'forbidden';

const COULD_NOT_REACH_SERVER = "Couldn't reach the server. Try again in a moment.";

/** How often a request in flight is asked about — mirrors `ai-proposal-panel.component.ts`'s own constants. */
const POLL_INTERVAL_MS = 2000;
const DEGRADED_POLL_INTERVAL_MS = 8000;
const MAX_CONSECUTIVE_POLL_FAILURES = 3;
const POLL_CEILING_MS = 5 * 60 * 1000;

/**
 * Ask for an AIREC-006 review of the whole pinned recipe, and read the findings.
 *
 * **Nothing to choose.** Unlike a substitution's ingredient or a revision's section, a review reads the whole
 * recipe — there is exactly one control here, and it asks.
 *
 * **Findings, never a diff.** A review changes nothing in the recipe — `RecipeReviewFinding` is deliberately
 * absent from `AiChangeApplicability` — so what comes back is read through `cp-ai-advisory-results`, the same
 * as a substitution's alternatives, not `cp-ai-proposal-panel`.
 *
 * **Its own poll loop**, for the reason `RecipeSubstitutionRequestComponent`'s own remarks give: the generic
 * `ai-proposals` route refuses an advisory operation outright.
 */
@Component({
  selector: 'cp-recipe-review-request',
  standalone: true,
  imports: [
    AiAdvisoryResultsComponent,
    AiAllowanceNoticeComponent,
    CpButtonComponent,
    CpStatusPillComponent,
    RecipeRevisionRequestComponent,
  ],
  templateUrl: './recipe-review-request.component.html',
  styleUrl: './recipe-review-request.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeReviewRequestComponent {
  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();
  readonly currentVersionId = input.required<string>();

  /**
   * Whether the editor hosting this pane has unsaved changes. The review is pinned to `currentVersionId`, so
   * anything still unsaved is not what was read — and a creator is told so rather than left to work it out.
   * The same input, and the same caveat, the recipe calculators in this tablist already carry.
   */
  readonly editorIsDirty = input(false);

  readonly decided = output<void>();

  /** The level of the heading this draws: 2 on the recipe page, 3 inside a guided step under the step's own heading. */
  readonly headingLevel = input<2 | 3>(2);

  /**
   * Let the creator pick findings and ask for changes that address them (13A.15, "Improve a recipe").
   *
   * Off on the recipe page, where the review stays pure advice. On, each finding gets a checkbox, and "Suggest
   * changes for these" asks for a whole-recipe revision whose goal lists the picked findings. That revision is an
   * ordinary one — its own request, its own proposal panel, every change ticked or not by the creator — so a
   * finding never changes the recipe by being picked: only an accepted change does.
   */
  readonly offerFixes = input(false);

  /** A suggested fix was kept or declined. Carries the proposal panel's reply. */
  readonly fixDecided = output<unknown>();

  private readonly fixRevision = viewChild(RecipeRevisionRequestComponent);

  /** The fix request on screen, so a host listing waiting suggestions does not list it a second time. */
  readonly fixRequestId = computed(() => this.fixRevision()?.requestId() ?? null);

  private readonly pickedIds = signal<ReadonlySet<string>>(new Set());
  readonly picked = this.pickedIds.asReadonly();

  /** The goal the picked findings were turned into, once the creator asked. Null while they are still picking. */
  private readonly fixGoalSignal = signal<FindingsGoal | null>(null);
  readonly fixGoal = this.fixGoalSignal.asReadonly();

  /** Every finding the review returned, in its order — one per `Add` row, as `cp-ai-advisory-results` groups them. */
  readonly findings = computed<readonly PickableFinding[]>(() =>
    (this.operation()?.proposal?.changes ?? [])
      .filter((change) => change.changeKind === 'Add')
      .map((change) => ({ targetId: change.targetId ?? change.changeId, summary: change.afterValue ?? '' }))
      .filter((finding) => finding.summary.trim().length > 0),
  );

  readonly pickedFindings = computed(() => this.findings().filter((finding) => this.pickedIds().has(finding.targetId)));

  /** Findings can be picked once the review has answered, until a fix has been asked for. */
  readonly picking = computed(
    () => this.offerFixes() && this.operation()?.status === 'Proposed' && this.findings().length > 0 && !this.fixGoal(),
  );

  readonly askFixLabel = computed(() => {
    const count = this.pickedFindings().length;
    return count === 0
      ? 'Suggest changes for the findings you pick'
      : `Suggest changes for ${count === 1 ? 'this finding' : `these ${count} findings`}`;
  });

  private readonly reviews = inject(RecipeReviewService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly usage = inject(AiUsageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly fieldLabels = REVIEW_FIELD_LABELS;
  readonly enumValueFields = REVIEW_ENUM_VALUE_FIELDS;
  readonly hiddenFields = REVIEW_HIDDEN_FIELDS;

  private readonly submitting = signal(false);
  private readonly requestIdSignal = signal<string | null>(null);
  readonly requestId = this.requestIdSignal.asReadonly();

  private readonly refusalSignal = signal<string | null>(null);
  readonly refusal = this.refusalSignal.asReadonly();

  private pendingRequestKey: string | null = null;

  private readonly operationSignal = signal<AiProposalStatus | null>(null);
  readonly operation = this.operationSignal.asReadonly();

  private readonly consecutiveFailures = signal(0);
  readonly connection = computed<AiStatusConnection>(() => (this.consecutiveFailures() > 0 ? 'degraded' : 'live'));

  constructor() {
    void this.memberships.ensureLoaded();

    toObservable(this.requestId)
      .pipe(
        switchMap((requestId) => (requestId === null ? EMPTY : this.pollLoop(requestId))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (outcome) => this.applyWatchOutcome(outcome),
      });
  }

  readonly rolesKnown = computed(() => this.memberships.state().status === 'ready');

  readonly canRequest = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && REQUEST_ROLES.includes(role);
  });

  readonly state = computed<ReviewRequestState>(() => {
    if (this.rolesKnown() && !this.canRequest()) return 'forbidden';
    if (this.submitting()) return 'submitting';
    return this.requestIdSignal() === null ? 'idle' : 'watching';
  });

  /** The account's allowance, so a spent one is said before it is hit rather than only after. */
  readonly allowance = this.usage.allowance;

  /** The server's refusal, once there has been one. Held apart from `refusal` so the notice can word it. */
  private readonly quotaRefusalSignal = signal<AiQuotaRefusal | null>(null);
  readonly quotaRefusal = this.quotaRefusalSignal.asReadonly();

  readonly canSubmit = computed(
    () => !this.submitting() && this.canRequest() && !allowanceBlocksNewRequests(this.allowance()),
  );

  async submit(): Promise<void> {
    if (!this.canSubmit()) return;

    this.submitting.set(true);
    this.refusalSignal.set(null);
    this.quotaRefusalSignal.set(null);

    this.pendingRequestKey ??= crypto.randomUUID();

    try {
      const outcome = await this.reviews.requestReview(
        this.workspaceSlug(),
        this.recipeId(),
        { sourceVersionId: this.currentVersionId() },
        this.pendingRequestKey,
      );

      this.apply(outcome);
    } finally {
      this.submitting.set(false);
    }
  }

  private apply(outcome: RequestReviewOutcome): void {
    switch (outcome.status) {
      case 'accepted':
        this.pendingRequestKey = null;
        this.operationSignal.set(outcome.operation);
        this.requestIdSignal.set(outcome.operation.aiProposalRequestId);

        // The run has been paid for, so the balance on screen is already out of date.
        void this.usage.refresh();
        return;

      // The account cannot pay for this. Handed to the allowance notice, which is the one place that knows
      // how to word a spent allowance and a switched-off account differently. The key is released because
      // this attempt reached the server and was answered: a retry is a new request, not a re-send.
      case 'quota_exhausted':
      case 'account_suspended':
      case 'trial_ended':
        this.pendingRequestKey = null;
        this.quotaRefusalSignal.set(outcome);

        // The figures moved under us — a refusal is the most current thing anyone has said about them.
        void this.usage.refresh();
        return;

      case 'validation_failed':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That request could not be made as asked.');
        return;

      case 'task_not_enabled':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Recipe review is not switched on for this workspace yet.');
        return;

      case 'source_version_invalid':
        this.pendingRequestKey = null;
        this.refusalSignal.set(
          'This recipe was saved while the request was being made. Reload it and ask again, so the review ' +
            'is against the version you are looking at.',
        );
        return;

      case 'idempotency_key_conflict':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That request clashed with another one. Try again.');
        return;

      case 'forbidden':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Your role in this workspace cannot ask for a review.');
        return;

      case 'not_found':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That recipe could not be found.');
        return;

      case 'unavailable':
        this.refusalSignal.set(COULD_NOT_REACH_SERVER);
        return;
    }
  }

  /** Clears the result so another review can be asked for. Nothing about the recipe is touched. */
  startAnother(): void {
    this.requestIdSignal.set(null);
    this.operationSignal.set(null);
    this.refusalSignal.set(null);
    this.pendingRequestKey = null;
    this.consecutiveFailures.set(0);
    this.pickedIds.set(new Set());
    this.fixGoalSignal.set(null);
    this.decided.emit();
  }

  onFindingToggled(change: { readonly targetId: string; readonly checked: boolean }): void {
    this.pickedIds.update((picked) => {
      const next = new Set(picked);
      if (change.checked) next.add(change.targetId);
      else next.delete(change.targetId);
      return next;
    });
  }

  pickAll(): void {
    this.pickedIds.set(new Set(this.findings().map((finding) => finding.targetId)));
  }

  clearPicks(): void {
    this.pickedIds.set(new Set());
  }

  /**
   * The creator's ask: turn the picked findings into a revision goal. The revision below sends it on arrival —
   * this button was the ask — and shows what comes back in the proposal panel, change by change.
   */
  askForFixes(): void {
    const picked = this.pickedFindings();
    if (picked.length === 0 || this.fixGoal() !== null) return;
    this.fixGoalSignal.set(goalFromFindings(picked));
  }

  /** Back to the findings, to pick again. The suggestion already asked for stays waiting until it is answered. */
  pickAgain(): void {
    this.fixGoalSignal.set(null);
  }

  onFixDecided(result: unknown): void {
    this.fixDecided.emit(result);
  }

  /** Mirrors `AiProposalPanelComponent.pollLoop` — see that file's own remarks for why `expand` over a fixed interval. */
  private pollLoop(requestId: string): Observable<WatchReviewOutcome> {
    const deadline = Date.now() + POLL_CEILING_MS;
    let failures = 0;

    const ask = (): Observable<WatchReviewOutcome> =>
      this.reviews.watchStatus(this.workspaceSlug(), this.recipeId(), requestId);

    const again = (outcome: WatchReviewOutcome): Observable<WatchReviewOutcome> => {
      if (outcome.status === 'not_found' || outcome.status === 'forbidden') return EMPTY;
      if (outcome.status === 'found' && isTerminalAiStatus(outcome.operation.status)) return EMPTY;
      if (outcome.status === 'found' && outcome.operation.status === 'Proposed') return EMPTY;

      failures = outcome.status === 'unavailable' ? failures + 1 : 0;
      if (failures >= MAX_CONSECUTIVE_POLL_FAILURES) return EMPTY;
      if (Date.now() >= deadline) return EMPTY;

      return timer(failures === 0 ? POLL_INTERVAL_MS : DEGRADED_POLL_INTERVAL_MS).pipe(switchMap(ask));
    };

    return defer(ask).pipe(expand(again));
  }

  private applyWatchOutcome(outcome: WatchReviewOutcome): void {
    if (outcome.status === 'found') {
      this.consecutiveFailures.set(0);
      this.operationSignal.set(outcome.operation);
      return;
    }

    if (outcome.status === 'unavailable') {
      this.consecutiveFailures.update((count) => count + 1);
    }
  }
}
