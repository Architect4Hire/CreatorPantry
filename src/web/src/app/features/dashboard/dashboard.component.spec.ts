import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { decodeDashboard } from '../../models/dashboard.models';
import { DashboardOutcome, DashboardService } from '../../services/dashboard.service';
import { DashboardComponent } from './dashboard.component';
import { dashboardBody } from './dashboard.fixtures';

const SLUG = 'cozy-fall';

describe('DashboardComponent', () => {
  let harness: RouterTestingHarness;
  let answers: Observable<DashboardOutcome>[];
  let reads: string[];

  const root = (): HTMLElement => harness.routeNativeElement!;
  const text = (): string => (root().textContent ?? '').replace(/\s+/g, ' ');
  const topic = (heading: string): HTMLElement =>
    Array.from(root().querySelectorAll('cp-topic-card')).find(
      (each) => each.querySelector('.heading')?.textContent?.trim() === heading,
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

  async function mount(...outcomes: Observable<DashboardOutcome>[]): Promise<void> {
    answers = outcomes;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/dashboard', component: DashboardComponent, data: { title: 'Dashboard' } }]),
        {
          provide: DashboardService,
          useValue: {
            get: (slug: string) => {
              reads.push(slug);

              return answers.shift() ?? found();
            },
          },
        },
      ],
    });

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/${SLUG}/dashboard`, DashboardComponent);
    await settle();
  }

  beforeEach(() => {
    reads = [];
  });

  // ---- The page ----

  it('is a detail page about the workspace: one head and a heading for every topic, in order of urgency', async () => {
    await mount(found());

    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(root().querySelector('h1')!.textContent).toBe('Dashboard');
    expect(Array.from(root().querySelectorAll('cp-topic-card h2')).map((heading) => heading.textContent?.trim())).toEqual([
      'Publishing that needs you',
      'Readiness blockers',
      'Test kitchen follow-ups',
      'AI proposals to review',
      'Coming up',
      'Recipes in progress',
      'Content board',
      'Recently added pictures',
    ]);
    expect(root().querySelector('ul[cpCardGrid]')!.getAttribute('aria-label')).toBe('Your workspace at a glance');
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

    const blocked = topic('Readiness blockers').querySelector('a')!;
    expect(blocked.textContent).toBe('Soda bread');
    expect(blocked.getAttribute('href')).toBe(`/${SLUG}/recipes/r1?area=readiness`);
    expect(topic('Readiness blockers').textContent).toContain('4 blockers');
    expect(Array.from(topic('Readiness blockers').querySelectorAll('.reasons li')).map((reason) => reason.textContent)).toEqual([
      'Add at least one ingredient.',
      'Add at least one step.',
      'Say how much it makes.',
    ]);

    expect(topic('Test kitchen follow-ups').querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/recipes/r1?area=tests`);
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
    await mount(found({ timeZoneId: null, today: null, upcoming: { status: 'Unavailable', data: null } }));

    const updated = root().querySelector('.updated')!.textContent!;

    // No day is named at all: a guessed one reads as an answer and is wrong by up to a day either way.
    expect(updated).not.toContain('October');
    expect(updated).toContain('Brand settings');
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
    await mount(found({ board: { status: 'Unavailable', data: null }, recentUploads: { status: 'Unavailable', data: null } }));

    expect(topic('Readiness blockers').querySelector('a')!.getAttribute('aria-label')).toBe('Readiness of Soda bread');
    const retries = Array.from(root().querySelectorAll('cp-topic-card button')).map((retry) => retry.getAttribute('aria-label'));
    expect(retries).toEqual(['Try reading the board again', 'Try reading recent pictures again']);
  });

  it('says none is blocked only about recipes that were actually checked', async () => {
    await mount(found({ readinessBlockers: { status: 'Ready', data: { evaluated: 0, unevaluated: 2, blocked: [] } } }));

    const readiness = topic('Readiness blockers');
    expect(readiness.textContent).not.toContain('None of them is blocked');
    expect(readiness.textContent).not.toContain('Checked');
    expect(readiness.textContent).toContain("2 recipes couldn't be checked just now.");
  });

  // ---- States ----

  it('says it is loading until the dashboard answers', async () => {
    const answer = new Subject<DashboardOutcome>();
    await mount(answer);

    expect(root().querySelector('cp-card [role="status"]')!.textContent).toContain('Loading your dashboard');
    expect(root().querySelector('cp-topic-card')).toBeNull();

    answer.next({ status: 'found', dashboard: decodeDashboard(dashboardBody())! });
    answer.complete();
    await settle();
    expect(root().querySelectorAll('cp-topic-card').length).toBe(8);
  });

  it('says so when it cannot be loaded, keeps its head, and loads on request with focus on the page', async () => {
    await mount(of({ status: 'unavailable' }), found());

    expect(root().querySelector('h1')!.textContent).toBe('Dashboard');
    expect(root().querySelector('[role="alert"]')!.textContent).toContain("couldn't be loaded");

    button('Try again').click();
    await settle();

    expect(root().querySelectorAll('cp-topic-card').length).toBe(8);

    // The control that was pressed has gone; focus goes to the page's heading, not to the document.
    expect(document.activeElement).toBe(root().querySelector('h1'));
  });

  it('keeps every other topic when one could not be read, and says so inside that one alone', async () => {
    await mount(found({ testFollowUps: { status: 'Unavailable', data: null } }));

    const tests = topic('Test kitchen follow-ups');
    expect(tests.querySelector('cp-notice')!.textContent).toContain("couldn't be read just now");
    expect(tests.querySelector('.items')).toBeNull();
    expect(topic('Readiness blockers').querySelector('cp-notice')).toBeNull();
    expect(topic('Content board').textContent).toContain('12');
  });

  it('says what each empty topic means in its own words', async () => {
    await mount(
      found({
        publishing: { status: 'Ready', data: { counts: [], needsAttentionCount: 0, needsAttention: [] } },
        aiProposals: { status: 'Ready', data: { awaitingDecision: 0, recent: [] } },
        testFollowUps: { status: 'Ready', data: { open: 0, blocking: 0, major: 0, minor: 0, recent: [] } },
        upcoming: { status: 'Ready', data: { from: '2026-10-14', through: '2026-10-27', cardCount: 0, cards: [], postCount: 0, posts: [] } },
        readinessBlockers: { status: 'Ready', data: { evaluated: 3, unevaluated: 1, blocked: [] } },
      }),
    );

    expect(topic('Publishing that needs you').textContent).toContain('No post failed or is waiting on you.');
    expect(topic('AI proposals to review').textContent).toContain('No proposals are waiting for you.');
    expect(topic('Test kitchen follow-ups').textContent).toContain('No test issues are waiting to be resolved.');
    expect(topic('Coming up').textContent).toContain('Nothing planned or scheduled in the next two weeks.');
    expect(topic('Readiness blockers').textContent).toContain('None of them is blocked.');
    expect(topic('Readiness blockers').textContent).toContain("One recipe couldn't be checked just now.");
  });

  it('offers a first step to a workspace with nothing in it yet', async () => {
    await mount(
      found({
        recipesInProgress: {
          status: 'Ready',
          data: {
            counts: ['Draft', 'InDevelopment', 'Testing', 'ReadyForReview'].map((status) => ({ status, count: 0 })),
            recent: [],
          },
        },
        board: { status: 'Ready', data: { total: 0, columns: [] } },
        recentUploads: { status: 'Ready', data: { items: [] } },
      }),
    );

    const start = topic('Get started');
    expect(start).toBeTruthy();
    expect(Array.from(start.querySelectorAll('a')).map((link) => link.getAttribute('href'))).toEqual([
      `/${SLUG}/recipes/new`,
      `/${SLUG}/content-board`,
    ]);
  });

  it('does not offer a first step while a part it would judge by could not be read', async () => {
    await mount(found({ recentUploads: { status: 'Unavailable', data: null }, board: { status: 'Ready', data: { total: 0, columns: [] } } }));

    expect(topic('Get started')).toBeUndefined();
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
    expect(root().querySelectorAll('cp-topic-card').length).toBe(8);
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

  it('says a workspace it cannot find is not found', async () => {
    await mount(of({ status: 'not_found' }));

    expect(root().querySelector('[role="alert"]')!.textContent).toContain('could not be found');
  });
});
