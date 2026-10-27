import { signal } from '@angular/core';
import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Observable, of } from 'rxjs';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  AiProposalDetail,
  AiProposalStatus,
  AiProposalWarning,
  AiProposedChange,
} from '../../models/ai-proposal.models';
import { RequestRecipeConceptsRequest } from '../../models/recipe-concept.models';
import { AiRequestOutcome } from '../../services/ai-request';
import {
  RecipeConceptService,
  RequestConceptsOutcome,
  WatchConceptsOutcome,
} from '../../services/recipe-concept.service';
import { CreativeContextDraft } from '../../models/creative-context.models';
import {
  CreativeContextCreateOutcome,
  CreativeContextReadOutcome,
  CreativeContextService,
} from '../../services/creative-context.service';
import { RecipeDraftService, RequestRecipeDraftRequest } from '../../services/recipe-draft.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeConceptStudioComponent } from './recipe-concept-studio.component';
import { RecipeStudioHost } from './recipe-studio-host';

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

  requestConcepts(_slug: string, request: RequestRecipeConceptsRequest, key: string): Promise<RequestConceptsOutcome> {
    this.requestCalls.push({ request, key });
    return Promise.resolve(this.requestOutcome);
  }
}

/** Answers a draft request with {@link outcome}, or holds it open on {@link pending} when a test needs to. */
class StubRecipeDraftService {
  outcome: AiRequestOutcome = {
    status: 'accepted',
    operation: operation({ aiProposalRequestId: 'draft-1', taskType: 'RecipeFirstDraft' }),
    replayed: false,
  };
  pending: Promise<AiRequestOutcome> | null = null;
  calls: { request: RequestRecipeDraftRequest; key: string }[] = [];

  requestDraft(_slug: string, request: RequestRecipeDraftRequest, key: string): Promise<AiRequestOutcome> {
    this.calls.push({ request, key });
    return this.pending ?? Promise.resolve(this.outcome);
  }
}

/** Answers a context create with {@link outcome}, recording what it was asked to start from. */
class StubCreativeContextService {
  outcome: CreativeContextCreateOutcome = {
    status: 'created',
    context: {
      id: 'ctx-1',
      workingTitle: null,
      pictureBrief: null,
      briefSource: null,
      workingBrief: null,
      channelKeys: [],
      day: null,
      weeklyThemeKey: null,
      references: [],
      createdAt: '2026-10-09T12:00:00+00:00',
      updatedAt: '2026-10-09T12:00:00+00:00',
      archivedAt: null,
      concurrencyToken: 'AAAAAAAAB9E=',
    },
  };
  calls: { slug: string; draft: CreativeContextDraft; key: string }[] = [];

  /** What a read of the work the studio was opened for answers. Not found unless a test says otherwise. */
  readOutcome: CreativeContextReadOutcome = { status: 'not_found' };
  reads: { slug: string; contextId: string }[] = [];

  create(slug: string, draft: CreativeContextDraft, key: string): Observable<CreativeContextCreateOutcome> {
    this.calls.push({ slug, draft, key });

    return of(this.outcome);
  }

  get(slug: string, contextId: string): Observable<CreativeContextReadOutcome> {
    this.reads.push({ slug, contextId });

    return of(this.readOutcome);
  }
}

describe('RecipeConceptStudioComponent', () => {
  let service: StubRecipeConceptService;
  let drafts: StubRecipeDraftService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeConceptStudioComponent>;
  let contexts: StubCreativeContextService;

  /** The work in the address, for a studio opened for one (AF.7.4). Null is the studio opened for none. */
  let routeContextId: string | null = null;

  /** The guided step the studio sits in, for the next render; null for the studio on its own page. */
  let host: RecipeStudioHost | null = null;
  let readOutcome: CreativeContextReadOutcome = { status: 'not_found' };

  function configure(role: WorkspaceRole = 'Contributor'): void {
    service = new StubRecipeConceptService();
    drafts = new StubRecipeDraftService();
    contexts = new StubCreativeContextService();
    contexts.readOutcome = readOutcome;
    memberships = new StubMembershipService();
    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeConceptService, useValue: service },
        { provide: RecipeDraftService, useValue: drafts },
        { provide: CreativeContextService, useValue: contexts },
        { provide: WorkspaceMembershipService, useValue: memberships },
        {
          provide: ActivatedRoute,
          useValue: {
            parent: null,
            snapshot: {
              paramMap: convertToParamMap(
                routeContextId === null
                  ? { workspaceSlug: 'cozy-fall' }
                  : { workspaceSlug: 'cozy-fall', contextId: routeContextId },
              ),
              queryParamMap: convertToParamMap({}),
            },
          },
        },
        {
          provide: Router,
          useValue: { navigate: jasmine.createSpy('navigate').and.returnValue(Promise.resolve(true)) },
        },
        ...(host ? [{ provide: RecipeStudioHost, useValue: host }] : []),
      ],
    });

    fixture = TestBed.createComponent(RecipeConceptStudioComponent);
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

  // aria-disabled rather than the native attribute, so a disabled button never drops focus mid-press.
  function isDisabled(text: string): boolean {
    return buttonWith(text).getAttribute('aria-disabled') === 'true';
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
    expect(isDisabled('Get concepts')).toBeTrue();
  }));

  it('leaves the form enabled for a Contributor', fakeAsync(() => {
    render('Contributor');

    expect(element().querySelector('[role="alert"]')).toBeNull();
    expect(isDisabled('Get concepts')).toBeFalse();
  }));

  // ---- Errors ----

  // ---- Opened for a piece of work (AF.7.4) ----

  afterEach(() => {
    routeContextId = null;
    readOutcome = { status: 'not_found' };
  });

  it('reads no piece of work and names none on the request when opened for none', fakeAsync(() => {
    render();
    typeInto('concept-cuisine', 'Sichuan');

    click('Get concepts');

    expect(contexts.reads.length).toBe(0);
    expect('creativeContextId' in service.requestCalls[0].request).toBeFalse();
    expect('applyDayNotes' in service.requestCalls[0].request).toBeFalse();
    discardPeriodicTasks();
  }));

  it('says which work the ideas are for, and names it on the request so they follow its themed day', fakeAsync(() => {
    routeContextId = 'ctx-9';
    readOutcome = {
      status: 'found',
      context: {
        id: 'ctx-9',
        workingTitle: 'Monday supper',
        pictureBrief: null,
        briefSource: null,
        workingBrief: null,
        channelKeys: [],
        day: 'Monday',
        weeklyThemeKey: null,
        references: [],
        createdAt: '2026-10-09T12:00:00+00:00',
        updatedAt: '2026-10-09T12:00:00+00:00',
        archivedAt: null,
        concurrencyToken: 'AAAAAAAAB9E=',
      },
    };

    const host = render();

    expect(contexts.reads).toEqual([{ slug: 'cozy-fall', contextId: 'ctx-9' }]);
    expect(host.querySelector('.studio-work')?.textContent).toContain('Monday supper');

    typeInto('concept-cuisine', 'Sichuan');
    click('Get concepts');

    expect(service.requestCalls[0].request.creativeContextId).toBe('ctx-9');
    // The notes are followed unless the creator says otherwise, so the switch is not on the request.
    expect('applyDayNotes' in service.requestCalls[0].request).toBeFalse();
    discardPeriodicTasks();
  }));

  it('still asks for ideas, without the work, when the work in the address cannot be read', fakeAsync(() => {
    routeContextId = 'someone-elses';
    readOutcome = { status: 'not_found' };

    const host = render();

    expect(host.textContent).toContain("couldn't be read");
    expect(host.querySelector('.studio-work')).toBeNull();

    typeInto('concept-cuisine', 'Sichuan');
    click('Get concepts');

    // Never named on a request: the server would refuse it, and the creator has been told it is not in play.
    expect(service.requestCalls.length).toBe(1);
    expect('creativeContextId' in service.requestCalls[0].request).toBeFalse();
    discardPeriodicTasks();
  }));

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

  it('submits the recipe name, which is what the concepts are variations on', fakeAsync(() => {
    render();
    typeInto('concept-dish-name', 'Fattoush Salad with Radishes and Grilled Chicken Shawarma');

    click('Get concepts');

    expect(service.requestCalls[0].request.dishName).toBe('Fattoush Salad with Radishes and Grilled Chicken Shawarma');
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

    expect(fixture.componentInstance.selectedConcept()?.title).toBe('Slow-roasted tomato soup');
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
    expect(element().querySelector('cp-ai-operation-status p.cp-sr-only[role="status"]')?.textContent?.trim()).toBe('');
  }));

  // ---- Page frame and results frame ----

  it('names the page once, in the page head, and nowhere else', fakeAsync(() => {
    render();

    // The page head prints the one h1 from the route's data.title. The h2s on this page name the brief's own
    // parts; none of them repeats the page's name.
    expect(element().querySelectorAll('h1').length).toBe(1);
    expect(element().querySelector('cp-page-head h1')).toBeTruthy();

    const headings = Array.from(element().querySelectorAll('h2')).map((h) => h.textContent?.trim());
    expect(headings).toEqual(['The dish', "Who it's for", 'Ingredients and limits', 'Your voice']);
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
    expect(element().querySelector('ul[cpCardGrid]')).toBeNull();
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
    expect(fields.length).toBe(12);
    expect(fields.every((field) => field.closest('cp-form-section') !== null)).toBeTrue();
  }));

  // ---- Drafting the chosen concept (AF.2.1) ----

  // ---- The concept grid (AU.5.1) ----

  function richConcept(): readonly AiProposedChange[] {
    return [
      change({
        changeId: 'add-1',
        targetId: 'concept-1',
        afterValue: 'Maple-butter overnight rolls',
        proposedPosition: 0,
      }),
      setRow('summary', 'Soft rolls brushed with maple butter.'),
      setRow('timeBudgetNote', 'Overnight rise, then a morning window.'),
      setRow('skillLevelFit', 'Weekend home bakers.'),
      setRow(
        'dietaryNotes',
        'Contains wheat and dairy based on the provided components.; Dietary goals were not specified.',
      ),
      setRow('assumptions', 'The starter is active.; Rolls are small portions.'),
      setRow('suggestedIngredients', 'bread flour; maple syrup; butter'),
      setRow('distinctnessRationale', 'The only one finished in the pan.'),
    ];
  }

  function optionCards(): HTMLElement[] {
    return Array.from(element().querySelectorAll<HTMLElement>('cp-option-card'));
  }

  it('compares its concepts as option cards inside one generated surface, named by what made them', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
    click('Get concepts');

    const generated = element().querySelector('cp-list-shell cp-generated');
    expect(generated).not.toBeNull();
    const label = generated!.querySelector('cp-generated-label');
    expect(label?.textContent).toContain('Drafted by AI');
    expect(label?.textContent).toContain('phi-4 (foundry-local)');
    // Never the prompt: its id and checksum are not this line's to show.
    expect(label?.textContent).not.toContain('recipe.concepts');

    expect(optionCards().length).toBe(2);
    expect(generated!.querySelectorAll('ul[cpCardGrid] > li > cp-option-card').length).toBe(2);
    expect(optionCards()[0].querySelector('h3')?.textContent?.trim()).toBe('Slow-roasted tomato soup');
  }));

  it('tags each concept with its time budget and skill fit, and keeps the rest behind its disclosure', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(richConcept()) })];
    click('Get concepts');

    const card = optionCards()[0];
    const tags = Array.from(card.querySelectorAll('.tags .tag')).map((tag) => [
      tag.querySelector('dt')?.textContent?.trim(),
      tag.querySelector('dd')?.textContent?.trim(),
    ]);
    expect(tags).toEqual([
      ['Time budget', 'Overnight rise, then a morning window.'],
      ['Skill fit', 'Weekend home bakers.'],
    ]);
    expect(card.querySelector('.summary')?.textContent).toContain('Soft rolls brushed with maple butter.');

    const detail = card.querySelector('details')!;
    expect(detail.querySelector('summary')?.textContent).toContain('and what it assumes');
    expect(detail.textContent).toContain('The only one finished in the pan.');
    // A list of words is still joined with commas.
    expect(detail.textContent).toContain('bread flour, maple syrup, butter');
    // A list of sentences is a list, never "portions., The" (AU.0).
    const assumptions = Array.from(detail.querySelectorAll('.concept-sentences li')).map((li) =>
      li.textContent?.trim(),
    );
    expect(assumptions).toEqual(['The starter is active.', 'Rolls are small portions.']);
    expect(card.textContent).not.toContain('., ');
  }));

  it('shows every dietary note as a sentence of its own, always open, under its caveat', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(richConcept()) })];
    click('Get concepts');

    const card = optionCards()[0];
    const fact = Array.from(card.querySelectorAll<HTMLElement>('[cpFact]')).find((el) =>
      el.querySelector('dt')?.textContent?.includes('Dietary notes'),
    )!;
    expect(fact).toBeTruthy();
    // Not behind the disclosure, and the caveat is the term itself, in this page's words.
    expect(fact.closest('details')).toBeNull();
    expect(fact.querySelector('dt')?.textContent).toContain('descriptive only, not a guarantee');
    // The caveat is this page's term, never one of the model's sentences, so no refactor can lose it among them.
    expect(fact.querySelector('dd')?.textContent).not.toContain('guarantee');
    const notes = Array.from(fact.querySelectorAll('dd li')).map((li) => li.textContent?.trim());
    expect(notes).toEqual([
      'Contains wheat and dairy based on the provided components.',
      'Dietary goals were not specified.',
    ]);
  }));

  it("keeps a concept's own cautions on its card, never behind the disclosure", fakeAsync(() => {
    render();
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal(twoConcepts(), [
          { kind: 'SafetyCaution', message: 'Cool the soup quickly.', changeId: 'add-1' },
        ]),
      }),
    ];
    click('Get concepts');

    const notice = optionCards()[0].querySelector('cp-notice');
    expect(notice?.textContent).toContain('Cool the soup quickly.');
    expect(notice?.closest('details')).toBeNull();
    expect(notice?.classList).toContain('cp-notice--warning');
    expect(optionCards()[1].querySelector('cp-notice')).toBeNull();
  }));

  it('marks the chosen card as chosen, by its edge, a ✓ and the word', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
    click('Get concepts');
    click('Choose this concept');

    const [first, second] = optionCards();
    expect(first.querySelector('cp-card')?.classList).toContain('selected');
    expect(first.querySelector('.chosen')?.textContent).toContain('Chosen');
    expect(second.querySelector('.chosen')).toBeNull();
  }));

  it('says what the model flagged as notices of their own, after the concepts', fakeAsync(() => {
    render();
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal(twoConcepts(), [
          { kind: 'Assumption', message: 'The dish name was the only ingredient spec.', changeId: null },
          { kind: 'SafetyCaution', message: 'Check the starter is active.', changeId: null },
        ]),
      }),
    ];
    click('Get concepts');

    const flags = element().querySelector('section.studio-flags')!;
    expect(flags.querySelector('h3')?.textContent).toContain('What the model flagged');
    expect(flags.querySelector('cp-topic-card')).toBeNull();
    const notices = Array.from(flags.querySelectorAll('cp-notice'));
    expect(notices.length).toBe(2);
    expect(notices[0].textContent).toContain('The dish name was the only ingredient spec.');
    expect(notices[1].textContent).toContain('Check the starter is active.');
    expect(notices[1].classList).toContain('cp-notice--warning');
    // Its own place: not inside a concept, and not inside the frame.
    expect(flags.closest('cp-list-shell')).toBeNull();
  }));

  it("still shows the model's flags when no concepts came back, since a flag is often why", fakeAsync(() => {
    render();
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([], [{ kind: 'Unspecified', message: 'The brief was empty.', changeId: null }]),
      }),
    ];
    click('Get concepts');

    expect(element().querySelector('section.studio-flags')?.textContent).toContain('The brief was empty.');
    expect(element().querySelector('cp-list-shell cp-empty-state cp-illustration')).not.toBeNull();
  }));

  it('offers a new brief inside the generated surface, as its action', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
    click('Get concepts');

    expect(buttonWith('Try a new brief').closest('[cpGeneratedActions]')).not.toBeNull();
    expect(buttons('Try a new brief').length).toBe(1);
  }));

  it("wears the studio's icon in its page head", fakeAsync(() => {
    render();

    expect(element().querySelector('cp-page-head cp-shell-nav-icon[cpPageHeadIcon]')).not.toBeNull();
  }));

  it('shows an illustrated first use where the concepts will land, before anything is asked', fakeAsync(() => {
    render();

    const empty = element().querySelector('cp-empty-state.studio-first-use');
    expect(empty).not.toBeNull();
    expect(empty!.querySelector('cp-illustration')).not.toBeNull();

    service.statuses = [found({ status: 'Running' })];
    click('Get concepts');
    expect(element().querySelector('cp-empty-state.studio-first-use')).toBeNull();
    discardPeriodicTasks();
  }));

  it('leaves its loading line empty once nothing is loading', fakeAsync(() => {
    render();
    service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
    click('Get concepts');

    expect(element().querySelector('.studio-loading-status')?.textContent?.trim()).toBe('');
    expect(element().querySelector('.studio-loading')).toBeNull();
  }));

  describe('drafting the chosen concept', () => {
    function draftNavigations(): unknown[][] {
      const navigate = TestBed.inject(Router).navigate as jasmine.Spy;

      return navigate.calls
        .allArgs()
        .filter((args) => Array.isArray(args[0]) && (args[0] as unknown[]).includes('draft'));
    }

    /** A brief typed, concepts returned, the first one chosen. */
    function chooseFirst(): void {
      render();
      typeInto('concept-dish-name', 'Fattoush Salad with Radishes');
      typeInto('concept-audience', 'Busy parents');
      service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
      click('Get concepts');
      buttons('Choose this concept')[0].click();
      settle();
    }

    function draftButton(): HTMLButtonElement {
      const found = element().querySelector<HTMLButtonElement>('button.studio-draft');
      if (!found) throw new Error('no draft button');
      return found;
    }

    function pressDraft(): void {
      draftButton().click();
      settle();
      settle();
    }

    /** Everything a refusal must leave exactly as it was. */
    function expectNothingLost(): void {
      expect(draftNavigations()).toEqual([]);
      expect(input('concept-audience').value).toBe('Busy parents');
      expect(element().textContent).toContain('Slow-roasted tomato soup');
      expect(element().textContent).toContain('Quick weeknight noodles');
      expect(fixture.componentInstance.selectedConcept()?.title).toBe('Slow-roasted tomato soup');
      expect(draftButton().getAttribute('aria-disabled')).toBeNull();
      expect(draftButton().textContent?.trim()).toBe('Draft this recipe');
    }

    it('offers nothing to draft until a concept is chosen', fakeAsync(() => {
      render();
      service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
      click('Get concepts');

      expect(element().querySelector('button.studio-draft')).toBeNull();

      buttons('Choose this concept')[0].click();
      settle();

      expect(draftButton().textContent?.trim()).toBe('Draft this recipe');
      expect(draftButton().classList).toContain('cp-button--primary');
    }));

    it('no longer says drafting is not built', fakeAsync(() => {
      chooseFirst();

      expect(element().textContent).not.toContain("isn't built yet");
      expect(element().querySelector('.studio-selection [role="status"]')?.textContent).toContain(
        'Slow-roasted tomato soup',
      );
    }));

    it('asks for a draft of the chosen concept, with the brief, and opens the page that reviews it', fakeAsync(() => {
      chooseFirst();

      pressDraft();

      expect(drafts.calls.length).toBe(1);
      expect(drafts.calls[0].request.sourceConceptRequestId).toBe(REQUEST_ID);
      expect(drafts.calls[0].request.sourceConceptId).toBe('concept-1');
      expect(drafts.calls[0].request.brief?.audience).toBe('Busy parents');
      // The name goes with it, so the draft is the dish that was pitched rather than one inferred afresh.
      expect(drafts.calls[0].request.brief?.dishName).toBe('Fattoush Salad with Radishes');

      // The plain review route, named by the request it will watch. No recipe exists yet.
      expect(draftNavigations()).toEqual([
        [['/', 'cozy-fall', 'ai-recipe-studio', 'draft'], { queryParams: { request: 'draft-1' } }],
      ]);
    }));

    it('drafts the concept that is chosen now, not the one chosen first', fakeAsync(() => {
      chooseFirst();
      buttons('Choose this concept')[0].click();
      settle();

      pressDraft();

      expect(drafts.calls[0].request.sourceConceptId).toBe('concept-2');
    }));

    it('says it is working, keeps focus, and sends one request however often it is pressed', fakeAsync(() => {
      chooseFirst();
      let answer: (outcome: AiRequestOutcome) => void = () => undefined;
      drafts.pending = new Promise<AiRequestOutcome>((resolve) => (answer = resolve));

      draftButton().focus();
      draftButton().click();
      settle();
      draftButton().click();
      draftButton().click();
      settle();

      expect(drafts.calls.length).toBe(1);
      expect(draftButton().textContent?.trim()).toBe('Starting your draft…');
      expect(draftButton().getAttribute('aria-disabled')).toBe('true');

      // The visible words shorten; the name a screen reader hears still says which concept.
      expect(draftButton().getAttribute('aria-label')).toContain('Slow-roasted tomato soup');

      // aria-disabled, not disabled: the button is still there to hold focus.
      expect(draftButton().disabled).toBeFalse();
      expect(document.activeElement).toBe(draftButton());

      answer({ status: 'unavailable' });
      settle();
      settle();

      expect(document.activeElement).toBe(draftButton());
    }));

    const refusals: readonly (readonly [AiRequestOutcome, string])[] = [
      [{ status: 'refused', code: 'ai.recipeConcept.not_found', message: 'x' }, 'can no longer be drafted from'],
      [{ status: 'refused', code: 'ai.recipeFirstDraft.too_large', message: 'x' }, 'too long to draft from together'],
      [{ status: 'task_not_enabled' }, 'switched off for now'],
      [{ status: 'idempotency_key_conflict' }, 'clashed with another'],
      [{ status: 'forbidden' }, 'does not permit asking for a recipe draft'],
      [{ status: 'unavailable' }, "Couldn't reach the server"],
      [
        { status: 'validation_failed', fieldErrors: { audience: ['Too long.'] } },
        'needs changing before this can be drafted',
      ],
    ];

    for (const [outcome, words] of refusals) {
      const name = outcome.status === 'refused' ? `refused (${outcome.code})` : outcome.status;

      it(`stays put and says why on '${name}', with the brief, the concepts and the choice untouched`, fakeAsync(() => {
        chooseFirst();
        drafts.outcome = outcome;

        pressDraft();

        const alert = element().querySelector('.studio-selection [role="alert"]');
        expect(alert?.textContent).toContain(words);
        expectNothingLost();
      }));
    }

    it('shows a field error from the server against its own field in the brief', fakeAsync(() => {
      chooseFirst();
      drafts.outcome = { status: 'validation_failed', fieldErrors: { audience: ['Keep this under 200 characters.'] } };

      pressDraft();

      expect(element().textContent).toContain('Keep this under 200 characters.');
      expect(input('concept-audience').getAttribute('aria-invalid')).toBe('true');
    }));

    for (const refusal of [
      {
        status: 'quota_exhausted',
        unit: 'Credits',
        allowance: 100,
        remaining: 2,
        required: 5,
        resetsAt: '2026-11-01T00:00:00+00:00',
      },
      { status: 'account_suspended', unit: 'Credits' },
    ] as const) {
      it(`hands '${refusal.status}' to the allowance notice by the action, and loses nothing`, fakeAsync(() => {
        chooseFirst();
        drafts.outcome = refusal as AiRequestOutcome;

        pressDraft();

        // Worded by the one component that words allowance refusals, beside the action that was refused.
        const notice = element().querySelector('.studio-selection cp-ai-allowance-notice');
        expect(notice).not.toBeNull();
        expect(notice!.textContent?.trim()).not.toBe('');
        expect(element().querySelector('.studio-selection cp-notice[role="alert"]')).toBeNull();
        expect(draftNavigations()).toEqual([]);
        expect(input('concept-audience').value).toBe('Busy parents');
        expect(fixture.componentInstance.selectedConcept()?.title).toBe('Slow-roasted tomato soup');
        expect(element().textContent).toContain('Quick weeknight noodles');
      }));
    }

    it('retries under the same key when the server could not be reached, so one draft is bought', fakeAsync(() => {
      chooseFirst();
      drafts.outcome = { status: 'unavailable' };

      pressDraft();
      pressDraft();

      expect(drafts.calls.length).toBe(2);
      expect(drafts.calls[1].key).toBe(drafts.calls[0].key);
    }));

    it('takes a new key once the server has given a definite answer', fakeAsync(() => {
      chooseFirst();
      drafts.outcome = { status: 'task_not_enabled' };

      pressDraft();
      pressDraft();

      expect(drafts.calls[1].key).not.toBe(drafts.calls[0].key);
    }));

    it('takes a new key for a different concept, even while the first is unanswered', fakeAsync(() => {
      chooseFirst();
      drafts.outcome = { status: 'unavailable' };
      pressDraft();

      // The other concept. Its request is a different request: the first key must not be spent on it.
      buttons('Choose this concept')[0].click();
      settle();
      pressDraft();

      expect(drafts.calls[1].request.sourceConceptId).toBe('concept-2');
      expect(drafts.calls[1].key).not.toBe(drafts.calls[0].key);
    }));

    it('clears what the last concept was refused for when another is chosen', fakeAsync(() => {
      chooseFirst();
      drafts.outcome = { status: 'task_not_enabled' };
      pressDraft();
      expect(element().querySelector('.studio-selection [role="alert"]')).not.toBeNull();

      buttons('Choose this concept')[0].click();
      settle();

      expect(element().querySelector('.studio-selection [role="alert"]')).toBeNull();
    }));

    it('clears the message as soon as the same concept is tried again', fakeAsync(() => {
      chooseFirst();
      drafts.outcome = { status: 'unavailable' };
      pressDraft();

      let answer: (outcome: AiRequestOutcome) => void = () => undefined;
      drafts.pending = new Promise<AiRequestOutcome>((resolve) => (answer = resolve));
      draftButton().click();
      settle();

      expect(element().querySelector('.studio-selection [role="alert"]')).toBeNull();

      answer({ status: 'unavailable' });
      settle();
      settle();
    }));

    it('does not let a role that cannot ask press it', fakeAsync(() => {
      chooseFirst();
      memberships.state.set({ status: 'ready', memberships: [membership('Viewer')] });
      fixture.detectChanges();

      expect(draftButton().getAttribute('aria-disabled')).toBe('true');

      pressDraft();

      expect(drafts.calls.length).toBe(0);
    }));
  });

  // ---- Taking the chosen concept elsewhere (AF.2.3) ----

  describe('using the chosen concept elsewhere', () => {
    function chooseFirst(): void {
      render();
      typeInto('concept-dish-name', 'Fattoush Salad with Radishes');
      typeInto('concept-audience', 'Busy parents');
      service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
      click('Get concepts');
      buttons('Choose this concept')[0].click();
      settle();
    }

    function handoff(): HTMLElement | null {
      return element().querySelector<HTMLElement>('cp-use-this-in');
    }

    function handoffButtons(): HTMLButtonElement[] {
      return Array.from(handoff()?.querySelectorAll<HTMLButtonElement>('button') ?? []);
    }

    function contextNavigations(): unknown[][] {
      const navigate = TestBed.inject(Router).navigate as jasmine.Spy;

      return navigate.calls
        .allArgs()
        .filter((args) => Array.isArray(args[0]) && (args[0] as unknown[]).includes('context'));
    }

    it('offers nowhere to take a concept until one is chosen', fakeAsync(() => {
      render();
      service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
      click('Get concepts');

      expect(handoff()).toBeNull();
    }));

    it('offers a picture, a content run and posts, in that order, under their own heading', fakeAsync(() => {
      chooseFirst();

      expect(handoff()!.querySelector('.heading')?.textContent).toBe('Or use this concept in…');
      expect(handoffButtons().map((button) => button.textContent?.trim())).toEqual([
        'Make a picture',
        'Start a content run',
        'Write posts',
      ]);
    }));

    it('keeps drafting the recipe as the one primary action, ahead of the others', fakeAsync(() => {
      chooseFirst();

      const selection = element().querySelector('.studio-selection')!;
      const primaries = Array.from(selection.querySelectorAll('button.cp-button--primary'));

      expect(primaries.map((button) => button.textContent?.trim())).toEqual(['Draft this recipe']);
      expect(handoffButtons().every((button) => button.classList.contains('cp-button--secondary'))).toBeTrue();

      // First in the region, so it is first in reading order and first in the tab order.
      const all = Array.from(selection.querySelectorAll('button'));
      expect(all[0].textContent?.trim()).toBe('Draft this recipe');
      expect(all.slice(1).map((button) => button.textContent?.trim())).toEqual([
        'Make a picture',
        'Start a content run',
        'Write posts',
      ]);
    }));

    it('hands over the chosen concept by id, and carries only what the destination asks for', fakeAsync(() => {
      chooseFirst();

      handoffButtons()[0].click();
      settle();
      settle();

      expect(contexts.calls.length).toBe(1);
      expect(contexts.calls[0].slug).toBe('cozy-fall');

      // The request the concept came from and the concept inside it — plus the working title, because both
      // destinations open on a "what it is called" field and arriving at an empty one, having named the dish
      // a moment ago, is the seam a creator feels. Nothing else travels: no audience, no summary, no
      // ingredients. The context points at the concept for all of that.
      expect(contexts.calls[0].draft).toEqual({
        workingTitle: 'Fattoush Salad with Radishes',
        from: { kind: 'RecipeConcept', conceptRequestId: REQUEST_ID, conceptId: 'concept-1' },
      });
    }));

    it("prefers the creator's own name for the dish over the model's wording for one variation", fakeAsync(() => {
      chooseFirst();

      handoffButtons()[0].click();
      settle();
      settle();

      // They typed this; "Slow-roasted tomato soup" is the concept's title. The concept is not lost — the
      // context names it — so the field they will edit starts from their words.
      expect(contexts.calls[0].draft.workingTitle).toBe('Fattoush Salad with Radishes');
    }));

    it('falls back to the chosen concepts title when the creator named no dish', fakeAsync(() => {
      render();
      typeInto('concept-audience', 'Busy parents');
      service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
      click('Get concepts');
      buttons('Choose this concept')[0].click();
      settle();

      handoffButtons()[0].click();
      settle();
      settle();

      // With nothing typed, the concept's title is the only thing that says what the work is about, and a
      // title they can reword beats an empty box.
      expect(contexts.calls[0].draft.workingTitle).toBe('Slow-roasted tomato soup');
    }));

    for (const [index, label, route] of [
      [0, 'Make a picture', ['/', 'cozy-fall', 'image-studio', 'context', 'ctx-1']],
      [1, 'Start a content run', ['/', 'cozy-fall', 'workflows', 'content-pipeline', 'context', 'ctx-1']],
    ] as const) {
      it(`opens '${label}' with the new contexts id in the route`, fakeAsync(() => {
        chooseFirst();

        handoffButtons()[index].click();
        settle();
        settle();

        expect(contextNavigations()).toEqual([[[...route]]]);

        // And no draft was asked for: these two never spend the allowance.
        expect(drafts.calls.length).toBe(0);
      }));
    }

    it('hands over whichever concept is chosen now', fakeAsync(() => {
      chooseFirst();
      buttons('Choose this concept')[0].click();
      settle();

      handoffButtons()[0].click();
      settle();
      settle();

      expect(contexts.calls[0].draft.from).toEqual({
        kind: 'RecipeConcept',
        conceptRequestId: REQUEST_ID,
        conceptId: 'concept-2',
      });
    }));

    it('stays in the studio with everything intact when the context cannot be started', fakeAsync(() => {
      chooseFirst();
      contexts.outcome = { status: 'unavailable' };

      handoffButtons()[0].click();
      settle();
      settle();

      expect(contextNavigations()).toEqual([]);
      expect(handoff()!.querySelector('[role="alert"]')?.textContent).toContain('could not be started');
      expect(input('concept-audience').value).toBe('Busy parents');
      expect(element().textContent).toContain('Quick weeknight noodles');
      expect(fixture.componentInstance.selectedConcept()?.title).toBe('Slow-roasted tomato soup');
    }));

    it('holds the other two while a draft is being asked for, so two requests are not on their way at once', fakeAsync(() => {
      chooseFirst();
      let answer: (outcome: AiRequestOutcome) => void = () => undefined;
      drafts.pending = new Promise<AiRequestOutcome>((resolve) => (answer = resolve));

      element().querySelector<HTMLButtonElement>('button.studio-draft')!.click();
      settle();

      expect(handoffButtons().every((button) => button.getAttribute('aria-disabled') === 'true')).toBeTrue();

      handoffButtons()[0].click();
      settle();
      expect(contexts.calls.length).toBe(0);

      answer({ status: 'unavailable' });
      settle();
      settle();

      expect(handoffButtons().every((button) => button.getAttribute('aria-disabled') === null)).toBeTrue();
    }));

    it('no longer publishes a selection for a host to act on', fakeAsync(() => {
      chooseFirst();

      // The studio acts on a chosen concept itself now. An output nothing consumes is a promise nobody keeps.
      expect('conceptSelected' in fixture.componentInstance).toBeFalse();
    }));
  });

  // ---- Hosted in a guided step (13A.14) ----

  describe('in a guided step', () => {
    let conceptsRequested: jasmine.Spy;
    let draftRequested: jasmine.Spy;

    beforeEach(() => {
      host = new RecipeStudioHost();
      host.workspaceSlug = 'cozy-fall';
      conceptsRequested = jasmine.createSpy('conceptsRequested');
      draftRequested = jasmine.createSpy('draftRequested');
      host.conceptsRequested = conceptsRequested;
      host.draftRequested = draftRequested;
    });

    afterEach(() => {
      host = null;
    });

    function navigations(): readonly unknown[][] {
      return (TestBed.inject(Router).navigate as jasmine.Spy).calls.allArgs();
    }

    it('prints no page head of its own', fakeAsync(() => {
      const root = render();

      expect(root.querySelector('cp-page-head')).toBeNull();
      expect(root.querySelector('#concept-dish-name')).not.toBeNull();
    }));

    it('hands the request it asked for to the host instead of writing it into the address', fakeAsync(() => {
      render();
      typeInto('concept-dish-name', 'Fattoush');
      click('Get concepts');

      expect(conceptsRequested).toHaveBeenCalledOnceWith(REQUEST_ID);
      expect(navigations()).toEqual([]);
    }));

    it('resumes on the request the host names', fakeAsync(() => {
      host!.conceptRequestId = 'op-kept';
      configure();
      service.statuses = [
        found({ aiProposalRequestId: 'op-kept', status: 'Proposed', proposal: proposal(twoConcepts()) }),
      ];
      fixture.detectChanges();
      settle();
      settle();

      expect(fixture.componentInstance.requestId()).toBe('op-kept');
      expect(element().textContent).toContain('Slow-roasted tomato soup');
    }));

    it('draws the shape of the concepts while it looks for the request it resumes on, and says so offscreen', fakeAsync(() => {
      host!.conceptRequestId = 'op-kept';
      configure();
      // A first poll that never answers leaves the request "loading".
      service.watchStatus = () => new Observable<WatchConceptsOutcome>(() => undefined);
      fixture.detectChanges();
      settle();

      expect(fixture.componentInstance.state()).toBe('loading');
      const loading = element().querySelector('.studio-loading')!;
      expect(loading.getAttribute('aria-busy')).toBe('true');
      expect(loading.querySelector('cp-skeleton')).not.toBeNull();
      expect(element().querySelector('.studio-loading-status')?.textContent).toContain('Looking for your request');
      discardPeriodicTasks();
    }));

    // ---- One part at a time, for the studio's own flow (AU.5.2) ----

    it('shows only the brief on the flow’s first step, and only what came back on its second', fakeAsync(() => {
      host!.part.set('brief');
      render();

      expect(element().querySelector('.brief-card')).not.toBeNull();
      expect(element().querySelector('cp-list-shell')).toBeNull();
      expect(element().querySelector('cp-empty-state.studio-first-use')).toBeNull();

      host!.part.set('concepts');
      fixture.detectChanges();

      expect(element().querySelector('.brief-card')).toBeNull();
      // Nothing asked for yet: the concepts step says where they will land.
      expect(element().querySelector('cp-empty-state.studio-first-use')).not.toBeNull();
    }));

    it('keeps what was typed in the brief while the concepts step shows', fakeAsync(() => {
      host!.part.set('brief');
      render();
      fixture.componentInstance.dishName.set('Soda bread');

      host!.part.set('concepts');
      fixture.detectChanges();
      host!.part.set('brief');
      fixture.detectChanges();
      settle();

      expect(input('concept-dish-name').value).toBe('Soda bread');
    }));

    it('names the model on the studio’s own flow, which is the studio’s page', fakeAsync(() => {
      host!.showMaker = true;
      host!.conceptRequestId = 'op-kept';
      configure();
      service.statuses = [
        found({ aiProposalRequestId: 'op-kept', status: 'Proposed', proposal: proposal(twoConcepts()) }),
      ];
      fixture.detectChanges();
      settle();
      settle();

      expect(element().querySelector('cp-generated cp-generated-label')?.textContent).toContain(
        'phi-4 (foundry-local)',
      );
    }));

    it('tells the host when a new brief is asked for, so the flow can go back to it', fakeAsync(() => {
      const restarted = jasmine.createSpy('briefRestarted');
      host!.briefRestarted = restarted;
      render();
      service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
      click('Get concepts');

      click('Try a new brief');

      expect(restarted).toHaveBeenCalledTimes(1);
    }));

    it('reads the piece of work from the host, as the address would name it', fakeAsync(() => {
      readOutcome = { status: 'not_found' };
      host!.contextId = 'ctx-from-host';
      render();

      expect(contexts.reads.map((read) => read.contextId)).toContain('ctx-from-host');
    }));

    it('still marks the concepts as drafted by AI in a guided step, without naming the model', fakeAsync(() => {
      host!.conceptRequestId = 'op-kept';
      configure();
      service.statuses = [
        found({ aiProposalRequestId: 'op-kept', status: 'Proposed', proposal: proposal(twoConcepts()) }),
      ];
      fixture.detectChanges();
      settle();
      settle();

      const label = element().querySelector('cp-generated cp-generated-label');
      expect(label?.textContent).toContain('Drafted by AI');
      expect(label?.textContent).not.toContain('phi-4');
    }));

    it('hands the draft it asked for to the host instead of opening the review page', fakeAsync(() => {
      render();
      service.statuses = [found({ status: 'Proposed', proposal: proposal(twoConcepts()) })];
      click('Get concepts');
      buttons('Choose this concept')[0].click();
      settle();

      element().querySelector<HTMLButtonElement>('button.studio-draft')!.click();
      settle();
      settle();

      expect(draftRequested).toHaveBeenCalledOnceWith('draft-1');
      expect(navigations()).toEqual([]);
    }));
  });
});
