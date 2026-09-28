import { ApplicationRef } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Observable } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { AiProposalDetail, AiProposalStatus, AiProposalWarning, AiProposedChange } from '../../models/ai-proposal.models';
import { RecipeDraftService, WatchRecipeDraftOutcome } from '../../services/recipe-draft.service';
import { RecipeDraftFieldEdit, RecipeFirstDraftReviewComponent } from './recipe-first-draft-review.component';

const REQUEST_ID = 'op-draft-1';

let nextChange = 0;

function change(overrides: Partial<AiProposedChange> = {}): AiProposedChange {
  return {
    changeId: `change-${nextChange++}`,
    changeKind: 'Set',
    targetKind: 'Recipe',
    targetId: null,
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function recipeField(fieldName: string, afterValue: string): AiProposedChange {
  return change({ changeKind: 'Set', targetKind: 'Recipe', targetId: null, fieldName, afterValue });
}

/** A complete enough draft: a title, one ingredient group with a line, one instruction group with a step. */
function fullDraftChanges(): AiProposedChange[] {
  return [
    recipeField('title', 'Weeknight Mapo Tofu'),
    recipeField('description', 'A quick version.'),
    recipeField('prepTimeMinutes', '10'),
    recipeField('yieldText', 'Serves 4'),
    change({ changeId: 'g1-add', changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: 'For the sauce' }),
    change({ changeId: 'l1-add', changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: '2 tbsp doubanjiang' }),
    change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'quantity', afterValue: '2' }),
    change({ changeId: 'ig1-add', changeKind: 'Add', targetKind: 'InstructionGroup', targetId: 'ig1', afterValue: null }),
    change({ changeId: 's1-add', changeKind: 'Add', targetKind: 'InstructionStep', targetId: 's1', afterValue: 'Fry the paste.' }),
    change({ changeId: 'e1-add', changeKind: 'Add', targetKind: 'Equipment', targetId: 'e1', afterValue: 'Wok' }),
  ];
}

function proposal(
  changes: readonly AiProposedChange[] = fullDraftChanges(),
  warnings: readonly AiProposalWarning[] = [],
): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: 'recipe.first-draft.v1',
    promptTemplateId: 'recipe.first-draft',
    promptTemplateVersion: '1.0.0',
    promptTemplateBodyChecksum: 'sha256:abc123',
    providerName: 'foundry-local',
    modelName: 'phi-4',
    createdAt: '2026-09-28T14:03:00Z',
    changes: [...changes],
    warnings: [...warnings],
  };
}

function operation(overrides: Partial<AiProposalStatus> = {}): AiProposalStatus {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'RecipeFirstDraft',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-09-28T14:02:00Z',
    statusChangedAt: '2026-09-28T14:02:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

function found(overrides: Partial<AiProposalStatus> = {}): WatchRecipeDraftOutcome {
  return { status: 'found', operation: operation(overrides) };
}

function ready(
  changes: readonly AiProposedChange[] = fullDraftChanges(),
  warnings: readonly AiProposalWarning[] = [],
): WatchRecipeDraftOutcome {
  return found({ status: 'Proposed', proposal: proposal(changes, warnings) });
}

/** Answers each poll in order, repeating the last, asynchronously so an unsubscribe is a cancelled read. */
class StubRecipeDraftService {
  statuses: WatchRecipeDraftOutcome[] = [found()];
  watchCalls = 0;

  watchStatus(): Observable<WatchRecipeDraftOutcome> {
    const index = this.watchCalls++;
    const outcome = this.statuses[Math.min(index, this.statuses.length - 1)];

    return new Observable<WatchRecipeDraftOutcome>((subscriber) => {
      const handle = setTimeout(() => {
        subscriber.next(outcome);
        subscriber.complete();
      }, 0);
      return () => clearTimeout(handle);
    });
  }
}

class StubConfirmService {
  answer = true;
  calls: ConfirmRequest[] = [];

  confirm(request: ConfirmRequest): Promise<boolean> {
    this.calls.push(request);
    return Promise.resolve(this.answer);
  }
}

describe('RecipeFirstDraftReviewComponent', () => {
  let service: StubRecipeDraftService;
  let confirmService: StubConfirmService;
  let fixture: ComponentFixture<RecipeFirstDraftReviewComponent>;
  let edits: RecipeDraftFieldEdit[];
  let discards: number;

  // Created here rather than in `configure`, so a test may script the polls it expects *before* rendering —
  // which is how every test below reads, and the alternative silently discarded them.
  beforeEach(() => {
    service = new StubRecipeDraftService();
    confirmService = new StubConfirmService();
  });

  function configure(queryParams: Record<string, string> = { request: REQUEST_ID }): void {
    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeDraftService, useValue: service },
        { provide: ConfirmService, useValue: confirmService },
        {
          provide: ActivatedRoute,
          useValue: {
            parent: null,
            snapshot: {
              paramMap: convertToParamMap({ workspaceSlug: 'cozy-fall' }),
              queryParamMap: convertToParamMap(queryParams),
            },
          },
        },
        { provide: Router, useValue: { navigate: jasmine.createSpy('navigate').and.returnValue(Promise.resolve(true)) } },
      ],
    });

    fixture = TestBed.createComponent(RecipeFirstDraftReviewComponent);
    edits = [];
    discards = 0;
    fixture.componentInstance.fieldEdited.subscribe((event) => edits.push(event));
    fixture.componentInstance.discarded.subscribe(() => (discards += 1));
  }

  function render(queryParams: Record<string, string> = { request: REQUEST_ID }): HTMLElement {
    configure(queryParams);
    fixture.detectChanges();
    settle();
    return element();
  }

  /**
   * Lets one pending microtask/0ms-timer land, then re-renders — the unit every interaction is built from.
   *
   * The `ApplicationRef.tick()` is what flushes `afterNextRender`, which is how this component moves focus
   * into and out of the inline editor. `fixture.detectChanges()` alone runs the component's own change
   * detection and leaves those hooks pending, so focus assertions would see `BODY` no matter what the
   * component did.
   */
  function settle(): void {
    tick(0);
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
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

  function click(label: string): void {
    buttonWith(label).click();
    settle();
  }

  /** The "Edit" button for one field, found by the hook focus restoration uses to come back to it. */
  function editButtonFor(key: string): HTMLButtonElement {
    const match = element().querySelector<HTMLButtonElement>(`button[data-edit-for="${key}"]`);
    if (!match) throw new Error(`no edit button for "${key}"`);
    return match;
  }

  // ---- states -----------------------------------------------------------------------------------------

  it('says there is nothing to review when no request is named', fakeAsync(() => {
    render({});

    expect(fixture.componentInstance.state()).toBe('idle');
    expect(text()).toContain('No draft to review');
  }));

  it('resumes from the request in the url', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    expect(fixture.componentInstance.requestId()).toBe(REQUEST_ID);
    expect(fixture.componentInstance.state()).toBe('proposed');
  }));

  it('shows a queued request as being watched', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    render();

    expect(fixture.componentInstance.state()).toBe('watching');
  }));

  /** The request resource exists from the moment it was accepted; a Proposed with no proposal is still waiting. */
  it('keeps watching a Proposed status that carries no proposal yet', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: null })];
    render();

    expect(fixture.componentInstance.state()).toBe('watching');
  }));

  // ---- the draft itself -------------------------------------------------------------------------------

  it('renders the draft as a recipe rather than a list of changes', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    expect(text()).toContain('Weeknight Mapo Tofu');
    expect(text()).toContain('2 tbsp doubanjiang');
    expect(text()).toContain('Fry the paste.');
    expect(text()).toContain('Wok');
    expect(text()).toContain('For the sauce');
  }));

  it('never says anything has been saved', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    expect(text()).toContain('No recipe has been created');
    expect(text()).toContain('nothing on this page is saved');
  }));

  it('shows a field the model did not propose as not suggested, not as a blank', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    expect(text()).toContain('Not suggested');
  }));

  /** AIREC-002's restriction: an unresolved value stays explicit. */
  it('leads with the questions the model would not guess at', fakeAsync(() => {
    service.statuses = [
      ready(fullDraftChanges(), [
        { kind: 'UnresolvedQuestion', message: 'What chili oil brand?', changeId: null },
      ]),
    ];
    render();

    expect(text()).toContain('Unresolved questions');
    expect(text()).toContain('What chili oil brand?');
    expect(fixture.componentInstance.unresolvedQuestions().length).toBe(1);
  }));

  it('keeps unresolved questions out of the ordinary warning list', fakeAsync(() => {
    service.statuses = [
      ready(fullDraftChanges(), [
        { kind: 'UnresolvedQuestion', message: 'What chili oil brand?', changeId: null },
        { kind: 'Assumption', message: 'Assumed a standard wok.', changeId: null },
      ]),
    ];
    render();

    expect(fixture.componentInstance.unresolvedQuestions().length).toBe(1);
    expect(fixture.componentInstance.warnings().length).toBe(1);
    expect(fixture.componentInstance.warnings()[0].kind).toBe('Assumption');
  }));

  /** ai.md: uncertainty is surfaced, and an unflagged draft has not been found safe. */
  it('says an unflagged draft has not been checked', fakeAsync(() => {
    service.statuses = [
      ready(fullDraftChanges(), [{ kind: 'Assumption', message: 'Assumed a standard wok.', changeId: null }]),
    ];
    render();

    expect(text()).toContain("hasn't been checked");
  }));

  it('names what is still missing before a recipe could be created', fakeAsync(() => {
    service.statuses = [ready([recipeField('description', 'No title, no lines, no steps.')])];
    render();

    expect(fixture.componentInstance.missingRequired()).toEqual(['Title', 'Ingredients', 'Method']);
    expect(text()).toContain('Not complete enough to become a recipe yet');
  }));

  it('says total time is not added up from the others', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    expect(text()).toContain("isn't added up from the others");
  }));

  // ---- editing ----------------------------------------------------------------------------------------

  it('records the creators wording and reports it as theirs', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('My Mapo Tofu');
    component.applyEdit(target);
    settle();

    expect(component.valueOf(target.key, 'Weeknight Mapo Tofu')).toBe('My Mapo Tofu');
    expect(component.isEdited(target.key)).toBe(true);
    expect(edits).toEqual([{ changeId: 'recipe', field: 'title', value: 'My Mapo Tofu' }]);
    expect(text()).toContain('Your wording');
  }));

  /** An edit is the creator's own words, never an acceptance of the model's. Nothing is sent. */
  it('sends nothing when a value is rewritten', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;
    const before = service.watchCalls;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Mine');
    component.applyEdit(target);
    settle();

    expect(service.watchCalls).toBe(before);
  }));

  it('withdraws an edit and tells the host to drop it', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Mine');
    component.applyEdit(target);
    component.startEdit(target);
    component.revertEdit(target);
    settle();

    expect(component.isEdited(target.key)).toBe(false);
    expect(component.valueOf(target.key, 'Weeknight Mapo Tofu')).toBe('Weeknight Mapo Tofu');
    expect(edits[edits.length - 1]).toEqual({ changeId: 'recipe', field: 'title', value: null });
  }));

  /**
   * There is no way to propose "this field should be blank" that a slip cannot be mistaken for, so an emptied
   * box says why nothing happened and keeps both the model's value and any earlier edit.
   */
  it('refuses an emptied box rather than treating it as a deletion', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('   ');
    component.applyEdit(target);
    settle();

    expect(component.editError()).not.toBe('');
    expect(component.isEdited(target.key)).toBe(false);
    expect(edits).toEqual([]);
    expect(component.editingKey()).toBe(target.key);
  }));

  it('trims the creators wording', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('l1-add', 'displayText', 'this line', '2 tbsp doubanjiang');
    component.startEdit(target);
    component.editDraft.set('  3 tbsp doubanjiang  ');
    component.applyEdit(target);
    settle();

    expect(edits[0].value).toBe('3 tbsp doubanjiang');
  }));

  it('opens the editor on whatever is currently shown, edit or original', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    expect(component.editDraft()).toBe('Weeknight Mapo Tofu');

    component.editDraft.set('Mine');
    component.applyEdit(target);
    component.startEdit(target);
    expect(component.editDraft()).toBe('Mine');
  }));

  it('cancelling leaves the value and the edit alone', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Half-written');
    component.cancelEdit();
    settle();

    expect(component.isEdited(target.key)).toBe(false);
    expect(edits).toEqual([]);
    expect(component.editingKey()).toBeNull();
  }));

  /**
   * Only one editor is open at a time, so without somewhere to put it, opening a second row would throw away
   * whatever was typed into the first.
   */
  it('keeps half-written text when the creator moves to another row and back', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const title = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    const line = component.editTarget('l1-add', 'displayText', 'this line', '2 tbsp doubanjiang');

    component.startEdit(title);
    component.editDraft.set('Half-written title');
    component.startEdit(line);
    settle();

    expect(component.editDraft()).toBe('2 tbsp doubanjiang');

    component.startEdit(title);
    settle();

    expect(component.editDraft()).toBe('Half-written title');
    expect(edits).toEqual([]);
  }));

  it('resumes half-written text after the editor was cancelled', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Half-written');
    component.cancelEdit();
    component.startEdit(target);
    settle();

    expect(component.editDraft()).toBe('Half-written');
  }));

  // ---- leaving with unsaved work ----------------------------------------------------------------------

  it('has nothing to lose before anything is written', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    expect(fixture.componentInstance.hasUnsavedWork()).toBe(false);
  }));

  it('counts a committed rewrite, a half-written one and the open box alike', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;
    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');

    component.startEdit(target);
    component.editDraft.set('Typing');
    expect(fixture.componentInstance.hasUnsavedWork()).toBe(true);

    component.cancelEdit();
    settle();
    expect(fixture.componentInstance.hasUnsavedWork()).toBe(true);

    component.startEdit(target);
    component.editDraft.set('Done');
    component.applyEdit(target);
    settle();
    expect(fixture.componentInstance.hasUnsavedWork()).toBe(true);
  }));

  it('leaves without asking when there is nothing to lose', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    let allowed: boolean | undefined;
    void fixture.componentInstance.confirmDiscardIfDirty().then((result) => (allowed = result));
    tick();

    expect(allowed).toBe(true);
    expect(confirmService.calls.length).toBe(0);
  }));

  /** frontend.md: unsaved creator edits survive navigation warnings. These have nowhere to be saved to. */
  it('asks before leaving with rewrites, and says they have nowhere to be kept', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Mine');
    component.applyEdit(target);
    settle();

    let allowed: boolean | undefined;
    void component.confirmDiscardIfDirty().then((result) => (allowed = result));
    tick();

    expect(confirmService.calls.length).toBe(1);
    expect(confirmService.calls[0].message).toContain('nowhere to save them to');
    expect(allowed).toBe(true);
  }));

  it('stays when the creator declines to leave', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;
    confirmService.answer = false;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Mine');
    component.applyEdit(target);
    settle();

    let allowed: boolean | undefined;
    void component.confirmDiscardIfDirty().then((result) => (allowed = result));
    tick();

    expect(allowed).toBe(false);
  }));

  it('shares one dialog between concurrent leave attempts', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Mine');
    component.applyEdit(target);
    settle();

    void component.confirmDiscardIfDirty();
    void component.confirmDiscardIfDirty();
    tick();

    expect(confirmService.calls.length).toBe(1);
  }));

  /** Discarding already asked once; leaving afterwards must not ask a second time about nothing. */
  it('leaves silently after a discard', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Mine');
    component.applyEdit(target);
    settle();

    click('Discard draft');
    settle();
    const afterDiscard = confirmService.calls.length;

    let allowed: boolean | undefined;
    void component.confirmDiscardIfDirty().then((result) => (allowed = result));
    tick();

    expect(allowed).toBe(true);
    expect(confirmService.calls.length).toBe(afterDiscard);
  }));

  // ---- discarding -------------------------------------------------------------------------------------

  it('asks before discarding, and says the draft is only left to expire', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    click('Discard draft');
    settle();

    expect(confirmService.calls.length).toBe(1);
    expect(confirmService.calls[0].message).toContain('expire');
    expect(confirmService.calls[0].message).toContain('no recipe was created');
    expect(confirmService.calls[0].tone).toBe('danger');
  }));

  it('clears the draft and reports it when confirmed', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    click('Discard draft');
    settle();

    expect(fixture.componentInstance.state()).toBe('discarded');
    expect(discards).toBe(1);
    expect(text()).toContain('nothing was sent anywhere');
    expect(text()).not.toContain('Weeknight Mapo Tofu');
  }));

  it('keeps the draft when the confirmation is declined', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    confirmService.answer = false;

    click('Discard draft');
    settle();

    expect(fixture.componentInstance.state()).toBe('proposed');
    expect(discards).toBe(0);
    expect(text()).toContain('Weeknight Mapo Tofu');
  }));

  /** Discarding is client-side: there is no route to call, so no read may be issued by it either. */
  it('sends nothing when discarding', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const before = service.watchCalls;

    click('Discard draft');
    settle();
    tick(10000);

    expect(service.watchCalls).toBe(before);
  }));

  it('drops the creators edits along with the draft', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    const target = component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu');
    component.startEdit(target);
    component.editDraft.set('Mine');
    component.applyEdit(target);
    settle();

    click('Discard draft');
    settle();

    expect(component.isEdited(target.key)).toBe(false);
  }));

  // ---- errors -----------------------------------------------------------------------------------------

  it('reports an unknown request', fakeAsync(() => {
    service.statuses = [{ status: 'not_found' }];
    render();

    expect(fixture.componentInstance.state()).toBe('gone');
    expect(element().querySelector('[role="alert"]')?.textContent).toContain("couldn't find");
  }));

  it('reports a read the creators role may not make', fakeAsync(() => {
    service.statuses = [{ status: 'forbidden' }];
    render();

    expect(fixture.componentInstance.state()).toBe('forbidden');
    expect(element().querySelector('[role="alert"]')?.textContent).toContain('role');
  }));

  it('reports a failed generation with the reason and offers to try again', fakeAsync(() => {
    service.statuses = [found({ status: 'Failed', failureCategory: 'Timeout' })];
    render();

    expect(fixture.componentInstance.state()).toBe('failed');
    expect(fixture.componentInstance.canAskAgain()).toBe(true);
    expect(text()).toContain('Asking again usually works');
  }));

  /** ai.md: a safety block is never retried automatically, so the page must not suggest it. */
  it('does not offer to try again after a safety block', fakeAsync(() => {
    service.statuses = [found({ status: 'Failed', failureCategory: 'SafetyBlocked' })];
    render();

    expect(fixture.componentInstance.state()).toBe('failed');
    expect(fixture.componentInstance.canAskAgain()).toBe(false);
  }));

  it('reports an expired request', fakeAsync(() => {
    service.statuses = [found({ status: 'Expired' })];
    render();

    expect(fixture.componentInstance.state()).toBe('expired');
  }));

  /** One failed poll must not blank a draft a creator is reading. */
  it('keeps the last known state when a poll does not arrive', fakeAsync(() => {
    service.statuses = [ready(), { status: 'unavailable' }];
    render();

    tick(2000);
    settle();

    expect(fixture.componentInstance.state()).toBe('proposed');
    expect(text()).toContain('Weeknight Mapo Tofu');
  }));

  it('says it is showing the last update it could get', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' }), { status: 'unavailable' }];
    render();

    tick(2000);
    settle();

    expect(fixture.componentInstance.connection()).toBe('degraded');
  }));

  it('stops after repeated failures and offers to look again', fakeAsync(() => {
    service.statuses = [{ status: 'unavailable' }];
    render();

    tick(30000);
    settle();

    expect(fixture.componentInstance.canCheckAgain()).toBe(true);
    expect(text()).toContain('stopped checking on its own');
  }));

  /** "Stop checking", not "Cancel": the API offers asking and reading and nothing else. */
  it('offers to stop checking without claiming to cancel the request', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    render();

    expect(text()).toContain('Stop checking');
    expect(text()).toContain('Your request keeps running');
    expect(text()).not.toContain('Cancel request');
  }));

  it('stops asking when told to', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    render();

    click('Stop checking');
    const after = service.watchCalls;
    tick(30000);
    settle();

    expect(service.watchCalls).toBe(after);
  }));

  it('resumes when asked to check again', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    render();

    click('Stop checking');
    const after = service.watchCalls;

    click('Check again');
    settle();

    expect(service.watchCalls).toBeGreaterThan(after);
    tick(30000);
    settle();
  }));

  // ---- accessibility ----------------------------------------------------------------------------------

  it('gives every section a heading its region is named by', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    for (const section of Array.from(element().querySelectorAll('section[aria-labelledby]'))) {
      const id = section.getAttribute('aria-labelledby')!;
      expect(element().querySelector(`#${id}`)).withContext(`no heading #${id}`).not.toBeNull();
    }
  }));

  /** The anchor nav moves focus to a heading, so each destination has to be focusable. */
  it('makes every anchor destination focusable', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    for (const item of fixture.componentInstance.sections()) {
      const heading = element().querySelector(`#${item.targetId}`);
      expect(heading).withContext(`no destination #${item.targetId}`).not.toBeNull();
      expect(heading!.getAttribute('tabindex')).toBe('-1');
    }
  }));

  it('names each edit control by the field it edits', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    const editButtons = Array.from(element().querySelectorAll('button')).filter((button) =>
      (button.textContent ?? '').includes('Edit'),
    );

    expect(editButtons.length).toBeGreaterThan(0);
    for (const button of editButtons) {
      expect(button.querySelector('.cp-sr-only')?.textContent).toContain('Edit ');
    }
  }));

  it('labels the edit box and ties it to its own control', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    component.startEdit(component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu'));
    settle();

    const textarea = element().querySelector('textarea');
    expect(textarea).not.toBeNull();
    const label = element().querySelector(`label[for="${textarea!.id}"]`);
    expect(label?.textContent).toContain('Title');
  }));

  it('announces a refusal assertively', fakeAsync(() => {
    service.statuses = [{ status: 'not_found' }];
    render();

    expect(element().querySelector('[role="alert"]')).not.toBeNull();
  }));

  it('announces progress politely', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    render();

    expect(element().querySelector('[role="status"], [aria-live]')).not.toBeNull();
  }));

  /** A flag's kind is text, never only a colour (WCAG 2.2 AA, 1.4.1). */
  it('names every flags kind in words beside it', fakeAsync(() => {
    service.statuses = [
      ready(fullDraftChanges(), [
        { kind: 'SafetyCaution', message: 'Check the internal temperature yourself.', changeId: null },
      ]),
    ];
    render();

    expect(text()).toContain('Check this yourself');
    expect(text()).toContain('Check the internal temperature yourself.');
  }));

  /** Opening the editor destroys the button that was pressed; focus must not fall to the top of the page. */
  it('moves focus into the box it just opened, and back to the row on close', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const key = 'recipe:title';

    editButtonFor(key).click();
    settle();
    expect(document.activeElement?.tagName).toBe('TEXTAREA');

    buttonWith('Cancel').click();
    settle();
    expect((document.activeElement as HTMLElement | null)?.getAttribute('data-edit-for')).toBe(key);
  }));

  /** The nav emits and this component scrolls and focuses — so every destination must be reachable. */
  it('lists every focusable destination it renders', fakeAsync(() => {
    service.statuses = [
      ready([...fullDraftChanges(), recipeField('somethingNew', 'an unplaceable value')]),
    ];
    render();

    const rendered = Array.from(element().querySelectorAll('h3[tabindex="-1"]')).map((h) => h.id);
    const listed = fixture.componentInstance.sections().map((item) => item.targetId);

    expect(listed.slice().sort()).toEqual(rendered.slice().sort());
  }));

  it('explains what the required marker means rather than leaving a bare glyph', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    expect(text()).toContain('Fields marked * are needed');
  }));

  /** The lede says nothing here is saved; the edit hint must not then say the opposite. */
  it('does not tell the creator their rewrite was saved', fakeAsync(() => {
    service.statuses = [ready()];
    render();
    const component = fixture.componentInstance;

    component.startEdit(component.editTarget('recipe', 'title', 'Title', 'Weeknight Mapo Tofu'));
    settle();

    expect(text()).toContain('Kept on this page only');
    expect(text()).not.toContain('Saved with the draft');
  }));

  it('offers a way out of the discarded state', fakeAsync(() => {
    service.statuses = [ready()];
    render();

    click('Discard draft');
    settle();

    click('Review another draft');
    settle();

    expect(fixture.componentInstance.state()).toBe('idle');
  }));

  it('marks an optional line in words, not only with a colour', fakeAsync(() => {
    service.statuses = [
      ready([
        ...fullDraftChanges(),
        change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'isOptional', afterValue: 'true' }),
      ]),
    ];
    render();

    expect(text()).toContain('Optional');
  }));
});
