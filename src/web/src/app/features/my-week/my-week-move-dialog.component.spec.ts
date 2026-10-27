import { ComponentFixture, TestBed } from '@angular/core/testing';

import { MyWeekMoveDialogComponent } from './my-week-move-dialog.component';
import { MyWeekPlace, tasksIn } from './my-week.moves';
import { fullWeek } from './my-week.fixtures';

describe('MyWeekMoveDialogComponent', () => {
  let fixture: ComponentFixture<MyWeekMoveDialogComponent>;
  let moved: MyWeekPlace[];
  let closed: number;
  const week = fullWeek();

  async function open(taskId: string): Promise<void> {
    await TestBed.configureTestingModule({ imports: [MyWeekMoveDialogComponent] }).compileComponents();
    fixture = TestBed.createComponent(MyWeekMoveDialogComponent);
    moved = [];
    closed = 0;
    fixture.componentInstance.moved.subscribe((place) => moved.push(place));
    fixture.componentInstance.closed.subscribe(() => closed++);

    const all = [
      week.backlog,
      week.carriedOver,
      week.unplanned,
      week.inProgress,
      week.waiting,
      week.completed,
      ...week.days,
    ].flatMap((g) => g.tasks);
    fixture.componentRef.setInput('week', week);
    fixture.componentRef.setInput(
      'task',
      all.find((each) => each.id === taskId)!,
    );
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const where = () => root().querySelector<HTMLSelectElement>('#my-week-move-where')!;
  const place = () => root().querySelector<HTMLSelectElement>('#my-week-move-place');
  const button = (text: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === text)!;

  function choose(select: HTMLSelectElement, value: string): void {
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('is a dialog named for the task, starting where the task is now', async () => {
    await open('thu-1');

    expect(root().querySelector('[role="dialog"]')!.textContent).toContain('Move “Write the headnote”');
    expect(where().value).toBe('day-2038-10-14');
    expect(place()!.value).toBe('thu-2');
  });

  it('names a place in another list and hands it over without sending anything', async () => {
    await open('thu-1');

    choose(where(), 'in-progress');
    expect(place()!.value).toBe('');
    choose(place()!, 'going');
    button('Move').click();

    expect(moved).toEqual([{ list: 'in-progress', beforeTaskId: 'going' }]);
  });

  it('reorders by choosing the same list and another place', async () => {
    await open('thu-1');

    choose(place()!, '');
    button('Move').click();

    expect(moved).toEqual([{ list: 'day-2038-10-14', beforeTaskId: null }]);
  });

  it('says where Done and next week put a task, since neither has places to choose', async () => {
    await open('thu-1');

    choose(where(), 'done');
    expect(place()).toBeNull();
    expect(root().textContent).toContain('top of Done');

    choose(where(), 'next-week');
    expect(root().textContent).toContain('bottom of next week’s backlog');
  });

  it('starts a task found in From earlier weeks on this week’s backlog, which is where it can be sent', async () => {
    await open('old');

    expect(where().value).toBe('backlog');
    expect(tasksIn(week, 'backlog').length).toBe(1);
  });

  it('says when a list is only partly on the board, so “At the bottom” is understood', async () => {
    await TestBed.configureTestingModule({ imports: [MyWeekMoveDialogComponent] }).compileComponents();
    fixture = TestBed.createComponent(MyWeekMoveDialogComponent);
    const cut = fullWeek({ waiting: { ...fullWeek().waiting, hasMore: true } });
    fixture.componentRef.setInput('week', cut);
    fixture.componentRef.setInput('task', cut.days[3].tasks[0]);
    fixture.detectChanges();

    choose(where(), 'waiting');
    expect(root().textContent).toContain('Only the first tasks in this list are on the board');
  });

  it('keeps what was chosen when the week is read again while the form is open', async () => {
    await open('thu-1');

    choose(where(), 'waiting');
    fixture.componentRef.setInput('week', fullWeek());
    fixture.detectChanges();
    await fixture.whenStable();

    expect(where().value).toBe('waiting');
  });

  it('closes from Cancel and from Escape without moving anything', async () => {
    await open('thu-1');

    button('Cancel').click();
    root()
      .querySelector('[role="dialog"]')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));

    expect(closed).toBe(2);
    expect(moved).toEqual([]);
  });
});
