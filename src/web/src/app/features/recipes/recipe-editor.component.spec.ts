import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { RecipeEditorComponent } from './recipe-editor.component';
import { recipeEditorCanDeactivateGuard } from './recipe-editor.guard';
import { RecipeDetail } from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';

const RECIPE_DETAIL: RecipeDetail = {
  id: 'r1',
  title: 'Chili',
  description: null,
  headnote: null,
  notes: null,
  storageNotes: null,
  attributionText: null,
  sourceUrl: null,
  cuisineId: null,
  courseId: null,
  primaryTechniqueId: null,
  prepTimeMinutes: null,
  cookTimeMinutes: null,
  restTimeMinutes: null,
  totalTimeMinutes: null,
  yieldText: null,
  yieldQuantity: null,
  yieldUnitId: null,
  status: 'Draft',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  concurrencyToken: 'AAAAAAAAB9E=',
  currentVersion: null,
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  assetLinks: [],
  tags: [],
};

/** A stub sibling route used to prove a real router-driven navigation away from the editor is (or isn't) blocked. */
@Component({ selector: 'stub-elsewhere', standalone: true, template: 'elsewhere' })
class ElsewhereStubComponent {}

// Every spy defaults to a well-formed 'unavailable' outcome — never bare `undefined` — so a test that
// doesn't care about a particular call (e.g. the constructor's fire-and-forget loadDetail() in a test
// that's really exercising something else) can't crash on `outcome.status` from an unconfigured spy.
function recipeServiceSpy() {
  return {
    getRecipeDetail: jasmine.createSpy('getRecipeDetail').and.resolveTo({ status: 'unavailable' }),
    createRecipe: jasmine.createSpy('createRecipe').and.resolveTo({ status: 'unavailable' }),
    updateRecipe: jasmine.createSpy('updateRecipe').and.resolveTo({ status: 'unavailable' }),
  };
}

async function createHarness(path: string, recipeService: ReturnType<typeof recipeServiceSpy>) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        {
          path: ':workspaceSlug',
          children: [
            {
              path: 'recipes',
              children: [
                { path: 'new', component: RecipeEditorComponent, canDeactivate: [recipeEditorCanDeactivateGuard] },
                { path: ':recipeId', component: RecipeEditorComponent, canDeactivate: [recipeEditorCanDeactivateGuard] },
              ],
            },
            { path: 'elsewhere', component: ElsewhereStubComponent },
          ],
        },
      ]),
      { provide: RecipeService, useValue: recipeService },
    ],
  }).compileComponents();

  const harness = await RouterTestingHarness.create();
  const component = await harness.navigateByUrl(path, RecipeEditorComponent);
  harness.detectChanges();
  return { harness, component };
}

function tabButton(root: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(root.querySelectorAll<HTMLButtonElement>('button[role="tab"]')).find((btn) => btn.textContent?.trim() === label);
}

/** Polls with real macrotask delays rather than a guessed number of microtask ticks, for state that
 *  a real router navigation (several internal async hops deep) sets asynchronously. */
async function waitUntil(predicate: () => boolean, timeoutMs = 2000): Promise<void> {
  const start = Date.now();
  while (!predicate()) {
    if (Date.now() - start > timeoutMs) throw new Error('waitUntil: condition was never met');
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

describe('RecipeEditorComponent', () => {
  describe('create mode', () => {
    it('is ready immediately with a blank form and no detail fetch', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(component.isCreateMode).toBeTrue();
      expect(component.loadState()).toEqual({ status: 'ready' });
      expect(recipeService.getRecipeDetail).not.toHaveBeenCalled();
      expect(harness.routeNativeElement?.querySelector('#recipe-title')).toBeTruthy();
    });

    it('disables Save until a title is entered', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(component.canSave()).toBeFalse();
      component.title.set('Weeknight Chili');
      expect(component.canSave()).toBeTrue();
    });

    it('switches which section is visible when a tab is clicked, not just which tab is marked selected', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);

      const metadataPanel = harness.routeNativeElement!.querySelector('#recipe-title')!.closest('cp-tab-panel') as HTMLElement;
      expect(metadataPanel.hasAttribute('hidden')).toBeFalse();
      expect(getComputedStyle(metadataPanel).display).not.toBe('none');

      const notesTab = tabButton(harness.routeNativeElement!, 'Notes')!;
      notesTab.click();
      harness.detectChanges();

      // A CSS rule that gives cp-tab-panel an unconditional `display` would defeat the component's
      // `[hidden]` attribute and show every section stacked at once — assert the actual rendered
      // display, not just the attribute, so that regression can't slip back in silently.
      expect(metadataPanel.hasAttribute('hidden')).toBeTrue();
      expect(getComputedStyle(metadataPanel).display).toBe('none');

      const notesPanel = harness.routeNativeElement!.querySelector('#recipe-notes')!.closest('cp-tab-panel') as HTMLElement;
      expect(notesPanel.hasAttribute('hidden')).toBeFalse();
      expect(getComputedStyle(notesPanel).display).not.toBe('none');
    });

    it('disables the Media and History tabs (no recipe exists yet)', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);

      const media = tabButton(harness.routeNativeElement!, 'Media');
      const history = tabButton(harness.routeNativeElement!, 'History');
      expect(media?.disabled).toBeTrue();
      expect(history?.disabled).toBeTrue();
    });

    it('creates the recipe with the workspace slug from the route and navigates into edit mode on success', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      await component.save();

      expect(recipeService.createRecipe).toHaveBeenCalledTimes(1);
      const [workspaceSlug, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(workspaceSlug).toBe('cozy-fall');
      expect(request.title).toBe('Weeknight Chili');
      expect(request.status).toBe('Draft');

      const router = TestBed.inject(Router);
      expect(router.url).toBe('/cozy-fall/recipes/new-id');
    });

    it('shows a validation banner and does not navigate when create fails validation', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors: {} });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('x');
      await component.save();
      harness.detectChanges();

      const alert = harness.routeNativeElement?.querySelector('[role="alert"]');
      expect(alert?.textContent).toContain('need attention');
      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/new');
    });
  });

  describe('edit mode', () => {
    it('fetches the recipe detail for the workspace slug and recipe id from the route', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(recipeService.getRecipeDetail).toHaveBeenCalledWith('cozy-fall', 'r1');
      expect(component.loadState()).toEqual({ status: 'ready' });
      expect(component.title()).toBe('Chili');
    });

    it('shows a not-found state distinctly from a generic failure', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'not_found' });
      const { harness } = await createHarness('/cozy-fall/recipes/missing', recipeService);

      expect(harness.routeNativeElement?.textContent).toContain('Recipe not found');
    });

    it('shows a retryable error state on an unavailable load', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'unavailable' });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      const retryButton = harness.routeNativeElement?.querySelector('button') as HTMLButtonElement;
      expect(retryButton.textContent).toContain('Try again');

      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      retryButton.click();
      await harness.fixture.whenStable();
      harness.detectChanges();

      expect(harness.routeNativeElement?.querySelector('#recipe-title')).toBeTruthy();
    });

    it('enables the Media and History tabs once the recipe exists', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      const media = tabButton(harness.routeNativeElement!, 'Media');
      expect(media?.disabled).toBeFalse();
    });

    it('sends every editable field as submitted, plus the loaded concurrency token, on save', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...RECIPE_DETAIL, title: 'New Chili', concurrencyToken: 'AAAAAAAAB9I=' } });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('New Chili');
      await component.save();
      harness.detectChanges();

      const [workspaceSlug, recipeId, request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(workspaceSlug).toBe('cozy-fall');
      expect(recipeId).toBe('r1');
      expect(request.expectedConcurrencyToken).toBe('AAAAAAAAB9E=');
      expect(request.title).toEqual({ submitted: true, value: 'New Chili' });
      expect(request.status).toEqual({ submitted: true, value: 'Draft' });

      expect(harness.routeNativeElement?.querySelector('[role="status"]')?.textContent).toContain('Saved');
    });

    it('shows a conflict banner with a reload action instead of silently overwriting', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'conflict' });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('New Chili');
      await component.save();
      harness.detectChanges();

      const alert = harness.routeNativeElement?.querySelector('[role="alert"]');
      expect(alert?.textContent).toContain('changed elsewhere');

      const latest = { ...RECIPE_DETAIL, title: 'Server Chili', concurrencyToken: 'AAAAAAAAB9Z=' };
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: latest });

      // Reloading discards the local title edit, so it goes through the same discard confirmation
      // as any other way of losing unsaved work — the click doesn't reload synchronously.
      const reloadButton = Array.from(harness.routeNativeElement!.querySelectorAll('button')).find((btn) => btn.textContent?.includes('Reload latest')) as HTMLButtonElement;
      reloadButton.click();
      harness.detectChanges();
      expect(component.leaveConfirmOpen()).toBeTrue();

      const discardButton = Array.from(harness.routeNativeElement!.querySelectorAll('button')).find((btn) => btn.textContent?.trim() === 'Discard changes') as HTMLButtonElement;
      discardButton.click();
      await harness.fixture.whenStable();
      harness.detectChanges();

      expect(component.title()).toBe('Server Chili');
      expect(component.saveState()).toEqual({ status: 'idle' });
    });

    it('discards every unsaved field on a confirmed conflict-reload, not only the one already asserted elsewhere', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'conflict' });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('New Chili');
      component.notes.set('My private notes');
      component.addInstructionGroup();
      await component.save();

      const latest = { ...RECIPE_DETAIL, title: 'Server Chili', notes: null, concurrencyToken: 'AAAAAAAAB9Z=' };
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: latest });

      const reloadPromise = component.reloadAfterConflict();
      component.resolveLeaveConfirm(true);
      await reloadPromise;

      expect(component.title()).toBe('Server Chili');
      expect(component.notes()).toBe('');
      expect(component.instructionGroups()).toEqual([]);
    });
  });

  describe('field-level validation errors', () => {
    it('shows the server message adjacent to its field and marks the control aria-invalid on create', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors: { title: ['Title is too long.'] } });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('x'.repeat(300));
      await component.save();
      harness.detectChanges();

      expect(component.fieldError('title')).toBe('Title is too long.');
      const titleInput = harness.routeNativeElement!.querySelector('#recipe-title') as HTMLInputElement;
      expect(titleInput.getAttribute('aria-invalid')).toBe('true');
      expect(titleInput.closest('cp-field')?.textContent).toContain('Title is too long.');
    });

    it('marks only the field the server named, leaving the rest without an error', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors: { sourceUrl: ['Not a valid URL.'] } });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      await component.save();
      harness.detectChanges();

      expect(component.fieldError('title')).toBe('');
      const titleInput = harness.routeNativeElement!.querySelector('#recipe-title') as HTMLInputElement;
      expect(titleInput.getAttribute('aria-invalid')).toBeNull();
    });

    it('clears stale field errors once a later save attempt no longer fails validation', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors: { title: ['Title is too long.'] } });
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('x'.repeat(300));
      await component.save();
      expect(component.fieldError('title')).toBe('Title is too long.');

      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      component.title.set('Weeknight Chili');
      await component.save();

      expect(component.fieldError('title')).toBe('');
    });
  });

  describe('idempotency key stability', () => {
    it('reuses the same key across a retry with an unchanged payload, but a fresh one once the payload changes', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({ status: 'unavailable' });
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      await component.save();
      const [, , firstKey] = recipeService.createRecipe.calls.mostRecent().args;

      await component.save(); // same payload — a true retry after a transient failure
      const [, , secondKey] = recipeService.createRecipe.calls.mostRecent().args;
      expect(secondKey).toBe(firstKey);

      component.title.set('Weeknight Chili With Beans'); // a different, new logical operation
      await component.save();
      const [, , thirdKey] = recipeService.createRecipe.calls.mostRecent().args;
      expect(thirdKey).not.toBe(firstKey);
    });

    it('generates a fresh key for the next save once a prior save already succeeded', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: RECIPE_DETAIL });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      await component.save();
      const [, , , firstKey] = recipeService.updateRecipe.calls.mostRecent().args;

      await component.save(); // identical payload, but the prior attempt already succeeded
      const [, , , secondKey] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(secondKey).not.toBe(firstKey);
    });
  });

  describe('instructions', () => {
    async function openInstructionsTab(path: string, recipeService: ReturnType<typeof recipeServiceSpy>) {
      const { harness, component } = await createHarness(path, recipeService);
      tabButton(harness.routeNativeElement!, 'Instructions')!.click();
      harness.detectChanges();
      return { harness, component };
    }

    it('adds a group and a step to it, each with a labelled, keyboard-operable remove action', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await openInstructionsTab('/cozy-fall/recipes/new', recipeService);

      component.addInstructionGroup();
      harness.detectChanges();
      expect(component.instructionGroups().length).toBe(1);

      const groupKey = component.instructionGroups()[0].key;
      component.addInstructionStep(groupKey);
      harness.detectChanges();
      expect(component.instructionGroups()[0].steps.length).toBe(1);

      const removeStep = harness.routeNativeElement!.querySelector('[aria-label="Remove step 1"]') as HTMLButtonElement;
      const moveGroupUp = harness.routeNativeElement!.querySelector('[aria-label="Move group 1 up"]');
      expect(removeStep).toBeTruthy();
      expect(moveGroupUp).toBeTruthy(); // present even though disabled — an assistive-tech user can still discover it

      removeStep.click();
      harness.detectChanges();
      expect(component.instructionGroups()[0].steps.length).toBe(0);
    });

    it('removes a group entirely, steps and all', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await openInstructionsTab('/cozy-fall/recipes/new', recipeService);

      component.addInstructionGroup();
      const groupKey = component.instructionGroups()[0].key;
      component.addInstructionStep(groupKey);

      component.removeInstructionGroup(groupKey);

      expect(component.instructionGroups().length).toBe(0);
    });

    it('reorders steps within a group with the move buttons, disabled at each end', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await openInstructionsTab('/cozy-fall/recipes/new', recipeService);

      component.addInstructionGroup();
      const groupKey = component.instructionGroups()[0].key;
      component.addInstructionStep(groupKey);
      component.addInstructionStep(groupKey);
      component.updateInstructionStep(groupKey, component.instructionGroups()[0].steps[0].key, { text: 'First' });
      component.updateInstructionStep(groupKey, component.instructionGroups()[0].steps[1].key, { text: 'Second' });
      harness.detectChanges();

      const firstStepKey = component.instructionGroups()[0].steps[0].key;
      component.moveInstructionStep(groupKey, firstStepKey, 1);

      expect(component.instructionGroups()[0].steps.map((step) => step.text)).toEqual(['Second', 'First']);

      // A move past either end is a no-op, not an out-of-range mutation.
      component.moveInstructionStep(groupKey, firstStepKey, 1);
      expect(component.instructionGroups()[0].steps.map((step) => step.text)).toEqual(['Second', 'First']);
    });

    it('reorders groups with the move buttons', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await openInstructionsTab('/cozy-fall/recipes/new', recipeService);

      component.addInstructionGroup();
      component.updateInstructionGroupTitle(component.instructionGroups()[0].key, 'Batter');
      component.addInstructionGroup();
      component.updateInstructionGroupTitle(component.instructionGroups()[1].key, 'Frosting');

      component.moveInstructionGroup(component.instructionGroups()[1].key, -1);

      expect(component.instructionGroups().map((group) => group.title)).toEqual(['Frosting', 'Batter']);
    });

    it('submits instructions on save, dropping steps left blank and trimming the rest', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { component } = await openInstructionsTab('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      component.addInstructionGroup();
      const groupKey = component.instructionGroups()[0].key;
      component.updateInstructionGroupTitle(groupKey, ' Batter ');
      component.addInstructionStep(groupKey); // left blank — must not reach the request
      component.addInstructionStep(groupKey);
      const [, secondStepKey] = component.instructionGroups()[0].steps.map((step) => step.key);
      component.updateInstructionStep(groupKey, secondStepKey, { text: '  Cream the butter.  ', note: 'Room temperature', durationMinutes: 5 });

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(request.instructions).toEqual([
        { id: null, title: 'Batter', steps: [{ id: null, text: 'Cream the butter.', durationMinutes: 5, note: 'Room temperature' }] },
      ]);
    });

    it('loads an existing recipe’s instructions into editable state, carrying their ids', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: {
          ...RECIPE_DETAIL,
          instructionGroups: [
            {
              id: 'g1',
              title: 'Batter',
              sortOrder: 0,
              steps: [
                { id: 's1', sortOrder: 0, text: 'Mix.', techniqueId: null, durationMinutes: 3, temperatureValue: null, temperatureUnitId: null, note: 'Gently' },
              ],
            },
          ],
        },
      });
      const { component } = await openInstructionsTab('/cozy-fall/recipes/r1', recipeService);

      expect(component.instructionGroups()).toEqual([
        { key: 'g1', id: 'g1', title: 'Batter', steps: [{ key: 's1', id: 's1', text: 'Mix.', note: 'Gently', durationMinutes: 3 }] },
      ]);
    });

    it('sends an edited existing step with its id, so the server updates it in place rather than adding a new one', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: {
          ...RECIPE_DETAIL,
          instructionGroups: [
            {
              id: 'g1',
              title: 'Batter',
              sortOrder: 0,
              steps: [{ id: 's1', sortOrder: 0, text: 'Mix.', techniqueId: null, durationMinutes: null, temperatureValue: null, temperatureUnitId: null, note: null }],
            },
          ],
        },
      });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: RECIPE_DETAIL });
      const { component } = await openInstructionsTab('/cozy-fall/recipes/r1', recipeService);

      component.updateInstructionStep('g1', 's1', { text: 'Mix thoroughly.' });
      await component.save();

      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.instructions.value).toEqual([
        { id: 'g1', title: 'Batter', steps: [{ id: 's1', text: 'Mix thoroughly.', durationMinutes: null, note: null }] },
      ]);
    });
  });

  describe('unsaved-change detection and leave confirmation', () => {
    it('is not dirty on a fresh create-mode form, becomes dirty on a field edit', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(component.isDirty()).toBeFalse();
      component.title.set('Weeknight Chili');
      expect(component.isDirty()).toBeTrue();
    });

    it('is not dirty right after loading an existing recipe, becomes dirty on a field edit', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(component.isDirty()).toBeFalse();
      component.title.set('New Chili');
      expect(component.isDirty()).toBeTrue();
    });

    it('becomes dirty from an instruction edit', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(component.isDirty()).toBeFalse();
      component.addInstructionGroup();
      expect(component.isDirty()).toBeTrue();
    });

    it('is dirty while typing an uncommitted tag, independent of the saved baseline', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(component.isDirty()).toBeFalse();
      component.newTagText.set('vegetarian');
      expect(component.isDirty()).toBeTrue();
      component.newTagText.set('');
      expect(component.isDirty()).toBeFalse();
    });

    it('clears dirty state after a successful create, right before navigating away', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      expect(component.isDirty()).toBeTrue();
      await component.save();

      expect(component.isDirty()).toBeFalse();
    });

    it('clears dirty state after a successful update', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...RECIPE_DETAIL, title: 'New Chili', concurrencyToken: 'AAAAAAAAB9I=' } });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('New Chili');
      expect(component.isDirty()).toBeTrue();
      await component.save();

      expect(component.isDirty()).toBeFalse();
    });

    it('stays dirty after a validation_failed, unavailable, or forbidden save failure', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      for (const status of ['validation_failed', 'unavailable', 'forbidden'] as const) {
        component.title.set(`Chili (${status})`);
        recipeService.updateRecipe.and.resolveTo(status === 'validation_failed' ? { status, fieldErrors: {} } : { status });
        await component.save();
        expect(component.isDirty()).withContext(status).toBeTrue();
      }
    });

    it('stays dirty immediately after a conflict save, and only clears once reloadAfterConflict is confirmed and completes', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'conflict' });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('New Chili');
      await component.save();
      expect(component.isDirty()).toBeTrue();

      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: { ...RECIPE_DETAIL, title: 'Server Chili', concurrencyToken: 'AAAAAAAAB9Z=' } });
      // reloadAfterConflict() discards the local edit, so — same as any other way of losing unsaved
      // work — it awaits confirmDiscardIfDirty() first rather than reloading synchronously.
      const reloadPromise = component.reloadAfterConflict();
      expect(component.leaveConfirmOpen()).toBeTrue();
      component.resolveLeaveConfirm(true);
      await reloadPromise;

      expect(component.isDirty()).toBeFalse();
    });

    it('leaves the form untouched if the user declines to discard when reloading after a conflict', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'conflict' });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('New Chili');
      await component.save();

      const reloadPromise = component.reloadAfterConflict();
      component.resolveLeaveConfirm(false);
      await reloadPromise;

      expect(recipeService.getRecipeDetail).toHaveBeenCalledTimes(1); // only the initial load — no reload fetch
      expect(component.title()).toBe('New Chili');
    });

    describe('confirmDiscardIfDirty', () => {
      it('resolves true immediately when the form is clean', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

        await expectAsync(component.confirmDiscardIfDirty()).toBeResolvedTo(true);
        expect(component.leaveConfirmOpen()).toBeFalse();
      });

      it('opens the dialog and resolves once answered when dirty', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        const pending = component.confirmDiscardIfDirty();
        expect(component.leaveConfirmOpen()).toBeTrue();

        component.resolveLeaveConfirm(true);
        await expectAsync(pending).toBeResolvedTo(true);
        expect(component.leaveConfirmOpen()).toBeFalse();
      });

      it('resolving "keep editing" (false) closes the dialog and leaves the form untouched', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        const pending = component.confirmDiscardIfDirty();
        component.resolveLeaveConfirm(false);

        await expectAsync(pending).toBeResolvedTo(false);
        expect(component.leaveConfirmOpen()).toBeFalse();
        expect(component.title()).toBe('Weeknight Chili');
      });

      it('resolves a still-pending confirm as "stay" if a second one is requested before it is answered', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        const first = component.confirmDiscardIfDirty();
        const second = component.confirmDiscardIfDirty();

        await expectAsync(first).toBeResolvedTo(false);
        component.resolveLeaveConfirm(true);
        await expectAsync(second).toBeResolvedTo(true);
      });
    });

    it('prevents an unload (tab close/refresh) while dirty, does nothing while clean', async () => {
      // Actually firing a real 'beforeunload' event on `window` makes a real browser (this suite runs
      // under real Chrome, not jsdom) treat the page as navigating away, which disconnects Karma's
      // socket ("Some of your tests did a full page reload!") — so this intercepts the handler the
      // component registers instead of dispatching a genuine event through the DOM.
      const addEventListenerSpy = spyOn(window, 'addEventListener').and.callThrough();
      const recipeService = recipeServiceSpy();
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      const registration = addEventListenerSpy.calls.allArgs().find(([type]) => type === 'beforeunload');
      const handler = registration?.[1] as (event: BeforeUnloadEvent) => void;
      expect(handler).toBeTruthy();

      const cleanEvent = { preventDefault: jasmine.createSpy('preventDefault'), returnValue: '' } as unknown as BeforeUnloadEvent;
      handler(cleanEvent);
      expect(cleanEvent.preventDefault).not.toHaveBeenCalled();

      component.title.set('Weeknight Chili');
      const dirtyEvent = { preventDefault: jasmine.createSpy('preventDefault'), returnValue: '' } as unknown as BeforeUnloadEvent;
      handler(dirtyEvent);
      expect(dirtyEvent.preventDefault).toHaveBeenCalled();
    });

    it('blocks a real router navigation while dirty until the user confirms discarding, then proceeds', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      component.title.set('Weeknight Chili');

      const navPromise = harness.navigateByUrl('/cozy-fall/elsewhere');
      // The router resolves canDeactivate through several internal async hops (and at least one
      // macrotask, not just microtasks) before our guard's pending promise is reachable from here —
      // poll with real delays rather than guessing a fixed number of Promise.resolve() ticks.
      await waitUntil(() => component.leaveConfirmOpen());
      harness.detectChanges();

      expect(component.leaveConfirmOpen()).toBeTrue();
      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/new');

      const discardButton = Array.from(harness.routeNativeElement!.querySelectorAll('button')).find(
        (btn) => btn.textContent?.trim() === 'Discard changes',
      ) as HTMLButtonElement;
      discardButton.click();

      await navPromise;
      expect(TestBed.inject(Router).url).toBe('/cozy-fall/elsewhere');
    });

    it('allows a real router navigation immediately, with no dialog, when the form is clean', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);

      await harness.navigateByUrl('/cozy-fall/elsewhere');

      expect(TestBed.inject(Router).url).toBe('/cozy-fall/elsewhere');
    });

    it('still navigates to the newly created recipe on save success — the guard does not deadlock its own post-create redirect', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      await component.save();

      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/new-id');
    });
  });
});
