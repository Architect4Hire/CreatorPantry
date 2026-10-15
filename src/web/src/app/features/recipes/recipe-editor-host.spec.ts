import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { ConfirmService } from '../../core/confirm.service';
import { RecipeDetail } from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeEditorFocus, RecipeEditorHost } from './recipe-editor-host';
import { RecipeEditorComponent } from './recipe-editor.component';

const DETAIL: RecipeDetail = {
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

/** A page that is not the editor's own: it provides the host and renders the editor inside it. */
@Component({
  selector: 'test-host-page',
  standalone: true,
  imports: [RecipeEditorComponent],
  providers: [{ provide: RecipeEditorHost, useFactory: () => Object.assign(new RecipeEditorHost(), nextHost) }],
  template: `<cp-recipe-editor />`,
})
class HostPageComponent {}

/** What the next page's host carries, set before the page is made: the editor reads its host as it is constructed. */
let nextHost: Partial<RecipeEditorHost> = {};

function recipeServiceSpy() {
  return {
    getRecipeDetail: jasmine.createSpy('getRecipeDetail').and.resolveTo({ status: 'found', recipe: DETAIL }),
    createRecipe: jasmine.createSpy('createRecipe').and.resolveTo({ status: 'unavailable' }),
    updateRecipe: jasmine.createSpy('updateRecipe').and.resolveTo({ status: 'unavailable' }),
  };
}

interface Opened {
  readonly fixture: ComponentFixture<HostPageComponent>;
  readonly editor: RecipeEditorComponent;
  readonly root: HTMLElement;
  readonly recipes: ReturnType<typeof recipeServiceSpy>;
  readonly created: jasmine.Spy<(recipeId: string) => void>;
  readonly navigate: jasmine.Spy;
}

async function open(focus: RecipeEditorFocus, recipeId: string | null, recipes = recipeServiceSpy()): Promise<Opened> {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [HostPageComponent],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: RecipeService, useValue: recipes },
      { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(true) } },
      {
        provide: WorkspaceMembershipService,
        useValue: { state: signal({ status: 'loading' }), ensureLoaded: () => Promise.resolve() },
      },
    ],
  }).compileComponents();

  const created = jasmine.createSpy<(recipeId: string) => void>('created');
  const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

  nextHost = { workspaceSlug: 'cozy-fall', recipeId, focus, created };
  const fixture = TestBed.createComponent(HostPageComponent);

  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();

  const editor = fixture.debugElement.children[0].componentInstance as RecipeEditorComponent;
  return { fixture, editor, root: fixture.nativeElement as HTMLElement, recipes, created, navigate };
}

describe('RecipeEditorComponent, hosted (RecipeEditorHost)', () => {
  it('shows only the sections its focus names, and none of its own page around them', async () => {
    const { root } = await open('details', null);

    expect(root.querySelector('#section-details')).not.toBeNull();
    expect(root.querySelector('#section-ingredients')).toBeNull();
    expect(root.querySelector('#section-instructions')).toBeNull();
    expect(root.querySelector('cp-page-head')).toBeNull();
    expect(root.querySelector('cp-tabs')).toBeNull();
    expect(root.querySelector('#recipe-tools-control')).toBeNull();
    expect(root.textContent).toContain('are required');
  });

  it('shows timing and yield together, with no legend about required fields it does not have', async () => {
    const { root } = await open('yield-and-time', 'r1');

    expect(root.querySelector('#section-timing')).not.toBeNull();
    expect(root.querySelector('#section-yield')).not.toBeNull();
    expect(root.querySelector('#section-details')).toBeNull();
    expect(root.textContent).not.toContain('are required');
  });

  it('reads the recipe the host names rather than one from the address', async () => {
    const { recipes, root } = await open('ingredients', 'r1');

    expect(recipes.getRecipeDetail).toHaveBeenCalledOnceWith('cozy-fall', 'r1');
    expect(root.querySelector('#section-ingredients')).not.toBeNull();
    expect(root.querySelector('cp-page-head')).toBeNull();
  });

  it('flushes nothing, and answers yes at once, when there is nothing to keep', async () => {
    const { editor, recipes } = await open('details', null);

    await expectAsync(editor.flush()).toBeResolvedTo(true);
    expect(recipes.createRecipe).not.toHaveBeenCalled();
  });

  it('creates on flush and hands the new recipe to the host instead of navigating to it', async () => {
    const recipes = recipeServiceSpy();
    recipes.createRecipe.and.resolveTo({
      status: 'created',
      recipe: { recipeId: 'r9', title: 'Soup', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-10-15T09:00:00Z' },
      replayed: false,
    });
    const { editor, created, navigate } = await open('details', null, recipes);

    editor.title.set('Soup');
    await expectAsync(editor.flush()).toBeResolvedTo(true);

    expect(recipes.createRecipe).toHaveBeenCalledTimes(1);
    expect(created).toHaveBeenCalledOnceWith('r9');
    expect(navigate).not.toHaveBeenCalled();
    expect(editor.isDirty()).toBeFalse();
    expect(editor.savedAt()).not.toBeNull();
  });

  it('refuses to flush a recipe with no name, and says so on the field', async () => {
    const { editor, recipes, fixture, root } = await open('details', null);

    editor.description.set('A weeknight soup.');
    await expectAsync(editor.flush()).toBeResolvedTo(false);
    fixture.detectChanges();

    expect(recipes.createRecipe).not.toHaveBeenCalled();
    expect(root.textContent).toContain('Give the recipe a name, so it can be kept.');
  });

  it('saves the whole recipe from one part of it, and says when it was kept', async () => {
    const recipes = recipeServiceSpy();
    recipes.updateRecipe.and.resolveTo({ status: 'updated', recipe: { ...DETAIL, notes: 'Better the next day.' }, replayed: false });
    const { editor } = await open('ingredients', 'r1', recipes);

    editor.notes.set('Better the next day.');
    await expectAsync(editor.flush()).toBeResolvedTo(true);

    const [, , request] = recipes.updateRecipe.calls.mostRecent().args;
    expect(request.expectedConcurrencyToken).toBe(DETAIL.concurrencyToken);
    // A field this part does not show still submits, so a save from here never drops it.
    expect(request.notes).toEqual({ submitted: true, value: 'Better the next day.' });
    expect(editor.savedAt()).not.toBeNull();
  });

  it('names a section it is not showing that a save was refused over, with the way to it', async () => {
    const recipes = recipeServiceSpy();
    recipes.updateRecipe.and.resolveTo({ status: 'validation_failed', fieldErrors: { title: ['Too long.'] } });
    const { editor, fixture, root } = await open('ingredients', 'r1', recipes);

    editor.notes.set('x');
    await expectAsync(editor.flush()).toBeResolvedTo(false);
    fixture.detectChanges();

    expect(root.textContent).toContain('Details needs fixing on the recipe’s own page.');
    const link = Array.from(root.querySelectorAll('a')).find((anchor) => anchor.textContent?.includes('Open the recipe'));
    expect(link?.getAttribute('href')).toBe('/cozy-fall/recipes/r1');
  });
});
