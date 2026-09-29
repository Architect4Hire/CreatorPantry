import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent } from '@creator-pantry/ui';

import { WorkspaceRole } from '../../models/auth.models';
import {
  ADAPTATION_GOAL_DETAIL_MAX_LENGTH,
  AiAdaptationGoal,
  RECIPE_ADAPTATION_GOALS,
  RecipeAdaptationGoalOption,
} from '../../models/recipe-adaptation.models';
import { RecipeAdaptationService, RequestAdaptationOutcome } from '../../services/recipe-adaptation.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiProposalPanelComponent } from './ai-proposal-panel.component';

/** Asking for an adaptation writes an operation, so it carries the same bar as editing the recipe by hand. */
const REQUEST_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

export type AdaptationRequestState = 'choosing' | 'submitting' | 'watching' | 'forbidden';

/** How a yield goal names its target: a factor to scale by, or the batch size to scale toward. */
export type YieldTargetMode = 'multiplier' | 'targetYield';

const COULD_NOT_REACH_SERVER = "Couldn't reach the server. Try again in a moment.";

/**
 * Ask for an AIREC-005 single-goal adaptation of one recipe: choose the goal, say what it means for this
 * recipe, and review what comes back.
 *
 * **The goal is the bound, and it is exactly one.** A complete cross-field proposal always runs against the
 * whole recipe — there is no scope to choose, unlike a revision — so the only choice here is which single
 * goal the adaptation serves.
 *
 * **A yield goal's target is a number the server scales deterministically, never a model guess.** Every other
 * goal's detail is the creator's own untrusted words, reaching the model inside a fenced preferences segment;
 * a yield target is instead a request field the server resolves through the same deterministic scaling code a
 * hand-typed scale-this-recipe request uses, before the model is ever called (ai.md).
 *
 * **Reviewing is `cp-ai-proposal-panel`'s job, not this component's.** An adaptation produces an ordinary
 * server-computed diff against the pinned version, exactly like a revision's, so this hands the panel the
 * request id and gets out of the way.
 */
@Component({
  selector: 'cp-recipe-adaptation-request',
  standalone: true,
  imports: [FormsModule, AiProposalPanelComponent, CpButtonComponent, CpFieldComponent],
  templateUrl: './recipe-adaptation-request.component.html',
  styleUrl: './recipe-adaptation-request.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeAdaptationRequestComponent {
  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The recipe's current version, which is the only one an adaptation may be asked against.
   *
   * Required and supplied by the host, which is the thing that just read the recipe — the same reason a
   * revision request takes this as an input rather than fetching it itself.
   */
  readonly currentVersionId = input.required<string>();

  /** An adaptation was decided. Carries the server's reply, including the version it wrote. */
  readonly decided = output<unknown>();

  private readonly adaptations = inject(RecipeAdaptationService);
  private readonly memberships = inject(WorkspaceMembershipService);

  readonly goals = RECIPE_ADAPTATION_GOALS;
  readonly goalDetailMaxLength = ADAPTATION_GOAL_DETAIL_MAX_LENGTH;

  readonly goal = signal<AiAdaptationGoal | null>(null);
  readonly goalDetail = signal('');
  readonly yieldMode = signal<YieldTargetMode>('multiplier');

  /**
   * `string` in principle, but Angular's `NumberValueAccessor` (bound via `type="number"` in the template)
   * writes back a `number`, or `''` for an empty/unparseable field — never rejects it. Read through
   * {@link resolvedYieldValue} rather than assumed to be one type or the other.
   */
  readonly yieldValue = signal<string | number>('');

  private resolvedYieldValue(): number | null {
    const raw = this.yieldValue();
    if (raw === '') return null;

    const value = typeof raw === 'number' ? raw : Number(raw);
    return Number.isFinite(value) ? value : null;
  }

  private readonly submitting = signal(false);
  private readonly requestIdSignal = signal<string | null>(null);
  readonly requestId = this.requestIdSignal.asReadonly();

  private readonly refusalSignal = signal<string | null>(null);
  readonly refusal = this.refusalSignal.asReadonly();

  private readonly fieldErrorsSignal = signal<Readonly<Record<string, readonly string[]>>>({});

  /** Reused only across a retried transport failure of one attempt; replaced for a fresh ask. */
  private pendingRequestKey: string | null = null;

  constructor() {
    // Asked for rather than assumed: until the memberships are known this cannot say what the creator may do.
    void this.memberships.ensureLoaded();
  }

  readonly rolesKnown = computed(() => this.memberships.state().status === 'ready');

  readonly canRequest = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && REQUEST_ROLES.includes(role);
  });

  readonly state = computed<AdaptationRequestState>(() => {
    if (this.rolesKnown() && !this.canRequest()) return 'forbidden';
    if (this.submitting()) return 'submitting';
    return this.requestIdSignal() === null ? 'choosing' : 'watching';
  });

  readonly selectedOption = computed<RecipeAdaptationGoalOption | null>(() => {
    const goal = this.goal();
    return goal === null ? null : (this.goals.find((option) => option.goal === goal) ?? null);
  });

  /**
   * A goal must be chosen, and it must be able to say what it means: free-text detail for three of the four
   * goals, a positive number for yield.
   */
  readonly canSubmit = computed(() => {
    if (this.submitting() || !this.canRequest()) return false;

    const option = this.selectedOption();
    if (option === null) return false;

    if (option.takesDetail) {
      return this.goalDetail().trim().length > 0;
    }

    const value = this.resolvedYieldValue();
    return value !== null && value > 0;
  });

  isChosen(option: RecipeAdaptationGoalOption): boolean {
    return this.goal() === option.goal;
  }

  choose(option: RecipeAdaptationGoalOption): void {
    this.goal.set(option.goal);
    this.refusalSignal.set(null);
  }

  errorFor(field: string): string {
    return this.fieldErrorsSignal()[field]?.[0] ?? '';
  }

  async submit(): Promise<void> {
    const option = this.selectedOption();
    if (option === null || !this.canSubmit()) return;

    this.submitting.set(true);
    this.refusalSignal.set(null);
    this.fieldErrorsSignal.set({});

    // One key per attempt, reused only if this same attempt has to be re-sent after a transport failure —
    // so a retry returns the first answer rather than buying a second.
    this.pendingRequestKey ??= crypto.randomUUID();

    const yieldValue = option.goal === 'Yield' ? this.resolvedYieldValue() : null;

    try {
      const outcome = await this.adaptations.requestAdaptation(
        this.workspaceSlug(),
        this.recipeId(),
        {
          sourceVersionId: this.currentVersionId(),
          goal: option.goal,
          goalDetail: this.goalDetail(),
          targetMultiplier: yieldValue !== null && this.yieldMode() === 'multiplier' ? yieldValue : null,
          targetYieldQuantity: yieldValue !== null && this.yieldMode() === 'targetYield' ? yieldValue : null,
        },
        this.pendingRequestKey,
      );

      this.apply(outcome);
    } finally {
      this.submitting.set(false);
    }
  }

  private apply(outcome: RequestAdaptationOutcome): void {
    switch (outcome.status) {
      case 'accepted':
        this.pendingRequestKey = null;
        this.requestIdSignal.set(outcome.operation.aiProposalRequestId);
        return;

      case 'validation_failed':
        this.pendingRequestKey = null;
        this.fieldErrorsSignal.set(outcome.fieldErrors);
        this.refusalSignal.set('That request could not be made as asked.');
        return;

      case 'task_not_enabled':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Recipe adaptation is not switched on for this workspace yet.');
        return;

      case 'source_version_invalid':
        this.pendingRequestKey = null;
        this.refusalSignal.set(
          'This recipe was saved while you were deciding what to ask for. Reload it and ask again, so the '
            + 'adaptation is against the version you are looking at.',
        );
        return;

      case 'yield_target_invalid':
        this.pendingRequestKey = null;
        this.refusalSignal.set(
          "That target could not be scaled to — either the number isn't usable, or this recipe's yield "
            + 'is not a plain number to scale from.',
        );
        return;

      case 'idempotency_key_conflict':
        // A fresh key next time: this one already belongs to a different request.
        this.pendingRequestKey = null;
        this.refusalSignal.set('That request clashed with another one. Try again.');
        return;

      case 'forbidden':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Your role in this workspace cannot ask for an adaptation.');
        return;

      case 'not_found':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That recipe could not be found.');
        return;

      case 'unavailable':
        // The key is deliberately kept: this attempt may have reached the server, and a new key would buy a
        // second answer to the same question.
        this.refusalSignal.set(COULD_NOT_REACH_SERVER);
        return;
    }
  }

  /** Clears the result so another adaptation can be asked for. Nothing about the recipe is touched. */
  startAnother(): void {
    this.requestIdSignal.set(null);
    this.refusalSignal.set(null);
    this.fieldErrorsSignal.set({});
    this.pendingRequestKey = null;
  }

  onDecided(result: unknown): void {
    this.decided.emit(result);
  }
}
