import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { RecipeEditorComponent } from './recipe-editor.component';
import { recipeEditorCanDeactivateGuard } from './recipe-editor.guard';
import { EditableIngredientGroup, EditableIngredientRow } from './recipe-ingredient-editor.component';
import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import { CreatedRecipe, RecipeDetail } from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';

/** A minimal, otherwise-blank editable ingredient row — the working copy's own defaults, not the server's. */
function editableRow(overrides: Partial<EditableIngredientRow> = {}): EditableIngredientRow {
  return {
    key: 'row-' + Math.random().toString(36).slice(2),
    id: null,
    displayText: '',
    ingredientNameText: '',
    quantityText: '',
    detectedQuantityText: null,
    unitId: null,
    unitLabel: '',
    ingredientId: null,
    matchState: 'unreviewed',
    preparationNote: '',
    isOptional: false,
    scalingBehavior: 'Proportional',
    ingredientCandidates: [],
    unitCandidates: [],
    ...overrides,
  };
}

function editableGroup(overrides: Partial<EditableIngredientGroup> = {}): EditableIngredientGroup {
  return { key: 'group-' + Math.random().toString(36).slice(2), id: null, title: '', ingredients: [], ...overrides };
}

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
  duplicatedFrom: null,
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

/** The copy a duplicate produces: a recipe of its own, always a Draft, at version 1. */
const COPY: CreatedRecipe = {
  recipeId: 'r2',
  title: 'Skillet Cornbread — sourdough',
  status: 'Draft',
  versionId: 'v1',
  versionNumber: 1,
  createdAt: '2026-03-12T14:02:00Z',
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
    // The history panel is mounted by its tab, and a test that opens it must not crash on a spy that has no
    // method for what the panel reads. The panel's own behaviour is covered by its own spec.
    getVersionHistory: jasmine.createSpy('getVersionHistory').and.resolveTo({ status: 'unavailable' }),
    compareVersions: jasmine.createSpy('compareVersions').and.resolveTo({ status: 'unavailable' }),
    restoreVersion: jasmine.createSpy('restoreVersion').and.resolveTo({ status: 'unavailable' }),
    duplicateRecipe: jasmine.createSpy('duplicateRecipe').and.resolveTo({ status: 'unavailable' }),
    setArchived: jasmine.createSpy('setArchived').and.resolveTo({ status: 'unavailable' }),
  };
}

/**
 * The role behind the archive action, and behind the history panel's own row commands.
 *
 * A test states the role outright; `null` stands for "not loaded yet", where no permission-bearing control
 * may be offered.
 */
function membershipServiceStub(role: WorkspaceRole | null) {
  const state = signal<MyMembershipsState>(
    role === null
      ? { status: 'loading' }
      : {
          status: 'ready',
          memberships: [
            {
              workspaceId: 'w1',
              workspaceSlug: 'cozy-fall',
              workspaceName: 'Cozy Fall',
              membershipId: 'm1',
              role,
              status: 'Active',
            },
          ],
        },
  );

  return { state, ensureLoaded: () => Promise.resolve() };
}

/**
 * A stand-in for the SweetAlert2-backed {@link ConfirmService}. It stays pending until `answer()` is called,
 * so a test can observe that a confirmation was asked for before deciding it — the same thing the old
 * `leaveConfirmOpen()` signal made observable, without rendering a real modal into the document.
 */
function confirmServiceStub() {
  let pending: ((leave: boolean) => void) | null = null;

  const confirm = jasmine
    .createSpy('confirm')
    .and.callFake(() => new Promise<boolean>((resolve) => (pending = resolve)));

  return {
    confirm,
    /** Whether a confirmation is open and waiting for an answer. */
    get isOpen(): boolean {
      return pending !== null;
    },
    answer(leave: boolean): void {
      const resolve = pending;
      pending = null;
      resolve?.(leave);
    },
  };
}

/** The stub the current harness provided, so any test can answer a confirmation without threading it through. */
let confirmService: ReturnType<typeof confirmServiceStub>;

async function createHarness(
  path: string,
  recipeService: ReturnType<typeof recipeServiceSpy>,
  confirm: ReturnType<typeof confirmServiceStub> = confirmServiceStub(),
  role: WorkspaceRole | null = 'Editor',
) {
  confirmService = confirm;

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      { provide: ConfirmService, useValue: confirm },
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
      { provide: WorkspaceMembershipService, useValue: membershipServiceStub(role) },
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

      // Across every live region on the surface, not the first one: the restore notice keeps an empty
      // `role="status"` region in the DOM at all times so that its own text can be announced when it lands.
      const announced = Array.from(harness.routeNativeElement?.querySelectorAll('[role="status"]') ?? [])
        .map((region) => region.textContent)
        .join(' ');
      expect(announced).toContain('Saved');
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
      expect(confirmService.isOpen).toBeTrue();

      confirmService.answer(true);
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
      confirmService.answer(true);
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

  describe('ingredients', () => {
    it('submits ingredient groups on save, dropping lines left blank and trimming the rest', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      component.onIngredientGroupsChanged([
        editableGroup({
          title: ' Dry ingredients ',
          ingredients: [
            editableRow({ displayText: '   ' }), // left blank — must not reach the request
            editableRow({
              displayText: '  2 cups flour  ',
              quantityText: '2',
              unitId: 'unit1',
              ingredientId: 'ref1',
              preparationNote: '  sifted  ',
              isOptional: true,
              scalingBehavior: 'Fixed',
            }),
          ],
        }),
      ]);

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(request.ingredientGroups).toEqual([
        {
          id: null,
          title: 'Dry ingredients',
          ingredients: [
            {
              id: null,
              displayText: '2 cups flour',
              quantity: 2,
              quantityUpper: null,
              measurementUnitId: 'unit1',
              ingredientId: 'ref1',
              preparationNote: 'sifted',
              isOptional: true,
              scalingBehavior: 'Fixed',
            },
          ],
        },
      ]);
    });

    it('loads an existing recipe’s ingredients into editable state, carrying their ids, without waiting on the child editor to resync', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: {
          ...RECIPE_DETAIL,
          ingredientGroups: [
            {
              id: 'g1',
              title: 'Dry ingredients',
              sortOrder: 0,
              ingredients: [
                {
                  id: 'ing1',
                  sortOrder: 0,
                  displayText: '2 cups flour',
                  ingredientNameText: 'flour',
                  quantity: 2,
                  quantityUpper: null,
                  measurementUnitId: 'unit1',
                  ingredientId: 'ref1',
                  matchStatus: 'Matched',
                  preparationNote: null,
                  isOptional: false,
                  scalingBehavior: 'Proportional',
                },
              ],
            },
          ],
        },
      });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      // Asserted with no harness.detectChanges() in between: applyDetail sets editedIngredientGroups
      // synchronously rather than waiting on the child editor's own effect to resync from [initialGroups].
      expect(component.editedIngredientGroups()).toEqual([
        {
          key: 'g1',
          id: 'g1',
          title: 'Dry ingredients',
          ingredients: [
            {
              key: 'ing1',
              id: 'ing1',
              displayText: '2 cups flour',
              ingredientNameText: 'flour',
              quantityText: '2',
              detectedQuantityText: null,
              unitId: 'unit1',
              unitLabel: '',
              ingredientId: 'ref1',
              matchState: 'matched',
              preparationNote: '',
              isOptional: false,
              scalingBehavior: 'Proportional',
              ingredientCandidates: [],
              unitCandidates: [],
            },
          ],
        },
      ]);
    });

    it('sends an edited existing ingredient line with its id, so the server updates it in place rather than adding a new one', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: {
          ...RECIPE_DETAIL,
          ingredientGroups: [
            {
              id: 'g1',
              title: null,
              sortOrder: 0,
              ingredients: [
                {
                  id: 'ing1',
                  sortOrder: 0,
                  displayText: '2 cups flour',
                  ingredientNameText: null,
                  quantity: 2,
                  quantityUpper: null,
                  measurementUnitId: null,
                  ingredientId: null,
                  matchStatus: 'NotAttempted',
                  preparationNote: null,
                  isOptional: false,
                  scalingBehavior: 'Proportional',
                },
              ],
            },
          ],
        },
      });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: RECIPE_DETAIL });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      const [group] = component.editedIngredientGroups();
      component.onIngredientGroupsChanged([
        { ...group, ingredients: group.ingredients.map((row) => (row.id === 'ing1' ? { ...row, displayText: '3 cups flour', quantityText: '3' } : row)) },
      ]);

      await component.save();

      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.ingredientGroups.value).toEqual([
        {
          id: 'g1',
          title: null,
          ingredients: [
            { id: 'ing1', displayText: '3 cups flour', quantity: 3, quantityUpper: null, measurementUnitId: null, ingredientId: null, preparationNote: null, isOptional: false, scalingBehavior: 'Proportional' },
          ],
        },
      ]);
    });

    it('ingredient edits survive a save and a reload', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.onIngredientGroupsChanged([
        editableGroup({
          title: 'Dry ingredients',
          ingredients: [editableRow({ displayText: '2 cups flour', quantityText: '2', unitId: 'unit1' })],
        }),
      ]);
      expect(component.isDirty()).toBeTrue();

      const saved: RecipeDetail = {
        ...RECIPE_DETAIL,
        concurrencyToken: 'AAAAAAAAB9I=',
        ingredientGroups: [
          {
            id: 'g1',
            title: 'Dry ingredients',
            sortOrder: 0,
            ingredients: [
              {
                id: 'ing1',
                sortOrder: 0,
                displayText: '2 cups flour',
                ingredientNameText: null,
                quantity: 2,
                quantityUpper: null,
                measurementUnitId: 'unit1',
                ingredientId: null,
                matchStatus: 'NotAttempted',
                preparationNote: null,
                isOptional: false,
                scalingBehavior: 'Proportional',
              },
            ],
          },
        ],
      };
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: saved });

      await component.save();

      // Survives the save: dirty clears, and the working copy now carries the server-assigned ids.
      expect(component.isDirty()).toBeFalse();
      expect(component.editedIngredientGroups()[0].id).toBe('g1');
      expect(component.editedIngredientGroups()[0].ingredients[0].id).toBe('ing1');

      // Survives a full reload too (e.g. coming back to this recipe later) — not just the in-memory
      // post-save state. The form is clean, so this reloads without asking.
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: saved });
      await component.reloadAfterConflict();

      expect(component.editedIngredientGroups()).toEqual([
        {
          key: 'g1',
          id: 'g1',
          title: 'Dry ingredients',
          ingredients: [
            {
              key: 'ing1',
              id: 'ing1',
              displayText: '2 cups flour',
              ingredientNameText: '',
              quantityText: '2',
              detectedQuantityText: null,
              unitId: 'unit1',
              unitLabel: '',
              ingredientId: null,
              matchState: 'unreviewed',
              preparationNote: '',
              isOptional: false,
              scalingBehavior: 'Proportional',
              ingredientCandidates: [],
              unitCandidates: [],
            },
          ],
        },
      ]);
      expect(component.isDirty()).toBeFalse();
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

    it('becomes dirty from an ingredient edit', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(component.isDirty()).toBeFalse();
      component.onIngredientGroupsChanged([editableGroup({ ingredients: [editableRow({ displayText: 'Salt' })] })]);
      expect(component.isDirty()).toBeTrue();
    });

    it('lights the "Unsaved changes" pill for an ingredient-only edit', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      harness.detectChanges();
      expect(harness.routeNativeElement?.querySelector('.dirty-indicator')).toBeFalsy();

      component.onIngredientGroupsChanged([editableGroup({ ingredients: [editableRow({ displayText: 'Salt' })] })]);
      harness.detectChanges();

      const pill = harness.routeNativeElement?.querySelector('.dirty-indicator');
      expect(pill?.textContent).toContain('Unsaved changes');
    });

    it('blocks a real router navigation when only an ingredient edit is unsaved, until the user confirms discarding', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      component.onIngredientGroupsChanged([editableGroup({ ingredients: [editableRow({ displayText: 'Salt' })] })]);

      const navPromise = harness.navigateByUrl('/cozy-fall/elsewhere');
      await waitUntil(() => confirmService.isOpen);
      harness.detectChanges();

      expect(confirmService.isOpen).toBeTrue();
      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/new');

      confirmService.answer(true);
      await navPromise;
      expect(TestBed.inject(Router).url).toBe('/cozy-fall/elsewhere');
    });

    it('clears dirty state after a successful update with only an ingredient edit', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...RECIPE_DETAIL, concurrencyToken: 'AAAAAAAAB9I=' } });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.onIngredientGroupsChanged([editableGroup({ ingredients: [editableRow({ displayText: 'Salt' })] })]);
      expect(component.isDirty()).toBeTrue();
      await component.save();

      expect(component.isDirty()).toBeFalse();
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
      expect(confirmService.isOpen).toBeTrue();
      confirmService.answer(true);
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
      confirmService.answer(false);
      await reloadPromise;

      expect(recipeService.getRecipeDetail).toHaveBeenCalledTimes(1); // only the initial load — no reload fetch
      expect(component.title()).toBe('New Chili');
    });

    describe('confirmDiscardIfDirty', () => {
      it('resolves true immediately when the form is clean', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);

        await expectAsync(component.confirmDiscardIfDirty()).toBeResolvedTo(true);

        // Nothing to lose, so nothing to ask about: a clean form must not raise a modal at all.
        expect(confirmService.confirm).not.toHaveBeenCalled();
      });

      it('asks for a confirmation and resolves once it is answered when dirty', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        const pending = component.confirmDiscardIfDirty();
        expect(confirmService.isOpen).toBeTrue();

        confirmService.answer(true);
        await expectAsync(pending).toBeResolvedTo(true);
        expect(confirmService.isOpen).toBeFalse();
      });

      it('names the destructive action rather than asking the creator to agree to "OK"', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        void component.confirmDiscardIfDirty();

        // The wording is the whole safety mechanism of a discard prompt, so it is pinned rather than left to
        // whatever a future edit makes it.
        expect(confirmService.confirm).toHaveBeenCalledWith(
          jasmine.objectContaining({ confirmLabel: 'Discard changes', cancelLabel: 'Keep editing', tone: 'danger' }),
        );

        confirmService.answer(false);
      });

      it('answering "keep editing" leaves the form untouched', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        const pending = component.confirmDiscardIfDirty();
        confirmService.answer(false);

        await expectAsync(pending).toBeResolvedTo(false);
        expect(component.title()).toBe('Weeknight Chili');
      });

      it('answers two concurrent attempts with one dialog rather than stacking a second', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        const first = component.confirmDiscardIfDirty();
        const second = component.confirmDiscardIfDirty();

        // One modal, one question, one answer for both callers. SweetAlert2 permits only one popup at a time,
        // and a second `fire()` would silently dismiss the first — so sharing the pending promise is what keeps
        // the two navigation attempts from disagreeing about what the creator said.
        expect(confirmService.confirm).toHaveBeenCalledTimes(1);

        confirmService.answer(true);
        await expectAsync(first).toBeResolvedTo(true);
        await expectAsync(second).toBeResolvedTo(true);
      });

      it('asks again on the next attempt once a confirmation has been answered', async () => {
        const recipeService = recipeServiceSpy();
        const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
        component.title.set('Weeknight Chili');

        const first = component.confirmDiscardIfDirty();
        confirmService.answer(false);
        await expectAsync(first).toBeResolvedTo(false);

        // The shared promise must be released when it settles, or "keep editing" would answer every later
        // attempt too and the creator could never leave.
        void component.confirmDiscardIfDirty();
        expect(confirmService.confirm).toHaveBeenCalledTimes(2);
        confirmService.answer(false);
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
      await waitUntil(() => confirmService.isOpen);
      harness.detectChanges();

      expect(confirmService.isOpen).toBeTrue();
      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/new');

      confirmService.answer(true);

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

  describe('archiving and bringing back', () => {
    const ARCHIVED: RecipeDetail = { ...RECIPE_DETAIL, status: 'Archived', concurrencyToken: 'AAAAAAAAB9Z=' };

    function archiveButton(root: HTMLElement): HTMLButtonElement | undefined {
      return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.includes('Archive'));
    }

    function bringBackButton(root: HTMLElement): HTMLButtonElement | undefined {
      return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.includes('Bring it back'));
    }

    function saveButton(root: HTMLElement): HTMLButtonElement | undefined {
      return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Save');
    }

    /** Loads a recipe, answers the confirmation with `agree`, and returns once the command has settled. */
    async function loaded(recipe: RecipeDetail, role: WorkspaceRole | null = 'Editor') {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe });
      const confirm = confirmServiceStub();
      const harness = await createHarness('/cozy-fall/recipes/r1', recipeService, confirm, role);
      await waitUntil(() => harness.component.loadState().status === 'ready');
      harness.harness.detectChanges();

      return { ...harness, recipeService, confirm };
    }

    it('offers archiving to an Editor, and not to a Contributor', async () => {
      const editor = await loaded(RECIPE_DETAIL, 'Editor');
      expect(archiveButton(editor.harness.routeNativeElement!)).toBeTruthy();

      const contributor = await loaded(RECIPE_DETAIL, 'Contributor');
      expect(archiveButton(contributor.harness.routeNativeElement!)).toBeUndefined();

      const unknownRole = await loaded(RECIPE_DETAIL, null);
      expect(archiveButton(unknownRole.harness.routeNativeElement!)).toBeUndefined();
    });

    it('asks first, and the question never says the recipe is deleted', async () => {
      const { component, confirm } = await loaded(RECIPE_DETAIL);

      const command = component.setArchived(true);
      await waitUntil(() => confirm.isOpen);

      const question = confirm.confirm.calls.mostRecent().args[0] as {
        title: string;
        message: string;
        confirmLabel: string;
        cancelLabel: string;
        tone: string;
      };
      expect(question.title).toBe('Archive this recipe?');
      expect(question.message).toContain('nothing is deleted');
      expect(question.message).toContain('bring it back');
      expect(question.message).toContain('filter for Archived');
      expect(question.confirmLabel).toBe('Archive recipe');
      expect(question.cancelLabel).toBe('Keep it active');

      // Archiving is reversible and destroys nothing, so it is not dressed as a destructive action. The word
      // "deleted" appears only in the sentence denying it.
      expect(question.tone).toBe('neutral');
      expect(`${question.title} ${question.message}`).not.toMatch(
        /permanent|for good|cannot be undone|gone forever|delete (this|the) recipe|remove (this|the) recipe/i,
      );

      confirm.answer(false);
      await command;
    });

    it('warns about unsaved edits only when there are some', async () => {
      const clean = await loaded(RECIPE_DETAIL);
      const first = clean.component.setArchived(true);
      await waitUntil(() => clean.confirm.isOpen);
      expect((clean.confirm.confirm.calls.mostRecent().args[0] as { message: string }).message).not.toContain('unsaved edits');
      clean.confirm.answer(false);
      await first;

      const dirty = await loaded(RECIPE_DETAIL);
      dirty.component.title.set('Half-written edit');
      const second = dirty.component.setArchived(true);
      await waitUntil(() => dirty.confirm.isOpen);
      expect((dirty.confirm.confirm.calls.mostRecent().args[0] as { message: string }).message).toContain(
        "You won't be able to save them until you bring it back.",
      );
      dirty.confirm.answer(false);
      await second;
    });

    it('archives nothing when the question is declined', async () => {
      const { component, confirm, recipeService } = await loaded(RECIPE_DETAIL);

      const command = component.setArchived(true);
      await waitUntil(() => confirm.isOpen);
      confirm.answer(false);
      await command;

      expect(recipeService.setArchived).not.toHaveBeenCalled();
      expect(component.isArchived()).toBeFalse();
    });

    it('quotes the token, applies the recipe that comes back, and turns editing off', async () => {
      const { harness, component, confirm, recipeService } = await loaded(RECIPE_DETAIL);
      recipeService.setArchived.and.resolveTo({ status: 'updated', recipe: ARCHIVED });

      const command = component.setArchived(true);
      await waitUntil(() => confirm.isOpen);
      confirm.answer(true);
      await command;
      harness.detectChanges();

      expect(recipeService.setArchived).toHaveBeenCalledWith('cozy-fall', 'r1', true, {
        expectedConcurrencyToken: 'AAAAAAAAB9E=',
      });

      expect(component.isArchived()).toBeTrue();
      expect(component.canSave()).toBeFalse();
      expect(saveButton(harness.routeNativeElement!)?.disabled).toBeTrue();

      // NgModel applies a disabled binding on its own microtask, so the select settles a tick after the rest.
      await waitUntil(() => harness.routeNativeElement?.querySelector<HTMLSelectElement>('#recipe-status')?.disabled === true);
      expect(harness.routeNativeElement?.querySelector<HTMLSelectElement>('#recipe-status')?.disabled).toBeTrue();
      expect(component.notice()?.text).toContain('Nothing was deleted');
    });

    it('says what is still true in the banner, and offers the way back', async () => {
      const { harness } = await loaded(ARCHIVED);

      const banner = harness.routeNativeElement?.querySelector('#recipe-archived-banner');
      expect(banner?.textContent).toContain('shelved, not deleted');
      expect(banner?.textContent).toContain('Every version, tag and photo is still here');
      expect(banner?.textContent).toContain('filter for Archived');
      expect(bringBackButton(harness.routeNativeElement!)).toBeTruthy();

      // The archive action is gone while it is archived — there is nothing left to archive.
      expect(archiveButton(harness.routeNativeElement!)).toBeUndefined();
    });

    it('shows the banner to a member who cannot bring it back, without offering them the button', async () => {
      const { harness } = await loaded(ARCHIVED, 'Contributor');

      expect(harness.routeNativeElement?.querySelector('#recipe-archived-banner')).toBeTruthy();
      expect(bringBackButton(harness.routeNativeElement!)).toBeUndefined();
    });

    it('never holds Archived in the status a save would send', async () => {
      const { harness, component } = await loaded(ARCHIVED);

      expect(component.status()).toBe('Draft');
      expect(harness.routeNativeElement?.querySelector<HTMLSelectElement>('#recipe-status')?.value).toBe('Draft');
    });

    it('says the recipe comes back as a Draft before bringing it back', async () => {
      const { component, confirm, recipeService } = await loaded(ARCHIVED);
      recipeService.setArchived.and.resolveTo({ status: 'updated', recipe: RECIPE_DETAIL });

      const command = component.setArchived(false);
      await waitUntil(() => confirm.isOpen);

      const question = confirm.confirm.calls.mostRecent().args[0] as { title: string; message: string };
      expect(question.title).toBe('Bring this recipe back?');
      expect(question.message).toContain('as a Draft');

      confirm.answer(true);
      await command;

      expect(recipeService.setArchived).toHaveBeenCalledWith('cozy-fall', 'r1', false, {
        expectedConcurrencyToken: 'AAAAAAAAB9Z=',
      });
      expect(component.isArchived()).toBeFalse();
      expect(component.canSave()).toBeTrue();
      expect(component.notice()?.text).toContain('as a Draft');
    });

    it('reports a stale token as a conflict that moved nothing, and offers a reload', async () => {
      const { harness, component, confirm, recipeService } = await loaded(RECIPE_DETAIL);
      recipeService.setArchived.and.resolveTo({ status: 'conflict' });

      const command = component.setArchived(true);
      await waitUntil(() => confirm.isOpen);
      confirm.answer(true);
      await command;
      harness.detectChanges();

      expect(component.isArchived()).toBeFalse();
      const alert = harness.routeNativeElement?.querySelector('[role="alert"]');
      expect(alert?.textContent).toContain('nothing was moved');
      expect(alert?.textContent).toContain('Reload latest');
    });

    it('names the role on a refusal, because the server has the last word', async () => {
      const { harness, component, confirm, recipeService } = await loaded(RECIPE_DETAIL);
      recipeService.setArchived.and.resolveTo({ status: 'forbidden' });

      const command = component.setArchived(true);
      await waitUntil(() => confirm.isOpen);
      confirm.answer(true);
      await command;
      harness.detectChanges();

      expect(harness.routeNativeElement?.querySelector('[role="alert"]')?.textContent).toContain('Editor role');
    });

    it('offers another attempt after a transient failure', async () => {
      const { harness, component, confirm, recipeService } = await loaded(RECIPE_DETAIL);
      recipeService.setArchived.and.resolveTo({ status: 'unavailable' });

      const command = component.setArchived(true);
      await waitUntil(() => confirm.isOpen);
      confirm.answer(true);
      await command;
      harness.detectChanges();

      expect(harness.routeNativeElement?.querySelector('[role="alert"]')?.textContent).toContain('Check your connection');
      expect(archiveButton(harness.routeNativeElement!)?.disabled).toBeFalse();
    });
  });

  describe('a version restored from the history panel', () => {
    const restoredRecipe: RecipeDetail = { ...RECIPE_DETAIL, title: 'Chilli', concurrencyToken: 'AAAAAAAAB9Z=' };

    it('re-seeds the form from the response and lands the creator on the content, not on the history', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      await waitUntil(() => component.loadState().status === 'ready');

      component.selectedTabId.set('history');
      component.title.set('Half-written edit');
      expect(component.isDirty()).toBeTrue();

      component.onVersionRestored({ recipe: restoredRecipe, fromVersionNumber: 3, newVersionNumber: 9 });
      harness.detectChanges();

      expect(component.title()).toBe('Chilli');
      expect(component.selectedTabId()).toBe('metadata');
      // The restore replaced the form wholesale, so there is nothing unsaved left to warn about.
      expect(component.isDirty()).toBeFalse();
      expect(harness.routeNativeElement?.textContent).toContain('Version 3 restored as version 9.');

      // No second read: the restore answered with the whole recipe.
      expect(recipeService.getRecipeDetail).toHaveBeenCalledTimes(1);
    });

    it('quotes the refreshed token on the next save, not the one the restore consumed', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: restoredRecipe });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      await waitUntil(() => component.loadState().status === 'ready');

      component.onVersionRestored({ recipe: restoredRecipe, fromVersionNumber: 3, newVersionNumber: 9 });
      component.title.set('Chilli con carne');
      await component.save();

      const request = recipeService.updateRecipe.calls.mostRecent().args[2] as { expectedConcurrencyToken: string };
      expect(request.expectedConcurrencyToken).toBe('AAAAAAAAB9Z=');
    });

    /** A restore that would change nothing writes no version, and saying otherwise would name one that isn't there. */
    it('says no new version was written when the restore changed nothing', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      await waitUntil(() => component.loadState().status === 'ready');

      component.onVersionRestored({ recipe: RECIPE_DETAIL, fromVersionNumber: 8, newVersionNumber: null });
      harness.detectChanges();

      const banner = harness.routeNativeElement?.textContent ?? '';
      expect(banner).toContain('no new version was written');
      expect(banner).not.toContain('restored as version');
    });

    it('takes the creator to the copy a duplicate created', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      await waitUntil(() => component.loadState().status === 'ready');

      await component.onRecipeDuplicated({ recipe: COPY, fromVersionNumber: 3 });

      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/r2');
    });

    /** The copy was created before the navigation was attempted, so staying must not hide it or repeat it. */
    it('names the copy and links to it when unsaved edits keep the creator here', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const confirm = confirmServiceStub();
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService, confirm);
      await waitUntil(() => component.loadState().status === 'ready');

      component.title.set('Half-written edit');
      const followed = component.onRecipeDuplicated({ recipe: COPY, fromVersionNumber: 3 });
      await waitUntil(() => confirm.isOpen);
      confirm.answer(false);
      await followed;
      harness.detectChanges();

      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/r1');
      expect(component.notice()?.recipeLink?.recipeId).toBe('r2');
      expect(component.isDirty()).toBeTrue();

      const banner = harness.routeNativeElement?.textContent ?? '';
      expect(banner).toContain('Skillet Cornbread — sourdough');
      expect(banner).toContain('a new Draft');

      // A link, never a button that would make a second copy.
      const link = harness.routeNativeElement?.querySelector('a[href="/cozy-fall/recipes/r2"]');
      expect(link?.textContent).toContain('Open Skillet Cornbread — sourdough');
    });

    it('says the recipe is still out of date when the creator declines the reload a conflict asked for', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const confirm = confirmServiceStub();
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService, confirm);
      await waitUntil(() => component.loadState().status === 'ready');

      component.title.set('Half-written edit');
      const reload = component.onHistoryReloadRequested();
      await waitUntil(() => confirm.isOpen);
      confirm.answer(false);
      await reload;
      harness.detectChanges();

      // Keeping the edits means keeping the stale token, so every later restore would be refused the same
      // way — with the same remedy offered. Saying so is what stops that loop.
      expect(component.notice()?.isProblem).toBeTrue();
      expect(harness.routeNativeElement?.textContent).toContain('out of date');
      expect(recipeService.getRecipeDetail).toHaveBeenCalledTimes(1);
    });

    it('drops the restore notice once the creator saves again', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: restoredRecipe });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      await waitUntil(() => component.loadState().status === 'ready');

      component.onVersionRestored({ recipe: restoredRecipe, fromVersionNumber: 3, newVersionNumber: 9 });
      await component.save();
      harness.detectChanges();

      expect(component.notice()).toBeNull();
      expect(harness.routeNativeElement?.textContent).not.toContain('restored as version 9');
    });
  });
});
