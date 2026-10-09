import { signal } from '@angular/core';
import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Observable, of } from 'rxjs';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { AiProposalDetail, AiProposalStatus, AiProposalWarning, AiProposedChange } from '../../models/ai-proposal.models';
import { RequestRecipeConceptsRequest } from '../../models/recipe-concept.models';
import { AiRequestOutcome } from '../../services/ai-request';
import { RecipeConceptService, RequestConceptsOutcome, WatchConceptsOutcome } from '../../services/recipe-concept.service';
import { CreativeContextDraft } from '../../models/creative-context.models';
import { CreativeContextCreateOutcome, CreativeContextService } from '../../services/creative-context.service';
import { RecipeDraftService, RequestRecipeDraftRequest } from '../../services/recipe-draft.service';
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

  create(slug: string, draft: CreativeContextDraft, key: string): Observable<CreativeContextCreateOutcome> {
    this.calls.push({ slug, draft, key });

    return of(this.outcome);
  }
}

describe('RecipeConceptStudioComponent', () => {
  let service: StubRecipeConceptService;
  let drafts: StubRecipeDraftService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeConceptStudioComponent>;
  let contexts: StubCreativeContextService;

  function configure(role: WorkspaceRole = 'Contributor'): void {
    service = new StubRecipeConceptService();
    drafts = new StubRecipeDraftService();
    contexts = new StubCreativeContextService();
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
            snapshot: { paramMap: convertToParamMap({ workspaceSlug: 'cozy-fall' }), queryParamMap: convertToParamMap({}) },
          },
        },
        { provide: Router, useValue: { navigate: jasmine.createSpy('navigate').and.returnValue(Promise.resolve(true)) } },
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

    expect(service.requestCalls[0].request.dishName).toBe(
      'Fattoush Salad with Radishes and Grilled Chicken Shawarma',
    );
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
    expect(fields.length).toBe(12);
    expect(fields.every((field) => field.closest('cp-form-section') !== null)).toBeTrue();
  }));

  // ---- Drafting the chosen concept (AF.2.1) ----

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
      [{ status: 'validation_failed', fieldErrors: { audience: ['Too long.'] } }, 'needs changing before this can be drafted'],
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

    it('offers a picture and a content run, in that order, under their own heading', fakeAsync(() => {
      chooseFirst();

      expect(handoff()!.querySelector('.heading')?.textContent).toBe('Or use this concept in…');
      expect(handoffButtons().map((button) => button.textContent?.trim())).toEqual([
        'Make a picture',
        'Start a content run',
      ]);
    }));

    it('keeps drafting the recipe as the one primary action, ahead of the other two', fakeAsync(() => {
      chooseFirst();

      const selection = element().querySelector('.studio-selection')!;
      const primaries = Array.from(selection.querySelectorAll('button.cp-button--primary'));

      expect(primaries.map((button) => button.textContent?.trim())).toEqual(['Draft this recipe']);
      expect(handoffButtons().every((button) => button.classList.contains('cp-button--secondary'))).toBeTrue();

      // First in the region, so it is first in reading order and first in the tab order.
      const all = Array.from(selection.querySelectorAll('button'));
      expect(all[0].textContent?.trim()).toBe('Draft this recipe');
      expect(all.slice(1).map((button) => button.textContent?.trim())).toEqual(['Make a picture', 'Start a content run']);
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
});
