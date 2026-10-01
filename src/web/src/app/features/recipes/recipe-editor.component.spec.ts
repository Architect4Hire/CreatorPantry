import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { RecipeEditorComponent } from './recipe-editor.component';
import { recipeEditorCanDeactivateGuard } from './recipe-editor.guard';
import { EditableIngredientGroup, EditableIngredientRow } from './recipe-ingredient-editor.component';
import { ConfirmService } from '../../core/confirm.service';
import { RuntimeConfigService } from '../../core/runtime-config.service';
import { WorkspaceRole } from '../../models/auth.models';
import {
  CreatedRecipe,
  IngredientGroupInput,
  InstructionGroupInput,
  RecipeDetail,
  RecipeIngredient,
} from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';

/**
 * A minimal, otherwise-blank editable ingredient row — the working copy's own defaults, not the server's.
 *
 * `displayTextIsComposed: false` rather than `blankRow()`'s `true`, because a test that hands a row a
 * `displayText` is describing a line whose text is already owned (pasted, or loaded from a recipe). Building
 * a row here at all skips the child editor, and with it the composition that gives a hand-entered line its
 * text — so a test that means to cover hand entry drives the real inputs instead. See "a line typed into the
 * row's own fields" below, and the child editor's own spec.
 */
function editableRow(overrides: Partial<EditableIngredientRow> = {}): EditableIngredientRow {
  return {
    key: 'row-' + Math.random().toString(36).slice(2),
    id: null,
    displayText: '',
    displayTextIsComposed: false,
    ingredientNameText: '',
    quantityText: '',
    quantityUpper: null,
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
  servingCount: null,
  servingSize: null,
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
  gatewayUrl: string | null = null,
) {
  confirmService = confirm;

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      // The editor reads the shared unit catalogue for its yield-unit picker, so `ReferenceService` has an
      // `HttpClient` to inject. With no gateway resolved (the default below) that read cannot be addressed at
      // all, so the picker reports itself unavailable and makes no request — which is what every test that is
      // not about it wants.
      provideHttpClient(),
      provideHttpClientTesting(),
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

  // A test that cares about the yield-unit picker resolves the gateway here and answers the catalogue read
  // itself; see `settleUnits`.
  if (gatewayUrl !== null) {
    const http = TestBed.inject(HttpTestingController);
    const loading = TestBed.inject(RuntimeConfigService).load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl });
    await loading;
  }

  const harness = await RouterTestingHarness.create();
  const component = await harness.navigateByUrl(path, RecipeEditorComponent);
  harness.detectChanges();
  return { harness, component };
}

function tabButton(root: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(root.querySelectorAll<HTMLButtonElement>('button[role="tab"]')).find((btn) => btn.textContent?.trim() === label);
}

/**
 * Selects one of the Edit area's three form tabs.
 *
 * Needed by every case that reaches for a control inside Ingredients or Instructions: `CpTabPanelComponent`
 * renders its content only once its tab has been selected, so those controls are absent from the DOM until
 * this runs. That is the whole substance of the split, and the reason `revealFirstFieldError()` selects a tab
 * before it goes looking for a field.
 */
function selectEditTab(harness: RouterTestingHarness, label: 'General' | 'Ingredients' | 'Instructions'): void {
  // By data-tab-id, not by label text: a tab's label carries "(1 problem)" after a refusal that named a field
  // on it, so matching the text would break exactly in the tests that are about a refusal.
  const id = label.toLowerCase();
  const button = harness.routeNativeElement!.querySelector<HTMLButtonElement>(
    `button[role="tab"][data-tab-id="${id}"]`,
  );
  expect(button).withContext(`the ${label} form tab`).toBeTruthy();
  button!.click();
  harness.detectChanges();
}

/** The tablist of the Edit area's form tabs — the inner one, not the areas tablist above it. */
function formTablist(root: HTMLElement): HTMLElement {
  const tablists = Array.from(root.querySelectorAll<HTMLElement>('[role="tablist"]'));
  expect(tablists.length).withContext('an areas tablist and a form tablist').toBeGreaterThan(1);
  return tablists[1];
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

    it('offers seven areas at the top level, with the form itself split inside Edit', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);
      const root = harness.routeNativeElement!;

      // Scoped to the outer tablist: the Edit area now contains a tablist of its own, and an unscoped query
      // would conflate the two levels.
      const areas = Array.from(root.querySelector('[role="tablist"]')!.querySelectorAll('button[role="tab"]'));
      expect(areas.map((tab) => tab.textContent?.trim())).toEqual(['Edit', 'Tools', 'Media', 'History', 'Test kitchen', 'Readiness', 'Publish']);

      const form = Array.from(formTablist(root).querySelectorAll('button[role="tab"]'));
      expect(form.map((tab) => tab.textContent?.trim())).toEqual(['General', 'Ingredients', 'Instructions']);
      // None of the three is ever disabled: they hold the form being filled in, not something needing a
      // saved recipe first.
      expect(form.some((tab) => (tab as HTMLButtonElement).disabled)).toBeFalse();
    });

    it('disables the Tools, Media, History, Test kitchen, Readiness and Publish areas (no recipe exists yet)', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(tabButton(harness.routeNativeElement!, 'Tools')?.disabled).toBeTrue();
      expect(tabButton(harness.routeNativeElement!, 'Media')?.disabled).toBeTrue();
      expect(tabButton(harness.routeNativeElement!, 'History')?.disabled).toBeTrue();
      expect(tabButton(harness.routeNativeElement!, 'Test kitchen')?.disabled).toBeTrue();
      expect(tabButton(harness.routeNativeElement!, 'Readiness')?.disabled).toBeTrue();
      expect(tabButton(harness.routeNativeElement!, 'Publish')?.disabled).toBeTrue();
      // Edit is the one area a recipe being created can use, and it is where the form is.
      expect(tabButton(harness.routeNativeElement!, 'Edit')?.disabled).toBeFalse();
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

    it('enables the Tools, Media, History, Test kitchen, Readiness and Publish areas once the recipe exists', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(tabButton(harness.routeNativeElement!, 'Tools')?.disabled).toBeFalse();
      expect(tabButton(harness.routeNativeElement!, 'Media')?.disabled).toBeFalse();
      expect(tabButton(harness.routeNativeElement!, 'History')?.disabled).toBeFalse();
      expect(tabButton(harness.routeNativeElement!, 'Test kitchen')?.disabled).toBeFalse();
      expect(tabButton(harness.routeNativeElement!, 'Readiness')?.disabled).toBeFalse();
      expect(tabButton(harness.routeNativeElement!, 'Publish')?.disabled).toBeFalse();
    });

    it('shows the readiness check on the Readiness tab, with the status the server reported', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: { ...RECIPE_DETAIL, status: 'ReadyForReview' } });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.selectedAreaId.set('readiness');
      harness.detectChanges();

      expect(component.recipeStatus()).toBe('ReadyForReview');
      expect(harness.routeNativeElement!.querySelector('cp-recipe-readiness')).not.toBeNull();
    });

    it('asks for a save first instead of mounting the readiness check while the form has unsaved edits', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('Edited but not saved');
      component.selectedAreaId.set('readiness');
      harness.detectChanges();

      const root = harness.routeNativeElement!;
      expect(root.querySelector('cp-recipe-readiness')).toBeNull();
      expect(root.textContent).toContain('You have changes that are not saved yet');
    });

    it('shows the test kitchen on its own tab, on the saved version', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.selectedAreaId.set('tests');
      harness.detectChanges();

      expect(harness.routeNativeElement!.querySelector('cp-recipe-test-kitchen')).not.toBeNull();
    });

    /**
     * The opposite call to Readiness, and deliberately so: reading what earlier cooks found does not depend
     * on what is unsaved. Recording is the part that does, and the kitchen refuses that itself rather than
     * the whole tab being withheld.
     */
    it('still shows the test kitchen while the form has unsaved edits', async () => {
      const recipeService = recipeServiceSpy();
      // A saved version, so that "no version to test" is not the reason recording is unavailable — this test
      // is about the unsaved edits being the reason.
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: {
          ...RECIPE_DETAIL,
          currentVersion: {
            id: 'v1',
            versionNumber: 1,
            source: 'CreatorEdit',
            readiness: 'Draft',
            reason: null,
            createdAt: '2026-03-01T00:00:00Z',
          },
        },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('Edited but not saved');
      component.selectedAreaId.set('tests');
      harness.detectChanges();

      const root = harness.routeNativeElement!;
      expect(root.querySelector('cp-recipe-test-kitchen')).not.toBeNull();
      expect(root.textContent).toContain('a test is recorded against the saved version');
    });

    it('takes the recipe as the server returns it after a confirmed move, and only then shows the new status', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: { ...RECIPE_DETAIL, status: 'ReadyForReview' } });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(component.recipeStatus()).toBe('ReadyForReview');

      component.onRecipeTransitioned({ ...RECIPE_DETAIL, status: 'Approved', concurrencyToken: 'token-after-approval' });

      expect(component.recipeStatus()).toBe('Approved');
      expect(component.concurrencyToken()).toBe('token-after-approval');
      expect(component.isDirty()).toBeFalse();
    });

    it('still hides an unselected area entirely, not merely marks its tab unselected', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      // #panel-edit by id, not the nearest panel to the title: the title now sits inside the General form
      // panel, which is nested in the area panel this case is about.
      const editPanel = harness.routeNativeElement!.querySelector('#panel-edit') as HTMLElement;
      expect(editPanel.hasAttribute('hidden')).toBeFalse();
      expect(getComputedStyle(editPanel).display).not.toBe('none');

      tabButton(harness.routeNativeElement!, 'History')!.click();
      harness.detectChanges();

      // A CSS rule that gives cp-tab-panel an unconditional `display` would defeat the component's
      // `[hidden]` attribute and show every area stacked at once — assert the actual rendered display,
      // not just the attribute, so that regression can't slip back in silently.
      expect(editPanel.hasAttribute('hidden')).toBeTrue();
      expect(getComputedStyle(editPanel).display).toBe('none');
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

  describe('why some areas are not available yet', () => {
    it('says in words why the greyed-out areas are greyed out, in create mode', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);

      const note = harness.routeNativeElement!.querySelector('.area-lock-note') as HTMLElement;
      expect(note).withContext('a disabled control with no stated reason is the defect').toBeTruthy();
      expect(note.textContent).toContain('open once you save this recipe');
    });

    it('names every area that is actually disabled, and no area that is not', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      const note = harness.routeNativeElement!.querySelector('.area-lock-note') as HTMLElement;

      // Derived from the tab definitions, not written out: an area disabled before the first save is named
      // here whether or not anyone remembered to update a sentence.
      const disabled = component.areas.filter((area) => area.disabled).map((area) => area.label);
      expect(disabled.length).toBeGreaterThan(0);
      for (const label of disabled) {
        expect(note.textContent).withContext(label).toContain(label);
      }

      for (const label of component.areas.filter((area) => !area.disabled).map((area) => area.label)) {
        expect(note.textContent).withContext(label).not.toContain(label);
      }
    });

    it('puts the reason with the tabs it explains, not elsewhere on the page', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);
      const root = harness.routeNativeElement!;

      const tablist = root.querySelector('[role="tablist"]')!;
      const note = root.querySelector('.area-lock-note')!;

      // Same container as the tablist, and after it — so a screen reader reaches the tabs, then the reason.
      expect(note.parentElement).toBe(tablist.parentElement);
      expect(tablist.compareDocumentPosition(note) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    });

    it('exposes the reason to assistive technology, with only the glyph hidden', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);
      const note = harness.routeNativeElement!.querySelector('.area-lock-note') as HTMLElement;

      expect(note.getAttribute('aria-hidden')).toBeNull();
      expect(note.classList.contains('cp-sr-only')).toBeFalse();
      expect(getComputedStyle(note).display).not.toBe('none');

      // Decoration beside the words, never the carrier of them.
      const icon = note.querySelector('.area-lock-icon')!;
      expect(icon.getAttribute('aria-hidden')).toBe('true');
      expect(note.textContent).toContain('open once you save this recipe');
    });

    it('drops the note once the recipe exists and the areas are open', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(harness.routeNativeElement!.querySelector('.area-lock-note')).toBeNull();
    });

    it('explains the rule without changing it — the areas are still disabled until the first save', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);

      // The note is a label on the existing behaviour, not a way around it.
      for (const label of ['Tools', 'Media', 'History']) {
        expect(tabButton(harness.routeNativeElement!, label)?.disabled)
          .withContext(label)
          .toBeTrue();
      }
      expect(tabButton(harness.routeNativeElement!, 'Edit')?.disabled).toBeFalse();
    });
  });

  describe('layout and information architecture', () => {
    it('splits the form into three tabs, General first, with the other two panels still unmounted', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const root = harness.routeNativeElement!;

      expect(component.selectedEditTabId()).toBe('general');

      // General holds what Details, Notes and Timing & yield held on the old single surface — including the
      // last of them, reachable with no tab click.
      for (const id of ['section-details', 'section-notes', 'section-timing']) {
        expect(root.querySelector('#' + id))
          .withContext(id)
          .toBeTruthy();
      }
      const yieldInput = root.querySelector('#recipe-yield-quantity') as HTMLElement;
      expect(yieldInput).toBeTruthy();
      expect(yieldInput.closest('cp-tab-panel')!.hasAttribute('hidden')).toBeFalse();

      // The other two are not merely hidden, they have not rendered — which is what makes the split real and
      // what revealFirstFieldError() has to account for.
      expect(root.querySelector('#section-ingredients')).toBeNull();
      expect(root.querySelector('#section-instructions')).toBeNull();

      selectEditTab(harness, 'Instructions');
      expect(root.querySelector('#section-instructions')).toBeTruthy();
      // General stays mounted behind it, so an edit made there is not discarded by looking elsewhere.
      expect(root.querySelector('#recipe-yield-quantity')).toBeTruthy();
      expect(root.querySelector('#recipe-yield-quantity')!.closest('cp-tab-panel')!.hasAttribute('hidden')).toBeTrue();
    });

    it('spans the form tablist across the full width of the card it sits in', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const root = harness.routeNativeElement!;

      // A width wide enough for the measurement to mean something: at Karma's default the editor is already
      // narrow, so a cap on the tabs would go unnoticed against a panel that is just as narrow.
      root.style.width = '80rem';
      harness.detectChanges();

      const tablist = formTablist(root);
      const host = tablist.closest('cp-tabs') as HTMLElement;
      const panel = root.querySelector('#panel-edit') as HTMLElement;

      // Full width of its own host, and the host full width of the area panel: nothing indents the tabs into
      // a column the way the rail's gutter once did.
      expect(panel.clientWidth).withContext('the edit panel at 80rem').toBeGreaterThan(600);
      expect(tablist.getBoundingClientRect().width).toBeCloseTo(host.getBoundingClientRect().width, 0);
      expect(host.getBoundingClientRect().width).toBeCloseTo(panel.clientWidth, 0);
    });

    it('keeps a tab switch inside the form: no navigation, and an unsaved edit survives it', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.title.set('An unsaved edit');
      harness.detectChanges();
      expect(component.isDirty()).toBeTrue();

      selectEditTab(harness, 'Ingredients');

      // A tablist of buttons, not the in-page anchors this replaced: nothing reaches the router, so the
      // unsaved-changes guard is never asked and the edit is still there.
      expect(component.selectedEditTabId()).toBe('ingredients');
      expect(confirmService.isOpen).toBeFalse();
      expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/r1');
      expect(component.isDirty()).toBeTrue();
      expect(component.title()).toBe('An unsaved edit');
    });

    it('stays within its own width at a narrow size, tabs included', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const root = harness.routeNativeElement!;

      // A phone, or a desktop at 200% zoom — both shrink the editor's CSS-px inline size, which is why this
      // host is a container rather than answering to a viewport media query.
      root.style.width = '24rem';
      harness.detectChanges();

      // Nothing reaches past the editor's own right edge (WCAG 2.2 SC 1.4.10, Reflow). Named rather than
      // counted, so a regression says what broke.
      const limit = root.getBoundingClientRect().right + 1;
      const overflowing = Array.from(root.querySelectorAll('*'))
        .filter((element) => element.getBoundingClientRect().right > limit)
        .map((element) => element.tagName.toLowerCase() + (element.id ? '#' + element.id : ''));
      expect(overflowing).withContext('overflowing the editor at 24rem').toEqual([]);

      // The tabs wrapped rather than scrolled, and each still meets the 40px target at that width.
      const tabs = Array.from(formTablist(root).querySelectorAll<HTMLButtonElement>('button[role="tab"]'));
      expect(tabs.length).toBe(3);
      for (const tab of tabs) {
        expect(tab.getBoundingClientRect().height)
          .withContext(tab.textContent?.trim())
          .toBeGreaterThanOrEqual(40);
      }

      // Still operable: the roving tabindex and arrow keys are the library's, exercised here through the
      // narrow layout to prove nothing here broke them.
      tabs[0].focus();
      tabs[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      harness.detectChanges();
      expect(document.activeElement).toBe(tabs[1]);
    });


    it('keeps each calculator lazy behind the Tools inner tabs, and History lazy behind its own area', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const root = harness.routeNativeElement!;

      // Nothing that fetches on mount has mounted just because the editor loaded.
      expect(root.querySelector('cp-recipe-scaling-preview')).toBeNull();
      expect(root.querySelector('cp-recipe-history')).toBeNull();

      tabButton(root, 'Tools')!.click();
      harness.detectChanges();

      // Opening Tools mounts only the calculator whose inner tab is selected — the other two stay lazy, so
      // one click does not start three requests.
      expect(root.querySelector('cp-recipe-scaling-preview')).toBeTruthy();
      expect(root.querySelector('cp-recipe-unit-conversion')).toBeNull();
      expect(root.querySelector('cp-recipe-yield-reconciliation')).toBeNull();
      expect(root.querySelector('cp-recipe-history')).toBeNull();

      tabButton(root, 'Convert')!.click();
      harness.detectChanges();
      expect(root.querySelector('cp-recipe-unit-conversion')).toBeTruthy();
      expect(root.querySelector('cp-recipe-yield-reconciliation')).toBeNull();
    });

    it('reaches the AI revision request from the Tools tab once the recipe has a current version', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: {
          ...RECIPE_DETAIL,
          currentVersion: {
            id: 'v1',
            versionNumber: 1,
            source: 'CreatorEdit',
            readiness: 'Draft',
            reason: null,
            createdAt: '2026-03-01T00:00:00Z',
          },
        },
      });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const root = harness.routeNativeElement!;

      tabButton(root, 'Tools')!.click();
      harness.detectChanges();
      expect(root.querySelector('cp-recipe-revision-request')).toBeNull();

      tabButton(root, 'Revise with AI')!.click();
      harness.detectChanges();

      expect(root.querySelector('cp-recipe-revision-request')).toBeTruthy();
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

    it('brings an errored field that is out of view back into view and focuses it, from another area entirely', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({
        status: 'validation_failed',
        fieldErrors: { yieldQuantity: ['Yield quantity must be greater than zero.'] },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const scrollSpy = spyOn(Element.prototype, 'scrollIntoView');

      // Save lives in the header, so it can be pressed from an area that is not the form at all.
      component.selectedAreaId.set('history');
      component.yieldQuantity.set(0);
      harness.detectChanges();

      await component.save();
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();

      // The area comes back to the form, the tab holding the field is selected, the control itself is
      // scrolled to and takes focus — which is what puts the existing visible focus ring on it. The generic
      // banner alone would have left the creator hunting.
      expect(component.selectedAreaId()).toBe('edit');
      expect(component.selectedEditTabId()).toBe('general');
      expect(scrollSpy.calls.all().some((call) => (call.object as Element).id === 'recipe-yield-quantity')).toBeTrue();
      expect(document.activeElement).toBe(harness.routeNativeElement!.querySelector('#recipe-yield-quantity'));

      // The message and aria-invalid are still adjacent to the field, as before.
      const input = harness.routeNativeElement!.querySelector('#recipe-yield-quantity')!;
      expect(input.getAttribute('aria-invalid')).toBe('true');
      expect(input.closest('cp-field')?.textContent).toContain('Yield quantity must be greater than zero.');
    });

    it('selects the tab holding an errored field, then scrolls to and focuses it', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({
        status: 'validation_failed',
        fieldErrors: { yieldQuantity: ['Yield quantity must be greater than zero.'] },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const scrollSpy = spyOn(Element.prototype, 'scrollIntoView');

      // The creator is on Instructions; the field at fault is in General, whose panel is mounted but hidden —
      // and a control inside a hidden panel cannot take focus, which is why the tab has to be selected first.
      selectEditTab(harness, 'Instructions');
      component.yieldQuantity.set(0);
      harness.detectChanges();
      const target = harness.routeNativeElement!.querySelector<HTMLElement>('#recipe-yield-quantity')!;
      expect(target.closest('cp-tab-panel')!.hasAttribute('hidden')).withContext('hidden before the save').toBeTrue();

      await component.save();
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();

      expect(component.selectedEditTabId()).toBe('general');
      expect(target.closest('cp-tab-panel')!.hasAttribute('hidden')).withContext('still hidden after the save').toBeFalse();
      expect(scrollSpy.calls.all().some((call) => (call.object as Element).id === 'recipe-yield-quantity')).toBeTrue();
      expect(document.activeElement).toBe(target);

      // The message and aria-invalid are adjacent to the field, as on the single surface before it.
      expect(target.getAttribute('aria-invalid')).toBe('true');
      expect(target.closest('cp-field')?.textContent).toContain('Yield quantity must be greater than zero.');
    });

    it('reveals the first errored field in page order when the server names several', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({
        status: 'validation_failed',
        fieldErrors: { yieldText: ['Too long.'], headnote: ['Too long.'] },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      await component.save();
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();

      // Headnote is in Details, above Timing's yield field, so it is the one a creator reading down the
      // page reaches first — not whichever key the server happened to serialise first.
      expect(document.activeElement).toBe(harness.routeNativeElement!.querySelector('#recipe-headnote'));
    });

    it('leaves the banner to speak for an error on a field that has no control of its own', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      recipeService.updateRecipe.and.resolveTo({
        status: 'validation_failed',
        // cuisineId is a real key the server can emit and this form has no control for — so it is genuinely
        // unlocatable, where an indexed ingredient or instruction key now resolves to its own tab.
        fieldErrors: { cuisineId: ['That is not a valid cuisine reference.'] },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      const scrollSpy = spyOn(Element.prototype, 'scrollIntoView');
      const activeBefore = document.activeElement;

      component.selectedAreaId.set('history');
      harness.detectChanges();

      await component.save();
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();

      // Nothing was guessed at: no scroll, no stolen focus, no area switch — and the generic banner is
      // still there saying so. Focusing some arbitrary other control would have been worse than silence.
      expect(scrollSpy).not.toHaveBeenCalled();
      expect(document.activeElement).toBe(activeBefore);
      expect(component.selectedAreaId()).toBe('history');
      expect(harness.routeNativeElement?.textContent).toContain('Some fields need attention.');
    });

    it('reveals the errored field on a failed create too, not only on an update', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'validation_failed',
        fieldErrors: { sourceUrl: ['Not a valid URL.'] },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      component.sourceUrl.set('not-a-url');
      await component.save();
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();

      expect(document.activeElement).toBe(harness.routeNativeElement!.querySelector('#recipe-source-url'));
    });

    /**
     * A refusal about a whole list — the ingredients or the method.
     *
     * The server validates each list as one rule with `CascadeMode.Stop`, so what comes back is a single
     * sentence under the flat key `ingredientGroups` or `instructions`, with no index. There is no one control
     * to blame, and before this the creator got only "Some fields need attention" with no tab and no sentence,
     * on the two surfaces that hold the most input.
     */
    describe('a refusal about a whole list', () => {
      const QUANTITY_PROBLEM = 'A quantity must be greater than zero.';
      const STEP_PROBLEM = 'An instruction step can be at most 2000 characters.';

      async function refusedWith(fieldErrors: Record<string, string[]>) {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
        recipeService.updateRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

        await component.save();
        harness.detectChanges();
        await harness.fixture.whenStable();
        harness.detectChanges();

        return { harness, component };
      }

      function tabLabels(harness: RouterTestingHarness): string[] {
        return Array.from(formTablist(harness.routeNativeElement!).querySelectorAll('button[role="tab"]')).map(
          (tab) => tab.textContent?.trim() ?? '',
        );
      }

      it('takes the creator to the Ingredients tab and says what the server said', async () => {
        const { harness, component } = await refusedWith({ ingredientGroups: [QUANTITY_PROBLEM] });

        expect(component.selectedEditTabId()).toBe('ingredients');

        const section = harness.routeNativeElement!.querySelector<HTMLElement>('#section-ingredients')!;
        expect(section.closest('cp-tab-panel')!.hasAttribute('hidden')).withContext('the panel is showing').toBeFalse();

        // The server's own sentence, under the heading of the list it is about. cp-form-section mints the id,
        // so the wiring is what is asserted here rather than a spelling.
        const problem = section.querySelector('.problem');
        expect(problem?.textContent).toContain(QUANTITY_PROBLEM);
        expect(problem?.getAttribute('role')).toBe('alert');

        // Focused, and described by the problem — so arriving there reads the heading and then the reason.
        expect(document.activeElement).toBe(section);
        expect(section.getAttribute('aria-describedby')).toBe(problem!.id);
      });

      it('does the same for the method', async () => {
        const { harness, component } = await refusedWith({ instructions: [STEP_PROBLEM] });

        expect(component.selectedEditTabId()).toBe('instructions');

        const section = harness.routeNativeElement!.querySelector<HTMLElement>('#section-instructions')!;
        expect(section.querySelector('.problem')?.textContent).toContain(STEP_PROBLEM);
        expect(document.activeElement).toBe(section);
      });

      /**
       * The case the reveal cannot cover on its own: two tabs refused at once. It can only take the creator to
       * one, so the other has to say so from its tab, where the label is also its accessible name.
       */
      it('marks every refused tab, not only the one it opened', async () => {
        const { harness, component } = await refusedWith({
          title: ['A recipe needs a title.'],
          ingredientGroups: [QUANTITY_PROBLEM],
          instructions: [STEP_PROBLEM],
        });

        // First in page order wins the reveal, which is General.
        expect(component.selectedEditTabId()).toBe('general');

        expect(tabLabels(harness)).toEqual([
          'General (1 problem)',
          'Ingredients (1 problem)',
          'Instructions (1 problem)',
        ]);
      });

      it('counts more than one problem on a tab, and leaves a clean tab unmarked', async () => {
        const { harness } = await refusedWith({
          yieldText: ['Too long.'],
          yieldQuantity: ['A yield must be greater than zero.'],
          ingredientGroups: [QUANTITY_PROBLEM],
        });

        expect(tabLabels(harness)).toEqual([
          'General (2 problems)',
          'Ingredients (1 problem)',
          'Instructions',
        ]);
      });

      // The banner stops saying "check your recipe details" when the problem is an ingredient line.
      it('points the banner at the tabs once it has somewhere to point', async () => {
        const { harness } = await refusedWith({ ingredientGroups: [QUANTITY_PROBLEM] });

        const banner = harness.routeNativeElement!.querySelector('.banner--error');
        expect(banner?.textContent).toContain('Some fields need attention.');
        expect(banner?.textContent).toContain('The tabs below say which');
      });

      /**
       * And keeps its old wording when it has nowhere to point. A key this form cannot locate leaves the banner
       * as the whole message, which is the honest-silence behaviour `revealFirstFieldError` documents — telling
       * the creator to look at the tabs would be sending them to look at nothing.
       */
      it('keeps the generic wording for a key it cannot locate, and marks no tab', async () => {
        const { harness, component } = await refusedWith({ cuisineId: ['That is not a valid cuisine reference.'] });

        const banner = harness.routeNativeElement!.querySelector('.banner--error');
        expect(banner?.textContent).toContain('Check your recipe details and try again.');
        expect(tabLabels(harness)).toEqual(['General', 'Ingredients', 'Instructions']);
        expect(component.selectedEditTabId()).withContext('nowhere to reveal').toBe('general');
        expect(harness.routeNativeElement!.querySelector('#section-ingredients .problem')).toBeNull();
      });

      it('clears the marks and the sentence once the next save succeeds', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
        recipeService.updateRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors: { ingredientGroups: [QUANTITY_PROBLEM] } });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

        await component.save();
        harness.detectChanges();
        expect(harness.routeNativeElement!.querySelector('#section-ingredients .problem')).toBeTruthy();

        recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...RECIPE_DETAIL, concurrencyToken: 'AAAAAAAAB9I=' } });
        component.title.set('Weeknight Chili');
        await component.save();
        harness.detectChanges();

        expect(harness.routeNativeElement!.querySelector('#section-ingredients .problem')).toBeNull();
        expect(tabLabels(harness)).toEqual(['General', 'Ingredients', 'Instructions']);
      });
    });

    /**
     * A refusal the server reported at a position — the line or step it is actually about.
     *
     * The client resolves a position and never decides what is wrong: every rule stays on the server. The one
     * piece of arithmetic here is which row a position names, and that is not the working copy's order, because
     * a row with no text is filtered out of the request.
     */
    describe('a refusal about one line', () => {
      const THREE_LINES: RecipeDetail = {
        ...RECIPE_DETAIL,
        ingredientGroups: [
          {
            id: 'g1',
            title: null,
            sortOrder: 0,
            ingredients: [0, 1, 2].map((index) => ({
              id: `ing${index}`,
              sortOrder: index,
              displayText: `line ${index}`,
              displayTextSource: 'Creator' as const,
              ingredientNameText: null,
              unitText: null,
              quantity: 1,
              quantityUpper: null,
              measurementUnitId: null,
              ingredientId: null,
              matchStatus: 'NotAttempted',
              preparationNote: null,
              isOptional: false,
              scalingBehavior: 'Proportional' as const,
            })),
          },
        ],
        instructionGroups: [
          {
            id: 'ig1',
            // Titled, so the group heading field renders at all — an untitled lone group has no heading to show.
            title: 'Batter',
            sortOrder: 0,
            steps: [0, 1].map((index) => ({
              id: `s${index}`,
              sortOrder: index,
              text: `step ${index}`,
              techniqueId: null,
              durationMinutes: null,
              temperatureValue: null,
              temperatureUnitId: null,
              note: null,
            })),
          },
        ],
      };

      async function refused(fieldErrors: Record<string, string[]>, recipe: RecipeDetail = THREE_LINES) {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe });
        recipeService.updateRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

        await component.save();
        harness.detectChanges();
        await harness.fixture.whenStable();
        harness.detectChanges();

        return { harness, component };
      }

      function rowProblems(harness: RouterTestingHarness): string[] {
        return Array.from(harness.routeNativeElement!.querySelectorAll('.row-problem')).map(
          (node) => node.textContent?.trim() ?? '',
        );
      }

      it('marks the line the server named, and no other', async () => {
        const { harness } = await refused({
          'ingredientGroups[0].ingredients[1].quantity': ['A quantity must be greater than zero.'],
        });
        selectEditTab(harness, 'Ingredients');

        expect(rowProblems(harness)).toEqual(['A quantity must be greater than zero.']);

        // On the second row specifically — the one at the position the server sent.
        const marked = harness.routeNativeElement!.querySelector('.row:has(.row-problem)');
        expect(marked?.textContent).toContain('line 1');
        expect(marked?.textContent).not.toContain('line 0');
      });

      it('says everything wrong with one line at once, rather than one refusal at a time', async () => {
        const { harness } = await refused({
          'ingredientGroups[0].ingredients[0].quantity': ['A quantity must be greater than zero.'],
          'ingredientGroups[0].ingredients[0].displayText': ['An ingredient line cannot be blank.'],
        });
        selectEditTab(harness, 'Ingredients');

        const [problem] = rowProblems(harness);
        expect(problem).toContain('A quantity must be greater than zero.');
        expect(problem).toContain('An ingredient line cannot be blank.');
        expect(rowProblems(harness).length).withContext('one row, one message').toBe(1);
      });

      /**
       * The mapping that is easy to get wrong: a row with no text never reaches the request, so the server's
       * position 1 is the *second row it was sent*, which here is the third row on screen.
       */
      it('counts positions as the request sent them, not as the editor holds them', async () => {
        const blanked: RecipeDetail = {
          ...THREE_LINES,
          ingredientGroups: [
            {
              ...THREE_LINES.ingredientGroups[0],
              ingredients: [
                { ...THREE_LINES.ingredientGroups[0].ingredients[0], displayText: '' },
                ...THREE_LINES.ingredientGroups[0].ingredients.slice(1),
              ],
            },
          ],
        };

        const { harness } = await refused(
          { 'ingredientGroups[0].ingredients[1].quantity': ['A quantity must be greater than zero.'] },
          blanked,
        );
        selectEditTab(harness, 'Ingredients');

        // Submitted lines were "line 1" and "line 2"; position 1 is the second of those.
        const marked = harness.routeNativeElement!.querySelector('.row:has(.row-problem)');
        expect(marked?.textContent).toContain('line 2');
      });

      it('puts a refused step on that step, and a refused heading on that heading', async () => {
        const { harness } = await refused({
          'instructions[0].steps[1].text': ['An instruction step cannot be blank.'],
          'instructions[0].title': ['An instruction group heading can be at most 120 characters.'],
        });
        selectEditTab(harness, 'Instructions');

        const stepField = harness.routeNativeElement!.querySelector('textarea[id="step-text-s1"]')!.closest('cp-field');
        expect(stepField?.textContent).toContain('An instruction step cannot be blank.');

        // And not on the step beside it.
        const other = harness.routeNativeElement!.querySelector('textarea[id="step-text-s0"]')!.closest('cp-field');
        expect(other?.textContent).not.toContain('cannot be blank');

        const heading = harness.routeNativeElement!.querySelector('input[id="group-title-ig1"]')!.closest('cp-field');
        expect(heading?.textContent).toContain('at most 120 characters');
      });

      // An indexed key still belongs to its list's tab, so the reveal and the markers have to read it as one.
      it('still selects the tab and marks it for an indexed key', async () => {
        const { harness, component } = await refused({
          'ingredientGroups[0].ingredients[1].quantity': ['A quantity must be greater than zero.'],
        });

        expect(component.selectedEditTabId()).toBe('ingredients');
        expect(
          Array.from(formTablist(harness.routeNativeElement!).querySelectorAll('button[role="tab"]')).map((tab) =>
            tab.textContent?.trim(),
          ),
        ).toEqual(['General', 'Ingredients (1 problem)', 'Instructions']);
      });

      /**
       * A position naming a row that is no longer there is dropped rather than pinned on whichever row moved
       * into the slot — that would blame a line the creator never wrote.
       */
      it('drops a position that names no row rather than blaming the wrong one', async () => {
        const { harness } = await refused({
          'ingredientGroups[0].ingredients[9].quantity': ['A quantity must be greater than zero.'],
        });
        selectEditTab(harness, 'Ingredients');

        expect(rowProblems(harness)).toEqual([]);
        // The tab still says something is wrong, because it is — just not which line.
        expect(
          Array.from(formTablist(harness.routeNativeElement!).querySelectorAll('button[role="tab"]')).map((tab) =>
            tab.textContent?.trim(),
          ),
        ).toContain('Ingredients (1 problem)');
      });

      it('clears the row marks once the next save succeeds', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: THREE_LINES });
        recipeService.updateRecipe.and.resolveTo({
          status: 'validation_failed',
          fieldErrors: { 'ingredientGroups[0].ingredients[1].quantity': ['A quantity must be greater than zero.'] },
        });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

        await component.save();
        harness.detectChanges();
        selectEditTab(harness, 'Ingredients');
        expect(rowProblems(harness).length).toBe(1);

        recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...THREE_LINES, concurrencyToken: 'AAAAAAAAB9I=' } });
        component.title.set('Weeknight Chili');
        await component.save();
        harness.detectChanges();

        expect(rowProblems(harness)).toEqual([]);
      });
    });
  });


  /**
   * Total time, derived from prep + cook + rest and not editable.
   *
   * The reversal worth stating: the server's own model argues for a stored total, because prep overlaps
   * cooking and "about two hours, mostly waiting" is a creator meaning it. The editor no longer offers that —
   * the total moves with its parts and has no control of its own — so what these tests are about is the two
   * things that follow: the number arriving without an interaction, and a recipe saved with a total of its own
   * being told beforehand that a save will replace it.
   */
  describe('total time', () => {
    function total(harness: RouterTestingHarness): string {
      return harness.routeNativeElement!.querySelector('[id="recipe-total-time"]')?.textContent?.trim() ?? '';
    }

    function note(harness: RouterTestingHarness): string {
      return harness.routeNativeElement!.querySelector('[id="recipe-total-time-hint"]')?.textContent?.trim() ?? '';
    }

    function replacementNote(harness: RouterTestingHarness): string {
      return harness.routeNativeElement!.querySelector('.total-time-replacement')?.textContent?.trim() ?? '';
    }

    it('has no control of its own, so nothing can be typed into it', async () => {
      const recipeService = recipeServiceSpy();
      const { harness } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(harness.routeNativeElement!.querySelector('input[id="recipe-total-time"]')).toBeNull();

      const output = harness.routeNativeElement!.querySelector('output[id="recipe-total-time"]');
      expect(output).not.toBeNull();

      // Explicitly off, not merely unset: `output` carries an implicit role="status", which is a polite live
      // region, so three number fields feeding it would announce a bare integer per keystroke.
      expect(output!.getAttribute('aria-live')).toBe('off');

      // And not a focus target: there is nothing here to correct, so revealFirstFieldError aims at the section.
      expect(output!.hasAttribute('tabindex')).toBeFalse();
    });

    // A recipe that says nothing about its timing has no total, and 0 would be an answer rather than the
    // absence of one. The dash is what stands in, and the note says what would fill it.
    it('all three blank: no total, and never a total of 0', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      expect(component.timingSum()).toBeNull();
      expect(component.totalTimeMinutes()).toBeNull();
      // The dash is aria-hidden and the words beside it are what is announced: an em dash is not spoken at
      // default punctuation levels, so on its own the state would read as an empty field.
      expect(total(harness)).toContain('No total yet');
      expect(harness.routeNativeElement!.querySelector('.derived-total-absent')?.getAttribute('aria-hidden')).toBe('true');
      expect(note(harness)).toBe('Added up from prep, cook and rest times once one of them is recorded.');
    });

    // The whole point of the change: the number is there once the part is entered, with nothing to press.
    it('a part entered updates the total with no further interaction', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.cookTimeMinutes.set(45);
      harness.detectChanges();

      expect(component.totalTimeMinutes()).toBe(45);
      // Self-describing, because an `output` is read on its own in browse mode.
      expect(total(harness)).toBe('45 minutes');
      expect(note(harness)).toBe('cook 45 = 45 minutes.');

      component.prepTimeMinutes.set(15);
      harness.detectChanges();

      expect(component.totalTimeMinutes()).toBe(60);
      expect(total(harness)).toBe('60 minutes');
      expect(note(harness)).toBe('prep 15 + cook 45 = 60 minutes.');

      // And downwards too, which a sum that only ever grew would get wrong.
      component.cookTimeMinutes.set(5);
      harness.detectChanges();

      expect(component.totalTimeMinutes()).toBe(20);
      expect(note(harness)).toBe('prep 15 + cook 5 = 20 minutes.');
    });

    it('a recorded zero counts, because the creator recorded it', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.prepTimeMinutes.set(0);
      component.cookTimeMinutes.set(20);
      harness.detectChanges();

      expect(component.timingSum()).toEqual({ minutes: 20, parts: ['prep 0', 'cook 20'] });
      expect(component.totalTimeMinutes()).toBe(20);
    });

    it('the derived total is what a create submits', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      component.prepTimeMinutes.set(15);
      component.cookTimeMinutes.set(30);
      harness.detectChanges();

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(request.totalTimeMinutes).toBe(45);
      expect(request.prepTimeMinutes).toBe(15);
      expect(request.cookTimeMinutes).toBe(30);
    });

    /**
     * The one case the old behaviour existed to protect, now handled by saying so rather than by keeping the
     * number. A recipe saved with a total of its own shows the derived one, is told what a save would do to
     * the stored one, and submits the derived one.
     */
    it('a stored total that disagrees is announced, and replaced by a save', async () => {
      const recipeService = recipeServiceSpy();
      const loaded: RecipeDetail = {
        ...RECIPE_DETAIL,
        prepTimeMinutes: 20,
        cookTimeMinutes: 40,
        restTimeMinutes: 30,
        totalTimeMinutes: 65,
      };
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: loaded });
      recipeService.updateRecipe.and.resolveTo({
        status: 'updated',
        recipe: { ...loaded, totalTimeMinutes: 90, concurrencyToken: 'AAAAAAAAB9I=' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(component.totalTimeMinutes()).withContext('the sum, not the stored 65').toBe(90);
      expect(note(harness)).toBe('prep 20 + cook 40 + rest 30 = 90 minutes.');
      expect(replacementNote(harness)).toBe('This recipe is saved with a total of 65 minutes. Saving will replace it with 90.');
      // Loading is not an edit: the total is no longer part of the form, so nothing about it can make the
      // form dirty on arrival and warn about leaving a recipe nobody touched.
      expect(component.isDirty()).toBeFalse();

      await component.save();

      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.totalTimeMinutes).toEqual({ submitted: true, value: 90 });

      // The saved recipe now agrees, so there is nothing left to announce.
      harness.detectChanges();
      expect(replacementNote(harness)).toBe('');
    });

    it('a stored total that already agrees is not announced', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: { ...RECIPE_DETAIL, prepTimeMinutes: 20, cookTimeMinutes: 40, restTimeMinutes: 30, totalTimeMinutes: 90 },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(component.totalTimeMinutes()).toBe(90);
      expect(replacementNote(harness)).toBe('');
    });

    // Three times each inside the server's limit can add to a sum outside it. There is no control to correct
    // it on now, so the note has to carry the whole explanation — and nothing unsendable is submitted.
    it('a sum beyond the recordable maximum yields no total and says why', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Aged Vinegar', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Aged Vinegar');
      component.prepTimeMinutes.set(525_600);
      component.cookTimeMinutes.set(60);
      harness.detectChanges();

      expect(component.totalTimeMinutes()).toBeNull();
      expect(total(harness)).toContain('No total yet');
      expect(note(harness)).toContain('more than the longest time that can be recorded');

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(request.totalTimeMinutes).toBeNull();
    });

    it('a stored total that the parts can no longer produce is announced as a clearing', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: { ...RECIPE_DETAIL, prepTimeMinutes: 525_600, cookTimeMinutes: 60, restTimeMinutes: null, totalTimeMinutes: 120 },
      });
      const { harness } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(replacementNote(harness)).toBe(
        'This recipe is saved with a total of 120 minutes. Saving will clear it, because its parts no longer add up to a total that can be recorded.',
      );
    });

    it('the note is wired to the field, so it is not read by nobody', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.cookTimeMinutes.set(45);
      harness.detectChanges();
      // cp-field wires the hint in an afterRenderEffect, and the harness's detectChanges does not run the
      // render hooks. A real application ticks them on every cycle; this is the test asking for one.
      TestBed.inject(ApplicationRef).tick();

      const output = harness.routeNativeElement!.querySelector('output[id="recipe-total-time"]');
      expect(output?.getAttribute('aria-describedby')).toContain('recipe-total-time-hint');
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
    /**
     * Instructions is one of the Edit area's three form tabs, and its panel mounts when that tab is first
     * selected — so this is where the cases below get their DOM. The single place that changes again if the
     * section ever moves.
     */
    async function openInstructionsTab(path: string, recipeService: ReturnType<typeof recipeServiceSpy>) {
      const { harness, component } = await createHarness(path, recipeService);
      expect(harness.routeNativeElement!.querySelector('#section-instructions')).withContext('before selecting the tab').toBeNull();

      selectEditTab(harness, 'Instructions');

      expect(harness.routeNativeElement!.querySelector('#section-instructions')).toBeTruthy();
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
        {
          id: null,
          title: 'Batter',
          steps: [
            {
              id: null,
              text: 'Cream the butter.',
              durationMinutes: 5,
              note: 'Room temperature',
              // Submitted although no control sets them: an omitted field is applied as null, so leaving
              // them out is how a save wipes a step's technique and temperature.
              techniqueId: null,
              temperatureValue: null,
              temperatureUnitId: null,
            },
          ],
        },
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
        {
          key: 'g1',
          id: 'g1',
          title: 'Batter',
          steps: [
            {
              key: 's1',
              id: 's1',
              text: 'Mix.',
              note: 'Gently',
              durationMinutes: 3,
              // Shown without being asked for, because the recipe already records one.
              showDuration: true,
              // Carried on the working copy although no control edits them, so a save cannot drop them.
              techniqueId: null,
              temperatureValue: null,
              temperatureUnitId: null,
            },
          ],
        },
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
        {
          id: 'g1',
          title: 'Batter',
          steps: [
            {
              id: 's1',
              text: 'Mix thoroughly.',
              durationMinutes: null,
              note: null,
              techniqueId: null,
              temperatureValue: null,
              temperatureUnitId: null,
            },
          ],
        },
      ]);
    });

    /**
     * A step's duration is optional and always was, but a number box on every step read as something a creator
     * was expected to fill in. It is offered instead — except where the recipe already records one, which the
     * creator must never have to go looking for.
     */
    describe('a step’s duration', () => {
      const WITH_DURATION: RecipeDetail = {
        ...RECIPE_DETAIL,
        instructionGroups: [
          {
            id: 'g1',
            title: null,
            sortOrder: 0,
            steps: [
              { id: 's1', sortOrder: 0, text: 'Brown the beef.', techniqueId: null, durationMinutes: 25, temperatureValue: null, temperatureUnitId: null, note: null },
              { id: 's2', sortOrder: 1, text: 'Add the tomatoes.', techniqueId: null, durationMinutes: null, temperatureValue: null, temperatureUnitId: null, note: null },
            ],
          },
        ],
      };

      function durationBox(harness: RouterTestingHarness, stepKey: string): HTMLInputElement | null {
        return harness.routeNativeElement!.querySelector<HTMLInputElement>(`input[id="step-duration-${stepKey}"]`);
      }

      function revealButton(harness: RouterTestingHarness, stepIndex: number): HTMLButtonElement | null {
        return harness.routeNativeElement!.querySelector<HTMLButtonElement>(
          `button[aria-label="Add a duration to step ${stepIndex}"]`,
        );
      }

      function stepKeys(component: RecipeEditorComponent): readonly string[] {
        return component.instructionGroups()[0].steps.map((step) => step.key);
      }

      it('offers the field on a new step rather than presenting it, and saves cleanly without one', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.createRecipe.and.resolveTo({
          status: 'created',
          recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
        });
        const { harness, component } = await openInstructionsTab('/cozy-fall/recipes/new', recipeService);

        component.title.set('Weeknight Chili');
        component.addInstructionStep();
        const [stepKey] = stepKeys(component);
        component.updateInstructionStep(component.instructionGroups()[0].key, stepKey, { text: 'Brown the beef.' });
        harness.detectChanges();

        expect(durationBox(harness, stepKey)).withContext('no box until it is asked for').toBeNull();
        expect(revealButton(harness, 1)).withContext('the offer').toBeTruthy();

        await component.save();

        // Still on the wire, still submitted — as null, which is what a step with no duration has always sent.
        const [, request] = recipeService.createRecipe.calls.mostRecent().args;
        expect(request.instructions![0].steps![0].durationMinutes).toBeNull();
      });

      it('shows a recorded duration without being asked, and still submits it', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_DURATION });
        recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...WITH_DURATION, concurrencyToken: 'AAAAAAAAB9I=' } });
        const { harness, component } = await openInstructionsTab('/cozy-fall/recipes/r1', recipeService);

        // The restriction this guards: an existing duration is not hidden behind the offer.
        expect(durationBox(harness, 's1')?.value).toBe('25');
        expect(revealButton(harness, 1)).withContext('nothing to offer on a step that has one').toBeNull();

        // And the step beside it, which has none, is the offer.
        expect(durationBox(harness, 's2')).toBeNull();
        expect(revealButton(harness, 2)).toBeTruthy();

        await component.save();

        const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
        expect(request.instructions.value![0].steps![0].durationMinutes).toBe(25);
        expect(request.instructions.value![0].steps![1].durationMinutes).toBeNull();
      });

      // Revealing writes no duration, so it is not an edit to the recipe. Dirty state is built from
      // buildInstructions(), which the flag is deliberately not part of.
      it('revealing the field does not make the recipe dirty', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_DURATION });
        const { harness, component } = await openInstructionsTab('/cozy-fall/recipes/r1', recipeService);

        expect(component.isDirty()).toBeFalse();

        revealButton(harness, 2)!.click();
        harness.detectChanges();

        expect(durationBox(harness, 's2')).withContext('the revealed field').toBeTruthy();
        expect(component.isDirty()).withContext('asking for a field is not filling one in').toBeFalse();

        // Typing in it is.
        component.updateInstructionStep(component.instructionGroups()[0].key, 's2', { durationMinutes: 4 });
        expect(component.isDirty()).toBeTrue();
      });

      // A field vanishing out from under the cursor is worse than an empty one, so it stays for the life of the
      // form — and the emptied value still submits as null.
      it('keeps the field once revealed, even when the creator empties it', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_DURATION });
        recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...WITH_DURATION, concurrencyToken: 'AAAAAAAAB9I=' } });
        const { harness, component } = await openInstructionsTab('/cozy-fall/recipes/r1', recipeService);

        component.updateInstructionStep(component.instructionGroups()[0].key, 's1', { durationMinutes: null });
        harness.detectChanges();

        expect(durationBox(harness, 's1')).withContext('still there').toBeTruthy();
        expect(revealButton(harness, 1)).toBeNull();

        await component.save();
        const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
        expect(request.instructions.value![0].steps![0].durationMinutes).toBeNull();
      });

      it('says the field is optional, and the field says so to a screen reader too', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_DURATION });
        const { harness } = await openInstructionsTab('/cozy-fall/recipes/r1', recipeService);

        const box = durationBox(harness, 's1')!;
        const describedBy = box.getAttribute('aria-describedby');
        expect(describedBy).withContext('cp-field wires its hint to the control').toContain('step-duration-s1-hint');

        const hint = harness.routeNativeElement!.querySelector(`[id="step-duration-s1-hint"]`);
        expect(hint?.textContent).toContain('Optional');
        // Optional means optional: nothing claims otherwise to assistive technology.
        expect(box.getAttribute('aria-required')).toBeNull();
      });

      /**
       * The convention the asterisk relies on, stated once. Three fields across the three tabs are required and
       * sixteen are not, so marking the optional ones would mean marking almost everything.
       */
      it('states once what the asterisk means, and marks the step text required to assistive tech', async () => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_DURATION });
        const { harness } = await openInstructionsTab('/cozy-fall/recipes/r1', recipeService);

        const legend = harness.routeNativeElement!.querySelector('.required-legend');
        expect(legend?.textContent).toContain('are required');
        // The glyph is decoration; the words beside it are what is read out.
        expect(legend?.querySelector('[aria-hidden="true"]')?.textContent).toBe('*');
        expect(legend?.querySelector('.cp-sr-only')?.textContent).toContain('asterisk');

        const stepText = harness.routeNativeElement!.querySelector('textarea[id="step-text-s1"]');
        expect(stepText?.getAttribute('aria-required')).withContext('the * is no longer visual only').toBe('true');
      });
    });
  });

  describe('ingredients', () => {
    // These build rows directly, which covers what this component does with a row it is given. What a row
    // arrives carrying is the child editor's business, and the test below drives that path through the DOM.
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
              // The row was given its text outright, so nothing here may re-derive it.
              displayTextSource: 'Creator',
              ingredientNameText: null,
              unitText: null,
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
                  displayTextSource: 'Creator',
                  unitText: null,
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
              displayTextIsComposed: false,
              ingredientNameText: 'flour',
              quantityText: '2',
              quantityUpper: null,
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
                  displayTextSource: 'Creator',
                  unitText: null,
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
            { id: 'ing1', displayText: '3 cups flour', displayTextSource: 'Creator', ingredientNameText: null, unitText: null, quantity: 3, quantityUpper: null, measurementUnitId: null, ingredientId: null, preparationNote: null, isOptional: false, scalingBehavior: 'Proportional' },
          ],
        },
      ]);
    });

    /**
     * The hand-entry path end to end, through the real child editor and its real inputs: the bug this covers
     * was a line filled into every visible field, reported as saved, and absent from the request body — because
     * every test until this one built its rows directly and set the one field no control in the UI sets.
     */
    it('a line typed into the row’s own fields reaches the request and survives a reload', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      selectEditTab(harness, 'Ingredients');

      const clickButton = (label: string) => {
        const button = Array.from(harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('button')).find(
          (candidate) => candidate.textContent?.trim() === label,
        );
        expect(button).withContext(`the "${label}" button`).toBeTruthy();
        button!.click();
        harness.detectChanges();
      };

      const typeInto = (field: string, rowKey: string, value: string) => {
        const input = harness.routeNativeElement!.querySelector<HTMLInputElement>(`input[id="row-${field}-${rowKey}"]`);
        expect(input).withContext(`the row's ${field} input`).toBeTruthy();
        input!.value = value;
        input!.dispatchEvent(new Event('input'));
        harness.detectChanges();
      };

      clickButton('Add ingredient group');
      clickButton('Add ingredient');

      const rowKey = component.editedIngredientGroups()[0].ingredients[0].key;
      typeInto('quantity', rowKey, '2');
      typeInto('unit', rowKey, 'cups');
      typeInto('ingredient', rowKey, 'all-purpose flour');
      typeInto('prep', rowKey, 'sifted');

      const saved: RecipeDetail = {
        ...RECIPE_DETAIL,
        concurrencyToken: 'AAAAAAAAB9I=',
        ingredientGroups: [
          {
            id: 'g9',
            title: null,
            sortOrder: 0,
            ingredients: [
              {
                id: 'ing9',
                sortOrder: 0,
                displayText: '2 cups all-purpose flour, sifted',
                ingredientNameText: null,
                quantity: 2,
                quantityUpper: null,
                measurementUnitId: null,
                ingredientId: null,
                displayTextSource: 'Creator',
                unitText: null,
                matchStatus: 'NotAttempted',
                preparationNote: 'sifted',
                isOptional: false,
                scalingBehavior: 'Proportional',
              },
            ],
          },
        ],
      };
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: saved });

      await component.save();

      // In the request body: the line is there, carrying the creator's own wording of the quantity, unit and
      // ingredient — the last two have nowhere else to go, since IngredientInput has no free-text field for
      // either one.
      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.ingredientGroups).toEqual({
        submitted: true,
        value: [
          {
            id: null,
            title: null,
            ingredients: [
              {
                id: null,
                displayText: '2 cups all-purpose flour, sifted',
                // Assembled from the fields, and said to be — so a reload keeps assembling it rather than
                // freezing a line that would then contradict the fields beside it.
                displayTextSource: 'Composed',
                // The creator's own words for the name and the unit, neither of which resolved to a reference.
                ingredientNameText: 'all-purpose flour',
                unitText: 'cups',
                quantity: 2,
                quantityUpper: null,
                measurementUnitId: null,
                ingredientId: null,
                preparationNote: 'sifted',
                isOptional: false,
                scalingBehavior: 'Proportional',
              },
            ],
          },
        ],
      });

      // And still there after a full reload, rendered rather than blank.
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: saved });
      await component.reloadAfterConflict();
      harness.detectChanges();

      expect(component.editedIngredientGroups()[0].ingredients[0].displayText).toBe('2 cups all-purpose flour, sifted');
      expect(harness.routeNativeElement!.textContent).toContain('2 cups all-purpose flour, sifted');
    });

    /**
     * The other half of the hand-entry path: an assembled line is still assembling one when the recipe is
     * opened again, and a line the creator wrote is still theirs. The difference is the recipe's own record of
     * which it is — not a comparison of the line against its parts, which cannot tell a line somebody wrote
     * from one that merely reads like its parts.
     */
    it('an assembled line keeps following the fields after a reload, and a written one never does', async () => {
      const lineFrom = (displayTextSource: 'Creator' | 'Composed'): RecipeDetail => ({
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
                displayTextSource,
                ingredientNameText: 'flour',
                unitText: 'cups',
                quantity: 2,
                quantityUpper: null,
                measurementUnitId: null,
                ingredientId: null,
                matchStatus: 'NotAttempted' as const,
                preparationNote: null,
                isOptional: false,
                scalingBehavior: 'Proportional',
              },
            ],
          },
        ],
      });

      const editQuantityTo = async (detail: RecipeDetail, value: string) => {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: detail });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
        selectEditTab(harness, 'Ingredients');

        const rowKey = component.editedIngredientGroups()[0].ingredients[0].key;
        const input = harness.routeNativeElement!.querySelector<HTMLInputElement>(`input[id="row-quantity-${rowKey}"]`);
        expect(input).withContext('the row quantity input').toBeTruthy();
        input!.value = value;
        input!.dispatchEvent(new Event('input'));
        harness.detectChanges();

        return component.editedIngredientGroups()[0].ingredients[0];
      };

      // The unit and the name come back as the creator wrote them — they have no reference to be read from.
      const composed = await editQuantityTo(lineFrom('Composed'), '3');
      expect(composed.unitLabel).toBe('cups');
      expect(composed.ingredientNameText).toBe('flour');
      expect(composed.displayText).toBe('3 cups flour');

      const written = await editQuantityTo(lineFrom('Creator'), '3');
      expect(written.unitLabel).toBe('cups');
      expect(written.displayText).toBe('2 cups flour');
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
                displayTextSource: 'Creator',
                unitText: null,
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
              displayTextIsComposed: false,
              ingredientNameText: '',
              quantityText: '2',
              quantityUpper: null,
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

    /**
     * A recorded range, which the editor offers no control for and therefore has to carry.
     *
     * `IngredientGroups` is a full replace and the server applies a submitted line wholesale, so a working copy
     * that drops the upper bound is a working copy whose next save clears it — a recipe losing "2–3 cups"
     * because someone renamed the title, and being told it saved.
     */
    describe('an ingredient line that records a range', () => {
      const RANGED_DETAIL: RecipeDetail = {
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
                displayText: '2–3 cups flour',
                displayTextSource: 'Creator',
                ingredientNameText: 'flour',
                unitText: 'cups',
                quantity: 2,
                quantityUpper: 3,
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
      };

      async function loadedWithRange(recipeService: ReturnType<typeof recipeServiceSpy>) {
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RANGED_DETAIL });
        recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...RANGED_DETAIL, concurrencyToken: 'AAAAAAAAB9I=' } });
        return createHarness('/cozy-fall/recipes/r1', recipeService);
      }

      function submittedLine(recipeService: ReturnType<typeof recipeServiceSpy>) {
        const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
        return request.ingredientGroups.value![0].ingredients![0];
      }

      // The bug, as a creator hits it: change the title and nothing else.
      it('keeps the upper bound through a save that never touched the line', async () => {
        const recipeService = recipeServiceSpy();
        const { harness, component } = await loadedWithRange(recipeService);

        const title = harness.routeNativeElement!.querySelector<HTMLInputElement>('#recipe-title')!;
        title.value = 'Weeknight Chili';
        title.dispatchEvent(new Event('input'));
        harness.detectChanges();

        await component.save();

        expect(submittedLine(recipeService).quantity).toBe(2);
        expect(submittedLine(recipeService).quantityUpper).withContext('the range the recipe recorded').toBe(3);
        // And the creator's own wording for the line, which is where the range is actually written.
        expect(submittedLine(recipeService).displayText).toBe('2–3 cups flour');
      });

      /**
       * The other half of carrying it: there is no control for the upper bound, so once the quantity itself is
       * retyped the recorded range no longer describes what the creator wrote. "2–3" edited to "5" must not be
       * submitted as 5–3.
       */
      it('drops the upper bound once the quantity itself is retyped', async () => {
        const recipeService = recipeServiceSpy();
        const { harness, component } = await loadedWithRange(recipeService);
        selectEditTab(harness, 'Ingredients');

        const quantity = harness.routeNativeElement!.querySelector<HTMLInputElement>('input[id="row-quantity-ing1"]')!;
        expect(quantity).withContext("the row's quantity input").toBeTruthy();
        quantity.value = '5';
        quantity.dispatchEvent(new Event('input'));
        harness.detectChanges();

        await component.save();

        expect(submittedLine(recipeService).quantity).toBe(5);
        expect(submittedLine(recipeService).quantityUpper).toBeNull();
        // The line is the creator's, so their wording of the old range stays theirs to edit.
        expect(submittedLine(recipeService).displayText).toBe('2–3 cups flour');
      });

      // Editing a different field on the same line leaves the range alone — only the quantity invalidates it.
      it('keeps the upper bound when another field on the same line is edited', async () => {
        const recipeService = recipeServiceSpy();
        const { harness, component } = await loadedWithRange(recipeService);
        selectEditTab(harness, 'Ingredients');

        const prep = harness.routeNativeElement!.querySelector<HTMLInputElement>('input[id="row-prep-ing1"]')!;
        prep.value = 'sifted';
        prep.dispatchEvent(new Event('input'));
        harness.detectChanges();

        await component.save();

        expect(submittedLine(recipeService).preparationNote).toBe('sifted');
        expect(submittedLine(recipeService).quantityUpper).toBe(3);
      });
    });
  });

  /**
   * Sections are opt-in on both halves of the form. The wire contract is unchanged — lines still travel inside
   * an IngredientGroupInput and steps inside an InstructionGroupInput (R.1) — so what these prove is that the
   * group a creator never asked for is untitled, invisible, and submitted exactly as the untitled group it is.
   */
  describe('ungrouped by default', () => {
    const clickButton = (harness: RouterTestingHarness, label: string) => {
      const button = Array.from(harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('button')).find(
        (candidate) => candidate.textContent?.trim() === label,
      );
      expect(button).withContext(`the "${label}" button`).toBeTruthy();
      button!.click();
      harness.detectChanges();
    };

    const typeInto = (harness: RouterTestingHarness, selector: string, value: string) => {
      const field = harness.routeNativeElement!.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector);
      expect(field).withContext(selector).toBeTruthy();
      field!.value = value;
      field!.dispatchEvent(new Event('input'));
      harness.detectChanges();
    };

    it('takes an ingredient and a step with no group made first, and saves each in one untitled group', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      typeInto(harness, '#recipe-title', 'Weeknight Chili');

      // Ingredients: the empty state's own action, with no group made first and no heading shown for the one
      // the wire needs.
      selectEditTab(harness, 'Ingredients');
      clickButton(harness, 'Add ingredient');
      const rowKey = component.editedIngredientGroups()[0].ingredients[0].key;
      typeInto(harness, `#row-quantity-${rowKey}`, '2');
      typeInto(harness, `#row-unit-${rowKey}`, 'cups');
      typeInto(harness, `#row-ingredient-${rowKey}`, 'kidney beans');
      expect(harness.routeNativeElement!.querySelector('input[id^="ingredient-group-title-"]')).toBeNull();

      // Instructions: the same, from its own empty state.
      selectEditTab(harness, 'Instructions');
      clickButton(harness, 'Add step');
      const stepKey = component.instructionGroups()[0].steps[0].key;
      typeInto(harness, `#step-text-${stepKey}`, 'Simmer for an hour.');
      expect(harness.routeNativeElement!.querySelector('input[id^="group-title-"]')).toBeNull();

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(request.ingredientGroups).toEqual([
        {
          id: null,
          title: null,
          ingredients: [
            {
              id: null,
              displayText: '2 cups kidney beans',
              displayTextSource: 'Composed',
              ingredientNameText: 'kidney beans',
              unitText: 'cups',
              quantity: 2,
              quantityUpper: null,
              measurementUnitId: null,
              ingredientId: null,
              preparationNote: null,
              isOptional: false,
              scalingBehavior: 'Proportional',
            },
          ],
        },
      ]);
      expect(request.instructions).toEqual([
        {
          id: null,
          title: null,
          steps: [
            {
              id: null,
              text: 'Simmer for an hour.',
              durationMinutes: null,
              note: null,
              techniqueId: null,
              temperatureValue: null,
              temperatureUnitId: null,
            },
          ],
        },
      ]);
    });

    /**
     * The group nav scrolls; it does not mount. So a line typed into the second group and then navigated away
     * from is still in the DOM, still in the working copy, and still in the request — which is the property a
     * lazily-mounted nav would have quietly broken.
     */
    it('counts an edit in a group the creator has navigated away from: still lights the pill and still submits', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Streusel cake', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      const root = () => harness.routeNativeElement!;

      const clickButton = (label: string) => {
        const button = Array.from(root().querySelectorAll<HTMLButtonElement>('button')).find(
          (candidate) => candidate.textContent?.trim() === label,
        );
        expect(button).withContext(`the "${label}" button`).toBeTruthy();
        button!.click();
        harness.detectChanges();
      };

      const typeInto = (selector: string, value: string) => {
        const field = root().querySelector<HTMLInputElement>(selector);
        expect(field).withContext(selector).toBeTruthy();
        field!.value = value;
        field!.dispatchEvent(new Event('input'));
        harness.detectChanges();
      };

      component.title.set('Streusel cake');
      selectEditTab(harness, 'Ingredients');

      // Two groups, the second of which gets the line.
      clickButton('Add ingredient group');
      clickButton('Add ingredient group');
      const [first, second] = component.editedIngredientGroups();
      typeInto(`#ingredient-group-title-${first.key}`, 'For the cake');
      typeInto(`#ingredient-group-title-${second.key}`, 'For the streusel');

      const addToSecond = Array.from(root().querySelectorAll<HTMLButtonElement>('button')).filter(
        (button) => button.textContent?.trim() === 'Add ingredient',
      )[1];
      addToSecond.click();
      harness.detectChanges();
      const rowKey = component.editedIngredientGroups()[1].ingredients[0].key;
      typeInto(`#row-quantity-${rowKey}`, '3');
      typeInto(`#row-unit-${rowKey}`, 'tbsp');
      typeInto(`#row-ingredient-${rowKey}`, 'demerara sugar');

      // Navigate back to the first group: the second is now scrolled away from, not unmounted.
      const navLinks = Array.from(root().querySelectorAll<HTMLAnchorElement>('nav[aria-label="Ingredient groups"] a'));
      expect(navLinks.length).toBe(2);
      navLinks[0].click();
      harness.detectChanges();
      expect(navLinks[0].getAttribute('aria-current')).toBe('true');
      expect(root().querySelector(`#row-ingredient-${rowKey}`)).withContext('still rendered').toBeTruthy();

      // Still dirty, and the pill says so.
      expect(component.isDirty()).toBeTrue();
      expect(root().querySelector('.dirty-indicator')?.textContent).toContain('Unsaved changes');

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      const groups = request.ingredientGroups as IngredientGroupInput[];
      expect(groups.map((group) => group.title)).toEqual(['For the cake', 'For the streusel']);
      expect(groups[0].ingredients).toEqual([]);
      expect(groups[1].ingredients!.map((line) => line.displayText)).toEqual(['3 tbsp demerara sugar']);
    });

    it('opens a recipe that has titled groups showing them, and round-trips them unchanged', async () => {
      const titled: RecipeDetail = {
        ...RECIPE_DETAIL,
        ingredientGroups: [
          {
            id: 'g1',
            title: 'For the crust',
            sortOrder: 0,
            ingredients: [
              {
                id: 'ing1',
                sortOrder: 0,
                displayText: '2 cups flour',
                displayTextSource: 'Creator',
                ingredientNameText: 'flour',
                unitText: 'cups',
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
          { id: 'g2', title: 'For the filling', sortOrder: 1, ingredients: [] },
        ],
        instructionGroups: [
          {
            id: 'ig1',
            title: 'The day before',
            sortOrder: 0,
            steps: [
              {
                id: 'step1',
                sortOrder: 0,
                text: 'Chill the dough.',
                techniqueId: null,
                durationMinutes: null,
                temperatureValue: null,
                temperatureUnitId: null,
                note: null,
              },
            ],
          },
          { id: 'ig2', title: 'On the day', sortOrder: 1, steps: [] },
        ],
      };

      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: titled });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: titled });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      selectEditTab(harness, 'Ingredients');
      await harness.fixture.whenStable();
      harness.detectChanges();
      expect(
        Array.from(harness.routeNativeElement!.querySelectorAll<HTMLInputElement>('input[id^="ingredient-group-title-"]')).map(
          (input) => input.value,
        ),
      ).toEqual(['For the crust', 'For the filling']);

      selectEditTab(harness, 'Instructions');
      await harness.fixture.whenStable();
      harness.detectChanges();
      expect(component.showInstructionGroups()).toBeTrue();
      expect(
        Array.from(harness.routeNativeElement!.querySelectorAll<HTMLInputElement>('input[id^="group-title-"]')).map(
          (input) => input.value,
        ),
      ).toEqual(['The day before', 'On the day']);

      // An edit elsewhere must not flatten or rename anything: both groups go back with their own ids and
      // titles, and the empty second group is still a group.
      component.title.set('Renamed');
      await component.save();

      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      const submittedIngredients = request.ingredientGroups.value as IngredientGroupInput[];
      const submittedInstructions = request.instructions.value as InstructionGroupInput[];

      expect(submittedIngredients.map((group) => [group.id, group.title])).toEqual([
        ['g1', 'For the crust'],
        ['g2', 'For the filling'],
      ]);
      expect(submittedIngredients[0].ingredients!.map((line) => line.displayText)).toEqual(['2 cups flour']);
      expect(submittedInstructions.map((group) => [group.id, group.title])).toEqual([
        ['ig1', 'The day before'],
        ['ig2', 'On the day'],
      ]);
      expect(submittedInstructions[0].steps!.map((step) => step.text)).toEqual(['Chill the dough.']);
    });

    it('keeps existing steps in the first group when the creator asks for phases', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      selectEditTab(harness, 'Instructions');

      clickButton(harness, 'Add step');
      const firstGroupKey = component.instructionGroups()[0].key;
      const firstStepKey = component.instructionGroups()[0].steps[0].key;
      typeInto(harness, `#step-text-${firstStepKey}`, 'Toast the spices.');
      clickButton(harness, 'Add step');
      const secondStepKey = component.instructionGroups()[0].steps[1].key;
      typeInto(harness, `#step-text-${secondStepKey}`, 'Add the tomatoes.');

      expect(component.showInstructionGroups()).toBeFalse();

      clickButton(harness, 'Add instruction group');

      const groups = component.instructionGroups();
      expect(groups.length).toBe(2);
      expect(groups[0].key).toBe(firstGroupKey);
      expect(groups[0].steps.map((step) => step.text)).toEqual(['Toast the spices.', 'Add the tomatoes.']);
      expect(groups[1].steps).toEqual([]);
      expect(component.showInstructionGroups()).toBeTrue();

      // The headings are now there to name, empty rather than invented — and the steps are still submitted.
      await harness.fixture.whenStable();
      harness.detectChanges();
      const headings = Array.from(harness.routeNativeElement!.querySelectorAll<HTMLInputElement>('input[id^="group-title-"]'));
      expect(headings.length).toBe(2);
      expect(headings.map((input) => input.value)).toEqual(['', '']);
    });
  });

  /**
   * The nav down the side of the Instructions tab — the same `CpAnchorNavComponent` the ingredient editor
   * renders, over a list that is always rendered in full. It scrolls; it never mounts.
   */
  describe('the instruction group nav', () => {
    const navHost = (harness: RouterTestingHarness) =>
      harness.routeNativeElement!.querySelector<HTMLElement>('cp-anchor-nav.group-nav');

    const navLinks = (harness: RouterTestingHarness) =>
      Array.from(harness.routeNativeElement!.querySelectorAll<HTMLAnchorElement>('nav[aria-label="Instruction groups"] a'));

    const clickButton = (harness: RouterTestingHarness, label: string) => {
      const button = Array.from(harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('button')).find(
        (candidate) => candidate.textContent?.trim() === label,
      );
      expect(button).withContext(`the "${label}" button`).toBeTruthy();
      button!.click();
      harness.detectChanges();
    };

    const typeInto = (harness: RouterTestingHarness, selector: string, value: string) => {
      const field = harness.routeNativeElement!.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector);
      expect(field).withContext(selector).toBeTruthy();
      field!.value = value;
      field!.dispatchEvent(new Event('input'));
      harness.detectChanges();
    };

    /** Two phases, one named and one not, with different numbers of steps. */
    async function twoPhases(recipeService: ReturnType<typeof recipeServiceSpy>) {
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      selectEditTab(harness, 'Instructions');

      clickButton(harness, 'Add step');
      const firstKey = component.instructionGroups()[0].key;
      typeInto(harness, `#step-text-${component.instructionGroups()[0].steps[0].key}`, 'Toast the spices.');
      clickButton(harness, 'Add instruction group');
      typeInto(harness, `#group-title-${firstKey}`, 'The day before');

      const secondKey = component.instructionGroups()[1].key;
      const addSteps = Array.from(harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('button')).filter(
        (button) => button.textContent?.trim() === 'Add step',
      );
      addSteps[1].click();
      harness.detectChanges();

      return { harness, component, firstKey, secondKey };
    }

    it('lists every instruction group, named and counted, and is the shared nav rather than a local copy', async () => {
      const { harness } = await twoPhases(recipeServiceSpy());

      expect(navLinks(harness).map((link) => link.querySelector('.label')?.textContent?.trim())).toEqual([
        'The day before',
        'Group 2 (untitled)',
      ]);
      expect(navLinks(harness).map((link) => link.querySelector('.detail')?.textContent?.trim())).toEqual([
        '1 step',
        '1 step',
      ]);

      // One implementation for both tabs: this nav is CpAnchorNavComponent, exactly as the ingredient editor's
      // is, so neither can drift into a nav of its own.
      expect(navHost(harness)).withContext('rendered by CpAnchorNavComponent').toBeTruthy();
      expect(navLinks(harness)[0].closest('cp-anchor-nav')).toBe(navHost(harness));

      // Ordinary navigation, not a tablist: tabbable in order, and every href names a group that exists.
      expect(navLinks(harness).some((link) => link.hasAttribute('tabindex'))).toBeFalse();
      for (const link of navLinks(harness)) {
        expect(harness.routeNativeElement!.querySelector(link.getAttribute('href')!.replace('#', '#')))
          .withContext(link.getAttribute('href')!)
          .toBeTruthy();
      }
    });

    it('reaches every group: each item scrolls to its own group and focuses its heading', async () => {
      const { harness, firstKey, secondKey } = await twoPhases(recipeServiceSpy());
      const scrollSpy = spyOn(Element.prototype, 'scrollIntoView');

      for (const [index, key] of [firstKey, secondKey].entries()) {
        navLinks(harness)[index].click();
        harness.detectChanges();

        expect((scrollSpy.calls.mostRecent().object as Element).id)
          .withContext(`group ${index + 1}`)
          .toBe(`instruction-group-${key}`);
        expect(document.activeElement)
          .withContext(`group ${index + 1} heading`)
          .toBe(harness.routeNativeElement!.querySelector(`#group-title-${key}`));
        expect(navLinks(harness)[index].getAttribute('aria-current')).toBe('true');
      }

      expect(navLinks(harness).filter((link) => link.getAttribute('aria-current') === 'true').length).toBe(1);
    });

    it('counts a step edited in a group navigated away from: still lights the pill and still submits', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { harness, component, secondKey } = await twoPhases(recipeService);

      component.title.set('Chili');
      const stepKey = component.instructionGroups()[1].steps[0].key;
      typeInto(harness, `#step-text-${stepKey}`, 'Simmer for an hour.');
      typeInto(harness, `#group-title-${secondKey}`, 'On the day');

      // Jump back to the first phase: the second is scrolled away from, not unmounted.
      navLinks(harness)[0].click();
      harness.detectChanges();
      expect(harness.routeNativeElement!.querySelector(`#step-text-${stepKey}`)).withContext('still rendered').toBeTruthy();

      expect(component.isDirty()).toBeTrue();
      expect(harness.routeNativeElement!.querySelector('.dirty-indicator')?.textContent).toContain('Unsaved changes');

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      const groups = request.instructions as InstructionGroupInput[];
      expect(groups.map((group) => group.title)).toEqual(['The day before', 'On the day']);
      expect(groups[1].steps!.map((step) => step.text)).toEqual(['Simmer for an hour.']);
    });

    it('renders no nav for a method with no phases, nor for a single one', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      selectEditTab(harness, 'Instructions');

      expect(navHost(harness)).withContext('nothing written yet').toBeNull();

      clickButton(harness, 'Add step');
      expect(component.instructionGroups().length).toBe(1);
      expect(navHost(harness)).withContext('one ungrouped method').toBeNull();

      clickButton(harness, 'Add instruction group');
      expect(navLinks(harness).length).toBe(2);
    });

    it('gives the steps the full width when no nav sits beside them, and a gutter when one does', async () => {
      const trackCount = (element: Element) => {
        const value = getComputedStyle(element).gridTemplateColumns;
        return value === 'none' || value === '' ? 0 : value.split(/\s+/).length;
      };

      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      selectEditTab(harness, 'Instructions');
      clickButton(harness, 'Add step');

      const root = harness.routeNativeElement!;
      root.style.width = '80rem';
      harness.detectChanges();

      // No nav, so no gutter: the steps have the width, and a step's own fields are not squeezed into 12rem.
      const layout = () => root.querySelector('.grouped-layout')!;
      expect(navHost(harness)).toBeNull();
      expect(trackCount(layout())).withContext('columns with no nav').toBe(1);
      const groups = root.querySelector('.groups')!;
      expect(groups.getBoundingClientRect().width).toBeGreaterThan(root.getBoundingClientRect().width * 0.8);

      // A second phase brings the nav, and with it the gutter.
      clickButton(harness, 'Add instruction group');
      expect(trackCount(layout())).withContext('columns with a nav').toBe(2);
      expect(getComputedStyle(navHost(harness)!).position).toBe('sticky');
      expect(getComputedStyle(navHost(harness)!.querySelector('ul')!).flexDirection).toBe('column');

      // Narrow — a phone, or 200% zoom. One column, full-size targets, nothing spilling sideways.
      root.style.width = '24rem';
      harness.detectChanges();
      expect(trackCount(layout())).withContext('columns at 24rem').toBe(1);
      expect(getComputedStyle(navHost(harness)!).position).toBe('static');
      for (const link of navLinks(harness)) {
        expect(link.getBoundingClientRect().height)
          .withContext(link.textContent?.trim())
          .toBeGreaterThanOrEqual(40);
      }
      const limit = root.getBoundingClientRect().right + 1;
      const overflowing = Array.from(root.querySelectorAll('*'))
        .filter((node) => node.getBoundingClientRect().right > limit)
        .map((node) => node.tagName.toLowerCase() + (node.id ? '#' + node.id : ''));
      expect(overflowing).withContext('overflowing the editor at 24rem').toEqual([]);

      expect(component.instructionGroups().length).toBe(2);
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

    /**
     * One Save writes all three tabs, so the pill has to light for an edit in any of them — the property the
     * split most easily breaks, since each tab is its own panel and two of them are not even mounted at load.
     */
    it('lights the "Unsaved changes" pill for an edit made in each of the three form tabs', async () => {
      const pillText = (harness: RouterTestingHarness) =>
        harness.routeNativeElement?.querySelector('.dirty-indicator')?.textContent ?? null;

      // General — typed into the real control, in the tab that is showing at load.
      {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
        expect(pillText(harness)).withContext('General, before the edit').toBeNull();

        const title = harness.routeNativeElement!.querySelector<HTMLInputElement>('#recipe-title')!;
        title.value = 'Weeknight chili, again';
        title.dispatchEvent(new Event('input'));
        harness.detectChanges();

        expect(component.isDirty()).withContext('General').toBeTrue();
        expect(pillText(harness)).withContext('General').toContain('Unsaved changes');
      }

      // Ingredients — a row added and typed into through the child editor's own inputs.
      {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
        selectEditTab(harness, 'Ingredients');
        expect(pillText(harness)).withContext('Ingredients, before the edit').toBeNull();

        const addGroup = Array.from(harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('button')).find(
          (button) => button.textContent?.trim() === 'Add ingredient group',
        )!;
        addGroup.click();
        harness.detectChanges();
        const addRow = Array.from(harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('button')).find(
          (button) => button.textContent?.trim() === 'Add ingredient',
        )!;
        addRow.click();
        harness.detectChanges();

        const rowKey = component.editedIngredientGroups()[0].ingredients[0].key;
        const ingredient = harness.routeNativeElement!.querySelector<HTMLInputElement>(`#row-ingredient-${rowKey}`)!;
        ingredient.value = 'flaky sea salt';
        ingredient.dispatchEvent(new Event('input'));
        harness.detectChanges();

        expect(component.isDirty()).withContext('Ingredients').toBeTrue();
        expect(pillText(harness)).withContext('Ingredients').toContain('Unsaved changes');
      }

      // Instructions — a step added and typed into, in a tab that was not mounted when the editor loaded.
      {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: RECIPE_DETAIL });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
        selectEditTab(harness, 'Instructions');
        expect(pillText(harness)).withContext('Instructions, before the edit').toBeNull();

        component.addInstructionGroup();
        harness.detectChanges();
        const groupKey = component.instructionGroups()[0].key;
        component.addInstructionStep(groupKey);
        harness.detectChanges();

        const stepKey = component.instructionGroups()[0].steps[0].key;
        const step = harness.routeNativeElement!.querySelector<HTMLTextAreaElement>(`#step-text-${stepKey}`);
        expect(step).withContext('the new step textarea').toBeTruthy();
        step!.value = 'Toast the spices.';
        step!.dispatchEvent(new Event('input'));
        harness.detectChanges();

        expect(component.isDirty()).withContext('Instructions').toBeTrue();
        expect(pillText(harness)).withContext('Instructions').toContain('Unsaved changes');
      }
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

    // The form no longer carries a status at all (TESTRUN-005), so there is nothing here that could hold
    // Archived: an edit never sends one, and editorial state moves through a readiness transition. What the
    // form still reads off the recipe is whether it is archived, which the tests above assert through the
    // banner, the disabled save and the absent archive button.
    it('has no status control for an edit to send', async () => {
      const { harness } = await loaded(ARCHIVED);

      expect(harness.routeNativeElement?.querySelector('#recipe-status')).toBeNull();
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

      component.selectedAreaId.set('history');
      component.title.set('Half-written edit');
      expect(component.isDirty()).toBeTrue();

      component.onVersionRestored({ recipe: restoredRecipe, fromVersionNumber: 3, newVersionNumber: 9 });
      harness.detectChanges();

      expect(component.title()).toBe('Chilli');
      expect(component.selectedAreaId()).toBe('edit');
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

  /**
   * The apply handshake (7.12c): a calculation preview becomes a recipe diff and is written through the
   * ordinary REC-004 update route — the same token, the same idempotency behaviour, the same history. The
   * panels decide *whether* a result can be applied and prove it in their own specs; these cover what the
   * editor does once one asks.
   */
  describe('applying a calculation', () => {
    const STEP_WITH_TEMPERATURE = {
      id: 's1',
      sortOrder: 0,
      text: 'Bake until golden',
      techniqueId: 't1',
      durationMinutes: 40,
      temperatureValue: 180,
      temperatureUnitId: 'unit-celsius',
      note: 'rotate halfway',
    };

    const SIBLING_STEP = {
      id: 's2',
      sortOrder: 1,
      text: 'Rest before slicing',
      techniqueId: 't2',
      durationMinutes: 10,
      temperatureValue: 74,
      temperatureUnitId: 'unit-celsius',
      note: 'centre should read 74',
    };

    const DETAIL_WITH_STEPS: RecipeDetail = {
      ...RECIPE_DETAIL,
      yieldQuantity: 12,
      yieldUnitId: 'unit-cup',
      currentVersion: {
        id: 'v3',
        versionNumber: 3,
        source: 'CreatorEdit',
        readiness: 'Draft',
        reason: null,
        createdAt: '2026-03-01T00:00:00Z',
      },
      instructionGroups: [{ id: 'g1', title: null, sortOrder: 0, steps: [STEP_WITH_TEMPERATURE, SIBLING_STEP] }],
    };

    const TEMPERATURE_APPLICATION = {
      stepId: 's1',
      stepLabel: 'Step 1 — Bake until golden',
      temperatureValue: 356,
      temperatureUnitId: 'unit-fahrenheit',
      beforeLabel: '180 °C',
      afterLabel: '356 °F',
    };

    const YIELD_APPLICATION = { yieldQuantity: 24, beforeLabel: '12', afterLabel: '24' };

    function updatedDetail(versionNumber: number): RecipeDetail {
      return {
        ...DETAIL_WITH_STEPS,
        concurrencyToken: 'BBBBBBBBB9E=',
        currentVersion: { ...DETAIL_WITH_STEPS.currentVersion!, versionNumber },
      };
    }

    async function loadedEditor() {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: DETAIL_WITH_STEPS });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      await waitUntil(() => component.loadState().status === 'ready');
      harness.detectChanges();
      return { harness, component, recipeService };
    }

    // ---- Success ----

    it('writes one narrow patch through the ordinary update route, quoting the recipe token', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: updatedDetail(4), replayed: false });

      const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await applying;

      expect(recipeService.updateRecipe).toHaveBeenCalledTimes(1);
      const [slug, recipeId, request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(slug).toBe('cozy-fall');
      expect(recipeId).toBe('r1');
      expect(request.expectedConcurrencyToken).toBe('AAAAAAAAB9E=');
      expect(request.reason).toContain('180 °C → 356 °F');

      // Narrow: every field this patch does not name is left exactly as it is, so one confirmed change
      // writes one version holding only that change.
      expect(Object.keys(request).sort()).toEqual(['expectedConcurrencyToken', 'instructions', 'reason']);
    });

    it('moves only the named step and leaves every sibling exactly as the recipe holds it', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: updatedDetail(4), replayed: false });

      const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await applying;

      const steps = recipeService.updateRecipe.calls.mostRecent().args[2].instructions.value[0].steps;

      expect(steps[0]).toEqual(
        jasmine.objectContaining({
          id: 's1',
          temperatureValue: 356,
          temperatureUnitId: 'unit-fahrenheit',
          techniqueId: 't1',
          note: 'rotate halfway',
        }),
      );
      expect(steps[1]).toEqual(
        jasmine.objectContaining({
          id: 's2',
          temperatureValue: 74,
          temperatureUnitId: 'unit-celsius',
          techniqueId: 't2',
          note: 'centre should read 74',
        }),
      );
    });

    it('reports the new version and re-seeds the form from the response', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: updatedDetail(4), replayed: false });

      const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await applying;

      expect(component.notice()?.isProblem).toBeFalse();
      expect(component.notice()?.text).toContain('356 °F');
      expect(component.notice()?.text).toContain('version 4');
      expect(component.currentVersionNumber()).toBe(4);
      // The refreshed token is what the next write must quote.
      expect(component.concurrencyToken()).toBe('BBBBBBBBB9E=');
    });

    it('records a reconciled yield as a patch naming only the yield quantity', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: updatedDetail(4), replayed: false });

      const applying = component.onYieldApplyRequested(YIELD_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await applying;

      const request = recipeService.updateRecipe.calls.mostRecent().args[2];
      expect(Object.keys(request).sort()).toEqual(['expectedConcurrencyToken', 'reason', 'yieldQuantity']);
      expect(request.yieldQuantity).toEqual({ submitted: true, value: 24 });
    });

    // ---- Rejection ----

    it('writes nothing when the confirmation is declined', async () => {
      const { component, recipeService } = await loadedEditor();

      const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(false);
      await applying;

      expect(recipeService.updateRecipe).not.toHaveBeenCalled();
      expect(component.notice()).toBeNull();
      expect(component.currentVersionNumber()).toBe(3);
    });

    // The confirmation names the change and its consequence, so a creator is never asked a bare "are you sure".
    it('names the change and says a version will be written when it asks', async () => {
      const { component } = await loadedEditor();

      const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);

      const question = confirmService.confirm.calls.mostRecent().args[0];
      expect(question.message).toContain('180 °C');
      expect(question.message).toContain('356 °F');
      expect(question.message).toContain('new version');
      expect(question.confirmLabel).not.toBe('OK');

      confirmService.answer(false);
      await applying;
    });

    // ---- Conflict ----

    it('reports a conflict as nothing written, and does not retry on its own', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'conflict' });

      const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await applying;

      expect(recipeService.updateRecipe).toHaveBeenCalledTimes(1);
      expect(component.notice()?.isProblem).toBeTrue();
      expect(component.notice()?.text).toContain('Someone else changed this recipe');
      expect(component.notice()?.text).toContain('nothing was applied');
      // The recipe is untouched: still the version and token it was loaded with.
      expect(component.currentVersionNumber()).toBe(3);
      expect(component.concurrencyToken()).toBe('AAAAAAAAB9E=');
    });

    it('states that nothing was written for every other refusal', async () => {
      for (const status of ['forbidden', 'not_found', 'validation_failed', 'unavailable'] as const) {
        const { component, recipeService } = await loadedEditor();
        recipeService.updateRecipe.and.resolveTo(
          status === 'validation_failed' ? { status, fieldErrors: {} } : { status },
        );

        const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
        await waitUntil(() => confirmService.isOpen);
        confirmService.answer(true);
        await applying;

        expect(component.notice()?.isProblem).toBeTrue();
        expect(component.notice()?.text).toContain('nothing was');
        expect(component.currentVersionNumber()).toBe(3);
      }
    });

    // ---- Replay ----

    // A lost response is the case this exists for: the client retries, the server recognises the key, and
    // the one change stays one version rather than becoming two.
    it('reuses one idempotency key across a retry of the identical apply', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'unavailable' });

      const first = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await first;

      const second = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await second;

      const [firstKey, secondKey] = recipeService.updateRecipe.calls.all().map((call) => call.args[3]);
      expect(firstKey).toBeTruthy();
      expect(secondKey).toBe(firstKey);
    });

    it('treats a replayed response as the one success it is, claiming no second version', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: updatedDetail(4), replayed: true });

      const applying = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await applying;

      expect(component.notice()?.isProblem).toBeFalse();
      expect(component.notice()?.text).toContain('version 4');
      expect(component.currentVersionNumber()).toBe(4);
    });

    it('starts a fresh key once the apply has landed, so the next one is its own operation', async () => {
      const { component, recipeService } = await loadedEditor();
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: updatedDetail(4), replayed: false });

      const first = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await first;

      const second = component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);
      await waitUntil(() => confirmService.isOpen);
      confirmService.answer(true);
      await second;

      const [firstKey, secondKey] = recipeService.updateRecipe.calls.all().map((call) => call.args[3]);
      expect(secondKey).not.toBe(firstKey);
    });

    // ---- Ineligibility ----

    // A narrow patch would write the calculated field and leave the creator's unsaved edits in a form that
    // then contradicts the recipe. The panels disable the control; this is the gate behind it.
    it('refuses to apply while the editor has unsaved changes, without even asking', async () => {
      const { component, recipeService, harness } = await loadedEditor();
      component.title.set('Chili, reworked');
      harness.detectChanges();
      expect(component.isDirty()).toBeTrue();

      await component.onTemperatureApplyRequested(TEMPERATURE_APPLICATION);

      expect(confirmService.confirm).not.toHaveBeenCalled();
      expect(recipeService.updateRecipe).not.toHaveBeenCalled();
    });

    /**
     * A converted ingredient amount, recorded on the line it came from.
     *
     * The third apply path, and it goes through the same handshake as the other two — same confirmation, same
     * token, same idempotency key, same one narrow patch. What is particular to it is the wording: the target
     * line is re-derived from its own fields, and only ever because it was already a line the recipe assembled.
     */
    describe('a converted ingredient amount', () => {
      const COMPOSED_LINE: RecipeIngredient = {
        id: 'i1',
        sortOrder: 0,
        displayText: '2 cups flour',
        displayTextSource: 'Composed',
        ingredientNameText: 'flour',
        unitText: 'cups',
        quantity: 2,
        quantityUpper: null,
        measurementUnitId: 'unit-cup',
        ingredientId: 'ref-flour',
        matchStatus: 'Matched',
        preparationNote: 'sifted',
        isOptional: false,
        scalingBehavior: 'Proportional',
      };

      /** The creator's own wording, which nothing may rewrite — here to prove it comes back untouched. */
      const CREATOR_LINE: RecipeIngredient = {
        ...COMPOSED_LINE,
        id: 'i2',
        sortOrder: 1,
        displayText: 'a good pinch of flaky salt',
        displayTextSource: 'Creator',
        ingredientNameText: 'flaky salt',
        unitText: 'pinch',
        quantity: 1,
        measurementUnitId: 'unit-pinch',
        ingredientId: null,
        matchStatus: 'NoMatch',
        preparationNote: null,
      };

      const DETAIL_WITH_LINES: RecipeDetail = {
        ...DETAIL_WITH_STEPS,
        ingredientGroups: [{ id: 'ig1', title: null, sortOrder: 0, ingredients: [COMPOSED_LINE, CREATOR_LINE] }],
      };

      const CONVERSION_APPLICATION = {
        recipeIngredientId: 'i1',
        quantity: 473.18,
        measurementUnitId: 'unit-milliliter',
        unitText: 'milliliter',
        lineLabel: '2 cups flour',
        beforeLabel: '2 cup',
        afterLabel: '473.18 ml',
      };

      async function editorWithLines() {
        const recipeService = recipeServiceSpy();
        recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: DETAIL_WITH_LINES });
        const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
        await waitUntil(() => component.loadState().status === 'ready');
        harness.detectChanges();
        return { harness, component, recipeService };
      }

      it('asks first, then writes one narrow patch quoting the recipe token', async () => {
        const { component, recipeService } = await editorWithLines();
        recipeService.updateRecipe.and.resolveTo({
          status: 'updated',
          recipe: { ...DETAIL_WITH_LINES, concurrencyToken: 'BBBBBBBBB9E=' },
          replayed: false,
        });

        const applying = component.onUnitConversionApplyRequested(CONVERSION_APPLICATION);
        await waitUntil(() => confirmService.isOpen);

        // Nothing has been written while the question is on screen.
        expect(recipeService.updateRecipe).not.toHaveBeenCalled();
        confirmService.answer(true);
        await applying;

        expect(recipeService.updateRecipe).toHaveBeenCalledTimes(1);
        const [slug, recipeId, request, key] = recipeService.updateRecipe.calls.mostRecent().args;
        expect(slug).toBe('cozy-fall');
        expect(recipeId).toBe('r1');
        expect(request.expectedConcurrencyToken).toBe('AAAAAAAAB9E=');
        expect(key).toBeTruthy();

        // One field group, and the reason that will appear in the version history.
        expect(Object.keys(request).sort()).toEqual(['expectedConcurrencyToken', 'ingredientGroups', 'reason']);
        expect(request.reason).toContain('2 cup');
      });

      it('moves the named line and re-derives its wording from its own fields', async () => {
        const { component, recipeService } = await editorWithLines();
        recipeService.updateRecipe.and.resolveTo({
          status: 'updated',
          recipe: { ...DETAIL_WITH_LINES, concurrencyToken: 'BBBBBBBBB9E=' },
          replayed: false,
        });

        const applying = component.onUnitConversionApplyRequested(CONVERSION_APPLICATION);
        await waitUntil(() => confirmService.isOpen);
        confirmService.answer(true);
        await applying;

        const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
        const [target] = request.ingredientGroups.value![0].ingredients!;

        expect(target.quantity).toBe(473.18);
        expect(target.measurementUnitId).toBe('unit-milliliter');
        expect(target.unitText).toBe('milliliter');
        // Assembled from the line's own fields, in the order the line reads — not invented and not the old text.
        expect(target.displayText).toBe('473.18 milliliter flour, sifted');
        expect(target.displayTextSource).toBe('Composed');
        // Everything the conversion has no business touching comes back as the recipe holds it.
        expect(target.ingredientId).toBe('ref-flour');
        expect(target.preparationNote).toBe('sifted');
        expect(target.scalingBehavior).toBe('Proportional');
      });

      /**
       * `IngredientGroups` is a full replace and the server applies a submitted line wholesale, so a sibling
       * left out — or submitted differently — is a sibling quietly rewritten.
       */
      it('submits every other line exactly as the recipe holds it, wording included', async () => {
        const { component, recipeService } = await editorWithLines();
        recipeService.updateRecipe.and.resolveTo({
          status: 'updated',
          recipe: { ...DETAIL_WITH_LINES, concurrencyToken: 'BBBBBBBBB9E=' },
          replayed: false,
        });

        const applying = component.onUnitConversionApplyRequested(CONVERSION_APPLICATION);
        await waitUntil(() => confirmService.isOpen);
        confirmService.answer(true);
        await applying;

        const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
        const sibling = request.ingredientGroups.value![0].ingredients![1];

        expect(sibling).toEqual({
          id: 'i2',
          displayText: 'a good pinch of flaky salt',
          displayTextSource: 'Creator',
          ingredientNameText: 'flaky salt',
          unitText: 'pinch',
          quantity: 1,
          quantityUpper: null,
          measurementUnitId: 'unit-pinch',
          ingredientId: null,
          preparationNote: null,
          isOptional: false,
          scalingBehavior: 'Proportional',
        });
      });

      // The restriction, in one test: no calculation writes to the recipe without an explicit confirmation.
      it('a declined confirmation writes nothing at all', async () => {
        const { component, recipeService } = await editorWithLines();

        const applying = component.onUnitConversionApplyRequested(CONVERSION_APPLICATION);
        await waitUntil(() => confirmService.isOpen);
        confirmService.answer(false);
        await applying;

        expect(recipeService.updateRecipe).not.toHaveBeenCalled();
        expect(component.notice()).toBeNull();
        // Still the version and token it loaded with.
        expect(component.currentVersionNumber()).toBe(3);
        expect(component.concurrencyToken()).toBe('AAAAAAAAB9E=');
      });

      it('reuses one idempotency key across a retry of the identical apply', async () => {
        const { component, recipeService } = await editorWithLines();
        recipeService.updateRecipe.and.resolveTo({ status: 'unavailable' });

        const first = component.onUnitConversionApplyRequested(CONVERSION_APPLICATION);
        await waitUntil(() => confirmService.isOpen);
        confirmService.answer(true);
        await first;

        const second = component.onUnitConversionApplyRequested(CONVERSION_APPLICATION);
        await waitUntil(() => confirmService.isOpen);
        confirmService.answer(true);
        await second;

        const [firstKey, secondKey] = recipeService.updateRecipe.calls.all().map((call) => call.args[3]);
        expect(firstKey).toBeTruthy();
        expect(secondKey).toBe(firstKey);
      });

      // The editorIsDirty contract, enforced a second time here: the panel refuses, and so does this.
      it('refuses outright while the form holds unsaved edits, without even asking', async () => {
        const { component, recipeService } = await editorWithLines();
        component.title.set('Something else');

        await component.onUnitConversionApplyRequested(CONVERSION_APPLICATION);

        expect(confirmService.confirm).not.toHaveBeenCalled();
        expect(recipeService.updateRecipe).not.toHaveBeenCalled();
      });
    });
  });

  /**
   * `Instructions` is a full replace and the server applies a submitted step wholesale, so a field this form
   * omits is a field the next save sets to null. These guard the fields no control here edits.
   */
  describe('preserving instruction fields no control edits', () => {
    const DETAIL_WITH_RICH_STEP: RecipeDetail = {
      ...RECIPE_DETAIL,
      instructionGroups: [
        {
          id: 'g1',
          title: null,
          sortOrder: 0,
          steps: [
            {
              id: 's1',
              sortOrder: 0,
              text: 'Bake until golden',
              techniqueId: 't1',
              durationMinutes: 40,
              temperatureValue: 180,
              temperatureUnitId: 'unit-celsius',
              note: null,
            },
          ],
        },
      ],
    };

    it('carries technique and temperature through an ordinary save of an unrelated field', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: DETAIL_WITH_RICH_STEP });
      recipeService.updateRecipe.and.resolveTo({ status: 'unavailable' });

      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);
      await waitUntil(() => component.loadState().status === 'ready');

      component.title.set('Chili, reworked');
      harness.detectChanges();
      await component.save();

      const step = recipeService.updateRecipe.calls.mostRecent().args[2].instructions.value[0].steps[0];
      expect(step.techniqueId).toBe('t1');
      expect(step.temperatureValue).toBe(180);
      expect(step.temperatureUnitId).toBe('unit-celsius');
    });
  });

  /**
   * The yield unit, as a picker over the shared platform catalogue.
   *
   * `yieldUnitId` and `yieldText` are two different facts and both exist deliberately (recipes.md): one is the
   * creator's own sentence about how much a recipe makes, the other is the vocabulary entry a scaling or pan
   * calculation reads. Most of what follows is about keeping them apart, and about the picker never costing
   * the recipe a value it had already recorded.
   */
  describe('the yield unit picker', () => {
    const GATEWAY = 'https://gateway.example';
    const UNITS_URL = `${GATEWAY}/api/v1/reference/units`;

    function unitJson(id: string, displayName: string, abbreviation: string, dimension: string): Record<string, unknown> {
      return {
        id,
        code: displayName.toLowerCase(),
        displayName,
        pluralName: displayName,
        abbreviation,
        dimension,
        system: 'Neutral',
        baseUnitFactor: 1,
        displayPrecision: 2,
      };
    }

    const SERVINGS = unitJson('u-servings', 'Servings', 'srv', 'Count');
    const GRAMS = unitJson('u-grams', 'Grams', 'g', 'Mass');
    const CELSIUS = unitJson('u-celsius', 'Celsius', 'degC', 'Temperature');

    /** The recipe an edit-mode case loads: a saved yield of four servings. */
    const WITH_YIELD_UNIT: RecipeDetail = { ...RECIPE_DETAIL, yieldQuantity: 4, yieldUnitId: 'u-servings' };

    /** Answers the catalogue read the editor starts as it is constructed. */
    async function settleUnits(
      harness: RouterTestingHarness,
      component: RecipeEditorComponent,
      units: readonly Record<string, unknown>[] = [SERVINGS, GRAMS, CELSIUS],
    ): Promise<void> {
      TestBed.inject(HttpTestingController)
        .expectOne((request) => request.url === UNITS_URL)
        .flush({ items: units, nextCursor: null });
      await waitUntil(() => component.unitCatalogue().status !== 'loading');
      harness.detectChanges();
    }

    async function failUnits(harness: RouterTestingHarness, component: RecipeEditorComponent): Promise<void> {
      TestBed.inject(HttpTestingController)
        .expectOne((request) => request.url === UNITS_URL)
        .flush({}, { status: 500, statusText: 'Server Error' });
      await waitUntil(() => component.unitCatalogue().status === 'unavailable');
      harness.detectChanges();
    }

    function unitBox(harness: RouterTestingHarness): HTMLInputElement {
      const input = harness.routeNativeElement!.querySelector<HTMLInputElement>('input[id="recipe-yield-unit"]');
      expect(input).withContext('the yield unit box').toBeTruthy();
      return input!;
    }

    /** `mousedown`, as the combobox listens for — a click would blur the box before it landed. */
    function pickUnit(harness: RouterTestingHarness, unitId: string): void {
      const option = harness.routeNativeElement!.querySelector(`[id="recipe-yield-unit-option-${unitId}"]`);
      expect(option).withContext(`the "${unitId}" option`).toBeTruthy();
      option!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
      harness.detectChanges();
    }

    function clickLabelled(harness: RouterTestingHarness, ariaLabel: string): void {
      const button = harness.routeNativeElement!.querySelector<HTMLButtonElement>(`button[aria-label="${ariaLabel}"]`);
      expect(button).withContext(`the "${ariaLabel}" button`).toBeTruthy();
      button!.click();
      harness.detectChanges();
    }

    it('records the unit a creator picks and leaves their own Yield wording alone', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      component.title.set('Weeknight Chili');
      component.yieldText.set('About 4 generous bowls');
      component.yieldQuantity.set(4);
      harness.detectChanges();

      pickUnit(harness, 'u-servings');

      expect(component.yieldUnitId()).toBe('u-servings');
      expect(unitBox(harness).value).toBe('Servings');
      // The sentence the creator wrote is a different fact, and picking a unit is not an edit to it.
      expect(component.yieldText()).toBe('About 4 generous bowls');

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(request.yieldUnitId).toBe('u-servings');
      expect(request.yieldText).toBe('About 4 generous bowls');
      expect(request.yieldQuantity).toBe(4);
    });

    it('shows a loaded recipe’s unit by name, and counts a change to it as unsaved', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_YIELD_UNIT });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      // The id arrives with the recipe and the name only with the catalogue, so this is what the constructor's
      // effect is for.
      expect(unitBox(harness).value).toBe('Servings');
      expect(component.isDirty()).withContext('loading a recipe is not editing it').toBeFalse();

      pickUnit(harness, 'u-grams');

      expect(component.yieldUnitId()).toBe('u-grams');
      expect(component.isDirty()).toBeTrue();
    });

    it('clears a recorded unit, and submits the clearance rather than omitting it', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_YIELD_UNIT });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...WITH_YIELD_UNIT, yieldUnitId: null, concurrencyToken: 'AAAAAAAAB9I=' } });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      // Restricted text reverts to the chosen unit's name when the field is left, so this button is the only
      // way back to "no unit" — hence a control of its own rather than an empty box.
      clickLabelled(harness, 'Clear the yield unit');

      expect(component.yieldUnitId()).toBeNull();
      expect(unitBox(harness).value).toBe('');
      expect(component.isDirty()).toBeTrue();

      await component.save();

      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.yieldUnitId).toEqual({ submitted: true, value: null });
      // The creator's own wording was never part of this.
      expect(request.yieldText).toEqual({ submitted: true, value: null });
    });

    it('offers no Clear until there is a unit to clear', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      expect(harness.routeNativeElement!.querySelector('button[aria-label="Clear the yield unit"]')).toBeNull();
      pickUnit(harness, 'u-servings');
      expect(harness.routeNativeElement!.querySelector('button[aria-label="Clear the yield unit"]')).toBeTruthy();
    });

    // Business refuses a temperature yield unit outright, so offering one would be offering a choice that can
    // only ever come back as an error.
    it('does not offer a unit the server would refuse', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      expect(component.yieldUnitOptions().map((option) => option.id)).toEqual(['u-grams', 'u-servings']);
      expect(harness.routeNativeElement!.querySelector('[id="recipe-yield-unit-option-u-celsius"]')).toBeNull();
    });

    /**
     * The failure this guards against: an unreadable catalogue quietly costing the recipe a unit it had
     * already recorded. The form holds an id, not a name, so there is nothing for a missing list to take.
     */
    it('keeps a recorded unit when the unit list cannot be read, and offers a retry', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_YIELD_UNIT });
      recipeService.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...WITH_YIELD_UNIT, concurrencyToken: 'AAAAAAAAB9I=' } });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService, undefined, 'Editor', GATEWAY);
      await failUnits(harness, component);

      // Nothing to pick from, so the box is not offered as if there were.
      expect(unitBox(harness).disabled).toBeTrue();
      expect(component.yieldUnitHint()).toContain('cannot be named');
      expect(component.isDirty()).withContext('a failed read is not an edit').toBeFalse();

      await component.save();
      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.yieldUnitId).toEqual({ submitted: true, value: 'u-servings' });

      // And the outage is not inherited for the rest of the session.
      clickLabelled(harness, 'Try reading the unit list again');
      await settleUnits(harness, component);

      expect(unitBox(harness).disabled).toBeFalse();
      expect(unitBox(harness).value).toBe('Servings');
    });

    it('lets a creator take a unit off even while the list is unreadable', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_YIELD_UNIT });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService, undefined, 'Editor', GATEWAY);
      await failUnits(harness, component);

      clickLabelled(harness, 'Clear the yield unit');

      expect(component.yieldUnitId()).toBeNull();
      expect(component.yieldUnitHint()).toContain('nothing to pick from');
    });

    // The saved value is the recipe's; the form field is the form's. The reconciliation panel reads the saved
    // one because a calculation is computed against the saved version.
    it('leaves the saved yield unit where it is when the form field moves', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: WITH_YIELD_UNIT });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      expect(component.savedYieldUnitId()).toBe('u-servings');

      clickLabelled(harness, 'Clear the yield unit');

      expect(component.yieldUnitId()).toBeNull();
      expect(component.savedYieldUnitId()).withContext('the saved unit is the recipe, not the form').toBe('u-servings');
    });

    // The server requires both halves of the pair. Said on the field rather than discovered by a refused save.
    it('says a unit needs a quantity beside it', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      expect(component.yieldUnitHint()).toContain('Measures both the batch yield and one serving');

      pickUnit(harness, 'u-servings');
      expect(component.yieldUnitHint()).toContain('Add a yield quantity as well');

      component.yieldQuantity.set(4);
      harness.detectChanges();
      expect(component.yieldUnitHint()).not.toContain('Add a yield quantity');
    });

    /**
     * The serving size shares this unit, so its hint belongs to this suite: the ready-state messages need a
     * settled catalogue, which is what settleUnits provides.
     */
    it('says a serving size needs a unit, before a save can refuse it', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      expect(component.servingSizeHint()).toContain('Needs a yield unit');

      // Entered without one, which the server refuses (CK_Recipes_ServingSize_RequiresYieldUnit). Said here
      // rather than learned from a refused save.
      component.servingSize.set(250);
      harness.detectChanges();
      expect(component.servingSizeHint()).toContain('Pick a yield unit as well');

      // And once there is a unit, the hint names it — a bare 250 is otherwise ambiguous between the units one
      // field away.
      component.yieldQuantity.set(2000);
      pickUnit(harness, 'u-grams');
      harness.detectChanges();
      expect(component.servingSizeHint()).toContain('grams');
    });

    it('shows a server error on the yield unit beside the control', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'validation_failed',
        fieldErrors: { yieldUnitId: ['A yield cannot be measured in degrees.'] },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService, undefined, 'Editor', GATEWAY);
      await settleUnits(harness, component);

      component.title.set('Weeknight Chili');
      await component.save();
      harness.detectChanges();

      expect(component.fieldError('yieldUnitId')).toBe('A yield cannot be measured in degrees.');
      const field = unitBox(harness).closest('cp-field');
      expect(field?.querySelector('.error')?.textContent?.trim()).toBe('A yield cannot be measured in degrees.');
    });
  });

  /**
   * The servings half of the yield.
   *
   * The gap it closes: before this the only structured amount a recipe could record was a measured batch, so
   * a creator writing "serves 12" had nowhere to put the 12 except their own prose — and scaling, which reads
   * the structured values, could not see it. `servingCount` is that number and is unitless on purpose;
   * `servingSize` is how much one serving is and shares the recipe's yield unit, because that is the only
   * arrangement in which batch, count and size describe the same thing.
   */
  describe('servings', () => {
    function numberInput(harness: RouterTestingHarness, id: string): HTMLInputElement {
      const input = harness.routeNativeElement!.querySelector<HTMLInputElement>(`input[id="${id}"]`);
      if (input === null) throw new Error(`No input with id ${id}.`);
      return input;
    }

    it('offers a serving count that needs no unit, and submits it', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'created',
        recipe: { recipeId: 'new-id', title: 'Weeknight Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      // The field exists at all, which is the whole of the complaint: a number of servings had nowhere to go.
      expect(numberInput(harness, 'recipe-serving-count')).toBeTruthy();

      component.title.set('Weeknight Chili');
      component.servingCount.set(12);
      harness.detectChanges();

      await component.save();

      const [, request] = recipeService.createRecipe.calls.mostRecent().args;
      expect(request.servingCount).toBe(12);
      // Unitless, deliberately: nothing about a serving count asks for a yield unit, and a recipe that says
      // only "serves 12" must be savable.
      expect(request.yieldUnitId).toBeNull();
      expect(request.yieldQuantity).toBeNull();
      expect(request.servingSize).toBeNull();
    });

    it('keeps the creator’s own yield wording untouched while the numbers are filled in', async () => {
      const recipeService = recipeServiceSpy();
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.yieldText.set('Makes 2 loaves, serves 12');
      component.servingCount.set(12);
      component.yieldQuantity.set(2);
      harness.detectChanges();

      expect(component.yieldText()).toBe('Makes 2 loaves, serves 12');
      expect(component.servingCount()).toBe(12);
    });

    it('a loaded recipe’s servings reach the form, and loading is not an edit', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.getRecipeDetail.and.resolveTo({
        status: 'found',
        recipe: { ...RECIPE_DETAIL, yieldQuantity: 2000, yieldUnitId: 'u-ml', servingCount: 8, servingSize: 250 },
      });
      const { component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      expect(component.servingCount()).toBe(8);
      expect(component.servingSize()).toBe(250);
      expect(component.isDirty()).toBeFalse();
    });

    it('a serving count edit is a change, and reaches the patch', async () => {
      const recipeService = recipeServiceSpy();
      const loaded: RecipeDetail = { ...RECIPE_DETAIL, servingCount: 4 };
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: loaded });
      recipeService.updateRecipe.and.resolveTo({
        status: 'updated',
        recipe: { ...loaded, servingCount: 6, concurrencyToken: 'AAAAAAAAB9I=' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.servingCount.set(6);
      harness.detectChanges();
      expect(component.isDirty()).toBeTrue();

      await component.save();

      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.servingCount).toEqual({ submitted: true, value: 6 });
      expect(component.isDirty()).withContext('saved, so no longer dirty').toBeFalse();
    });

    it('a serving size edit is a change, and reaches the patch', async () => {
      const recipeService = recipeServiceSpy();
      const loaded: RecipeDetail = { ...RECIPE_DETAIL, yieldQuantity: 4, yieldUnitId: 'u-cup', servingSize: 1 };
      recipeService.getRecipeDetail.and.resolveTo({ status: 'found', recipe: loaded });
      recipeService.updateRecipe.and.resolveTo({
        status: 'updated',
        recipe: { ...loaded, servingSize: 2, concurrencyToken: 'AAAAAAAAB9I=' },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/r1', recipeService);

      component.servingSize.set(2);
      harness.detectChanges();
      expect(component.isDirty()).toBeTrue();

      await component.save();

      const [, , request] = recipeService.updateRecipe.calls.mostRecent().args;
      expect(request.servingSize).toEqual({ submitted: true, value: 2 });
    });

    /**
     * The server refuses a serving size with no unit to read it in
     * (`CK_Recipes_ServingSize_RequiresYieldUnit`). Said on the field, so it is not learned from a refused
     * save — the same choice the yield unit's own hint makes about the quantity it needs.
     */
    // The degraded state its neighbour already had: with the catalogue unreadable the unit box is disabled, so
    // telling a creator to pick a unit would be telling them to do what the page has made impossible.
    it('says why a serving size cannot be measured when the unit list cannot be read', async () => {
      const recipeService = recipeServiceSpy();
      const { component } = await createHarness('/cozy-fall/recipes/new', recipeService);
      await waitUntil(() => component.unitCatalogue().status !== 'loading');

      expect(component.servingSizeHint()).toContain('The unit list cannot be read');
    });

    it('routes a server error on a serving field to that field', async () => {
      const recipeService = recipeServiceSpy();
      recipeService.createRecipe.and.resolveTo({
        status: 'validation_failed',
        fieldErrors: { servingCount: ['A serving count must be greater than zero.'] },
      });
      const { harness, component } = await createHarness('/cozy-fall/recipes/new', recipeService);

      component.title.set('Weeknight Chili');
      await component.save();
      harness.detectChanges();

      expect(component.fieldError('servingCount')).toBe('A serving count must be greater than zero.');
      const field = numberInput(harness, 'recipe-serving-count').closest('cp-field');
      expect(field?.querySelector('.error')?.textContent?.trim()).toBe('A serving count must be greater than zero.');
    });
  });
});
