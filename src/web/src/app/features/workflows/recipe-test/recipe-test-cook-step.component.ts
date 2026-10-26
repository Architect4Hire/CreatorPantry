import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

import { RecipeDetail } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeTestKitchenComponent } from '../../recipes/recipe-test-kitchen.component';
import { RecipeReadinessService } from '../../../services/recipe-readiness.service';
import { linkedRecipeId } from '../recipe-steps/recipe-run';
import { NO_RECIPE_YET, readRunReadiness, readinessRuleMet } from './recipe-test-readiness';

type Loaded =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly recipe: RecipeDetail };

/**
 * "Cook it and note what happened" in "Test and approve" (13A.16): the recipe page's own Test kitchen, on the
 * version the creator is cooking — record how it went, what was seen, and anything that went wrong (TESTRUN-001), with
 * earlier tests below (TESTRUN-003). A test is an observation and never changes the recipe. The server decides the
 * step is done once the current version has a test.
 */
@Component({
  selector: 'cp-recipe-test-cook-step',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, CpSkeletonComponent, RecipeTestKitchenComponent],
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
          <p class="plain">
            @if (recipe.currentVersion; as version) {
              Cook version {{ version.versionNumber }} exactly as it’s written, then say how it went.
            } @else {
              Cook it exactly as it’s written, then say how it went.
            }
            Writing down what happened never changes your recipe.
          </p>
          <cp-recipe-test-kitchen
            [workspaceSlug]="workspaceSlug()"
            [recipeId]="recipe.id"
            [versionNumber]="recipe.currentVersion?.versionNumber ?? null"
            [headingLevel]="3"
            (changed)="checkTested()"
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
export class RecipeTestCookStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly recipes = inject(RecipeService);
  private readonly readiness = inject(RecipeReadinessService);

  protected readonly loaded = signal<Loaded>({ status: 'loading' });
  protected readonly noRecipe = NO_RECIPE_YET;

  /** Whether the server finds the current version tested: until then the Test kitchen holds the step's next move. */
  private readonly tested = signal(false);

  protected ready(): RecipeDetail | null {
    const loaded = this.loaded();
    return loaded.status === 'ready' ? loaded.recipe : null;
  }

  constructor() {
    // A test is kept by the Test kitchen when it is recorded; there is nothing here to keep.
    const unregister = inject(WorkflowStepPort).register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      ready: this.tested.asReadonly(),
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
    void this.checkTested();
  }

  /** Asks the server again whether the current version has a test — after the Test kitchen recorded one. */
  async checkTested(): Promise<void> {
    const readiness = await readRunReadiness(this.readiness, this.workspaceSlug(), this.run());
    this.tested.set(readiness !== null && readinessRuleMet(readiness, 'recipe.testing.currentVersionUntested'));
  }

  async load(): Promise<void> {
    this.loaded.set(await loadRecipe(this.recipes, this.workspaceSlug(), this.run()));
  }
}

/** The run's recipe, read for a step: found, gone, or not readable just now. */
export async function loadRecipe(recipes: RecipeService, workspaceSlug: string, run: WorkflowRun): Promise<Loaded> {
  const id = linkedRecipeId(run);
  if (id === null) return { status: 'missing' };
  const outcome = await recipes.getRecipeDetail(workspaceSlug, id);
  if (outcome.status === 'found') return { status: 'ready', recipe: outcome.recipe };
  return outcome.status === 'not_found' ? { status: 'missing' } : { status: 'unavailable' };
}

export type { Loaded as LoadedRecipe };
