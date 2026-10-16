import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { NetworkStatusService } from '../../core/network-status.service';
import { MyMembershipsState } from '../../services/workspace-membership.service';
import { TodayWorkTasks, WorkTask } from '../../models/work-task.models';
import { TodayOutcome, TopThreeOutcome, WorkTaskChangeOutcome, WorkTaskService } from '../../services/work-task.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { QuickCaptureService } from '../../shared/quick-capture/quick-capture.service';
import { MyDayComponent } from './my-day.component';
import { RUN, TODAY, day, fullDay, task } from './my-day.fixtures';

const SLUG = 'sams-kitchen';

describe('MyDayComponent', () => {
  let harness: RouterTestingHarness;
  let answers: TodayOutcome[];
  let reads: number;
  let today: jasmine.Spy;
  let complete: jasmine.Spy<(slug: string, task: WorkTask) => Promise<WorkTaskChangeOutcome>>;
  let reopen: jasmine.Spy<(slug: string, task: WorkTask) => Promise<WorkTaskChangeOutcome>>;
  let move: jasmine.Spy;
  let setTopThree: jasmine.Spy<(slug: string, ids: readonly string[]) => Promise<TopThreeOutcome>>;
  let online: ReturnType<typeof signal<boolean>>;
  let memberships: ReturnType<typeof signal<MyMembershipsState>>;
  let navigate: jasmine.Spy;
  let captures: QuickCaptureService;

  const found = (shown: TodayWorkTasks): TodayOutcome => ({ status: 'found', day: shown });
  const saved = (shown: WorkTask): WorkTaskChangeOutcome => ({ status: 'saved', task: shown });

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

  async function mount(first: TodayOutcome, ...rest: TodayOutcome[]): Promise<void> {
    answers = [first, ...rest];
    reads = 0;
    today = jasmine.createSpy('today').and.callFake(async () => {
      reads += 1;
      return answers.length > 1 ? answers.shift()! : answers[0];
    });
    complete = jasmine.createSpy('complete').and.callFake(async (_slug, shown: WorkTask) => saved({ ...shown, status: 'Done' }));
    reopen = jasmine.createSpy('reopen').and.callFake(async (_slug, shown: WorkTask) => saved({ ...shown, status: 'Backlog' }));
    move = jasmine.createSpy('move').and.callFake(async (_slug: string, shown: WorkTask) => saved(shown));
    setTopThree = jasmine.createSpy('setTopThree').and.callFake(async (_slug, ids) => ({ status: 'saved', taskIds: ids }));
    online = signal(true);
    memberships = signal(membership('Owner'));

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/my-day', component: MyDayComponent, data: { title: 'My Day' } }]),
        { provide: WorkTaskService, useValue: { today, complete, reopen, move, setTopThree } },
        { provide: WorkspaceMembershipService, useValue: { state: memberships, ensureLoaded: async () => undefined } },
        { provide: NetworkStatusService, useValue: { online } },
      ],
    });

    captures = TestBed.inject(QuickCaptureService);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/${SLUG}/my-day`, MyDayComponent);
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    await settle();
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const headings = () => [...root().querySelectorAll('cp-topic-card h2')].map((heading) => heading.textContent!.trim());
  const named = (name: string) => root().querySelector<HTMLElement>(`[aria-label="${name}"]`);
  const button = (text: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((candidate) => candidate.textContent!.trim() === text);
  const outcome = () => root().querySelector('cp-notice.outcome')!.textContent!.replace(/\s+/g, ' ').trim();
  const rowsIn = (list: string) => [...root().querySelectorAll<HTMLElement>(`#my-day-list-${list} > li`)];

  async function press(element: HTMLElement | null): Promise<void> {
    expect(element).withContext('the control to press').toBeTruthy();
    element!.click();
    await settle();
  }

  // ---- The page ----

  it('is a detail page about the day: one heading, the date, Add a task, then what to do next and each group', async () => {
    await mount(found(fullDay()));

    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(root().querySelector('h1')!.textContent!.trim()).toBe('My Day');
    expect(root().textContent).toContain('Thursday 14 October');
    expect(button('+ Add a task')?.getAttribute('aria-haspopup')).toBe('dialog');

    // The overdue task is up next, so its own group has nothing left to show and is not drawn.
    expect(headings()).toEqual(['Up next', 'Today', 'In progress', 'Waiting', 'Done today']);
    expect(today).toHaveBeenCalledWith(SLUG, true);
  });

  it('lists every task on the day exactly once, and none that is not', async () => {
    await mount(found(fullDay()));

    const ids = [...root().querySelectorAll('li[data-task-id]')].map((row) => row.getAttribute('data-task-id'));
    expect(ids.length).toBe(6);
    expect(new Set(ids).size).toBe(6);
    expect(rowsIn('lead')[0].textContent).toContain('Overdue since Tue 12 Oct');
  });

  it('leads with the creator’s top three, and offers to change it', async () => {
    await mount(found(fullDay({ topThree: ['plan-2', 'going'] })));

    expect(headings()[0]).toBe('Your top three');
    expect(rowsIn('lead').map((row) => row.getAttribute('data-task-id'))).toEqual(['plan-2', 'going']);
    expect(button('Change my top three')).toBeTruthy();
  });

  it('says when a group holds more than it shows, and where to see the rest', async () => {
    await mount(found(fullDay({ scheduledTodayHasMore: true })));

    const note = [...root().querySelectorAll('p')].find((p) => p.textContent!.includes('Showing the first 50'))!;
    expect(note.querySelector('a')!.getAttribute('href')).toBe(`/${SLUG}/my-week`);
  });

  // ---- Done, Undo and focus ----

  it('marks a task done, reads the day again, says so with Undo, and focuses the next task in that list', async () => {
    const before = fullDay();
    const after = fullDay({
      scheduledToday: [before.scheduledToday[1]],
      completedToday: [...before.completedToday, { ...before.scheduledToday[0], status: 'Done' }],
    });
    await mount(found(before), found(after));

    await press(named('Mark “Write the headnote” done'));

    expect(complete).toHaveBeenCalledWith(SLUG, before.scheduledToday[0]);
    expect(reads).toBe(2);
    expect(outcome()).toContain('Done: “Write the headnote”.');
    expect(button('Undo')).toBeTruthy();
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Mark “Edit the photos” done');
  });

  it('undoes a completion by reopening the task as the server last answered it', async () => {
    await mount(found(fullDay()));

    await press(named('Mark “Write the headnote” done'));
    const completed = (await complete.calls.mostRecent().returnValue) as { task: WorkTask };
    await press(button('Undo')!);

    expect(reopen).toHaveBeenCalledWith(SLUG, completed.task);
    expect(outcome()).toContain('Undone');
  });

  it('focuses the list’s heading when the last task leaves it', async () => {
    const before = day({ waiting: [task({ id: 'held', title: 'Hear back', status: 'Waiting' })], inProgress: [task({ id: 'going', title: 'Draft', status: 'InProgress' })] });
    const after = day({ waiting: [task({ id: 'held', title: 'Hear back', status: 'Waiting' })] });
    await mount(found(before), found(after));

    // "Draft" is up next; once it is done nothing is, and focus lands on the heading that now says so.
    await press(named('Mark “Draft” done'));

    expect(document.activeElement?.textContent?.trim()).toBe('Nothing to do next');
  });

  // ---- Move ----

  it('moves a task from its inline choices and says where it went', async () => {
    const before = fullDay();
    const after = fullDay({ inProgress: [], waiting: [...before.waiting, { ...before.inProgress[0], status: 'Waiting' }] });
    await mount(found(before), found(after));

    await press(named('Move “Draft the newsletter”'));
    await press(root().querySelector<HTMLElement>('[aria-label="Move “Draft the newsletter” to"] [data-move="waiting"]'));

    expect(move).toHaveBeenCalledWith(SLUG, before.inProgress[0], { status: 'Waiting' });
    expect(outcome()).toContain('“Draft the newsletter” is now under Waiting.');
  });

  it('says honestly that a late task planned for tomorrow stays under Overdue', async () => {
    const shown = fullDay({ topThree: ['plan-1'] });
    await mount(found(shown));

    await press(named('Move “Send the invoice”'));
    await press(root().querySelector<HTMLElement>('[data-move="tomorrow"]'));

    expect(move).toHaveBeenCalledWith(SLUG, shown.overdue[0], { status: 'Backlog', scheduledDate: '2038-10-15' });
    expect(outcome()).toContain('stays under Overdue until its due date changes');
  });

  it('says a task changed elsewhere, writes nothing, and shows the day as it now stands', async () => {
    await mount(found(fullDay()));
    complete.and.resolveTo({ status: 'stale' });

    await press(named('Mark “Write the headnote” done'));

    expect(outcome()).toContain('changed somewhere else');
    expect(button('Undo')).toBeUndefined();
    expect(reads).toBe(2);
  });

  // ---- Start ----

  it('starts a task about nothing in particular by marking it in progress, and stays on the page', async () => {
    await mount(found(fullDay({ overdue: [] })));

    await press(named('Start “Write the headnote”'));

    expect(move).toHaveBeenCalledWith(SLUG, jasmine.objectContaining({ id: 'plan-1' }), { status: 'InProgress' });
    expect(navigate).not.toHaveBeenCalled();
  });

  it('starts a task from a workflow by marking it in progress and then opening the run', async () => {
    const linked = task({ id: 'step', title: 'Shoot the hero picture', links: { ...task().links, workflowRunId: RUN, workflowRunRoute: `workflows/runs/${RUN}` } });
    await mount(found(day({ scheduledToday: [linked] })));

    await press(named('Start “Shoot the hero picture”'));

    expect(move).toHaveBeenCalledWith(SLUG, linked, { status: 'InProgress' });
    expect(navigate).toHaveBeenCalledWith(['/', SLUG, 'workflows', 'runs', RUN]);
  });

  it('stays on the page with the reason and a way to open the work anyway when starting fails', async () => {
    const linked = task({ id: 'r', title: 'Test the pie', links: { ...task().links, recipeId: 'r-1' } });
    await mount(found(day({ scheduledToday: [linked] })));
    move.and.resolveTo({ status: 'unavailable', offline: false });

    await press(named('Start “Test the pie”'));

    expect(navigate).not.toHaveBeenCalled();
    expect(outcome()).toContain('couldn’t be marked as started');
    await press(button('Open the recipe anyway')!);
    expect(navigate).toHaveBeenCalledWith(['/', SLUG, 'recipes', 'r-1']);
  });

  // ---- An empty day ----

  it('says there is nothing planned, and only then offers suggestions, which plan nothing until asked', async () => {
    const suggestion = task({ id: 'idea', title: 'Pitch the cookbook', scheduledDate: null });
    await mount(found(day({ suggested: [{ task: suggestion, reason: 'Next in your backlog.' }] })));

    expect(headings()).toEqual(['Nothing planned for today', 'If you have time']);
    expect(root().textContent).toContain('Suggestion');
    expect(move).not.toHaveBeenCalled();

    await press(named('Plan for today: Pitch the cookbook'));
    expect(move).toHaveBeenCalledWith(SLUG, suggestion, { status: 'Backlog', scheduledDate: TODAY });
    expect(outcome()).toContain('Planned “Pitch the cookbook” for today.');
  });

  it('does not offer suggestions on a day that still has work in it', async () => {
    await mount(found(fullDay({ suggested: [{ task: task({ id: 'idea' }), reason: 'Next in your backlog.' }] })));

    expect(headings()).not.toContain('If you have time');
  });

  // ---- States ----

  it('asks for a time zone rather than guessing a day', async () => {
    await mount({ status: 'no_time_zone' });

    expect(headings()).toEqual(['Your day needs a time zone']);
    const link = [...root().querySelectorAll('a')].find((a) => a.textContent!.includes('Brand settings'))!;
    expect(link.getAttribute('href')).toBe(`/${SLUG}/brand`);
  });

  it('says it could not read the day, and reads it again when asked', async () => {
    await mount({ status: 'unavailable', offline: false }, found(fullDay()));

    expect(root().querySelector('[role="alert"]')!.textContent).toContain('couldn’t be read');
    await press(button('Try again')!);

    expect(headings()[0]).toBe('Up next');
  });

  it('shows a Viewer their day with no way to change it', async () => {
    await mount(found(fullDay()));
    memberships.set(membership('Viewer'));
    await settle();

    expect(root().textContent).toContain('view-only for you');
    expect(button('+ Add a task')).toBeUndefined();
    expect(button('Choose my top three')).toBeUndefined();
    expect(root().querySelectorAll('li[data-task-id] button').length).toBe(0);
  });

  it('says nothing about view-only while the memberships are still being read', async () => {
    await mount(found(fullDay()));
    memberships.set({ status: 'loading' });
    await settle();

    expect(root().textContent).not.toContain('view-only');
    expect(button('+ Add a task')).toBeUndefined();
  });

  it('says it is still saving when a second change is pressed before the first answers', async () => {
    await mount(found(fullDay()));
    let finish!: (outcome: WorkTaskChangeOutcome) => void;
    complete.and.returnValue(new Promise((resolve) => (finish = resolve)));

    named('Mark “Write the headnote” done')!.click();
    await settle();
    await press(named('Mark “Edit the photos” done'));

    expect(complete).toHaveBeenCalledTimes(1);
    expect(outcome()).toContain('Still saving the last change');

    finish({ status: 'saved', task: task({ id: 'plan-1', title: 'Write the headnote', status: 'Done' }) });
    await settle();
  });

  it('puts focus back on the task when a change is refused, rather than losing it with the closed choices', async () => {
    await mount(found(fullDay()));
    move.and.resolveTo({ status: 'unavailable', offline: false });

    await press(named('Move “Draft the newsletter”'));
    await press(named('Move “Draft the newsletter” to Waiting'));

    expect(root().querySelector('[role="group"]')).toBeNull();
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Mark “Draft the newsletter” done');
  });

  it('puts focus on the task again after Undo, whose button goes with the message', async () => {
    await mount(found(fullDay()));

    await press(named('Mark “Write the headnote” done'));
    await press(button('Undo')!);

    expect(document.activeElement?.closest('li')?.getAttribute('data-task-id')).toBe('plan-1');
  });

  it('says the day may be out of date when it cannot be read again after a capture', async () => {
    await mount(found(fullDay()), { status: 'unavailable', offline: false });

    captures.notifyCaptured(task({ id: 'new' }));
    await settle();

    expect(outcome()).toContain('couldn’t be read again');
    expect(button('Try again')).toBeTruthy();
  });

  it('refuses a change while offline and says why', async () => {
    await mount(found(fullDay()));
    online.set(false);
    await settle();

    const done = named('Mark “Write the headnote” done')!;
    expect(done.getAttribute('aria-disabled')).toBe('true');
    await press(done);

    expect(complete).not.toHaveBeenCalled();
    expect(root().textContent).toContain('You’re offline');
  });

  // ---- Quick capture ----

  it('opens quick capture on today, and reads the day again when a task is captured', async () => {
    await mount(found(fullDay()));
    const open = spyOn(captures, 'open');

    await press(button('+ Add a task')!);
    expect(open).toHaveBeenCalledWith({ today: TODAY });
    expect(document.activeElement).toBe(button('+ Add a task')!);

    captures.notifyCaptured(task({ id: 'new' }));
    await settle();
    expect(reads).toBe(2);
  });

  // ---- Top three ----

  it('saves a top three from its dialog and reads the day again', async () => {
    await mount(found(fullDay()), found(fullDay({ topThree: ['going'] })));

    await press(button('Choose my top three')!);
    await press(root().querySelector<HTMLInputElement>('#my-day-top-three-going'));
    await press(button('Save')!);

    expect(setTopThree).toHaveBeenCalledWith(SLUG, ['going']);
    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(outcome()).toContain('Your top three is at the top of your day.');
    expect(headings()[0]).toBe('Your top three');
  });

  it('keeps the dialog open and says why when a chosen task has left the day', async () => {
    await mount(found(fullDay()));
    setTopThree.and.resolveTo({ status: 'refused' });

    await press(button('Choose my top three')!);
    await press(root().querySelector<HTMLInputElement>('#my-day-top-three-going'));
    await press(button('Save')!);

    expect(root().querySelector('[role="dialog"]')).not.toBeNull();
    expect(root().querySelector('[role="dialog"] [role="alert"]')!.textContent).toContain('no longer on your day');
    expect(reads).toBe(2);
  });
});
