import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

import { RecipeMoveOffer, roleAllows } from '../../../models/recipe-readiness.models';
import { RecipeDetail, RecipeStatus } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeReadinessComponent } from '../../recipes/recipe-readiness.component';
import { LoadedRecipe, loadRecipe } from './recipe-test-cook-step.component';
import { NO_RECIPE_YET } from './recipe-test-readiness';

/** Which move this step is about. */
export type RecipeReadinessStepMode = 'send-for-review' | 'approve';

/**
 * The forward moves only, each by where it starts: in a guided step nothing archives or reopens a recipe, and a reopen
 * leads to In development as a forward move does.
 */
const OFFERS: Readonly<Record<RecipeReadinessStepMode, readonly RecipeMoveOffer[]>> = {
  'send-for-review': [
    { from: 'Draft', to: 'InDevelopment' },
    { from: 'InDevelopment', to: 'Testing' },
    { from: 'Testing', to: 'ReadyForReview' },
  ],
  approve: [{ from: 'ReadyForReview', to: 'Approved' }],
};

/**
 * Where each step is done, by the status the recipe stands in — the server's completion rule, mirrored to say so and
 * to step Continue forward. Never proof: Continue asks the server.
 */
const DONE: Readonly<Record<RecipeReadinessStepMode, readonly RecipeStatus[]>> = {
  'send-for-review': ['ReadyForReview', 'Approved'],
  approve: ['Approved'],
};

/** How many of this step's moves are left from a status, walking them forward; null when none starts there. */
function movesLeft(offers: readonly RecipeMoveOffer[], from: RecipeStatus): number | null {
  let count = 0;
  let at: RecipeStatus = from;
  for (let move = offers.find((offer) => offer.from === at); move; move = offers.find((offer) => offer.from === at)) {
    count += 1;
    if (move.to === 'Archived') break;
    at = move.to;
  }
  return count > 0 ? count : null;
}

/**
 * "Send it for review" and "Approve it" in "Test and approve" (13A.16): the recipe page's own readiness panel —
 * what still blocks the recipe, each blocker explained, and the moves on — offering only this step's forward moves.
 *
 * <p><strong>The workflow never moves a status.</strong> Each move is the panel's own transition request, which quotes
 * the recipe's version and a reason where one is asked for; approving re-runs the readiness check on the server and is
 * refused while anything blocks (TESTRUN-004/005). The step is done when the server reads the new status back, so
 * nothing here marks a recipe approved, or waives a blocker, ahead of the domain.</p>
 *
 * <p>One move at a time: from Draft a recipe goes to In development, then Testing, then Ready for review, each an
 * explicit act the creator makes and the recipe records.</p>
 */
@Component({
  selector: 'cp-recipe-test-readiness-step',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpNoticeComponent, CpSkeletonComponent, RecipeReadinessComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, outside the busy lines, so the step says once what it is waiting for (AU.7). -->
    <p class="cp-sr-only" role="status">{{ loaded().status === 'loading' ? 'Reading your recipe…' : '' }}</p>
    @switch (loaded().status) {
      @case ('loading') {
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('missing') {
        <cp-notice tone="warning" role="status">{{ noRecipe }}</cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your recipe couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (ready(); as recipe) {
          @if (done()) {
            <cp-notice tone="success" role="status">{{ doneText() }}</cp-notice>
          } @else if (mode() === 'send-for-review' && movesLeft(recipe.status); as left) {
            <p class="plain">
              {{ left === 1 ? 'One more move and it’s ready for review.' : left + ' moves, one at a time, and it’s ready for review.' }}
              Anything in the way is listed first.
            </p>
          } @else if (mode() === 'approve' && recipe.status !== 'ReadyForReview') {
            <p class="plain">It needs to be sent for review before it can be approved. Go Back a step to do that.</p>
          } @else if (mode() === 'approve' && !mayApprove()) {
            <p class="plain">
              Someone with the Editor role gives the final yes. You can Save and exit — it will be here for them.
            </p>
          }

          <!-- In the page from the start, quiet until asked: a live region added with its text is not reliably heard. -->
          <cp-notice role="status" [quiet]="!evidenceAsked()">
            @if (evidenceAsked()) {
              That’s fixed on the recipe’s own page.
              <a [routerLink]="['/', workspaceSlug(), 'recipes', recipe.id]">Open the recipe</a>, then come back here and carry on.
            }
          </cp-notice>

          <cp-recipe-readiness
            [workspaceSlug]="workspaceSlug()"
            [recipeId]="recipe.id"
            [status]="recipe.status"
            [concurrencyToken]="recipe.concurrencyToken"
            [offer]="offer()"
            heading="What’s holding it back"
            [headingLevel]="3"
            (transitioned)="onTransitioned($event)"
            (evidenceActivated)="evidenceAsked.set(true)"
          />
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-5);
      min-width: 0;
    }

    .plain {
      margin: 0;
    }
  `,
})
export class RecipeTestReadinessStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();
  readonly mode = input.required<RecipeReadinessStepMode>();

  private readonly recipes = inject(RecipeService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly loaded = signal<LoadedRecipe>({ status: 'loading' });
  protected readonly noRecipe = NO_RECIPE_YET;
  protected readonly evidenceAsked = signal(false);

  protected readonly offer = computed(() => OFFERS[this.mode()]);

  protected readonly done = computed(() => {
    const recipe = this.ready();
    return recipe !== null && DONE[this.mode()].includes(recipe.status);
  });

  protected readonly doneText = computed(() =>
    this.ready()?.status === 'Approved' ? 'Approved. This version is the one you said yes to.' : 'It’s ready for review.',
  );

  /** Whether the reader's role could approve. The server is still the authority; this only chooses what to say. */
  protected readonly mayApprove = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return true;
    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role ?? null;
    return roleAllows(role, 'Editor');
  });

  constructor() {
    // Nothing of its own to keep: each move is recorded by the panel that makes it.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      ready: this.done,
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  protected ready(): RecipeDetail | null {
    const loaded = this.loaded();
    return loaded.status === 'ready' ? loaded.recipe : null;
  }

  protected movesLeft(status: RecipeStatus): number | null {
    return movesLeft(OFFERS['send-for-review'], status);
  }

  ngOnInit(): void {
    void this.membershipsLoaded();
    void this.load();
  }

  async load(): Promise<void> {
    this.loaded.set(await loadRecipe(this.recipes, this.workspaceSlug(), this.run()));
  }

  /** The recipe as the server left it after a move: its status, its token, and so what is offered next. */
  protected onTransitioned(recipe: RecipeDetail): void {
    this.loaded.set({ status: 'ready', recipe });
    this.port.announce(
      DONE[this.mode()].includes(recipe.status)
        ? recipe.status === 'Approved' ? 'Approved. Continue to finish.' : 'Ready for review. Continue when you’re ready.'
        : 'Moved on. The next move is below.',
    );
    // The button that was pressed belongs to the move just made, and is gone: never leave focus on nothing.
    this.port.focusStep();
  }

  private membershipsLoaded(): Promise<void> {
    return this.memberships.ensureLoaded();
  }
}
