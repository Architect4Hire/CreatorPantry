import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { WorkTask } from '../../models/work-task.models';
import { RUN } from '../my-day/my-day.fixtures';
import { MyWeekCardComponent } from './my-week-card.component';
import { TODAY, task } from './my-week.fixtures';

describe('MyWeekCardComponent', () => {
  let fixture: ComponentFixture<MyWeekCardComponent>;

  async function create(shown: WorkTask, level: 4 | 5 = 4, canAct = false): Promise<HTMLElement> {
    await TestBed.configureTestingModule({ imports: [MyWeekCardComponent], providers: [provideRouter([])] }).compileComponents();
    fixture = TestBed.createComponent(MyWeekCardComponent);
    fixture.componentRef.setInput('task', shown);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('today', TODAY);
    fixture.componentRef.setInput('headingLevel', level);
    fixture.componentRef.setInput('canAct', canAct);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('is an article headed by its title, one level below the list that holds it', async () => {
    const root = await create(task(), 5);

    expect(root.querySelector('cp-card article')).not.toBeNull();
    expect(root.querySelector('h5')!.textContent!.trim()).toBe('Shoot the galette');
    expect(root.querySelector('h4')).toBeNull();
  });

  it('makes the title the deep link to where the work is, and says what it opens', async () => {
    const root = await create(task({ links: { ...task().links, workflowRunId: RUN, workflowRunRoute: `workflows/runs/${RUN}` } }));

    expect(root.querySelector('h4 a')!.getAttribute('href')).toBe(`/sams-kitchen/workflows/runs/${RUN}`);
    expect(root.textContent).toContain('the workflow');
  });

  it('leaves a task about nothing in particular as plain text, with no empty facts list', async () => {
    const root = await create(task());

    expect(root.querySelector('h4 a')).toBeNull();
    expect(root.querySelector('dl')).toBeNull();
  });

  it('says priority, estimate, labels and a late due date in words, never by colour alone', async () => {
    const root = await create(
      task({ priority: 'High', estimateMinutes: 90, dueDate: '2038-10-12', labels: [{ id: 'tag-1', name: 'Weeknight' }] }),
    );

    expect(root.textContent).toContain('High');
    expect(root.textContent).toContain('1 hr 30 min');
    expect(root.textContent).toContain('Tue 12 Oct, overdue');
    expect(root.textContent).toContain('Weeknight');
  });

  // ---- Controls (13A.33, turns 2–5) ----

  it('offers Done and Move, each named for its task, and emits the Move button so focus can return to it', async () => {
    const root = await create(task(), 4, true);
    const events: string[] = [];
    let mover = null as HTMLElement | null;
    fixture.componentInstance.done.subscribe(() => events.push('done'));
    fixture.componentInstance.moveRequested.subscribe((button) => (mover = button));

    const done = root.querySelector<HTMLButtonElement>('[aria-label="Mark “Shoot the galette” done"]')!;
    const move = root.querySelector<HTMLButtonElement>('[aria-label="Move “Shoot the galette”"]')!;
    expect(move.getAttribute('aria-haspopup')).toBe('dialog');

    done.click();
    move.click();
    expect(events).toEqual(['done']);
    expect(mover).toBe(move);
  });

  it('offers a finished task only Reopen', async () => {
    const root = await create(task({ status: 'Done', completedAt: '2038-10-13T10:00:00Z' }), 4, true);

    expect([...root.querySelectorAll('button')].map((b) => b.getAttribute('aria-label'))).toEqual(['Reopen “Shoot the galette”']);
  });

  it('keeps its controls focusable but says they are unavailable while a change is on its way', async () => {
    const root = await create(task(), 4, true);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    const move = root.querySelector<HTMLButtonElement>('[data-action="move"]')!;
    expect(move.disabled).toBeFalse();
    expect(move.getAttribute('aria-disabled')).toBe('true');
    expect(root.querySelector('article')!.getAttribute('aria-busy')).toBe('true');
    expect(root.textContent).toContain('Saving its new place');
  });

  it('shows a Viewer no controls', async () => {
    const root = await create(task());

    expect(root.querySelectorAll('button').length).toBe(0);
  });

  it('gives its labels as one fact, comma-separated', async () => {
    const root = await create(task({ labels: [{ id: 'a', name: 'Weeknight' }, { id: 'b', name: 'Autumn' }] }));

    const facts = [...root.querySelectorAll('dl > div')].filter((fact) => fact.textContent!.includes('Weeknight'));
    expect(facts.length).toBe(1);
    expect(facts[0].textContent).toContain('Weeknight, Autumn');
  });

  it('says nothing about Normal priority, an unknown estimate, or the due date of a finished task', async () => {
    const root = await create(task({ status: 'Done', dueDate: '2038-10-12', completedAt: '2038-10-13T10:00:00Z' }));

    expect(root.textContent).not.toContain('Normal');
    expect(root.textContent).not.toContain('overdue');
    expect(root.textContent).not.toContain('min');
  });
});
