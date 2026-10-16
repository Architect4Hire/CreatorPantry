import { ComponentFixture, TestBed } from '@angular/core/testing';

import { fullDay } from './my-day.fixtures';
import { MyDayTopThreeComponent } from './my-day-top-three.component';
import { topThreeCandidates } from './my-day.logic';

describe('MyDayTopThreeComponent', () => {
  let fixture: ComponentFixture<MyDayTopThreeComponent>;
  let saved: (readonly string[])[];
  let closed: number;

  async function create(chosen: readonly string[] = []): Promise<void> {
    await TestBed.configureTestingModule({ imports: [MyDayTopThreeComponent] }).compileComponents();

    fixture = TestBed.createComponent(MyDayTopThreeComponent);
    fixture.componentRef.setInput('candidates', topThreeCandidates(fullDay({ topThree: [...chosen] })));
    fixture.componentRef.setInput('chosen', chosen);
    fixture.componentRef.setInput('open', true);
    saved = [];
    closed = 0;
    fixture.componentInstance.save.subscribe((ids) => saved.push(ids));
    fixture.componentInstance.closed.subscribe(() => closed++);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const tile = (id: string) => root().querySelector<HTMLInputElement>(`#my-day-top-three-${id}`)!;
  const button = (text: string) => [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.trim() === text)!;
  const order = () => [...root().querySelectorAll('.order li')].map((li) => li.textContent!.trim());

  function pick(id: string): void {
    tile(id).click();
    fixture.detectChanges();
  }

  it('is a named dialog that says choosing changes nothing about a task', async () => {
    await create();

    const dialog = root().querySelector('[role="dialog"]')!;
    expect(dialog.getAttribute('aria-labelledby')).toBe('my-day-top-three-title');
    expect(dialog.textContent).toContain('Choose my top three');
    expect(dialog.textContent).toContain('nothing else about them changes');
  });

  it('keeps the order things were picked in, says it back, and saves exactly that order', async () => {
    await create();

    pick('going');
    pick('late');
    pick('plan-2');
    expect(order()).toEqual(['Draft the newsletter', 'Send the invoice', 'Edit the photos']);

    // Unpicking one closes the gap; the next pick goes last.
    pick('late');
    pick('held');
    expect(order()).toEqual(['Draft the newsletter', 'Edit the photos', 'Hear back from the editor']);

    button('Save').click();
    expect(saved).toEqual([['going', 'plan-2', 'held']]);
  });

  it('disables the rest at three, so the cap shows before it bites', async () => {
    await create();

    pick('going');
    pick('late');
    pick('plan-2');

    expect(tile('plan-1').disabled).toBeTrue();
    expect(tile('going').disabled).toBeFalse();
  });

  it('starts from what is chosen now, and offers to clear it', async () => {
    await create(['plan-2', 'done']);

    expect(order()).toEqual(['Edit the photos', 'Buy apples']);
    expect(tile('plan-2').checked).toBeTrue();

    button('Clear my top three').click();
    expect(saved).toEqual([[]]);
  });

  it('never sends or counts a chosen task that has since left the day', async () => {
    await create(['vanished', 'going']);

    expect(order()).toEqual(['Draft the newsletter']);
    pick('late');
    pick('plan-1');
    expect(tile('plan-2').disabled).toBeTrue();

    button('Save').click();
    expect(saved).toEqual([['going', 'late', 'plan-1']]);
  });

  it('will not save from an empty day, where Save would only clear a real choice', async () => {
    await create();
    fixture.componentRef.setInput('candidates', []);
    fixture.detectChanges();

    button('Save').click();
    expect(saved).toEqual([]);
  });

  it('closes from Cancel and from Escape without saving', async () => {
    await create();

    button('Cancel').click();
    root().querySelector('[role="dialog"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));

    expect(closed).toBe(2);
    expect(saved).toEqual([]);
  });

  it('refuses a second save while one is on its way, and shows why the last one did not land', async () => {
    await create();
    fixture.componentRef.setInput('saving', true);
    fixture.componentRef.setInput('problem', 'That didn’t save. Try again.');
    fixture.detectChanges();

    const save = button('Saving…');
    expect(save.disabled).toBeFalse();
    expect(save.getAttribute('aria-disabled')).toBe('true');
    save.click();

    expect(saved).toEqual([]);
    expect(root().querySelector('[role="alert"]')!.textContent).toContain('That didn’t save.');
  });
});
