import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { EMPTY, Observable, defer, expand, switchMap, timer } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpNoticeComponent,
  CpNoticeTone,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpTabDefinition,
  CpTabPanelComponent,
  CpTabsComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import {
  BrandStyleTestDrive,
  BrandStyleTestDriveComparison,
  STYLE_TEST_DRIVE_SUBJECT_MAX,
} from '../../models/brand-style-test-drive.models';
import { AiUsageService } from '../../services/ai-usage.service';
import {
  BrandStyleTestDriveService,
  RequestTestDriveOutcome,
  WatchTestDriveOutcome,
} from '../../services/brand-style-test-drive.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../shared/allowance-gate';

/** Asking writes an operation and spends an allowance, so it carries the same bar as any other generation. */
const REQUEST_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

const COULD_NOT_REACH_SERVER = "Couldn't reach the server. Try again in a moment.";

/** Mirrors the poll constants every other AI request screen uses. */
const POLL_INTERVAL_MS = 2000;
const DEGRADED_POLL_INTERVAL_MS = 8000;
const MAX_CONSECUTIVE_POLL_FAILURES = 3;
const POLL_CEILING_MS = 5 * 60 * 1000;

/** What each sample is called on screen. The keys are the server's own names for the three. */
const SAMPLE_LABELS: Readonly<Record<string, string>> = {
  blogIntro: 'Blog opening',
  socialCaption: 'Social caption',
  imagePrompt: 'Photo direction',
};

/**
 * "Test my style": the same three pieces written without a creator's guide and with it, side by side.
 *
 * **Arriving costs nothing.** No request is made on load, on a refresh, or when the subject is typed into.
 * The one control that spends anything is behind a confirmation that says what it will spend, and the
 * allowance on screen is read before it rather than discovered after.
 *
 * **Two generations, said out loud.** The server writes the left column with no brand context at all and the
 * right from the version named, which is the only way the left column can honestly be labelled "without your
 * guide" — and it is two provider calls, so the confirmation says so.
 *
 * **Nothing here can be saved.** There is no accept, no apply and no route that would take one; the screen
 * says as much rather than leaving a creator looking for a button. Generated text is marked as generated
 * (frontend.md), and read-only is the honest state here because there is nothing to accept it into.
 *
 * **The guide is named, not flattered.** Beside the comparison the screen lists what the guide actually asked
 * for, in the creator's own words, and the examples the request was sent with — the server's own account of
 * the grounding. Where that account may have moved since the samples were written, the screen says so instead
 * of showing current wording beside older writing.
 */
@Component({
  selector: 'cp-brand-style-test-drive',
  standalone: true,
  imports: [
    RouterLink,
    AiAllowanceNoticeComponent,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    CpTabPanelComponent,
    CpTabsComponent,
  ],
  templateUrl: './brand-style-test-drive.component.html',
  styleUrl: './brand-style-test-drive.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandStyleTestDriveComponent {
  private readonly testDrives = inject(BrandStyleTestDriveService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly usage = inject(AiUsageService);
  private readonly confirmService = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = this.routeParam('workspaceSlug');
  readonly guideId = this.routeParam('guideId');

  /**
   * Which version to try, from `?version=`.
   *
   * A query parameter rather than a path segment because it is the one thing a creator changes without
   * leaving: the history links here per row, and a creator comparing two versions runs this twice.
   */
  readonly versionNumber = Number.parseInt(this.route.snapshot.queryParamMap.get('version') ?? '', 10);

  readonly subjectMax = STYLE_TEST_DRIVE_SUBJECT_MAX;
  readonly subject = signal('');

  private readonly submitting = signal(false);
  private readonly requestIdSignal = signal<string | null>(null);
  readonly requestId = this.requestIdSignal.asReadonly();

  private readonly testDriveSignal = signal<BrandStyleTestDrive | null>(null);
  readonly testDrive = this.testDriveSignal.asReadonly();

  private readonly refusalSignal = signal<string | null>(null);
  readonly refusal = this.refusalSignal.asReadonly();

  private readonly quotaRefusalSignal = signal<AiQuotaRefusal | null>(null);
  readonly quotaRefusal = this.quotaRefusalSignal.asReadonly();

  private readonly consecutiveFailures = signal(0);

  /** Held across retries of one logical ask, so a dropped response does not buy a second pair of samples. */
  private pendingRequestKey: string | null = null;

  /** The account's allowance, so a spent one is said before the ask rather than only after it. */
  readonly allowance = this.usage.allowance;

  constructor() {
    void this.memberships.ensureLoaded();

    toObservable(this.requestId)
      .pipe(
        switchMap((requestId) => (requestId === null ? EMPTY : this.pollLoop(requestId))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({ next: (outcome) => this.applyWatchOutcome(outcome) });
  }

  // ---- what the screen can offer ----

  readonly rolesKnown = computed(() => this.memberships.state().status === 'ready');

  readonly canRequest = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug)?.role;

    return role !== undefined && REQUEST_ROLES.includes(role);
  });

  /** A version number the route did not carry is not a version to try, and nothing is offered against it. */
  readonly hasVersion = computed(() => Number.isInteger(this.versionNumber) && this.versionNumber > 0);

  readonly canSubmit = computed(
    () =>
      this.hasVersion()
      && this.canRequest()
      && !this.submitting()
      && !allowanceBlocksNewRequests(this.allowance()),
  );

  readonly working = computed(() => {
    const status = this.testDrive()?.status;

    return this.submitting() || status === 'Requested' || status === 'Running';
  });

  readonly comparison = computed<BrandStyleTestDriveComparison | null>(() => this.testDrive()?.comparison ?? null);

  readonly failed = computed(() => this.testDrive()?.status === 'Failed');

  /** Degraded rather than broken: the samples may still be coming, and the screen says which it is. */
  readonly connectionDegraded = computed(() => this.consecutiveFailures() > 0);

  readonly tabs = computed<CpTabDefinition[]>(() =>
    (this.comparison()?.samples ?? []).map((pair) => ({
      id: `cp-style-sample-${pair.sample}`,
      label: SAMPLE_LABELS[pair.sample] ?? pair.sample,
    })),
  );

  readonly statusTone = computed<CpStatusPillTone>(() => {
    if (this.failed()) return 'error';
    if (this.comparison() !== null) return 'success';

    return this.working() ? 'progress' : 'neutral';
  });

  readonly statusLabel = computed(() => {
    if (this.failed()) return 'Could not write the samples';
    if (this.comparison() !== null) return 'Samples ready';
    if (this.working()) return 'Writing both versions';

    return 'Nothing asked for yet';
  });

  /**
   * One line of status, read from a live region that is always in the DOM.
   *
   * Empty before anything is asked for, and the region still renders — an `aria-live` element inserted
   * already populated is not reliably announced, so a creator who pressed the button would hear nothing when
   * the samples landed.
   */
  readonly statusMessage = computed(() => {
    if (this.failed()) return 'The samples could not be written. Nothing was saved.';
    if (this.comparison() !== null) return 'Both versions are ready to read.';
    if (this.working()) {
      return this.connectionDegraded()
        ? 'Still writing. The connection is slow, so this screen is checking less often.'
        : 'Writing one version without your guide and one with it.';
    }

    return '';
  });

  /** What the failure means for the creator, in their terms rather than the category's. */
  readonly failureMessage = computed(() => {
    const category = this.testDrive()?.failureCategory;

    switch (category) {
      case 'DomainInvalid':
        return 'That version of your guide does not have enough written in it to show a difference yet.';
      case 'OutputSchemaInvalid':
        return 'The samples came back in a shape we could not read, so none of them is being shown.';
      case 'Quota':
      case 'AccountSuspended':
        return 'Your AI allowance could not cover this.';
      default:
        return 'Something went wrong while writing the samples. Nothing was saved, and you can try again.';
    }
  });

  readonly groundingNoticeTone = computed<CpNoticeTone>(() => 'warning');

  protected label(sample: string): string {
    return SAMPLE_LABELS[sample] ?? sample;
  }

  // ---- asking ----

  /**
   * Asks, once the creator has confirmed what it costs.
   *
   * The confirmation is not a formality: it is where "two generations against your allowance" and "nothing is
   * saved to your content" are said, before anything is spent. Declining spends nothing and changes nothing.
   */
  async submit(): Promise<void> {
    if (!this.canSubmit()) return;

    const agreed = await this.confirmService.confirm({
      title: 'Write both versions?',
      // What it costs, stated before it is spent (audit 11A.24a, S5). The ceiling is disclosed rather than
      // discovered: each half is allowed one more attempt if its answer comes back malformed, so two calls is
      // the ordinary case and four is the most it can be.
      message:
        `This writes a blog opening, a caption and a photo direction twice — once with no guidance at all, and `
        + `once from version ${this.versionNumber} of your guide. That is two short pieces of AI work against `
        + 'your allowance — up to four if an answer comes back malformed and we have to ask again. Nothing is '
        + 'saved to your recipes, posts or guide: these are samples to read.',
      confirmLabel: 'Write both versions',
      cancelLabel: 'Not now',
      tone: 'neutral',
    });

    if (!agreed) return;

    this.submitting.set(true);
    this.refusalSignal.set(null);
    this.quotaRefusalSignal.set(null);
    this.consecutiveFailures.set(0);

    this.pendingRequestKey ??= crypto.randomUUID();

    try {
      const outcome = await this.testDrives.request(
        this.workspaceSlug,
        {
          guideId: this.guideId,
          versionNumber: this.versionNumber,
          subject: this.subject(),
        },
        this.pendingRequestKey,
      );

      this.apply(outcome);
    } finally {
      this.submitting.set(false);
    }
  }

  /** Clears the result so another can be asked for. Nothing on the server is touched. */
  startAnother(): void {
    this.requestIdSignal.set(null);
    this.testDriveSignal.set(null);
    this.refusalSignal.set(null);
    this.quotaRefusalSignal.set(null);
    this.consecutiveFailures.set(0);
    this.pendingRequestKey = null;
  }

  protected setSubject(event: Event): void {
    this.subject.set((event.target as HTMLInputElement).value);
  }

  private apply(outcome: RequestTestDriveOutcome): void {
    switch (outcome.status) {
      case 'accepted':
        this.pendingRequestKey = null;
        this.testDriveSignal.set(outcome.testDrive);
        this.requestIdSignal.set(outcome.testDrive.requestId);

        // Both runs have been paid for, so the balance on screen is already out of date.
        void this.usage.refresh();
        return;

      // Handed to the allowance notice, which is the one place that words a spent allowance and a switched-off
      // account differently. The key is released because this attempt reached the server and was answered: a
      // retry is a new request, not a re-send.
      case 'quota_exhausted':
      case 'account_suspended':
        this.pendingRequestKey = null;
        this.quotaRefusalSignal.set(outcome);
        void this.usage.refresh();
        return;

      case 'guide_has_no_guidance':
        this.pendingRequestKey = null;
        this.refusalSignal.set(
          'There is nothing written in this version of your guide yet, so both versions would read the same. '
            + 'Write a part of your guide and try again — nothing was spent.',
        );
        return;

      case 'task_not_enabled':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Trying your style out is not switched on for this workspace yet.');
        return;

      case 'validation_failed':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That request could not be made as asked. Check what you typed and try again.');
        return;

      case 'idempotency_key_conflict':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That request clashed with another one. Try again.');
        return;

      case 'forbidden':
        this.pendingRequestKey = null;
        this.refusalSignal.set('Your role in this workspace cannot ask for this.');
        return;

      case 'not_found':
        this.pendingRequestKey = null;
        this.refusalSignal.set('That version of that guide could not be found.');
        return;

      // Deliberately keeps the key: nobody answered, so a retry continues the same ask rather than buying a
      // second pair of samples.
      case 'unavailable':
        this.refusalSignal.set(COULD_NOT_REACH_SERVER);
        return;
    }
  }

  /** Mirrors the other request screens' loops — see `ai-proposal-panel.component.ts` for why `expand`. */
  private pollLoop(requestId: string): Observable<WatchTestDriveOutcome> {
    const deadline = Date.now() + POLL_CEILING_MS;
    let failures = 0;

    const ask = (): Observable<WatchTestDriveOutcome> =>
      this.testDrives.watchStatus(this.workspaceSlug, requestId);

    const again = (outcome: WatchTestDriveOutcome): Observable<WatchTestDriveOutcome> => {
      if (outcome.status === 'not_found' || outcome.status === 'forbidden') return EMPTY;

      if (outcome.status === 'found') {
        const status = outcome.testDrive.status;
        if (status !== 'Requested' && status !== 'Running') return EMPTY;
      }

      failures = outcome.status === 'unavailable' ? failures + 1 : 0;
      if (failures >= MAX_CONSECUTIVE_POLL_FAILURES) return EMPTY;
      if (Date.now() >= deadline) return EMPTY;

      return timer(failures === 0 ? POLL_INTERVAL_MS : DEGRADED_POLL_INTERVAL_MS).pipe(switchMap(ask));
    };

    return defer(ask).pipe(expand(again));
  }

  private applyWatchOutcome(outcome: WatchTestDriveOutcome): void {
    if (outcome.status === 'found') {
      this.consecutiveFailures.set(0);
      this.testDriveSignal.set(outcome.testDrive);
      return;
    }

    if (outcome.status === 'unavailable') {
      this.consecutiveFailures.update((count) => count + 1);
      return;
    }

    this.refusalSignal.set(
      outcome.status === 'not_found'
        ? 'That test drive is no longer there.'
        : 'Your role in this workspace cannot read this.',
    );
  }

  private routeParam(name: string): string {
    return this.route.snapshot.paramMap.get(name) ?? '';
  }
}
