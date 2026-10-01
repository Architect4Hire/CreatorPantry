import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { CpButtonComponent, CpCardComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { roleAllows } from '../../models/recipe-readiness.models';
import { RecipeTestRun, TestRunSummary } from '../../models/recipe-test-run.models';
import { RecipeTestRunService } from '../../services/recipe-test-run.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import {
  RecipeTestHistoryComponent,
  TEST_OUTCOME_LABELS,
  TEST_OUTCOME_TONES,
} from './recipe-test-history.component';
import { RecipeTestIssuesComponent } from './recipe-test-issues.component';
import { RecipeTestRunFormComponent, TestRunSaved } from './recipe-test-run-form.component';

/**
 * Which of the kitchen's two surfaces is showing.
 *
 * Two rather than three, and not a tablist: these are the steps of one errand, not parallel views of a
 * recipe. `test` is one test — recording a new one and opening an old one are the same surface, because the
 * only difference between them is whether there is already a test to show.
 */
export type TestKitchenView = 'history' | 'test';

/** What became of the attempt to open one test. */
type OpenState =
  | { readonly status: 'idle' }
  | { readonly status: 'loading' }
  | { readonly status: 'ready' }
  /** The test is not there any more — somebody deleted it, or the list is stale. */
  | { readonly status: 'not_found' }
  /** The recipe itself cannot be read. A different remedy, so a different state. */
  | { readonly status: 'recipe_not_found' }
  | { readonly status: 'unavailable' };

let nextInstance = 0;

/**
 * The Test Kitchen: the host that joins the three test surfaces into one errand (TEST-UI-001/002).
 *
 * Each of the three panels was built to be hosted and none of them fetches what another holds, so something
 * has to own the move between them and the one read they share. This does, and it owns nothing else: every
 * write and every verdict stays in the panel that already had it.
 *
 * **Returning to the history is how a new test appears in it.** The list is inside an `@if`, so coming back
 * mounts it again and it re-reads from the first page. That is deliberate rather than incidental — the
 * alternative is a `viewChild` reaching into the list to restart it, which is a second thing that has to know
 * when a write happened. The cost is that filters do not survive the trip, which the list already treats as
 * ephemeral state belonging to its host.
 *
 * **Recording is filed against the saved version, so it waits for a clean form; correcting does not.** A new
 * test cites the version the editor last saved, and a creator with unsaved edits would be filing a write-up
 * against words that are not the ones on their screen. An existing test already names its own version, and
 * correcting its prose has nothing to do with the recipe form at all.
 *
 * **A Viewer reads and does not write.** The form is offered from Contributor up, the same bar as the route
 * behind it; below that a test opens as a card and its problems as a read-only list, rather than as a form
 * whose Save was always going to be refused.
 */
@Component({
  selector: 'cp-recipe-test-kitchen',
  standalone: true,
  imports: [
    DatePipe,
    CpButtonComponent,
    CpCardComponent,
    CpStatusPillComponent,
    RecipeTestHistoryComponent,
    RecipeTestIssuesComponent,
    RecipeTestRunFormComponent,
  ],
  templateUrl: './recipe-test-kitchen.component.html',
  styleUrl: './recipe-test-kitchen.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeTestKitchenComponent {
  private readonly testRunService = inject(RecipeTestRunService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);

  readonly idPrefix = `cp-test-kitchen-${nextInstance++}`;

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The saved recipe's current version, as its history numbers it. Null before one has been read.
   *
   * What a *new* test is recorded against, and the reason recording is unavailable on a recipe that has no
   * saved version yet. An opened test names its own version instead.
   */
  readonly versionNumber = input<number | null>(null);

  /** Whether the editor hosting this has edits that are not saved. See the class note on why it matters. */
  readonly editorIsDirty = input(false);

  /**
   * A version carrying a correction, activated by a reader of the issues panel.
   *
   * Forwarded rather than handled: only the editor knows where a version can be shown.
   */
  readonly versionActivated = output<string>();

  readonly outcomeLabels = TEST_OUTCOME_LABELS;
  readonly outcomeTones = TEST_OUTCOME_TONES;

  private readonly viewSignal = signal<TestKitchenView>('history');
  readonly view = this.viewSignal.asReadonly();

  /**
   * The history row an open test came from, or null when recording a new one.
   *
   * Kept beside the test itself because it carries two things the test does not: the version *number*, which
   * the API publishes as an id, and the tester's display name, which this route deliberately does not resolve.
   */
  private readonly openSummarySignal = signal<TestRunSummary | null>(null);
  readonly openSummary = this.openSummarySignal.asReadonly();

  /** The test in hand, whole. Null while one is loading, while one failed, and while recording a new one. */
  private readonly openRunSignal = signal<RecipeTestRun | null>(null);
  readonly openRun = this.openRunSignal.asReadonly();

  private readonly openStateSignal = signal<OpenState>({ status: 'idle' });
  readonly openState = this.openStateSignal.asReadonly();

  /** What just happened, said once above the form. Announced, because a save is otherwise silent. */
  private readonly noticeSignal = signal('');
  readonly notice = this.noticeSignal.asReadonly();

  constructor() {
    void this.membershipService.ensureLoaded();
  }

  // -------------------------------------------------------------------------
  // Who may do what
  // -------------------------------------------------------------------------

  /** Derived from the reason rather than beside it, so a control can never be enabled with a reason showing. */
  readonly canRecord = computed(() => this.recordBlockedReason() === '');

  /**
   * Why recording a new test is unavailable, in the words a creator needs, or empty when it is available.
   *
   * One sentence rather than a disabled control on its own: each of the three reasons has a different remedy,
   * and a greyed-out button names none of them. Role first, because no amount of saving changes it — and the
   * bar is Contributor, the same as writing a recipe, mirroring the policy on the POST route.
   */
  readonly recordBlockedReason = computed(() => {
    if (!this.canWrite()) {
      return 'Recording a test needs the Contributor role or above in this workspace.';
    }

    if (this.versionNumber() === null) {
      return 'There is no saved version to test yet. Save the recipe first.';
    }

    if (this.editorIsDirty()) {
      return 'You have changes that are not saved yet. Save them first: a test is recorded against the saved version.';
    }

    return '';
  });

  /**
   * Whether this caller may write a test up at all. The Contributor bar, mirroring both routes behind it.
   *
   * Separate from {@link canRecord}, because correcting an existing test carries none of its other two
   * conditions: an open test already names the version it was filed against, so neither a missing saved
   * version nor an unsaved edit on the recipe form has anything to do with it.
   */
  readonly canWrite = computed(() => roleAllows(this.myRole(), 'Contributor'));

  private myRole() {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role ?? null;
  }

  /**
   * The version the form names: the open test's own, or the saved recipe's when recording a new one.
   *
   * Taken from the history row rather than from the test, because the API publishes a test's version as an id
   * and the number is what a tester reads and cites.
   */
  readonly versionForForm = computed(() => this.openSummary()?.versionNumber ?? this.versionNumber());

  /** Whether the form is the right surface for what is open: a writer, and a test that is actually in hand. */
  readonly showForm = computed(
    () => this.canWrite() && this.versionForForm() !== null
      && (this.openSummary() === null || this.openRun() !== null),
  );

  // -------------------------------------------------------------------------
  // Opening and recording
  // -------------------------------------------------------------------------

  startRecording(): void {
    if (!this.canRecord()) return;

    this.openSummarySignal.set(null);
    this.openRunSignal.set(null);
    this.openStateSignal.set({ status: 'idle' });
    this.noticeSignal.set('');
    this.viewSignal.set('test');
  }

  /** Opens the picked row as the test it names, which takes a read: a row carries counts, not contents. */
  onTestSelected(row: TestRunSummary): void {
    this.openSummarySignal.set(row);
    this.openRunSignal.set(null);
    this.noticeSignal.set('');
    this.viewSignal.set('test');

    this.focusOpenTest();
    void this.loadOpenTest();
  }

  /** Reads the open test again — the remedy for a failed open, and for a conflict the form could not resolve. */
  retryOpen(): void {
    void this.loadOpenTest();
  }

  private async loadOpenTest(): Promise<void> {
    const row = this.openSummary();
    if (row === null) return;

    this.openStateSignal.set({ status: 'loading' });

    const outcome = await this.testRunService.getTestRun(this.workspaceSlug(), this.recipeId(), row.id);

    if (outcome.status === 'found') {
      this.openRunSignal.set(outcome.testRun);
      this.openStateSignal.set({ status: 'ready' });
      return;
    }

    // Whatever was on screen is cleared rather than left under a failure message: a test that could not be
    // re-read is not a test anybody should still be correcting.
    this.openRunSignal.set(null);
    this.openStateSignal.set({ status: outcome.status });
  }

  /**
   * Back to the list.
   *
   * Also drops the test in hand, because the form and the problems panel belong to the write-up being looked
   * at, not to the recipe: leaving them mounted behind the list would show a decision surface for a test
   * nobody has open.
   */
  backToHistory(): void {
    this.openSummarySignal.set(null);
    this.openRunSignal.set(null);
    this.openStateSignal.set({ status: 'idle' });
    this.noticeSignal.set('');
    this.viewSignal.set('history');
  }

  async onSaved(saved: TestRunSaved): Promise<void> {
    if (saved.kind === 'updated') {
      this.openRunSignal.set(saved.testRun);
      this.openStateSignal.set({ status: 'ready' });
      this.noticeSignal.set('Correction saved.');
      return;
    }

    this.noticeSignal.set(`Test recorded against version ${saved.testRun.sourceVersionNumber}.`);

    // A create answers with ids and not contents, so the problems panel has nothing to show from it. Reading
    // the test back is what makes the problems just recorded resolvable without leaving the page — which is
    // the whole reason the read route exists. A failed read is not a failed save: the notice above already
    // said the test is recorded, so this only leaves the panel unopened.
    const outcome = await this.testRunService.getTestRun(
      this.workspaceSlug(), this.recipeId(), saved.testRun.testRunId);

    if (outcome.status === 'found') {
      this.openRunSignal.set(outcome.testRun);
      this.openStateSignal.set({ status: 'ready' });
    }
  }

  onCancelled(): void {
    this.backToHistory();
  }

  /**
   * The form's remedy for a conflict: a fresh copy of the test, which there is now a route to fetch.
   *
   * Re-reading replaces the form's input, which reseeds it — so what the other person wrote is what is on
   * screen afterwards, and this caller's unsaved corrections are gone. That is the trade the form's own
   * conflict copy describes; it asks for this only when the creator has chosen it.
   */
  onReloadRequested(): void {
    void this.loadOpenTest();
  }

  onIssueResolved(): void {
    this.noticeSignal.set('Recorded what was done about that problem.');
  }

  // -------------------------------------------------------------------------
  // The open test's header
  // -------------------------------------------------------------------------

  /**
   * Whether the open row was corrected after it was written up.
   *
   * Three timestamps mean three different things on a test: when it was cooked, when it was written up, and
   * when the write-up was last changed. The list shows the first; the header shows the other two, and only
   * mentions a correction when there was one.
   */
  readonly openWasCorrected = computed(() => {
    const row = this.openSummary();
    return row !== null && row.updatedAt !== row.createdAt;
  });

  /** The phase times the open row recorded, in the order a cook reads them. Empty when none were. */
  readonly openTimes = computed(() => {
    const row = this.openSummary();
    if (row === null) return [];

    const times: { readonly label: string; readonly minutes: number }[] = [];
    if (row.actualPrepTimeMinutes !== null) times.push({ label: 'Prep', minutes: row.actualPrepTimeMinutes });
    if (row.actualCookTimeMinutes !== null) times.push({ label: 'Cook', minutes: row.actualCookTimeMinutes });
    if (row.actualRestTimeMinutes !== null) times.push({ label: 'Rest', minutes: row.actualRestTimeMinutes });
    // Last and never a sum of the three above — the same rule the recipe itself keeps.
    if (row.actualTotalTimeMinutes !== null) times.push({ label: 'Total', minutes: row.actualTotalTimeMinutes });

    return times;
  });

  testerName(row: TestRunSummary): string {
    return row.testedByName ?? 'someone no longer in this workspace';
  }

  /**
   * Moves focus into the opened test, once its header has rendered.
   *
   * The focus move is the point of the interaction rather than a nicety: a row activated near the bottom of a
   * long list would otherwise replace the whole surface with no indication of where the reader now is. It
   * waits for a render rather than the read, because the header comes from the row and is on screen while the
   * test is still being fetched — focus should land there, not after the network.
   */
  private focusOpenTest(): void {
    afterNextRender(
      () => {
        const header = this.elementRef.nativeElement.querySelector<HTMLElement>(`#${this.idPrefix}-open`);
        header?.scrollIntoView({ block: 'nearest' });
        header?.focus();
      },
      { injector: this.injector },
    );
  }
}
