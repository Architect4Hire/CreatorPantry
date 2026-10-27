import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

import {
  BRAND_SETUP_STEPS,
  BrandSetupSession,
  parseBrandSetupDraft,
  stepIndexOf,
} from '../../../models/brand-setup.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { BrandSetupSessionService } from '../../../services/brand-setup-session.service';
import { workflowRunQuery } from '../../../shared/workflow-shell/workflow-return';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { decodeFinishState } from '../../brand/setup/brand-setup-finish';

type Read =
  | { readonly status: 'loading' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'none' }
  | { readonly status: 'found'; readonly session: BrandSetupSession };

/**
 * "Tell us how you sound", the first step of "Create my brand voice" (13A.17): the Phase 11A setup, carried rather
 * than copied. The setup is a guided flow of its own — seven steps, its own autosave — so this step does not hold
 * it: it reads where the setup has got to from the setup's own saved session and links straight there, and the
 * setup links back. Nothing here writes the setup, builds a guide or switches one on.
 *
 * <p>Once the setup has kept a guide, Continue names it, and the server checks it before the step is done.</p>
 */
@Component({
  selector: 'cp-brand-voice-setup-step',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpNoticeComponent, CpSkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{ read().status === 'loading' ? 'Finding your setup…' : '' }}</p>
    @switch (read().status) {
      @case ('loading') {
        <!-- Where the setup stands, arriving as a few lines. -->
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your setup couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('none') {
        <p class="plain"
          >A few short questions about what you make and how you write, then a guide built from your answers.</p
        >
        <div class="cp-actions">
          <a cpButton variant="primary" [routerLink]="setupLink('goals')" [queryParams]="returnQuery()"
            >Start the setup</a
          >
        </div>
      }
      @case ('found') {
        @if (guideId()) {
          <cp-notice tone="success" role="status">
            Your guide “{{ guideName() }}” is built.
            @if (switchedOn()) {
              It’s already switched on, so you can skip the next steps if you like.
            } @else {
              Next, add to it, read it, and try it before you switch it on.
            }
          </cp-notice>
        } @else if (finished()) {
          <cp-notice tone="warning" role="status">
            The setup finished without keeping a guide. Start it again to build one.
          </cp-notice>
          <div class="cp-actions">
            <a cpButton variant="primary" [routerLink]="setupLink('goals')" [queryParams]="returnQuery()"
              >Start the setup again</a
            >
          </div>
        } @else {
          <p class="plain">
            You’re on step {{ stepNumber() }} of {{ stepCount }}: <strong>{{ stepLabel() }}</strong
            >.
            @if (savedOn()) {
              Saved {{ savedOn() }}.
            }
          </p>
          <div class="cp-actions">
            <a cpButton variant="primary" [routerLink]="setupLink(currentStep())" [queryParams]="returnQuery()"
              >Carry on with the setup</a
            >
          </div>
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .plain {
      margin: 0;
    }
  `,
})
export class BrandVoiceSetupStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly sessions = inject(BrandSetupSessionService);

  protected readonly read = signal<Read>({ status: 'loading' });
  protected readonly stepCount = BRAND_SETUP_STEPS.length;

  private readonly session = computed(() => {
    const read = this.read();
    return read.status === 'found' ? read.session : null;
  });

  /** What the setup's last step kept: the guide, its name, and whether it was switched on there. */
  private readonly finish = computed(() => {
    const session = this.session();
    if (session === null) return null;
    const slice = parseBrandSetupDraft(session.draftJson)['finish'] ?? null;
    return decodeFinishState(slice);
  });

  protected readonly guideId = computed(() => this.finish()?.guideId ?? null);
  protected readonly guideName = computed(() => this.finish()?.name ?? 'Your guide');
  protected readonly switchedOn = computed(() => this.finish()?.activated ?? false);
  protected readonly finished = computed(() => this.session()?.status === 'completed');
  protected readonly currentStep = computed(() => this.session()?.currentStep ?? 'goals');
  protected readonly stepNumber = computed(() => stepIndexOf(this.currentStep()) + 1);
  protected readonly stepLabel = computed(() => BRAND_SETUP_STEPS[Math.max(0, stepIndexOf(this.currentStep()))].label);

  protected readonly savedOn = computed(() => {
    const at = this.session()?.updatedUtc;
    return at ? new Date(at).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '';
  });

  constructor() {
    // The setup keeps its own work; this step holds none.
    const unregister = inject(WorkflowStepPort).register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      results: computed(() => {
        const id = this.guideId();
        return id ? [{ kind: 'BrandStyleGuide', id }] : [];
      }),
      ready: computed(() => this.guideId() !== null),
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.read.set({ status: 'loading' });
    const outcome = await this.sessions.getSession(this.workspaceSlug());
    this.read.set(
      outcome.status === 'found'
        ? { status: 'found', session: outcome.session }
        : outcome.status === 'none'
          ? { status: 'none' }
          : { status: 'unavailable' },
    );
  }

  protected setupLink(step: string): string[] {
    return ['/', this.workspaceSlug(), 'brand', 'setup', step];
  }

  protected returnQuery(): Record<string, string> {
    return workflowRunQuery(this.run().id);
  }
}
