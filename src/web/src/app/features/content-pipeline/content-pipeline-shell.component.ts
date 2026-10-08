import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { CpButtonComponent, CpCardComponent, CpNoticeComponent, CpProgressComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_PIPELINE_STEPS,
  CONTENT_PIPELINE_STEP_COUNT,
  CONTENT_PIPELINE_STEP_SLUGS,
  ContentPipelineConfig,
  ContentPipelineDraft,
  ContentPipelineStepSlug,
  FIRST_CONTENT_PIPELINE_STEP,
  STEP_STATUS_GLYPH,
  STEP_STATUS_WORD,
  contentPipelineStepIndex,
  contentPipelineStepStatus,
  emptyContentPipelineDraft,
  isContentPipelineDraftEmpty,
  isContentPipelineStepSlug,
} from '../../models/content-pipeline.models';
import {
  ContentPipelineDraftOwner,
  ContentPipelineDraftService,
} from '../../services/content-pipeline-draft.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { ContentPipelineIdeaStepComponent } from './content-pipeline-idea-step.component';
import { ContentPipelineImagesStepComponent } from './content-pipeline-images-step.component';
import { ContentPipelinePromptStepComponent } from './content-pipeline-prompt-step.component';
import { ContentPipelineSetupStepComponent } from './content-pipeline-setup-step.component';
import { ContentPipelineStepPlaceholderComponent } from './content-pipeline-step-placeholder.component';

/**
 * `loading` and `unavailable` are how reading the creator's memberships can end; `not_found` and `read_only`
 * are the two refusals; `resumable` offers a kept draft; `editing` is the journey itself.
 */
type Phase = 'loading' | 'unavailable' | 'not_found' | 'read_only' | 'resumable' | 'editing';

const HEADING_ID = 'cp-pipeline-step-heading';

/**
 * The Content Pipeline: the guided journey from an idea to finished posts (PIPE-UI-001/002).
 *
 * **It owns navigation and the kept draft; the steps own their fields.** A step is handed the draft and hands
 * back the next one, so there is exactly one copy of the creator's answers and what is on screen is always what
 * is kept.
 *
 * **Nothing here reaches the server except through a typed service.** These two steps write nothing at all: the
 * draft lives in the browser (`ContentPipelineDraftService`) because there is no run to save yet, and the only
 * call made is the one that suggests an idea.
 *
 * **The step list is the whole journey, including the four steps not built yet**, so "Step 2 of 6" is the truth
 * about the pipeline rather than about how much of it has shipped. A step that is not built offers no action.
 *
 * **Leaving costs nothing, so leaving is not confirmed.** Every change is kept as it is made, which is why
 * there is no unsaved-work guard here — unlike a form whose edits live only in memory. Starting over does ask,
 * because that is the one action that throws the draft away.
 *
 * **The workspace comes from the route and the creator's own membership.** An unknown workspace and one they
 * cannot reach answer alike, so neither discloses that the other exists (.claude/rules/tenancy.md).
 */
@Component({
  selector: 'cp-content-pipeline-shell',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpNoticeComponent,
    CpProgressComponent,
    ContentPipelineSetupStepComponent,
    ContentPipelineIdeaStepComponent,
    ContentPipelinePromptStepComponent,
    ContentPipelineImagesStepComponent,
    ContentPipelineStepPlaceholderComponent,
  ],
  templateUrl: './content-pipeline-shell.component.html',
  styleUrl: './content-pipeline-shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineShellComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly drafts = inject(ContentPipelineDraftService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirm = inject(ConfirmService);
  private readonly injector = inject(Injector);

  readonly steps = CONTENT_PIPELINE_STEPS;
  readonly total = CONTENT_PIPELINE_STEP_COUNT;

  readonly workspaceSlug = signal('');
  readonly draft = signal<ContentPipelineDraft>(emptyContentPipelineDraft());
  /** Null until the step in the URL has been checked against how far the creator has actually got. */
  readonly currentIndex = signal<number | null>(null);
  readonly announcement = signal('');
  /** True when something was stored here that could not be read, so the creator is told rather than puzzled. */
  readonly discarded = signal(false);

  /** False until this creator's kept draft for this workspace has been read. */
  private readonly loaded = signal(false);
  /** True while a kept draft is being offered, before the creator has said Continue or Start over. */
  private readonly resumeOffered = signal(false);

  private stepParam: string | null = null;
  /** The owner key whose draft is currently loaded, so the same one is not read twice. */
  private loadedKey: string | null = null;

  readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  /**
   * Whose pipeline this is: the workspace by id, and the membership reading it.
   *
   * Null until the creator's memberships have been read, which is why the draft cannot be loaded from the route
   * alone — the slug names a workspace, and a kept draft belongs to one *person* in one workspace.
   */
  readonly owner = computed<ContentPipelineDraftOwner | null>(() => {
    const membership = this.membership();

    return membership === null
      ? null
      : { workspaceId: membership.workspaceId, membershipId: membership.membershipId };
  });

  /**
   * One string for that owner, used to key the step on screen.
   *
   * A step is destroyed and built again when this changes, which is what keeps one workspace's idea — or one
   * colleague's — from staying on screen under another's name. Without it the shell swaps the draft underneath
   * child components that are still holding the previous one's state (.claude/rules/tenancy.md).
   */
  readonly ownerKey = computed(() => {
    const owner = this.owner();

    return owner === null ? '' : `${owner.workspaceId}.${owner.membershipId}`;
  });

  readonly currentStep = computed(() => {
    const index = this.currentIndex();
    return index === null ? null : CONTENT_PIPELINE_STEPS[index];
  });

  /** A list of one, so the template can name the step with `@for` and never index into it. */
  readonly currentStepList = computed(() => {
    const step = this.currentStep();
    return step === null ? [] : [step];
  });

  readonly currentSlug = computed<ContentPipelineStepSlug | null>(() => {
    const index = this.currentIndex();
    return index === null ? null : CONTENT_PIPELINE_STEP_SLUGS[index];
  });

  readonly rows = computed(() => {
    const current = this.currentSlug();
    const furthest = this.draft().furthestStep;

    return CONTENT_PIPELINE_STEPS.map((step) => {
      const status = contentPipelineStepStatus(step.slug, current, furthest);
      return { step, status, glyph: STEP_STATUS_GLYPH[status], word: STEP_STATUS_WORD[status] };
    });
  });

  readonly stepNumber = computed(() => (this.currentIndex() ?? 0) + 1);
  readonly progressText = computed(() => `Step ${this.stepNumber()} of ${this.total}`);
  readonly percent = computed(() => Math.round((this.stepNumber() / this.total) * 100));
  readonly isFirst = computed(() => this.currentIndex() === 0);

  /** True for a step whose body is not built: it has nothing to continue from. */
  readonly isComingSoon = computed(() => this.currentStep()?.comingSoon !== null);

  /**
   * Whether Continue is offered.
   *
   * Setup asks nothing required, so it always continues. The idea step continues once an idea has been picked —
   * which is what picking one is for. A step that is not built continues nowhere.
   */
  readonly canContinue = computed(() => {
    const slug = this.currentSlug();
    if (slug === null || this.isComingSoon()) return false;
    if (slug === 'idea') return this.draft().seed.accepted !== null;
    // A prompt is what this step is for, so carrying on means having one. The creator's own text counts: a
    // prompt they wrote themselves is as finished as one that was written for them.
    if (slug === 'prompt') return this.draft().prompt.finalPrompt.trim() !== '';
    // A picture is what the steps after this one are about, so one has to be kept. Pictures that came back and
    // were not chosen are not a decision, which is why the count rather than the run is what counts.
    if (slug === 'images') return this.draft().images.keepers.length > 0;

    return contentPipelineStepIndex(slug) < this.total - 1;
  });

  readonly savedText = computed(() => {
    const savedAt = this.draft().savedAt;
    const at = savedAt === '' ? null : new Date(savedAt);
    if (at === null || Number.isNaN(at.getTime())) return 'Your answers are kept on this device as you go.';

    const when = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(at);
    return `Kept on this device at ${when}.`;
  });

  readonly resumeStep = computed(() => CONTENT_PIPELINE_STEPS[contentPipelineStepIndex(this.draft().furthestStep)]);


  /**
   * Derived rather than set, so the screen cannot be left claiming a phase the data has moved past.
   *
   * **An unknown workspace and one this creator is not in answer alike**, because telling the two apart would
   * disclose that the other exists (.claude/rules/tenancy.md).
   *
   * **A Viewer is stopped at the door rather than led through.** The idea generator itself is readable by every
   * member, but the steps this journey ends in are not, and walking someone to a wall is worse than saying so.
   */
  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';

    const membership = this.membership();
    if (membership === null) return 'not_found';
    if (membership.role === 'Viewer') return 'read_only';
    if (!this.loaded()) return 'loading';

    return this.resumeOffered() ? 'resumable' : 'editing';
  });

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          const read = (key: string): string | null => {
            for (const params of maps) {
              const value = params.get(key);
              if (value) return value;
            }
            return null;
          };
          return { slug: read('workspaceSlug'), step: read('step') };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, step }) => {
        if (slug === null) throw new Error('ContentPipelineShellComponent route is missing a workspaceSlug segment.');
        this.stepParam = step;
        if (slug !== this.workspaceSlug()) this.closeWorkspace(slug);
        this.applyRoute();
      });

    // The draft is read once the memberships say who is asking, which is also what re-reads it when the
    // workspace in the route changes or a different person signs in on this browser.
    effect(() => {
      const owner = this.owner();
      if (owner === null || this.ownerKey() === this.loadedKey) return;

      this.loadedKey = this.ownerKey();
      this.open(owner);
    });
  }

  /** Every route node from this one up to the root, so a parameter can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }

  /**
   * Forget the workspace on screen, because the route now names another one.
   *
   * Everything read for the previous one goes, including the step index — nothing of one workspace may still be
   * showing while the next one's draft is being read.
   */
  private closeWorkspace(slug: string): void {
    this.workspaceSlug.set(slug);
    this.loadedKey = null;
    this.loaded.set(false);
    this.draft.set(emptyContentPipelineDraft());
    this.currentIndex.set(null);
    this.resumeOffered.set(false);
    this.discarded.set(false);
    this.announcement.set('');
  }

  /**
   * Read one creator's kept draft for one workspace.
   *
   * The read itself is synchronous, so there is no loading state of its own here — what is waited on is the
   * memberships that say whose draft to read.
   */
  private open(owner: ContentPipelineDraftOwner): void {
    const { draft: kept, discarded } = this.drafts.read(owner);
    this.discarded.set(discarded);

    if (kept === null) {
      this.draft.set(emptyContentPipelineDraft());
      this.resumeOffered.set(false);
    } else {
      this.draft.set(kept);
      // Offered only when there is something to come back to. A draft that is all defaults is not a resume.
      this.resumeOffered.set(!isContentPipelineDraftEmpty(kept));
    }

    this.loaded.set(true);
    this.applyRoute();
  }

  /**
   * Settle which step is on screen.
   *
   * A step nobody has reached yet, and a step slug this build does not know, both land on the furthest step
   * reached rather than on an error: the URL is navigation, and the remedy for a wrong one is to go somewhere
   * right.
   */
  private applyRoute(): void {
    // Nothing can be settled against a draft that has not been read: a step would be judged against the wrong
    // "furthest reached" and the creator would be sent back to the start of their own run.
    if (!this.loaded()) return;

    const furthest = this.draft().furthestStep;
    const furthestIndex = contentPipelineStepIndex(furthest);
    const asked = this.stepParam;

    if (!isContentPipelineStepSlug(asked) || contentPipelineStepIndex(asked) > furthestIndex) {
      if (asked !== furthest) {
        void this.router.navigate(this.routeFor(furthest), { replaceUrl: true });
        return;
      }
    }

    const index = contentPipelineStepIndex(asked);
    const settled = index === -1 ? furthestIndex : index;
    const previous = this.currentIndex();
    if (previous === settled) return;

    this.currentIndex.set(settled);
    this.focusHeading();

    // Announced on a *move*, not on arrival: the first step is announced by focus landing on its heading, and
    // a live region that already said something is what a screen reader has nothing else to tell it from.
    if (previous !== null) {
      this.announce(`${CONTENT_PIPELINE_STEPS[settled].label}. Step ${settled + 1} of ${this.total}.`);
    }
  }

  private routeFor(slug: ContentPipelineStepSlug): readonly unknown[] {
    return ['/', this.workspaceSlug(), 'workflows', 'content-pipeline', slug];
  }

  /**
   * Move focus to the step's heading after a step change.
   *
   * Without it a keyboard or screen-reader user stays wherever the Continue button was and has to find the new
   * step by hand; the heading is `tabindex="-1"` for exactly this and never enters the tab order.
   */
  private focusHeading(): void {
    afterNextRender(
      () => {
        const heading = document.getElementById(HEADING_ID);
        heading?.focus();
      },
      { injector: this.injector },
    );
  }

  private announce(sentence: string): void {
    this.announcement.set(sentence);
  }

  /** Keep the draft and write it through, so what is on screen and what is kept never differ. */
  private keep(next: ContentPipelineDraft): void {
    const owner = this.owner();
    if (owner === null) return;

    const stamped: ContentPipelineDraft = { ...next, savedAt: new Date().toISOString() };
    this.draft.set(stamped);
    this.drafts.write(owner, stamped);
  }

  protected onConfigChanged(config: ContentPipelineConfig): void {
    this.keep({ ...this.draft(), config });
  }

  protected onDraftChanged(next: ContentPipelineDraft): void {
    this.keep(next);
  }

  protected onAnnounced(sentence: string): void {
    this.announce(sentence);
  }

  protected resume(): void {
    this.resumeOffered.set(false);
    void this.router.navigate(this.routeFor(this.draft().furthestStep));
  }

  protected back(): void {
    const index = this.currentIndex();
    if (index === null || index === 0) return;

    void this.router.navigate(this.routeFor(CONTENT_PIPELINE_STEP_SLUGS[index - 1]));
  }

  /**
   * Forward one step, widening how far the creator has got.
   *
   * `furthestStep` only ever moves forward: coming back to an earlier step to change an answer must not take
   * away the steps already reached.
   */
  protected continue(): void {
    const index = this.currentIndex();
    if (index === null || !this.canContinue()) return;

    const next = CONTENT_PIPELINE_STEP_SLUGS[index + 1];
    const draft = this.draft();
    if (contentPipelineStepIndex(next) > contentPipelineStepIndex(draft.furthestStep)) {
      this.keep({ ...draft, furthestStep: next });
    }

    void this.router.navigate(this.routeFor(next));
  }

  /** Leave. Nothing has to be saved on the way out, because nothing was ever only on screen. */
  protected async saveAndExit(): Promise<void> {
    await this.router.navigate(['/', this.workspaceSlug(), 'workflows']);
  }

  /**
   * Throw the draft away and begin again.
   *
   * The one action here that loses work, so it is the one that asks — through `ConfirmService`, where Escape and
   * the backdrop both mean "stay" (.claude/rules/design-system.md).
   */
  protected async startOver(): Promise<void> {
    const discard = await this.confirm.confirm({
      title: 'Start this over?',
      message: 'Everything you have filled in for this pipeline will be thrown away, including the idea you picked.',
      confirmLabel: 'Throw it away',
      cancelLabel: 'Keep what I have',
    });
    if (!discard) return;

    const owner = this.owner();
    if (owner !== null) this.drafts.clear(owner);

    this.draft.set(emptyContentPipelineDraft());
    this.resumeOffered.set(false);
    this.discarded.set(false);
    this.announce('Started over.');

    const first = FIRST_CONTENT_PIPELINE_STEP;
    if (this.stepParam === first) this.applyRoute();
    else await this.router.navigate(this.routeFor(first));
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }
}
