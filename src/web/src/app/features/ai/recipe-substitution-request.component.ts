import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
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
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAdvisoryResultsComponent } from './ai-advisory-results.component';
import { AiStatusConnection } from './ai-operation-status.component';

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
    CpButtonComponent,
    CpComboboxComponent,
    CpFieldComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-substitution-request.component.html',
  styleUrl: './recipe-substitution-request.component.css',
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

  private readonly substitutions = inject(RecipeSubstitutionService);
  private readonly memberships = inject(WorkspaceMembershipService);
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

  readonly canSubmit = computed(
    () => this.selectedIngredient() !== null && !this.submitting() && this.canRequest(),
  );

  errorFor(field: string): string {
    return this.fieldErrorsSignal()[field]?.[0] ?? '';
  }

  async submit(): Promise<void> {
    const ingredient = this.selectedIngredient();
    if (ingredient === null || !this.canSubmit()) return;

    this.submitting.set(true);
    this.refusalSignal.set(null);
    this.fieldErrorsSignal.set({});

    this.pendingRequestKey ??= crypto.randomUUID();

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
          'This recipe was saved while you were deciding what to ask for. Reload it and ask again, so the '
            + 'request is against the version you are looking at.',
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
    this.decided.emit();
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
