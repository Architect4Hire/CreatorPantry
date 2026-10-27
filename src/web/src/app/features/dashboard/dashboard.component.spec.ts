import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { decodeDashboard } from '../../models/dashboard.models';
import { DashboardOutcome, DashboardService } from '../../services/dashboard.service';
import { QuickCaptureService } from '../../shared/quick-capture/quick-capture.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { DashboardComponent } from './dashboard.component';
import { DASHBOARD_TOPIC_KEYS } from './dashboard-topic.directive';
import { dashboardBody } from './dashboard.fixtures';

const SLUG = 'cozy-fall';

/** Every topic the page draws, in the order it draws them. */
const TOPICS = [
  'Carry on where you left off',
  'Workflows that need a look',
  'Your day',
  'Your week',
  'Publishing that needs you',
  'What is holding recipes back',
  'Test kitchen follow-ups',
  'AI proposals to review',
  'Coming up',
  'Recipes in progress',
  'Content board',
  'Recently added pictures',
];

describe('DashboardComponent', () => {
  let harness: RouterTestingHarness;
  let answers: Observable<DashboardOutcome>[];
  let reads: string[];
  let memberships: ReturnType<typeof signal<MyMembershipsState>>;
  let captures: QuickCaptureService;

  const root = (): HTMLElement => harness.routeNativeElement!;
  const text = (): string => (root().textContent ?? '').replace(/\s+/g, ' ');
  const topic = (heading: string): HTMLElement =>
    Array.from(root().querySelectorAll('cp-topic-card')).find(
      (each) => each.querySelector('.heading')?.textContent?.trim() === heading,
    ) as HTMLElement;
  /** Every topic heading in the order the page draws them: the featured card first, then the grid. */
  const headings = (): string[] =>
    Array.from(root().querySelectorAll('cp-topic-card h2')).map((heading) => heading.textContent?.trim() ?? '');
  const figure = (label: string): HTMLElement =>
    Array.from(root().querySelectorAll('cp-stat-tile')).find((tile) =>
      tile.textContent?.includes(label),
    ) as HTMLElement;
  const button = (name: string): HTMLButtonElement =>
    Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim() === name)!;

  function found(overrides: Record<string, unknown> = {}): Observable<DashboardOutcome> {
    return of({ status: 'found', dashboard: decodeDashboard(dashboardBody(overrides))! });
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      harness.detectChanges();
      await harness.fixture.whenStable();
    }
    harness.detectChanges();
  }

  function membership(role: 'Owner' | 'Viewer'): MyMembershipsState {
    return {
      status: 'ready',
      memberships: [
        {
          workspaceId: 'w-1',
          workspaceSlug: SLUG,
          workspaceName: 'Cozy Fall',
          membershipId: 'm-1',
          role,
          status: 'Active',
        },
      ],
    } as MyMembershipsState;
  }

  async function mount(...outcomes: Observable<DashboardOutcome>[]): Promise<void> {
    answers = outcomes;
    memberships = signal(membership('Owner'));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: ':workspaceSlug/dashboard', component: DashboardComponent, data: { title: 'Dashboard' } },
        ]),
        {
          provide: DashboardService,
          useValue: {
            get: (slug: string) => {
              reads.push(slug);

              return answers.shift() ?? found();
            },
          },
        },
        { provide: WorkspaceMembershipService, useValue: { state: memberships, ensureLoaded: async () => undefined } },
      ],
    });

    captures = TestBed.inject(QuickCaptureService);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/${SLUG}/dashboard`, DashboardComponent);
    await settle();
  }

  /** A workspace nobody has done anything in yet — every part the first-use card judges by at zero. */
  function emptyWorkspace(): Record<string, unknown> {
    return {
      recipesInProgress: {
        status: 'Ready',
        data: {
          counts: ['Draft', 'InDevelopment', 'Testing', 'ReadyForReview'].map((status) => ({ status, count: 0 })),
          recent: [],
        },
      },
      board: { status: 'Ready', data: { total: 0, columns: [] } },
      recentUploads: { status: 'Ready', data: { items: [] } },
      workflows: {
        status: 'Ready',
        data: { inProgressCount: 0, continue: [], needsAttentionCount: 0, needsAttention: [] },
      },
      publishing: { status: 'Ready', data: { counts: [], needsAttentionCount: 0, needsAttention: [] } },
      testFollowUps: { status: 'Ready', data: { open: 0, blocking: 0, major: 0, minor: 0, recent: [] } },
      aiProposals: { status: 'Ready', data: { awaitingDecision: 0, recent: [] } },
      readinessBlockers: { status: 'Ready', data: { evaluated: 0, unevaluated: 0, blocked: [] } },
      upcoming: {
        status: 'Ready',
        data: { from: '2026-10-14', through: '2026-10-27', cardCount: 0, cards: [], postCount: 0, posts: [] },
      },
      workPlan: {
        status: 'Ready',
        data: {
          date: '2026-10-14',
          timeZoneId: 'America/Chicago',
          overdueCount: 0,
          overdue: [],
          todayCount: 0,
          today: [],
          topThree: [],
          thisWeek: {
            weekStart: '2026-10-12',
            weekEnd: '2026-10-18',
            plannedTaskCount: 0,
            estimatedTaskCount: 0,
            estimatedMinutes: null,
            inProgressCount: 0,
            waitingCount: 0,
            completedCount: 0,
          },
          unplannedCount: 0,
        },
      },
    };
  }

  beforeEach(() => {
    reads = [];
  });

  // ---- The page ----

  it('is a detail page about the workspace: one head, the most urgent topic first, then the rest', async () => {
    await mount(found());

    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(root().querySelector('h1')!.textContent).toBe('Dashboard');

    // Two tasks are late, so "Your day" is lifted out of the grid as the one featured card and leads the
    // page. Every other topic keeps the order it was declared in, and no topic is drawn twice.
    expect(headings()).toEqual(['Your day', ...TOPICS.filter((heading) => heading !== 'Your day')]);
    expect(root().querySelector('ul[aria-label="What needs you"]')).toBeTruthy();
    expect(root().querySelector('ul.topics')!.getAttribute('aria-label')).toBe('Your workspace at a glance');

    // A topic added to the page without a heading here would pass every other spec in this file.
    expect(TOPICS.length).toBe(DASHBOARD_TOPIC_KEYS.length);
    expect(reads).toEqual([SLUG]);
  });

  it('shows the server’s counts and none of its own', async () => {
    await mount(found());

    // The board's total is the server's twelve; the page does not add up its columns.
    expect(topic('Content board').textContent).toContain('In all');
    expect(topic('Content board').textContent).toContain('12');
    expect(topic('Publishing that needs you').textContent).toContain('3 posts need a look');
    expect(topic('AI proposals to review').textContent).toContain('7 proposals are waiting');
    expect(topic('Coming up').textContent).toContain('9');
    expect(topic('Test kitchen follow-ups').textContent).toContain('Blocking');
  });

  it('opens each blocked recipe on its Readiness tab and each test issue on its Test kitchen tab', async () => {
    await mount(found());

    const blocked = topic('What is holding recipes back').querySelector('a')!;
    expect(blocked.textContent).toBe('Soda bread');
    expect(blocked.getAttribute('href')).toBe(`/${SLUG}/recipes/r1?area=readiness`);
    expect(topic('What is holding recipes back').textContent).toContain('4 blockers');
    expect(
      Array.from(topic('What is holding recipes back').querySelectorAll('.reasons li')).map(
        (reason) => reason.textContent,
      ),
    ).toEqual(['Add at least one ingredient.', 'Add at least one step.', 'Say how much it makes.']);

    expect(topic('Test kitchen follow-ups').querySelector('a')!.getAttribute('href')).toBe(
      `/${SLUG}/recipes/r1?area=tests`,
    );
  });

  it('opens a post on its card, and lists one with no card without a link', async () => {
    await mount(found());

    const attention = topic('Publishing that needs you');
    const items = Array.from(attention.querySelectorAll('.items > li'));
    expect(items[0].querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/content-board/c2`);
    expect(items[0].textContent).toContain('Not published');
    expect(items[1].querySelector('a')).toBeNull();
    expect(items[1].textContent).toContain('A post to Another network');
    expect(items[1].textContent).toContain('Needs attention');
  });

  it('opens a proposal on its recipe, and names one with no recipe by what it is', async () => {
    await mount(found());

    const items = Array.from(topic('AI proposals to review').querySelectorAll('.items > li'));
    expect(items[0].querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/recipes/r1`);
    expect(items[0].textContent).toContain('Revision');
    expect(items[1].querySelector('a')).toBeNull();
    expect(items[1].textContent).toContain('Recipe ideas');
  });

  it('says every state in a word, never by colour alone', async () => {
    await mount(found());

    for (const pill of Array.from(root().querySelectorAll('cp-status-pill'))) {
      expect(pill.textContent?.trim()).not.toBe('');
    }
  });

  it('says which day today is, and with no time zone set says it has none rather than naming a day', async () => {
    await mount(
      found({
        timeZoneId: null,
        today: null,
        timeZoneState: 'NotSet',
        upcoming: { status: 'Unavailable', data: null },
      }),
    );

    const updated = root().querySelector('.updated')!.textContent!;

    // No day is named at all: a guessed one reads as an answer and is wrong by up to a day either way.
    expect(updated).not.toContain('October');
    expect(updated).toContain('no time zone set yet');
  });

  it('shows days as the days they name and times in the workspace’s zone, whatever zone the reader is in', async () => {
    await mount(found());

    // 15:30 UTC is 10:30 in Chicago: the workspace's time, not this browser's.
    expect(root().querySelector('.updated')!.textContent).toContain('10:30 AM');
    expect(root().querySelector('.updated')!.textContent).toContain('Wednesday, October 14');

    // A planned date is a day, not an instant: the 16th is Friday the 16th for every reader.
    expect(topic('Coming up').textContent).toContain('Fri, Oct 16');
    expect(topic('Coming up').textContent).toContain('Oct 14 to Oct 27');

    // 14:00 UTC on the 16th is 9:00 AM in Chicago.
    expect(topic('Coming up').textContent).toContain('Fri, Oct 16, 9:00 AM');
  });

  it('names each link and each retry for where it goes, not just what it is', async () => {
    await mount(
      found({ board: { status: 'Unavailable', data: null }, recentUploads: { status: 'Unavailable', data: null } }),
    );

    expect(topic('What is holding recipes back').querySelector('a')!.getAttribute('aria-label')).toBe(
      'Readiness of Soda bread',
    );
    const retries = Array.from(root().querySelectorAll('cp-topic-card cp-notice button')).map((retry) =>
      retry.getAttribute('aria-label'),
    );
    expect(retries).toEqual(['Try again to read the board', 'Try again to read recent pictures']);
  });

  it('says none is blocked only about recipes that were actually checked', async () => {
    await mount(found({ readinessBlockers: { status: 'Ready', data: { evaluated: 0, unevaluated: 2, blocked: [] } } }));

    const readiness = topic('What is holding recipes back');
    expect(readiness.textContent).not.toContain('None of them is blocked');
    expect(readiness.textContent).not.toContain('Checked');
    expect(readiness.textContent).toContain("2 recipes couldn't be checked just now.");
  });

  // ---- States ----

  it('draws the shape of what is coming while it loads, and says it once, offscreen', async () => {
    const answer = new Subject<DashboardOutcome>();
    await mount(answer);

    // No visible "Loading…" sentence (APP-003): the figures, the featured card and the topics as
    // silhouettes, which say nothing, and one status line nobody sees.
    const loading = root().querySelector('.loading')!;
    expect(loading.getAttribute('aria-busy')).toBe('true');
    expect(loading.querySelectorAll('cp-skeleton').length).toBe(11);
    for (const bone of Array.from(loading.querySelectorAll('cp-skeleton'))) {
      expect(bone.getAttribute('aria-hidden')).toBe('true');
    }

    const status = root().querySelector('.cp-sr-only[role="status"]')!;
    expect(status.textContent).toContain('Loading your dashboard');

    // Outside the busy region: a live region held inside one may never be read at all.
    expect(loading.contains(status)).toBe(false);

    // Said once. The page's own outcome region is the only other one, and it is empty.
    expect(
      Array.from(root().querySelectorAll('[role="status"]')).filter((region) => region.textContent?.trim() !== '')
        .length,
    ).toBe(1);
    expect(root().querySelector('cp-topic-card')).toBeNull();
    expect(root().querySelector('cp-stat-tile')).toBeNull();

    answer.next({ status: 'found', dashboard: decodeDashboard(dashboardBody())! });
    answer.complete();
    await settle();

    expect(root().querySelectorAll('cp-topic-card').length).toBe(TOPICS.length);
    expect(root().querySelector('.loading')).toBeNull();
    expect(root().querySelector('.cp-sr-only[role="status"]')).toBeNull();
  });

  it('says so when it cannot be loaded, keeps its head, and loads on request with focus on the page', async () => {
    await mount(of({ status: 'unavailable' }), found());

    expect(root().querySelector('h1')!.textContent).toBe('Dashboard');
    expect(root().querySelector('[role="alert"]')!.textContent).toContain("couldn't be loaded");

    button('Try again').click();
    await settle();

    expect(root().querySelectorAll('cp-topic-card').length).toBe(TOPICS.length);

    // The control that was pressed has gone; focus goes to the page's heading, not to the document.
    expect(document.activeElement).toBe(root().querySelector('h1'));
  });

  it('keeps every other topic when one could not be read, and says so inside that one alone', async () => {
    await mount(found({ testFollowUps: { status: 'Unavailable', data: null } }));

    const tests = topic('Test kitchen follow-ups');
    expect(tests.querySelector('cp-notice')!.textContent).toContain("couldn't be read just now");
    expect(tests.querySelector('.items')).toBeNull();
    expect(topic('What is holding recipes back').querySelector('cp-notice')).toBeNull();
    expect(topic('Content board').textContent).toContain('12');
  });

  it('says what each empty topic means in its own words', async () => {
    await mount(
      found({
        publishing: { status: 'Ready', data: { counts: [], needsAttentionCount: 0, needsAttention: [] } },
        aiProposals: { status: 'Ready', data: { awaitingDecision: 0, recent: [] } },
        testFollowUps: { status: 'Ready', data: { open: 0, blocking: 0, major: 0, minor: 0, recent: [] } },
        upcoming: {
          status: 'Ready',
          data: { from: '2026-10-14', through: '2026-10-27', cardCount: 0, cards: [], postCount: 0, posts: [] },
        },
        readinessBlockers: { status: 'Ready', data: { evaluated: 3, unevaluated: 1, blocked: [] } },
      }),
    );

    expect(topic('Publishing that needs you').textContent).toContain('No post failed or is waiting on you.');
    expect(topic('AI proposals to review').textContent).toContain('No proposals are waiting for you.');
    expect(topic('Test kitchen follow-ups').textContent).toContain('No test issues are waiting to be resolved.');
    expect(topic('Coming up').textContent).toContain('Nothing planned or scheduled in the next two weeks.');
    expect(topic('What is holding recipes back').textContent).toContain('None of them is blocked.');
    expect(topic('What is holding recipes back').textContent).toContain("One recipe couldn't be checked just now.");
  });

  it('offers a first step to a workspace with nothing in it yet', async () => {
    await mount(found(emptyWorkspace()));

    const start = topic('Get started');
    expect(start).toBeTruthy();
    expect(Array.from(start.querySelectorAll('a')).map((link) => link.getAttribute('href'))).toEqual([
      `/${SLUG}/recipes/new`,
      `/${SLUG}/workflows`,
      `/${SLUG}/content-board`,
    ]);
  });

  it('does not offer a first step to someone with a workflow under way', async () => {
    await mount(found({ ...emptyWorkspace(), workflows: dashboardBody()['workflows'] }));

    expect(topic('Get started')).toBeUndefined();
  });

  it('does not offer a first step to someone with work on their own plan', async () => {
    await mount(found({ ...emptyWorkspace(), workPlan: dashboardBody()['workPlan'] }));

    expect(topic('Get started')).toBeUndefined();
  });

  it('still offers a first step when the plan cannot be read, because no zone is no evidence either way', async () => {
    await mount(
      found({
        ...emptyWorkspace(),
        timeZoneId: null,
        today: null,
        timeZoneState: 'NotSet',
        workPlan: { status: 'Unavailable', data: null },
      }),
    );

    expect(topic('Get started')).toBeTruthy();
  });

  /**
   * Each part on its own is enough to say the workspace is not new. A workspace whose recipes are all
   * approved has none in progress, so judging by one part would greet a creator with a year of work behind
   * them as though they had just arrived.
   */
  const busyParts = (): readonly { readonly part: string; readonly data: Record<string, unknown> }[] => [
    {
      part: 'a post that needs a look',
      data: { publishing: dashboardBody()['publishing'] as Record<string, unknown> },
    },
    {
      part: 'an open test issue',
      data: { testFollowUps: dashboardBody()['testFollowUps'] as Record<string, unknown> },
    },
    { part: 'a proposal waiting', data: { aiProposals: dashboardBody()['aiProposals'] as Record<string, unknown> } },
    { part: 'something coming up', data: { upcoming: dashboardBody()['upcoming'] as Record<string, unknown> } },
    {
      part: 'a picture in the library',
      data: { recentUploads: dashboardBody()['recentUploads'] as Record<string, unknown> },
    },
    { part: 'a card on the board', data: { board: dashboardBody()['board'] as Record<string, unknown> } },
  ];

  for (const row of busyParts()) {
    it(`does not offer a first step to a workspace with ${row.part}`, async () => {
      await mount(found({ ...emptyWorkspace(), ...row.data }));

      expect(topic('Get started')).toBeUndefined();
      expect(root().querySelectorAll('cp-stat-tile').length).toBe(4);
    });
  }

  it('does not offer a first step when the plan could not be read and the workspace has a zone', async () => {
    // With a zone set, an unreadable plan is a part we cannot judge by, like any other: it may hold the
    // creator's whole week. Only a workspace with no zone at all makes an unreadable plan no evidence.
    await mount(found({ ...emptyWorkspace(), workPlan: { status: 'Unavailable', data: null } }));

    expect(topic('Get started')).toBeUndefined();
  });

  it('does not offer a first step while a part it would judge by could not be read', async () => {
    await mount(
      found({
        recentUploads: { status: 'Unavailable', data: null },
        board: { status: 'Ready', data: { total: 0, columns: [] } },
      }),
    );

    expect(topic('Get started')).toBeUndefined();
  });

  // ---- The creator's own topics (13A.33a) ----

  it('opens each workflow at its run, which resumes on the step it is on, and says what that step is', async () => {
    await mount(found());

    const carryOn = topic('Carry on where you left off');
    expect(carryOn.textContent).toContain('4 workflows are under way');

    const rows = Array.from(carryOn.querySelectorAll('.items > li'));
    expect(rows.length).toBe(2);
    expect(rows[0].querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/workflows/runs/w1`);
    expect(rows[0].querySelector('a')!.getAttribute('aria-label')).toBe(
      'Carry on with Soda bread, start to finish, last touched Oct 14',
    );
    expect(rows[0].textContent).toContain('Check your method');

    // A step above the reader's role is said in words, not left to a colour or to silence.
    expect(rows[0].textContent).not.toContain('Needs someone else');
    expect(rows[1].textContent).toContain('Needs someone else');
    expect(carryOn.querySelector('a[href="/cozy-fall/workflows"]')!.textContent).toContain('All workflows');
  });

  it('says in words why each workflow needs a look, longest-waiting first', async () => {
    await mount(found());

    const stuck = topic('Workflows that need a look');
    expect(stuck.textContent).toContain('2 workflows need a look');
    expect(stuck.querySelector('.items a')!.getAttribute('href')).toBe(`/${SLUG}/workflows/runs/w3`);
    expect(Array.from(stuck.querySelectorAll('.reasons li')).map((reason) => reason.textContent)).toEqual([
      'Nobody has touched it in over a week',
      'A newer version of this workflow has shipped since it started',
    ]);
    expect(stuck.textContent).not.toContain('idle');
    expect(stuck.textContent).not.toContain('newer_version');
  });

  it('opens each task in My Day at itself, late first, and marks the ones chosen as today’s three', async () => {
    await mount(found());

    const day = topic('Your day');
    expect(day.textContent).toContain('2 tasks are late');
    expect(day.textContent).toContain('3 tasks are planned for today');

    const late = Array.from(day.querySelectorAll('[aria-label="Late tasks"] > li'));
    expect(late[0].querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/my-day?task=k1`);
    expect(late[0].textContent).toContain('Late');
    expect(late[0].textContent).toContain('Was due');

    const planned = Array.from(day.querySelectorAll('[aria-label="Planned for today"] > li'));
    expect(planned[0].querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/my-day?task=k2`);
    expect(planned[0].textContent).toContain('Top three');
    expect(planned[1].textContent).not.toContain('Top three');
  });

  it('shows the week’s own totals, and how much of it anyone has put a time to', async () => {
    await mount(found());

    const week = topic('Your week');
    expect(week.textContent).toContain('Oct 12 to Oct 18');
    expect(week.textContent).toContain('About 2 hr 30 min of planned work');
    expect(week.textContent).toContain('4 of 6');
    expect(week.textContent).toContain('5 captured tasks are waiting to be planned');
    expect(week.querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/my-week`);
  });

  it('says a week with nothing estimated has no time on it rather than no work in it', async () => {
    const plan = dashboardBody()['workPlan'] as { data: Record<string, unknown> };
    const thisWeek = plan.data['thisWeek'] as Record<string, unknown>;
    await mount(
      found({
        workPlan: {
          status: 'Ready',
          data: {
            ...plan.data,
            thisWeek: { ...thisWeek, estimatedTaskCount: 0, estimatedMinutes: null },
            unplannedCount: 0,
          },
        },
      }),
    );

    // Null minutes stay unknown: nothing claims the work takes no time.
    expect(topic('Your week').textContent).toContain('Nothing planned this week has a time on it yet');
    expect(topic('Your week').textContent).toContain('6');
    expect(topic('Your week').textContent).toContain('Nothing is waiting to be planned');
  });

  it('captures a task through the shell’s dialog, on the day it is showing, and reads itself again', async () => {
    await mount(found(), found());

    const opened: unknown[] = [];
    spyOn(captures, 'open').and.callFake((options) => {
      opened.push(options);
    });

    const capture = button('+ Capture a task');
    capture.click();
    await settle();

    // The button keeps focus, so the dialog has somewhere to give it back to.
    expect(opened).toEqual([{ today: '2026-10-14' }]);
    expect(document.activeElement).toBe(capture);
    expect(reads.length).toBe(1);

    // A task captured anywhere belongs in these counts at once, without a second announcement.
    captures.notifyCaptured({ id: 'k9', title: 'Captured' } as never);
    await settle();

    expect(reads.length).toBe(2);
    expect(root().querySelector('.outcome')!.textContent!.trim()).toBe('');
  });

  it('offers a Viewer no way to capture work, and still shows them their own plan', async () => {
    await mount(found());
    memberships.set(membership('Viewer'));
    await settle();

    expect(button('+ Capture a task')).toBeUndefined();
    expect(topic('Your day').querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/my-day?task=k1`);
    expect(topic('Your day').textContent).toContain('2 tasks are late');
  });

  it('does not tell the creator to set a time zone when the zone simply could not be read', async () => {
    // A brand profile that failed to read says nothing about whether a zone is set. Sending that creator to
    // Brand settings sends them to change something that is very likely already right; "couldn't be read" with
    // a retry is the truthful answer, and the retry can actually succeed.
    await mount(
      found({
        timeZoneId: null,
        today: null,
        timeZoneState: 'Unreadable',
        workPlan: { status: 'Unavailable', data: null },
        upcoming: { status: 'Unavailable', data: null },
      }),
    );

    for (const heading of ['Your day', 'Your week', 'Coming up']) {
      const notice = topic(heading).querySelector('cp-notice')!;
      expect(notice.textContent).toContain("couldn't be read just now");
      expect(notice.textContent).not.toContain('needs your workspace’s time zone');
    }

    expect(root().querySelector('.updated')!.textContent).toContain('could not be read');
  });

  it('keeps the day and the week with a notice when the plan could not be read, and the workflows untouched', async () => {
    // A zone is set here on purpose: with none, there is nothing to read yet and the day and the week say
    // so instead (the test below). Only a workspace that has a zone can have a plan that failed to read.
    await mount(
      found({ workPlan: { status: 'Unavailable', data: null }, upcoming: { status: 'Unavailable', data: null } }),
    );

    expect(topic('Your day').querySelector('cp-notice')!.textContent).toContain("couldn't be read just now");
    expect(topic('Your week').querySelector('cp-notice')!.textContent).toContain("couldn't be read just now");
    expect(topic('Your day').querySelector('button')!.getAttribute('aria-label')).toBe('Try again to read your day');
    expect(topic('Carry on where you left off').querySelector('cp-notice')).toBeNull();
    expect(topic('Carry on where you left off').textContent).toContain('4 workflows are under way');
  });

  it('says a day-bound topic needs a time zone, rather than that it could not be read', async () => {
    // A brand-new workspace has no zone, so the day, the week and what is coming up have nothing to show.
    // Saying "couldn't be read" there offers a retry that can never succeed and reads as a broken page on
    // the first screen of the product; My Day and My Week already say what is missing and where to set it.
    await mount(
      found({
        timeZoneId: null,
        today: null,
        timeZoneState: 'NotSet',
        workPlan: { status: 'Unavailable', data: null },
        upcoming: { status: 'Unavailable', data: null },
      }),
    );

    for (const heading of ['Your day', 'Your week', 'Coming up']) {
      const notice = topic(heading).querySelector('cp-notice')!;
      expect(notice.textContent).toContain('needs your workspace’s time zone');
      expect(notice.textContent).not.toContain("couldn't be read just now");
      expect(notice.querySelector('a')!.getAttribute('href')).toContain('/brand');
    }

    // Nothing offers a retry for a condition a retry cannot change. The card's own actions stay.
    expect(topic('Your day').querySelector('button[aria-label="Try again to read your day"]')).toBeNull();
  });

  it('says what the creator’s own empty topics mean in their own words', async () => {
    await mount(
      found({
        workflows: {
          status: 'Ready',
          data: { inProgressCount: 0, continue: [], needsAttentionCount: 0, needsAttention: [] },
        },
        workPlan: {
          status: 'Ready',
          data: {
            date: '2026-10-14',
            timeZoneId: 'America/Chicago',
            overdueCount: 0,
            overdue: [],
            todayCount: 0,
            today: [],
            topThree: [],
            thisWeek: {
              weekStart: '2026-10-12',
              weekEnd: '2026-10-18',
              plannedTaskCount: 0,
              estimatedTaskCount: 0,
              estimatedMinutes: null,
              inProgressCount: 0,
              waitingCount: 0,
              completedCount: 0,
            },
            unplannedCount: 0,
          },
        },
      }),
    );

    // Two of these mean "nothing here yet", and are drawn (APP-004); the other two mean "all clear", and a
    // drawing of good news on every quiet topic would be a page of drawings on a dashboard that is simply fine.
    const carryOn = topic('Carry on where you left off');
    expect(carryOn.textContent).toContain('Nothing under way');
    expect(carryOn.querySelector('cp-empty-state cp-illustration')!.getAttribute('data-name')).toBe('board');

    const day = topic('Your day');
    expect(day.textContent).toContain('Nothing is late, and nothing is planned for today.');
    expect(day.querySelector('cp-empty-state cp-illustration')!.getAttribute('data-name')).toBe('calendar');

    expect(topic('Workflows that need a look').textContent).toContain('Nothing has stalled.');
    expect(topic('Workflows that need a look').querySelector('cp-empty-state')).toBeNull();
    expect(topic('Your week').textContent).toContain('Nothing is waiting to be planned.');
    expect(topic('Your week').querySelector('cp-empty-state')).toBeNull();
  });

  // ---- The figures and the featured card (AU.4.1) ----

  it('reads four of the server’s own counts at a glance, each a figure and what it counts', async () => {
    await mount(found());

    const tiles = Array.from(root().querySelectorAll('cp-stat-tile'));
    expect(tiles.map((tile) => tile.querySelector('.value')?.textContent)).toEqual(['2', '3', '6', '7']);

    // The figure and its label are one sentence, never a bare number, and the context is the server's too.
    expect(figure('tasks late').textContent).toContain('2 tasks late');
    expect(figure('tasks late').textContent).toContain('3 tasks planned for today.');
    expect(figure('posts need a look').textContent).toContain('3 posts need a look');
    expect(figure('test issues open').textContent).toContain('1 blocking, 2 major and 3 minor.');
    expect(figure('AI proposals waiting for you').textContent).toContain('7 AI proposals waiting for you');

    // One destination, named for what it opens. The other three are dealt with item by item in the cards.
    expect(figure('tasks late').querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/my-day`);
    expect(figure('posts need a look').querySelector('a')).toBeNull();
  });

  it('says one late task in the singular, and adds up nothing', async () => {
    const plan = dashboardBody()['workPlan'] as { data: Record<string, unknown> };
    await mount(found({ workPlan: { status: 'Ready', data: { ...plan.data, overdueCount: 1, todayCount: 1 } } }));

    expect(figure('task late').textContent).toContain('1 task late');
    expect(figure('task late').textContent).toContain('1 task planned for today.');

    // No tile claims a number the read model does not send: no total of recipes in progress, no count of
    // blocked recipes, no size of the picture library.
    expect(root().querySelectorAll('cp-stat-tile').length).toBe(4);
  });

  it('keeps a figure whose part could not be read, with its label and why it is not there', async () => {
    await mount(found({ testFollowUps: { status: 'Unavailable', data: null } }));

    const tile = figure('test issues open');
    expect(tile.querySelector('.value')).toBeNull();
    expect(tile.textContent).toContain('test issues open');
    expect(tile.querySelector('cp-notice')!.textContent).toContain("couldn't be read just now");

    // The other three are untouched.
    expect(figure('tasks late').querySelector('.value')!.textContent).toBe('2');
  });

  it('says a figure that needs a day needs a time zone, not that it could not be read', async () => {
    await mount(
      found({
        timeZoneId: null,
        today: null,
        timeZoneState: 'NotSet',
        workPlan: { status: 'Unavailable', data: null },
      }),
    );

    const tile = figure('tasks late');
    expect(tile.querySelector('cp-notice')!.textContent).toContain('needs your workspace’s time zone');
    expect(tile.textContent).not.toContain("couldn't be read");

    // Nothing to open for a figure there is no figure for.
    expect(tile.querySelector('a')).toBeNull();
  });

  it('features the most urgent topic, and only ever one', async () => {
    await mount(found());

    const featured = Array.from(root().querySelectorAll('cp-topic-card')).filter((card) =>
      card.querySelector('.cp-card--featured'),
    );
    expect(featured.length).toBe(1);
    expect(featured[0].querySelector('h2')!.textContent!.trim()).toBe('Your day');
    expect(headings()[0]).toBe('Your day');
  });

  /**
   * The page's order of urgency, each step turning off the one above it: what is late, then what failed to
   * publish, then what is blocked, then what blocks a test, then a workflow that has stalled, and last what
   * the creator was in the middle of. Each case is its own spec, because one spec mounts one page.
   */
  const onTime = (): Record<string, unknown> => {
    const plan = dashboardBody()['workPlan'] as { data: Record<string, unknown> };

    return { status: 'Ready', data: { ...plan.data, overdueCount: 0, overdue: [] } };
  };
  const calmWorkflows = (): Record<string, unknown> => {
    const workflows = dashboardBody()['workflows'] as { data: Record<string, unknown> };

    return { status: 'Ready', data: { ...workflows.data, needsAttentionCount: 0, needsAttention: [] } };
  };
  const quietPublishing = { status: 'Ready', data: { counts: [], needsAttentionCount: 0, needsAttention: [] } };
  const unblocked = { status: 'Ready', data: { evaluated: 1, unevaluated: 0, blocked: [] } };
  const passing = { status: 'Ready', data: { open: 0, blocking: 0, major: 0, minor: 0, recent: [] } };
  const noProposals = { status: 'Ready', data: { awaitingDecision: 0, recent: [] } };

  const urgency = (): readonly { readonly featured: string; readonly overrides: Record<string, unknown> }[] => [
    { featured: 'Publishing that needs you', overrides: { workPlan: onTime() } },
    { featured: 'What is holding recipes back', overrides: { workPlan: onTime(), publishing: quietPublishing } },
    {
      featured: 'Test kitchen follow-ups',
      overrides: { workPlan: onTime(), publishing: quietPublishing, readinessBlockers: unblocked },
    },
    {
      featured: 'Workflows that need a look',
      overrides: {
        workPlan: onTime(),
        publishing: quietPublishing,
        readinessBlockers: unblocked,
        testFollowUps: passing,
      },
    },
    {
      featured: 'AI proposals to review',
      overrides: {
        workPlan: onTime(),
        publishing: quietPublishing,
        readinessBlockers: unblocked,
        testFollowUps: passing,
        workflows: calmWorkflows(),
      },
    },
    {
      featured: 'Carry on where you left off',
      overrides: {
        workPlan: onTime(),
        publishing: quietPublishing,
        readinessBlockers: unblocked,
        testFollowUps: passing,
        workflows: calmWorkflows(),
        aiProposals: noProposals,
      },
    },
  ];

  for (const [step, row] of urgency().entries()) {
    it(`features "${row.featured}" once the ${step + 1} topics above it are clear`, async () => {
      await mount(found(row.overrides));

      // The whole order, not just its head: a topic drawn twice, or dropped from the grid when it was
      // lifted out of it, would pass an assertion about the first heading alone.
      expect(headings()).toEqual([row.featured, ...TOPICS.filter((heading) => heading !== row.featured)]);
      expect(root().querySelectorAll('.cp-card--featured').length).toBe(1);
    });
  }

  it('features nothing on a dashboard where nothing needs anyone', async () => {
    await mount(
      found({
        workflows: {
          status: 'Ready',
          data: { inProgressCount: 0, continue: [], needsAttentionCount: 0, needsAttention: [] },
        },
        workPlan: { status: 'Unavailable', data: null },
        publishing: { status: 'Ready', data: { counts: [], needsAttentionCount: 0, needsAttention: [] } },
        readinessBlockers: { status: 'Ready', data: { evaluated: 1, unevaluated: 0, blocked: [] } },
        testFollowUps: { status: 'Ready', data: { open: 0, blocking: 0, major: 0, minor: 0, recent: [] } },
        aiProposals: { status: 'Ready', data: { awaitingDecision: 0, recent: [] } },
      }),
    );

    expect(root().querySelectorAll('.cp-card--featured').length).toBe(0);
    expect(headings()).toEqual(TOPICS);
  });

  it('features the first step in a workspace with nothing in it, and shows no figures above it', async () => {
    await mount(found(emptyWorkspace()));

    const start = topic('Get started');
    expect(start.querySelector('.cp-card--featured')).toBeTruthy();
    expect(root().querySelectorAll('.cp-card--featured').length).toBe(1);
    expect(start.querySelector('cp-empty-state cp-illustration')!.getAttribute('data-name')).toBe('recipe');
    expect(start.querySelector('cp-empty-state')!.getAttribute('data-variant')).toBe('first-use');

    // Four zeros above a first step say nothing the first step does not say better.
    expect(root().querySelector('cp-stat-tile')).toBeNull();
  });

  it('draws the topics whose emptiness means nothing here yet', async () => {
    await mount(
      found({
        recipesInProgress: {
          status: 'Ready',
          data: { counts: [{ status: 'Draft', count: 0 }], recent: [] },
        },
        board: { status: 'Ready', data: { total: 0, columns: [] } },
        recentUploads: { status: 'Ready', data: { items: [] } },
      }),
    );

    const drawn: Record<string, string> = {
      'Recipes in progress': 'recipe',
      'Content board': 'board',
      'Recently added pictures': 'picture',
    };
    for (const [heading, illustration] of Object.entries(drawn)) {
      const card = topic(heading);
      expect(card.querySelector('cp-empty-state cp-illustration')!.getAttribute('data-name')).toBe(illustration);

      // Compact, not the roomy first-use layout: a dashboard holds a dozen of these at once.
      expect(card.querySelector('cp-empty-state')!.getAttribute('data-variant')).toBe('no-results');
    }

    // A board with nothing on it says so rather than listing a column of zeros.
    expect(topic('Content board').querySelector('dl')).toBeNull();
    expect(topic('Recipes in progress').querySelector('dl')).toBeNull();
  });

  it('still lists the statuses when a recipe is in progress, drawn state or not', async () => {
    await mount(found());

    expect(topic('Recipes in progress').querySelector('cp-empty-state')).toBeNull();
    expect(topic('Recipes in progress').textContent).toContain('Soda bread');
  });

  // ---- Naming a run nobody named (AU.4.1, from AU.0) ----

  it('tells three runs of one workflow apart, for the eye and for a screen reader', async () => {
    // A run nobody gave a name to is titled after its goal, so these three all arrive as "Create a recipe".
    const run = (id: string, stepTitle: string | null, updatedAt: string): Record<string, unknown> => ({
      id,
      title: 'Create a recipe',
      definitionKey: 'recipe.create',
      goal: 'Create a recipe',
      stepKey: stepTitle === null ? null : 'step',
      stepTitle,
      youCanDoIt: true,
      updatedAt,
      attentionReasons: [],
    });

    await mount(
      found({
        workflows: {
          status: 'Ready',
          data: {
            inProgressCount: 3,
            continue: [
              run('w1', 'Check your method', '2026-10-14T11:00:00+00:00'),
              run('w2', 'Write the headnote', '2026-10-13T11:00:00+00:00'),
              run('w3', null, '2026-10-12T11:00:00+00:00'),
            ],
            needsAttentionCount: 0,
            needsAttention: [],
          },
        },
      }),
    );

    const links = Array.from(topic('Carry on where you left off').querySelectorAll('.items a'));
    expect(links.map((link) => link.textContent?.trim())).toEqual([
      'Create a recipe — Check your method',
      'Create a recipe — Write the headnote',
      'Create a recipe — finish it',
    ]);

    // Each accessible name differs too, and carries the visible one inside it (SC 2.5.3).
    const names = links.map((link) => link.getAttribute('aria-label') ?? '');
    expect(new Set(names).size).toBe(3);
    expect(names[0]).toBe('Carry on with Create a recipe — Check your method, last touched Oct 14');
    for (const [index, link] of links.entries()) {
      expect(names[index]).toContain(link.textContent!.trim());
    }
  });

  it('keeps the creator’s own name for a run they named, and offers to open one that is not theirs to take', async () => {
    await mount(found());

    const rows = Array.from(topic('Carry on where you left off').querySelectorAll('.items > li'));

    // Named by its creator: shown as they wrote it, with no step bolted on.
    expect(rows[0].querySelector('a')!.textContent!.trim()).toBe('Soda bread, start to finish');

    // Somebody else's step: offered as something to open, not to carry on with.
    expect(rows[1].querySelector('a')!.getAttribute('aria-label')).toBe('Open Granola, improved, last touched Oct 13');
  });

  it('refreshes on request, says so, and keeps what it shows if the refresh fails', async () => {
    await mount(found(), found(), of({ status: 'unavailable' }));
    const refresh = button('Refresh');
    const outcome = (): string => root().querySelector('.outcome')!.textContent!.trim();

    refresh.focus();
    refresh.click();
    await settle();

    expect(reads.length).toBe(2);
    expect(outcome()).toContain('up to date');

    // Never disabled, so the focused button keeps focus through the refresh.
    expect(refresh.disabled).toBe(false);
    expect(document.activeElement).toBe(refresh);

    refresh.click();
    await settle();

    expect(reads.length).toBe(3);
    expect(root().querySelectorAll('cp-topic-card').length).toBe(TOPICS.length);
    expect(outcome()).toContain("couldn't be refreshed just now");
    expect(root().querySelector('.outcome')!.getAttribute('role')).toBe('status');
  });

  it('ignores a second press while a refresh is running, and says it is running', async () => {
    const slow = new Subject<DashboardOutcome>();
    await mount(found(), slow);

    button('Refresh').click();
    harness.detectChanges();
    const running = button('Refreshing…');
    expect(running.getAttribute('aria-disabled')).toBe('true');

    running.click();
    expect(reads.length).toBe(2);

    slow.next({ status: 'found', dashboard: decodeDashboard(dashboardBody())! });
    slow.complete();
    await settle();
    expect(button('Refresh')).toBeTruthy();
  });

  it('keeps one workspace’s dashboard out of another’s, and reads the one in the route', async () => {
    const other = 'winter-kitchen';
    await mount(
      found(),
      found({
        recipesInProgress: {
          status: 'Ready',
          data: {
            counts: [{ status: 'Draft', count: 1 }],
            recent: [
              { id: 'r9', title: 'Winter kitchen loaf', status: 'Draft', updatedAt: '2026-10-14T09:00:00+00:00' },
            ],
          },
        },
      }),
    );

    expect(topic('Recipes in progress').textContent).toContain('Soda bread');

    await harness.navigateByUrl(`/${other}/dashboard`, DashboardComponent);
    await settle();

    // Read by the slug in the route, and every link now points into that workspace and no other.
    expect(reads).toEqual([SLUG, other]);
    expect(topic('Recipes in progress').textContent).toContain('Winter kitchen loaf');
    expect(topic('Recipes in progress').textContent).not.toContain('Soda bread');

    const hrefs = Array.from(root().querySelectorAll('a')).map((link) => link.getAttribute('href') ?? '');
    expect(hrefs.length).toBeGreaterThan(0);
    for (const href of hrefs) {
      expect(href.startsWith(`/${other}/`)).toBe(true);
    }
  });

  it('says a workspace it cannot find is not found', async () => {
    await mount(of({ status: 'not_found' }));

    expect(root().querySelector('[role="alert"]')!.textContent).toContain('could not be found');
  });
});
