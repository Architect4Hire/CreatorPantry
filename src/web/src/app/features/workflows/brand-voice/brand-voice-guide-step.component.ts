import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { BrandStyleGuideDetail } from '../../../models/brand-style-guide.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { BrandStyleGuideService } from '../../../services/brand-style-guide.service';
import { LoadedGuide, NO_GUIDE_YET, loadGuide } from './brand-voice-guide';
import { workflowRunQuery } from '../../../shared/workflow-shell/workflow-return';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';

/** Which page of the guide this step opens. */
export type BrandVoiceGuideStepMode = 'review' | 'try';

/**
 * "Read your guide and change anything" and "Try it on a real piece" in "Create my brand voice" (13A.17): the
 * guide's name and version, and a link straight to the Phase 11A page that does the step — the guide editor, or
 * Test my style on the guide's current version. Both pages link back here. Optional, and done on the creator's word.
 */
@Component({
  selector: 'cp-brand-voice-guide-step',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpNoticeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (loaded().status) {
      @case ('loading') {
        <p class="cp-muted" role="status">Reading your guide…</p>
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
        @if (guide(); as guide) {
          <p class="plain">
            <strong>{{ guide.displayName }}</strong>, version {{ guide.workingVersion.versionNumber }}.
            @if (mode() === 'review') {
              It’s your guide: change any word that doesn’t sound like you. Every version you save is kept.
            } @else {
              See a short piece written with this version before you decide. Nothing is saved to your recipes, posts or guide.
            }
          </p>
          <div class="cp-actions">
            <a cpButton variant="primary" [routerLink]="link(guide)" [queryParams]="query(guide)">
              {{ mode() === 'review' ? 'Open your guide' : 'Try it out' }}
            </a>
          </div>
          <p class="plain cp-muted">When you’re done there, come back here and carry on.</p>
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
export class BrandVoiceGuideStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();
  readonly mode = input.required<BrandVoiceGuideStepMode>();

  private readonly guides = inject(BrandStyleGuideService);

  protected readonly loaded = signal<LoadedGuide>({ status: 'loading' });
  protected readonly noGuide = NO_GUIDE_YET;

  protected readonly guide = computed(() => {
    const loaded = this.loaded();
    return loaded.status === 'ready' ? loaded.guide : null;
  });

  constructor() {
    // The guide's pages keep their own work; this step holds none.
    const unregister = inject(WorkflowStepPort).register({ unsaved: signal(false).asReadonly(), flush: () => Promise.resolve(true) });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loaded.set({ status: 'loading' });
    this.loaded.set(await loadGuide(this.guides, this.workspaceSlug(), this.run()));
  }

  protected link(guide: BrandStyleGuideDetail): string[] {
    const base = ['/', this.workspaceSlug(), 'brand', 'style-guide', guide.id];
    return this.mode() === 'review' ? base : [...base, 'test-drive'];
  }

  protected query(guide: BrandStyleGuideDetail): Record<string, string> {
    const back = workflowRunQuery(this.run().id);
    return this.mode() === 'review' ? back : { ...back, version: String(guide.workingVersion.versionNumber) };
  }
}
