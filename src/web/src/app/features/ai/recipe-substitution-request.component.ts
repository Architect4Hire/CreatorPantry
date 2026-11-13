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
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, defer, expand, switchMap, timer } from 'rxjs';
import {
  CpButtonComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpFieldComponent,
  CpStatusPillComponent,
} from '@creator-pantry/ui';

import { WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus, isTerminalAiStatus } from '../../models/ai-proposal.models';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import {
  SUBSTITUTION_ENUM_VALUE_FIELDS,
  SUBSTITUTION_FIELD_LABELS,
  SUBSTITUTION_REASON_MAX_LENGTH,
} from '../../models/recipe-substitution.models';
import {
  RecipeSubstitutionService,
  RequestSubstitutionOutcome,
  WatchSubstitutionOutcome,
} from '../../services/recipe-substitution.service';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../shared/allowance-gate';
import { REVISION_GOAL_MAX_LENGTH } from '../../models/recipe-revision.models';
import { AiAdvisoryItem, AiAdvisoryResultsComponent } from './ai-advisory-results.component';
import { AiStatusConnection } from './ai-operation-status.component';
import { RecipeRevisionRequestComponent } from './recipe-revision-request.component';

/** The alternative a creator chose, and the ingredient line it stands in for. */
export interface ChosenSwap {
  readonly line: string;
  readonly alternative: string;
  readonly goal: string;
}

/**
 * One alternative as a revision goal: replace this line with that, in the amount the advice gave, and touch the
 * method only where the swap needs it.
 *
 * The line and the alternative are always there — they are the swap — and cut short with an ellipsis only if one
 * alone would overflow the goal. The amount and the technique note go in whole or not at all, in that order, so the
 * goal never ends mid-sentence. Every word of it is either the creator's own line or advice they chose, and it reaches
 * the model in the fenced preferences segment like any goal they type.
 */
export function goalFromSubstitution(
  line: string,
  item: Pick<AiAdvisoryItem, 'summary' | 'fields'>,
  maxLength = REVISION_GOAL_MAX_LENGTH,
): string {
  const clip = (text: string, length: number) =>
    text.length <= length ? text : `${text.slice(0, Math.max(0, length - 1)).trimEnd()}…`;

  const swap = `Swap the ingredient "${clip(line.trim(), 120)}" for: ${clip(item.summary.trim(), 200)}.`;
  const closing = ' Change the method only where this swap needs it, and change nothing else.';
  let goal = swap;

  const field = (key: string) => item.fields.find((entry) => entry.key === key)?.value.trim() ?? '';
  for (const extra of [
    field('quantityGuidance') && ` How much to use: ${field('quantityGuidance')}`,
    field('techniqueImpact') && ` Effect on technique: ${field('techniqueImpact')}`,
  ]) {
    if (extra && goal.length + extra.length + closing.length <= maxLength) goal += extra;
  }

  return clip(goal + closing, maxLength);
}

/** Asking for a substitution writes an operation, so it carries the same bar as editing the recipe by hand. */
const REQUEST_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

export type SubstitutionRequestState = 'choosing' | 'submitting' | 'watching' | 'forbidden';

const COULD_NOT_REACH_SERVER = "Couldn't reach the server. Try again in a moment.";

/** How often a request in flight is asked about — mirrors `ai-proposal-panel.component.ts`'s own constants. */
const POLL_INTERVAL_MS = 2000;
const DEGRADED_POLL_INTERVAL_MS = 8000;
const MAX_CONSECUTIVE_POLL_FAILURES = 3;
const POLL_CEILING_MS = 5 * 60 * 1000;

/**
 * Ask for AIREC-004 substitution advice about one ingredient: choose the line, say why if you like, and read
 * what comes back.
 *
 * **Advice, never a diff.** A substitution changes nothing in the recipe — `IngredientSubstitution` is
 * deliberately absent from `AiChangeApplicability` — so what comes back is read through
 * `cp-ai-advisory-results`, not `cp-ai-proposal-panel`: there is nothing here for a creator to accept or
 * reject, only alternatives to judge and act on themselves.
 *
 * **Its own poll loop**, because the generic `ai-proposals` route this app's other polling goes through
 * deliberately refuses an advisory operation (`IAiProposalBusiness.GetAsync`). Mirrors
 * `ai-proposal-panel.component.ts`'s own loop exactly, minus everything about deciding.
 */
@Component({
  selector: 'cp-recipe-substitution-request',
  standalone: true,
  imports: [
    FormsModule,
    AiAdvisoryResultsComponent,
    AiAllowanceNoticeComponent,
    CpButtonComponent,
    CpComboboxComponent,
    RecipeRevisionRequestComponent,
    CpFieldComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-substitution-request.component.html',
  styleUrl: './recipe-substitution-request.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeSubstitutionRequestComponent {
  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();
  readonly currentVersionId = input.required<string>();
  readonly ingredientGroups = input.required<readonly RecipeIngredientGroup[]>();

  /**
   * Whether the editor hosting this pane has unsaved changes. The request is pinned to `currentVersionId`, so
   * anything still unsaved is not what the model reads — and a creator is told so rather than left to work it
   * out. The same input, and the same caveat, the recipe calculators in this tablist already carry.
   */
  readonly editorIsDirty = input(false);

  readonly decided = output<void>();

  /**
   * Let the creator pick an alternative and have it made (13A.15, "Improve a recipe").
   *
   * Off on the recipe page, where substitution stays advice. On, each alternative gets "Use this one", which asks
   * for a whole-recipe revision that swaps the line and adjusts the method where the swap needs it. That revision is
   * an ordinary one — every change shown, each kept or not by the creator — so choosing an alternative never changes
   * the recipe by itself: only an accepted change does.
   */
  readonly offerSwap = input(false);

  /** A swap's suggested changes were kept or declined. Carries the proposal panel's reply. */
  readonly swapDecided = output<unknown>();

  /** The level of the heading this draws: 2 on the recipe page, 3 inside a guided step. */
  readonly headingLevel = input<2 | 3>(2);

  private readonly swapRevision = viewChild(RecipeRevisionRequestComponent);

  /** The swap request on screen, so a host listing waiting suggestions does not list it a second time. */
  readonly swapRequestId = computed(() => this.swapRevision()?.requestId() ?? null);

  /** The ingredient line the advice on screen is about, as it read when the creator asked. */
  private readonly askedAboutSignal = signal<string | null>(null);

  private readonly chosenSignal = signal<ChosenSwap | null>(null);
  readonly chosen = this.chosenSignal.asReadonly();

  /** Alternatives can be chosen once the advice has arrived, until one has been. */
  readonly choosing = computed(
    () => this.offerSwap() && this.operation()?.status === 'Proposed' && this.chosen() === null,
  );

  private readonly substitutions = inject(RecipeSubstitutionService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly usage = inject(AiUsageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly fieldLabels = SUBSTITUTION_FIELD_LABELS;
  readonly enumValueFields = SUBSTITUTION_ENUM_VALUE_FIELDS;
  readonly reasonMaxLength = SUBSTITUTION_REASON_MAX_LENGTH;

  readonly ingredientText = signal('');
  readonly selectedIngredient = signal<CpComboboxOption | null>(null);
  readonly reason = signal('');

  private readonly submitting = signal(false);
  private readonly requestIdSignal = signal<string | null>(null);
  readonly requestId = this.requestIdSignal.asReadonly();

  private readonly refusalSignal = signal<string | null>(null);
  readonly refusal = this.refusalSignal.asReadonly();

  private readonly fieldErrorsSignal = signal<Readonly<Record<string, readonly string[]>>>({});

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

  readonly ingredientOptions = computed<readonly CpComboboxOption[]>(() =>
    this.ingredientGroups().flatMap((group) =>
      group.ingredients.map((ingredient) => ({
        id: ingredient.id,
        label: ingredient.displayText,
        detail: group.title ?? undefined,
      })),
    ),
  );

  readonly rolesKnown = computed(() => this.memberships.state().status === 'ready');

  readonly canRequest = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && REQUEST_ROLES.includes(role);
  });

  readonly state = computed<SubstitutionRequestState>(() => {
    if (this.rolesKnown() && !this.canRequest()) return 'forbidden';
    if (this.submitting()) return 'submitting';
    return this.requestIdSignal() === null ? 'choosing' : 'watching';
  });

  /** The account's allowance, so a spent one is said before it is hit rather than only after. */
  readonly allowance = this.usage.allowance;

  /** The server's refusal, once there has been one. Worded by the allowance notice. */
  private readonly quotaRefusalSignal = signal<AiQuotaRefusal | null>(null);
  readonly quotaRefusal = this.quotaRefusalSignal.asReadonly();

  readonly canSubmit = computed(
    () =>
      this.selectedIngredient() !== null &&
      !this.submitting() &&
      this.canRequest() &&
      !allowanceBlocksNewRequests(this.allowance()),
  );

  errorFor(field: string): string {
    return this.fieldErrorsSignal()[field]?.[0] ?? '';
  }

  async submit(): Promise<void> {
    const ingredient = this.selectedIngredient();
    if (ingredient === null || !this.canSubmit()) return;

    this.submitting.set(true);
    this.refusalSignal.set(null);
    this.quotaRefusalSignal.set(null);
    this.fieldErrorsSignal.set({});

    this.pendingRequestKey ??= crypto.randomUUID();
    this.askedAboutSignal.set(ingredient.label);

    try {
      const outcome = await this.substitutions.requestSubstitution(
        this.workspaceSlug(),
        this.recipeId(),
        {
          sourceVersionId: this.currentVersionId(),
          ingredientId: ingredient.id,
          reason: this.reason(),
        },
        this.pendingRequestKey,
      );

      this.apply(outcome);
    } finally {
      this.submitting.set(false);
    }
  }

  private apply(outcome: RequestSubstitutionOutcome): void {
    switch (outcome.status) {
      case 'accepted':
        this.pendingRequestKey = null;
        this.operationSignal.set(outcome.operation);
        this.requestIdSignal.set(outcome.operation.aiProposalRequestId);

        // The run has been paid for, so the balance on screen is already out of date.
        void this.usage.refresh();
        return;

      // The account cannot pay for this. Handed to the allowance notice, which is the one place that words a
      // spent allowance and a switched-off account differently. The key is released because this attempt
      // reached the server and was answered: a retry is a new request, not a re-send.
      case 'quota_exhausted':
      case 'account_suspended':
      case 'trial_ended':
        this.pendingRequestKey = null;
        this.quotaRefusalSignal.set(outcome);
        void this.usage.refresh();
        return;

      case 'validation_failed':
        this.pendingRequestKey = null;
        this.fieldErrorsSignal.set(outcome.fieldErrors);
        this.refusalSignal.set('That request could not be made as asked.');
        return;

      case 'task_not_enabled':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Ingredient substitution is not switched on for this workspace yet.');
        return;

      case 'source_version_invalid':
        this.pendingRequestKey = null;
        this.refusalSignal.set(
          'This recipe was saved while you were deciding what to ask for. Reload it and ask again, so the ' +
            'request is against the version you are looking at.',
        );
        return;

      case 'idempotency_key_conflict':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That request clashed with another one. Try again.');
        return;

      case 'forbidden':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Your role in this workspace cannot ask for substitution advice.');
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

  /** Clears the result so another substitution can be asked for. Nothing about the recipe is touched. */
  startAnother(): void {
    this.requestIdSignal.set(null);
    this.operationSignal.set(null);
    this.refusalSignal.set(null);
    this.fieldErrorsSignal.set({});
    this.pendingRequestKey = null;
    this.consecutiveFailures.set(0);
    this.chosenSignal.set(null);
    this.decided.emit();
  }

  /** The creator's ask: make this alternative. The revision below sends it on arrival — this button was the ask. */
  useAlternative(item: AiAdvisoryItem): void {
    const line = this.askedAboutSignal() ?? this.selectedIngredient()?.label ?? '';
    if (line.length === 0 || this.chosen() !== null) return;
    this.chosenSignal.set({ line, alternative: item.summary, goal: goalFromSubstitution(line, item) });
  }

  /** Back to the alternatives. A swap already asked for stays waiting until it is answered. */
  chooseAgain(): void {
    this.chosenSignal.set(null);
  }

  onSwapDecided(result: unknown): void {
    this.swapDecided.emit(result);
  }

  /** Mirrors `AiProposalPanelComponent.pollLoop` — see that file's own remarks for why `expand` over a fixed interval. */
  private pollLoop(requestId: string): Observable<WatchSubstitutionOutcome> {
    const deadline = Date.now() + POLL_CEILING_MS;
    let failures = 0;

    const ask = (): Observable<WatchSubstitutionOutcome> =>
      this.substitutions.watchStatus(this.workspaceSlug(), this.recipeId(), requestId);

    const again = (outcome: WatchSubstitutionOutcome): Observable<WatchSubstitutionOutcome> => {
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

  private applyWatchOutcome(outcome: WatchSubstitutionOutcome): void {
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
