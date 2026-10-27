import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { RUN, TODAY, task } from './my-day.fixtures';
import { MyDayTaskComponent } from './my-day-task.component';
import { MyDayEntry, MyDayMove } from './my-day.logic';

/** The row as the page uses it: an `li` inside a list. */
@Component({
  standalone: true,
  imports: [MyDayTaskComponent],
  template: `<ul>
    <li
      cpMyDayTask
      [entry]="entry()"
      workspaceSlug="sams-kitchen"
      [today]="today"
      [showFrom]="showFrom()"
      [canAct]="canAct()"
      [busy]="busy()"
      [offline]="offline()"
      [moveOpen]="moveOpen()"
      (done)="events.push('done')"
      (reopen)="events.push('reopen')"
      (start)="events.push('start')"
      (move)="moved.push($event)"
      (moveToggled)="moveOpen.set(!moveOpen())"
    ></li>
  </ul>`,
})
class HostComponent {
  readonly today = TODAY;
  readonly entry = signal<MyDayEntry>({ task: task(), group: 'scheduledToday' });
  readonly showFrom = signal(false);
  readonly canAct = signal(true);
  readonly busy = signal(false);
  readonly offline = signal(false);
  readonly moveOpen = signal(false);
  readonly events: string[] = [];
  readonly moved: MyDayMove[] = [];
}

describe('MyDayTaskComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideRouter([])],
    }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  const root = () => fixture.nativeElement as HTMLElement;
  const buttons = () => [...root().querySelectorAll<HTMLElement>('button, a[cpButton]')];
  const named = (name: string) => buttons().find((button) => button.getAttribute('aria-label') === name);

  function show(entry: MyDayEntry): void {
    host.entry.set(entry);
    fixture.detectChanges();
  }

  it('is a list item named by its task, with every control visible and naming the task it is for', () => {
    const row = root().querySelector('li')!;
    expect(row.getAttribute('data-task-id')).toBe('t-1');
    expect(row.textContent).toContain('Shoot the galette');

    expect(named('Mark “Shoot the galette” done')).toBeTruthy();
    expect(named('Start “Shoot the galette”')).toBeTruthy();
    expect(named('Move “Shoot the galette”')!.getAttribute('aria-expanded')).toBe('false');
    // It controls nothing while there is nothing to control.
    expect(named('Move “Shoot the galette”')!.hasAttribute('aria-controls')).toBeFalse();

    // Each name begins with or holds the words on the button, so speech input finds it by what it says.
    for (const button of buttons()) {
      const visible = button.textContent!.replace(/[✓▸▾]/g, '').trim().toLowerCase();
      expect(button.getAttribute('aria-label')!.toLowerCase()).toContain(visible);
    }
  });

  it('emits what was pressed and decides nothing itself', () => {
    named('Mark “Shoot the galette” done')!.click();
    named('Start “Shoot the galette”')!.click();

    expect(host.events).toEqual(['done', 'start']);
  });

  it('opens its moves inline under the row, names the group, and closes on Escape back onto Move', () => {
    const move = named('Move “Shoot the galette”')!;
    move.click();
    fixture.detectChanges();

    expect(move.getAttribute('aria-expanded')).toBe('true');
    expect(move.hasAttribute('aria-controls')).toBeTrue();
    const panel = root().querySelector<HTMLElement>(`#${move.getAttribute('aria-controls')}`)!;
    expect(panel.getAttribute('role')).toBe('group');
    expect(panel.getAttribute('aria-label')).toBe('Move “Shoot the galette” to');
    expect([...panel.querySelectorAll('button')].map((button) => button.textContent!.trim())).toEqual([
      'In progress',
      'Waiting',
      'Tomorrow',
      'Later this week',
    ]);

    // Each choice names the task and where it goes, so a list of buttons does not read "Tomorrow" ten times.
    expect(panel.querySelector('[data-move="tomorrow"]')!.getAttribute('aria-label')).toBe(
      'Move “Shoot the galette” to Tomorrow',
    );
    expect(move.getAttribute('aria-controls')).toBe(panel.id);

    panel.querySelector<HTMLButtonElement>('[data-move="tomorrow"]')!.click();
    expect(host.moved[0].request).toEqual({ status: 'Backlog', scheduledDate: '2038-10-15' });

    panel
      .querySelector('button')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    expect(root().querySelector('[role="group"]')).toBeNull();
    expect(document.activeElement).toBe(named('Move “Shoot the galette”')!);
  });

  it('resumes a started task where its work is, by a link, and offers no Start', () => {
    show({
      task: task({
        status: 'InProgress',
        links: { ...task().links, workflowRunRoute: `workflows/runs/${RUN}`, workflowRunId: RUN },
      }),
      group: 'inProgress',
    });

    const resume = named('Resume “Shoot the galette” in the workflow')!;
    expect(resume.tagName).toBe('A');
    expect(resume.getAttribute('href')).toBe(`/sams-kitchen/workflows/runs/${RUN}`);
    expect(named('Start “Shoot the galette”')).toBeUndefined();

    // The title is the same deep link, and the row says what it opens.
    expect(root().querySelector<HTMLAnchorElement>('.task-title a')!.getAttribute('href')).toBe(
      `/sams-kitchen/workflows/runs/${RUN}`,
    );
    expect(root().textContent).toContain('the workflow');
  });

  it('offers a finished task only Reopen', () => {
    show({ task: task({ status: 'Done', completedAt: '2038-10-14T14:00:00Z' }), group: 'completedToday' });

    expect(buttons().map((button) => button.getAttribute('aria-label'))).toEqual(['Reopen “Shoot the galette”']);
  });

  it('says which group it came from only at the top of the page, and why an overdue one is there', () => {
    show({ task: task({ dueDate: '2038-10-12', scheduledDate: null }), group: 'overdue' });
    expect(root().textContent).not.toContain('Overdue since');

    host.showFrom.set(true);
    fixture.detectChanges();
    expect(root().textContent).toContain('Overdue since Tue 12 Oct');
  });

  it('says priority only when it is not Normal, and an estimate only when there is one', () => {
    expect(root().textContent).not.toContain('Priority');
    expect(root().textContent).not.toContain('Estimate');

    show({ task: task({ priority: 'High', estimateMinutes: 45 }), group: 'scheduledToday' });
    expect(root().textContent).toContain('High');
    expect(root().textContent).toContain('45 min');
  });

  it('keeps its controls focusable but marked unavailable while busy or offline', () => {
    host.busy.set(true);
    fixture.detectChanges();

    const done = named('Mark “Shoot the galette” done') as HTMLButtonElement;
    expect(done.disabled).toBeFalse();
    expect(done.getAttribute('aria-disabled')).toBe('true');

    host.busy.set(false);
    host.offline.set(true);
    fixture.detectChanges();
    expect(done.getAttribute('aria-disabled')).toBe('true');
  });

  it('shows a Viewer the task and no controls', () => {
    host.canAct.set(false);
    fixture.detectChanges();

    expect(root().textContent).toContain('Shoot the galette');
    expect(buttons()).toEqual([]);
  });
});
