import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { RecipeDetail } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeDisplayNormalizationComponent } from '../../recipes/recipe-display-normalization.component';
import { RecipeScalingPreviewComponent } from '../../recipes/recipe-scaling-preview.component';
import { linkedRecipeId } from './recipe-run';

type Loaded =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly recipe: RecipeDetail };

/**
 * "Try it at another size", in "Create a recipe" and "Improve a recipe" (13A.14, 13A.15): the recipe page's own
 * scaling preview, on the saved recipe, with how its amounts will read tucked away below. Both are previews worked out by the server — nothing here does arithmetic, and
 * nothing here changes the recipe. The step is optional and done when the creator says so, so it has nothing to
 * keep.
 */
@Component({
  selector: 'cp-recipe-sizes-step',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, RecipeDisplayNormalizationComponent, RecipeScalingPreviewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (loaded().status) {
      @case ('loading') {
        <p class="cp-muted" role="status">Reading your recipe…</p>
      }
      @case ('missing') {
        <cp-notice tone="warning" role="status">
          The recipe can’t be found any more, so there’s nothing to try at another size.
        </cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your recipe couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (ready(); as recipe) {
          <div class="sizes">
            <cp-recipe-scaling-preview
              [workspaceSlug]="workspaceSlug()"
              [recipeId]="recipe.id"
              [sourceVersionNumber]="recipe.currentVersion?.versionNumber ?? null"
              [savedIngredientGroups]="recipe.ingredientGroups"
              [recipeYieldQuantity]="recipe.yieldQuantity"
              [recipeYieldText]="recipe.yieldText"
            />

            <details class="cp-disclosure">
              <summary>How your amounts will read</summary>
              <cp-recipe-display-normalization
                [workspaceSlug]="workspaceSlug()"
                [recipeId]="recipe.id"
                [sourceVersionNumber]="recipe.currentVersion?.versionNumber ?? null"
                [savedIngredientGroups]="recipe.ingredientGroups"
              />
            </details>
          </div>
        }
      }
    }
  `,
  styles: `
    .sizes {
      display: grid;
      gap: var(--cp-space-6);
    }
  `,
})
export class RecipeSizesStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly recipes = inject(RecipeService);

  protected readonly loaded = signal<Loaded>({ status: 'loading' });

  protected ready(): RecipeDetail | null {
    const loaded = this.loaded();
    return loaded.status === 'ready' ? loaded.recipe : null;
  }

  constructor() {
    // Nothing to keep: a preview changes nothing, and the step is done when the creator says so.
    const unregister = inject(WorkflowStepPort).register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    const id = linkedRecipeId(this.run());
    if (id === null) {
      this.loaded.set({ status: 'missing' });
      return;
    }

    this.loaded.set({ status: 'loading' });
    const outcome = await this.recipes.getRecipeDetail(this.workspaceSlug(), id);
    this.loaded.set(
      outcome.status === 'found'
        ? { status: 'ready', recipe: outcome.recipe }
        : outcome.status === 'not_found'
          ? { status: 'missing' }
          : { status: 'unavailable' },
    );
  }
}
