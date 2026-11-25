import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';
import { firstValueFrom, switchMap } from 'rxjs';

import { DEFAULT_RECIPE_SEARCH_QUERY, RecipeSummary } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { LIST_BATCH_SIZE, matchesWords, searchWords } from '../../../shared/list-filter/list-filter';
import { ListFilterComponent } from '../../../shared/list-filter/list-filter.component';
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

/** What a recipe's row says under its name: the version it is at and when it last changed. */
function hintOf(recipe: Picked): string | undefined {
  return recipe.latestVersionNumber
    ? `Version ${recipe.latestVersionNumber}, changed ${new Date(recipe.updatedAt).toLocaleDateString(undefined, { dateStyle: 'medium' })}`
    : undefined;
}

/** What a recipe is found by: the words on its row, so a title, a version number or a date all find it. */
function searchableText(recipe: Picked): string {
  return `${recipe.title} ${hintOf(recipe) ?? ''}`;
}

/** What a read of the library found, said aloud: the list itself changes silently. */
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
 *
 * <p>The library is read once and the list is filtered here, in the browser, by {@link ListFilterComponent}: ten
 * recipes in a region that scrolls, and ten more for each "Load more recipes" — narrowed to the loaded recipes that
 * match as the creator types, and grown the same way. Asking for more than the loaded recipes can fill reads the
 * next page of the library, and a filter only ever sees what has been read — said, in its status, while the server
 * has more.</p>
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
    CpSkeletonComponent,
    ListFilterComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cp-form-section sectionId="recipe-choose" heading="Which recipe?" [intro]="intro()">
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
          @if (held().length > 0) {
            <cp-list-filter
              label="Search your recipes"
              placeholder="Search, such as “banana” or “loaf”"
              noun="recipes"
              [(query)]="query"
              [(shown)]="shown"
              [total]="matches().length"
              [status]="statusText()"
              [noMatch]="filtering() && matches().length === 0"
              [hasMore]="moreCursor() !== null"
              [loadingMore]="loadingMore()"
              loadMoreLabel="Load more recipes"
              (loadMore)="fetchNext()"
              noMatchTitle="No recipe matches that"
              noMatchDescription="Try another word, or clear the search to see all your recipes."
            >
              <cp-choice-group
                label="Your recipes"
                idPrefix="recipe-choose-"
                [options]="options()"
                [value]="picked()"
                (valueChange)="pick($event)"
              />
            </cp-list-filter>
            @if (moreFailed()) {
              <cp-notice tone="error" role="alert">More recipes couldn’t be read just now. Try again.</cp-notice>
            }
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

  /** What the creator typed to narrow the list. Nothing typed shows every recipe in hand. */
  protected readonly query = signal('');
  protected readonly found = signal<Found>({ status: 'loading' });
  private readonly retries = signal(0);

  /** How many of the matching recipes are on show: a batch of ten, and ten more for each "Load more recipes". */
  protected readonly shown = signal(LIST_BATCH_SIZE);

  /** The recipe picked here, or the one the run already holds when the creator came Back to change it. */
  private readonly pickedId = signal<string | null>(null);

  /** The picked recipe as it was listed, so it stays on show when the first five or a filter would leave it out. */
  private readonly pickedRecipe = signal<Picked | null>(null);

  protected readonly loadingMore = signal(false);

  private readonly words = computed(() => searchWords(this.query()));
  protected readonly filtering = computed(() => this.words().length > 0);

  /** Every recipe read so far, which is what a filter searches. */
  protected readonly held = computed<readonly RecipeSummary[]>(() => {
    const found = this.found();
    return found.status === 'ready' ? found.recipes : [];
  });

  /** The recipes in hand that hold every word typed — all of them while nothing is typed. */
  protected readonly matches = computed(() => {
    const words = this.words();
    return this.held().filter((recipe) => matchesWords(searchableText(recipe), words));
  });

  protected readonly moreCursor = computed(() => {
    const found = this.found();
    return found.status === 'ready' ? found.nextCursor : null;
  });

  /** The one sentence about what the filter found, including nothing. */
  protected readonly statusText = computed(() => {
    if (!this.filtering()) return '';
    const found = this.matches().length;
    const said = found === 0 ? 'Nothing matches that.' : `${found} of ${this.held().length} match.`;
    // The library is read a page at a time, so a filter cannot see what has not been read yet. Said, not hidden.
    return this.moreCursor() !== null ? `${said} Only the recipes loaded so far are searched.` : said;
  });

  protected readonly picked = computed<readonly string[]>(() => {
    const id = this.pickedId();
    return id ? [id] : [];
  });

  protected readonly options = computed<readonly CpChoiceOption[]>(() => {
    if (this.found().status !== 'ready') return [];
    const listed = this.matches().slice(0, this.shown());
    // The pick first when the rows on show do not have it — a filter left it out, it is further down than the
    // creator has loaded, or it is a recipe that was never read — so what Continue will link is always on show as
    // picked. Once its own row is shown it is there, and not repeated above.
    const kept = this.pickedRecipe();
    const rows: readonly Picked[] =
      kept && !listed.some((recipe) => recipe.id === kept.id) ? [kept, ...listed] : listed;
    return rows.map((recipe) => ({ value: recipe.id, label: recipe.title, hint: hintOf(recipe) }));
  });

  /** Set when the next page could not be read, and said beside the button that asked for it. */
  protected readonly moreFailed = signal(false);

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

    // One read of the library, and again on "Try again". Everything after it is filtering what was read.
    toObservable(this.retries)
      .pipe(
        switchMap(() => {
          this.found.set({ status: 'loading' });
          return this.recipes.searchRecipes(this.workspaceSlug(), DEFAULT_RECIPE_SEARCH_QUERY);
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

  /** The next page of the library, added to what is in hand; "Load more recipes" shows it as it arrives. */
  protected async fetchNext(): Promise<void> {
    const found = this.found();
    if (found.status !== 'ready' || found.nextCursor === null || this.loadingMore()) return;

    this.loadingMore.set(true);
    this.moreFailed.set(false);
    try {
      const outcome = await firstValueFrom(
        this.recipes.searchRecipes(this.workspaceSlug(), { ...DEFAULT_RECIPE_SEARCH_QUERY, cursor: found.nextCursor }),
      );

      // A page for a list that has since been read again is dropped, never pasted under another's results.
      const now = this.found();
      if (now.status !== 'ready' || now.nextCursor !== found.nextCursor) return;

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

  protected retry(): void {
    this.retries.update((count) => count + 1);
  }
}
