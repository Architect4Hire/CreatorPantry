import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import { RECIPE_REVISION_SECTIONS, RequestRecipeRevisionRequest } from '../../models/recipe-revision.models';
import { RecipeRevisionService, RequestRevisionOutcome } from '../../services/recipe-revision.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeRevisionRequestComponent } from './recipe-revision-request.component';

const REQUEST_ID = 'op-revision-1';
const VERSION_ID = 'v-1';

function operation(): AiProposalStatus {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'RecipeRevision',
    scope: 'Metadata',
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

class StubRevisionService {
  outcome: RequestRevisionOutcome = { status: 'accepted', operation: operation(), replayed: false };
  calls: { request: RequestRecipeRevisionRequest; key: string }[] = [];

  requestRevision(
    _slug: string,
    _recipeId: string,
    request: RequestRecipeRevisionRequest,
    key: string,
  ): Promise<RequestRevisionOutcome> {
    this.calls.push({ request, key });
    return Promise.resolve(this.outcome);
  }
}

describe('RecipeRevisionRequestComponent', () => {
  let service: StubRevisionService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeRevisionRequestComponent>;

  beforeEach(() => {
    service = new StubRevisionService();
    memberships = new StubMembershipService();
  });

  function render(role: WorkspaceRole = 'Contributor'): HTMLElement {
    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeRevisionService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
      ],
    });

    fixture = TestBed.createComponent(RecipeRevisionRequestComponent);
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

  function chooseSection(label: string): void {
    const radios = Array.from(element().querySelectorAll<HTMLInputElement>('input[type="radio"]'));
    const index = RECIPE_REVISION_SECTIONS.findIndex((section) => section.label === label);
    if (index < 0) throw new Error(`no section "${label}"`);

    radios[index].click();
    settle();
  }

  // ---- the bound -------------------------------------------------------------------------------------

  it('offers only the sections the server can revise', fakeAsync(() => {
    render();

    const labels = Array.from(element().querySelectorAll('.revision-section-label')).map(
      (span) => span.textContent?.trim(),
    );

    expect(labels).toEqual(RECIPE_REVISION_SECTIONS.map((section) => section.label));
    expect(labels).not.toContain('Media');
  }));

  /**
   * A request that could name fields could widen the bound it was just asked to respect. Which fields a
   * section covers is the server's answer.
   */
  it('offers no field picker', fakeAsync(() => {
    render();

    expect(element().querySelectorAll('input[type="checkbox"]').length).toBe(0);
    expect(text()).toContain('Anything outside the part you choose is left alone');
  }));

  it('never says anything has been changed', fakeAsync(() => {
    render();

    expect(text()).toContain('nothing changes until you accept');
    expect(text()).toContain('Nothing is changed until you review');
  }));

  /** There is no sensible default section, and the widest one is the wrong guess. */
  it('cannot be submitted until a section is chosen', fakeAsync(() => {
    render();

    expect(buttonWith('Ask for suggestions').disabled).toBe(true);

    chooseSection('Method');

    expect(buttonWith('Ask for suggestions').disabled).toBe(false);
  }));

  it('sends the chosen section, the pinned version and the goal', fakeAsync(() => {
    render();
    chooseSection('Ingredients');
    fixture.componentInstance.goal.set('  Cut the sugar  ');
    settle();

    buttonWith('Ask for suggestions').click();
    settle();

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].request.scope).toBe('Ingredients');
    expect(service.calls[0].request.sourceVersionId).toBe(VERSION_ID);
    expect(service.calls[0].request.goal).toBe('  Cut the sugar  ');
  }));

  it('sends an idempotency key with every ask', fakeAsync(() => {
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    expect(service.calls[0].key).toBeTruthy();
  }));

  // ---- handing over to the panel ---------------------------------------------------------------------

  it('hands the accepted request to the proposal panel', fakeAsync(() => {
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBe(REQUEST_ID);
    expect(fixture.componentInstance.state()).toBe('watching');
    expect(element().querySelector('cp-ai-proposal-panel')).not.toBeNull();
  }));

  it('can be reset to ask for another revision', fakeAsync(() => {
    render();
    chooseSection('Method');
    buttonWith('Ask for suggestions').click();
    settle();

    buttonWith('Ask for another revision').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBeNull();
    expect(fixture.componentInstance.state()).toBe('choosing');
  }));

  // ---- refusals --------------------------------------------------------------------------------------

  /** The one a creator can act on themselves: the recipe moved while they were deciding what to ask. */
  it('explains a stale version in terms of what to do about it', fakeAsync(() => {
    service.outcome = { status: 'source_version_invalid' };
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('Reload it and ask again');
    expect(fixture.componentInstance.state()).toBe('choosing');
  }));

  it('explains a task nobody switched on', fakeAsync(() => {
    service.outcome = { status: 'task_not_enabled' };
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('not switched on');
  }));

  it('surfaces a field error against the field it names', fakeAsync(() => {
    service.outcome = {
      status: 'validation_failed',
      fieldErrors: { Goal: ['Keep the goal to 500 characters or fewer.'] },
    };
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    expect(fixture.componentInstance.errorFor('Goal')).toContain('500 characters');
  }));

  it('reports a server it could not reach', fakeAsync(() => {
    service.outcome = { status: 'unavailable' };
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain("Couldn't reach the server");
  }));

  /**
   * The attempt may have reached the server, so a new key would buy a second answer to the same question.
   * A retry after a transport failure reuses the key; a fresh ask after a refusal does not.
   */
  it('reuses the key when retrying a request that may have landed', fakeAsync(() => {
    service.outcome = { status: 'unavailable' };
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();
    buttonWith('Ask for suggestions').click();
    settle();

    expect(service.calls.length).toBe(2);
    expect(service.calls[0].key).toBe(service.calls[1].key);
  }));

  it('takes a fresh key after a refusal the server definitely recorded', fakeAsync(() => {
    service.outcome = { status: 'task_not_enabled' };
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    service.outcome = { status: 'accepted', operation: operation(), replayed: false };
    buttonWith('Ask for suggestions').click();
    settle();

    expect(service.calls[0].key).not.toBe(service.calls[1].key);
  }));

  // ---- role ------------------------------------------------------------------------------------------

  it('refuses a viewer before they can ask', fakeAsync(() => {
    render('Viewer');

    expect(fixture.componentInstance.state()).toBe('forbidden');
    expect(element().querySelector('[role="alert"]')?.textContent).toContain('cannot ask for a revision');
    expect(service.calls.length).toBe(0);
  }));

  // ---- accessibility ---------------------------------------------------------------------------------

  /** A real radio group: focusable, announced, and operable by arrow key. */
  it('presents the sections as a labelled radio group', fakeAsync(() => {
    render();

    const legend = element().querySelector('fieldset legend');
    expect(legend?.textContent).toContain('Which part may change?');

    const radios = Array.from(element().querySelectorAll<HTMLInputElement>('input[type="radio"]'));
    expect(radios.length).toBe(RECIPE_REVISION_SECTIONS.length);
    expect(new Set(radios.map((radio) => radio.name)).size).toBe(1);
  }));

  it('names each section in words as well as marking the chosen one', fakeAsync(() => {
    render();
    chooseSection('Method');

    const radios = Array.from(element().querySelectorAll<HTMLInputElement>('input[type="radio"]'));
    const chosen = radios.filter((radio) => radio.checked);

    expect(chosen.length).toBe(1);
    expect(text()).toContain('The steps and their groups.');
  }));

  it('labels the goal field and ties it to its own control', fakeAsync(() => {
    render();

    const textarea = element().querySelector('textarea');
    expect(textarea).not.toBeNull();
    expect(element().querySelector(`label[for="${textarea!.id}"]`)?.textContent).toContain('What do you want');
  }));

  it('announces a refusal assertively', fakeAsync(() => {
    service.outcome = { status: 'forbidden' };
    render();
    chooseSection('Method');

    buttonWith('Ask for suggestions').click();
    settle();

    expect(element().querySelector('[role="alert"]')).not.toBeNull();
  }));
});
