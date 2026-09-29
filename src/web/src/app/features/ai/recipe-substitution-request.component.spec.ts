import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RequestIngredientSubstitutionRequest } from '../../models/recipe-substitution.models';
import {
  RecipeSubstitutionService,
  RequestSubstitutionOutcome,
  WatchSubstitutionOutcome,
} from '../../services/recipe-substitution.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeSubstitutionRequestComponent } from './recipe-substitution-request.component';

const REQUEST_ID = 'op-substitution-1';
const VERSION_ID = 'v-1';

const INGREDIENT_GROUPS: readonly RecipeIngredientGroup[] = [
  {
    id: 'g1',
    title: 'Dough',
    sortOrder: 0,
    ingredients: [
      {
        id: 'ing-1',
        sortOrder: 0,
        displayText: '2 cups flour',
        displayTextSource: 'Creator',
        ingredientNameText: 'flour',
        unitText: 'cups',
        quantity: 2,
        quantityUpper: null,
        measurementUnitId: null,
        ingredientId: null,
      } as RecipeIngredientGroup['ingredients'][number],
    ],
  },
];

function operation(proposal: AiProposalStatus['proposal'] = null, status: AiProposalStatus['status'] = 'Requested'): AiProposalStatus {
  return {
    aiProposalRequestId: REQUEST_ID,
    status,
    taskType: 'IngredientSubstitution',
    scope: 'Advisory',
    sourceVersionId: VERSION_ID,
    requestedAt: '2026-09-28T14:02:00Z',
    statusChangedAt: '2026-09-28T14:02:00Z',
    failureCategory: null,
    proposal,
  };
}

function membership(role: WorkspaceRole): MyWorkspaceMembership {
  return {
    workspaceId: 'w1',
    workspaceSlug: 'cozy-fall',
    workspaceName: 'Cozy Fall',
    membershipId: 'm1',
    role,
    status: 'Active',
  };
}

class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership('Contributor')] });
  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }
}

class StubSubstitutionService {
  outcome: RequestSubstitutionOutcome = { status: 'accepted', operation: operation(), replayed: false };
  watchOutcome: WatchSubstitutionOutcome = { status: 'found', operation: operation(null, 'Proposed') };
  calls: { request: RequestIngredientSubstitutionRequest; key: string }[] = [];

  requestSubstitution(
    _slug: string,
    _recipeId: string,
    request: RequestIngredientSubstitutionRequest,
    key: string,
  ): Promise<RequestSubstitutionOutcome> {
    this.calls.push({ request, key });
    return Promise.resolve(this.outcome);
  }

  watchStatus(): Observable<WatchSubstitutionOutcome> {
    return of(this.watchOutcome);
  }
}

describe('RecipeSubstitutionRequestComponent', () => {
  let service: StubSubstitutionService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeSubstitutionRequestComponent>;

  beforeEach(() => {
    service = new StubSubstitutionService();
    memberships = new StubMembershipService();
  });

  function render(role: WorkspaceRole = 'Contributor'): HTMLElement {
    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeSubstitutionService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
      ],
    });

    fixture = TestBed.createComponent(RecipeSubstitutionRequestComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('currentVersionId', VERSION_ID);
    fixture.componentRef.setInput('ingredientGroups', INGREDIENT_GROUPS);
    fixture.detectChanges();
    settle();

    return fixture.nativeElement as HTMLElement;
  }

  function settle(): void {
    tick(0);
    fixture.detectChanges();
  }

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function buttonWith(label: string): HTMLButtonElement {
    const match = Array.from(element().querySelectorAll('button')).find((button) =>
      (button.textContent ?? '').includes(label),
    );
    if (!match) throw new Error(`no button containing "${label}"`);
    return match as HTMLButtonElement;
  }

  // aria-disabled rather than the native attribute, so a disabled button never drops focus mid-press.
  function isDisabled(label: string): boolean {
    return buttonWith(label).getAttribute('aria-disabled') === 'true';
  }

  function pickIngredient(): void {
    fixture.componentInstance.selectedIngredient.set({ id: 'ing-1', label: '2 cups flour' });
    settle();
  }

  // ---- the bound -------------------------------------------------------------------------------------

  it('offers every ingredient line as a choice', fakeAsync(() => {
    render();

    expect(fixture.componentInstance.ingredientOptions()).toEqual([
      { id: 'ing-1', label: '2 cups flour', detail: 'Dough' },
    ]);
  }));

  it('never says anything has been changed', fakeAsync(() => {
    render();

    expect(element().textContent).toContain('changes nothing in the recipe');
  }));

  it('cannot be submitted until an ingredient is chosen', fakeAsync(() => {
    render();

    expect(isDisabled('Find alternatives')).toBe(true);

    pickIngredient();

    expect(isDisabled('Find alternatives')).toBe(false);
  }));

  it('sends the chosen ingredient, the pinned version and the reason', fakeAsync(() => {
    render();
    pickIngredient();
    fixture.componentInstance.reason.set('allergic to dairy');
    settle();

    buttonWith('Find alternatives').click();
    settle();

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].request.ingredientId).toBe('ing-1');
    expect(service.calls[0].request.sourceVersionId).toBe(VERSION_ID);
    expect(service.calls[0].request.reason).toBe('allergic to dairy');
  }));

  it('sends an idempotency key with every ask', fakeAsync(() => {
    render();
    pickIngredient();

    buttonWith('Find alternatives').click();
    settle();

    expect(service.calls[0].key).toBeTruthy();
  }));

  // ---- handing over to the advisory results ----------------------------------------------------------

  it('hands the accepted request to the advisory results, read-only', fakeAsync(() => {
    service.watchOutcome = {
      status: 'found',
      operation: operation(
        {
          proposalId: 'p1',
          outputSchemaVersion: 'recipe.substitution.v1',
          promptTemplateId: 'recipe.substitution',
          promptTemplateVersion: '1.0.0',
          promptTemplateBodyChecksum: 'sha256:abc',
          providerName: 'test-provider',
          modelName: 'test-model',
          createdAt: '2026-09-28T14:02:00Z',
          changes: [
            { changeId: 'a1', changeKind: 'Add', targetKind: 'IngredientSubstitution', targetId: 't1', fieldName: null, beforeValue: null, afterValue: 'soured milk', proposedPosition: 0, disposition: 'Pending' },
          ],
          warnings: [],
        },
        'Proposed',
      ),
    };
    render();
    pickIngredient();

    buttonWith('Find alternatives').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBe(REQUEST_ID);
    expect(fixture.componentInstance.state()).toBe('watching');
    expect(element().querySelector('cp-ai-advisory-results')).not.toBeNull();
    // No disposition surface of any kind — advisory results is presentational only.
    expect(element().querySelector('input[type="checkbox"]')).toBeNull();
  }));

  it('can be reset to ask about another ingredient', fakeAsync(() => {
    render();
    pickIngredient();
    buttonWith('Find alternatives').click();
    settle();

    buttonWith('Ask about another ingredient').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBeNull();
    expect(fixture.componentInstance.state()).toBe('choosing');
  }));

  // ---- refusals --------------------------------------------------------------------------------------

  it('explains a stale version in terms of what to do about it', fakeAsync(() => {
    service.outcome = { status: 'source_version_invalid' };
    render();
    pickIngredient();

    buttonWith('Find alternatives').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('Reload it and ask again');
  }));

  it('explains a task nobody switched on', fakeAsync(() => {
    service.outcome = { status: 'task_not_enabled' };
    render();
    pickIngredient();

    buttonWith('Find alternatives').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('not switched on');
  }));

  it('reports a server it could not reach', fakeAsync(() => {
    service.outcome = { status: 'unavailable' };
    render();
    pickIngredient();

    buttonWith('Find alternatives').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain("Couldn't reach the server");
  }));

  // ---- role ------------------------------------------------------------------------------------------

  it('refuses a viewer before they can ask', fakeAsync(() => {
    render('Viewer');

    expect(fixture.componentInstance.state()).toBe('forbidden');
    expect(element().querySelector('.substitution-blocked[role="status"]')?.textContent).toContain('cannot ask for substitution advice');
    expect(service.calls.length).toBe(0);
  }));

  // ---- the version this reads ------------------------------------------------------------------------

  it('says nothing about unsaved edits when the editor has none', fakeAsync(() => {
    render();

    expect(element().querySelector('.substitution-caveat')).toBeNull();
  }));

  it('says the request works from the last saved version while the editor is dirty', fakeAsync(() => {
    render();

    fixture.componentRef.setInput('editorIsDirty', true);
    settle();

    const caveat = element().querySelector('.substitution-caveat');
    expect(caveat).not.toBeNull();
    expect(caveat?.textContent).toContain("Unsaved edits aren't included");
    expect(caveat?.querySelector('cp-status-pill')).not.toBeNull();
  }));
});
