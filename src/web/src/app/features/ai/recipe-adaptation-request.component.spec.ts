import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import { RECIPE_ADAPTATION_GOALS, RequestRecipeAdaptationRequest } from '../../models/recipe-adaptation.models';
import { RecipeAdaptationService, RequestAdaptationOutcome } from '../../services/recipe-adaptation.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeAdaptationRequestComponent } from './recipe-adaptation-request.component';

const REQUEST_ID = 'op-adaptation-1';
const VERSION_ID = 'v-1';

function operation(): AiProposalStatus {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'RecipeAdaptation',
    scope: 'WholeRecipe',
    sourceVersionId: VERSION_ID,
    requestedAt: '2026-09-28T14:02:00Z',
    statusChangedAt: '2026-09-28T14:02:00Z',
    failureCategory: null,
    proposal: null,
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

class StubAdaptationService {
  outcome: RequestAdaptationOutcome = { status: 'accepted', operation: operation(), replayed: false };
  calls: { request: RequestRecipeAdaptationRequest; key: string }[] = [];

  requestAdaptation(
    _slug: string,
    _recipeId: string,
    request: RequestRecipeAdaptationRequest,
    key: string,
  ): Promise<RequestAdaptationOutcome> {
    this.calls.push({ request, key });
    return Promise.resolve(this.outcome);
  }
}

describe('RecipeAdaptationRequestComponent', () => {
  let service: StubAdaptationService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeAdaptationRequestComponent>;

  beforeEach(() => {
    service = new StubAdaptationService();
    memberships = new StubMembershipService();
  });

  function render(role: WorkspaceRole = 'Contributor'): HTMLElement {
    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeAdaptationService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
      ],
    });

    fixture = TestBed.createComponent(RecipeAdaptationRequestComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('currentVersionId', VERSION_ID);
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

  function text(): string {
    return element().textContent ?? '';
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

  function chooseGoal(label: string): void {
    const radios = Array.from(
      element().querySelectorAll<HTMLInputElement>('input[type="radio"][name="cp-adaptation-goal"]'),
    );
    const index = RECIPE_ADAPTATION_GOALS.findIndex((option) => option.label === label);
    if (index < 0) throw new Error(`no goal "${label}"`);

    radios[index].click();
    settle();
  }

  function setTextarea(id: string, value: string): void {
    const textarea = element().querySelector<HTMLTextAreaElement>(`#${id}`)!;
    textarea.value = value;
    textarea.dispatchEvent(new Event('input'));
    settle();
  }

  // ---- the bound -------------------------------------------------------------------------------------

  it('offers exactly the four adaptation goals', fakeAsync(() => {
    render();

    const labels = Array.from(element().querySelectorAll('.adaptation-goal-label')).map(
      (span) => span.textContent?.trim(),
    );

    expect(labels).toEqual(RECIPE_ADAPTATION_GOALS.map((option) => option.label));
  }));

  it('never says anything has been changed', fakeAsync(() => {
    render();

    expect(text()).toContain('nothing changes until you accept');
    expect(text()).toContain('Nothing is changed until you review');
  }));

  // ---- text goals --------------------------------------------------------------------------------------

  it('cannot be submitted for a text goal until its detail is filled in', fakeAsync(() => {
    render();
    chooseGoal('A diet or restriction');

    expect(isDisabled('Ask for an adaptation')).toBe(true);

    setTextarea('cp-adaptation-goal-detail', 'gluten-free');

    expect(isDisabled('Ask for an adaptation')).toBe(false);
  }));

  it('sends the chosen goal, the pinned version and the detail', fakeAsync(() => {
    render();
    chooseGoal('Equipment you have, or do not have');
    setTextarea('cp-adaptation-goal-detail', 'no stand mixer');

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].request.goal).toBe('Equipment');
    expect(service.calls[0].request.sourceVersionId).toBe(VERSION_ID);
    expect(service.calls[0].request.goalDetail).toBe('no stand mixer');
    expect(service.calls[0].request.targetMultiplier).toBeNull();
    expect(service.calls[0].request.targetYieldQuantity).toBeNull();
  }));

  // ---- yield goal --------------------------------------------------------------------------------------

  it('offers no free-text requirement for the yield goal', fakeAsync(() => {
    render();
    chooseGoal('A different batch size');

    expect(isDisabled('Ask for an adaptation')).toBe(true);
    expect(element().querySelector('#cp-adaptation-yield-value')).not.toBeNull();
  }));

  it('sends a multiplier by default for the yield goal', fakeAsync(() => {
    render();
    chooseGoal('A different batch size');

    const input = element().querySelector<HTMLInputElement>('#cp-adaptation-yield-value')!;
    input.value = '2';
    input.dispatchEvent(new Event('input'));
    settle();

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(service.calls[0].request.goal).toBe('Yield');
    expect(service.calls[0].request.targetMultiplier).toBe(2);
    expect(service.calls[0].request.targetYieldQuantity).toBeNull();
  }));

  it('sends a target yield instead when that mode is chosen', fakeAsync(() => {
    render();
    chooseGoal('A different batch size');

    const modeRadios = Array.from(
      element().querySelectorAll<HTMLInputElement>('input[type="radio"][name="cp-adaptation-yield-mode"]'),
    );
    modeRadios[1].click();
    settle();

    const input = element().querySelector<HTMLInputElement>('#cp-adaptation-yield-value')!;
    input.value = '12';
    input.dispatchEvent(new Event('input'));
    settle();

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(service.calls[0].request.targetMultiplier).toBeNull();
    expect(service.calls[0].request.targetYieldQuantity).toBe(12);
  }));

  it('cannot submit a non-positive yield value', fakeAsync(() => {
    render();
    chooseGoal('A different batch size');

    const input = element().querySelector<HTMLInputElement>('#cp-adaptation-yield-value')!;
    input.value = '0';
    input.dispatchEvent(new Event('input'));
    settle();

    expect(isDisabled('Ask for an adaptation')).toBe(true);
  }));

  // ---- handing over to the panel ---------------------------------------------------------------------

  it('hands the accepted request to the proposal panel', fakeAsync(() => {
    render();
    chooseGoal('A diet or restriction');
    setTextarea('cp-adaptation-goal-detail', 'vegan');

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBe(REQUEST_ID);
    expect(fixture.componentInstance.state()).toBe('watching');
    expect(element().querySelector('cp-ai-proposal-panel')).not.toBeNull();
  }));

  it('can be reset to ask for another adaptation', fakeAsync(() => {
    render();
    chooseGoal('A diet or restriction');
    setTextarea('cp-adaptation-goal-detail', 'vegan');
    buttonWith('Ask for an adaptation').click();
    settle();

    buttonWith('Ask for another adaptation').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBeNull();
    expect(fixture.componentInstance.state()).toBe('choosing');
  }));

  // ---- refusals --------------------------------------------------------------------------------------

  it('explains an unresolvable yield target', fakeAsync(() => {
    service.outcome = { status: 'yield_target_invalid' };
    render();
    chooseGoal('A different batch size');
    const input = element().querySelector<HTMLInputElement>('#cp-adaptation-yield-value')!;
    input.value = '2';
    input.dispatchEvent(new Event('input'));
    settle();

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain("could not be scaled to");
  }));

  it('explains a stale version in terms of what to do about it', fakeAsync(() => {
    service.outcome = { status: 'source_version_invalid' };
    render();
    chooseGoal('A diet or restriction');
    setTextarea('cp-adaptation-goal-detail', 'vegan');

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('Reload it and ask again');
  }));

  it('surfaces a field error against the field it names', fakeAsync(() => {
    service.outcome = {
      status: 'validation_failed',
      fieldErrors: { GoalDetail: ['Say what the goal means for this recipe.'] },
    };
    render();
    chooseGoal('A diet or restriction');
    setTextarea('cp-adaptation-goal-detail', 'vegan');

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(fixture.componentInstance.errorFor('GoalDetail')).toContain('Say what the goal means');
  }));

  it('reports a server it could not reach', fakeAsync(() => {
    service.outcome = { status: 'unavailable' };
    render();
    chooseGoal('A diet or restriction');
    setTextarea('cp-adaptation-goal-detail', 'vegan');

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain("Couldn't reach the server");
  }));

  // ---- role ------------------------------------------------------------------------------------------

  it('refuses a viewer before they can ask', fakeAsync(() => {
    render('Viewer');

    expect(fixture.componentInstance.state()).toBe('forbidden');
    expect(element().querySelector('.adaptation-blocked[role="status"]')?.textContent).toContain('cannot ask for an adaptation');
    expect(service.calls.length).toBe(0);
  }));

  // ---- accessibility ---------------------------------------------------------------------------------

  it('presents the goals as a labelled radio group', fakeAsync(() => {
    render();

    const legend = element().querySelector('fieldset legend');
    expect(legend?.textContent).toContain('What is this adaptation for?');

    const radios = Array.from(
      element().querySelectorAll<HTMLInputElement>('input[type="radio"][name="cp-adaptation-goal"]'),
    );
    expect(radios.length).toBe(RECIPE_ADAPTATION_GOALS.length);
    expect(new Set(radios.map((radio) => radio.name)).size).toBe(1);
  }));

  it('announces a refusal assertively', fakeAsync(() => {
    service.outcome = { status: 'forbidden' };
    render();
    chooseGoal('A diet or restriction');
    setTextarea('cp-adaptation-goal-detail', 'vegan');

    buttonWith('Ask for an adaptation').click();
    settle();

    expect(element().querySelector('[role="alert"]')).not.toBeNull();
  }));

  // ---- the version this reads ------------------------------------------------------------------------

  it('says nothing about unsaved edits when the editor has none', fakeAsync(() => {
    render();

    expect(element().querySelector('.adaptation-caveat')).toBeNull();
  }));

  it('says the request works from the last saved version while the editor is dirty', fakeAsync(() => {
    render();

    fixture.componentRef.setInput('editorIsDirty', true);
    settle();

    const caveat = element().querySelector('.adaptation-caveat');
    expect(caveat).not.toBeNull();
    expect(caveat?.textContent).toContain("Unsaved edits aren't included");
    expect(caveat?.querySelector('cp-status-pill')).not.toBeNull();
  }));
});
