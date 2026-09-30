import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpCardComponent,
  CpFieldComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import {
  RecipeTestRun,
  ResolveTestIssueRequest,
  ResolvedTestIssue,
  TestIssue,
  TestIssueResolution,
  TestIssueResolutionKind,
  TestIssueSeverity,
} from '../../models/recipe-test-run.models';
import { RecipeTestRunService, ResolveTestIssueOutcome } from '../../services/recipe-test-run.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { roleAllows } from '../../models/recipe-readiness.models';

type ResolveState =
  | { readonly status: 'idle' }
  | { readonly status: 'submitting' }
  /** Refused on shape or on a rule about the named version. What was typed stays on screen. */
  | { readonly status: 'invalid'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  /** Somebody recorded a decision first. Immutable once written, so retrying can never change it. */
  | { readonly status: 'already_resolved' }
  | { readonly status: 'key_conflict' }
  | { readonly status: 'unavailable' };

export const TEST_ISSUE_SEVERITY_SHORT: Readonly<Record<TestIssueSeverity, string>> = {
  Minor: 'Minor',
  Major: 'Major',
  Blocking: 'Blocking',
};

/**
 * A tone per severity. Descriptive of the recipe's fitness as a draft, never of food safety — a `Blocking`
 * issue means the creator should not treat this version as a finished source, and the absence of one is not a
 * safety clearance.
 */
const SEVERITY_TONES: Readonly<Record<TestIssueSeverity, CpStatusPillTone>> = {
  Minor: 'neutral',
  Major: 'warning',
  Blocking: 'error',
};

export const RESOLUTION_KIND_LABELS: Readonly<Record<TestIssueResolutionKind, string>> = {
  Fixed: 'Fixed',
  WontFix: 'Not going to change it',
  NotReproduced: 'Could not reproduce it',
};

const RESOLUTION_KIND_ORDER: readonly TestIssueResolutionKind[] = ['Fixed', 'WontFix', 'NotReproduced'];

/** Mirrors TestRunPolicy.ResolutionNotesMaxLength and PredatingVersionOverrideReasonMaxLength. */
const RESOLUTION_NOTES_MAX_LENGTH = 4000;
const OVERRIDE_REASON_MAX_LENGTH = 1000;

let nextInstance = 0;

/**
 * What one test found wrong, and what was done about it (TEST-UI-002).
 *
 * **The test arrives as an input and is never fetched here.** No route reads one test back — the history lists
 * summaries with counts, not contents — so whoever holds a `RecipeTestRun` supplies it. Until such a read
 * exists this panel is reachable only from a client that has just written the test it is showing.
 *
 * **Observations are shown and never touched.** An observation is what the tester noticed, and resolving an
 * issue does not rewrite it; the linked note appears as context for the problem, read-only.
 *
 * **A resolution is write-once.** Recorded here by an Editor, it is then read and never edited — which is why
 * a resolved issue offers no controls at all, and why `already_resolved` is a refusal no retry can clear.
 */
@Component({
  selector: 'cp-recipe-test-issues',
  standalone: true,
  imports: [DatePipe, FormsModule, CpButtonComponent, CpCardComponent, CpFieldComponent, CpStatusPillComponent],
  templateUrl: './recipe-test-issues.component.html',
  styleUrl: './recipe-test-issues.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeTestIssuesComponent {
  private readonly testRunService = inject(RecipeTestRunService);
  private readonly membershipService = inject(WorkspaceMembershipService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The test whose problems these are. Supplied, never fetched. */
  readonly testRun = input.required<RecipeTestRun>();

  /**
   * The version that was cooked, as the history lists it. Copy only — used to say which version a correction
   * has to come after.
   */
  readonly testedVersionNumber = input<number | null>(null);

  readonly resolved = output<ResolvedTestIssue>();

  /**
   * The version carrying a correction, activated by the reader.
   *
   * An output rather than a link: only the host knows where a version can be shown, and a dead link is worse
   * than a named version.
   */
  readonly versionActivated = output<string>();

  readonly idPrefix = `cp-test-issues-${nextInstance++}`;
  readonly severityLabels = TEST_ISSUE_SEVERITY_SHORT;
  readonly resolutionKindLabels = RESOLUTION_KIND_LABELS;
  readonly resolutionKindOptions = RESOLUTION_KIND_ORDER;
  readonly notesMaxLength = RESOLUTION_NOTES_MAX_LENGTH;
  readonly overrideReasonMaxLength = OVERRIDE_REASON_MAX_LENGTH;

  /** The issue whose resolve form is open, or null. One at a time: a decision deserves attention. */
  private readonly resolvingIssueIdSignal = signal<string | null>(null);
  readonly resolvingIssueId = this.resolvingIssueIdSignal.asReadonly();

  readonly kind = signal<TestIssueResolutionKind | ''>('');
  readonly notes = signal('');
  readonly versionNumber = signal('');
  readonly overrideReason = signal('');

  private readonly stateSignal = signal<ResolveState>({ status: 'idle' });
  readonly state = this.stateSignal.asReadonly();

  /**
   * Resolutions this panel has just written, by issue id.
   *
   * The input is the test as it was handed over and is not rewritten here, so a decision recorded in this
   * session is merged over it. The host also hears about it through {@link resolved} and may replace the input
   * with a fresher read.
   */
  private readonly writtenResolutions = signal<ReadonlyMap<string, TestIssueResolution>>(new Map());

  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  constructor() {
    void this.membershipService.ensureLoaded();
  }

  // -------------------------------------------------------------------------
  // Reading
  // -------------------------------------------------------------------------

  readonly issues = computed<readonly TestIssue[]>(() =>
    [...this.testRun().issues].sort((left, right) => left.sortOrder - right.sortOrder),
  );

  readonly hasIssues = computed(() => this.issues().length > 0);

  readonly unresolvedCount = computed(() => this.issues().filter((issue) => this.resolutionFor(issue) === null).length);

  /** The resolution on record, whether it came with the test or was written here. */
  resolutionFor(issue: TestIssue): TestIssueResolution | null {
    return this.writtenResolutions().get(issue.id) ?? issue.resolution;
  }

  severityTone(severity: TestIssueSeverity): CpStatusPillTone {
    return SEVERITY_TONES[severity];
  }

  /** The tester's note this problem was raised from, when it came from one. */
  observationTextFor(issue: TestIssue): string {
    if (issue.testObservationId === null) return '';

    return this.testRun().observations.find((observation) => observation.id === issue.testObservationId)?.text ?? '';
  }

  activateVersion(versionId: string): void {
    this.versionActivated.emit(versionId);
  }

  // -------------------------------------------------------------------------
  // Resolving
  // -------------------------------------------------------------------------

  /** Recording a decision carries the Editor bar, a step above reporting a problem. */
  readonly canResolve = computed(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role
      ?? null;

    return roleAllows(role, 'Editor');
  });

  readonly submitting = computed(() => this.state().status === 'submitting');

  /** Whether trying the same resolution again could ever succeed. */
  readonly canRetry = computed(() => {
    switch (this.state().status) {
      case 'already_resolved':
      case 'forbidden':
      case 'not_found':
        return false;
      default:
        return true;
    }
  });

  openResolve(issue: TestIssue): void {
    this.resolvingIssueIdSignal.set(issue.id);
    this.stateSignal.set({ status: 'idle' });
    this.kind.set('');
    this.notes.set('');
    this.versionNumber.set('');
    this.overrideReason.set('');
    this.pendingIdempotencyKey = null;
    this.pendingRequestSignature = null;
  }

  cancelResolve(): void {
    this.resolvingIssueIdSignal.set(null);
    this.stateSignal.set({ status: 'idle' });
  }

  kindFromValue(value: string): TestIssueResolutionKind | '' {
    return RESOLUTION_KIND_ORDER.find((kind) => kind === value) ?? '';
  }

  /**
   * Whether a correction version may be named at all.
   *
   * Only a fix names one — a statement of the contract, not a rule evaluated here: the server refuses the same
   * combination and the database has a constraint for it. Hiding the field for the other two kinds is the
   * honest way to say so, rather than offering a box whose contents can only be refused.
   */
  readonly canNameVersion = computed(() => this.kind() === 'Fixed');

  readonly canSubmit = computed(() => this.kind() !== '' && !this.submitting() && this.canRetry());

  fieldError(field: string): string {
    const state = this.state();
    return state.status === 'invalid' ? (state.fieldErrors[field]?.join(' ') ?? '') : '';
  }

  readonly stateMessage = computed(() => {
    switch (this.state().status) {
      case 'invalid':
        return 'That resolution was not recorded. What needs changing is marked below.';
      case 'already_resolved':
        return 'Somebody has already recorded what was done about this. A resolution cannot be replaced.';
      case 'forbidden':
        return 'You need the Editor role in this workspace to decide an issue is dealt with.';
      case 'not_found':
        return 'This test or this issue is no longer there.';
      case 'key_conflict':
        return 'That resolution was already used for something else. Try again.';
      case 'unavailable':
        return 'The resolution could not be recorded. Nothing has been written — try again.';
      default:
        return '';
    }
  });

  async submitResolve(issue: TestIssue): Promise<void> {
    if (!this.canSubmit() || this.resolvingIssueId() !== issue.id) return;

    const version = this.versionNumber().trim();
    const override = this.overrideReason().trim();
    const request: ResolveTestIssueRequest = {
      kind: this.kind() as TestIssueResolutionKind,
      notes: this.notes().trim().length === 0 ? null : this.notes().trim(),
      // Only ever sent for a fix, and only when one was typed. `Number` is safe here because the control is a
      // number input, which hands over an empty string for anything it could not parse.
      resolutionVersionNumber: this.canNameVersion() && version.length > 0 ? Number(version) : null,
      predatingVersionOverrideReason: override.length === 0 ? null : override,
    };

    this.stateSignal.set({ status: 'submitting' });

    const outcome = await this.testRunService.resolveIssue(
      this.workspaceSlug(),
      this.recipeId(),
      this.testRun().id,
      issue.id,
      request,
      this.idempotencyKeyFor(issue.id, request),
    );

    this.applyOutcome(issue, outcome);
  }

  private applyOutcome(issue: TestIssue, outcome: ResolveTestIssueOutcome): void {
    switch (outcome.status) {
      case 'resolved':
        this.writtenResolutions.update((written) => {
          const next = new Map(written);
          next.set(outcome.resolved.testIssueId, outcome.resolved.resolution);
          return next;
        });
        this.resolvingIssueIdSignal.set(null);
        this.stateSignal.set({ status: 'idle' });
        this.pendingIdempotencyKey = null;
        this.pendingRequestSignature = null;
        this.resolved.emit(outcome.resolved);
        return;

      case 'validation_failed':
      case 'version_not_found':
        // Both name a field — the second names `resolutionVersionNumber` — so both belong beside the control
        // rather than in a sentence about the request as a whole.
        this.stateSignal.set({ status: 'invalid', fieldErrors: outcome.fieldErrors });
        return;

      case 'already_resolved':
        this.stateSignal.set({ status: 'already_resolved' });
        return;

      case 'forbidden':
        this.stateSignal.set({ status: 'forbidden' });
        return;

      case 'not_found':
        this.stateSignal.set({ status: 'not_found' });
        return;

      case 'idempotency_key_conflict':
        this.pendingIdempotencyKey = null;
        this.pendingRequestSignature = null;
        this.stateSignal.set({ status: 'key_conflict' });
        return;

      default:
        void issue;
        this.stateSignal.set({ status: 'unavailable' });
    }
  }

  /** Reuses the in-flight key for a retry of the identical resolution; a changed one is a new operation. */
  private idempotencyKeyFor(issueId: string, request: ResolveTestIssueRequest): string {
    const signature = JSON.stringify([this.testRun().id, issueId, request]);

    if (this.pendingIdempotencyKey !== null && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }

    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;
    return key;
  }
}
