import { Observable, of } from 'rxjs';

import { RecipeVersionHistoryEntry } from '../models/recipe-version.models';
import { RecipeDetail, RecipeSearchQuery, RecipeStatus, RecipeSummary } from '../models/recipe.models';
import { RecipeDetailOutcome, RecipeSearchOutcome, RecipeVersionHistoryOutcome } from './recipe.service';

/** One recipe in the fake library: which workspace it is in, and its versions, oldest first. */
export interface FakeRecipe {
  readonly slug: string;
  readonly id: string;
  readonly title: string;
  readonly status?: RecipeStatus;
  /** Version ids, oldest first. Empty for a recipe that has never been saved as a version. */
  readonly versionIds?: readonly string[];
}

/**
 * An in-memory stand-in for the three `RecipeService` reads a recipe picker makes, for specs only.
 *
 * It keeps each workspace's recipes apart by slug, as the routes do, and filters by status as the search does —
 * so a spec about archived recipes or a second workspace is asking the fake the same question the product asks
 * the server. `offline` makes every read answer `unavailable`.
 */
export class FakeRecipeLibrary {
  offline = false;

  readonly searches: { slug: string; query: RecipeSearchQuery }[] = [];
  readonly histories: { slug: string; recipeId: string }[] = [];

  private recipes: FakeRecipe[] = [];

  constructor(recipes: readonly FakeRecipe[] = []) {
    this.recipes = [...recipes];
  }

  /** Save a new version of a recipe, as an edit made after it was linked would. */
  addVersion(recipeId: string, versionId: string): void {
    this.recipes = this.recipes.map((recipe) =>
      recipe.id === recipeId ? { ...recipe, versionIds: [...(recipe.versionIds ?? []), versionId] } : recipe,
    );
  }

  setStatus(recipeId: string, status: RecipeStatus): void {
    this.recipes = this.recipes.map((recipe) => (recipe.id === recipeId ? { ...recipe, status } : recipe));
  }

  searchRecipes(slug: string, query: RecipeSearchQuery): Observable<RecipeSearchOutcome> {
    if (this.offline) return of<RecipeSearchOutcome>({ status: 'unavailable' });

    this.searches.push({ slug, query });
    const text = query.search.toLowerCase();

    const items = this.recipes
      .filter((recipe) => recipe.slug === slug)
      .filter((recipe) => query.statuses.length === 0 || query.statuses.includes(recipe.status ?? 'Draft'))
      .filter((recipe) => recipe.title.toLowerCase().includes(text))
      .map((recipe): RecipeSummary => {
        const versions = recipe.versionIds ?? [];

        return {
          id: recipe.id,
          title: recipe.title,
          description: null,
          status: recipe.status ?? 'Draft',
          cuisineId: null,
          courseId: null,
          createdAt: '2026-10-01T09:00:00Z',
          updatedAt: '2026-10-01T09:00:00Z',
          latestVersionNumber: versions.length === 0 ? null : versions.length,
          latestVersionReadiness: versions.length === 0 ? null : 'Draft',
          hasUnmatchedIngredients: false,
        };
      });

    return of<RecipeSearchOutcome>({ status: 'found', page: { items, nextCursor: null, totalCount: null } });
  }

  getVersionHistory(slug: string, recipeId: string): Promise<RecipeVersionHistoryOutcome> {
    if (this.offline) return Promise.resolve({ status: 'unavailable' });

    this.histories.push({ slug, recipeId });
    const recipe = this.find(slug, recipeId);
    if (recipe === null) return Promise.resolve({ status: 'not_found' });

    // Newest first, as the route answers.
    const items = (recipe.versionIds ?? [])
      .map(
        (id, index): RecipeVersionHistoryEntry => ({
          id,
          versionNumber: index + 1,
          source: 'CreatorEdit',
          readiness: 'Draft',
          reason: null,
          createdAt: '2026-10-01T09:00:00Z',
          createdByName: null,
          parentVersionId: null,
          restoredFromVersionId: null,
          aiProposalId: null,
        }),
      )
      .reverse();

    return Promise.resolve({ status: 'found', page: { items, nextCursor: null } });
  }

  getRecipeDetail(slug: string, recipeId: string): Promise<RecipeDetailOutcome> {
    if (this.offline) return Promise.resolve({ status: 'unavailable' });

    const recipe = this.find(slug, recipeId);
    if (recipe === null) return Promise.resolve({ status: 'not_found' });

    // Only what a picker reads. The rest of a recipe is not this fake's to invent.
    const detail = { id: recipe.id, title: recipe.title, status: recipe.status ?? 'Draft' } as RecipeDetail;

    return Promise.resolve({ status: 'found', recipe: detail });
  }

  private find(slug: string, recipeId: string): FakeRecipe | null {
    return this.recipes.find((recipe) => recipe.slug === slug && recipe.id === recipeId) ?? null;
  }
}
