import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { NetworkStatusService } from '../../core/network-status.service';
import { WeekWorkTasks, WorkTask } from '../../models/work-task.models';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { WeekOutcome, WorkTaskChangeOutcome, WorkTaskService } from '../../services/work-task.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { MyWeekComponent } from './my-week.component';
import { applyPlace, tasksIn } from './my-week.moves';
import { fullWeek } from './my-week.fixtures';

const SLUG = 'sams-kitchen';

/** Moving on My Week (13A.33, turns 2–5): the keyboard form, reordering, dragging, Done and Reopen. */
describe('MyWeekComponent — moving', () => {
  let harness: RouterTestingHarness;
  let answers: WeekOutcome[];
  let weekSpy: jasmine.Spy;
  let move: jasmine.Spy;
  let reorder: jasmine.Spy;
  let complete: jasmine.Spy;
  let reopen: jasmine.Spy;
  let online: ReturnType<typeof signal<boolean>>;

  const found = (shown: WeekWorkTasks = fullWeek()): WeekOutcome => ({ status: 'found', week: shown });
  const saved = (shown: WorkTask): WorkTaskChangeOutcome => ({ status: 'saved', task: shown });

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      harness.detectChanges();
      await harness.fixture.whenStable();
      await new Promise((resolve) => setTimeout(resolve));
    }
    harness.detectChanges();
  }

  async function mount(...outcomes: WeekOutcome[]): Promise<void> {
    answers = outcomes;
    weekSpy = jasmine.createSpy('week').and.callFake(async () => (answers.length > 1 ? answers.shift()! : answers[0]));
    move = jasmine.createSpy('move').and.callFake(async (_slug: string, shown: WorkTask) => saved(shown));
    reorder = jasmine.createSpy('reorder').and.callFake(async (_slug: string, shown: WorkTask) => saved(shown));
    complete = jasmine.createSpy('complete').and.callFake(async (_slug: string, shown: WorkTask) => saved({ ...shown, status: 'Done' }));
    reopen = jasmine.createSpy('reopen').and.callFake(async (_slug: string, shown: WorkTask) => saved({ ...shown, status: 'Backlog' }));
    online = signal(true);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/my-week', component: MyWeekComponent, data: { title: 'My Week' } }]),
        { provide: WorkTaskService, useValue: { week: weekSpy, move, reorder, complete, reopen } },
        { provide: WorkspaceTagService, useValue: { list: async () => ({ status: 'found', tags: [] }) } },
        {
          provide: WorkspaceMembershipService,
          useValue: {
            state: signal<MyMembershipsState>({
              status: 'ready',
              memberships: [{ workspaceId: 'w', workspaceSlug: SLUG, workspaceName: 'S', membershipId: 'm-1', role: 'Owner', status: 'Active' }],
            } as MyMembershipsState),
            ensureLoaded: async () => undefined,
          },
        },
        { provide: NetworkStatusService, useValue: { online } },
      ],
    });

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/${SLUG}/my-week`, MyWeekComponent);
    await settle();
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const component = () => harness.routeDebugElement!.componentInstance as MyWeekComponent;
  const named = (name: string) => root().querySelector<HTMLButtonElement>(`[aria-label="${name}"]`)!;
  const dialog = () => document.querySelector<HTMLElement>('[role="dialog"]');
  /** The polite region, for a change that was kept. */
  const outcome = () => root().querySelector('cp-notice.outcome')!;
  /** The assertive region, for one that was not. */
  const refusal = () => root().querySelector('cp-notice.outcome-alert')!;
  const idsIn = (section: string) => [...root().querySelectorAll(`ul[data-section="${section}"] > li`)].map((li) => li.getAttribute('data-task-id'));
  const button = (text: string) => [...document.querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === text)!;
  /** A button inside the Move form — the cards have Move buttons too. */
  const inDialog = (text: string) => [...dialog()!.querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === text)!;

  function choose(selector: string, value: string): void {
    const select = document.querySelector<HTMLSelectElement>(selector)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    harness.detectChanges();
  }

  async function openMove(title: string): Promise<void> {
    named(`Move “${title}”`).click();
    await settle();
  }

  // ---- Turn 2: the keyboard move ----

  it('moves a task to another list from the Move form, shows it there at once, then reads the week again', async () => {
    const before = fullWeek();
    const thursday = tasksIn(before, 'day-2038-10-14')[0];
    await mount(found(before), found(applyPlace(before, thursday, { list: 'waiting', beforeTaskId: null })));
    let finish!: (outcome: WorkTaskChangeOutcome) => void;
    move.and.returnValue(new Promise((resolve) => (finish = resolve)));

    await openMove('Write the headnote');
    expect(dialog()).not.toBeNull();
    choose('#my-week-move-where', 'waiting');
    inDialog('Move').click();
    await settle();

    // Shown in its new place before the server has answered, and saying it is saving.
    expect(idsIn('waiting')).toEqual(['held', 'thu-1']);
    expect(root().querySelector('li[data-task-id="thu-1"] article')!.getAttribute('aria-busy')).toBe('true');
    expect(move).toHaveBeenCalledWith(SLUG, thursday, { status: 'Waiting' });

    finish(saved({ ...thursday, status: 'Waiting' }));
    await settle();

    expect(weekSpy).toHaveBeenCalledTimes(2);
    expect(outcome().getAttribute('role')).toBe('status');
    expect(outcome().textContent).toContain('Moved “Write the headnote” to Waiting.');
    expect(refusal().textContent!.trim()).toBe('');
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Move “Write the headnote”');
  });

  it('puts the task back and says so, at once, when the move never left the browser', async () => {
    await mount(found());
    move.and.resolveTo({ status: 'unavailable', offline: true });

    await openMove('Write the headnote');
    choose('#my-week-move-where', 'waiting');
    inDialog('Move').click();
    await settle();

    expect(idsIn('waiting')).toEqual(['held']);
    expect(idsIn('day-2038-10-14')).toEqual(['thu-1', 'thu-2']);
    expect(refusal().getAttribute('role')).toBe('alert');
    expect(refusal().textContent).toContain('It’s back where it was.');
    expect(weekSpy).toHaveBeenCalledTimes(1);
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Move “Write the headnote”');
  });

  it('reads the week rather than guessing when an answer never came — the move may have been saved', async () => {
    await mount(found(), found());
    move.and.resolveTo({ status: 'unavailable', offline: false });

    await openMove('Write the headnote');
    choose('#my-week-move-where', 'waiting');
    inDialog('Move').click();
    await settle();

    expect(weekSpy).toHaveBeenCalledTimes(2);
    expect(refusal().textContent).toContain('couldn’t be confirmed');
  });

  it('never puts back an old week over one read meanwhile, so the next move goes to the week on screen', async () => {
    const next = fullWeek({ weekStart: '2038-10-18', isCurrentWeek: false });
    await mount(found(), found(next), found(next));
    let finish!: (outcome: WorkTaskChangeOutcome) => void;
    move.and.returnValue(new Promise((resolve) => (finish = resolve)));

    await openMove('Write the headnote');
    choose('#my-week-move-where', 'waiting');
    inDialog('Move').click();
    await settle();

    // The creator moves on to next week before the answer comes, and then the move is refused.
    await harness.navigateByUrl(`/${SLUG}/my-week?week=2038-10-18`);
    await settle();
    finish({ status: 'forbidden' });
    await settle();

    expect(component()['week']()!.weekStart).toBe('2038-10-18');
    expect(weekSpy).toHaveBeenCalledTimes(3);
  });

  it('reads the week again, rather than guessing, when the task changed somewhere else', async () => {
    const moved = fullWeek({ waiting: { ...fullWeek().waiting, tasks: [] } });
    await mount(found(), found(moved));
    move.and.resolveTo({ status: 'stale' });

    await openMove('Write the headnote');
    choose('#my-week-move-where', 'in-progress');
    inDialog('Move').click();
    await settle();

    expect(weekSpy).toHaveBeenCalledTimes(2);
    expect(refusal().textContent).toContain('changed somewhere else');
    expect(idsIn('waiting')).toEqual([]);
  });

  it('reads the week again when the place asked for has gone', async () => {
    await mount(found(), found());
    move.and.resolveTo({ status: 'place_refused' });

    await openMove('Write the headnote');
    choose('#my-week-move-where', 'in-progress');
    choose('#my-week-move-place', 'going');
    inDialog('Move').click();
    await settle();

    expect(move).toHaveBeenCalledWith(SLUG, jasmine.objectContaining({ id: 'thu-1' }), { status: 'InProgress', beforeTaskId: 'going' });
    expect(refusal().textContent).toContain('That place isn’t there any more');
    expect(weekSpy).toHaveBeenCalledTimes(2);
  });

  it('takes a move back with Undo, to the list and place it had', async () => {
    await mount(found());

    await openMove('Write the headnote');
    choose('#my-week-move-where', 'waiting');
    inDialog('Move').click();
    await settle();

    // The read after the move answered with the week as it was; the board then shows it in Waiting again.
    component()['state'].set({ status: 'ready', week: applyPlace(fullWeek(), tasksIn(fullWeek(), 'day-2038-10-14')[0], { list: 'waiting', beforeTaskId: null }) });
    await settle();
    button('Undo').click();
    await settle();

    expect(move.calls.mostRecent().args[2]).toEqual({ status: 'Backlog', scheduledDate: '2038-10-14', beforeTaskId: 'thu-2' });
    expect(outcome().textContent).toContain('Undone');
  });

  it('sends a task to next week’s backlog, off this board', async () => {
    await mount(found());

    await openMove('Write the headnote');
    choose('#my-week-move-where', 'next-week');
    inDialog('Move').click();
    await settle();

    expect(move).toHaveBeenCalledWith(SLUG, jasmine.objectContaining({ id: 'thu-1' }), { status: 'Backlog', planningWeekStart: '2038-10-18' });
    expect(outcome().textContent).toContain('next week’s backlog');
  });

  // ---- Turn 3: reordering ----

  it('reorders within a list through the reorder route, never as a move', async () => {
    await mount(found());

    await openMove('Write the headnote');
    choose('#my-week-move-place', '');
    inDialog('Move').click();
    await settle();

    expect(reorder).toHaveBeenCalledWith(SLUG, jasmine.objectContaining({ id: 'thu-1' }), null);
    expect(move).not.toHaveBeenCalled();
    expect(outcome().textContent).toContain('to its new place in Thu 14 Oct (today)');
  });

  it('asks nothing when the place chosen is the one the task already has', async () => {
    await mount(found());

    await openMove('Write the headnote');
    inDialog('Move').click();
    await settle();

    expect(reorder).not.toHaveBeenCalled();
    expect(move).not.toHaveBeenCalled();
    expect(outcome().textContent).toContain('already there');
  });

  // ---- Turn 4: dragging ----

  it('sends a drop as the same request the Move form sends, placed before the card it landed above', async () => {
    await mount(found());
    const thursday = tasksIn(fullWeek(), 'day-2038-10-14')[0];

    await component()['onDrop']({
      item: { data: thursday },
      container: { data: 'day-2038-10-11' },
      previousContainer: { data: 'day-2038-10-14' },
      currentIndex: 0,
      previousIndex: 0,
    } as never);
    await settle();

    expect(move).toHaveBeenCalledWith(SLUG, thursday, { status: 'Backlog', scheduledDate: '2038-10-11', beforeTaskId: 'mon' });
  });

  it('puts a dropped card back and says so when the server refuses it', async () => {
    // Every other rollback test drives the Move dialog. A drop takes the same path, but nothing proved it:
    // a drag whose request is refused could have left the card in the list it was dropped into, showing a
    // move the server never made.
    await mount(found());
    move.and.resolveTo({ status: 'unavailable', offline: true });
    const thursday = tasksIn(fullWeek(), 'day-2038-10-14')[0];

    await component()['onDrop']({
      item: { data: thursday },
      container: { data: 'waiting' },
      previousContainer: { data: 'day-2038-10-14' },
      currentIndex: 0,
      previousIndex: 0,
    } as never);
    await settle();

    expect(idsIn('waiting')).toEqual(['held']);
    expect(idsIn('day-2038-10-14')).toEqual(['thu-1', 'thu-2']);
    expect(refusal().getAttribute('role')).toBe('alert');
    expect(refusal().textContent).toContain('It’s back where it was.');
  });

  it('reorders a drop within its own list, and ignores one that lands where it started', async () => {
    await mount(found());
    const thursday = tasksIn(fullWeek(), 'day-2038-10-14')[0];
    const same = { item: { data: thursday }, container: { data: 'day-2038-10-14' } };

    await component()['onDrop']({ ...same, previousContainer: same.container, previousIndex: 0, currentIndex: 0 } as never);
    expect(reorder).not.toHaveBeenCalled();

    await component()['onDrop']({ ...same, previousContainer: same.container, previousIndex: 0, currentIndex: 1 } as never);
    await settle();
    expect(reorder).toHaveBeenCalledWith(SLUG, thursday, null);
  });

  it('takes nothing dropped into From earlier weeks or No week yet — not even their own, which Move cannot offer', async () => {
    await mount(found());
    const canDrop = component()['canDrop'] as (drag: unknown, drop: unknown) => boolean;
    const own = { data: 'carried-over' };

    expect(canDrop({ dropContainer: { data: 'backlog' } }, own)).toBeFalse();
    expect(canDrop({ dropContainer: own }, own)).toBeFalse();
    expect(canDrop({ dropContainer: { data: 'backlog' } }, { data: 'backlog' })).toBeTrue();
    expect(canDrop({ dropContainer: { data: 'backlog' } }, { data: 'unplanned' })).toBeFalse();
    expect(canDrop({ dropContainer: { data: 'backlog' } }, { data: 'done' })).toBeTrue();
  });

  it('makes every list a drop target, keeps finished cards from being picked up, and says how to move without a pointer', async () => {
    await mount(found());

    expect(root().querySelectorAll('ul.cdk-drop-list').length).toBe(root().querySelectorAll('ul[data-section]').length);
    expect(root().querySelector('li[data-task-id="done"]')!.classList).toContain('cdk-drag-disabled');
    expect(root().querySelector('li[data-task-id="thu-1"]')!.classList).not.toContain('cdk-drag-disabled');
    expect(root().textContent).toContain('use its Move button');
    expect(root().textContent).toContain('Finished tasks stay in Done until you reopen them');
  });

  it('says a column is busy while a change to one of its tasks is on its way', async () => {
    await mount(found());
    let finish!: (outcome: WorkTaskChangeOutcome) => void;
    complete.and.returnValue(new Promise((resolve) => (finish = resolve)));

    named('Mark “Write the headnote” done').click();
    await settle();

    const busy = [...root().querySelectorAll('cp-board-column')].filter((column) => column.querySelector('[aria-busy="true"]'));
    expect(busy.length).toBeGreaterThan(0);

    finish({ status: 'saved', task: { ...tasksIn(fullWeek(), 'day-2038-10-14')[0], status: 'Done' } });
    await settle();
  });

  // ---- Turn 5: Done and Reopen ----

  it('marks a task done from its card, at the top of Done at once, and offers Undo', async () => {
    await mount(found());

    named('Mark “Write the headnote” done').click();
    await settle();

    expect(complete).toHaveBeenCalledWith(SLUG, jasmine.objectContaining({ id: 'thu-1' }));
    expect(outcome().textContent).toContain('Done: “Write the headnote”.');
    expect(button('Undo')).toBeTruthy();
  });

  it('completes a task dropped on Done', async () => {
    await mount(found());
    const thursday = tasksIn(fullWeek(), 'day-2038-10-14')[0];

    await component()['onDrop']({
      item: { data: thursday },
      container: { data: 'done' },
      previousContainer: { data: 'day-2038-10-14' },
      currentIndex: 0,
      previousIndex: 0,
    } as never);
    await settle();

    expect(complete).toHaveBeenCalledWith(SLUG, thursday);
    expect(move).not.toHaveBeenCalled();
  });

  it('reopens a finished task, reading the week to find where it went back to', async () => {
    const reopened = fullWeek({ completed: { ...fullWeek().completed, tasks: [] }, waiting: { ...fullWeek().waiting, tasks: [...fullWeek().waiting.tasks, { ...fullWeek().completed.tasks[0], status: 'Waiting' }] } });
    await mount(found(), found(reopened));

    named('Reopen “Buy apples”').click();
    await settle();

    expect(reopen).toHaveBeenCalledWith(SLUG, jasmine.objectContaining({ id: 'done' }));
    expect(outcome().textContent).toContain('Reopened “Buy apples” — it’s back in Waiting.');
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Move “Buy apples”');
  });

  // ---- One at a time ----

  it('says it is still saving when a second change is asked for before the first answers', async () => {
    await mount(found());
    let finish!: (outcome: WorkTaskChangeOutcome) => void;
    complete.and.returnValue(new Promise((resolve) => (finish = resolve)));

    named('Mark “Write the headnote” done').click();
    await settle();
    named('Mark “Shoot the galette” done').click();
    await settle();

    expect(complete).toHaveBeenCalledTimes(1);
    expect(outcome().textContent).toContain('Still saving the last change');
    expect(root().querySelector('ul.cdk-drop-list')!.classList).toContain('cdk-drop-list-disabled');

    finish({ status: 'saved', task: { ...tasksIn(fullWeek(), 'day-2038-10-14')[0], status: 'Done' } });
    await settle();
  });

  it('refuses a change while offline and says why', async () => {
    await mount(found());
    online.set(false);
    await settle();

    named('Mark “Write the headnote” done').click();
    await settle();

    expect(complete).not.toHaveBeenCalled();
    expect(refusal().textContent).toContain('You’re offline');
  });
});
