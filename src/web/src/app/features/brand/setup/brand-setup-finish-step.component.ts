import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { CpButtonComponent, CpFieldComponent, CpFormSectionComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../../core/confirm.service';
import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BRAND_STYLE_GUIDE_NAME_MAX } from '../../../models/brand-style-guide.models';
import { BrandStyleGuideFailure, BrandStyleGuideService } from '../../../services/brand-style-guide.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import {
  FAILURE_COPY,
  FinishState,
  canRetry,
  decodeFinishState,
  guidePayload,
  sameFinishState,
  writtenParts,
} from './brand-setup-finish';

/** What the creator has settled, as the step talks about it. */
type Outcome = 'undecided' | 'kept' | 'activated';

/**
 * Step 7 of "Create my voice": what the guide will govern, and the one decision left — switch it on, or keep
 * it as a draft.
 *
 * **Nothing happens without the creator pressing something.** Arriving here writes nothing. Each action is
 * explicit, the switch-on is confirmed in a dialog that says what it will govern, and neither action publishes
 * content or touches a recipe.
 *
 * **Switching on is three writes behind one confirmation**, because the server keeps them as three decisions:
 * the guide is written, its version is marked finished, and only then does it become the workspace default.
 * Each carries its own idempotency key, kept in the creator's draft, so a dropped response followed by a retry
 * continues the first attempt rather than writing a second guide.
 *
 * **An Owner switches it on.** The wizard admits Editors and Contributors, and activation is the Owner's
 * decision by server policy, so anyone else is offered the draft and told plainly who can switch it over —
 * never a button that would refuse them.
 */
@Component({
  selector: 'cp-brand-setup-finish-step',
  standalone: true,
  imports: [CpButtonComponent, CpFieldComponent, CpFormSectionComponent, CpNoticeComponent],
  templateUrl: './brand-setup-finish-step.component.html',
  styleUrl: './brand-setup-finish-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupFinishStepComponent implements OnInit {
  readonly step = input.required<BrandSetupStepDefinition>();
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  /** The first step's saved slice: which channels the creator shares on, so the parts match the editor's. */
  readonly goals = input<Readonly<Record<string, unknown>> | null>(null);
  /** The editor's saved slice: the guide itself. */
  readonly guide = input<Readonly<Record<string, unknown>> | null>(null);
  readonly workspaceSlug = input.required<string>();
  readonly reported = output<BrandSetupStepReport>();

  private readonly guides = inject(BrandStyleGuideService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirmService = inject(ConfirmService);

  protected readonly nameMax = BRAND_STYLE_GUIDE_NAME_MAX;
  protected readonly state = signal<FinishState>(decodeFinishState(null));
  protected readonly busy = signal<'' | 'activate' | 'keep'>('');
  protected readonly problem = signal('');
  protected readonly retryable = signal(false);
  protected readonly announcement = signal('');

  private baseline: FinishState = decodeFinishState(null);
  private ready = false;

  protected readonly parts = computed(() => writtenParts(this.goals(), this.guide()));

  protected readonly outcome = computed<Outcome>(() => {
    const state = this.state();
    if (state.activated) return 'activated';
    return state.guideId === null ? 'undecided' : 'kept';
  });

  /** Activation is the Owner's decision. Unknown membership is not an Owner, which only hides an action. */
  protected readonly isOwner = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;
    return state.memberships.find((each) => each.workspaceSlug === this.workspaceSlug())?.role === 'Owner';
  });

  constructor() {
    effect(() => {
      const state = this.state();
      const outcome = this.outcome();
      if (!this.ready) return;

      this.reported.emit({
        // Decided, either way. Finishing setup before deciding would leave the guide nowhere.
        canContinue: outcome !== 'undecided',
        isDirty: !sameFinishState(state, this.baseline),
        draft: { ...state },
      });
    });
  }

  ngOnInit(): void {
    const saved = this.draft();
    this.baseline = decodeFinishState(saved !== null && Object.keys(saved).length > 0 ? saved : null);
    this.state.set(this.baseline);
    this.ready = true;
    void this.memberships.ensureLoaded();
  }

  protected setName(event: Event): void {
    const name = (event.target as HTMLInputElement).value;
    this.state.update((state) => ({ ...state, name }));
  }

  // ---- The decision ----

  protected async keepAsDraft(): Promise<void> {
    if (this.busy() !== '') return;
    this.busy.set('keep');
    this.clearProblem();

    if (await this.ensureGuide()) {
      this.announce('Your guide is saved as a draft. Nothing is switched on.');
    }
    this.busy.set('');
  }

  protected async activate(): Promise<void> {
    if (this.busy() !== '' || !this.isOwner()) return;

    const agreed = await this.confirmService.confirm({
      title: 'Switch your voice on?',
      message:
        `"${this.state().name.trim() || 'Your guide'}" becomes what this workspace writes with: every new blog post, ` +
        'caption and newsletter starts from it. Nothing you have already written changes, and you can switch to ' +
        'another voice later.',
      confirmLabel: 'Switch it on',
      cancelLabel: 'Not yet',
      tone: 'neutral',
    });
    if (!agreed) return;

    this.busy.set('activate');
    this.clearProblem();

    if ((await this.ensureGuide()) && (await this.ensureApproved()) && (await this.ensureActivated())) {
      this.announce('Your voice is switched on.');
    }
    this.busy.set('');
  }

  protected async retry(): Promise<void> {
    // The same journey, from wherever it stopped: every step that succeeded is remembered and skipped.
    if (this.state().approved || this.state().activated) {
      await this.activate();
      return;
    }
    await this.keepAsDraft();
  }

  // ---- The three writes, each done at most once ----

  private async ensureGuide(): Promise<boolean> {
    const state = this.state();
    if (state.guideId !== null) return true;

    if (this.parts().length === 0) {
      this.fail('empty');
      return false;
    }

    const outcome = await this.guides.create(
      this.workspaceSlug(),
      guidePayload(state.name, this.goals(), this.guide()),
      state.createKey,
    );

    if (outcome.status !== 'created') {
      this.fail(outcome.reason);
      return false;
    }

    this.state.update((current) => ({
      ...current,
      guideId: outcome.guide.guideId,
      versionNumber: outcome.guide.versionNumber,
    }));

    return true;
  }

  private async ensureApproved(): Promise<boolean> {
    const state = this.state();
    if (state.approved) return true;
    if (state.guideId === null || state.versionNumber === null) return false;

    const outcome = await this.guides.approve(
      this.workspaceSlug(),
      state.guideId,
      state.versionNumber,
      null,
      state.approveKey,
    );

    if (outcome.status !== 'approved') {
      this.fail(outcome.reason);
      return false;
    }

    this.state.update((current) => ({ ...current, approved: true }));

    return true;
  }

  private async ensureActivated(): Promise<boolean> {
    const state = this.state();
    if (state.activated) return true;
    if (state.guideId === null || state.versionNumber === null) return false;

    // Null is the claim that this workspace has no voice yet, which is the wizard's own premise. The server
    // checks it rather than assuming, so a workspace that already has one is refused and said so — rather
    // than this quietly replacing a voice the creator never saw.
    const outcome = await this.guides.activate(
      this.workspaceSlug(),
      state.guideId,
      state.versionNumber,
      null,
      state.activateKey,
    );

    if (outcome.status !== 'activated') {
      this.fail(outcome.reason);
      return false;
    }

    this.state.update((current) => ({ ...current, activated: true }));

    return true;
  }

  // ---- Telling the creator ----

  private fail(reason: BrandStyleGuideFailure): void {
    this.problem.set(FAILURE_COPY[reason]);
    this.retryable.set(canRetry(reason));
    this.announce(FAILURE_COPY[reason]);
  }

  private clearProblem(): void {
    this.problem.set('');
    this.retryable.set(false);
  }

  /** Clears first so the same sentence twice in a row is still announced. */
  private announce(message: string): void {
    this.announcement.set('');
    queueMicrotask(() => this.announcement.set(message));
  }
}
