import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { WeekOutcome, WorkTaskService } from '../../services/work-task.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { QuickCaptureService } from '../../shared/quick-capture/quick-capture.service';
import { MyWeekComponent } from './my-week.component';
import { WEEK, fullWeek, task, week } from './my-week.fixtures';

const SLUG = 'sams-kitchen';

describe('MyWeekComponent', () => {
  let harness: RouterTestingHarness;
  let answers: WeekOutcome[];
  let weekSpy: jasmine.Spy<(slug: string, weekStart: string | null) => Promise<WeekOutcome>>;
  let tagsList: jasmine.Spy;
  let memberships: ReturnType<typeof signal<MyMembershipsState>>;
  let captures: QuickCaptureService;

  function membership(role: 'Owner' | 'Viewer'): MyMembershipsState {
    return {
      status: 'ready',
      memberships: [{ workspaceId: 'w-1', workspaceSlug: SLUG, workspaceName: "Sam's Kitchen", membershipId: 'm-1', role, status: 'Active' }],
    } as MyMembershipsState;
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      harness.detectChanges();
      await harness.fixture.whenStable();
      await new Promise((resolve) => setTimeout(resolve));
    }
    harness.detectChanges();
  }

  async function mount(url: string, ...outcomes: WeekOutcome[]): Promise<void> {
    answers = outcomes;
    weekSpy = jasmine.createSpy('week').and.callFake(async () => (answers.length > 1 ? answers.shift()! : answers[0]));
    tagsList = jasmine.createSpy('list').and.resolveTo({ status: 'found', tags: [{ id: 'tag-1', name: 'Weeknight' }] });
    memberships = signal(membership('Owner'));

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/my-week', component: MyWeekComponent, data: { title: 'My Week' } }]),
        { provide: WorkTaskService, useValue: { week: weekSpy } },
        { provide: WorkspaceTagService, useValue: { list: tagsList } },
        { provide: WorkspaceMembershipService, useValue: { state: memberships, ensureLoaded: async () => undefined } },
      ],
    });

    captures = TestBed.inject(QuickCaptureService);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, MyWeekComponent);
    await settle();
  }

  const found = (shown = fullWeek()): WeekOutcome => ({ status: 'found', week: shown });
  const root = () => harness.routeNativeElement as HTMLElement;
  const columnHeadings = () => [...root().querySelectorAll('cp-board-column h3')].map((h) => h.textContent!.replace(/\s+/g, ' ').trim());
  const text = () => root().textContent!.replace(/\s+/g, ' ');
  const chip = (label: string) => [...root().querySelectorAll<HTMLButtonElement>('button[cpChip]')].find((b) => b.textContent!.trim() === label)!;
  const link = (label: string) => [...root().querySelectorAll<HTMLAnchorElement>('a')].find((a) => a.textContent!.replace(/[‹›]/g, '').trim() === label);
  const query = () => TestBed.inject(Router).routerState.snapshot.root.queryParams;

  async function navigate(url: string): Promise<void> {
    await harness.navigateByUrl(url);
    await settle();
  }

  // ---- The board ----

  it('is a library laid out as a board: one heading, the week, a toolbar, and five columns in the compact view', async () => {
    await mount(`/${SLUG}/my-week`, found());

    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(root().querySelector('h1')!.textContent!.trim()).toBe('My Week');
    expect(text()).toContain('Week of Mon 11 Oct — this week');
    expect(root().querySelector('[role="toolbar"]')!.getAttribute('aria-label')).toBe('Week, filters and view');
    expect(root().querySelector('[cpBoard]')).not.toBeNull();
    expect(weekSpy).toHaveBeenCalledWith(SLUG, null);
  });

  it('says what the week adds up to in the server’s numbers, and each day’s own totals', async () => {
    await mount(`/${SLUG}/my-week`, found());

    expect(text()).toContain('4 tasks · 2 hr 30 min estimated · 2 not estimated');
    const thursday = root().querySelector('#my-week-section-day-2038-10-14')!.closest('section')!;
    expect(thursday.textContent).toContain('Today');
    expect(thursday.textContent).toContain('2 tasks · 1 hr estimated · 1 not estimated');
  });

  it('lists every task once, as an article in a list, with the column’s count read in words', async () => {
    await mount(`/${SLUG}/my-week`, found());

    const ids = [...root().querySelectorAll('li[data-task-id]')].map((row) => row.getAttribute('data-task-id'));
    expect(ids.length).toBe(9);
    expect(new Set(ids).size).toBe(9);
    expect(root().querySelectorAll('li[data-task-id] article').length).toBe(9);
  });

  it('shows each day as its own column in the Days view, kept in the address', async () => {
    await mount(`/${SLUG}/my-week`, found());

    chip('Days').click();
    await settle();

    expect(query()['view']).toBe('days');
    expect(chip('Days').getAttribute('aria-pressed')).toBe('true');
    expect(columnHeadings().length).toBe(11);
    expect(weekSpy).toHaveBeenCalledTimes(1);
  });

  it('starts in the Days view when the address says so', async () => {
    await mount(`/${SLUG}/my-week?view=days`, found());

    expect(chip('Days').getAttribute('aria-pressed')).toBe('true');
  });

  // ---- Weeks ----

  it('moves between weeks by real links, reading the week each names', async () => {
    await mount(`/${SLUG}/my-week`, found(), found(week({ weekStart: '2038-10-18', isCurrentWeek: false })));

    expect(link('Next week')!.getAttribute('href')).toBe(`/${SLUG}/my-week?week=2038-10-18`);
    expect(link('Previous week')!.getAttribute('href')).toBe(`/${SLUG}/my-week?week=2038-10-04`);
    expect(link('This week')).toBeUndefined();

    await navigate(`/${SLUG}/my-week?week=2038-10-18`);

    expect(weekSpy).toHaveBeenCalledWith(SLUG, '2038-10-18');
    expect(link('This week')).toBeTruthy();
  });

  it('keeps the way to other weeks when a week cannot be read', async () => {
    await mount(`/${SLUG}/my-week?week=2038-10-18`, { status: 'unavailable', offline: false });

    expect(link('Previous week')!.getAttribute('href')).toBe(`/${SLUG}/my-week?week=2038-10-11`);
    expect(link('This week')).toBeTruthy();
  });

  it('puts the week links in the toolbar’s actions, not loose in its filters', async () => {
    await mount(`/${SLUG}/my-week`, found());

    const groups = [...root().querySelectorAll('[role="toolbar"] > .group')];
    expect(groups[2].contains(link('Next week')!)).toBeTrue();
  });

  it('treats a date that is not on the calendar as no week at all', async () => {
    await mount(`/${SLUG}/my-week?week=2038-02-31`, { status: 'invalid' }, found());

    expect(weekSpy.calls.first().args[1]).toBe('2038-02-31');
    expect(query()['week']).toBeUndefined();
  });

  it('reads any day of a week as that week', async () => {
    await mount(`/${SLUG}/my-week?week=2038-10-14`, found());

    expect(weekSpy).toHaveBeenCalledWith(SLUG, WEEK);
  });

  it('shows the current week, and says why, when the address names no week', async () => {
    await mount(`/${SLUG}/my-week?week=nonsense`, { status: 'invalid' }, found());

    expect(query()['week']).toBeUndefined();
    expect(text()).toContain('didn’t name a week');
  });

  // ---- Filters ----

  it('filters the loaded cards from the address and says how many of each list it shows', async () => {
    await mount(`/${SLUG}/my-week?q=galette`, found());

    expect([...root().querySelectorAll('li[data-task-id]')].map((row) => row.getAttribute('data-task-id'))).toEqual(['thu-2']);
    expect(text()).toContain('1 of 2 shown');
    expect(root().querySelector<HTMLInputElement>('input[type="search"]')!.value).toBe('galette');
  });

  it('turns the High priority chip on and off in the address, and clears every filter at once', async () => {
    await mount(`/${SLUG}/my-week?label=tag-1`, found());

    chip('High priority').click();
    await settle();
    expect(query()['priority']).toBe('high');
    expect(chip('High priority').getAttribute('aria-pressed')).toBe('true');

    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === 'Clear filters')!.click();
    await settle();
    expect(query()['priority']).toBeUndefined();
    expect(query()['label']).toBeUndefined();
  });

  it('filters once typing pauses, keeping every keystroke in the box meanwhile', async () => {
    await mount(`/${SLUG}/my-week`, found());
    const box = root().querySelector<HTMLInputElement>('input[type="search"]')!;

    box.value = 'gal';
    box.dispatchEvent(new Event('input'));
    box.value = 'galette';
    box.dispatchEvent(new Event('input'));
    await settle();
    expect(query()['q']).toBeUndefined();
    expect(box.value).toBe('galette');

    // Well past the pause, so a slow machine does not catch it mid-wait.
    await new Promise((resolve) => setTimeout(resolve, 700));
    await settle();
    expect(query()['q']).toBe('galette');
    expect(root().querySelectorAll('li[data-task-id]').length).toBe(1);
  });

  it('puts focus in the search box after Clear filters, whose button goes with them', async () => {
    await mount(`/${SLUG}/my-week?priority=high`, found());

    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === 'Clear filters')!.click();
    await settle();

    expect(document.activeElement).toBe(root().querySelector('input[type="search"]'));
  });

  it('says when nothing matches the filters, with a way to clear them', async () => {
    await mount(`/${SLUG}/my-week?q=nothing-like-this`, found());

    expect(text()).toContain('No tasks match these filters');
  });

  it('lets a label in the address hide nothing while the labels cannot be read, and keeps it for later', async () => {
    await mount(`/${SLUG}/my-week`, found());
    tagsList.and.resolveTo({ status: 'unavailable' });
    await navigate(`/other-kitchen/my-week?label=tag-1`);

    expect(root().querySelectorAll('li[data-task-id]').length).toBe(9);
    expect(query()['label']).toBe('tag-1');
    expect(text()).not.toMatch(/\d+ of (the first )?\d+ shown/);
  });

  it('keeps the board and turns the label filter off when the labels cannot be read', async () => {
    await mount(`/${SLUG}/my-week`, found());
    tagsList.and.resolveTo({ status: 'unavailable' });
    await navigate(`/other-kitchen/my-week`);

    expect(text()).toContain('the label filter is off for now');
    expect(root().querySelector<HTMLSelectElement>('select[name="label"]')!.disabled).toBeTrue();
    expect(root().querySelectorAll('li[data-task-id]').length).toBe(9);
  });

  // ---- States ----

  it('asks for a time zone rather than guessing the week’s edges, and offers nothing to do over nothing', async () => {
    await mount(`/${SLUG}/my-week`, { status: 'no_time_zone' });

    expect([...root().querySelectorAll('button')].some((b) => b.textContent!.includes('Add a task'))).toBeFalse();
    expect(root().querySelector('[role="toolbar"]')!.getAttribute('aria-disabled')).toBe('true');

    expect(text()).toContain('Your week needs a time zone');
    expect(link('Set the time zone in Brand settings')!.getAttribute('href')).toBe(`/${SLUG}/brand`);
  });

  it('says it could not read the week, and reads it again when asked', async () => {
    await mount(`/${SLUG}/my-week`, { status: 'unavailable', offline: false }, found());

    expect(root().querySelector('cp-list-shell [role="alert"]')!.textContent).toContain('couldn’t be read');
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === 'Try again')!.click();
    await settle();

    expect(root().querySelectorAll('li[data-task-id]').length).toBe(9);
    expect(document.activeElement?.textContent?.trim()).toBe('Your week');
  });

  it('says a week has nothing planned, with the head’s Add a task the one way to add', async () => {
    await mount(`/${SLUG}/my-week`, found(week()));

    expect(text()).toContain('Nothing planned for this week');
    expect([...root().querySelectorAll('button')].filter((b) => b.textContent!.includes('Add a task')).length).toBe(1);
  });

  it('shows a Viewer the week with no Add a task', async () => {
    await mount(`/${SLUG}/my-week`, found());
    memberships.set(membership('Viewer'));
    await settle();

    expect([...root().querySelectorAll('button')].some((b) => b.textContent!.includes('Add a task'))).toBeFalse();
    expect(root().querySelectorAll('li[data-task-id]').length).toBe(9);
  });

  // ---- Quick capture ----

  it('captures into this week by default, into the week on screen otherwise, and reads the week again after', async () => {
    await mount(`/${SLUG}/my-week`, found(), found(week({ weekStart: '2038-10-18', isCurrentWeek: false })));
    const open = spyOn(captures, 'open');
    const add = () => [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === '+ Add a task')!;

    add().click();
    expect(open).toHaveBeenCalledWith({});
    expect(document.activeElement).toBe(add());

    await navigate(`/${SLUG}/my-week?week=2038-10-18`);
    add().click();
    expect(open).toHaveBeenCalledWith({ weekStart: '2038-10-18' });

    const reads = weekSpy.calls.count();
    captures.notifyCaptured(task({ id: 'new' }));
    await settle();
    expect(weekSpy.calls.count()).toBe(reads + 1);
  });

  // ---- Narrow ----

  it('offers a jump list to each column, which brings the column into view and focuses its heading', async () => {
    await mount(`/${SLUG}/my-week`, found());

    const jump = [...root().querySelectorAll<HTMLAnchorElement>('cp-anchor-nav a')];
    expect(jump.map((a) => a.textContent!.replace(/\s+/g, ' ').trim())[0]).toContain('Backlog');

    jump[2].click();
    await settle();
    expect(document.activeElement?.textContent).toContain('In progress');
  });
});
