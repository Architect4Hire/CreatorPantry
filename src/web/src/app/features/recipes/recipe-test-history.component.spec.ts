import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Observable, of } from 'rxjs';

import { MyWorkspaceMembership } from '../../models/auth.models';
import { TestRunHistoryPage, TestRunHistoryQuery, TestRunSummary } from '../../models/recipe-test-run.models';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeTestRunService, TestRunHistoryOutcome } from '../../services/recipe-test-run.service';
import { RecipeTestHistoryComponent } from './recipe-test-history.component';

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
    createdAt: '2026-09-28T19:00:00Z',
    updatedAt: '2026-09-28T19:00:00Z',
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

class StubTestRunService {
  outcome: TestRunHistoryOutcome = { status: 'found', page: page() };
  queries: TestRunHistoryQuery[] = [];

  /** Set to answer from here instead, so a test can control when a page arrives. */
  pending: ((outcome: TestRunHistoryOutcome) => void) | null = null;
  holdNext = false;
  cursorRefused = false;

  listTestRuns(_slug: string, _recipeId: string, query: TestRunHistoryQuery): Observable<TestRunHistoryOutcome> {
    this.queries.push(query);

    // Only a request that carries a cursor can have it refused; the server has nothing to refuse on a first page.
    if (this.cursorRefused && query.cursor !== null) return of<TestRunHistoryOutcome>({ status: 'cursor_expired' });
    if (!this.holdNext) return of(this.outcome);

    this.holdNext = false;
    return new Observable<TestRunHistoryOutcome>((subscriber) => {
      this.pending = (outcome) => {
        subscriber.next(outcome);
        subscriber.complete();
      };
      return () => {
        this.pending = null;
      };
    });
  }
}

class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership()] });

  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }
}

function membership(): MyWorkspaceMembership {
  return {
    workspaceId: 'w1',
    workspaceSlug: 'cozy-fall',
    workspaceName: 'Cozy Fall',
    membershipId: 'm1',
    role: 'Editor',
    status: 'Active',
  };
}

describe('RecipeTestHistoryComponent', () => {
  let service: StubTestRunService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeTestHistoryComponent>;
  let selected: TestRunSummary[];

  async function render(): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeTestHistoryComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');

    selected = [];
    fixture.componentInstance.testSelected.subscribe((run) => selected.push(run));

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

  function lastQuery(): TestRunHistoryQuery {
    return service.queries[service.queries.length - 1];
  }

  beforeEach(() => {
    service = new StubTestRunService();
    memberships = new StubMembershipService();

    TestBed.configureTestingModule({
      imports: [RecipeTestHistoryComponent],
      providers: [
        { provide: RecipeTestRunService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
      ],
    });
  });

  // ---- Rows -------------------------------------------------------------

  it('reads the first page on mount, asking for the counts', async () => {
    await render();

    expect(service.queries.length).toBe(1);
    expect(lastQuery().cursor).toBeNull();
    expect(lastQuery().includeSummary).toBeTrue();
  });

  it('shows what each row actually carries', async () => {
    const element = await render();

    const text = element.textContent ?? '';
    expect(text).toContain('Version 3');
    expect(text).toContain('Sam Okafor');
    expect(text).toContain('Worked, with notes');
    expect(text).toContain('rated 4 of 5');
    expect(text).toContain('Good crumb, slightly over-baked');
    expect(text).toContain('2 notes');
    expect(text).toContain('1 problem');
    expect(text).toContain('made Got 10, not 12');
    expect(text).toContain('took 55 min');
  });

  /** Authorship outlives membership, so a tester who has left is still named as somebody. */
  it('names a tester whose membership has gone', async () => {
    service.outcome = { status: 'found', page: page({ items: [row({ testedByName: null })] }) };
    const element = await render();

    expect(element.textContent).toContain('Someone who has left this workspace');
  });

  it('marks a row with something still open, and one with everything settled', async () => {
    service.outcome = {
      status: 'found',
      page: page({
        items: [
          row({ id: 'a', unresolvedIssueCount: 2, issueCount: 3 }),
          row({ id: 'b', unresolvedIssueCount: 0, issueCount: 3 }),
        ],
      }),
    };
    const element = await render();

    expect(element.textContent).toContain('2 unresolved');
    expect(element.textContent).toContain('All 3 resolved');
  });

  it('emits the row a reader picked rather than navigating itself', async () => {
    const element = await render();

    expect(element.querySelector('.run-row a')).toBeNull();
    (element.querySelector('.run-button') as HTMLButtonElement).click();

    expect(selected.length).toBe(1);
    expect(selected[0].id).toBe('t1');
  });

  // ---- Summary ----------------------------------------------------------

  it('says the counts describe the filtered set once a filter is on', async () => {
    const element = await render();

    expect(element.textContent).toContain('1 test');
    expect(element.textContent).not.toContain('matching these filters');

    await click(element, 'Did not work');

    expect(element.textContent).toContain('matching these filters');
  });

  it('breaks the verdicts down with every outcome named, even at zero', async () => {
    const element = await render();

    const counts = element.querySelector('.outcome-counts')?.textContent ?? '';
    expect(counts).toContain('0 Worked');
    expect(counts).toContain('1 Worked, with notes');
    expect(counts).toContain('0 Did not work');
    expect(counts).toContain('0 No verdict');
  });

  it('says so plainly when nothing is outstanding', async () => {
    service.outcome = {
      status: 'found',
      page: page({
        summary: {
          totalCount: 2,
          byOutcome: { Succeeded: 2, SucceededWithIssues: 0, Failed: 0, NotStated: 0 },
          runsWithUnresolvedIssues: 0,
          unresolvedIssueCount: 0,
        },
      }),
    };
    const element = await render();

    expect(element.textContent).toContain('with nothing unresolved');
  });

  // ---- Filters ----------------------------------------------------------

  it('sends the verdict chips comma-joined and applied by the server', async () => {
    const element = await render();

    await click(element, 'Worked, with notes');
    await click(element, 'Did not work');

    expect(lastQuery().outcomes).toEqual(['SucceededWithIssues', 'Failed']);
  });

  it('turns a chip off again', async () => {
    const element = await render();

    await click(element, 'Did not work');
    await click(element, 'Did not work');

    expect(lastQuery().outcomes).toEqual([]);
  });

  it('sends the version text as typed, for the server to split and validate', async () => {
    const element = await render();

    fixture.componentInstance.versionsText.set('3, 5');
    fixture.componentInstance.applyTypedFilters();
    await settle();

    expect(lastQuery().versions).toBe('3, 5');
  });

  it('sends the outstanding-problems filter as a single value', async () => {
    const element = await render();

    fixture.componentInstance.setIssueFilter('HasUnresolved');
    await settle();

    expect(lastQuery().issues).toBe('HasUnresolved');

    fixture.componentInstance.setIssueFilter('');
    await settle();
    expect(lastQuery().issues).toBeNull();
    void element;
  });

  /**
   * The upper bound is exclusive, so a creator who picks a day means that day included — which is the day after
   * at midnight. A courtesy to whoever picked the date, not a rule.
   */
  it('sends a chosen day range as instants, with the end shifted to include that day', async () => {
    await render();

    fixture.componentInstance.testedFromDate.set('2026-09-01');
    fixture.componentInstance.testedToDate.set('2026-09-30');
    fixture.componentInstance.applyTypedFilters();
    await settle();

    const from = new Date(lastQuery().testedFrom!);
    const before = new Date(lastQuery().testedBefore!);

    expect(from.getFullYear()).toBe(2026);
    expect(from.getDate()).toBe(1);
    // The 1st of October, locally: the day after the 30th.
    expect(before.getMonth()).toBe(9);
    expect(before.getDate()).toBe(1);
  });

  it('filters to this reader’s own tests by their membership', async () => {
    const element = await render();

    await click(element, 'Cooked by me');

    expect(lastQuery().testedBy).toEqual(['m1']);
  });

  /** No endpoint lists a workspace's members, so there is no general "tested by" picker to offer. */
  it('offers no tester picker, and hides even "Cooked by me" until the membership is known', async () => {
    memberships.state.set({ status: 'loading' });
    const element = await render();

    expect(element.querySelector('[name="testedBy"]')).toBeNull();
    expect(buttonWith(element, 'Cooked by me')).toBeUndefined();
  });

  it('clears every filter at once', async () => {
    const element = await render();

    fixture.componentInstance.versionsText.set('3');
    fixture.componentInstance.testedFromDate.set('2026-09-01');
    await click(element, 'Did not work');
    await click(element, 'Cooked by me');

    await click(element, 'Clear filters');

    expect(fixture.componentInstance.hasActiveFilters()).toBeFalse();
    expect(lastQuery().outcomes).toEqual([]);
    expect(lastQuery().versions).toBe('');
    expect(lastQuery().testedFrom).toBeNull();
    expect(lastQuery().testedBy).toEqual([]);
  });

  it('starts again from the first page whenever a filter changes', async () => {
    service.outcome = { status: 'found', page: page({ nextCursor: 'c1' }) };
    const element = await render();

    await click(element, 'Next');
    expect(lastQuery().cursor).toBe('c1');

    await click(element, 'Did not work');
    expect(lastQuery().cursor).toBeNull();
    expect(fixture.componentInstance.pageNumber()).toBe(1);
  });

  /**
   * A superseded page must not land. Without cancellation an earlier, slower response could arrive after a
   * later one and show the wrong rows under the wrong filters.
   */
  it('abandons a page in flight when a filter changes', async () => {
    const element = await render();
    service.holdNext = true;

    fixture.componentInstance.setIssueFilter('HasUnresolved');
    await settle();
    const abandoned = service.pending;
    expect(abandoned).not.toBeNull();

    await click(element, 'Did not work');

    // The held request was unsubscribed, which is what clears the handle.
    expect(service.pending).toBeNull();
  });

  // ---- Paging -----------------------------------------------------------

  it('follows the cursor forward and stops asking for the counts again', async () => {
    service.outcome = { status: 'found', page: page({ nextCursor: 'c1' }) };
    const element = await render();

    await click(element, 'Next');

    expect(lastQuery().cursor).toBe('c1');
    expect(lastQuery().includeSummary).toBeFalse();
    expect(fixture.componentInstance.pageNumber()).toBe(2);
  });

  it('keeps the counts across pages', async () => {
    service.outcome = { status: 'found', page: page({ nextCursor: 'c1' }) };
    const element = await render();

    service.outcome = { status: 'found', page: page({ nextCursor: null, summary: null }) };
    await click(element, 'Next');

    expect(element.textContent).toContain('1 test');
  });

  /** A keyset cursor is forward-only, so "previous" is a position this screen remembers. */
  it('goes back to a page it remembers having been at', async () => {
    service.outcome = { status: 'found', page: page({ nextCursor: 'c1' }) };
    const element = await render();

    await click(element, 'Next');
    await click(element, 'Previous');

    expect(lastQuery().cursor).toBeNull();
    expect(fixture.componentInstance.pageNumber()).toBe(1);
  });

  it('offers no paging when there is one page', async () => {
    const element = await render();

    expect(buttonWith(element, 'Next')).toBeUndefined();
    expect(buttonWith(element, 'Previous')).toBeUndefined();
  });

  it('disables Previous on the first page of several', async () => {
    service.outcome = { status: 'found', page: page({ nextCursor: 'c1' }) };
    const element = await render();

    expect(buttonWith(element, 'Previous')?.disabled).toBeTrue();
    expect(buttonWith(element, 'Next')?.disabled).toBeFalse();
  });

  // ---- Empty ------------------------------------------------------------

  it('distinguishes a recipe nobody has tested from filters that match nothing', async () => {
    service.outcome = { status: 'found', page: page({ items: [], summary: null }) };
    const element = await render();

    expect(element.textContent).toContain('No tests yet');
    expect(buttonWith(element, 'Clear filters')).toBeUndefined();

    service.outcome = { status: 'found', page: page({ items: [] }) };
    await click(element, 'Did not work');

    expect(element.textContent).toContain('No tests match these filters');
    expect(buttonWith(element, 'Clear filters')).toBeDefined();
  });

  // ---- Errors -----------------------------------------------------------

  it('places a refused filter beside the control that carries it', async () => {
    service.outcome = {
      status: 'invalid_request',
      fieldErrors: { version: ['A version number starts at 1.'] },
    };
    const element = await render();

    expect(element.textContent).toContain('A version number starts at 1.');

    const input = element.querySelector<HTMLInputElement>(`[id="${fixture.componentInstance.idPrefix}-versions"]`);
    expect(input?.getAttribute('aria-invalid')).toBe('true');
    expect(input?.getAttribute('aria-describedby')).toContain('versions-error');
  });

  /** A refusal must not erase what was typed, or a creator cannot see what to correct. */
  it('keeps the typed filter after it was refused', async () => {
    service.outcome = { status: 'invalid_request', fieldErrors: { version: ['No.'] } };
    await render();

    fixture.componentInstance.versionsText.set('nine');
    fixture.componentInstance.applyTypedFilters();
    await settle();

    expect(fixture.componentInstance.versionsText()).toBe('nine');
  });

  /** Starting again is the only remedy, and doing it beats explaining it. */
  it('restarts the list itself when the cursor was refused', async () => {
    service.outcome = { status: 'found', page: page({ nextCursor: 'c1' }) };
    const element = await render();

    service.cursorRefused = true;
    await click(element, 'Next');

    const restarted = service.queries[service.queries.length - 1];
    expect(restarted.cursor).toBeNull();
    expect(fixture.componentInstance.pageNumber()).toBe(1);
  });

  /** Regression: a refused first page used to restart itself without end, overflowing the stack. */
  it('treats a refused first page as a failure instead of restarting forever', async () => {
    service.outcome = { status: 'cursor_expired' };
    const element = await render();

    expect(service.queries.length).toBe(1);
    expect(element.textContent).toContain('Try again');
  });

  it('reports an unreadable recipe', async () => {
    service.outcome = { status: 'not_found' };
    const element = await render();

    expect(element.textContent).toContain('no longer there');
  });

  it('offers a retry and says nothing about the recipe changed', async () => {
    service.outcome = { status: 'unavailable' };
    const element = await render();

    expect(element.textContent).toContain('Nothing about the recipe has changed');
    await click(element, 'Try again');
    expect(service.queries.length).toBe(2);
  });

  // ---- Accessibility ----------------------------------------------------

  it('names the toolbar and both filter groups', async () => {
    const element = await render();

    expect(element.querySelector('cp-toolbar')?.querySelector('[role="toolbar"]')?.getAttribute('aria-label')).toBe(
      'Test history filters',
    );
    expect(element.querySelector('.filter-group')?.getAttribute('aria-label')).toContain('verdict');
  });

  it('carries chip state in aria-pressed, not only in colour', async () => {
    const element = await render();

    const chip = buttonWith(element, 'Did not work');
    expect(chip?.getAttribute('aria-pressed')).toBe('false');

    await click(element, 'Did not work');
    expect(buttonWith(element, 'Did not work')?.getAttribute('aria-pressed')).toBe('true');
  });

  it('announces the counts politely rather than as an alert', async () => {
    const element = await render();

    const line = element.querySelector('.summary-line');
    expect(line?.getAttribute('role')).toBe('status');
    expect(line?.getAttribute('aria-live')).toBe('polite');
  });

  it('gives every filter control a name', async () => {
    const element = await render();

    for (const label of Array.from(element.querySelectorAll('.typed label.field'))) {
      expect(label.querySelector('.label')?.textContent?.trim().length).toBeGreaterThan(0);
      expect(label.querySelector('input')).not.toBeNull();
    }

    expect(element.querySelector('.issues-field .cp-sr-only')?.textContent).toContain('outstanding problems');
  });

  it('names the pager as navigation', async () => {
    service.outcome = { status: 'found', page: page({ nextCursor: 'c1' }) };
    const element = await render();

    expect(element.querySelector('nav.pager-actions')?.getAttribute('aria-label')).toBe('Test history pages');
  });

  it('carries every status pill in words', async () => {
    const element = await render();

    for (const pill of Array.from(element.querySelectorAll('cp-status-pill'))) {
      expect(pill.textContent?.replace(/[\d\s]/g, '').length).toBeGreaterThan(0);
    }
  });
});
