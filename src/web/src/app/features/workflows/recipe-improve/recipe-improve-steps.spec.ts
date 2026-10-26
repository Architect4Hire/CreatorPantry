import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
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
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { RecipeImproveChangeStepComponent } from './recipe-improve-change-step.component';
import { RecipeChooseStepComponent } from '../recipe-steps/recipe-choose-step.component';
import { RecipeImproveCompareStepComponent } from './recipe-improve-compare-step.component';
import { RecipeImproveDecideStepComponent } from './recipe-improve-decide-step.component';

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
    currentVersion: { id: 'v3', versionNumber: 3, source: 'CreatorEdit', readiness: 'Draft', reason: null, createdAt: '2026-10-01T09:00:00Z' },
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
  return decodeWorkflowRun(wireRun({
    definitionKey: 'recipe.improve',
    startedAt: STARTED,
    links: options.recipe === false ? [] : [{ kind: 'Recipe', resourceId: 'recipe-1', stepKey: null }],
  }))!;
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

function configure(): void {
  port = new WorkflowStepPort();
  focusStep = jasmine.createSpy('focusStep');
  port.connect((_draft: WorkflowResourcePointer) => Promise.resolve(true), focusStep);
  recipes = {
    getRecipeDetail: jasmine.createSpy('getRecipeDetail').and.resolveTo({ status: 'found', recipe: detail() }),
    updateRecipe: jasmine.createSpy('updateRecipe').and.resolveTo({ status: 'unavailable' }),
    searchRecipes: jasmine.createSpy('searchRecipes').and.returnValue(
      of<RecipeSearchOutcome>({ status: 'found', page: { items: [summary('recipe-1', 'Soda bread'), summary('recipe-2', 'Rye loaf')], nextCursor: null, totalCount: 2 } }),
    ),
    getVersionHistory: jasmine.createSpy('getVersionHistory'),
    compareVersions: jasmine.createSpy('compareVersions'),
  };
  open = { status: 'found', open: [] };
  listOpen = jasmine.createSpy('listOpen').and.callFake(() => Promise.resolve(open));

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
          watchStatus: () => NEVER,
          requestProposal: jasmine.createSpy('requestProposal'),
          disposition: jasmine.createSpy('disposition'),
        },
      },
      { provide: ConfirmService, useValue: { confirm: jasmine.createSpy('confirm').and.resolveTo(true) } },
      { provide: WorkspaceMembershipService, useValue: { state: signal({ status: 'loading' }), ensureLoaded: () => Promise.resolve() } },
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
      Array.from(status.parentElement!.children).find((each) => each.matches('[aria-busy="true"]') && each.querySelector('cp-skeleton'));

    function expectLoading(scope: Element, sentence: string): void {
      const status = statuses(scope).find((p) => p.textContent === sentence);
      expect(status).withContext(sentence).toBeTruthy();
      const busy = busyBeside(status!)!;
      expect(busy).toBeTruthy();
      expect(busy.textContent!.trim()).toBe('');
      expect(Array.from(scope.querySelectorAll('p:not(.cp-sr-only)')).some((p) => p.textContent?.includes(sentence))).toBeFalse();
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
      const fixture = await render(RecipeChooseStepComponent, { run: improveRun({ recipe: false }), workspaceSlug: SLUG });
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle(fixture);
      expectLoading(root(fixture), 'Finding your recipes…');

      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({ status: 'found', page: { items: [summary('recipe-1', 'Soda bread')], nextCursor: null, totalCount: 1 } }),
      );
      const read = await render(RecipeChooseStepComponent, { run: improveRun({ recipe: false }), workspaceSlug: SLUG });
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle(read);
      expectRead(root(read), 'Finding your recipes…');
    });

    it('Choose the recipe: its pager says "Loading more…" while it reads the next page', async () => {
      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({ status: 'found', page: { items: [summary('recipe-1', 'Soda bread')], nextCursor: 'next' as string | null, totalCount: 2 } }),
      );
      const fixture = await render(RecipeChooseStepComponent, { run: improveRun({ recipe: false }), workspaceSlug: SLUG });
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle(fixture);

      recipes.searchRecipes.and.returnValue(NEVER);
      const more = () => Array.from(root(fixture).querySelectorAll('button')).find((b) => b.textContent?.includes('more'))!;
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
      expectLoading(root(fixture).querySelector('cp-recipe-open-suggestions')!, 'Looking for suggestions waiting for you…');

      listOpen.and.callFake(() => Promise.resolve(open));
      const read = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectRead(root(read).querySelector('cp-recipe-open-suggestions')!, 'Looking for suggestions waiting for you…');
    });

    it('Compare with before: draws lines while it compares, then keeps its status emptied', async () => {
      recipes.getVersionHistory.and.returnValue(never());
      const fixture = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectLoading(root(fixture), 'Comparing your recipe with how it was…');

      recipes.getVersionHistory.and.resolveTo({ status: 'found', page: { items: [version(3, '2026-10-01T09:00:00Z')], nextCursor: null } });
      const read = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });
      expectRead(root(read), 'Comparing your recipe with how it was…');
    });
  });

  describe('Choose the recipe', () => {
    async function choose(run = improveRun({ recipe: false })) {
      const fixture = await render(RecipeChooseStepComponent, { run, workspaceSlug: SLUG });
      // The list is asked for once typing settles.
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle(fixture);
      return fixture;
    }

    it('keeps the pick on show when a later search would leave it out, and lists more on asking', async () => {
      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({ status: 'found', page: { items: [summary('recipe-1', 'Soda bread')], nextCursor: 'next' as string | null, totalCount: 2 } }),
      );
      const fixture = await choose();
      await pick(fixture, 'recipe-choose-recipe-1');

      recipes.searchRecipes.and.returnValue(
        of<RecipeSearchOutcome>({ status: 'found', page: { items: [summary('recipe-2', 'Rye loaf')], nextCursor: null, totalCount: 1 } }),
      );
      Array.from(root(fixture).querySelectorAll('button')).find((b) => b.textContent?.includes('Show more recipes'))!.click();
      await settle(fixture);
      expect(text(fixture)).toContain('Rye loaf');

      const search = root(fixture).querySelector<HTMLInputElement>('input[type="search"]')!;
      search.value = 'rye';
      search.dispatchEvent(new Event('input'));
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle(fixture);

      expect(root(fixture).querySelector<HTMLInputElement>('#recipe-choose-recipe-1')!.checked).toBeTrue();
      expect(port.registration()!.results!()).toEqual([{ kind: 'Recipe', id: 'recipe-1' }]);
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
        of<RecipeSearchOutcome>({ status: 'found', page: { items: [summary('recipe-2', 'Rye loaf')], nextCursor: 'more', totalCount: 60 } }),
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

      recipes.searchRecipes.and.returnValue(of({ status: 'found', page: { items: [], nextCursor: null, totalCount: 0 } }));
      Array.from(root(fixture).querySelectorAll('button')).find((b) => b.textContent?.includes('Try again'))!.click();
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle(fixture);

      expect(text(fixture)).toContain('There are no recipes to improve yet.');
    });
  });

  describe('Make your changes', () => {
    function change() {
      return render(RecipeImproveChangeStepComponent, { run: improveRun(), workspaceSlug: SLUG });
    }

    it('offers each way in plain words, with no unscoped "make it better", and asks for nothing until one is chosen', async () => {
      const fixture = await change();

      for (const label of ['Change part of it', 'Adapt it for a need', 'Swap an ingredient', 'Check it over', 'Change it myself']) {
        expect(text(fixture)).toContain(label);
      }
      expect(text(fixture)).not.toMatch(/make it better|prompt|model/i);
      expect(root(fixture).querySelector('cp-recipe-revision-request')).toBeNull();
      expect(editorOf(fixture)).toBeNull();
    });

    it('opens the scoped revision on the recipe’s current version', async () => {
      const fixture = await change();
      await pick(fixture, 'recipe-improve-way-revise');

      expect(root(fixture).querySelector('cp-recipe-revision-request')).not.toBeNull();
      expect(port.registration()!.capabilityKey!()).toBe('recipes.suggest-changes');
    });

    it('shows a suggestion a reload left waiting, in the proposal panel, before the ways', async () => {
      open = { status: 'found', open: [openItem()] };
      const fixture = await change();

      expect(text(fixture)).toContain('Waiting for your answer');
      expect(text(fixture)).toContain('Suggested changes');
      expect(text(fixture)).toContain('2 changes wait for your answer.');
      expect(root(fixture).querySelector('cp-ai-proposal-panel')).not.toBeNull();
    });

    it('opens a suggestion made before the latest save as stale, and asking again opens the way that asks for it', async () => {
      open = { status: 'found', open: [openItem({ isStale: true, taskType: 'RecipeAdaptation' })] };
      const fixture = await change();

      const panel = fixture.debugElement.query((node) => node.componentInstance instanceof AiProposalPanelComponent)
        .componentInstance as AiProposalPanelComponent;
      expect(panel.knownStale()).toBeTrue();
      expect(panel.headingLevel()).toBe(4);

      panel.restartRequested.emit({ taskType: 'RecipeAdaptation', task: null, scope: 'WholeRecipe', staleSourceVersionId: null });
      await settle(fixture);

      expect(root(fixture).querySelector('cp-recipe-adaptation-request')).not.toBeNull();
      expect(focusStep).toHaveBeenCalled();
    });

    it('folds what is waiting into a line that says how many, and shows nothing when nothing waits', async () => {
      open = { status: 'found', open: [openItem(), openItem({ requestId: 'req-2' })] };
      const fixture = await change();

      const fold = root(fixture).querySelector<HTMLDetailsElement>('details.waiting')!;
      expect(fold.hidden).toBeFalse();
      expect(fold.open).toBeFalse();
      expect(fold.querySelector('summary')!.textContent).toContain('2 suggestions are waiting for your answer');
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
      recipes.updateRecipe.and.resolveTo({ status: 'updated', recipe: detail({ notes: 'More salt.' }), replayed: false });
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
        recipe: detail({ currentVersion: { id: 'v4', versionNumber: 4, source: 'AiProposalAccepted', readiness: 'Draft', reason: null, createdAt: '2026-10-15T09:30:00Z' } }),
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
        open: [openItem({ requestId: 'plain' }), openItem({ requestId: 'careful', hasSafetyCaution: true, taskType: 'RecipeAdaptation' })],
      };
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      const items = Array.from(root(fixture).querySelectorAll('li')).map((li) => li.textContent ?? '');
      expect(items.length).toBe(2);
      expect(items[0]).toContain('An adapted version');
      expect(items[0]).toContain('safety note');
      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('offers Try again when the list cannot be read', async () => {
      open = { status: 'unavailable' };
      const fixture = await render(RecipeImproveDecideStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('couldn’t be read just now');
      open = { status: 'found', open: [] };
      Array.from(root(fixture).querySelectorAll('button')).find((b) => b.textContent?.includes('Try again'))!.click();
      await settle(fixture);

      expect(text(fixture)).toContain('Nothing is waiting for your answer.');
    });
  });

  describe('Compare with before', () => {
    const COMPARISON: RecipeVersionComparison = {
      from: { versionId: 'v3', versionNumber: 3, source: 'CreatorEdit', readiness: 'Draft', createdAt: '2026-10-01T09:00:00Z' },
      to: { versionId: 'v5', versionNumber: 5, source: 'AiProposalAccepted', readiness: 'Draft', createdAt: '2026-10-15T09:40:00Z' },
      comparison: { sections: [], hasChanges: false },
    };

    it('compares the version the run began on with the recipe as it is now', async () => {
      recipes.getVersionHistory.and.resolveTo({
        status: 'found',
        page: { items: [version(5, '2026-10-15T09:40:00Z'), version(4, '2026-10-15T09:20:00Z'), version(3, '2026-10-01T09:00:00Z')], nextCursor: null },
      });
      recipes.compareVersions.and.resolveTo({ status: 'found', comparison: COMPARISON });

      const fixture = await render(RecipeImproveCompareStepComponent, { run: improveRun(), workspaceSlug: SLUG });

      expect(recipes.compareVersions).toHaveBeenCalledOnceWith(SLUG, 'recipe-1', 3, 5);
      expect(root(fixture).querySelector('cp-recipe-version-comparison')).not.toBeNull();
      expect(text(fixture)).toContain('Version 3, from before you started, beside version 5');
    });

    it('says nothing changed when no version was saved during the run', async () => {
      recipes.getVersionHistory.and.resolveTo({ status: 'found', page: { items: [version(3, '2026-10-01T09:00:00Z')], nextCursor: null } });

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
