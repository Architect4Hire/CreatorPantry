import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { RecipeDetail } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeReviewRequestComponent } from '../../ai/recipe-review-request.component';
import { RecipeEditorHost } from '../../recipes/recipe-editor-host';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { RecipeTestKitchenComponent } from '../../recipes/recipe-test-kitchen.component';
import { RecipeReadinessService } from '../../../services/recipe-readiness.service';
import { linkedRecipeId } from '../recipe-steps/recipe-run';
import { NO_RECIPE_YET, readRunReadiness, readinessRuleMet } from './recipe-test-readiness';
import { LoadedRecipe, loadRecipe } from './recipe-test-cook-step.component';

type Way = 'problems' | 'change' | 'check';

const WAYS: readonly CpChoiceOption[] = [
  {
    value: 'problems',
    label: 'Sort out the problems you noted',
    hint: 'Close each one, and say which version fixed it. If the test went wrong, cook it again here once it’s fixed.',
  },
  { value: 'change', label: 'Change the recipe', hint: 'Edit any part of it. Your saved change is a new version, ready to cook again.' },
  {
    value: 'check',
    label: 'Check it over',
    hint: 'Get a read-through for anything worth a second look. It’s advice, not a safety check, and it changes nothing on its own.',
  },
];

/**
 * "Sort out what went wrong" in "Test and approve" (13A.16). Each way is a screen the recipe page already has: the
 * Test kitchen, where an issue is closed with a link to the version that fixed it and a fixed recipe is cooked again
 * (TESTRUN-002), the recipe editor hosted on every section, and the quality and safety review (AIREC-006) — advice
 * only, which never changes the recipe. Done when the server's readiness check finds the last test did not fail and
 * no blocking issue is open; nothing here can waive either.
 */
@Component({
  selector: 'cp-recipe-test-fix-step',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
    RecipeEditorComponent,
    RecipeReviewRequestComponent,
    RecipeTestKitchenComponent,
  ],
  providers: [RecipeEditorHost],
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
          <div class="fix">
            <cp-form-section sectionId="recipe-test-fix-way" heading="What’s needed?" intro="Nothing counts as fixed until it is.">
              <cp-choice-group
                label="What’s needed?"
                idPrefix="recipe-test-fix-"
                [options]="ways"
                [value]="chosen()"
                (valueChange)="choose($event)"
              />
            </cp-form-section>

            @switch (way()) {
              @case ('problems') {
                <cp-recipe-test-kitchen
                  [workspaceSlug]="workspaceSlug()"
                  [recipeId]="recipe.id"
                  [versionNumber]="recipe.currentVersion?.versionNumber ?? null"
                  [headingLevel]="3"
                  (changed)="checkSorted()"
                />
              }
              @case ('change') {
                <cp-recipe-editor />
              }
              @case ('check') {
                <cp-recipe-review-request
                  [workspaceSlug]="workspaceSlug()"
                  [recipeId]="recipe.id"
                  [currentVersionId]="recipe.currentVersion?.id ?? ''"
                  [headingLevel]="3"
                />
              }
            }
          </div>
        }
      }
    }
  `,
  styles: `
    .fix {
      display: grid;
      gap: var(--cp-space-6);
      min-width: 0;
    }
  `,
})
export class RecipeTestFixStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly recipes = inject(RecipeService);
  private readonly port = inject(WorkflowStepPort);
  private readonly editorHost = inject(RecipeEditorHost);
  private readonly editor = viewChild(RecipeEditorComponent);

  protected readonly ways = WAYS;
  protected readonly loaded = signal<LoadedRecipe>({ status: 'loading' });
  protected readonly noRecipe = NO_RECIPE_YET;
  private readonly readiness = inject(RecipeReadinessService);

  /** Whether the server finds nothing left to sort out: no failed last test, no blocking problem open. */
  private readonly sorted = signal(false);
  /** The problems first: that is what this step is about, and where a fixed recipe is cooked again. */
  protected readonly way = signal<Way>('problems');
  /** Bumped when a switch is refused, so the tiles are put back to the way still showing. */
  private readonly reasserted = signal(0);
  protected readonly chosen = computed<readonly string[]>(() => {
    this.reasserted();
    return [this.way()];
  });
  private readonly injector = inject(Injector);

  protected ready(): RecipeDetail | null {
    const loaded = this.loaded();
    return loaded.status === 'ready' ? loaded.recipe : null;
  }

  constructor() {
    const unregister = this.port.register({
      unsaved: computed(() => (this.way() === 'change' ? (this.editor()?.isDirty() ?? false) : false)),
      saving: computed(() => this.editor()?.saving() ?? false),
      savedAt: computed(() => this.editor()?.savedAt() ?? null),
      autosave: async () => (this.way() === 'change' ? ((await this.editor()?.flush()) ?? true) : true),
      flush: async () => {
        if (this.way() !== 'change') return true;
        const kept = (await this.editor()?.flush()) ?? true;
        if (kept) void this.load();
        return kept;
      },
      capabilityKey: computed(() => (this.way() === 'change' ? 'recipes.edit' : 'recipes.test-kitchen')),
      ready: this.sorted.asReadonly(),
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    this.editorHost.workspaceSlug = this.workspaceSlug();
    this.editorHost.recipeId = linkedRecipeId(this.run());
    this.editorHost.focus = 'all';
    void this.load();
  }

  async load(): Promise<void> {
    this.loaded.set(await loadRecipe(this.recipes, this.workspaceSlug(), this.run()));
    await this.checkSorted();
  }

  /** Asks the server again what is left to sort out — after a test, a problem answered, or a saved change. */
  async checkSorted(): Promise<void> {
    const readiness = await readRunReadiness(this.readiness, this.workspaceSlug(), this.run());
    this.sorted.set(
      readiness !== null
        && readinessRuleMet(readiness, 'recipe.testing.lastOutcomeFailed')
        && readinessRuleMet(readiness, 'recipe.testing.blockingIssueOutstanding'),
    );
  }

  /** A change of way saves the creator's own edits first rather than dropping them; refused, the editor stays. */
  async choose(value: readonly string[]): Promise<void> {
    const next = (value[0] ?? 'problems') as Way;
    if (next === this.way()) return;

    if (this.way() === 'change') {
      const hadEdits = this.editor()?.isDirty() ?? false;
      if (!((await this.editor()?.flush()) ?? true)) {
        afterNextRender(() => this.reasserted.update((count) => count + 1), { injector: this.injector });
        return;
      }
      // The saved change is the version to cook next, and the one an issue's fix can name.
      await this.load();
      if (hadEdits) this.port.announce('Saved. Cook this version again once you’re ready.');
    }

    this.way.set(next);
  }
}
