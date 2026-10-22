import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ConfirmService } from '../../../core/confirm.service';
import { WorkspaceRole } from '../../../models/auth.models';
import { RecipeReadiness, RecipeReadinessFinding } from '../../../models/recipe-readiness.models';
import { RecipeDetail, RecipeStatus } from '../../../models/recipe.models';
import { WorkflowRun, decodeWorkflowRun } from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { RecipeReadinessService } from '../../../services/recipe-readiness.service';
import { RecipeService } from '../../../services/recipe.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeReadinessComponent } from '../../recipes/recipe-readiness.component';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { RecipeTestKitchenComponent } from '../../recipes/recipe-test-kitchen.component';
import { RecipeTestBasicsStepComponent } from './recipe-test-basics-step.component';
import { RecipeTestCookStepComponent } from './recipe-test-cook-step.component';
import { RecipeTestFixStepComponent } from './recipe-test-fix-step.component';
import { RecipeTestReadinessStepComponent } from './recipe-test-readiness-step.component';

const SLUG = 'sams-kitchen';

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

function finding(ruleId: string, status: RecipeReadinessFinding['status']): RecipeReadinessFinding {
  return { ruleId, status, summary: ruleId, detail: null, evidence: [], severityOverridden: false };
}

function readiness(findings: readonly RecipeReadinessFinding[] = []): RecipeReadiness {
  return {
    recipeId: 'recipe-1',
    evaluatedVersionId: 'v3',
    evaluatedVersionNumber: 3,
    concurrencyToken: 'AAAAAAAAB9E=',
    ruleSetVersion: '1',
    hasBlockers: findings.some((item) => item.status === 'Blocker'),
    blockerCount: findings.filter((item) => item.status === 'Blocker').length,
    recommendationCount: 0,
    findings,
    disabledRuleIds: [],
    unknownConfiguredRuleIds: [],
  };
}

const ALL_BASICS = ['recipe.ingredients.present', 'recipe.instructions.present', 'recipe.yield.stated', 'recipe.time.stated'];

function basics(missing: readonly string[]): RecipeReadiness {
  return readiness(ALL_BASICS.map((ruleId) => finding(ruleId, missing.includes(ruleId) ? 'Blocker' : 'Satisfied')));
}

function testRun(): WorkflowRun {
  return decodeWorkflowRun(wireRun({ definitionKey: 'recipe.test-and-approve' }))!;
}

let port: WorkflowStepPort;
let role: WorkspaceRole;
let recipes: { getRecipeDetail: jasmine.Spy; updateRecipe: jasmine.Spy };
let getReadiness: jasmine.Spy;

function configure(): void {
  port = new WorkflowStepPort();
  port.connect(() => Promise.resolve(true), () => undefined);
  role = 'Owner';
  recipes = {
    getRecipeDetail: jasmine.createSpy('getRecipeDetail').and.resolveTo({ status: 'found', recipe: detail() }),
    updateRecipe: jasmine.createSpy('updateRecipe').and.resolveTo({ status: 'unavailable' }),
  };
  getReadiness = jasmine.createSpy('getReadiness').and.resolveTo({ status: 'found', readiness: basics([]) });

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: RecipeService, useValue: recipes },
      { provide: RecipeReadinessService, useValue: { getReadiness, transition: jasmine.createSpy('transition') } },
      { provide: ConfirmService, useValue: { confirm: jasmine.createSpy('confirm').and.resolveTo(true) } },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: signal({
            status: 'ready',
            memberships: [{ workspaceId: 'w1', workspaceSlug: SLUG, workspaceName: 'Sam', membershipId: 'm1', get role() { return role; }, status: 'Active' }],
          }),
          ensureLoaded: () => Promise.resolve(),
        },
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

function kitchenOf<T>(fixture: ComponentFixture<T>): RecipeTestKitchenComponent {
  return fixture.debugElement.query((node) => node.componentInstance instanceof RecipeTestKitchenComponent).componentInstance as RecipeTestKitchenComponent;
}

function buttons<T>(fixture: ComponentFixture<T>): string[] {
  return Array.from(root(fixture).querySelectorAll('button')).map((button) => button.textContent?.trim() ?? '');
}

describe('"Test and approve" step screens (13A.16)', () => {
  beforeEach(() => configure());

  describe('Fill in the basics', () => {
    it('says everything a test needs is here, with no form, when the server finds nothing missing', async () => {
      const fixture = await render(RecipeTestBasicsStepComponent, { run: testRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Everything a test needs is here.');
      expect(root(fixture).querySelectorAll('cp-status-pill').length).toBe(4);
      expect(editorOf(fixture)).toBeNull();
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('opens the editor on just the part that is missing', async () => {
      getReadiness.and.resolveTo({ status: 'found', readiness: basics(['recipe.ingredients.present']) });
      const fixture = await render(RecipeTestBasicsStepComponent, { run: testRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Still needed');
      expect(root(fixture).querySelector('#section-ingredients')).not.toBeNull();
      expect(root(fixture).querySelector('#section-instructions')).toBeNull();
      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('opens every part when several are missing, saves on a move, and checks the recipe again', async () => {
      getReadiness.and.resolveTo({ status: 'found', readiness: basics(['recipe.ingredients.present', 'recipe.time.stated']) });
      recipes.updateRecipe.and.resolveTo({ status: 'updated', recipe: detail({ notes: 'x' }), replayed: false });
      const fixture = await render(RecipeTestBasicsStepComponent, { run: testRun(), workspaceSlug: SLUG });

      expect(root(fixture).querySelector('#section-ingredients')).not.toBeNull();
      expect(root(fixture).querySelector('#section-timing')).not.toBeNull();

      editorOf(fixture)!.form.notes.set('x');
      getReadiness.and.resolveTo({ status: 'found', readiness: basics([]) });
      await expectAsync(port.flush()).toBeResolvedTo(true);
      await settle(fixture);

      expect(recipes.updateRecipe).toHaveBeenCalledTimes(1);
      expect(getReadiness).toHaveBeenCalledTimes(2);
      expect(text(fixture)).not.toContain('Still needed');
    });

    it('takes a rule the check did not report as met only when this workspace switched it off', async () => {
      getReadiness.and.resolveTo({
        status: 'found',
        readiness: { ...readiness(ALL_BASICS.slice(0, 2).map((ruleId) => finding(ruleId, 'Satisfied'))), disabledRuleIds: ['recipe.yield.stated'] },
      });
      const fixture = await render(RecipeTestBasicsStepComponent, { run: testRun(), workspaceSlug: SLUG });

      // Yield is switched off and so met; time was not reported and is not switched off, so it is still needed.
      expect(Array.from(root(fixture).querySelectorAll('cp-status-pill')).map((pill) => pill.textContent?.replace(/^\W+/, '').trim()))
        .toEqual(['Done', 'Done', 'Done', 'Still needed']);
      expect(root(fixture).querySelector('#section-timing')).not.toBeNull();
      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('does not step Continue forward for unsaved edits: whether they fill the gap is the server’s to say', async () => {
      getReadiness.and.resolveTo({ status: 'found', readiness: basics(['recipe.ingredients.present']) });
      const fixture = await render(RecipeTestBasicsStepComponent, { run: testRun(), workspaceSlug: SLUG });

      editorOf(fixture)!.form.notes.set('x');

      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('offers Try again when the recipe cannot be checked', async () => {
      getReadiness.and.resolveTo({ status: 'unavailable' });
      const fixture = await render(RecipeTestBasicsStepComponent, { run: testRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('couldn’t be checked just now');
      expect(buttons(fixture)).toContain('Try again');
    });
  });

  describe('Cook it and note what happened', () => {
    it('steps Continue forward only once the server finds the current version tested, read again after a test is recorded', async () => {
      getReadiness.and.resolveTo({ status: 'found', readiness: readiness([finding('recipe.testing.currentVersionUntested', 'Blocker')]) });
      const fixture = await render(RecipeTestCookStepComponent, { run: testRun(), workspaceSlug: SLUG });
      expect(port.registration()!.ready!()).toBeFalse();

      getReadiness.and.resolveTo({ status: 'found', readiness: readiness([finding('recipe.testing.currentVersionUntested', 'Satisfied')]) });
      kitchenOf(fixture).changed.emit();
      await settle(fixture);

      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('opens the Test kitchen on the version to cook, and says a test never changes the recipe', async () => {
      const fixture = await render(RecipeTestCookStepComponent, { run: testRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Cook version 3 exactly as it’s written');
      expect(text(fixture)).toContain('never changes your recipe');
      expect(root(fixture).querySelector('cp-recipe-test-kitchen')).not.toBeNull();
      await expectAsync(port.flush()).toBeResolvedTo(true);
    });
  });

  describe('Sort out what went wrong', () => {
    it('is ready only when the server finds no failed last test and no blocking problem, read again after one is answered', async () => {
      getReadiness.and.resolveTo({
        status: 'found',
        readiness: readiness([finding('recipe.testing.lastOutcomeFailed', 'Satisfied'), finding('recipe.testing.blockingIssueOutstanding', 'Blocker')]),
      });
      const fixture = await render(RecipeTestFixStepComponent, { run: testRun(), workspaceSlug: SLUG });
      expect(port.registration()!.ready!()).toBeFalse();

      getReadiness.and.resolveTo({
        status: 'found',
        readiness: readiness([finding('recipe.testing.lastOutcomeFailed', 'Satisfied'), finding('recipe.testing.blockingIssueOutstanding', 'Satisfied')]),
      });
      kitchenOf(fixture).changed.emit();
      await settle(fixture);

      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('opens on the problems noted, in the Test kitchen', async () => {
      const fixture = await render(RecipeTestFixStepComponent, { run: testRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Sort out the problems you noted');
      expect(root(fixture).querySelector('cp-recipe-test-kitchen')).not.toBeNull();
    });

    it('changes the recipe in the editor and saves it before turning to anything else', async () => {
      recipes.updateRecipe.and.resolveTo({ status: 'updated', recipe: detail({ notes: 'Less soda.' }), replayed: false });
      const fixture = await render(RecipeTestFixStepComponent, { run: testRun(), workspaceSlug: SLUG });

      root(fixture).querySelector<HTMLInputElement>('#recipe-test-fix-change')!.click();
      await settle(fixture);
      expect(root(fixture).querySelector('#section-details')).not.toBeNull();
      editorOf(fixture)!.form.notes.set('Less soda.');

      root(fixture).querySelector<HTMLInputElement>('#recipe-test-fix-check')!.click();
      await settle(fixture);

      expect(recipes.updateRecipe).toHaveBeenCalledTimes(1);
      expect(root(fixture).querySelector('cp-recipe-review-request')).not.toBeNull();
    });
  });

  describe('Send it for review, and approve it', () => {
    function panel(fixture: ComponentFixture<RecipeTestReadinessStepComponent>): RecipeReadinessComponent {
      return fixture.debugElement.query((node) => node.componentInstance instanceof RecipeReadinessComponent).componentInstance as RecipeReadinessComponent;
    }

    async function at(status: RecipeStatus, mode: 'send-for-review' | 'approve') {
      recipes.getRecipeDetail.and.resolveTo({ status: 'found', recipe: detail({ status }) });
      return render(RecipeTestReadinessStepComponent, { run: testRun(), workspaceSlug: SLUG, mode });
    }

    it('offers only the next move forward, never archive or reopen, and says how many are left', async () => {
      const fixture = await at('Draft', 'send-for-review');

      expect(text(fixture)).toContain('3 moves, one at a time');
      expect(panel(fixture).availableMoves().map((move) => move.to)).toEqual(['InDevelopment']);
      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('is done when the server says the recipe is ready for review, and not before', async () => {
      const fixture = await at('Testing', 'send-for-review');
      expect(port.registration()!.ready!()).toBeFalse();

      panel(fixture).transitioned.emit(detail({ status: 'ReadyForReview' }));
      await settle(fixture);

      expect(port.registration()!.ready!()).toBeTrue();
      expect(text(fixture)).toContain('It’s ready for review.');
    });

    it('never offers a reopen, even where one leads to a place a forward move also does', async () => {
      role = 'Owner';
      for (const status of ['Testing', 'ReadyForReview'] as const) {
        TestBed.resetTestingModule();
        configure();
        const fixture = await at(status, 'send-for-review');
        const offered = panel(fixture).availableMoves().map((move) => `${move.from}->${move.to}`);
        expect(offered).toEqual(status === 'Testing' ? ['Testing->ReadyForReview'] : []);
      }
    });

    it('says a recipe must be sent for review before it can be approved, rather than offering nothing', async () => {
      const fixture = await at('Testing', 'approve');

      expect(text(fixture)).toContain('It needs to be sent for review before it can be approved.');
      expect(panel(fixture).availableMoves()).toEqual([]);
    });

    it('names the panel plainly and nests its heading under the step’s', async () => {
      const fixture = await at('Testing', 'send-for-review');

      expect(root(fixture).querySelector('h3')?.textContent?.trim()).toBe('What’s holding it back');
      expect(root(fixture).querySelector('h2')).toBeNull();
    });

    it('offers an Editor only the approval', async () => {
      const fixture = await at('ReadyForReview', 'approve');

      expect(panel(fixture).availableMoves().map((move) => move.to)).toEqual(['Approved']);
      expect(text(fixture)).not.toContain('Someone with the Editor role');
    });

    it('tells a Contributor who gives the final yes, and offers them no approval', async () => {
      role = 'Contributor';
      const fixture = await at('ReadyForReview', 'approve');

      expect(panel(fixture).availableMoves()).toEqual([]);
      expect(text(fixture)).toContain('Someone with the Editor role gives the final yes.');
    });

    it('points a blocker that is fixed elsewhere at the recipe’s own page', async () => {
      const fixture = await at('Testing', 'send-for-review');

      panel(fixture).evidenceActivated.emit({ kind: 'RecipeAssetLink', recordId: null, fieldName: null, label: null });
      await settle(fixture);

      const link = Array.from(root(fixture).querySelectorAll('a')).find((anchor) => anchor.textContent?.includes('Open the recipe'));
      expect(link?.getAttribute('href')).toBe(`/${SLUG}/recipes/recipe-1`);
    });
  });
});
