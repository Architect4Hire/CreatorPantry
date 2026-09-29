import { signal } from '@angular/core';
import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Observable } from 'rxjs';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { AiProposalDetail, AiProposalStatus, AiProposalWarning, AiProposedChange } from '../../models/ai-proposal.models';
import { RequestRecipeConceptsRequest } from '../../models/recipe-concept.models';
import { RecipeConceptService, RequestConceptsOutcome, WatchConceptsOutcome } from '../../services/recipe-concept.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeConceptStudioComponent } from './recipe-concept-studio.component';

const REQUEST_ID = 'op-1';

function change(overrides: Partial<AiProposedChange> = {}): AiProposedChange {
  return {
    changeId: 'add-1',
    changeKind: 'Add',
    targetKind: 'RecipeConcept',
    targetId: 'concept-1',
    fieldName: null,
    beforeValue: null,
    afterValue: 'Slow-roasted tomato soup',
    proposedPosition: 0,
    disposition: 'Pending',
    ...overrides,
  };
}

function setRow(field: string, value: string, targetId = 'concept-1'): AiProposedChange {
  return {
    changeId: `set-${targetId}-${field}`,
    changeKind: 'Set',
    targetKind: 'RecipeConcept',
    targetId,
    fieldName: field,
    beforeValue: null,
    afterValue: value,
    proposedPosition: null,
    disposition: 'Pending',
  };
}

function twoConcepts(): readonly AiProposedChange[] {
  return [
    change({ changeId: 'add-1', targetId: 'concept-1', afterValue: 'Slow-roasted tomato soup', proposedPosition: 0 }),
    setRow('summary', 'A slow weekend soup.', 'concept-1'),
    change({ changeId: 'add-2', targetId: 'concept-2', afterValue: 'Quick weeknight noodles', proposedPosition: 1 }),
    setRow('summary', 'Fifteen minutes, one pan.', 'concept-2'),
  ];
}

function proposal(
  changes: readonly AiProposedChange[] = [change()],
  warnings: readonly AiProposalWarning[] = [],
): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: 'recipe.concepts.v1',
    promptTemplateId: 'recipe.concepts',
    promptTemplateVersion: '1.0.0',
    promptTemplateBodyChecksum: 'sha256:abc123',
    providerName: 'foundry-local',
    modelName: 'phi-4',
    createdAt: '2026-03-12T14:03:00Z',
    changes: [...changes],
    warnings: [...warnings],
  };
}

function operation(overrides: Partial<AiProposalStatus> = {}): AiProposalStatus {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'RecipeConcepts',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-03-12T14:02:00Z',
    statusChangedAt: '2026-03-12T14:02:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

function found(overrides: Partial<AiProposalStatus> = {}): WatchConceptsOutcome {
  return { status: 'found', operation: operation(overrides) };
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

/**
 * Answers each poll from {@link statuses} in order, repeating the last one, delivered asynchronously so that
 * unsubscribing before an answer arrives is a cancelled read — the same double shape `ai-proposal-panel`'s
 * spec uses, trimmed to this service's two methods.
 */
class StubRecipeConceptService {
  statuses: WatchConceptsOutcome[] = [found()];
  watchCalls = 0;
  cancelledWatches = 0;

  requestOutcome: RequestConceptsOutcome = { status: 'accepted', operation: operation(), replayed: false };
  requestCalls: { request: RequestRecipeConceptsRequest; key: string }[] = [];

  watchStatus(): Observable<WatchConceptsOutcome> {
    const index = this.watchCalls++;
    const outcome = this.statuses[Math.min(index, this.statuses.length - 1)];

    return new Observable<WatchConceptsOutcome>((subscriber) => {
      let delivered = false;
      const handle = setTimeout(() => {
        delivered = true;
        subscriber.next(outcome);
        subscriber.complete();
      }, 0);

      return () => {
        clearTimeout(handle);
        if (!delivered) this.cancelledWatches += 1;
      };
    });
  }

  requestConcepts(
    _slug: string,
    request: RequestRecipeConceptsRequest,
    key: string,
  ): Promise<RequestConceptsOutcome> {
    this.requestCalls.push({ request, key });
    return Promise.resolve(this.requestOutcome);
  }
}

describe('RecipeConceptStudioComponent', () => {
  let service: StubRecipeConceptService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeConceptStudioComponent>;
  let selections: unknown[];

  function configure(role: WorkspaceRole = 'Contributor'): void {
    service = new StubRecipeConceptService();
    memberships = new StubMembershipService();
    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeConceptService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
        {
          provide: ActivatedRoute,
          useValue: {
            parent: null,
            snapshot: { paramMap: convertToParamMap({ workspaceSlug: 'cozy-fall' }), queryParamMap: convertToParamMap({}) },
          },
        },
        { provide: Router, useValue: { navigate: jasmine.createSpy('navigate').and.returnValue(Promise.resolve(true)) } },
      ],
    });

    fixture = TestBed.createComponent(RecipeConceptStudioComponent);
    selections = [];
    fixture.componentInstance.conceptSelected.subscribe((event) => selections.push(event));
  }

  function render(role: WorkspaceRole = 'Contributor'): HTMLElement {
    configure(role);
    fixture.detectChanges();
    settle();
    return element();
  }

  /** Lets one pending microtask/0ms-timer land, then re-renders — the unit every interaction is built from. */
  function settle(): void {
    tick(0);
    fixture.detectChanges();
  }

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function input(id: string): HTMLInputElement {
    const found = element().querySelector<HTMLInputElement>(`#${id}`);
    if (!found) throw new Error(`no input #${id}`);
    return found;
  }

  function typeInto(id: string, value: string): void {
    const control = input(id);
    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function buttons(text: string): HTMLButtonElement[] {
    return Array.from(element().querySelectorAll('button')).filter((btn) => btn.textContent?.includes(text));
  }

  function buttonWith(text: string): HTMLButtonElement {
    const match = buttons(text)[0];
    if (!match) throw new Error(`no button containing "${text}"`);
    return match;
  }

  /** Click, then settle twice — once for the async handler's own promise, once for whatever it schedules next. */
  function click(text: string): void {
    buttonWith(text).click();
    settle();
    settle();
  }

  // ---- Role gating ----

  it('disables the form and explains why for a Viewer', fakeAsync(() => {
    render('Viewer');

    expect(element().querySelector('.studio-blocked[role="status"]')?.textContent).toContain('does not permit');
    expect(buttonWith('Get concepts').disabled).toBeTrue();
  }));

  it('leaves the form enabled for a Contributor', fakeAsync(() => {
    render('Contributor');

    expect(element().querySelector('[role="alert"]')).toBeNull();
    expect(buttonWith('Get concepts').disabled).toBeFalse();
  }));

  // ---- Errors ----

  it('submits only the fields the creator filled in, under one idempotency key', fakeAsync(() => {
    render();
    typeInto('concept-audience', 'Weeknight home cooks');
    typeInto('concept-cuisine', 'Sichuan');

    click('Get concepts');

    expect(service.requestCalls.length).toBe(1);
    expect(service.requestCalls[0].request.audience).toBe('Weeknight home cooks');
    expect(service.requestCalls[0].request.cuisine).toBe('Sichuan');
    expect(service.requestCalls[0].request.course).toBe('');
    expect(service.requestCalls[0].key.length).toBeGreaterThan(0);
    discardPeriodicTasks();
  }));

  it('shows a server field-validation error next to its field', fakeAsync(() => {
    render();
    service.requestOutcome = { status: 'validation_failed', fieldErrors: { audience: ['Too long.'] } };

    click('Get concepts');

    expect(element().querySelector('#concept-audience-error')?.textContent).toContain('Too long.');
    expect(input('concept-audience').getAttribute('aria-invalid')).toBe('true');
  }));

  it('reports a not-enabled refusal in words', fakeAsync(() => {
    render();
    service.requestOutcome = { status: 'task_not_enabled' };

    click('Get concepts');

    expect(element().querySelector('.studio-problem')?.textContent).toContain('switched off');
  }));

  it('reports an unreachable server without losing what was typed', fakeAsync(() => {
    render();
    service.requestOutcome = { status: 'unavailable' };
    typeInto('concept-audience', 'Weeknight home cooks');

    click('Get concepts');

    expect(element().querySelector('.studio-problem')?.textContent).toContain("Couldn't reach the server");
    expect(input('concept-audience').value).toBe('Weeknight home cooks');
  }));

  it('shows a degraded connection state without dropping the last known status', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Running' }), { status: 'unavailable' }];

    click('Get concepts');
    expect(element().textContent).toContain('Working');

    tick(2000);
    settle();

    expect(element().textContent).toContain('Not up to date');
    expect(element().textContent).toContain('Working');

    discardPeriodicTasks();
  }));

  // ---- Selection ----

  it('renders each returned concept and lets the creator choose one', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];

    click('Get concepts');

    expect(element().textContent).toContain('Slow-roasted tomato soup');
    expect(element().textContent).toContain('Quick weeknight noodles');

    click('Choose this concept');

    expect(selections.length).toBe(1);
    expect(element().querySelector('.studio-selection')?.textContent).toContain('chosen');
  }));

  it('replaces the prior selection when a different concept is chosen', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
    click('Get concepts');

    // Both cards start with the same label; the first click leaves only one "Choose this concept" button —
    // the chosen card now reads "Chosen" — so the next click on that same query unambiguously picks the other.
    buttons('Choose this concept')[0].click();
    settle();
    expect(fixture.componentInstance.selectedConcept()?.title).toBe('Slow-roasted tomato soup');

    buttons('Choose this concept')[0].click();
    settle();

    expect(selections.length).toBe(2);
    expect(fixture.componentInstance.selectedConcept()?.title).toBe('Quick weeknight noodles');
  }));

  it('clears the selection when the creator starts a new brief', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
    click('Get concepts');
    click('Choose this concept');

    expect(fixture.componentInstance.selectedConcept()).not.toBeNull();

    click('Try a new brief');

    expect(fixture.componentInstance.selectedConcept()).toBeNull();
    expect(element().textContent).not.toContain('Slow-roasted tomato soup');
  }));

  // ---- Cancellation ----

  it('stops polling on "Stop checking" and resumes on "Check again"', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Running' })];
    click('Get concepts');

    const callsBeforeStop = service.watchCalls;
    expect(element().textContent).toContain('keeps running');
    click('Stop checking');

    tick(10000);
    fixture.detectChanges();
    expect(service.watchCalls).toBe(callsBeforeStop);
    expect(buttonWith('Check again')).toBeTruthy();

    click('Check again');

    expect(service.watchCalls).toBeGreaterThan(callsBeforeStop);
    discardPeriodicTasks();
  }));

  it('aborts an in-flight poll on teardown', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Running' })];

    buttonWith('Get concepts').click();
    settle(); // flushes the submit promise and registers the poll's first ask — not yet delivered

    expect(service.watchCalls).toBe(1);
    expect(service.cancelledWatches).toBe(0);

    fixture.destroy();

    expect(service.cancelledWatches).toBe(1);
    tick(0);
  }));

  // ---- Accessibility ----

  it('gives every brief field a label and describes its error through aria-describedby', fakeAsync(() => {
    render();
    service.requestOutcome = { status: 'validation_failed', fieldErrors: { audience: ['Too long.'] } };
    click('Get concepts');

    const control = input('concept-audience');
    expect(element().querySelector('label[for="concept-audience"]')).toBeTruthy();
    expect(control.getAttribute('aria-describedby')).toContain('concept-audience-error');
  }));

  it('renders concept actions as real, individually reachable buttons', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
    click('Get concepts');

    const actions = buttons('Choose this concept');
    expect(actions.length).toBe(2);
    for (const action of actions) {
      expect(action.tagName).toBe('BUTTON');
      expect(action.getAttribute('type')).toBe('button');
    }
  }));

  it('announces a failure assertively, reusing cp-ai-operation-status’s live regions', fakeAsync(() => {
    render();
    service.statuses = [{ status: 'found', operation: operation({ status: 'Failed', failureCategory: 'Provider' }) }];

    click('Get concepts');

    const alert = element().querySelector('p.cp-sr-only[role="alert"]');
    expect(alert?.textContent).toContain("Didn't finish");
    expect(element().querySelector('p.cp-sr-only[role="status"]')?.textContent?.trim()).toBe('');
  }));

  // ---- Page frame and results frame ----

  it('leaves the page title to the shell instead of repeating it', fakeAsync(() => {
    render();

    // The shell renders the h1 from the route's data.title. The h2s on this page name the brief's own parts;
    // none of them repeats the page's name.
    expect(element().querySelector('h1')).toBeNull();
    expect(element().textContent).not.toContain('AI Recipe Studio');

    const headings = Array.from(element().querySelectorAll('h2')).map((h) => h.textContent?.trim());
    expect(headings).toEqual(["The dish", "Who it's for", 'Ingredients and limits', 'Your voice']);
  }));

  it('reads its concepts through the shared list frame, which owns the results heading', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];

    click('Get concepts');

    const shell = element().querySelector('cp-list-shell');
    expect(shell).not.toBeNull();
    expect(shell?.querySelector('h2')?.textContent).toContain('Recipe concepts');
    expect(fixture.componentInstance.conceptsListState()).toBe('ready');
  }));

  it('shows an empty results frame, not a bare sentence, when the brief returned nothing', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal([]) })];

    click('Get concepts');

    expect(fixture.componentInstance.conceptsListState()).toBe('empty');
    expect(element().querySelector('cp-empty-state')).not.toBeNull();
    expect(element().querySelector('.concept-list')).toBeNull();
  }));

  it('reports a request that did not finish as an error in the results frame', fakeAsync(() => {
    render();
    service.statuses = [{ status: 'found', operation: operation({ status: 'Failed', failureCategory: 'Provider' }) }];

    click('Get concepts');

    expect(fixture.componentInstance.conceptsListState()).toBe('error');
    expect(element().querySelector('cp-list-shell')?.textContent).toContain("didn't finish");
  }));
  it('reads as a form: one card, a legend, and named sections of stacked fields', fakeAsync(() => {
    render();

    const card = element().querySelector('cp-card')!;
    expect(card).not.toBeNull();
    expect(card.querySelector('.brief-legend')?.textContent).toContain('Every field is optional');

    const sections = Array.from(card.querySelectorAll('cp-form-section'));
    expect(sections.length).toBe(4);

    // Stacked, never tidied into rows: prose and list fields read as a column at the reading measure, which is
    // the shape the recipe editor sets (DESIGN-SYSTEM.md, "The shape of a form").
    expect(card.querySelector('cp-field-row')).toBeNull();

    // Every field the brief submits sits inside one of those sections, not loose beside them.
    const fields = Array.from(card.querySelectorAll('cp-field'));
    expect(fields.length).toBe(11);
    expect(fields.every((field) => field.closest('cp-form-section') !== null)).toBeTrue();
  }));
});
