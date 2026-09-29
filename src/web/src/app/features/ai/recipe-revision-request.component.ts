import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { WorkspaceRole } from '../../models/auth.models';
import { AiOperationScope } from '../../models/ai-proposal.models';
import {
  RECIPE_REVISION_SECTIONS,
  REVISION_GOAL_MAX_LENGTH,
  RecipeRevisionSection,
} from '../../models/recipe-revision.models';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { RecipeRevisionService, RequestRevisionOutcome } from '../../services/recipe-revision.service';
import { AiUsageService } from '../../services/ai-usage.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../shared/allowance-gate';
import { AiProposalPanelComponent } from './ai-proposal-panel.component';

/** Asking for a revision writes an operation, so it carries the same bar as editing the recipe by hand. */
const REQUEST_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

export type RevisionRequestState = 'choosing' | 'submitting' | 'watching' | 'forbidden';

const COULD_NOT_REACH_SERVER = "Couldn't reach the server. Try again in a moment.";

/**
 * Ask for an AIREC-003 scoped revision of one recipe: choose the section, say what you want, and review what
 * comes back.
 *
 * **The section is the bound, and the creator sets it before anything is generated.** The server records it
 * on the operation and refuses any proposed change that reaches outside it — a change to another section, or
 * to a field that section does not cover. This form offers no field list because there is none to offer:
 * which fields a section permits is the server's answer, and a request that could name them could widen the
 * bound it just asked for.
 *
 * **The goal is the creator's own words and is treated as untrusted throughout.** It reaches the model inside
 * a fenced preferences segment, never as part of the task's instructions.
 *
 * **Reviewing is `cp-ai-proposal-panel`'s job, not this component's.** A revision produces an ordinary
 * server-computed diff against the pinned version, which is exactly what that panel already reviews, edits
 * and decides — so this hands it the request id and gets out of the way. Building a second review surface
 * would mean a second opinion about what a creator is approving.
 */
@Component({
  selector: 'cp-recipe-revision-request',
  standalone: true,
  imports: [
    FormsModule,
    AiAllowanceNoticeComponent,
    AiProposalPanelComponent,
    CpButtonComponent,
    CpFieldComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-revision-request.component.html',
  styleUrl: './recipe-revision-request.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeRevisionRequestComponent {
  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The recipe's current version, which is the only one a revision may be asked against.
   *
   * Required and supplied by the host, which is the thing that just read the recipe. A component that fetched
   * it for itself could be a moment behind the editor beside it and pin a version the creator has already
   * moved past — the server refuses that, but late and after a wait.
   */
  readonly currentVersionId = input.required<string>();

  /**
   * Whether the editor hosting this pane has unsaved changes. The request is pinned to `currentVersionId`, so
   * anything still unsaved is not what the model reads — and a creator is told so rather than left to work it
   * out. The same input, and the same caveat, the recipe calculators in this tablist already carry.
   */
  readonly editorIsDirty = input(false);

  /** A revision was decided. Carries the server's reply, including the version it wrote. */
  readonly decided = output<unknown>();

  private readonly revisions = inject(RecipeRevisionService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly usage = inject(AiUsageService);

  readonly sections = RECIPE_REVISION_SECTIONS;
  readonly goalMaxLength = REVISION_GOAL_MAX_LENGTH;

  readonly scope = signal<AiOperationScope | null>(null);
  readonly goal = signal('');

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

  readonly state = computed<RevisionRequestState>(() => {
    if (this.rolesKnown() && !this.canRequest()) return 'forbidden';
    if (this.submitting()) return 'submitting';
    return this.requestIdSignal() === null ? 'choosing' : 'watching';
  });

  /** The account's allowance, so a spent one is said before it is hit rather than only after. */
  readonly allowance = this.usage.allowance;

  /** The server's refusal, once there has been one. Worded by the allowance notice. */
  private readonly quotaRefusalSignal = signal<AiQuotaRefusal | null>(null);
  readonly quotaRefusal = this.quotaRefusalSignal.asReadonly();

  /** A section must be chosen: there is no sensible default, and the widest one is the wrong guess. */
  readonly canSubmit = computed(
    () =>
      this.scope() !== null &&
      !this.submitting() &&
      this.canRequest() &&
      !allowanceBlocksNewRequests(this.allowance()),
  );

  isChosen(section: RecipeRevisionSection): boolean {
    return this.scope() === section.scope;
  }

  choose(section: RecipeRevisionSection): void {
    this.scope.set(section.scope);
    this.refusalSignal.set(null);
  }

  errorFor(field: string): string {
    return this.fieldErrorsSignal()[field]?.[0] ?? '';
  }

  async submit(): Promise<void> {
    const scope = this.scope();
    if (scope === null || !this.canSubmit()) return;

    this.submitting.set(true);
    this.refusalSignal.set(null);
    this.quotaRefusalSignal.set(null);
    this.fieldErrorsSignal.set({});

    // One key per attempt, reused only if this same attempt has to be re-sent after a transport failure —
    // so a retry returns the first answer rather than buying a second.
    this.pendingRequestKey ??= crypto.randomUUID();

    try {
      const outcome = await this.revisions.requestRevision(
        this.workspaceSlug(),
        this.recipeId(),
        { scope, sourceVersionId: this.currentVersionId(), goal: this.goal() },
        this.pendingRequestKey,
      );

      this.apply(outcome);
    } finally {
      this.submitting.set(false);
    }
  }

  private apply(outcome: RequestRevisionOutcome): void {
    switch (outcome.status) {
      case 'accepted':
        this.pendingRequestKey = null;
        this.requestIdSignal.set(outcome.operation.aiProposalRequestId);

        // The run has been paid for, so the balance on screen is already out of date.
        void this.usage.refresh();
        return;

      // The account cannot pay for this. Handed to the allowance notice, which is the one place that words a
      // spent allowance and a switched-off account differently. The key is released because this attempt
      // reached the server and was answered: a retry is a new request, not a re-send.
      case 'quota_exhausted':
      case 'account_suspended':
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
        this.refusalSignal.set('Recipe revision is not switched on for this workspace yet.');
        return;

      case 'source_version_invalid':
        this.pendingRequestKey = null;
        this.refusalSignal.set(
          'This recipe was saved while you were deciding what to ask for. Reload it and ask again, so the '
            + 'revision is against the version you are looking at.',
        );
        return;

      case 'idempotency_key_conflict':
        // A fresh key next time: this one already belongs to a different request.
        this.pendingRequestKey = null;
        this.refusalSignal.set('That request clashed with another one. Try again.');
        return;

      case 'forbidden':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Your role in this workspace cannot ask for a revision.');
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

  /** Clears the result so another revision can be asked for. Nothing about the recipe is touched. */
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
