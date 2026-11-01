import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpNoticeComponent } from '@creator-pantry/ui';

import { WorkflowRun } from '../../../models/workflow-run.models';
import { BrandStyleGuideActiveReadOutcome, BrandStyleGuideService } from '../../../services/brand-style-guide.service';
import { HANDOFF_ROUTES } from '../../../shared/use-this-in/handoff-destinations';
import { workflowRunQuery } from '../../../shared/workflow-shell/workflow-return';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { SocialStudioComponent } from '../../social-studio/social-studio.component';
import { SocialStudioHost, SocialStudioPart } from '../../social-studio/social-studio-host';
import { contextPointer, newestLinked, runContextId } from '../creative-work/creative-work-run';

/**
 * The first three steps of "Create social posts" (13A.20) — what the posts are about, writing and choosing them, and
 * a picture to go with them — each one part of the Social Studio, hosted ({@link SocialStudioHost}).
 *
 * <p><strong>Orchestration only.</strong> Every field, the posts run with its channel limits, its stale and
 * needs-review states, and each channel's own keep, edit, decline and rewrite are the studio's, so posts written here
 * and in the studio are one set on one piece of work. Nothing is kept unless the creator keeps it, and nothing here
 * schedules or posts.</p>
 *
 * <p><strong>The guide is said, not asked.</strong> The first step names the brand guide the posts are written in,
 * as the server reads it, and links to where it is changed.</p>
 */
@Component({
  selector: 'cp-social-studio-step',
  standalone: true,
  imports: [RouterLink, CpNoticeComponent, SocialStudioComponent],
  providers: [SocialStudioHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (configured()) {
      @if (missingWork()) {
        <cp-notice role="note">
          This step works on what you chose in the first step, which isn’t there yet. Go back to the first step to say
          what the posts are about.
        </cp-notice>
      } @else {
        @if (part() === 'about') {
          @switch (guide().status) {
            @case ('found') {
              <cp-notice role="note">
                Your posts are written in your brand guide, “{{ voiceName() }}”, version {{ voiceVersion() }}, with its
                advice for social posts.
                <a [routerLink]="guideLink()">Change it in your guide</a>
              </cp-notice>
            }
            @case ('none') {
              <cp-notice role="note">
                You haven’t switched on a brand guide, so your posts follow your brand’s basics.
                <a [routerLink]="['/', workspaceSlug(), 'brand']">Set up your brand</a>
              </cp-notice>
            }
            @case ('unavailable') {
              <cp-notice role="status">
                Your brand guide couldn’t be read just now. Your posts are written in it all the same.
              </cp-notice>
            }
          }
        }

        <cp-social-studio />

        @if (keepFailed()) {
          <cp-notice tone="warning" role="status">
            Your work is saved, but this workflow couldn’t be pointed at it just now, so coming back may not find it
            here. It is still in the Social Studio.
          </cp-notice>
        }

        <!-- The studio goes the same steps, so this opens it on the one in hand (AU.5.3). -->
        @if (contextId(); as id) {
          <p class="cp-muted elsewhere">
            <a [routerLink]="studioLink(id)" [queryParams]="returnQuery()">Open this in the Social Studio</a>
          </p>
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .elsewhere {
      margin: 0;
    }
  `,
})
export class SocialStudioStepComponent implements OnInit {
  readonly part = input.required<SocialStudioPart>();
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly host = inject(SocialStudioHost);
  private readonly port = inject(WorkflowStepPort);
  private readonly guides = inject(BrandStyleGuideService);
  private readonly studio = viewChild(SocialStudioComponent);

  protected readonly configured = signal(false);
  protected readonly contextId = signal<string | null>(null);
  protected readonly keepFailed = signal(false);
  protected readonly guide = signal<BrandStyleGuideActiveReadOutcome | { readonly status: 'loading' }>({
    status: 'loading',
  });

  protected readonly missingWork = computed(() => this.part() !== 'about' && this.contextId() === null);
  /** The way back to this run, and the studio's step that is this one: the parts share their names. */
  protected readonly returnQuery = computed(() => ({ ...workflowRunQuery(this.run().id), step: this.part() }));

  protected readonly voiceName = computed(() => {
    const guide = this.guide();
    return guide.status === 'found' ? guide.voice.displayName : '';
  });

  protected readonly voiceVersion = computed(() => {
    const guide = this.guide();
    return guide.status === 'found' ? guide.voice.versionNumber : 0;
  });

  protected readonly guideLink = computed(() => {
    const guide = this.guide();
    return guide.status === 'found' ? ['/', this.workspaceSlug(), 'brand', 'style-guide', guide.voice.guideId] : [];
  });

  private keeping: Promise<boolean> = Promise.resolve(true);
  private gone = false;

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    const unregister = this.port.register({
      // A post's edited words are kept on this device until they are saved, and the shell says "Not saved yet."
      unsaved: computed(() => this.part() === 'write' && (this.studio()?.hasUnsavedWords() ?? false)),
      saving: computed(() => this.studio()?.sending() ?? false),
      flush: () => this.flush(),
      // The same keep, on the shell's timer: it asks nothing and is safe to repeat.
      autosave: () => this.flush(),
      // Only the first part keeps its work in a draft: the later parts' actions take none, and the run already
      // links the piece of work the first part made.
      draft: computed(() => {
        const id = this.contextId();
        return id === null || this.part() !== 'about' ? null : contextPointer(id);
      }),
      results: computed(() => {
        const id = this.contextId();
        return this.part() === 'about' && id !== null ? [contextPointer(id)] : [];
      }),
      ready: computed(() => {
        switch (this.part()) {
          case 'about':
            return this.contextId() !== null;
          case 'write':
            return this.studio()?.anyPostKept() ?? false;
          default:
            return true;
        }
      }),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    const run = this.run();
    const contextId = runContextId(run);

    this.contextId.set(contextId);
    this.host.part = this.part();
    this.host.contextId = contextId;
    this.host.suggestedRecipeId = this.part() === 'about' ? newestLinked(run, 'Recipe') : null;
    this.host.filed = (id) => this.filed(id);
    if (this.part() === 'about') void this.readGuide(this.workspaceSlug());

    this.configured.set(true);
  }

  protected studioLink(contextId: string): readonly string[] {
    return ['/', this.workspaceSlug(), ...HANDOFF_ROUTES.socialStudio(contextId)];
  }

  private filed(contextId: string): void {
    this.contextId.set(contextId);
    this.keepFailed.set(false);
    this.keeping = this.port.keepDraft(contextPointer(contextId)).then((kept) => {
      if (!this.gone) this.keepFailed.set(!kept);
      return kept;
    });
  }

  private async flush(): Promise<boolean> {
    const sent = (await this.studio()?.flushWork()) ?? true;
    await this.keeping;
    return sent;
  }

  private async readGuide(slug: string): Promise<void> {
    const outcome = await this.guides.getActive(slug);
    if (!this.gone) this.guide.set(outcome);
  }
}
