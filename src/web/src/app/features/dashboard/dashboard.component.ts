import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest, firstValueFrom, map } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpCardGridComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpStatusPillComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { injectRouteTitle } from '../../core/route-title';
import { QuickCaptureService } from '../../shared/quick-capture/quick-capture.service';
import { estimateWords } from '../../shared/work-tasks/work-task-presentation';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { CONTENT_BOARD_COLUMN_LABELS } from '../../models/content-board.models';
import { DAM_ASSET_KIND_LABELS } from '../../models/dam-asset.models';
import { Dashboard, DashboardPublishingItem, DashboardSection, DashboardTask, DashboardWorkPlan, aiTaskLabel } from '../../models/dashboard.models';
import { RECIPE_STATUS_LABELS } from '../../models/recipe-readiness.models';
import { DashboardOutcome, DashboardService } from '../../services/dashboard.service';
import { PUBLISHING_STATUS_WORDS, SEVERITY_WORDS, TASK_STATUS_WORDS, attentionWords } from './dashboard-presentation';
import { platformName } from '../../shared/publishing-names';

type State =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly dashboard: Dashboard }
  | { readonly status: 'not_found' }
  | { readonly status: 'error' };

/**
 * The workspace at a glance (`:workspaceSlug/dashboard`, HOME-UI-001/002), over 13.11's read model.
 *
 * **A detail page about the workspace, with its topics in a grid** (DESIGN-SYSTEM.md, "A dashboard"): one
 * head, one fixed place for messages, then a `cp-topic-card` per topic, ordered from what needs the creator now
 * to what is worth a glance. On a phone it is one column in that order.
 *
 * **Every number is the server's.** The page shows the counts it was given and derives none: no total is added
 * up here, and nothing is drawn as a chart, because a count and a link to the thing are what a creator acts on.
 *
 * **Each topic stands alone.** A section the server could not read keeps its card and says so inside it, with
 * a way to try again; the other cards are untouched. A topic with nothing in it says so in its own words.
 *
 * **Every item opens where it can be dealt with.** A blocked recipe opens on its Readiness tab and a test issue
 * on its Tests tab; a post opens the card it was published from, where its publishing controls are. A guided
 * workflow opens at its run, which resumes on the step it is on, and a task opens in My Day at itself. An item
 * with nowhere of its own to go is listed without a link rather than with a link to somewhere vaguer.
 *
 * **Two topics are the creator's own** (13A.33a): the workflows to carry on with and the day's work plan. They
 * come first, because they are what the creator came here to find — what they were in the middle of, and what
 * today holds — and the workspace's own topics follow.
 */
@Component({
  selector: 'cp-dashboard',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpCardGridComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpStatusPillComponent,
    CpTopicCardComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardComponent {
  protected readonly pageTitle = injectRouteTitle('Dashboard');
  private readonly service = inject(DashboardService);
  private readonly quickCapture = inject(QuickCaptureService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  private readonly head = viewChild.required(CpPageHeadComponent);

  readonly workspaceSlug = signal('');
  protected readonly state = signal<State>({ status: 'loading' });
  protected readonly refreshing = signal(false);

  /** What the last refresh has to say, in the page's one place for messages. Empty when nothing. */
  protected readonly message = signal('');
  protected readonly messageTone = signal<'neutral' | 'warning'>('neutral');

  protected readonly recipeStatusLabels = RECIPE_STATUS_LABELS;
  protected readonly columnLabels = CONTENT_BOARD_COLUMN_LABELS;
  protected readonly kindLabels = DAM_ASSET_KIND_LABELS;
  protected readonly publishingWords = PUBLISHING_STATUS_WORDS;
  protected readonly taskWords = TASK_STATUS_WORDS;
  protected readonly attentionWords = attentionWords;
  protected readonly estimateWords = estimateWords;
  protected readonly severityWords = SEVERITY_WORDS;
  protected readonly aiTaskLabel = aiTaskLabel;
  protected readonly platformName = platformName;

  protected readonly dashboard = computed(() => {
    const state = this.state();

    return state.status === 'ready' ? state.dashboard : null;
  });

  /**
   * A workspace with nothing in it yet: no recipe in progress, no card on the board, no picture in the library,
   * no guided workflow under way and nothing kept on the creator's own plan. Read from the server's own zeros;
   * nothing is counted here.
   *
   * The plan is unavailable until a zone is set, and a workspace without one is new enough for that to be no
   * evidence either way, so an unreadable plan does not keep the first-use card from showing.
   */
  protected readonly firstUse = computed(() => {
    const dashboard = this.dashboard();
    if (dashboard === null) return false;

    const { recipesInProgress, board, recentUploads, workflows, workPlan } = dashboard;
    const noPlan =
      workPlan.status !== 'ready' ||
      (workPlan.data.overdueCount === 0 &&
        workPlan.data.todayCount === 0 &&
        workPlan.data.unplannedCount === 0 &&
        workPlan.data.thisWeek.plannedTaskCount === 0 &&
        workPlan.data.thisWeek.inProgressCount === 0 &&
        workPlan.data.thisWeek.waitingCount === 0);

    return (
      recipesInProgress.status === 'ready' &&
      recipesInProgress.data.counts.every((count) => count.count === 0) &&
      board.status === 'ready' &&
      board.data.total === 0 &&
      recentUploads.status === 'ready' &&
      recentUploads.data.length === 0 &&
      workflows.status === 'ready' &&
      workflows.data.inProgressCount === 0 &&
      workflows.data.needsAttentionCount === 0 &&
      noPlan
    );
  });

  /**
   * Whether the creator may keep work here — Contributor and above, as My Day decides it. A Viewer sees their
   * plan, which can still hold work handed to them before they were made one, and no capture control.
   */
  protected readonly canAct = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const membership = state.memberships.find((candidate) => candidate.workspaceSlug === this.workspaceSlug());

    return membership !== undefined && membership.role !== 'Viewer';
  });

  protected readonly link = (...segments: readonly string[]): readonly string[] => ['/', this.workspaceSlug(), ...segments];

  constructor() {
    void this.memberships.ensureLoaded();

    // A task captured from this page belongs in its counts straight away. The dialog has already said it
    // saved, so this is a silent re-read: no second announcement, and nothing moves under the creator.
    this.quickCapture.captured$.pipe(takeUntilDestroyed()).subscribe(() => void this.read(this.workspaceSlug()));

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          for (const params of maps) {
            const value = params.get('workspaceSlug');
            if (value) return value;
          }

          return null;
        }),
        takeUntilDestroyed(),
      )
      .subscribe((slug) => {
        if (slug === null) throw new Error('DashboardComponent route is missing a workspaceSlug segment.');

        // Another workspace: nothing of the last one stays on screen while the next is read.
        this.workspaceSlug.set(slug);
        this.message.set('');
        this.state.set({ status: 'loading' });
        void this.read(slug);
      });
  }

  /**
   * Read everything again. The page keeps showing what it has until the answer comes, and says what happened
   * in its one place for messages. Focus is never dropped: the control that asked stays where it is, and a
   * page that was an error and is now a dashboard moves focus to its heading.
   */
  protected async refresh(): Promise<void> {
    if (this.refreshing()) return;

    const wasShowing = this.state().status === 'ready';
    this.refreshing.set(true);
    this.message.set('');

    const outcome = await this.read(this.workspaceSlug());
    this.refreshing.set(false);

    if (outcome === 'found') {
      this.messageTone.set('neutral');
      this.message.set('Your dashboard is up to date.');
      if (!wasShowing) afterNextRender(() => this.head().focusHeading(), { injector: this.injector });
    } else if (wasShowing && outcome === 'unavailable') {
      this.messageTone.set('warning');
      this.message.set("Your dashboard couldn't be refreshed just now. What you see is from the last time it was read.");
    }
  }

  /**
   * An instant as the workspace sees it: in its own zone, or UTC when it has none — the zone "today" is in,
   * so a time and a day on this page never disagree. The date pipe takes offsets, not zone names, so this is
   * `Intl`'s to do.
   */
  protected when(instant: string, style: 'time' | 'day' | 'dayTime'): string {
    const options: Intl.DateTimeFormatOptions =
      style === 'time'
        ? { hour: 'numeric', minute: '2-digit' }
        : style === 'day'
          ? { month: 'short', day: 'numeric' }
          : { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' };

    return this.format(new Date(instant), { ...options, timeZone: this.dashboard()?.timeZoneId ?? 'UTC' });
  }

  /**
   * A calendar day (`yyyy-MM-dd`), already the workspace's own: shown as that day for every reader. Read as UTC
   * midnight and formatted in UTC, because a bare date parsed any other way lands on the previous day for a
   * reader west of UTC or the next for one east of it.
   */
  protected day(date: string, style: 'short' | 'weekday' | 'long'): string {
    const options: Intl.DateTimeFormatOptions =
      style === 'short'
        ? { month: 'short', day: 'numeric' }
        : style === 'weekday'
          ? { weekday: 'short', month: 'short', day: 'numeric' }
          : { weekday: 'long', month: 'long', day: 'numeric' };

    return this.format(new Date(`${date}T00:00:00Z`), { ...options, timeZone: 'UTC' });
  }

  /** A section's data when it could be read, or null when it could not. */
  protected data<T>(section: DashboardSection<T>): T | null {
    return section.status === 'ready' ? section.data : null;
  }

  /** Whether this task is one of the three the creator chose to do first today (13A.32). */
  protected chosen(plan: DashboardWorkPlan, task: DashboardTask): boolean {
    return plan.topThree.includes(task.id);
  }

  /**
   * Quick capture: the shell's one dialog, started on the day this page is about. The button is focused first
   * so the dialog gives focus back to it.
   */
  protected captureTask(button: HTMLElement): void {
    button.focus();
    this.quickCapture.open({ today: this.dashboard()?.today ?? undefined });
  }

  protected postTitle(item: DashboardPublishingItem): string {
    return item.contentCardTitle ?? `A post to ${platformName(item.platform)}`;
  }

  private format(value: Date, options: Intl.DateTimeFormatOptions): string {
    if (Number.isNaN(value.getTime())) return '';

    try {
      return new Intl.DateTimeFormat('en-US', options).format(value);
    } catch {
      // A zone this browser does not know: the instant in UTC rather than nothing.
      return new Intl.DateTimeFormat('en-US', { ...options, timeZone: 'UTC' }).format(value);
    }
  }

  private async read(slug: string): Promise<DashboardOutcome['status'] | 'superseded'> {
    const outcome = await firstValueFrom(this.service.get(slug));
    if (this.workspaceSlug() !== slug) return 'superseded';

    switch (outcome.status) {
      case 'found':
        this.state.set({ status: 'ready', dashboard: outcome.dashboard });
        break;
      case 'not_found':
        this.state.set({ status: 'not_found' });
        break;
      default:
        // A refresh that failed keeps what is on screen; only a first read with nothing to show is an error.
        if (this.state().status !== 'ready') this.state.set({ status: 'error' });
    }

    return outcome.status;
  }

  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}
