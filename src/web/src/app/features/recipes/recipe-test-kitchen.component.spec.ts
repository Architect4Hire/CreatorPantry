import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Observable, of } from 'rxjs';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  CreateRecipeTestRunRequest,
  CreatedRecipeTestRun,
  RecipeTestRun,
  ResolveTestIssueRequest,
  TestIssue,
  TestObservation,
  TestRunHistoryPage,
  TestRunHistoryQuery,
  TestRunSummary,
  UpdateRecipeTestRunRequest,
} from '../../models/recipe-test-run.models';
import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { ListUnitsOutcome, ReferenceService } from '../../services/reference.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import {
  CreateTestRunOutcome,
  GetTestRunOutcome,
  RecipeTestRunService,
  ResolveTestIssueOutcome,
  TestRunHistoryOutcome,
  UpdateTestRunOutcome,
} from '../../services/recipe-test-run.service';
import { RecipeTestKitchenComponent } from './recipe-test-kitchen.component';

function row(overrides: Partial<TestRunSummary> = {}): TestRunSummary {
  return {
    id: 't1',
    recipeVersionId: 'v3',
    versionNumber: 3,
    testedAt: '2026-09-28T18:00:00Z',
    testedByMembershipId: 'm1',
    testedByName: 'Sam Okafor',
    outcome: 'SucceededWithIssues',
    rating: 4,
    summaryNotes: 'Good crumb, slightly over-baked',
    actualYieldText: 'Got 10, not 12',
    actualYieldQuantity: 10,
    actualYieldUnitId: 'u1',
    actualPrepTimeMinutes: 20,
    actualCookTimeMinutes: 35,
    actualRestTimeMinutes: 10,
    actualTotalTimeMinutes: 55,
    observationCount: 2,
    issueCount: 1,
    unresolvedIssueCount: 1,
    attachmentCount: 0,
    createdAt: '2026-09-29T19:00:00Z',
    updatedAt: '2026-09-29T19:00:00Z',
    concurrencyToken: 'token-1',
    ...overrides,
  };
}

function page(overrides: Partial<TestRunHistoryPage> = {}): TestRunHistoryPage {
  return {
    items: [row()],
    nextCursor: null,
    summary: {
      totalCount: 1,
      byOutcome: { Succeeded: 0, SucceededWithIssues: 1, Failed: 0, NotStated: 0 },
      runsWithUnresolvedIssues: 1,
      unresolvedIssueCount: 1,
    },
    ...overrides,
  };
}

function issue(overrides: Partial<TestIssue> = {}): TestIssue {
  return {
    id: 'i1',
    severity: 'Major',
    title: 'Bake time too short',
    description: 'The middle was still wet at 35 minutes.',
    testObservationId: 'o1',
    sortOrder: 0,
    resolution: null,
    ...overrides,
  };
}

const OBSERVATION: TestObservation = {
  id: 'o1',
  kind: 'Timing',
  text: 'Needed five more minutes.',
  sortOrder: 0,
};

function savedRun(overrides: Partial<RecipeTestRun> = {}): RecipeTestRun {
  return {
    id: 't1',
    recipeId: 'r1',
    recipeVersionId: 'v3',
    testedAt: '2026-09-28T18:00:00Z',
    testedByMembershipId: 'm1',
    outcome: 'SucceededWithIssues',
    rating: 4,
    environmentNotes: 'Fan oven, ran hot.',
    equipmentNotes: null,
    summaryNotes: 'Good crumb.',
    actualYieldText: null,
    actualYieldQuantity: null,
    actualYieldUnitId: null,
    actualPrepTimeMinutes: null,
    actualCookTimeMinutes: null,
    actualRestTimeMinutes: null,
    actualTotalTimeMinutes: null,
    createdByMembershipId: 'm1',
    updatedByMembershipId: 'm1',
    createdAt: '2026-09-28T19:00:00Z',
    updatedAt: '2026-09-28T19:00:00Z',
    concurrencyToken: 'token-1',
    observations: [OBSERVATION],
    issues: [issue()],
    ...overrides,
  };
}

/** What a create answers with: the ids it assigned, never the contents. */
const CREATED_RUN: CreatedRecipeTestRun = {
  testRunId: 't1',
  recipeId: 'r1',
  recipeVersionId: 'v3',
  sourceVersionNumber: 3,
  outcome: 'NotStated',
  testedAt: '2026-09-28T18:00:00Z',
  createdAt: '2026-09-28T19:00:00Z',
  concurrencyToken: 'token-1',
  observationIds: [],
  issueIds: [],
};

/** One stub for all three children plus the host's own read, because the host mounts the real panels. */
class StubTestRunService {
  historyOutcome: TestRunHistoryOutcome = { status: 'found', page: page() };
  getOutcome: GetTestRunOutcome = { status: 'found', testRun: savedRun() };
  createOutcome: CreateTestRunOutcome = { status: 'created', replayed: false, testRun: CREATED_RUN };
  updateOutcome: UpdateTestRunOutcome = { status: 'updated', replayed: false, testRun: savedRun() };
  resolveOutcome: ResolveTestIssueOutcome = { status: 'unavailable' };

  queries: TestRunHistoryQuery[] = [];
  getCalls: string[] = [];
  createCalls: CreateRecipeTestRunRequest[] = [];

  listTestRuns(_slug: string, _recipeId: string, query: TestRunHistoryQuery): Observable<TestRunHistoryOutcome> {
    this.queries.push(query);
    return of(this.historyOutcome);
  }

  getTestRun(_slug: string, _recipeId: string, testRunId: string): Promise<GetTestRunOutcome> {
    this.getCalls.push(testRunId);
    return Promise.resolve(this.getOutcome);
  }

  createTestRun(
    _slug: string,
    _recipeId: string,
    request: CreateRecipeTestRunRequest,
  ): Promise<CreateTestRunOutcome> {
    this.createCalls.push(request);
    return Promise.resolve(this.createOutcome);
  }

  updateTestRun(
    _slug: string,
    _recipeId: string,
    _testRunId: string,
    _request: UpdateRecipeTestRunRequest,
  ): Promise<UpdateTestRunOutcome> {
    return Promise.resolve(this.updateOutcome);
  }

  resolveIssue(
    _slug: string,
    _recipeId: string,
    _testRunId: string,
    _issueId: string,
    _request: ResolveTestIssueRequest,
  ): Promise<ResolveTestIssueOutcome> {
    return Promise.resolve(this.resolveOutcome);
  }
}

class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership('Editor')] });

  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }

  setRole(role: WorkspaceRole): void {
    this.state.set({ status: 'ready', memberships: [membership(role)] });
  }
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

class StubReferenceService {
  listUnits(): Promise<ListUnitsOutcome> {
    return Promise.resolve({ status: 'found', units: [] } satisfies ListUnitsOutcome);
  }
}

class StubConfirmService {
  answer = true;

  confirm(_request: ConfirmRequest): Promise<boolean> {
    return Promise.resolve(this.answer);
  }
}

describe('RecipeTestKitchenComponent', () => {
  let service: StubTestRunService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeTestKitchenComponent>;
  let versions: string[];

  async function render(versionNumber: number | null = 3, editorIsDirty = false): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeTestKitchenComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('versionNumber', versionNumber);
    fixture.componentRef.setInput('editorIsDirty', editorIsDirty);

    versions = [];
    fixture.componentInstance.versionActivated.subscribe((version) => versions.push(version));

    fixture.detectChanges();
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function buttonWith(element: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(element.querySelectorAll('button')).find((button) => button.textContent?.trim().includes(text));
  }

  async function click(element: HTMLElement, text: string): Promise<void> {
    buttonWith(element, text)?.click();
    await settle();
  }

  /** The first row in the history, which is a button that raises `testSelected`. */
  async function pickFirstRow(element: HTMLElement): Promise<void> {
    (element.querySelector('.run-button') as HTMLButtonElement).click();
    await settle();
  }

  function openCard(element: HTMLElement): HTMLElement | null {
    return element.querySelector<HTMLElement>(`[id="${fixture.componentInstance.idPrefix}-open"]`);
  }

  beforeEach(() => {
    service = new StubTestRunService();
    memberships = new StubMembershipService();

    TestBed.configureTestingModule({
      imports: [RecipeTestKitchenComponent],
      providers: [
        { provide: RecipeTestRunService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
        { provide: ReferenceService, useValue: new StubReferenceService() },
        { provide: ConfirmService, useValue: new StubConfirmService() },
      ],
    });
  });

  // ---- The landing surface ------------------------------------------------

  it('opens on the history and reads it', async () => {
    const element = await render();

    expect(element.textContent).toContain('Test history');
    expect(element.textContent).toContain('Sam Okafor');
    expect(service.queries.length).toBe(1);
  });

  it('says nothing recorded here changes the recipe', async () => {
    const element = await render();

    expect(element.textContent).toContain('nothing recorded here changes the recipe');
  });

  it('names the version a new test would be filed against', async () => {
    const element = await render(7);

    expect(element.textContent).toContain('version 7');
  });

  // ---- When recording is unavailable --------------------------------------

  /**
   * Each of the three reasons has a different remedy, so each is said rather than left to a greyed-out
   * button. The reason must also be *present*, not hidden behind a hover or a focus the control cannot take.
   */
  it('refuses to record below Contributor, and says why', async () => {
    memberships.setRole('Viewer');
    const element = await render();

    expect(buttonWith(element, 'Record a test')?.disabled).toBeTrue();
    expect(element.textContent).toContain('Contributor role or above');
  });

  it('refuses to record while the editor has unsaved changes, and says why', async () => {
    const element = await render(3, true);

    expect(buttonWith(element, 'Record a test')?.disabled).toBeTrue();
    expect(element.textContent).toContain('recorded against the saved version');
  });

  it('refuses to record when there is no saved version yet, and says why', async () => {
    const element = await render(null);

    expect(buttonWith(element, 'Record a test')?.disabled).toBeTrue();
    expect(element.textContent).toContain('no saved version to test yet');
  });

  it('does not open the form when recording is unavailable', async () => {
    const element = await render(3, true);

    fixture.componentInstance.startRecording();
    await settle();

    expect(element.querySelector('cp-recipe-test-run-form')).toBeNull();
  });

  /**
   * Correcting an existing test carries none of recording's other two conditions: the test already names the
   * version it was filed against, so an unsaved edit on the recipe form has nothing to do with it.
   */
  it('still opens an existing test for correction while the editor has unsaved changes', async () => {
    const element = await render(3, true);

    await pickFirstRow(element);

    expect(element.querySelector('cp-recipe-test-run-form')).not.toBeNull();
  });

  // ---- Recording ----------------------------------------------------------

  it('opens the form on the saved version, and leaves the history', async () => {
    const element = await render(7);

    await click(element, 'Record a test');

    expect(element.querySelector('cp-recipe-test-run-form')).not.toBeNull();
    expect(element.querySelector('cp-recipe-test-history')).toBeNull();
    expect(element.textContent).toContain('version 7');
  });

  it('goes back to the history and re-reads it, which is how a new test appears', async () => {
    const element = await render();
    await click(element, 'Record a test');

    await click(element, 'Back to the history');

    expect(element.querySelector('cp-recipe-test-history')).not.toBeNull();
    expect(service.queries.length).toBe(2);
  });

  it('returns to the history when the form is cancelled', async () => {
    const element = await render();
    await click(element, 'Record a test');

    fixture.componentInstance.onCancelled();
    await settle();

    expect(element.querySelector('cp-recipe-test-history')).not.toBeNull();
  });

  /** Proves the form's `saved` output is actually bound, which calling onSaved() directly would not. */
  it('confirms a recorded test, naming the version it was filed against', async () => {
    const element = await render(3);
    await click(element, 'Record a test');

    (element.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    await settle();

    expect(service.createCalls.length).toBe(1);
    expect(element.textContent).toContain('Test recorded against version 3');
  });

  /**
   * A create answers with ids, not severities and titles, so the problems panel has nothing to show from it.
   * Reading the test back is what makes the problems just recorded resolvable without leaving the page.
   */
  it('reads a newly recorded test back so its problems can be resolved straight away', async () => {
    const element = await render();
    await click(element, 'Record a test');

    (element.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    await settle();

    expect(service.getCalls).toEqual([CREATED_RUN.testRunId]);
    expect(element.querySelector('cp-recipe-test-issues')).not.toBeNull();
    expect(element.textContent).toContain('Bake time too short');
  });

  /** A failed read is not a failed save: the test is recorded, and only the panel stays shut. */
  it('still confirms the save when reading the new test back fails', async () => {
    const element = await render();
    await click(element, 'Record a test');
    service.getOutcome = { status: 'unavailable' };

    (element.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    await settle();

    expect(element.textContent).toContain('Test recorded against version 3');
    expect(element.querySelector('cp-recipe-test-issues')).toBeNull();
  });

  // ---- Opening a test from the history ------------------------------------

  it('reads the picked test whole, rather than showing only what the row carried', async () => {
    const element = await render();

    await pickFirstRow(element);

    expect(service.getCalls).toEqual(['t1']);
    expect(element.querySelector('cp-recipe-test-issues')).not.toBeNull();
    expect(element.textContent).toContain('Bake time too short');
  });

  /** The whole point of the read: a correction can now start from the history, not only from a save. */
  it('offers the opened test for correction, seeded with what was recorded', async () => {
    const element = await render();

    await pickFirstRow(element);

    expect(element.querySelector('cp-recipe-test-run-form')).not.toBeNull();
    expect(element.textContent).toContain('Correct this test');
  });

  it('heads the opened test with the facts a list row has no space for', async () => {
    const element = await render();

    await pickFirstRow(element);

    const card = openCard(element);
    expect(card).not.toBeNull();
    const text = card?.textContent ?? '';
    expect(text).toContain('Version 3');
    expect(text).toContain('Prep');
    expect(text).toContain('20 min');
    expect(text).toContain('Written up');
  });

  it('mentions a correction only when the write-up was corrected', async () => {
    const element = await render();

    await pickFirstRow(element);
    expect(openCard(element)?.textContent).not.toContain('Last corrected');

    service.historyOutcome = {
      status: 'found',
      page: page({ items: [row({ updatedAt: '2026-09-30T08:00:00Z' })] }),
    };
    await click(element, 'Back to the history');
    await pickFirstRow(element);

    expect(openCard(element)?.textContent).toContain('Last corrected');
  });

  /** A test names its version as an id; the number a tester cites comes from the row it was opened from. */
  it('takes the cooked version number from the row, not from the saved recipe', async () => {
    service.historyOutcome = { status: 'found', page: page({ items: [row({ versionNumber: 9 })] }) };
    const element = await render(3);

    await pickFirstRow(element);

    expect(fixture.componentInstance.versionForForm()).toBe(9);
  });

  it('moves focus to the opened test, which replaces a list that can be a page long', async () => {
    const element = await render();

    await pickFirstRow(element);

    expect(document.activeElement).toBe(openCard(element));
  });

  it('names the opened test for a screen reader by its own heading', async () => {
    const element = await render();

    await pickFirstRow(element);

    const labelledBy = openCard(element)?.getAttribute('aria-labelledby');
    expect(labelledBy).toBeTruthy();
    expect(element.querySelector(`[id="${labelledBy}"]`)?.textContent).toContain('Version 3');
  });

  it('drops the opened test when the reader goes back to the history', async () => {
    const element = await render();
    await pickFirstRow(element);

    await click(element, 'Back to the history');

    expect(openCard(element)).toBeNull();
    expect(element.querySelector('cp-recipe-test-issues')).toBeNull();
    expect(element.querySelector('cp-recipe-test-run-form')).toBeNull();
  });

  // ---- When the read fails ------------------------------------------------

  /** A stale list, not a broken one: the row was real when it was listed. */
  it('says a test that is no longer there is gone, and offers the way back', async () => {
    service.getOutcome = { status: 'not_found' };
    const element = await render();

    await pickFirstRow(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('no longer there');
    expect(element.querySelector('cp-recipe-test-run-form')).toBeNull();
  });

  /** A different remedy from a missing test, so a different message. */
  it('says an unreadable recipe is an unreadable recipe', async () => {
    service.getOutcome = { status: 'recipe_not_found' };
    const element = await render();

    await pickFirstRow(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('recipe could not be read');
  });

  it('offers to try again when the read failed for no stated reason', async () => {
    service.getOutcome = { status: 'unavailable' };
    const element = await render();
    await pickFirstRow(element);

    service.getOutcome = { status: 'found', testRun: savedRun() };
    await click(element, 'Try again');

    expect(service.getCalls.length).toBe(2);
    expect(element.querySelector('cp-recipe-test-run-form')).not.toBeNull();
  });

  /** A test that could not be re-read is not one anybody should still be correcting. */
  it('clears the open test when a re-read fails', async () => {
    const element = await render();
    await pickFirstRow(element);
    expect(element.querySelector('cp-recipe-test-run-form')).not.toBeNull();

    service.getOutcome = { status: 'unavailable' };
    fixture.componentInstance.retryOpen();
    await settle();

    expect(element.querySelector('cp-recipe-test-run-form')).toBeNull();
    expect(element.querySelector('cp-recipe-test-issues')).toBeNull();
  });

  // ---- Correcting ---------------------------------------------------------

  it('takes the corrected test as the server returned it', async () => {
    const element = await render();
    await pickFirstRow(element);

    await fixture.componentInstance.onSaved({
      kind: 'updated',
      testRun: savedRun({ issues: [issue({ title: 'Renamed problem' })] }),
    });
    await settle();

    expect(element.textContent).toContain('Correction saved.');
    expect(element.textContent).toContain('Renamed problem');
  });

  it('opens no problems panel for a test that found nothing', async () => {
    service.getOutcome = { status: 'found', testRun: savedRun({ issues: [] }) };
    const element = await render();

    await pickFirstRow(element);

    expect(element.querySelector('cp-recipe-test-issues')).toBeNull();
    expect(element.querySelector('cp-recipe-test-run-form')).not.toBeNull();
  });

  /**
   * The form's remedy for a conflict, which is now a read that exists. Re-reading replaces what is on screen
   * with what the other person wrote — the trade the form's own conflict copy describes.
   */
  it('re-reads the test when the form asks for a fresh copy after a conflict', async () => {
    const element = await render();
    await pickFirstRow(element);

    service.getOutcome = { status: 'found', testRun: savedRun({ summaryNotes: 'What they wrote instead.' }) };
    fixture.componentInstance.onReloadRequested();
    await settle();

    expect(service.getCalls.length).toBe(2);
    expect(fixture.componentInstance.openRun()?.summaryNotes).toBe('What they wrote instead.');
  });

  it('forwards a version the problems panel names, rather than handling it here', async () => {
    const element = await render();
    await pickFirstRow(element);

    fixture.componentInstance.versionActivated.emit('4');

    expect(versions).toEqual(['4']);
  });

  // ---- A reader who may not write ----------------------------------------

  /** A form whose Save was always going to be refused is worse than no form. */
  it('shows a Viewer the test without a form, and says why', async () => {
    memberships.setRole('Viewer');
    const element = await render();

    await pickFirstRow(element);

    expect(element.querySelector('cp-recipe-test-run-form')).toBeNull();
    expect(element.textContent).toContain('Correcting a write-up needs the Contributor role');
  });

  /** The notes live in the form for a writer, so a reader has to be shown them somewhere. */
  it('shows a Viewer the tester’s own notes, which the form would otherwise hold', async () => {
    memberships.setRole('Viewer');
    const element = await render();

    await pickFirstRow(element);

    expect(openCard(element)?.textContent).toContain('Needed five more minutes.');
    expect(openCard(element)?.textContent).toContain('Fan oven, ran hot.');
  });

  /** The panel gates recording a decision on the Editor role itself, so a Viewer gets the list and no controls. */
  it('still shows a Viewer what the test found', async () => {
    memberships.setRole('Viewer');
    const element = await render();

    await pickFirstRow(element);

    expect(element.querySelector('cp-recipe-test-issues')).not.toBeNull();
    expect(element.textContent).toContain('Bake time too short');
  });

  /** A writer reads them in the form, where they can also be corrected; twice would be the same words twice. */
  it('does not repeat the notes above the form for a writer', async () => {
    const element = await render();

    await pickFirstRow(element);

    expect(openCard(element)?.textContent).not.toContain('Needed five more minutes.');
  });
});
