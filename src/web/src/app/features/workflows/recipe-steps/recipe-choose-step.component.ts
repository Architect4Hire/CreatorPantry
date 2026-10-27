import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSearchFieldComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';
import { debounceTime, distinctUntilChanged, firstValueFrom, switchMap } from 'rxjs';

import { DEFAULT_RECIPE_SEARCH_QUERY, RecipeSummary } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { linkedRecipeId } from './recipe-run';

/** The picked recipe as it is shown, whether it came from a list or from the run. */
interface Picked {
  readonly id: string;
  readonly title: string;
  readonly latestVersionNumber: number | null;
  readonly updatedAt: string;
}

type Found =
  | { readonly status: 'loading' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly recipes: readonly RecipeSummary[]; readonly nextCursor: string | null };

/** How long typing settles before the list is asked again. */
const SEARCH_SETTLE_MS = 300;

/** What a search found, said aloud: the list itself changes silently. */
function countSaid(shown: number, total: number | null): string {
  if (shown === 0) return 'No recipes found.';
  const count = total ?? shown;
  return count === 1 ? 'One recipe found.' : `${count} recipes found.`;
}

/**
 * "Choose the recipe", the first step of "Improve a recipe" and "Test and approve" (13A.15, 13A.16): find one of the
 * workspace's recipes and pick
 * it. A read of the library and nothing more — picking names the recipe as the step's result, and Continue asks the
 * server to link it. A run started from a recipe has this step done already; coming Back to it shows the recipe the
 * run holds and offers to change it.
 */
@Component({
  selector: 'cp-recipe-choose-step',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpSearchFieldComponent,
    CpSkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cp-form-section sectionId="recipe-choose" heading="Which recipe?" [intro]="intro()">
      <cp-search-field label="Search your recipes">
        <input type="search" name="search" autocomplete="off" [value]="searchText()" (input)="onSearch($event)" />
      </cp-search-field>

      <!-- Always present, outside the busy lines, so the step says once what it is waiting for (AU.7). -->
      <p class="cp-sr-only" role="status">{{ found().status === 'loading' ? 'Finding your recipes…' : '' }}</p>
      @switch (found().status) {
        @case ('loading') {
          <!-- The recipes to choose from, arriving as the rows the choices will be. -->
          <div aria-busy="true"><cp-skeleton shape="line" [count]="4" /></div>
        }
        @case ('unavailable') {
          <cp-notice tone="error" role="alert">
            Your recipes couldn’t be read just now.
            <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
          </cp-notice>
        }
        @case ('ready') {
          @if (options().length > 0) {
            <cp-choice-group
              label="Your recipes"
              idPrefix="recipe-choose-"
              [options]="options()"
              [value]="picked()"
              (valueChange)="pick($event)"
            />
            @if (moreFailed()) {
              <cp-notice tone="error" role="alert">More recipes couldn’t be read just now. Try again.</cp-notice>
            }
            @if (moreCursor()) {
              <div class="cp-actions">
                <button
                  cpButton
                  type="button"
                  variant="secondary"
                  [attr.aria-disabled]="loadingMore() || null"
                  (click)="loadMore()"
                >
                  {{ loadingMore() ? 'Loading more…' : 'Show more recipes' }}
                </button>
              </div>
            }
          } @else if (searchText().trim().length > 0) {
            <p class="plain">No recipe matches “{{ searchText().trim() }}”.</p>
          } @else {
            <p class="plain">
              There are no recipes to improve yet.
              <a [routerLink]="['/', workspaceSlug(), 'workflows']">Create one first</a>.
            </p>
          }
        }
      }
    </cp-form-section>
  `,
  styles: `
    .plain {
      margin: 0;
    }
  `,
})
export class RecipeChooseStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();
  /** One line under the question, saying what will be done with the recipe picked. */
  readonly intro = input('You’ll be working on its current version. Every earlier version is kept.');

  private readonly recipes = inject(RecipeService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly searchText = signal('');
  protected readonly found = signal<Found>({ status: 'loading' });
  private readonly retries = signal(0);

  /** The recipe picked here, or the one the run already holds when the creator came Back to change it. */
  private readonly pickedId = signal<string | null>(null);

  /** The picked recipe as it was listed, so it stays on show when a later search would leave it out. */
  private readonly pickedRecipe = signal<Picked | null>(null);

  protected readonly loadingMore = signal(false);

  protected readonly moreCursor = computed(() => {
    const found = this.found();
    return found.status === 'ready' ? found.nextCursor : null;
  });

  protected readonly picked = computed<readonly string[]>(() => {
    const id = this.pickedId();
    return id ? [id] : [];
  });

  protected readonly options = computed<readonly CpChoiceOption[]>(() => {
    const found = this.found();
    if (found.status !== 'ready') return [];
    // The pick first when this search would not list it: what Continue will link is always the one showing as picked.
    const kept = this.pickedRecipe();
    const listed =
      kept && !found.recipes.some((recipe) => recipe.id === kept.id) ? [kept, ...found.recipes] : found.recipes;
    return listed.map((recipe) => ({
      value: recipe.id,
      label: recipe.title,
      hint: recipe.latestVersionNumber
        ? `Version ${recipe.latestVersionNumber}, changed ${new Date(recipe.updatedAt).toLocaleDateString(undefined, { dateStyle: 'medium' })}`
        : undefined,
    }));
  });

  constructor() {
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      results: computed(() => {
        const id = this.pickedId();
        return id ? [{ kind: 'Recipe', id }] : [];
      }),
      ready: computed(() => this.pickedId() !== null),
    });
    inject(DestroyRef).onDestroy(unregister);

    // One read per settled search; a newer one replaces an older still in flight.
    toObservable(computed(() => ({ text: this.searchText().trim(), retry: this.retries() })))
      .pipe(
        debounceTime(SEARCH_SETTLE_MS),
        distinctUntilChanged((a, b) => a.text === b.text && a.retry === b.retry),
        switchMap(({ text }) => {
          this.found.set({ status: 'loading' });
          return this.recipes.searchRecipes(this.workspaceSlug(), { ...DEFAULT_RECIPE_SEARCH_QUERY, search: text });
        }),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        this.found.set(
          outcome.status === 'found'
            ? { status: 'ready', recipes: outcome.page.items, nextCursor: outcome.page.nextCursor }
            : { status: 'unavailable' },
        );
        if (outcome.status === 'found')
          this.port.announce(countSaid(outcome.page.items.length, outcome.page.totalCount));
      });
  }

  ngOnInit(): void {
    const linked = linkedRecipeId(this.run());
    this.pickedId.set(linked);
    if (linked !== null) void this.showLinked(linked);
  }

  /** The recipe the run already holds, read so it shows as picked even when no page of the list names it. */
  private async showLinked(recipeId: string): Promise<void> {
    const outcome = await this.recipes.getRecipeDetail(this.workspaceSlug(), recipeId);
    if (outcome.status !== 'found' || this.pickedId() !== recipeId || this.pickedRecipe() !== null) return;
    const recipe = outcome.recipe;
    this.pickedRecipe.set({
      id: recipe.id,
      title: recipe.title,
      latestVersionNumber: recipe.currentVersion?.versionNumber ?? null,
      updatedAt: recipe.updatedAt,
    });
  }

  protected pick(value: readonly string[]): void {
    const id = value[0] ?? null;
    this.pickedId.set(id);
    const found = this.found();
    if (found.status === 'ready')
      this.pickedRecipe.set(found.recipes.find((recipe) => recipe.id === id) ?? this.pickedRecipe());
    const name = this.options().find((option) => option.value === id)?.label;
    if (name) this.port.announce(`${name} chosen. Continue when you’re ready.`);
  }

  /** The next page of the same search, added below what is listed. */
  protected async loadMore(): Promise<void> {
    const found = this.found();
    if (found.status !== 'ready' || found.nextCursor === null || this.loadingMore()) return;

    const searched = this.searchText().trim();
    this.loadingMore.set(true);
    this.moreFailed.set(false);
    try {
      const outcome = await firstValueFrom(
        this.recipes.searchRecipes(this.workspaceSlug(), {
          ...DEFAULT_RECIPE_SEARCH_QUERY,
          search: searched,
          cursor: found.nextCursor,
        }),
      );

      // A page for a search that is no longer the one on screen is dropped, never pasted under another's results.
      const now = this.found();
      if (this.searchText().trim() !== searched || now.status !== 'ready' || now.nextCursor !== found.nextCursor)
        return;

      if (outcome.status === 'found') {
        this.found.set({
          status: 'ready',
          recipes: [...now.recipes, ...outcome.page.items],
          nextCursor: outcome.page.nextCursor,
        });
        this.port.announce(
          `${outcome.page.items.length === 1 ? 'One more recipe' : outcome.page.items.length + ' more recipes'} listed.`,
        );
      } else {
        this.moreFailed.set(true);
      }
    } finally {
      this.loadingMore.set(false);
    }
  }

  /** Set when the next page could not be read, and said beside the button that asked for it. */
  protected readonly moreFailed = signal(false);

  protected onSearch(event: Event): void {
    this.searchText.set((event.target as HTMLInputElement).value);
  }

  protected retry(): void {
    this.retries.update((count) => count + 1);
  }
}
