import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import {
  CpButtonComponent,
  CpPageHeadComponent,
  CpProgressComponent,
  CpStep,
  CpStepperComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { injectRouteTitle } from '../../core/route-title';
import { ShellNavIconComponent } from '../../shared/nav-icon/shell-nav-icon.component';
import { CONTEXT_ROUTE_PARAM } from '../../shared/use-this-in/handoff-destinations';
import { RecipeConceptStudioComponent } from './recipe-concept-studio.component';
import { RecipeFirstDraftReviewComponent } from './recipe-first-draft-review.component';
import { RecipeStudioHost } from './recipe-studio-host';

/** The studio's steps, in order. */
export type RecipeStudioStep = 'brief' | 'concepts' | 'draft';

const STEPS: readonly { readonly step: RecipeStudioStep; readonly label: string; readonly help: string }[] = [
  {
    step: 'brief',
    label: 'Describe what you’re after',
    help: 'Say what kind of dish you want. Ask, and a few different takes on it come back on the next step.',
  },
  {
    step: 'concepts',
    label: 'Choose a concept',
    help: 'Compare the concepts side by side and choose one to draft. Each is a proposal; nothing is saved until you keep a draft.',
  },
  {
    step: 'draft',
    label: 'Review the first draft',
    help: 'Read the draft and change any of it. It becomes a recipe only when you keep it.',
  },
];

/** The step card's heading, which takes focus when the step changes. */
const STEP_HEADING_ID = 'cp-recipe-studio-step-heading';

function isStep(value: string | null): value is RecipeStudioStep {
  return STEPS.some((each) => each.step === value);
}

function indexOf(step: RecipeStudioStep): number {
  return STEPS.findIndex((each) => each.step === step);
}

/**
 * The AI Recipe Studio's own page, as a flow (AU.5.2): describe what you're after, choose a concept, review the
 * first draft — one step at a time, the shape the "Create a recipe" workflow and the Image Studio have.
 *
 * **The same two screens the workflow hosts.** The ideas screen and the first-draft review are hosted here through
 * {@link RecipeStudioHost}, as "Start your recipe" hosts them, so the requests, the review and the one acceptance
 * that creates a recipe are the same work wherever it is started. One ideas screen stays for the first two steps,
 * so going back to the brief finds it as it was left; the host says which part of it shows.
 *
 * **The place is in the address.** `?step=`, the ideas request as `?request=` (the studio's own name for it, so an
 * old link still opens it) and the draft request as `?draft=`, each written with `replaceUrl`, so a refresh opens
 * the same step on the same requests. An address with requests and no step opens on the furthest of them.
 *
 * **Moving on is the screens' own actions.** Asking for concepts moves to the second step once the request is
 * taken, and asking for a draft to the third. Continue is offered only where the next step has something to show;
 * Back always, and leaving the review asks first when the creator's rewrites would be lost.
 */
@Component({
  selector: 'cp-recipe-studio-flow',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpPageHeadComponent,
    CpProgressComponent,
    CpStepperComponent,
    CpTopicCardComponent,
    RecipeConceptStudioComponent,
    RecipeFirstDraftReviewComponent,
    ShellNavIconComponent,
  ],
  providers: [RecipeStudioHost],
  templateUrl: './recipe-studio-flow.component.html',
  styleUrl: './recipe-studio-flow.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeStudioFlowComponent {
  /** The page's name, from its own route: what `cp-page-head` prints as the one `h1`. */
  protected readonly pageTitle = injectRouteTitle('AI Recipe Studio');
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly host = inject(RecipeStudioHost);

  private readonly review = viewChild(RecipeFirstDraftReviewComponent);

  private readonly conceptRequestId = signal<string | null>(null);
  private readonly draftRequestId = signal<string | null>(null);

  protected readonly step = signal<RecipeStudioStep>('brief');
  /** What a step change did, said in the page's polite region: focus reads the step's name, not its place. */
  protected readonly announcement = signal('');
  /** The furthest step reached on this visit, which is what the stepper calls done. */
  private readonly furthest = signal(0);

  protected readonly stepHeadingId = STEP_HEADING_ID;
  protected readonly stepIndex = computed(() => indexOf(this.step()));
  protected readonly currentStep = computed(() => STEPS[this.stepIndex()]);
  protected readonly isFirst = computed(() => this.stepIndex() === 0);
  protected readonly progressText = computed(() => `Step ${this.stepIndex() + 1} of ${STEPS.length}`);
  protected readonly percent = computed(() => Math.round(((this.stepIndex() + 1) / STEPS.length) * 100));

  /** Continue only where the next step has something to show: ideas asked for, or a draft asked for. */
  protected readonly canContinue = computed(() => {
    switch (this.step()) {
      case 'brief':
        return this.conceptRequestId() !== null;
      case 'concepts':
        return this.draftRequestId() !== null;
      default:
        return false;
    }
  });

  /** The steps as `cp-stepper` takes them: it owns the word and the glyph for each state. */
  protected readonly stepperSteps = computed<readonly CpStep[]>(() =>
    STEPS.map((each, index) => ({
      id: each.step,
      label: each.label,
      status: index === this.stepIndex() ? 'current' : index < this.furthest() ? 'done' : 'upcoming',
    })),
  );

  constructor() {
    const query = this.route.snapshot.queryParamMap;
    const conceptId = query.get('request') || null;
    const draftId = query.get('draft') || null;
    const named = query.get('step');
    // A legacy `/draft?request=` address names the draft by `request`.
    const legacyDraft = this.route.snapshot.data['legacyDraft'] === true;

    this.conceptRequestId.set(legacyDraft ? null : conceptId);
    this.draftRequestId.set(legacyDraft ? conceptId : draftId);

    const opening: RecipeStudioStep = isStep(named)
      ? named
      : this.draftRequestId() !== null
        ? 'draft'
        : this.conceptRequestId() !== null
          ? 'concepts'
          : 'brief';
    this.step.set(opening);
    this.furthest.set(indexOf(opening));

    this.host.workspaceSlug = this.resolveWorkspaceSlug();
    this.host.contextId = this.route.snapshot.paramMap.get(CONTEXT_ROUTE_PARAM);
    this.host.showMaker = true;
    this.host.conceptRequestId = this.conceptRequestId();
    this.host.draftRequestId = this.draftRequestId();
    this.host.part.set(opening === 'concepts' ? 'concepts' : 'brief');

    this.host.conceptsRequested = (requestId) => {
      const fresh = requestId !== this.conceptRequestId();
      this.conceptRequestId.set(requestId);
      this.host.conceptRequestId = requestId;
      // Asking moves on; resuming the request already on screen stays where it is.
      if (fresh && this.step() === 'brief') this.goTo('concepts');
      else this.writeAddress();
    };
    this.host.briefRestarted = () => {
      this.conceptRequestId.set(null);
      this.draftRequestId.set(null);
      this.host.conceptRequestId = null;
      this.host.draftRequestId = null;
      this.furthest.set(0);
      this.goTo('brief');
    };
    this.host.draftRequested = (requestId) => {
      this.draftRequestId.set(requestId);
      this.host.draftRequestId = requestId;
      this.goTo('draft');
    };
    this.host.draftClosed = () => this.leaveDraft();
    this.host.draftDiscarded = () => this.leaveDraft();
    this.host.accepted = (recipeId) => {
      void this.router.navigate(['/', this.host.workspaceSlug, 'recipes', recipeId]);
    };

    // An old `/draft` address moves to the studio's own, so there is one address for the studio.
    if (legacyDraft) this.writeAddress();
  }

  /**
   * For the route's leave guard: the review's rewrites have nowhere to be saved, so leaving the page from the
   * review asks first, as the review's own page did.
   */
  confirmLeaveIfUnsaved(): Promise<boolean> {
    return this.review()?.confirmDiscardIfDirty() ?? Promise.resolve(true);
  }

  protected async back(): Promise<void> {
    if (this.isFirst()) return;
    if (this.step() === 'draft' && !(await this.confirmLeaveIfUnsaved())) return;
    this.goTo(STEPS[this.stepIndex() - 1].step);
  }

  protected continue(): void {
    if (this.canContinue()) this.goTo(STEPS[this.stepIndex() + 1].step);
  }

  /** Off the review, back to the concepts: the draft was discarded, or it could not be found. */
  private leaveDraft(): void {
    this.draftRequestId.set(null);
    this.host.draftRequestId = null;
    this.goTo('concepts');
  }

  /**
   * Show one step: tell the ideas screen which part to show, say so in the address, and move focus to the step's
   * heading, so a keyboard or screen-reader user arrives on the new step rather than where the button was.
   */
  private goTo(step: RecipeStudioStep): void {
    this.step.set(step);
    this.furthest.update((value) => Math.max(value, indexOf(step)));
    if (step !== 'draft') this.host.part.set(step);
    this.writeAddress();
    this.announcement.set(`${this.progressText()}: ${this.currentStep().label}.`);
    afterNextRender(() => document.getElementById(STEP_HEADING_ID)?.focus(), { injector: this.injector });
  }

  /** Written with `replaceUrl`, so the page resumes after a refresh without cluttering the browser's history. */
  private writeAddress(): void {
    const base = this.host.contextId
      ? ['/', this.host.workspaceSlug, 'ai-recipe-studio', 'context', this.host.contextId]
      : ['/', this.host.workspaceSlug, 'ai-recipe-studio'];
    void this.router.navigate(base, {
      replaceUrl: true,
      queryParams: {
        step: this.step() === 'brief' ? null : this.step(),
        request: this.conceptRequestId(),
        draft: this.draftRequestId(),
      },
    });
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }
    return '';
  }
}
