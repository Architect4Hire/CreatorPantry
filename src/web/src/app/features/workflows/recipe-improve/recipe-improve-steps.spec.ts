import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, Observable, of } from 'rxjs';

import { ConfirmService } from '../../../core/confirm.service';
import { AiOpenProposal } from '../../../models/ai-proposal.models';
import { RecipeDetail, RecipeSummary } from '../../../models/recipe.models';
import { RecipeVersionComparison, RecipeVersionHistoryEntry } from '../../../models/recipe-version.models';
import { WorkflowResourcePointer, WorkflowRun, decodeWorkflowRun } from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { AiOpenProposalsOutcome, AiProposalService } from '../../../services/ai-proposal.service';
import { RecipeSearchOutcome, RecipeService } from '../../../services/recipe.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { AiProposalPanelComponent } from '../../ai/ai-proposal-panel.component';
import { RecipeReviewRequestComponent } from '../../ai/recipe-review-request.component';
import { RecipeRevisionRequestComponent } from '../../ai/recipe-revision-request.component';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { RecipeImproveChangeStepComponent } from './recipe-improve-change-step.component';
import { RecipeChooseStepComponent } from '../recipe-steps/recipe-choose-step.component';
import { RecipeImproveCompareStepComponent } from './recipe-improve-compare-step.component';
import { RecipeImproveDecideStepComponent } from './recipe-improve-decide-step.component';
import { RecipeOpenSuggestionsComponent } from './recipe-open-suggestions.component';

const SLUG = 'sams-kitchen';
const STARTED = '2026-10-15T09:00:00Z';

function detail(overrides: Partial<RecipeDetail> = {}): RecipeDetail {
  return {
    id: 'recipe-1',
    title: 'Soda bread',
    description: null,
    headnote: null,
    notes: null,
    storageNotes: null,
    attributionText: null,
    sourceUrl: null,
    cuisineId: null,
    courseId: null,
    primaryTechniqueId: null,
    prepTimeMinutes: 10,
    cookTimeMinutes: 40,
    restTimeMinutes: null,
    totalTimeMinutes: 50,
    yieldText: 'One loaf',
    yieldQuantity: 1,
    yieldUnitId: null,
    servingCount: 8,
    servingSize: null,
    status: 'Draft',
    duplicatedFrom: null,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T09:00:00Z',
    concurrencyToken: 'AAAAAAAAB9E=',
    currentVersion: {
      id: 'v3',
      versionNumber: 3,
      source: 'CreatorEdit',
      readiness: 'Draft',
      reason: null,
      createdAt: '2026-10-01T09:00:00Z',
    },
    ingredientGroups: [],
    instructionGroups: [],
    equipment: [],
    assetLinks: [],
    tags: [],
    ...overrides,
  };
}

function summary(id: string, title: string): RecipeSummary {
  return {
    id,
    title,
    description: null,
    status: 'Draft',
    cuisineId: null,
    courseId: null,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-02T09:00:00Z',
    latestVersionNumber: 3,
    latestVersionReadiness: 'Draft',
    hasUnmatchedIngredients: false,
    heroAsset: null,
  };
}

function openItem(overrides: Partial<AiOpenProposal> = {}): AiOpenProposal {
  return {
    requestId: 'req-1',
    taskType: 'RecipeRevision',
    status: 'Proposed',
    requestedAt: '2026-10-15T09:05:00Z',
    pendingChangeCount: 2,
    hasSafetyCaution: false,
    isStale: false,
    ...overrides,
  };
}

function version(number: number, createdAt: string): RecipeVersionHistoryEntry {
  return {
    id: `v${number}`,
    versionNumber: number,
    source: 'CreatorEdit',
    readiness: 'Draft',
    reason: null,
    createdAt,
    createdByName: 'Sam',
    parentVersionId: null,
    restoredFromVersionId: null,
    aiProposalId: null,
  };
}

/** An "Improve a recipe" run, started at {@link STARTED}, holding `recipe-1` unless it holds nothing. */
function improveRun(options: { recipe?: boolean } = {}): WorkflowRun {
  return decodeWorkflowRun(
    wireRun({
      definitionKey: 'recipe.improve',
      startedAt: STARTED,
      links: options.recipe === false ? [] : [{ kind: 'Recipe', resourceId: 'recipe-1', stepKey: null }],
    }),
  )!;
}

let port: WorkflowStepPort;
let focusStep: jasmine.Spy;
let recipes: {
  getRecipeDetail: jasmine.Spy;
  updateRecipe: jasmine.Spy;
  searchRecipes: jasmine.Spy<(slug: string, query: unknown) => Observable<RecipeSearchOutcome>>;
  getVersionHistory: jasmine.Spy;
  compareVersions: jasmine.Spy;
};
let open: AiOpenProposalsOutcome;
let listOpen: jasmine.Spy;
let watchHandler: () => Observable<unknown>;
let disposition: jasmine.Spy;
let memberships: WritableSignal<unknown>;

function configure(): void {
  port = new WorkflowStepPort();
  focusStep = jasmine.createSpy('focusStep');
  port.connect((_draft: WorkflowResourcePointer) => Promise.resolve(true), focusStep);
  recipes = {
    getRecipeDetail: jasmine.createSpy('getRecipeDetail').and.resolveTo({ status: 'found', recipe: detail() }),
    updateRecipe: jasmine.createSpy('updateRecipe').and.resolveTo({ status: 'unavailable' }),
    searchRecipes: jasmine.createSpy('searchRecipes').and.returnValue(
      of<RecipeSearchOutcome>({
        status: 'found',
        page: {
          items: [summary('recipe-1', 'Soda bread'), summary('recipe-2', 'Rye loaf')],
          nextCursor: null,
          totalCount: 2,
        },
      }),
    ),
    getVersionHistory: jasmine.createSpy('getVersionHistory'),
    compareVersions: jasmine.createSpy('compareVersions'),
  };
  open = { status: 'found', open: [] };
  listOpen = jasmine.createSpy('listOpen').and.callFake(() => Promise.resolve(open));
  watchHandler = () => NEVER;
  disposition = jasmine.createSpy('disposition');
  memberships = signal<unknown>({ status: 'loading' });

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: RecipeService, useValue: recipes },
      {
        provide: AiProposalService,
        useValue: {
          listOpen,
          // A proposal still being written, as far as each panel can tell: no answer, no timers.
          watchStatus: () => watchHandler(),
          requestProposal: jasmine.createSpy('requestProposal'),
          disposition,
        },
      },
      { provide: ConfirmService, useValue: { confirm: jasmine.createSpy('confirm').and.resolveTo(true) } },
      {
        provide: WorkspaceMembershipService,
        useValue: { state: memberships, ensureLoaded: () => Promise.resolve() },
      },
    ],
  });
}

async function render<T>(type: Type<T>, inputs: Record<string, unknown>): Promise<ComponentFixture<T>> {
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  await settle(fixture);
  return fixture;
}

async function settle<T>(fixture: ComponentFixture<T>, rounds = 3): Promise<void> {
  for (let i = 0; i < rounds; i++) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

function root<T>(fixture: ComponentFixture<T>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function text<T>(fixture: ComponentFixture<T>): string {
  return root(fixture).textContent?.replace(/\s+/g, ' ') ?? '';
}

function editorOf<T>(fixture: ComponentFixture<T>): RecipeEditorComponent | null {
  const found = fixture.debugElement.query((node) => node.componentInstance instanceof RecipeEditorComponent);
  return (found?.componentInstance as RecipeEditorComponent | undefined) ?? null;
}

async function pick<T>(fixture: ComponentFixture<T>, id: string): Promise<void> {
  root(fixture).querySelector<HTMLInputElement>(`#${id}`)!.click();
  await settle(fixture);
}

describe('"Improve a recipe" step screens (13A.15)', () => {
  beforeEach(() => configure());

  // AU.7: what a step is waiting for arrives as its shape, and is said once from a region that is always there.
  describe('while a step reads what it needs', () => {
    const statuses = (scope: Element) => Array.from(scope.querySelectorAll('p.cp-sr-only[role="status"]'));
    const never = <T>() => new Promise<T>(() => undefined);
    /** The busy lines drawn beside a status: the step's own, not a child panel's. */
    const busyBeside = (status: Element) =>
      Array.from(status.parentElement!.children).find(
        (each) => each.matches('[aria-busy="true"]') && each.querySelector('cp-skeleton'),
      );

    function expectLoading(scope: Element, sentence: string): void {
      const status = statuses(scope).find((p) => p.textContent === sentence);
      expect(status).withContext(sentence).toBeTruthy();
      const busy = busyBeside(status!)!;
      expect(busy).toBeTruthy();
      expect(busy.textContent!.trim()).toBe('');
      expect(
        Array.from(scope.querySelectorAll('p:not(.cp-sr-only)')).some((p) => p.textContent?.includes(sentence)),
      ).toBeFalse();
    }

    function expectRead(scope: Element, sentence: string): void {
      // The region stays, its words gone: it was there before the answer, so the next change to it is heard.
      const emptied = statuses(scope).filter((p) => p.textContent === '');
      expect(emptied.length).toBeGreaterThan(0);
      expect(statuses(scope).some((p) => p.textContent?.includes(sentence))).toBeFalse();
      expect(emptied.some((p) => busyBeside(p))).toBeFalse();
    }

    it('Choose the recipe: draws the rows arriving while it finds the recipes, then keeps its status emptied', async () => {
      recipes.searchRecipes.and.returnValue(NEVER);
      const fixture = await render(RecipeChooseStepComponent, {
        run: improveRun({ recipe: false }),
        workspaceSlug: SLUG,
      });
      await settle(fixture);
      expectLoading(root(fixture), 'Finding your recipes…');

      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({
          status: 'found',
          page: { items: [summary('recipe-1', 'Soda bread')], nextCursor: null, totalCount: 1 },
        }),
      );
      const read = await render(RecipeChooseStepComponent, { run: improveRun({ recipe: false }), workspaceSlug: SLUG });
      await settle(read);
      expectRead(root(read), 'Finding your recipes…');
    });

    it('Choose the recipe: its pager says "Loading more…" while it reads the next page', async () => {
      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({
          status: 'found',
          page: { items: [summary('recipe-1', 'Soda bread')], nextCursor: 'next' as string | null, totalCount: 2 },
        }),
      );
      const fixture = await render(RecipeChooseStepComponent, {
        run: improveRun({ recipe: false }),
        workspaceSlug: SLUG,
      });
      await settle(fixture);

      recipes.searchRecipes.and.returnValue(NEVER);
      const more = () =>
        Array.from(root(fixture).querySelectorAll('button')).find((b) => b.textContent?.includes('more'))!;
      more().click();
      await settle(fixture);

      expect(more().textContent?.trim()).toBe('Loading more…');
    });

    it('Make your changes: draws lines while it reads the recipe, then keeps its status emptied', async () => {
      recipes.getRecipeDetail.and.returnValue(never());
      const fixture = await render(RecipeImproveChangeStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectLoading(root(fixture), 'Reading your recipe…');

      recipes.getRecipeDetail.and.resolveTo({ status: 'found', recipe: detail() });
      const read = await render(RecipeImproveChangeStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectRead(root(read), 'Reading your recipe…');
    });

    it('Decide on every suggestion: draws lines while it looks for what is waiting, then keeps its status emptied', async () => {
      listOpen.and.returnValue(never());
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectLoading(
        root(fixture).querySelector('cp-recipe-open-suggestions')!,
        'Looking for suggestions waiting for you…',
      );

      listOpen.and.callFake(() => Promise.resolve(open));
      const read = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectRead(root(read).querySelector('cp-recipe-open-suggestions')!, 'Looking for suggestions waiting for you…');
    });

    it('Compare with before: draws lines while it compares, then keeps its status emptied', async () => {
      recipes.getVersionHistory.and.returnValue(never());
      const fixture = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectLoading(root(fixture), 'Comparing your recipe with how it was…');

      recipes.getVersionHistory.and.resolveTo({
        status: 'found',
        page: { items: [version(3, '2026-10-01T09:00:00Z')], nextCursor: null },
      });
      const read = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectRead(root(read), 'Comparing your recipe with how it was…');
    });
  });

  describe('Choose the recipe', () => {
    async function choose(run = improveRun({ recipe: false })) {
      const fixture = await render(RecipeChooseStepComponent, { run, workspaceSlug: SLUG });
      await settle(fixture);
      return fixture;
    }

    /** A library of `count` recipes: the odd ones "Banana bread n", the even ones "Rye loaf n". */
    function library(count: number, nextCursor: string | null = null): Observable<RecipeSearchOutcome> {
      return of<RecipeSearchOutcome>({
        status: 'found',
        page: {
          items: Array.from({ length: count }, (_, i) =>
            summary(`recipe-${i + 1}`, (i + 1) % 2 === 1 ? `Banana bread ${i + 1}` : `Rye loaf ${i + 1}`),
          ),
          nextCursor,
          totalCount: count,
        },
      });
    }

    const listedIds = <T>(fixture: ComponentFixture<T>) =>
      Array.from(root(fixture).querySelectorAll<HTMLInputElement>('input[id^="recipe-choose-recipe-"]')).map((input) =>
        input.id.replace('recipe-choose-', ''),
      );

    const button = <T>(fixture: ComponentFixture<T>, label: string) =>
      Array.from(root(fixture).querySelectorAll('button')).find((b) => b.textContent?.trim() === label) ?? null;

    async function filterBy<T>(fixture: ComponentFixture<T>, value: string): Promise<void> {
      const search = root(fixture).querySelector<HTMLInputElement>('input[type="search"]')!;
      search.value = value;
      search.dispatchEvent(new Event('input'));
      await settle(fixture);
    }

    const filterStatus = <T>(fixture: ComponentFixture<T>) =>
      root(fixture).querySelector('cp-list-filter [role="status"]')!.textContent;

    const footer = <T>(fixture: ComponentFixture<T>) =>
      (root(fixture).querySelector('cp-list-filter .summary')?.textContent ?? '').replace(/\s+/g, ' ').trim();

    it('shows ten recipes, and ten more for each Load more recipes', async () => {
      recipes.searchRecipes.and.returnValue(library(23));
      const fixture = await choose();

      expect(listedIds(fixture)).toEqual(Array.from({ length: 10 }, (_, i) => `recipe-${i + 1}`));
      expect(footer(fixture)).toContain('Showing 10 of 23 recipes');

      button(fixture, 'Load more recipes')!.click();
      await settle(fixture);
      expect(listedIds(fixture)).toEqual(Array.from({ length: 20 }, (_, i) => `recipe-${i + 1}`));

      button(fixture, 'Load more recipes')!.click();
      await settle(fixture);
      expect(listedIds(fixture).length).toBe(23);
      expect(footer(fixture)).toContain('Showing 23 of 23 recipes');
      expect(button(fixture, 'Load more recipes')).toBeNull();
    });

    it('does not read the library again to show recipes it already has', async () => {
      recipes.searchRecipes.and.returnValue(library(23, 'next'));
      const fixture = await choose();
      recipes.searchRecipes.calls.reset();

      button(fixture, 'Load more recipes')!.click();
      await settle(fixture);

      expect(listedIds(fixture).length).toBe(20);
      expect(recipes.searchRecipes).not.toHaveBeenCalled();
    });

    it('holds the recipes in a region that scrolls', async () => {
      recipes.searchRecipes.and.returnValue(library(23));
      const fixture = await choose();

      const scroller = root(fixture).querySelector<HTMLElement>('cp-list-filter .scroller')!;
      expect(scroller.querySelector('cp-choice-group')).not.toBeNull();
      expect(getComputedStyle(scroller).overflowY).toBe('auto');
    });

    it('says how many there are, and offers no Load more, when they all fit in the first ten', async () => {
      recipes.searchRecipes.and.returnValue(library(8));
      const fixture = await choose();

      expect(footer(fixture)).toContain('Showing 8 of 8 recipes');
      expect(button(fixture, 'Load more recipes')).toBeNull();
    });

    it('offers the filter beside a short list too', async () => {
      recipes.searchRecipes.and.returnValue(library(3));
      const fixture = await choose();

      expect(root(fixture).querySelector('input[type="search"]')).not.toBeNull();
      await filterBy(fixture, 'rye');
      expect(listedIds(fixture)).toEqual(['recipe-2']);
    });

    it('filters the recipes it has read as the creator types, and grows the matches the same way, without asking the server again', async () => {
      recipes.searchRecipes.and.returnValue(library(30));
      const fixture = await choose();
      recipes.searchRecipes.calls.reset();

      await filterBy(fixture, 'rye');

      // Every even recipe is a "Rye loaf": fifteen of thirty, ten of them to begin with.
      expect(listedIds(fixture)).toEqual(Array.from({ length: 10 }, (_, i) => `recipe-${(i + 1) * 2}`));
      expect(filterStatus(fixture)).toContain('15 of 30 match.');
      expect(footer(fixture)).toContain('Showing 10 of 15 recipes');
      expect(recipes.searchRecipes).not.toHaveBeenCalled();

      button(fixture, 'Load more recipes')!.click();
      await settle(fixture);
      expect(listedIds(fixture)).toEqual(Array.from({ length: 15 }, (_, i) => `recipe-${(i + 1) * 2}`));
      expect(button(fixture, 'Load more recipes')).toBeNull();
    });

    it('starts again at the first ten when something is typed', async () => {
      recipes.searchRecipes.and.returnValue(library(30));
      const fixture = await choose();
      button(fixture, 'Load more recipes')!.click();
      await settle(fixture);
      expect(listedIds(fixture).length).toBe(20);

      await filterBy(fixture, 'rye');

      expect(listedIds(fixture).length).toBe(10);
      expect(listedIds(fixture)[0]).toBe('recipe-2');
    });

    it('needs every word typed, and finds a recipe by the words on its row as well as its name', async () => {
      recipes.searchRecipes.and.returnValue(library(8));
      const fixture = await choose();

      await filterBy(fixture, 'rye 8');
      expect(listedIds(fixture)).toEqual(['recipe-8']);

      // The row says "Version 3, changed …", so the version is searchable too.
      await filterBy(fixture, 'version 3 banana');
      expect(listedIds(fixture).length).toBe(4);
    });

    it('says when nothing matches, with no footer, and puts the first ten back on clearing', async () => {
      recipes.searchRecipes.and.returnValue(library(23));
      const fixture = await choose();

      await filterBy(fixture, 'spaceship');
      expect(listedIds(fixture)).toEqual([]);
      expect(text(fixture)).toContain('No recipe matches that');
      expect(filterStatus(fixture)).toContain('Nothing matches that.');
      expect(footer(fixture)).toBe('');

      button(fixture, 'Clear search')!.click();
      await settle(fixture);
      expect(listedIds(fixture).length).toBe(10);
      expect(text(fixture)).not.toContain('No recipe matches that');
    });

    it('keeps the pick on show when a filter would leave it out', async () => {
      recipes.searchRecipes.and.returnValue(library(8));
      const fixture = await choose();
      await pick(fixture, 'recipe-choose-recipe-1');

      await filterBy(fixture, 'rye');

      expect(listedIds(fixture)).toEqual(['recipe-1', 'recipe-2', 'recipe-4', 'recipe-6', 'recipe-8']);
      expect(root(fixture).querySelector<HTMLInputElement>('#recipe-choose-recipe-1')!.checked).toBeTrue();
      expect(port.registration()!.results!()).toEqual([{ kind: 'Recipe', id: 'recipe-1' }]);
    });

    it('shows the pick above the rows while a filter hides it, and only in its own place once it is shown again', async () => {
      recipes.searchRecipes.and.returnValue(library(23));
      const fixture = await choose();
      await pick(fixture, 'recipe-choose-recipe-1');

      await filterBy(fixture, 'rye');
      expect(listedIds(fixture)[0]).toBe('recipe-1');

      button(fixture, 'Clear search')?.click();
      await filterBy(fixture, '');

      // Back in its own row, once, and not repeated above the rest.
      expect(listedIds(fixture).filter((id) => id === 'recipe-1').length).toBe(1);
      expect(listedIds(fixture).length).toBe(10);
      expect(root(fixture).querySelector<HTMLInputElement>('#recipe-choose-recipe-1')!.checked).toBeTrue();
    });

    it('says a filter only sees what has been read while the library has more, and reads the next page for it', async () => {
      recipes.searchRecipes.and.returnValue(library(4, 'next'));
      const fixture = await choose();

      await filterBy(fixture, 'rye');
      expect(filterStatus(fixture)).toContain('Only the recipes loaded so far are searched.');

      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({
          status: 'found',
          page: { items: [summary('recipe-20', 'Rye sourdough 20')], nextCursor: null, totalCount: 5 },
        }),
      );
      button(fixture, 'Load more recipes')!.click();
      await settle(fixture);

      expect(recipes.searchRecipes.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ cursor: 'next' }));
      expect(listedIds(fixture)).toEqual(['recipe-2', 'recipe-4', 'recipe-20']);
      expect(filterStatus(fixture)).not.toContain('Only the recipes loaded so far');
    });

    it('shows what it reads when it reads more of the library, because the count ran ahead of it', async () => {
      recipes.searchRecipes.and.returnValue(library(10, 'next'));
      const fixture = await choose();
      expect(footer(fixture)).toContain('Showing 10 of the 10 recipes loaded so far');

      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({
          status: 'found',
          page: { items: [summary('recipe-11', 'Banana bread 11')], nextCursor: null, totalCount: 11 },
        }),
      );
      button(fixture, 'Load more recipes')!.click();
      await settle(fixture);

      expect(listedIds(fixture).length).toBe(11);
      expect(listedIds(fixture)[10]).toBe('recipe-11');
      expect(footer(fixture)).toContain('Showing 11 of 11 recipes');
      expect(button(fixture, 'Load more recipes')).toBeNull();
    });

    it('offers no filter when there are no recipes at all', async () => {
      recipes.searchRecipes.and.returnValue(library(0));
      const fixture = await choose();

      expect(root(fixture).querySelector('input[type="search"]')).toBeNull();
      expect(text(fixture)).toContain('There are no recipes to improve yet.');
    });

    it('draws the search box as wide as its field, not at the browser’s default', async () => {
      const fixture = await choose();

      const search = root(fixture).querySelector<HTMLInputElement>('input[type="search"]')!;
      const field = search.closest('cp-search-field')!;
      expect(search.getBoundingClientRect().width).toBeGreaterThanOrEqual(field.getBoundingClientRect().width - 1);
    });

    it('lists the workspace’s recipes, and picking one names it as the step’s result', async () => {
      const fixture = await choose();

      expect(text(fixture)).toContain('Which recipe?');
      expect(text(fixture)).toContain('Rye loaf');
      expect(port.registration()!.ready!()).toBeFalse();

      await pick(fixture, 'recipe-choose-recipe-2');

      expect(port.registration()!.results!()).toEqual([{ kind: 'Recipe', id: 'recipe-2' }]);
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('shows the recipe the run holds as picked even when no page of the list names it', async () => {
      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({
          status: 'found',
          page: { items: [summary('recipe-2', 'Rye loaf')], nextCursor: 'more', totalCount: 60 },
        }),
      );
      const fixture = await choose(improveRun());

      expect(text(fixture)).toContain('Soda bread');
      expect(root(fixture).querySelector<HTMLInputElement>('#recipe-choose-recipe-1')!.checked).toBeTrue();
    });

    it('shows the recipe the run holds as picked when the creator comes back to change it', async () => {
      const fixture = await choose(improveRun());

      expect(root(fixture).querySelector<HTMLInputElement>('#recipe-choose-recipe-1')!.checked).toBeTrue();
      expect(port.registration()!.results!()).toEqual([{ kind: 'Recipe', id: 'recipe-1' }]);
    });

    it('says when nothing matches, and offers Try again when the list cannot be read', async () => {
      recipes.searchRecipes.and.returnValue(of({ status: 'unavailable' }));
      const fixture = await choose();

      expect(text(fixture)).toContain('couldn’t be read just now');

      recipes.searchRecipes.and.returnValue(
        of({ status: 'found', page: { items: [], nextCursor: null, totalCount: 0 } }),
      );
      Array.from(root(fixture).querySelectorAll('button'))
        .find((b) => b.textContent?.includes('Try again'))!
        .click();
      await settle(fixture);

      expect(text(fixture)).toContain('There are no recipes to improve yet.');
    });
  });

  describe('Make your changes', () => {
    function change() {
      return render(RecipeImproveChangeStepComponent, { run: improveRun(), workspaceSlug: SLUG });
    }

    it('opens on getting suggestions for the whole recipe, offers every other way in plain words, and asks for nothing yet', async () => {
      const fixture = await change();

      for (const label of [
        'Get suggestions',
        'Check it over',
        'Adapt it for a need',
        'Swap an ingredient',
        'Change it myself',
      ]) {
        expect(text(fixture)).toContain(label);
      }
      expect(text(fixture)).not.toMatch(/make it better|prompt|model/i);
      expect(root(fixture).querySelector<HTMLInputElement>('#recipe-improve-way-revise')!.checked).toBeTrue();

      const revision = fixture.debugElement.query(
        (node) => node.componentInstance instanceof RecipeRevisionRequestComponent,
      ).componentInstance as RecipeRevisionRequestComponent;
      expect(revision.scope()).toBe('WholeRecipe');
      expect(revision.requestId()).toBeNull();
      expect(
        TestBed.inject(HttpTestingController).match((request) => request.url.includes('revision-requests')),
      ).toEqual([]);
      expect(editorOf(fixture)).toBeNull();
    });

    it('opens the revision on the recipe’s current version', async () => {
      const fixture = await change();

      expect(root(fixture).querySelector('cp-recipe-revision-request')).not.toBeNull();
      expect(port.registration()!.capabilityKey!()).toBe('recipes.suggest-changes');
    });

    it('lets the findings of a check be picked and asked about', async () => {
      const fixture = await change();
      await pick(fixture, 'recipe-improve-way-check');

      const review = fixture.debugElement.query(
        (node) => node.componentInstance instanceof RecipeReviewRequestComponent,
      ).componentInstance as RecipeReviewRequestComponent;
      expect(review.offerFixes()).toBeTrue();
      // The findings of a check open in a drawer here, not under the step's choices.
      expect(review.findingsInDrawer()).toBeTrue();
      expect(review.headingLevel()).toBe(3);
    });

    it('keeps a suggestion a reload left waiting out of the page, and opens it in a drawer on asking', async () => {
      open = { status: 'found', open: [openItem()] };
      const fixture = await change();

      // The page says one line about it, and nothing of the list.
      expect(text(fixture)).toContain('1 suggestion is waiting for your answer');
      expect(root(fixture).querySelector('cp-ai-proposal-panel')).toBeNull();

      const show = Array.from(root(fixture).querySelectorAll('button')).find(
        (button) => (button.textContent ?? '').trim() === 'Show suggestions',
      )!;
      show.click();
      await settle(fixture);

      // An open drawer moves its content to <body>, and the page behind it is inert.
      const drawer = document.body.querySelector('[role="dialog"]')!;
      expect(drawer.textContent).toContain('Suggestions waiting for you');
      expect(drawer.textContent).toContain('Suggested changes');
      expect(drawer.textContent).toContain('2 changes');
      expect(drawer.querySelector('cp-ai-proposal-panel')).not.toBeNull();
      expect(root(fixture).querySelector('cp-ai-proposal-panel')).toBeNull();

      document.body.querySelector<HTMLButtonElement>('[aria-label="Close suggestions"]')!.click();
      await settle(fixture);

      expect(document.body.querySelector('[role="dialog"]')).toBeNull();
      expect(text(fixture)).toContain('1 suggestion is waiting for your answer');
    });

    it('opens a suggestion made before the latest save as stale, and asking again opens the way that asks for it', async () => {
      open = { status: 'found', open: [openItem({ isStale: true, taskType: 'RecipeAdaptation' })] };
      const fixture = await change();

      // The panel is in the drawer, so it is on the page once the drawer is open.
      Array.from(root(fixture).querySelectorAll('button'))
        .find((button) => (button.textContent ?? '').trim() === 'Show suggestions')!
        .click();
      await settle(fixture);

      const panel = fixture.debugElement.query((node) => node.componentInstance instanceof AiProposalPanelComponent)
        .componentInstance as AiProposalPanelComponent;
      expect(panel.knownStale()).toBeTrue();
      expect(panel.headingLevel()).toBe(4);

      panel.restartRequested.emit({
        taskType: 'RecipeAdaptation',
        task: null,
        scope: 'WholeRecipe',
        staleSourceVersionId: null,
      });
      await settle(fixture);

      expect(root(fixture).querySelector('cp-recipe-adaptation-request')).not.toBeNull();
      expect(focusStep).toHaveBeenCalled();
    });

    it('says how many are waiting in one line, and shows nothing when nothing waits', async () => {
      open = { status: 'found', open: [openItem(), openItem({ requestId: 'req-2' })] };
      const fixture = await change();

      const line = root(fixture).querySelector('.waiting-line')!;
      expect(line.textContent).toContain('2 suggestions are waiting for your answer');
      expect(line.querySelector('button')!.textContent).toContain('Show suggestions');

      open = { status: 'found', open: [] };
      const none = await change();
      expect(root(none).querySelector('.waiting-line')).toBeNull();
    });

    it('has the tool open what it asks for in a drawer too', async () => {
      const fixture = await change();

      const revision = fixture.debugElement.query(
        (node) => node.componentInstance instanceof RecipeRevisionRequestComponent,
      ).componentInstance as RecipeRevisionRequestComponent;
      expect(revision.suggestionsInDrawer()).toBeTrue();
    });

    it('steps Continue forward while there are edits to keep, since Continue keeps them', async () => {
      const fixture = await change();
      await pick(fixture, 'recipe-improve-way-edit');
      expect(port.registration()!.ready!()).toBeFalse();

      editorOf(fixture)!.form.notes.set('More salt.');

      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('keeps the editor, and the tile that says so, when its edits cannot be kept on switching', async () => {
      const fixture = await change();
      await pick(fixture, 'recipe-improve-way-edit');
      editorOf(fixture)!.form.title.set('');
      editorOf(fixture)!.form.notes.set('x');

      await pick(fixture, 'recipe-improve-way-check');

      expect(editorOf(fixture)).not.toBeNull();
      expect(root(fixture).querySelector<HTMLInputElement>('#recipe-improve-way-edit')!.checked).toBeTrue();
    });

    it('edits the whole recipe in the hosted editor, and saves it rather than discarding it on moving to another way', async () => {
      recipes.updateRecipe.and.resolveTo({
        status: 'updated',
        recipe: detail({ notes: 'More salt.' }),
        replayed: false,
      });
      const fixture = await change();
      await pick(fixture, 'recipe-improve-way-edit');

      expect(root(fixture).querySelector('#section-details')).not.toBeNull();
      expect(root(fixture).querySelector('#section-ingredients')).not.toBeNull();
      expect(root(fixture).querySelector('#section-instructions')).not.toBeNull();
      expect(port.registration()!.capabilityKey!()).toBe('recipes.edit');

      editorOf(fixture)!.form.notes.set('More salt.');
      expect(port.unsaved()).toBeTrue();
      await pick(fixture, 'recipe-improve-way-check');

      expect(recipes.updateRecipe).toHaveBeenCalledTimes(1);
      expect(root(fixture).querySelector('cp-recipe-review-request')).not.toBeNull();
    });

    it('is ready to move on only once the recipe has a version saved during the run', async () => {
      const fixture = await change();
      expect(port.registration()!.ready!()).toBeFalse();

      recipes.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: detail({
          currentVersion: {
            id: 'v4',
            versionNumber: 4,
            source: 'AiProposalAccepted',
            readiness: 'Draft',
            reason: null,
            createdAt: '2026-10-15T09:30:00Z',
          },
        }),
      });
      await fixture.componentInstance.load();
      await settle(fixture);

      expect(port.registration()!.ready!()).toBeTrue();
      expect(text(fixture)).toContain('Your recipe has changes saved since you started.');
    });
  });

  describe('Decide on every suggestion', () => {
    it('says so when nothing is waiting, and is ready to move on', async () => {
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Nothing is waiting for your answer.');
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('lists everything still waiting, a safety note first, and is not ready while any waits', async () => {
      open = {
        status: 'found',
        open: [
          openItem({ requestId: 'plain' }),
          openItem({ requestId: 'careful', hasSafetyCaution: true, taskType: 'RecipeAdaptation' }),
        ],
      };
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      const items = Array.from(root(fixture).querySelectorAll('li')).map((li) => li.textContent ?? '');
      expect(items.length).toBe(2);
      expect(items[0]).toContain('An adapted version');
      expect(items[0]).toContain('safety note');
      expect(port.registration()!.ready!()).toBeFalse();
    });

    /** What a panel reads for a suggestion that is ready but was asked for against an earlier version. */
    function readyProposal(requestId: string): unknown {
      return {
        status: 'found',
        operation: {
          aiProposalRequestId: requestId,
          status: 'Proposed',
          taskType: 'RecipeRevision',
          scope: 'WholeRecipe',
          sourceVersionId: 'v1',
          requestedAt: '2026-10-15T09:05:00Z',
          statusChangedAt: '2026-10-15T09:06:00Z',
          failureCategory: null,
          proposal: {
            proposalId: 'p-' + requestId,
            outputSchemaVersion: 'recipe.revision.v1',
            promptTemplateId: 'recipe.revision',
            promptTemplateVersion: '1.0.0',
            promptTemplateBodyChecksum: 'sha256:abc',
            providerName: 'test-provider',
            modelName: 'test-model',
            createdAt: '2026-10-15T09:06:00Z',
            changes: [],
            warnings: [],
          },
        },
      };
    }

    function rows(fixture: ComponentFixture<unknown>): HTMLDetailsElement[] {
      return Array.from(root(fixture).querySelectorAll<HTMLDetailsElement>('details.row'));
    }

    function buttonIn(parent: ParentNode, label: string): HTMLButtonElement | undefined {
      return Array.from(parent.querySelectorAll('button')).find(
        (button) => (button.textContent ?? '').trim() === label,
      );
    }

    it('shows each waiting suggestion as a closed row that says how many changes and when, with a count above', async () => {
      open = {
        status: 'found',
        open: [openItem({ requestId: 'a' }), openItem({ requestId: 'b', pendingChangeCount: 1 })],
      };
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      const list = rows(fixture);
      expect(list.length).toBe(2);
      expect(list.every((row) => !row.open)).toBeTrue();
      expect(list[0].querySelector('summary')!.textContent).toContain('2 changes');
      expect(list[1].querySelector('summary')!.textContent).toContain('1 change');
      expect(list[0].querySelector('summary')!.textContent).toContain('asked');
      expect(text(fixture)).toContain('Still waiting for your answer (2)');
    });

    it('opens the only suggestion, and one with a safety note, by itself', async () => {
      open = { status: 'found', open: [openItem({ requestId: 'only' })] };
      const single = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expect(rows(single)[0].open).toBeTrue();

      open = {
        status: 'found',
        open: [openItem({ requestId: 'plain' }), openItem({ requestId: 'careful', hasSafetyCaution: true })],
      };
      const several = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      const [careful, plain] = rows(several);
      expect(careful.open).toBeTrue();
      expect(careful.textContent).toContain('Safety note');
      expect(plain.open).toBeFalse();
    });

    it('keeps what the creator opened or closed when the list is read again', async () => {
      open = {
        status: 'found',
        open: [openItem({ requestId: 'a' }), openItem({ requestId: 'b' })],
      };
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      rows(fixture)[1].open = true;
      await settle(fixture);
      await fixture.debugElement
        .query((node) => node.componentInstance instanceof RecipeOpenSuggestionsComponent)
        .componentInstance.reload();
      await settle(fixture);

      expect(rows(fixture)[1].open).toBeTrue();
      expect(rows(fixture)[0].open).toBeFalse();
    });

    it('keeps an out-of-date suggestion closed, with its ways out on the row rather than inside it', async () => {
      open = { status: 'found', open: [openItem({ requestId: 'old', isStale: true })] };
      watchHandler = () => of(readyProposal('old'));
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      const [row] = rows(fixture);
      expect(row.open).toBeFalse();
      expect(row.querySelector('summary')!.textContent).toContain('Out of date');

      // On the row, outside the part that opens, and only once: the panel does not draw them again.
      const actions = root(fixture).querySelector('.row-actions')!;
      expect(buttonIn(actions, 'Start again from this recipe')).toBeTruthy();
      expect(row.contains(actions)).toBeFalse();
      expect(
        Array.from(root(fixture).querySelectorAll('button')).filter(
          (button) => (button.textContent ?? '').trim() === 'Start again from this recipe',
        ).length,
      ).toBe(1);

      buttonIn(actions, 'Start again from this recipe')!.click();
      await settle(fixture);
      expect(text(fixture)).toContain('go Back to “Make your changes”');
    });

    it('says nothing twice: no "not saved" chip and no "ready" status on a suggestion the row already describes', async () => {
      open = { status: 'found', open: [openItem({ requestId: 'only' })] };
      watchHandler = () => of(readyProposal('only'));
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(text(fixture)).not.toContain('Not saved to your recipe yet');
      expect(root(fixture).querySelector('cp-ai-operation-status')).toBeNull();
    });

    it('declines every out-of-date suggestion at once, after one question', async () => {
      memberships.set({
        status: 'ready',
        memberships: [
          {
            workspaceId: 'w1',
            workspaceSlug: SLUG,
            workspaceName: 'Cozy Fall',
            membershipId: 'm1',
            role: 'Owner',
            status: 'Active',
          },
        ],
      });
      open = {
        status: 'found',
        open: [
          openItem({ requestId: 'old-1', isStale: true }),
          openItem({ requestId: 'old-2', isStale: true }),
          openItem({ requestId: 'current' }),
        ],
      };
      watchHandler = () => of(readyProposal('old'));
      disposition.and.resolveTo({
        status: 'decided',
        result: {
          aiProposalRequestId: 'old',
          status: 'Rejected',
          acceptedChangeCount: 0,
          rejectedChangeCount: 0,
          recipeVersionNumber: null,
          decidedAt: '2026-10-15T09:10:00Z',
        },
      });
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      const all = buttonIn(root(fixture), 'Decline all out-of-date (2)');
      expect(all).toBeTruthy();

      all!.click();
      await settle(fixture, 6);

      const confirm = TestBed.inject(ConfirmService).confirm as jasmine.Spy;
      expect(confirm).toHaveBeenCalledTimes(1);
      expect(confirm.calls.mostRecent().args[0].title).toContain('Decline 2 out-of-date suggestions');
      expect(disposition).toHaveBeenCalledTimes(2);
    });

    it('offers no decline-all for a single out-of-date suggestion', async () => {
      memberships.set({
        status: 'ready',
        memberships: [
          {
            workspaceId: 'w1',
            workspaceSlug: SLUG,
            workspaceName: 'Cozy Fall',
            membershipId: 'm1',
            role: 'Owner',
            status: 'Active',
          },
        ],
      });
      open = { status: 'found', open: [openItem({ requestId: 'old', isStale: true }), openItem({ requestId: 'new' })] };
      watchHandler = () => of(readyProposal('old'));
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(buttonIn(root(fixture), 'Decline these')).toBeTruthy();
      expect(text(fixture)).not.toContain('Decline all out-of-date');
    });

    it('offers Try again when the list cannot be read', async () => {
      open = { status: 'unavailable' };
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('couldn’t be read just now');
      open = { status: 'found', open: [] };
      Array.from(root(fixture).querySelectorAll('button'))
        .find((b) => b.textContent?.includes('Try again'))!
        .click();
      await settle(fixture);

      expect(text(fixture)).toContain('Nothing is waiting for your answer.');
    });
  });

  describe('Compare with before', () => {
    const COMPARISON: RecipeVersionComparison = {
      from: {
        versionId: 'v3',
        versionNumber: 3,
        source: 'CreatorEdit',
        readiness: 'Draft',
        createdAt: '2026-10-01T09:00:00Z',
      },
      to: {
        versionId: 'v5',
        versionNumber: 5,
        source: 'AiProposalAccepted',
        readiness: 'Draft',
        createdAt: '2026-10-15T09:40:00Z',
      },
      comparison: { sections: [], hasChanges: false },
    };

    it('compares the version the run began on with the recipe as it is now', async () => {
      recipes.getVersionHistory.and.resolveTo({
        status: 'found',
        page: {
          items: [
            version(5, '2026-10-15T09:40:00Z'),
            version(4, '2026-10-15T09:20:00Z'),
            version(3, '2026-10-01T09:00:00Z'),
          ],
          nextCursor: null,
        },
      });
      recipes.compareVersions.and.resolveTo({ status: 'found', comparison: COMPARISON });

      const fixture = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(recipes.compareVersions).toHaveBeenCalledOnceWith(SLUG, 'recipe-1', 3, 5);
      expect(root(fixture).querySelector('cp-recipe-version-comparison')).not.toBeNull();
      expect(text(fixture)).toContain('Version 3, from before you started, beside version 5');
    });

    it('says nothing changed when no version was saved during the run', async () => {
      recipes.getVersionHistory.and.resolveTo({
        status: 'found',
        page: { items: [version(3, '2026-10-01T09:00:00Z')], nextCursor: null },
      });

      const fixture = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Nothing has changed since you started.');
      expect(recipes.compareVersions).not.toHaveBeenCalled();
    });

    it('offers Try again when the versions cannot be read', async () => {
      recipes.getVersionHistory.and.resolveTo({ status: 'unavailable' });

      const fixture = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('couldn’t be compared just now');
    });
  });
});
