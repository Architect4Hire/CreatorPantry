import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, defer, expand, switchMap, timer } from 'rxjs';
import {
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpListShellComponent,
  CpListShellState,
} from '@creator-pantry/ui';

import { WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus, isTerminalAiStatus } from '../../models/ai-proposal.models';
import {
  RecipeConcept,
  RequestRecipeConceptsRequest,
  conceptsFromProposal,
  generalConceptWarnings,
} from '../../models/recipe-concept.models';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { RecipeConceptService, WatchConceptsOutcome } from '../../services/recipe-concept.service';
import { AiUsageService } from '../../services/ai-usage.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../shared/allowance-gate';
import { AiOperationStatusComponent, AiStatusConnection, isRetryableAiOutcome } from './ai-operation-status.component';

/** Requesting concepts writes an operation, so it carries the same bar as the recipe-diff proposal panel. */
const REQUEST_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

/** Same cadence as `AiProposalPanelComponent`, so the two AI surfaces in the app behave identically. */
const POLL_INTERVAL_MS = 2000;
const DEGRADED_POLL_INTERVAL_MS = 8000;
const MAX_CONSECUTIVE_POLL_FAILURES = 3;
const POLL_CEILING_MS = 5 * 60 * 1000;

/** Mirrors `AiPolicy.BriefFieldMaxLength` / `AiPolicy.BriefListFieldMaxLength`. Client-side hint, not the check. */
const SHORT_FIELD_MAX_LENGTH = 200;
const LIST_FIELD_MAX_LENGTH = 500;

export type ConceptStudioState =
  | 'idle'
  | 'loading'
  | 'submitting'
  | 'watching'
  | 'proposed'
  | 'failed'
  | 'expired'
  | 'gone'
  | 'forbidden';

type StudioRefusalKind = 'request' | 'unavailable' | 'forbidden';

interface StudioRefusal {
  readonly kind: StudioRefusalKind;
  readonly message: string;
}

const COULD_NOT_REACH_SERVER = "Couldn't reach the server. Try again in a moment.";

/**
 * The AI Recipe Studio (`:workspaceSlug/ai-recipe-studio`): a structured brief for AIREC-001, and a place to
 * look at and choose among the concepts it returns.
 *
 * **Choosing a concept does not create a recipe.** There is no route here that writes one — see
 * `RecipeConceptService`, which has no disposition method to call. Selection is client-side state, surfaced so
 * a later step can pick it up; nothing about it is sent anywhere.
 *
 * **This is a different shape of problem from `cp-ai-proposal-panel`.** That panel reviews a diff against an
 * existing recipe — per-change accept/reject/edit, against a disposition endpoint. A concept-generation answer
 * has no recipe to diff against and no disposition to record, so this page renders its own result cards from
 * `conceptsFromProposal` instead of hosting that panel. It does reuse `cp-ai-operation-status` — purely
 * presentational — and the same poll/backoff/role-gating shape the panel already established, so the two AI
 * surfaces in the app read as one system.
 */
@Component({
  selector: 'cp-recipe-concept-studio',
  standalone: true,
  imports: [
    FormsModule,
    AiAllowanceNoticeComponent,
    AiOperationStatusComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpListShellComponent,
  ],
  templateUrl: './recipe-concept-studio.component.html',
  styleUrl: './recipe-concept-studio.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeConceptStudioComponent {
  private readonly concepts = inject(RecipeConceptService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly usage = inject(AiUsageService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly workspaceSlug = this.resolveWorkspaceSlug();

  readonly shortFieldMaxLength = SHORT_FIELD_MAX_LENGTH;
  readonly listFieldMaxLength = LIST_FIELD_MAX_LENGTH;

  /** A concept a host mounting this page can act on later. Selecting one never calls a create-recipe route. */
  readonly conceptSelected = output<RecipeConcept>();

  // ---- Brief fields ----

  readonly audience = signal('');
  readonly course = signal('');
  readonly cuisine = signal('');
  readonly dietaryGoals = signal('');
  readonly availableIngredients = signal('');
  readonly exclusions = signal('');
  readonly equipment = signal('');
  readonly skill = signal('');
  readonly season = signal('');
  readonly timeBudget = signal('');
  readonly creatorStyle = signal('');

  // ---- Request/watch state ----

  private readonly requestIdSignal = signal<string | null>(null);
  readonly requestId = this.requestIdSignal.asReadonly();

  private readonly operationSignal = signal<AiProposalStatus | null>(null);
  readonly operation = this.operationSignal.asReadonly();

  private readonly watchProblem = signal<'not_found' | 'forbidden' | null>(null);
  private readonly consecutiveFailures = signal(0);
  private readonly pollStopped = signal(false);

  /** False once the creator stops checking. There is no cancel route — see {@link stopChecking}. */
  private readonly watching = signal(true);

  /** Bumped to start a fresh poll loop over the same request. */
  private readonly resumeToken = signal(0);

  private readonly submitting = signal(false);

  private readonly fieldErrorsSignal = signal<Readonly<Record<string, readonly string[]>>>({});
  private readonly refusalSignal = signal<StudioRefusal | null>(null);
  readonly refusal = this.refusalSignal.asReadonly();

  private readonly selectedConceptSignal = signal<RecipeConcept | null>(null);
  readonly selectedConcept = this.selectedConceptSignal.asReadonly();

  /** Reused only across a retried transport failure of the same attempt; replaced for a fresh submit. */
  private pendingRequestKey: string | null = null;

  constructor() {
    this.restoreFromUrl();

    // One loop per (request, resume) pair — the key deliberately excludes status, or every transition would
    // tear the loop down and re-ask. Mirrors AiProposalPanelComponent's own constructor wiring.
    toObservable(this.pollKey)
      .pipe(
        switchMap((key) => {
          if (key === null) return EMPTY;
          this.pollStopped.set(false);
          return this.pollLoop(key.requestId);
        }),
        takeUntilDestroyed(),
      )
      .subscribe({ next: (outcome) => this.applyWatchOutcome(outcome) });

    void this.memberships.ensureLoaded();
  }

  private readonly pollKey = computed<{ readonly requestId: string; readonly resume: number } | null>(() => {
    const requestId = this.requestIdSignal();
    if (requestId === null || !this.watching()) return null;
    return { requestId, resume: this.resumeToken() };
  });

  /** Asks, then keeps asking until there is nothing left to learn. See `AiProposalPanelComponent.pollLoop`. */
  private pollLoop(requestId: string): Observable<WatchConceptsOutcome> {
    const deadline = Date.now() + POLL_CEILING_MS;
    let failures = 0;

    const ask = (): Observable<WatchConceptsOutcome> => this.concepts.watchStatus(this.workspaceSlug, requestId);

    const again = (outcome: WatchConceptsOutcome): Observable<WatchConceptsOutcome> => {
      if (outcome.status === 'not_found' || outcome.status === 'forbidden') return EMPTY;
      if (outcome.status === 'found' && !RecipeConceptStudioComponent.needsWatching(outcome.operation)) return EMPTY;

      failures = outcome.status === 'unavailable' ? failures + 1 : 0;
      if (failures >= MAX_CONSECUTIVE_POLL_FAILURES) return EMPTY;
      if (Date.now() >= deadline) return EMPTY;

      return timer(failures === 0 ? POLL_INTERVAL_MS : DEGRADED_POLL_INTERVAL_MS).pipe(switchMap(ask));
    };

    return defer(ask).pipe(
      expand(again),
      (source) =>
        new Observable<WatchConceptsOutcome>((subscriber) =>
          source.subscribe({
            next: (value) => subscriber.next(value),
            error: (error: unknown) => subscriber.error(error),
            complete: () => {
              this.pollStopped.set(true);
              subscriber.complete();
            },
          }),
        ),
    );
  }

  /** Nothing moves server-side once concepts are `Proposed` — there is no disposition route to move it further. */
  private static needsWatching(operation: AiProposalStatus): boolean {
    return !isTerminalAiStatus(operation.status) && operation.status !== 'Proposed';
  }

  private applyWatchOutcome(outcome: WatchConceptsOutcome): void {
    if (outcome.status === 'found') {
      this.consecutiveFailures.set(0);
      this.watchProblem.set(null);
      this.operationSignal.set(outcome.operation);
      return;
    }

    if (outcome.status === 'unavailable') {
      // The last known state stays on screen. One failed poll must not blank concepts a creator is reading.
      this.consecutiveFailures.update((count) => count + 1);
      return;
    }

    this.watchProblem.set(outcome.status);
  }

  // -------------------------------------------------------------------------
  // Derived view
  // -------------------------------------------------------------------------

  readonly connection = computed<AiStatusConnection>(() => (this.consecutiveFailures() > 0 ? 'degraded' : 'live'));

  readonly state = computed<ConceptStudioState>(() => {
    if (this.submitting()) return 'submitting';
    if (this.requestIdSignal() === null) return 'idle';

    const problem = this.watchProblem();
    if (problem === 'not_found') return 'gone';
    if (problem === 'forbidden') return 'forbidden';

    const operation = this.operationSignal();
    if (operation === null) return 'loading';

    switch (operation.status) {
      case 'Requested':
      case 'Running':
        return 'watching';
      case 'Proposed':
        return operation.proposal === null ? 'watching' : 'proposed';
      case 'Failed':
        return 'failed';
      case 'Expired':
        return 'expired';
      // Never reached: nothing decides a concept request, so these never arrive here.
      case 'Accepted':
      case 'PartiallyAccepted':
      case 'Rejected':
        return 'proposed';
    }
  });

  readonly rolesKnown = computed(() => this.memberships.state().status === 'ready');

  readonly canRequest = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug)?.role;
    return role !== undefined && REQUEST_ROLES.includes(role);
  });

  /** The account's allowance, so a spent one is said before it is hit rather than only after. */
  readonly allowance = this.usage.allowance;

  /** The server's refusal, once there has been one. Worded by the allowance notice. */
  private readonly quotaRefusalSignal = signal<AiQuotaRefusal | null>(null);
  readonly quotaRefusal = this.quotaRefusalSignal.asReadonly();

  /** Everything `canRequest` asks, plus an allowance that is known to be spent. */
  readonly canSubmit = computed(
    () => !this.submitting() && this.canRequest() && !allowanceBlocksNewRequests(this.allowance()),
  );

  readonly conceptCards = computed<readonly RecipeConcept[]>(() =>
    conceptsFromProposal(this.operation()?.proposal ?? null),
  );

  readonly generalWarnings = computed(() => generalConceptWarnings(this.operation()?.proposal ?? null));

  /**
   * The concepts frame's state, so loading, error, empty and ready are the shell's rather than four hand-rolled
   * branches (frontend.md). `failed` and `expired` are errors here even though `cp-ai-operation-status` above
   * already names them: the frame is a data surface in its own right, and a shell left on "loading" for a
   * request that has stopped would be saying something untrue about it.
   */
  readonly conceptsListState = computed<CpListShellState>(() => {
    switch (this.state()) {
      case 'failed':
      case 'expired':
        return 'error';
      case 'proposed':
        return this.conceptCards().length === 0 ? 'empty' : 'ready';
      default:
        return 'loading';
    }
  });

  /** Short and factual; the status component above carries the detail and the failure category. */
  readonly conceptsErrorMessage = computed(() =>
    this.state() === 'expired'
      ? 'This request expired before any concepts came back. Nothing was changed.'
      : "This request didn't finish, so there are no concepts to read. Nothing was changed.",
  );

  readonly canAskAgain = computed(() => {
    const operation = this.operationSignal();
    return operation !== null && isRetryableAiOutcome(operation.status, operation.failureCategory);
  });

  /** Whether the panel has stopped asking on its own and is waiting to be told to look again. */
  readonly canCheckAgain = computed(() => {
    if (this.requestIdSignal() === null) return false;
    if (!this.watching()) return true;

    const operation = this.operationSignal();
    return this.pollStopped() && (operation === null || RecipeConceptStudioComponent.needsWatching(operation));
  });

  readonly isWatching = computed(() => this.watching() && !this.pollStopped());

  // -------------------------------------------------------------------------
  // Submitting a brief
  // -------------------------------------------------------------------------

  private currentBrief(): RequestRecipeConceptsRequest {
    return {
      audience: this.audience(),
      course: this.course(),
      cuisine: this.cuisine(),
      dietaryGoals: this.dietaryGoals(),
      availableIngredients: this.availableIngredients(),
      exclusions: this.exclusions(),
      equipment: this.equipment(),
      skill: this.skill(),
      season: this.season(),
      timeBudget: this.timeBudget(),
      creatorStyle: this.creatorStyle(),
    };
  }

  async submit(): Promise<void> {
    if (!this.canSubmit()) return;

    this.submitting.set(true);
    this.fieldErrorsSignal.set({});
    this.refusalSignal.set(null);
    this.quotaRefusalSignal.set(null);

    // One key per attempt, reused only if this same attempt has to be re-sent after a transport failure.
    this.pendingRequestKey ??= crypto.randomUUID();

    const outcome = await this.concepts.requestConcepts(this.workspaceSlug, this.currentBrief(), this.pendingRequestKey);

    this.submitting.set(false);

    switch (outcome.status) {
      case 'accepted':
        this.pendingRequestKey = null;
        this.resetForNewRequest();
        this.operationSignal.set(outcome.operation);
        this.requestIdSignal.set(outcome.operation.aiProposalRequestId);
        this.writeUrl();

        // The run has been paid for, so the balance on screen is already out of date.
        void this.usage.refresh();
        return;

      // The account cannot pay for this. Handed to the allowance notice, which is the one place that words a
      // spent allowance and a switched-off account differently. Nothing the creator typed is touched: the
      // brief is still on screen, ready to be sent again when the allowance comes back.
      case 'quota_exhausted':
      case 'account_suspended':
        this.pendingRequestKey = null;
        this.quotaRefusalSignal.set(outcome);
        void this.usage.refresh();
        return;

      case 'validation_failed':
        this.pendingRequestKey = null;
        this.fieldErrorsSignal.set(outcome.fieldErrors);
        return;

      case 'task_not_enabled':
        this.pendingRequestKey = null;
        this.refusalSignal.set({
          kind: 'request',
          message: 'Recipe concept generation is switched off for now, so this can’t be asked for.',
        });
        return;

      case 'idempotency_key_conflict':
        this.pendingRequestKey = null;
        this.refusalSignal.set({ kind: 'request', message: 'That attempt clashed with another. Try once more.' });
        return;

      case 'forbidden':
        this.pendingRequestKey = null;
        this.refusalSignal.set({
          kind: 'forbidden',
          message: 'Your role in this workspace does not permit asking for recipe concepts.',
        });
        return;

      case 'unavailable':
        // The key is kept: this attempt may have reached the server even though the answer did not come back,
        // and re-sending the same key is what stops a second answer being bought for it.
        this.refusalSignal.set({ kind: 'unavailable', message: COULD_NOT_REACH_SERVER });
        return;
    }
  }

  fieldError(field: string): string {
    return this.fieldErrorsSignal()[field]?.[0] ?? '';
  }

  /** Clears the form's result state so a new brief can be submitted; the typed field values are left as-is. */
  startNewBrief(): void {
    this.requestIdSignal.set(null);
    this.operationSignal.set(null);
    this.watchProblem.set(null);
    this.refusalSignal.set(null);
    this.selectedConceptSignal.set(null);
    this.consecutiveFailures.set(0);
    this.watching.set(true);
    this.pollStopped.set(false);
    this.writeUrl();
  }

  private resetForNewRequest(): void {
    this.watchProblem.set(null);
    this.selectedConceptSignal.set(null);
    this.consecutiveFailures.set(0);
    this.watching.set(true);
    this.pollStopped.set(false);
  }

  // -------------------------------------------------------------------------
  // Checking again, stopping
  // -------------------------------------------------------------------------

  /** Starts a fresh poll loop over the same request. */
  checkAgain(): void {
    this.consecutiveFailures.set(0);
    this.watching.set(true);
    this.resumeToken.update((token) => token + 1);
  }

  /**
   * Stops this page from checking. It does **not** cancel the request — the API offers asking and reading and
   * nothing else, so a button claiming to cancel generation would promise what the server cannot honour.
   */
  stopChecking(): void {
    this.watching.set(false);
  }

  // -------------------------------------------------------------------------
  // Selection
  // -------------------------------------------------------------------------

  /** Sets which concept the creator is looking at going forward. Never creates a recipe. */
  choose(concept: RecipeConcept): void {
    this.selectedConceptSignal.set(concept);
    this.conceptSelected.emit(concept);
  }

  isSelected(concept: RecipeConcept): boolean {
    return this.selectedConceptSignal()?.targetId === concept.targetId;
  }

  // -------------------------------------------------------------------------
  // URL resume
  // -------------------------------------------------------------------------

  private restoreFromUrl(): void {
    const requestId = this.route.snapshot.queryParamMap.get('request');
    if (requestId !== null && requestId.length > 0) this.requestIdSignal.set(requestId);
  }

  /** Written with `replaceUrl`, so polling is resumable after a refresh without cluttering browser history. */
  private writeUrl(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      replaceUrl: true,
      queryParams: { request: this.requestIdSignal() },
    });
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }

    throw new Error('RecipeConceptStudioComponent route is missing a workspaceSlug segment.');
  }
}
