import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { combineLatest, debounce, of, switchMap, timer } from 'rxjs';
import {
  CpButtonComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { DamKeepRecipe } from '../../models/dam-asset.models';
import { recipeReferenceOf } from '../../models/creative-context.models';
import { DEFAULT_RECIPE_SEARCH_QUERY, RecipeStatus, RecipeSummary } from '../../models/recipe.models';
import { CreativeContextReferenceOutcome, CreativeContextSession } from '../../services/creative-context-session';
import { RecipeService } from '../../services/recipe.service';

/** Every status a recipe can be linked in. Archived is the one left out: an archived recipe is not offered. */
const LINKABLE_STATUSES: readonly RecipeStatus[] = ['Draft', 'InDevelopment', 'Testing', 'ReadyForReview', 'Approved'];

/** How long typing rests before the library is asked. The first read does not wait. */
const SEARCH_DELAY_MS = 250;

/** What the library answered for the text in the box. */
type SearchState =
  | { readonly status: 'loading' }
  | { readonly status: 'found'; readonly recipes: readonly RecipeSummary[] }
  | { readonly status: 'unavailable' };

/**
 * What is known about the linked recipe, read once when it is linked or the work is opened.
 *
 * A context names its recipe by id and never copies it, so the title and the version's number are read here
 * and nowhere kept.
 */
type LinkedState =
  | { readonly status: 'loading' }
  | {
      readonly status: 'ready';
      readonly title: string;
      /** The pinned version's number, or null when the recipe had none, or it is too old to be on the page read. */
      readonly pinnedNumber: number | null;
      /** The recipe's newest version, when that is not the one pinned. Null means the pin is current. */
      readonly newer: { readonly id: string; readonly number: number } | null;
    }
  /** Archived or removed since it was linked. Nothing is sent for it. */
  | { readonly status: 'gone' }
  | { readonly status: 'unavailable' };

type Action =
  | { readonly status: 'idle' }
  | { readonly status: 'working'; readonly sentence: string }
  | { readonly status: 'failed'; readonly sentence: string; readonly retry: () => void };

const FAILURES: Readonly<Record<Exclude<CreativeContextReferenceOutcome, 'saved' | 'duplicate'>, string>> = {
  source_unavailable: 'This recipe can no longer be linked. It may have been archived or removed.',
  limit: 'This piece of work already names as many sources as it can. Remove one to link a recipe.',
  forbidden: 'You do not have permission to change this piece of work.',
  held: 'This could not be done yet. Sort out the notice about your unsaved changes below, then try again.',
  unavailable: "This couldn't be done right now. Nothing was changed.",
};

/**
 * Link one recipe from the library to a piece of work (AF.3.3, FLU-006).
 *
 * A shared composition, used by Content Pipeline setup and Image Studio. It searches the creator's own recipes,
 * and the one they choose is stored as a recipe reference on the work's creative context — pinned to the
 * version that is current at that moment — through the surface's own `CreativeContextSession`. It shows that
 * reference as a summary that can be removed. What an AI request then names is read from the context by the
 * surface; this component sends nothing to any model.
 *
 * **The pin never moves on its own.** A recipe edited after it was linked is shown as changed, with a button
 * that re-pins it. Until the creator presses it, every request names the version they linked.
 *
 * **Archived recipes are not offered** — the search leaves them out — and one archived after it was linked is
 * said to be unavailable rather than quietly dropped, because the creator should know why a picture stopped
 * being planned around it.
 *
 * **Nothing is half-linked.** Changing the recipe is two writes (the context names a recipe once), and if the
 * second fails the summary shows that nothing is linked, with the way to try again.
 *
 * **Accessibility.** The search is a `cp-combobox` inside a `cp-field`, so it has a label, the WAI-ARIA
 * keyboard, and an announced result count. Every state that appears on its own — linking, linked, changed,
 * failed — is in a live region or an alert; the summary's buttons name the recipe they act on.
 */
@Component({
  selector: 'cp-recipe-picker',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpComboboxComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
  ],
  templateUrl: './recipe-picker.component.html',
  styleUrl: './recipe-picker.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipePickerComponent {
  private readonly recipes = inject(RecipeService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  /** The surface's hold on the work's creative context: where the choice is stored and read back from. */
  readonly session = input.required<CreativeContextSession>();
  /** Unique on the page, so two pickers never share a control id. */
  readonly idPrefix = input('cp-recipe-picker');
  /** What linking a recipe does on this surface, which differs: only the pipeline has an idea to build. */
  readonly intro = input('Link the recipe this picture is of, and the looks and the prompt are planned around it. Optional.');

  /** One sentence for the surface's polite live region when the link changes. */
  readonly announced = output<string>();

  /**
   * The linked recipe with its title, or null when none is linked or it can no longer be read.
   *
   * The context names a recipe by id; the title is read here, so a surface that needs the *words* — a save
   * form offering the link, say — is told rather than reading the same recipe a second time. Emitted when the
   * answer changes, so a page can hold it in a signal.
   */
  readonly linkedRecipe = output<DamKeepRecipe | null>();

  protected readonly searchText = signal('');
  protected readonly search = signal<SearchState>({ status: 'loading' });
  protected readonly linked = signal<LinkedState>({ status: 'loading' });
  protected readonly action = signal<Action>({ status: 'idle' });

  protected readonly inputId = computed(() => `${this.idPrefix()}-search`);

  /** The context's recipe reference, or null when it names none. */
  protected readonly reference = computed(() => recipeReferenceOf(this.session().context()));

  protected readonly libraryLink = computed(() => ['/', this.workspaceSlug(), 'recipes']);
  protected readonly recipeLink = computed(() => {
    const reference = this.reference();

    return reference?.recipeId ? ['/', this.workspaceSlug(), 'recipes', reference.recipeId] : null;
  });

  protected readonly options = computed<readonly CpComboboxOption[]>(() => {
    const state = this.search();
    if (state.status !== 'found') return [];

    return state.recipes.map((recipe) => ({
      id: recipe.id,
      label: recipe.title,
      detail: recipe.latestVersionNumber === null ? 'No saved version yet' : `Version ${recipe.latestVersionNumber}`,
    }));
  });

  /** True when the creator has no recipes at all — an empty library, not a search that found nothing. */
  protected readonly libraryEmpty = computed(() => {
    const state = this.search();

    return state.status === 'found' && state.recipes.length === 0 && this.searchText().trim() === '';
  });

  /** What the list says in place of options. */
  protected readonly emptyText = computed(() => {
    const status = this.search().status;
    if (status === 'loading') return 'Searching your recipes…';
    if (status === 'unavailable') return "Your recipes couldn't be searched right now.";

    return 'No recipes match that.';
  });

  protected readonly resultsLabel = computed(() => {
    const status = this.search().status;

    return (count: number): string => {
      if (status === 'loading') return 'Searching your recipes…';
      if (count === 0) return 'No recipes match that.';

      return count === 1 ? '1 recipe' : `${count} recipes`;
    };
  });

  protected readonly busy = computed(() => this.action().status === 'working');

  /** Raised to ask the library again for the same text, after a search that failed. */
  private readonly searchAgain = signal(0);
  /** The recipe id whose details are on screen or being read, so the same one is not read twice. */
  private readFor: string | null = null;
  /** The last answer {@link linkedRecipe} gave, so one answer is reported once. */
  private reported: string | null = null;
  /** True once the page has let go of this component, so an answer still on its way changes nothing. */
  private gone = false;

  constructor() {
    this.destroyRef.onDestroy(() => (this.gone = true));

    // A superseded search is cancelled rather than raced: an earlier, slower answer must not replace the
    // results for what the creator has typed since.
    combineLatest([toObservable(this.workspaceSlug), toObservable(this.searchText), toObservable(this.searchAgain)])
      .pipe(
        debounce(([, text]) => (text === '' ? of(0) : timer(SEARCH_DELAY_MS))),
        switchMap(([slug, text]) => {
          this.search.set({ status: 'loading' });

          return this.recipes.searchRecipes(slug, {
            ...DEFAULT_RECIPE_SEARCH_QUERY,
            search: text.trim(),
            statuses: LINKABLE_STATUSES,
          });
        }),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        this.search.set(
          outcome.status === 'found' ? { status: 'found', recipes: outcome.page.items } : { status: 'unavailable' },
        );
      });

    // Whatever the context names is read once: its title, the pinned version's number, and whether the recipe
    // has moved on since.
    effect(() => {
      const reference = this.reference();
      const slug = this.workspaceSlug();
      const key = reference === null ? null : `${slug}|${reference.recipeId}|${reference.recipeVersionId ?? ''}`;
      if (key === this.readFor) return;

      this.readFor = key;
      untracked(() => {
        if (reference?.recipeId) void this.readLinked(slug, reference.recipeId, reference.recipeVersionId, key);
        // Nothing is linked any more, which a surface holding the title has to be told before it offers it.
        else this.report(null);
      });
    });
  }

  /** Say what is linked, once per answer. */
  private report(recipe: DamKeepRecipe | null): void {
    const key = recipe === null ? null : `${recipe.id}|${recipe.title}`;
    if (key === this.reported) return;

    this.reported = key;
    this.linkedRecipe.emit(recipe);
  }

  protected retrySearch(): void {
    this.searchAgain.update((count) => count + 1);
  }

  protected onPicked(option: CpComboboxOption | null): void {
    if (option === null || this.busy()) return;

    void this.link(option.id, option.label);
  }

  /** Pin the recipe's newest version in place of the one linked. Only ever at the creator's asking. */
  protected repin(): void {
    const reference = this.reference();
    const state = this.linked();
    if (reference?.recipeId == null || state.status !== 'ready' || this.busy()) return;

    void this.link(reference.recipeId, state.title);
  }

  /** Unlink. The recipe itself is untouched, which is why this does not ask. */
  protected async remove(): Promise<void> {
    const reference = this.reference();
    if (reference === null || this.busy()) return;

    this.action.set({ status: 'working', sentence: 'Removing the link…' });
    const outcome = await this.session().removeReference(reference.id);
    if (this.gone) return;

    if (outcome !== 'saved') {
      this.action.set({ status: 'failed', sentence: FAILURES[outcome === 'duplicate' ? 'unavailable' : outcome], retry: () => void this.remove() });
      return;
    }

    this.action.set({ status: 'idle' });
    this.searchText.set('');
    this.announced.emit('The recipe is no longer linked.');
  }

  protected retryLinked(): void {
    const reference = this.reference();
    if (reference?.recipeId == null) return;

    void this.readLinked(this.workspaceSlug(), reference.recipeId, reference.recipeVersionId, this.readFor);
  }

  /**
   * Link a recipe, pinned to the version that is current now.
   *
   * The version is read at this moment rather than taken from the search row, which carries a number and not
   * the id a pin needs — and which may be a few seconds old. A recipe with no saved version is linked without
   * one, which every route reads as "the current one".
   */
  private async link(recipeId: string, title: string): Promise<void> {
    const slug = this.workspaceSlug();
    const session = this.session();
    const retry = (): void => void this.link(recipeId, title);

    this.action.set({ status: 'working', sentence: `Linking ${title}…` });

    const history = await this.recipes.getVersionHistory(slug, recipeId);
    if (this.gone || this.workspaceSlug() !== slug) return;

    if (history.status !== 'found') {
      this.action.set({
        status: 'failed',
        sentence: history.status === 'not_found' ? FAILURES.source_unavailable : FAILURES.unavailable,
        retry,
      });
      return;
    }

    const recipeVersionId = history.page.items[0]?.id ?? null;
    const existing = this.reference();

    if (existing !== null) {
      if (existing.recipeId === recipeId && existing.recipeVersionId === recipeVersionId) {
        this.action.set({ status: 'idle' });
        return;
      }

      // A context names a recipe once, whichever version is pinned, so the link in place goes first.
      const removed = await session.removeReference(existing.id);
      if (this.gone) return;
      if (removed !== 'saved') {
        this.action.set({ status: 'failed', sentence: FAILURES[removed === 'duplicate' ? 'unavailable' : removed], retry });
        return;
      }
    }

    const added = await session.addReference({ kind: 'Recipe', recipeId, recipeVersionId });
    if (this.gone) return;

    if (added !== 'saved' && added !== 'duplicate') {
      this.action.set({ status: 'failed', sentence: FAILURES[added], retry });
      return;
    }

    this.action.set({ status: 'idle' });
    this.searchText.set('');
    this.announced.emit(`${title} is linked.`);
  }

  private async readLinked(slug: string, recipeId: string, pinnedVersionId: string | null, key: string | null): Promise<void> {
    this.linked.set({ status: 'loading' });

    const [detail, history] = await Promise.all([
      this.recipes.getRecipeDetail(slug, recipeId),
      this.recipes.getVersionHistory(slug, recipeId),
    ]);
    // The link changed while this was being read: that read has its own answer coming.
    if (this.gone || key !== this.readFor) return;

    if (detail.status === 'not_found' || (detail.status === 'found' && detail.recipe.status === 'Archived')) {
      this.linked.set({ status: 'gone' });
      this.report(null);
      return;
    }
    if (detail.status !== 'found' || history.status !== 'found') {
      this.linked.set({ status: 'unavailable' });
      this.report(null);
      return;
    }

    const versions = history.page.items;
    const newest = versions[0] ?? null;

    this.linked.set({
      status: 'ready',
      title: detail.recipe.title,
      pinnedNumber: versions.find((version) => version.id === pinnedVersionId)?.versionNumber ?? null,
      newer: newest !== null && newest.id !== pinnedVersionId ? { id: newest.id, number: newest.versionNumber } : null,
    });
    this.report({ id: recipeId, title: detail.recipe.title });
  }

}
