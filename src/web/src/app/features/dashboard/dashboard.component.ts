import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  Signal,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest, firstValueFrom, map } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpCardGridComponent,
  CpEmptyStateComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpSkeletonComponent,
  CpStatTileComponent,
  CpStatusPillComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { injectRouteTitle } from '../../core/route-title';
import { QuickCaptureService } from '../../shared/quick-capture/quick-capture.service';
import { estimateWords } from '../../shared/work-tasks/work-task-presentation';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { CONTENT_BOARD_COLUMN_LABELS } from '../../models/content-board.models';
import { DAM_ASSET_KIND_LABELS } from '../../models/dam-asset.models';
import {
  Dashboard,
  DashboardPublishingItem,
  DashboardRecipesInProgress,
  DashboardSection,
  DashboardTask,
  DashboardWorkPlan,
  aiTaskLabel,
} from '../../models/dashboard.models';
import { RECIPE_STATUS_LABELS } from '../../models/recipe-readiness.models';
import { DashboardOutcome, DashboardService } from '../../services/dashboard.service';
import { DASHBOARD_TOPIC_KEYS, DashboardTopicDirective, DashboardTopicKey } from './dashboard-topic.directive';
import {
  PUBLISHING_STATUS_WORDS,
  SEVERITY_WORDS,
  TASK_STATUS_WORDS,
  attentionWords,
  runLinkLabel,
  runName,
} from './dashboard-presentation';
import { platformName } from '../../shared/publishing-names';

/**
 * One figure in the row across the top of the page (AU.4.1, APP-008): the server's own count as text, what it
 * counts in words, and — where there is no figure to show — which of the two reasons there is none. A part
 * that could not be read keeps its tile and says so; a part that needs a day the workspace has not got says
 * that instead, because there is nothing wrong and nothing to retry.
 */
export interface DashboardFigure {
  readonly key: 'late' | 'publishing' | 'tests' | 'proposals';
  readonly value: string;
  readonly label: string;
  readonly context: string;
  readonly missing: 'read' | 'zone' | null;
}

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
    CpEmptyStateComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpSkeletonComponent,
    CpStatTileComponent,
    CpStatusPillComponent,
    CpTopicCardComponent,
    DashboardTopicDirective,
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
  protected readonly runName = runName;
  protected readonly runLinkLabel = runLinkLabel;

  protected readonly dashboard = computed(() => {
    const state = this.state();

    return state.status === 'ready' ? state.dashboard : null;
  });

  /** Each part of the read model, or null when the server could not read that part. */
  protected readonly workflows = this.part((dashboard) => dashboard.workflows);
  protected readonly workPlan = this.part((dashboard) => dashboard.workPlan);
  protected readonly recipes = this.part((dashboard) => dashboard.recipesInProgress);
  protected readonly readiness = this.part((dashboard) => dashboard.readinessBlockers);
  protected readonly proposals = this.part((dashboard) => dashboard.aiProposals);
  protected readonly tests = this.part((dashboard) => dashboard.testFollowUps);
  protected readonly upcoming = this.part((dashboard) => dashboard.upcoming);
  protected readonly board = this.part((dashboard) => dashboard.board);
  protected readonly uploads = this.part((dashboard) => dashboard.recentUploads);
  protected readonly publishing = this.part((dashboard) => dashboard.publishing);

  /**
   * Whether a topic that needs a day has none because the workspace has no time zone set. Not the same as a
   * part that could not be read: there is nothing wrong and nothing to retry.
   */
  protected readonly needsZone = computed(() => this.dashboard()?.timeZoneState === 'NotSet');

  /** The topic cards as declared, which is the order the grid draws them in. */
  private readonly topics = viewChildren(DashboardTopicDirective);

  /** How many silhouettes the loading page draws where the figures and the topics will be. */
  protected readonly figurePlaces = [0, 1, 2, 3];
  protected readonly topicPlaces = [0, 1, 2, 3, 4, 5];

  /**
   * A workspace with nothing in it yet: nothing in progress, nothing on the board, nothing in the library,
   * no guided workflow, nothing published or waiting to publish, no test issue, no proposal, nothing coming
   * up and nothing on the creator's own plan. Read from the server's own zeros; nothing is counted here.
   *
   * **Every part has to agree.** A workspace whose recipes are all approved has none in progress, and judging
   * by that alone would greet a creator with a year of work behind them as though they had just arrived.
   *
   * **A part that could not be read is not evidence of emptiness**, so an unreadable part keeps the page from
   * claiming a first use — with one exception. The plan and what is coming up are unavailable until the
   * workspace has a time zone, and a workspace without one is new enough for that to be no evidence either
   * way. Once a zone is set, those two are read like any other part.
   */
  protected readonly firstUse = computed(() => {
    const dashboard = this.dashboard();
    if (dashboard === null) return false;

    const empty = <T>(section: DashboardSection<T>, nothingIn: (data: T) => boolean): boolean =>
      section.status === 'ready' && nothingIn(section.data);
    const emptyDayBound = <T>(section: DashboardSection<T>, nothingIn: (data: T) => boolean): boolean =>
      section.status === 'ready' ? nothingIn(section.data) : dashboard.timeZoneState === 'NotSet';

    return (
      empty(dashboard.recipesInProgress, (recipes) => recipes.counts.every((count) => count.count === 0)) &&
      empty(dashboard.board, (board) => board.total === 0) &&
      empty(dashboard.recentUploads, (uploads) => uploads.length === 0) &&
      empty(
        dashboard.workflows,
        (workflows) => workflows.inProgressCount === 0 && workflows.needsAttentionCount === 0,
      ) &&
      empty(
        dashboard.publishing,
        (publishing) => publishing.needsAttentionCount === 0 && publishing.counts.every((count) => count.count === 0),
      ) &&
      empty(dashboard.testFollowUps, (tests) => tests.open === 0) &&
      empty(dashboard.aiProposals, (proposals) => proposals.awaitingDecision === 0) &&
      emptyDayBound(dashboard.upcoming, (upcoming) => upcoming.cardCount === 0 && upcoming.postCount === 0) &&
      emptyDayBound(
        dashboard.workPlan,
        (plan) =>
          plan.overdueCount === 0 &&
          plan.todayCount === 0 &&
          plan.unplannedCount === 0 &&
          plan.thisWeek.plannedTaskCount === 0 &&
          plan.thisWeek.inProgressCount === 0 &&
          plan.thisWeek.waitingCount === 0,
      )
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

  /**
   * The row across the top: four of the server's own counts, each the answer to "does this need me now"
   * (APP-008). Every value is one field of the read model printed as it came; nothing here adds two counts
   * together, which is why there is no tile for recipes in progress (the read model sends a count per status
   * and no total), for blocked recipes (a list, with no count of its own) or for the picture library (the
   * most recent few, with no total).
   */
  protected readonly figures = computed<readonly DashboardFigure[]>(() => {
    const plan = this.workPlan();
    const publishing = this.publishing();
    const tests = this.tests();
    const proposals = this.proposals();
    // A plan is unavailable until a zone is set, which is the creator's to fix rather than ours to retry.
    const noPlan = this.needsZone() ? 'zone' : 'read';

    return [
      {
        key: 'late',
        value: plan === null ? '' : `${plan.overdueCount}`,
        label: plan?.overdueCount === 1 ? 'task late' : 'tasks late',
        context:
          plan === null ? '' : `${plan.todayCount} ${plan.todayCount === 1 ? 'task' : 'tasks'} planned for today.`,
        missing: plan === null ? noPlan : null,
      },
      {
        key: 'publishing',
        value: publishing === null ? '' : `${publishing.needsAttentionCount}`,
        label: publishing?.needsAttentionCount === 1 ? 'post needs a look' : 'posts need a look',
        context: '',
        missing: publishing === null ? 'read' : null,
      },
      {
        key: 'tests',
        value: tests === null ? '' : `${tests.open}`,
        label: tests?.open === 1 ? 'test issue open' : 'test issues open',
        context:
          tests === null || tests.open === 0
            ? ''
            : `${tests.blocking} blocking, ${tests.major} major and ${tests.minor} minor.`,
        missing: tests === null ? 'read' : null,
      },
      {
        key: 'proposals',
        value: proposals === null ? '' : `${proposals.awaitingDecision}`,
        label: proposals?.awaitingDecision === 1 ? 'AI proposal waiting for you' : 'AI proposals waiting for you',
        context: '',
        missing: proposals === null ? 'read' : null,
      },
    ];
  });

  /**
   * The one topic that matters most on the page (DESIGN-SYSTEM.md, "Warmth inside the app": at most one
   * featured card), taken from the page's own order of urgency — what is late, then what has failed, then
   * what is blocked, and last what the creator was in the middle of.
   *
   * Nothing is derived: each line asks whether one of the server's counts is above zero, and a part that
   * could not be read is passed over rather than guessed at. A workspace with nothing needing anyone features
   * nothing, and a workspace with nothing in it at all features "Get started" instead.
   */
  protected readonly featured = computed<DashboardTopicKey | null>(() => {
    if (this.dashboard() === null || this.firstUse()) return null;

    const plan = this.workPlan();
    if (plan !== null && plan.overdueCount > 0) return 'day';

    const publishing = this.publishing();
    if (publishing !== null && publishing.needsAttentionCount > 0) return 'publishing';

    const readiness = this.readiness();
    if (readiness !== null && readiness.blocked.length > 0) return 'readiness';

    const tests = this.tests();
    if (tests !== null && tests.blocking > 0) return 'tests';

    const workflows = this.workflows();
    if (workflows !== null && workflows.needsAttentionCount > 0) return 'stuck';

    const proposals = this.proposals();
    if (proposals !== null && proposals.awaitingDecision > 0) return 'proposals';

    return workflows !== null && workflows.inProgressCount > 0 ? 'continue' : null;
  });

  /** That topic's card, to draw above the grid. */
  protected readonly featuredTopic = computed(
    () => this.topics().find((topic) => topic.key() === this.featured()) ?? null,
  );

  /**
   * Every other topic, in the order `DASHBOARD_TOPIC_KEYS` names rather than the order a view query happens
   * to answer in, so the page's order is a list a reader can check against the rule it follows. The featured
   * one is lifted out, never repeated.
   */
  protected readonly gridTopics = computed(() => {
    const declared = new Map(this.topics().map((topic) => [topic.key(), topic]));

    return DASHBOARD_TOPIC_KEYS.filter((key) => key !== this.featured())
      .map((key) => declared.get(key))
      .filter((topic): topic is DashboardTopicDirective => topic !== undefined);
  });

  protected readonly link = (...segments: readonly string[]): readonly string[] => [
    '/',
    this.workspaceSlug(),
    ...segments,
  ];

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
      this.message.set(
        "Your dashboard couldn't be refreshed just now. What you see is from the last time it was read.",
      );
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

  /**
   * One part of the read model as a signal of its own: its data, or null when the server could not read it.
   * A method rather than a field, so the fields above can call it while they are being initialised.
   */
  private part<T>(pick: (dashboard: Dashboard) => DashboardSection<T>): Signal<T | null> {
    return computed(() => {
      const dashboard = this.dashboard();

      return dashboard === null ? null : this.data(pick(dashboard));
    });
  }

  /**
   * Whether no recipe is in any in-progress state — the server's own counts, every one of them zero. Read,
   * not added up: nothing here needs the total the read model does not send.
   */
  protected noRecipesInProgress(recipes: DashboardRecipesInProgress): boolean {
    return recipes.counts.every((count) => count.count === 0);
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

  /**
   * The heading of the topic card the creator is working in, if they are in one. The featured topic moves
   * between the top of the page and the grid as the counts change, and Angular builds a new card where it
   * lands, so a creator whose focus was on a retry inside that card — one that has just succeeded, which is
   * exactly what moves it — would be left on the document body.
   */
  private focusedTopicHeading(): string | null {
    const card = (document.activeElement as HTMLElement | null)?.closest('cp-topic-card') ?? null;

    return card?.querySelector('.heading')?.id ?? null;
  }

  /** Puts focus back in that topic, on its heading, but only where the read has actually dropped it. */
  private keepFocusInTopic(headingId: string | null): void {
    if (headingId === null) return;

    afterNextRender(
      () => {
        if (document.activeElement !== null && document.activeElement !== document.body) return;

        document.getElementById(headingId)?.focus();
      },
      { injector: this.injector },
    );
  }

  private async read(slug: string): Promise<DashboardOutcome['status'] | 'superseded'> {
    const wasFocusedIn = this.focusedTopicHeading();
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

    this.keepFocusInTopic(wasFocusedIn);

    return outcome.status;
  }

  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}
