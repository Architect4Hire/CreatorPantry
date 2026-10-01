import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, switchMap, tap } from 'rxjs';
import {
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpListShellComponent,
  CpListShellState,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpToolbarComponent,
} from '@creator-pantry/ui';

import {
  TestRunHistoryPage,
  TestRunHistoryQuery,
  TestRunHistorySummary,
  TestRunIssueFilter,
  TestRunOutcome,
  TestRunSummary,
} from '../../models/recipe-test-run.models';
import { RecipeTestRunService, TestRunHistoryOutcome } from '../../services/recipe-test-run.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';

type HistoryState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly page: TestRunHistoryPage }
  /** A filter the server could not read. What was typed stays on screen, with the refusal beside it. */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** The cursor no longer describes this set. Starting again is the only remedy. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'not_found' }
  | { readonly status: 'error' };

/** The tester's own verdict, in the order a creator reads them. Never derived from the issues. */
const OUTCOME_ORDER: readonly TestRunOutcome[] = ['Succeeded', 'SucceededWithIssues', 'Failed', 'NotStated'];

export const TEST_OUTCOME_LABELS: Readonly<Record<TestRunOutcome, string>> = {
  Succeeded: 'Worked',
  SucceededWithIssues: 'Worked, with notes',
  Failed: 'Did not work',
  NotStated: 'No verdict',
};

/**
 * A tone per verdict. Exhaustive by type, so a verdict added to the enum has to be given one here rather than
 * falling through to a neutral chip nobody chose.
 *
 * `NotStated` is neutral and never `success`: an unstated verdict is not a pass.
 */
export const TEST_OUTCOME_TONES: Readonly<Record<TestRunOutcome, CpStatusPillTone>> = {
  Succeeded: 'success',
  SucceededWithIssues: 'warning',
  Failed: 'error',
  NotStated: 'neutral',
};

const ISSUE_FILTER_LABELS: Readonly<Record<TestRunIssueFilter, string>> = {
  HasUnresolved: 'Still has something unresolved',
  AllResolved: 'Everything resolved',
};

const COULD_NOT_LOAD = "Couldn't read this recipe's test history. Nothing about the recipe has changed.";
const CURSOR_EXPIRED = 'That page is no longer there. The list has started again from the top.';
const NOT_FOUND = 'This recipe is no longer there, or you cannot read it.';

let nextInstance = 0;

/**
 * A recipe's test history (TEST-UI-002): what was cooked, by whom, how it went, and what is still outstanding.
 *
 * **Every filter is applied by the server.** A page is a page of a filtered, ordered set, so narrowing it here
 * would silently drop matches living on pages this screen has not fetched — the one mistake a partial-page list
 * makes that nobody can see.
 *
 * **Rows are summaries, and this screen does not pretend otherwise.** The route returns counts where a test has
 * observations and issues, and reads no attachment byte, asset id or recipe snapshot. So a row says how many
 * problems a test found and how many are still open; what they *are* needs a read of the test itself, which no
 * route offers yet.
 *
 * **The counts are never a readiness verdict.** They describe the filtered set of tests. Whether a recipe is
 * ready is a separate deterministic evaluation with its own screen.
 *
 * **Most recently cooked first**, which is the contract and not a choice: a test written up a week late still
 * belongs to the day it was cooked, and there is no sort parameter to offer.
 */
@Component({
  selector: 'cp-recipe-test-history',
  standalone: true,
  imports: [
    DatePipe,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpListShellComponent,
    CpStatusPillComponent,
    CpToolbarComponent,
  ],
  templateUrl: './recipe-test-history.component.html',
  styleUrl: './recipe-test-history.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeTestHistoryComponent {
  private readonly testRunService = inject(RecipeTestRunService);
  private readonly membershipService = inject(WorkspaceMembershipService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** A row the reader picked. The host decides what to show; this screen only lists. */
  readonly testSelected = output<TestRunSummary>();

  readonly idPrefix = `cp-test-history-${nextInstance++}`;
  readonly outcomeOptions = OUTCOME_ORDER;
  readonly outcomeLabels = TEST_OUTCOME_LABELS;
  readonly issueFilterLabels = ISSUE_FILTER_LABELS;

  private readonly stateSignal = signal<HistoryState>({ status: 'loading' });
  readonly state = this.stateSignal.asReadonly();

  // Filters. Local rather than in the URL: this panel is embedded, and the query string belongs to its host.
  readonly versionsText = signal('');
  readonly outcomes = signal<readonly TestRunOutcome[]>([]);
  readonly issueFilter = signal<TestRunIssueFilter | ''>('');
  readonly testedFromDate = signal('');
  readonly testedToDate = signal('');
  readonly mineOnly = signal(false);

  /** The cursor of the page being shown, and the cursors of the pages behind it, oldest first. */
  private readonly cursor = signal<string | null>(null);
  private readonly visited = signal<readonly (string | null)[]>([]);

  /**
   * Carried across pages: the counts are asked for once, because turning a page cannot change them.
   *
   * Also why a later page asks `includeSummary=false` — counting costs two more queries for figures this
   * screen already has.
   */
  private readonly summarySignal = signal<TestRunHistorySummary | null>(null);
  readonly summary = this.summarySignal.asReadonly();

  private readonly requests = new Subject<TestRunHistoryQuery>();

  constructor() {
    void this.membershipService.ensureLoaded();

    this.requests
      .pipe(
        tap(() => this.stateSignal.set({ status: 'loading' })),
        // switchMap, so a filter changed while a page is in flight cancels it. Without this an earlier, slower
        // response could land after a later one and show the wrong rows under the wrong filters.
        switchMap((query) => this.testRunService.listTestRuns(this.workspaceSlug(), this.recipeId(), query)),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => this.apply(outcome));

    // Starts again whenever the host points this at a different recipe. `untracked` so that reading the
    // filters to build the query does not make this effect depend on them.
    effect(() => {
      const recipeId = this.recipeId();
      const workspaceSlug = this.workspaceSlug();
      void recipeId;
      void workspaceSlug;
      untracked(() => this.restart());
    });
  }

  // -------------------------------------------------------------------------
  // Derived state
  // -------------------------------------------------------------------------

  readonly page = computed(() => {
    const state = this.state();
    return state.status === 'ready' ? state.page : null;
  });

  readonly rows = computed<readonly TestRunSummary[]>(() => this.page()?.items ?? []);

  readonly listShellState = computed<CpListShellState>(() => {
    switch (this.state().status) {
      case 'loading':
        return 'loading';
      case 'ready':
        return this.rows().length === 0 ? 'empty' : 'ready';
      default:
        return 'error';
    }
  });

  readonly errorMessage = computed(() => {
    switch (this.state().status) {
      case 'cursor_expired':
        return CURSOR_EXPIRED;
      case 'not_found':
        return NOT_FOUND;
      case 'invalid_request':
        return 'Those filters could not be read. What is wrong is marked above.';
      case 'error':
        return COULD_NOT_LOAD;
      default:
        return '';
    }
  });

  /** A refused filter's messages, placed on the control that carries it. */
  filterError(field: string): string {
    const state = this.state();
    return state.status === 'invalid_request' ? (state.fieldErrors[field]?.join(' ') ?? '') : '';
  }

  readonly hasActiveFilters = computed(
    () =>
      this.versionsText().trim().length > 0
      || this.outcomes().length > 0
      || this.issueFilter() !== ''
      || this.testedFromDate().length > 0
      || this.testedToDate().length > 0
      || this.mineOnly(),
  );

  readonly hasNextPage = computed(() => this.page()?.nextCursor !== null && this.page()?.nextCursor !== undefined);
  readonly hasPreviousPage = computed(() => this.visited().length > 0);
  readonly pageNumber = computed(() => this.visited().length + 1);

  /**
   * The counts, as a sentence.
   *
   * Says "matching these filters" whenever any are set, because the figures describe the filtered set and a
   * bare total would read as the whole history.
   */
  readonly resultSummary = computed(() => {
    const summary = this.summary();
    if (summary === null) return '';

    const scope = this.hasActiveFilters() ? ' matching these filters' : '';
    const tests = summary.totalCount === 1 ? 'test' : 'tests';

    if (summary.unresolvedIssueCount === 0) {
      return `${summary.totalCount} ${tests}${scope}, with nothing unresolved.`;
    }

    const issues = summary.unresolvedIssueCount === 1 ? 'problem' : 'problems';
    const runs = summary.runsWithUnresolvedIssues === 1 ? 'test' : 'tests';

    return (
      `${summary.totalCount} ${tests}${scope}. `
      + `${summary.unresolvedIssueCount} unresolved ${issues} across ${summary.runsWithUnresolvedIssues} ${runs}.`
    );
  });

  /** The verdict breakdown, with every outcome named even at zero. */
  readonly outcomeCounts = computed<readonly { readonly outcome: TestRunOutcome; readonly count: number }[]>(() => {
    const byOutcome = this.summary()?.byOutcome;
    if (byOutcome === undefined) return [];

    return OUTCOME_ORDER.map((outcome) => ({ outcome, count: byOutcome[outcome] ?? 0 }));
  });

  outcomeTone(outcome: TestRunOutcome): CpStatusPillTone {
    return TEST_OUTCOME_TONES[outcome];
  }

  /** Authorship outlives membership, so a tester who has left still has to be named as somebody. */
  testerName(row: TestRunSummary): string {
    return row.testedByName ?? 'Someone who has left this workspace';
  }

  // -------------------------------------------------------------------------
  // Filters
  // -------------------------------------------------------------------------

  isOutcomeSelected(outcome: TestRunOutcome): boolean {
    return this.outcomes().includes(outcome);
  }

  toggleOutcome(outcome: TestRunOutcome): void {
    this.outcomes.update((selected) =>
      selected.includes(outcome) ? selected.filter((value) => value !== outcome) : [...selected, outcome],
    );
    this.restart();
  }

  setIssueFilter(value: string): void {
    this.issueFilter.set(value === 'HasUnresolved' || value === 'AllResolved' ? value : '');
    this.restart();
  }

  toggleMine(): void {
    this.mineOnly.update((value) => !value);
    this.restart();
  }

  /** Applies the typed filters. Explicit rather than live, because a half-typed version list matches nothing. */
  applyTypedFilters(): void {
    this.restart();
  }

  clearFilters(): void {
    this.versionsText.set('');
    this.outcomes.set([]);
    this.issueFilter.set('');
    this.testedFromDate.set('');
    this.testedToDate.set('');
    this.mineOnly.set(false);
    this.restart();
  }

  /** Whether this reader's own membership is known, which is what "Tested by me" needs. */
  readonly canFilterToMine = computed(() => this.myMembershipId() !== null);

  private myMembershipId(): string | null {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return null;

    return (
      state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.membershipId ?? null
    );
  }

  // -------------------------------------------------------------------------
  // Paging
  // -------------------------------------------------------------------------

  nextPage(): void {
    const next = this.page()?.nextCursor;
    if (next === null || next === undefined) return;

    this.visited.update((stack) => [...stack, this.cursor()]);
    this.cursor.set(next);
    this.request();
  }

  /**
   * Back to the page before this one.
   *
   * A remembered position rather than a query: a keyset cursor is forward-only, so "previous" is not something
   * the server can answer.
   */
  previousPage(): void {
    const stack = this.visited();
    if (stack.length === 0) return;

    this.cursor.set(stack[stack.length - 1]);
    this.visited.set(stack.slice(0, -1));
    this.request();
  }

  retry(): void {
    this.request();
  }

  /** Back to the first page of the current filters. Also the remedy for a cursor the server refused. */
  restart(): void {
    this.cursor.set(null);
    this.visited.set([]);
    this.summarySignal.set(null);
    this.request();
  }

  private request(): void {
    this.requests.next(this.buildQuery());
  }

  private buildQuery(): TestRunHistoryQuery {
    const cursor = this.cursor();
    const mine = this.mineOnly() ? this.myMembershipId() : null;

    return {
      versions: this.versionsText(),
      testedBy: mine === null ? [] : [mine],
      outcomes: this.outcomes(),
      testedFrom: startOfDay(this.testedFromDate()),
      // The upper bound is exclusive, so "to the 30th" is midnight on the 31st. Shifting it here is a courtesy
      // to whoever picked a date, not a rule: a creator choosing a day means that day included.
      testedBefore: startOfNextDay(this.testedToDate()),
      issues: this.issueFilter() === '' ? null : (this.issueFilter() as TestRunIssueFilter),
      // Asked for on a first page only. Turning a page cannot change the figures, and counting costs two more
      // queries.
      includeSummary: cursor === null,
      cursor,
    };
  }

  private apply(outcome: TestRunHistoryOutcome): void {
    switch (outcome.status) {
      case 'found':
        this.stateSignal.set({ status: 'ready', page: outcome.page });
        if (outcome.page.summary !== null) this.summarySignal.set(outcome.page.summary);
        return;

      case 'cursor_expired':
        // The remedy is to start again, and doing it rather than only saying so keeps the reader looking at a
        // list instead of an explanation.
        this.stateSignal.set({ status: 'cursor_expired' });
        this.cursor.set(null);
        this.visited.set([]);
        this.request();
        return;

      case 'invalid_request':
        this.stateSignal.set({ status: 'invalid_request', fieldErrors: outcome.fieldErrors });
        return;

      case 'not_found':
        this.stateSignal.set({ status: 'not_found' });
        return;

      default:
        this.stateSignal.set({ status: 'error' });
    }
  }
}

/** A date input's value as the instant that day starts, locally. Null for an empty box. */
function startOfDay(date: string): string | null {
  if (date.trim().length === 0) return null;

  const parsed = new Date(`${date}T00:00`);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/** The instant the day *after* the given one starts, which is what an exclusive upper bound wants. */
function startOfNextDay(date: string): string | null {
  if (date.trim().length === 0) return null;

  const parsed = new Date(`${date}T00:00`);
  if (Number.isNaN(parsed.getTime())) return null;

  parsed.setDate(parsed.getDate() + 1);
  return parsed.toISOString();
}
