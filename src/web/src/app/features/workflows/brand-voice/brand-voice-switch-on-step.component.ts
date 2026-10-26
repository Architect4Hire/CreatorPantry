import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

import { BrandStyleGuideDetail, BrandStyleGuideVersionRow } from '../../../models/brand-style-guide.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { BrandStyleGuideService, BrandStyleGuideWorkspaceVoice } from '../../../services/brand-style-guide.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { BrandGuideActivateComponent } from '../../brand/brand-guide-activate.component';
import { NO_GUIDE_YET, linkedGuideId } from './brand-voice-guide';

/** How many pages of the guide's history are read looking for a version that can be switched on. */
const MAX_VERSION_PAGES = 5;

type Read =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'unavailable' }
  | {
      readonly status: 'ready';
      readonly guide: BrandStyleGuideDetail;
      /** What the workspace writes with now — this guide, another, or nothing. */
      readonly current: BrandStyleGuideWorkspaceVoice | null;
      /** The newest version that can be switched on, or null for none. */
      readonly target: BrandStyleGuideVersionRow | null;
    };

/**
 * "Switch it on", the last step of "Create my brand voice" (13A.17): the guide history's own switch-on dialog, for the
 * newest version of the guide that can be — which asks the Owner to confirm, and records why. Never implicit: nothing
 * is switched on until the Owner says so there, and the step is done only when the server reads the guide as the one
 * switched on.
 *
 * <p>It reads what the workspace writes with now before it offers anything, so it can say what switching on would
 * replace, and quote that on the request — which the server checks, refusing rather than replacing a voice nobody
 * saw. Anyone but the Owner is told who decides.</p>
 */
@Component({
  selector: 'cp-brand-voice-switch-on-step',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, BrandGuideActivateComponent, CpSkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{
      read().status === 'loading'
        ? 'Reading your guide…'
        : read().status === 'ready' && !isOn() && role() === 'loading'
          ? 'Checking who can switch it on…'
          : ''
    }}</p>
    @switch (read().status) {
      @case ('loading') {
        <!-- The guide arriving, as a few lines. -->
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('missing') {
        <cp-notice tone="warning" role="status">{{ noGuide }}</cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your guide couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (ready(); as state) {
          @if (isOn()) {
            <cp-notice tone="success" role="status">“{{ state.guide.displayName }}” is switched on. Continue to finish.</cp-notice>
          } @else if (role() === 'loading') {
            <!-- Where what the creator can do will be, while who they are here is read; said by the status above. -->
            <div aria-busy="true"><cp-skeleton shape="line" [count]="2" /></div>
          } @else if (role() === 'unknown') {
            <cp-notice tone="error" role="alert">
              We couldn’t check who can switch it on just now.
              <button cpButton type="button" variant="secondary" size="sm" (click)="retryRole()">Try again</button>
            </cp-notice>
          } @else if (role() !== 'owner') {
            <p class="plain">The workspace Owner switches a voice on. You can Save and exit — it will be here for them.</p>
          } @else if (state.guide.isArchived) {
            <cp-notice tone="warning" role="status">“{{ state.guide.displayName }}” is archived, so it can’t be switched on.</cp-notice>
          } @else if (state.target; as version) {
            <p class="plain">
              Switching on makes version {{ version.versionNumber }} of “{{ state.guide.displayName }}” this workspace’s voice.
              @if (state.current; as current) {
                It replaces “{{ current.displayName }}”, which is kept as it is, and you can switch back later.
              } @else {
                You can switch to another one later, and nothing is removed.
              }
            </p>
            <div class="cp-actions">
              <button #switch cpButton type="button" variant="primary" (click)="confirming.set(true)">Switch it on</button>
            </div>
          } @else {
            <cp-notice tone="warning" role="status">
              “{{ state.guide.displayName }}” hasn’t been approved yet, and only an approved version can be switched on.
            </cp-notice>
          }

          @if (confirming() && state.target; as version) {
            <cp-brand-guide-activate
              [workspaceSlug]="workspaceSlug()"
              [guideId]="state.guide.id"
              [version]="version"
              [expectedActiveVersionId]="state.current?.versionId ?? null"
              [expectedActiveVersionNumber]="state.current?.versionNumber ?? null"
              (activated)="onActivated()"
              (reloadRequested)="onReloadRequested()"
              (closed)="closeConfirm()"
            />
          }
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
export class BrandVoiceSwitchOnStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly guides = inject(BrandStyleGuideService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly port = inject(WorkflowStepPort);
  private readonly injector = inject(Injector);
  private readonly switchButton = viewChild<ElementRef<HTMLButtonElement>>('switch');

  protected readonly read = signal<Read>({ status: 'loading' });
  protected readonly noGuide = NO_GUIDE_YET;
  protected readonly confirming = signal(false);

  protected readonly ready = computed(() => {
    const read = this.read();
    return read.status === 'ready' ? read : null;
  });

  /** The server's word: this guide holds the workspace's default version. */
  protected readonly isOn = computed(() => this.ready()?.guide.activeVersionId != null);

  /**
   * Whether the reader is the Owner, who alone switches a voice on (11A) — or not yet known. The server is the
   * authority; this only chooses what to say, and never says "someone else" before it knows.
   */
  protected readonly role = computed<'loading' | 'unknown' | 'owner' | 'other'>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unknown';
    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role === 'Owner' ? 'owner' : 'other';
  });

  constructor() {
    // The switch-on is recorded by its own dialog; the step holds nothing of its own.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      ready: this.isOn,
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.memberships.ensureLoaded();
    void this.load();
  }

  async load(): Promise<void> {
    this.confirming.set(false);
    this.read.set({ status: 'loading' });

    const id = linkedGuideId(this.run());
    if (id === null) {
      this.read.set({ status: 'missing' });
      return;
    }

    const [detail, current] = await Promise.all([this.guides.getDetail(this.workspaceSlug(), id), this.guides.getActive(this.workspaceSlug())]);
    if (detail.status !== 'ok') {
      this.read.set({ status: detail.status === 'not_found' ? 'missing' : 'unavailable' });
      return;
    }
    if (current.status === 'unavailable') {
      this.read.set({ status: 'unavailable' });
      return;
    }

    const target = await this.newestUsable(id);
    if (target === undefined) {
      this.read.set({ status: 'unavailable' });
      return;
    }

    this.read.set({
      status: 'ready',
      guide: detail.guide,
      current: current.status === 'found' && current.voice.guideId !== id ? current.voice : null,
      target,
    });
  }

  protected retryRole(): void {
    void this.memberships.ensureLoaded();
  }

  protected closeConfirm(): void {
    this.confirming.set(false);
    this.switchButton()?.nativeElement.focus();
  }

  /** The dialog found the voice changed under it: read everything again, and put focus somewhere that is still there. */
  protected async onReloadRequested(): Promise<void> {
    await this.load();
    afterNextRender(
      () => {
        const button = this.switchButton()?.nativeElement;
        if (button) button.focus();
        else this.port.focusStep();
      },
      { injector: this.injector },
    );
  }

  protected async onActivated(): Promise<void> {
    await this.load();
    this.port.announce('Your voice is switched on. Continue to finish.');
    this.port.focusStep();
  }

  /**
   * The newest version that can be switched on — approved, citing nothing stale — the rule the guide history offers
   * its own switch-on by. Read across pages, newest first; undefined when the history could not be read.
   */
  private async newestUsable(guideId: string): Promise<BrandStyleGuideVersionRow | null | undefined> {
    let cursor: string | null = null;
    for (let page = 0; page < MAX_VERSION_PAGES; page++) {
      const outcome = await this.guides.listVersions(this.workspaceSlug(), guideId, cursor);
      if (outcome.status !== 'ok') return undefined;
      const usable = outcome.page.items.find((row) => row.status === 'Approved' && row.staleSourceCount === 0);
      if (usable) return usable;
      cursor = outcome.page.nextCursor;
      if (cursor === null) return null;
    }
    return null;
  }
}
