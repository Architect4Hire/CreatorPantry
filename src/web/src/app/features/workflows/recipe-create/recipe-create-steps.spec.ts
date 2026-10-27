import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { NEVER, Observable, of } from 'rxjs';

import { ConfirmService } from '../../../core/confirm.service';
import { RecipeDetail } from '../../../models/recipe.models';
import {
  WorkflowNextAction,
  WorkflowResourcePointer,
  WorkflowRun,
  decodeWorkflowRun,
} from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { RecipeConceptService, WatchConceptsOutcome } from '../../../services/recipe-concept.service';
import { RecipeDraftService, WatchRecipeDraftOutcome } from '../../../services/recipe-draft.service';
import { RecipeService } from '../../../services/recipe.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeStudioHost } from '../../ai/recipe-studio-host';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { RecipeCreatePartStepComponent } from './recipe-create-part-step.component';
import { RecipeSizesStepComponent } from '../recipe-steps/recipe-sizes-step.component';
import { RecipeCreateStartStepComponent } from './recipe-create-start-step.component';

const SLUG = 'sams-kitchen';

const DETAIL: RecipeDetail = {
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
  createdAt: '2026-10-15T09:00:00Z',
  updatedAt: '2026-10-15T09:05:00Z',
  concurrencyToken: 'AAAAAAAAB9E=',
  currentVersion: null,
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  assetLinks: [],
  tags: [],
};

const BOTH_WAYS: WorkflowNextAction = {
  stepKey: 'start',
  title: 'Start your recipe',
  help: 'Write it yourself, or get a first draft.',
  requirement: 'Required',
  actions: [
    {
      capabilityKey: 'recipes.new',
      name: 'Write it yourself',
      route: 'recipes/new',
      available: true,
      youCanDoIt: true,
    },
    {
      capabilityKey: 'recipes.draft-with-help',
      name: 'Help me draft it',
      route: 'ai-recipe-studio',
      available: true,
      youCanDoIt: true,
    },
  ],
  waitingFor: [],
};

/** A "Create a recipe" run on its first step, holding what each spec says it holds. */
function startRun(options: { draft?: WorkflowResourcePointer; linked?: string } = {}): WorkflowRun {
  const steps = (wireRun()['steps'] as Record<string, unknown>[]).map((step) =>
    step['key'] === 'start'
      ? {
          ...step,
          status: options.draft ? 'InProgress' : 'NotStarted',
          completedAt: null,
          draftKind: options.draft?.kind ?? null,
          draftResourceId: options.draft?.id ?? null,
        }
      : step,
  );
  const run = decodeWorkflowRun(
    wireRun({
      completedStepCount: 0,
      steps,
      links: options.linked ? [{ kind: 'Recipe', resourceId: options.linked, stepKey: 'start' }] : [],
    }),
  );
  if (!run) throw new Error('A test run did not decode.');
  return run;
}

/** The run with its recipe linked, as the steps after the first see it. */
function laterRun(): WorkflowRun {
  return decodeWorkflowRun(wireRun())!;
}

let port: WorkflowStepPort;
let keepDraft: jasmine.Spy<(draft: WorkflowResourcePointer) => Promise<boolean>>;
let focusStep: jasmine.Spy<() => void>;
let recipes: { getRecipeDetail: jasmine.Spy; createRecipe: jasmine.Spy; updateRecipe: jasmine.Spy };
let draftStatus: WatchRecipeDraftOutcome;
let draftWatches = 0;

function configure(): void {
  port = new WorkflowStepPort();
  keepDraft = jasmine.createSpy('keepDraft').and.resolveTo(true);
  focusStep = jasmine.createSpy('focusStep');
  port.connect(keepDraft, focusStep);
  recipes = {
    getRecipeDetail: jasmine.createSpy('getRecipeDetail').and.resolveTo({ status: 'found', recipe: DETAIL }),
    createRecipe: jasmine.createSpy('createRecipe').and.resolveTo({ status: 'unavailable' }),
    updateRecipe: jasmine.createSpy('updateRecipe').and.resolveTo({ status: 'unavailable' }),
  };
  draftStatus = { status: 'not_found' };
  draftWatches = 0;

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: RecipeService, useValue: recipes },
      {
        provide: RecipeDraftService,
        useValue: {
          // The step's own look answers; the review's polls after it are left unanswered, as a request still working.
          watchStatus: (): Observable<WatchRecipeDraftOutcome> => (draftWatches++ === 0 ? of(draftStatus) : NEVER),
          requestDraft: jasmine.createSpy('requestDraft'),
          acceptDraft: jasmine.createSpy('acceptDraft'),
        },
      },
      {
        provide: RecipeConceptService,
        useValue: {
          watchStatus: (): Observable<WatchConceptsOutcome> => NEVER,
          requestConcepts: jasmine.createSpy('requestConcepts'),
        },
      },
      { provide: ConfirmService, useValue: { confirm: jasmine.createSpy('confirm').and.resolveTo(true) } },
      {
        provide: WorkspaceMembershipService,
        useValue: { state: signal({ status: 'loading' }), ensureLoaded: () => Promise.resolve() },
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

async function settle<T>(fixture: ComponentFixture<T>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }
}

function root<T>(fixture: ComponentFixture<T>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function editorOf<T>(fixture: ComponentFixture<T>): RecipeEditorComponent | null {
  const found = fixture.debugElement.query((node) => node.componentInstance instanceof RecipeEditorComponent);
  return (found?.componentInstance as RecipeEditorComponent | undefined) ?? null;
}

describe('"Create a recipe" step screens (13A.14)', () => {
  beforeEach(() => configure());

  // AU.7: what a step is waiting for arrives as its shape, and is said once from a region that is always there.
  describe('while a step reads what it needs', () => {
    const statuses = (host: HTMLElement) => Array.from(host.querySelectorAll('p.cp-sr-only[role="status"]'));
    /** The busy lines drawn beside a status: the step's own, not a child panel's. */
    const busyBeside = (status: Element) =>
      Array.from(status.parentElement!.children).find(
        (each) => each.matches('[aria-busy="true"]') && each.querySelector('cp-skeleton'),
      );

    function expectLoading(host: HTMLElement, sentence: string): void {
      const status = statuses(host).find((p) => p.textContent === sentence);
      expect(status).withContext(sentence).toBeTruthy();
      const busy = busyBeside(status!)!;
      expect(busy).toBeTruthy();
      expect(busy.textContent!.trim()).toBe('');
      expect(
        Array.from(host.querySelectorAll('p:not(.cp-sr-only)')).some((p) => p.textContent?.includes(sentence)),
      ).toBeFalse();
    }

    function expectRead(host: HTMLElement, sentence: string): void {
      // The region stays, its words gone, and the lines beside it with them.
      const emptied = statuses(host).filter((p) => p.textContent === '');
      expect(emptied.length).toBeGreaterThan(0);
      expect(statuses(host).some((p) => p.textContent?.includes(sentence))).toBeFalse();
      expect(emptied.some((p) => busyBeside(p))).toBeFalse();
    }

    it('Start your recipe: draws lines while it picks up an AI request it left, then keeps its status emptied', async () => {
      // The step's own look is left unanswered: the request is still being found.
      draftWatches = 1;
      const fixture = await render(RecipeCreateStartStepComponent, {
        step: BOTH_WAYS,
        run: startRun({ draft: { kind: 'AiRequest', id: 'draft-1' } }),
        workspaceSlug: SLUG,
      });
      expectLoading(root(fixture), 'Picking up where you left off…');

      const read = await render(RecipeCreateStartStepComponent, {
        step: BOTH_WAYS,
        run: startRun(),
        workspaceSlug: SLUG,
      });
      expectRead(root(read), 'Picking up where you left off…');
    });

    it('Try it at another size: draws lines while it reads the recipe, then keeps its status emptied', async () => {
      recipes.getRecipeDetail.and.returnValue(new Promise(() => undefined));
      const fixture = await render(RecipeSizesStepComponent, { run: laterRun(), workspaceSlug: SLUG });
      expectLoading(root(fixture), 'Reading your recipe…');

      recipes.getRecipeDetail.and.resolveTo({ status: 'found', recipe: DETAIL });
      const read = await render(RecipeSizesStepComponent, { run: laterRun(), workspaceSlug: SLUG });
      expectRead(root(read), 'Reading your recipe…');
    });
  });

  describe('Start your recipe', () => {
    function start(run: WorkflowRun, step: WorkflowNextAction = BOTH_WAYS) {
      return render(RecipeCreateStartStepComponent, { step, run, workspaceSlug: SLUG });
    }

    function studioHost(fixture: ComponentFixture<RecipeCreateStartStepComponent>): RecipeStudioHost {
      return fixture.debugElement.injector.get(RecipeStudioHost);
    }

    async function pick(
      fixture: ComponentFixture<RecipeCreateStartStepComponent>,
      value: 'write' | 'help',
    ): Promise<void> {
      root(fixture).querySelector<HTMLInputElement>(`#recipe-start-${value}`)!.click();
      await settle(fixture);
    }

    it('offers the two ways to start in plain words, and nothing else until one is picked', async () => {
      const fixture = await start(startRun());

      expect(root(fixture).querySelector('[role="radiogroup"]')?.getAttribute('aria-label')).toBe(
        'How do you want to start?',
      );
      expect(root(fixture).textContent).toContain('Write it yourself');
      expect(root(fixture).textContent).toContain('Help me draft it');
      expect(root(fixture).textContent).not.toMatch(/prompt|model|AI request/i);
      expect(editorOf(fixture)).toBeNull();
      expect(root(fixture).querySelector('cp-recipe-concept-studio')).toBeNull();
    });

    it('writes a new recipe in the editor, points the run at it once made, and names it as the step’s result', async () => {
      recipes.createRecipe.and.resolveTo({
        status: 'created',
        recipe: {
          recipeId: 'recipe-9',
          title: 'Soda bread',
          status: 'Draft',
          versionId: 'v1',
          versionNumber: 1,
          createdAt: '2026-10-15T09:00:00Z',
        },
        replayed: false,
      });
      const fixture = await start(startRun());
      await pick(fixture, 'write');

      expect(root(fixture).querySelector('#section-details')).not.toBeNull();
      expect(port.registration()!.capabilityKey!()).toBe('recipes.new');

      editorOf(fixture)!.form.title.set('Soda bread');
      expect(port.unsaved()).toBeTrue();
      await expectAsync(port.flush()).toBeResolvedTo(true);
      await settle(fixture);

      expect(recipes.createRecipe).toHaveBeenCalledTimes(1);
      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'Recipe', id: 'recipe-9' });
      expect(port.registration()!.draft!()).toEqual({ kind: 'Recipe', id: 'recipe-9' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'Recipe', id: 'recipe-9' }]);
      // The same step now edits the recipe it made: a second save updates it, never makes another.
      expect(root(fixture).textContent).toContain('Your recipe is in your library.');
      expect(recipes.getRecipeDetail).toHaveBeenCalledWith(SLUG, 'recipe-9');
      expect(root(fixture).querySelector('cp-choice-group')).toBeNull();
    });

    it('does not move on while the run could not be pointed at the recipe, and tries again on the next move', async () => {
      recipes.createRecipe.and.resolveTo({
        status: 'created',
        recipe: {
          recipeId: 'recipe-9',
          title: 'Soda bread',
          status: 'Draft',
          versionId: 'v1',
          versionNumber: 1,
          createdAt: '2026-10-15T09:00:00Z',
        },
        replayed: false,
      });
      keepDraft.and.resolveTo(false);
      const fixture = await start(startRun());
      await pick(fixture, 'write');
      editorOf(fixture)!.form.title.set('Soda bread');

      await expectAsync(port.flush()).toBeResolvedTo(false);
      await settle(fixture);
      expect(root(fixture).textContent).toContain('Your work is safe');

      keepDraft.and.resolveTo(true);
      await expectAsync(port.flush()).toBeResolvedTo(true);
      expect(keepDraft).toHaveBeenCalledTimes(2);
      expect(recipes.createRecipe).toHaveBeenCalledTimes(1);
    });

    it('opens on the recipe a reload left it on, without offering the choice again', async () => {
      const fixture = await start(startRun({ draft: { kind: 'Recipe', id: 'recipe-1' } }));

      expect(recipes.getRecipeDetail).toHaveBeenCalledOnceWith(SLUG, 'recipe-1');
      expect(root(fixture).querySelector('cp-choice-group')).toBeNull();
      expect(root(fixture).querySelector('#section-details')).not.toBeNull();
      expect(port.registration()!.results!()).toEqual([{ kind: 'Recipe', id: 'recipe-1' }]);
    });

    it('shows the recipe a step already made when the creator comes back to it, and points nothing anew', async () => {
      const fixture = await start(startRun({ linked: 'recipe-1' }));

      expect(recipes.getRecipeDetail).toHaveBeenCalledOnceWith(SLUG, 'recipe-1');
      expect(root(fixture).querySelector('cp-choice-group')).toBeNull();
      expect(keepDraft).not.toHaveBeenCalled();
    });

    it('opens a draft a reload left it waiting on in the review', async () => {
      draftStatus = {
        status: 'found',
        operation: {
          aiProposalRequestId: 'draft-1',
          status: 'Requested',
          taskType: 'RecipeFirstDraft',
          scope: 'Unspecified',
          sourceVersionId: null,
          requestedAt: '2026-10-15T09:00:00Z',
          statusChangedAt: '2026-10-15T09:00:00Z',
          failureCategory: null,
          proposal: null,
        },
      };
      const fixture = await start(startRun({ draft: { kind: 'AiRequest', id: 'draft-1' } }));

      expect(root(fixture).querySelector('cp-recipe-first-draft-review')).not.toBeNull();
      expect(studioHost(fixture).draftRequestId).toBe('draft-1');
      expect(port.registration()!.capabilityKey!()).toBe('recipes.draft-with-help');
    });

    it('opens ideas a reload left it waiting on in the studio', async () => {
      const fixture = await start(startRun({ draft: { kind: 'AiRequest', id: 'ideas-1' } }));

      expect(root(fixture).querySelector('cp-recipe-concept-studio')).not.toBeNull();
      expect(studioHost(fixture).conceptRequestId).toBe('ideas-1');
      expect(root(fixture).querySelector('cp-choice-group')).toBeNull();
    });

    it('says when what it was waiting on cannot be read, and offers to try again', async () => {
      draftStatus = { status: 'unavailable' };
      const fixture = await start(startRun({ draft: { kind: 'AiRequest', id: 'ideas-1' } }));

      expect(root(fixture).textContent).toContain('couldn’t be read just now');
      draftStatus = { status: 'not_found' };
      draftWatches = 0;
      Array.from(root(fixture).querySelectorAll('button'))
        .find((b) => b.textContent?.includes('Try again'))!
        .click();
      await settle(fixture);

      expect(root(fixture).querySelector('cp-recipe-concept-studio')).not.toBeNull();
    });

    it('keeps each request the studio makes on the run, then the recipe the draft becomes', async () => {
      const fixture = await start(startRun());
      await pick(fixture, 'help');
      expect(root(fixture).querySelector('cp-recipe-concept-studio')).not.toBeNull();
      const host = studioHost(fixture);

      host.conceptsRequested('ideas-1');
      expect(keepDraft).toHaveBeenCalledWith({ kind: 'AiRequest', id: 'ideas-1' });

      host.draftRequested('draft-1');
      await settle(fixture);
      expect(keepDraft).toHaveBeenCalledWith({ kind: 'AiRequest', id: 'draft-1' });
      expect(root(fixture).querySelector('cp-recipe-first-draft-review')).not.toBeNull();

      // Discarding the draft points the run back at the ideas, so a reload does not bring the draft back.
      keepDraft.calls.reset();
      host.draftDiscarded();
      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'AiRequest', id: 'ideas-1' });

      // Leaving the draft goes back to the same ideas, with focus on the step rather than on a control that went.
      host.draftClosed();
      await settle(fixture);
      expect(host.conceptRequestId).toBe('ideas-1');
      expect(focusStep).toHaveBeenCalled();
      host.draftRequested('draft-1');
      await settle(fixture);

      host.accepted('recipe-7');
      await settle(fixture);
      expect(keepDraft).toHaveBeenCalledWith({ kind: 'Recipe', id: 'recipe-7' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'Recipe', id: 'recipe-7' }]);
      expect(root(fixture).textContent).toContain('Your recipe is in your library.');
      expect(recipes.getRecipeDetail).toHaveBeenCalledWith(SLUG, 'recipe-7');
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('is not ready to move on until there is a recipe, so its own screen holds the primary action', async () => {
      const fixture = await start(startRun());
      await pick(fixture, 'help');

      expect(port.registration()!.ready!()).toBeFalse();
      expect(root(fixture).textContent).toContain('How do you want to start?');
    });

    it('takes the only way open at once, with nothing to choose between', async () => {
      const fixture = await start(startRun(), { ...BOTH_WAYS, actions: [BOTH_WAYS.actions[0]] });

      expect(root(fixture).querySelector('cp-choice-group')).toBeNull();
      expect(root(fixture).querySelector('#section-details')).not.toBeNull();
    });

    it('tells a reader who cannot start it that someone else needs to', async () => {
      const fixture = await start(startRun(), {
        ...BOTH_WAYS,
        actions: BOTH_WAYS.actions.map((action) => ({ ...action, youCanDoIt: false })),
      });

      expect(root(fixture).textContent).toContain(
        'Someone with more access to this workspace needs to start this recipe.',
      );
      expect(editorOf(fixture)).toBeNull();
    });
  });

  describe('a part of the recipe', () => {
    for (const [focus, section] of [
      ['ingredients', '#section-ingredients'],
      ['method', '#section-instructions'],
      ['yield-and-time', '#section-timing'],
    ] as const) {
      it(`edits the run's recipe on just its ${focus}`, async () => {
        const fixture = await render(RecipeCreatePartStepComponent, { run: laterRun(), workspaceSlug: SLUG, focus });

        expect(recipes.getRecipeDetail).toHaveBeenCalledOnceWith(SLUG, 'recipe-1');
        expect(root(fixture).querySelector(section)).not.toBeNull();
        expect(root(fixture).querySelector('#section-details')).toBeNull();
        expect(root(fixture).querySelector('cp-page-head')).toBeNull();
      });
    }

    it('keeps its work by saving the recipe when the shell asks', async () => {
      recipes.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...DETAIL, notes: 'x' }, replayed: false });
      const fixture = await render(RecipeCreatePartStepComponent, {
        run: laterRun(),
        workspaceSlug: SLUG,
        focus: 'ingredients',
      });

      expect(port.unsaved()).toBeFalse();
      editorOf(fixture)!.form.notes.set('x');
      expect(port.unsaved()).toBeTrue();

      await expectAsync(port.flush()).toBeResolvedTo(true);
      expect(recipes.updateRecipe).toHaveBeenCalledTimes(1);
      expect(port.registration()!.savedAt!()).not.toBeNull();
    });

    it('says so, rather than showing an empty form, when the run holds no recipe', async () => {
      const fixture = await render(RecipeCreatePartStepComponent, {
        run: startRun(),
        workspaceSlug: SLUG,
        focus: 'method',
      });

      expect(root(fixture).textContent).toContain('There’s no recipe yet.');
      expect(editorOf(fixture)).toBeNull();
    });
  });

  describe('Try it at another size', () => {
    it('shows the server’s scaling preview of the saved recipe, and has nothing to keep', async () => {
      const fixture = await render(RecipeSizesStepComponent, { run: laterRun(), workspaceSlug: SLUG });

      expect(recipes.getRecipeDetail).toHaveBeenCalledOnceWith(SLUG, 'recipe-1');
      expect(root(fixture).querySelector('cp-recipe-scaling-preview')).not.toBeNull();
      expect(root(fixture).querySelector('details cp-recipe-display-normalization')).not.toBeNull();
      expect(port.unsaved()).toBeFalse();
      await expectAsync(port.flush()).toBeResolvedTo(true);
      expect(recipes.updateRecipe).not.toHaveBeenCalled();
    });

    it('offers Try again when the recipe cannot be read', async () => {
      recipes.getRecipeDetail.and.resolveTo({ status: 'unavailable' });
      const fixture = await render(RecipeSizesStepComponent, { run: laterRun(), workspaceSlug: SLUG });

      expect(root(fixture).textContent).toContain('couldn’t be read just now');
      recipes.getRecipeDetail.and.resolveTo({ status: 'found', recipe: DETAIL });
      Array.from(root(fixture).querySelectorAll('button'))
        .find((b) => b.textContent?.includes('Try again'))!
        .click();
      await settle(fixture);

      expect(root(fixture).querySelector('cp-recipe-scaling-preview')).not.toBeNull();
    });
  });

  it('never navigates on its own', async () => {
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    await render(RecipeCreateStartStepComponent, {
      step: BOTH_WAYS,
      run: startRun({ linked: 'recipe-1' }),
      workspaceSlug: SLUG,
    });

    expect(navigate).not.toHaveBeenCalled();
  });
});
