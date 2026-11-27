import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpMenuSelectAction, CpMenuSelectComponent, CpMenuSelectOption } from './menu-select.component';

@Component({
  imports: [CpMenuSelectComponent],
  template: `<cp-menu-select
    label="Switch workspace"
    placeholder="Choose a workspace"
    [options]="options()"
    [value]="value()"
    [actions]="actions()"
    (selected)="picked.push($event)"
    (actionChosen)="acted.push($event)"
  />`,
})
class HostComponent {
  readonly options = signal<readonly CpMenuSelectOption[]>([
    { value: 'cozy-fall', label: 'Cozy Fall' },
    { value: 'weeknight', label: 'Weeknight' },
  ]);
  readonly value = signal<string | null>('cozy-fall');
  readonly actions = signal<readonly CpMenuSelectAction[]>([{ id: 'create', label: '+ Create workspace…' }]);
  readonly picked: string[] = [];
  readonly acted: string[] = [];
}

describe('CpMenuSelectComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let el: HTMLElement;

  const trigger = () => el.querySelector<HTMLButtonElement>('button[aria-haspopup="menu"]')!;
  const menu = () => el.querySelector<HTMLElement>('[role="menu"]');
  const rows = () => Array.from(el.querySelectorAll<HTMLButtonElement>('[role="menu"] button'));

  async function open() {
    trigger().click();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function press(key: string, target: HTMLElement = document.activeElement as HTMLElement) {
    target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    fixture.detectChanges();
  }

  beforeEach(() => {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    // Attached, because focus only moves in a document.
    document.body.appendChild(el);
    fixture.detectChanges();
  });

  afterEach(() => el.remove());

  it('shows the chosen answer on a closed button, named by the label and the answer, with no menu', () => {
    expect(trigger().textContent).toContain('Cozy Fall');
    expect(trigger().getAttribute('aria-label')).toBe('Switch workspace, current: Cozy Fall');
    expect(trigger().getAttribute('aria-expanded')).toBe('false');
    expect(menu()).toBeNull();
    // A native select's open list is painted by the OS and cannot be themed; this control must not use one.
    expect(el.querySelector('select')).toBeNull();
  });

  it('shows the placeholder, and names the button by the label alone, when no option is chosen', () => {
    host.value.set(null);
    fixture.detectChanges();

    expect(trigger().textContent).toContain('Choose a workspace');
    expect(trigger().getAttribute('aria-label')).toBe('Switch workspace');
  });

  it('lists the options with the chosen one checked, then the actions below a divider', async () => {
    await open();

    expect(menu()!.getAttribute('aria-label')).toBe('Switch workspace');
    expect(trigger().getAttribute('aria-expanded')).toBe('true');
    expect(trigger().getAttribute('aria-controls')).toBe(menu()!.id);
    expect(rows().map((r) => r.getAttribute('role'))).toEqual(['menuitemradio', 'menuitemradio', 'menuitem']);
    expect(rows().map((r) => r.getAttribute('aria-checked'))).toEqual(['true', 'false', null]);
    expect(menu()!.querySelector('[role="separator"]')).not.toBeNull();
    // The chosen option says so with a glyph as well as a fill.
    expect(rows()[0].textContent).toContain('✓');
    expect(rows()[1].textContent).not.toContain('✓');
  });

  it('draws no divider when there are no actions', async () => {
    host.actions.set([]);
    fixture.detectChanges();
    await open();

    expect(rows().length).toBe(2);
    expect(menu()!.querySelector('[role="separator"]')).toBeNull();
  });

  it('focuses the chosen option when it opens, so the arrow keys start where the creator is', async () => {
    host.value.set('weeknight');
    fixture.detectChanges();
    await open();

    expect(document.activeElement).toBe(rows()[1]);
  });

  it('emits the picked value, closes, and returns focus to the button', async () => {
    await open();

    rows()[1].click();
    fixture.detectChanges();

    expect(host.picked).toEqual(['weeknight']);
    expect(menu()).toBeNull();
    expect(document.activeElement).toBe(trigger());
  });

  it('keeps showing the old answer until the consumer passes the new one back', async () => {
    await open();
    rows()[1].click();
    fixture.detectChanges();

    expect(trigger().textContent).toContain('Cozy Fall');

    host.value.set('weeknight');
    fixture.detectChanges();

    expect(trigger().textContent).toContain('Weeknight');
  });

  it('does not emit when the chosen option is picked again', async () => {
    await open();

    rows()[0].click();
    fixture.detectChanges();

    expect(host.picked).toEqual([]);
    expect(menu()).toBeNull();
  });

  it('emits an action by its id and never as a value', async () => {
    await open();

    rows()[2].click();
    fixture.detectChanges();

    expect(host.acted).toEqual(['create']);
    expect(host.picked).toEqual([]);
    expect(menu()).toBeNull();
    expect(trigger().textContent).toContain('Cozy Fall');
  });

  describe('keyboard and dismissal', () => {
    it('opens from the button with ArrowDown on the current option, and ArrowUp on the last row', async () => {
      trigger().focus();
      press('ArrowDown');
      await fixture.whenStable();
      expect(document.activeElement).toBe(rows()[0]);

      press('Escape');
      trigger().focus();
      press('ArrowUp');
      await fixture.whenStable();
      expect(document.activeElement).toBe(rows()[2]);
    });

    it('moves between rows with the arrow keys, wrapping, and jumps with Home and End', async () => {
      await open();
      const items = rows();

      press('ArrowDown');
      expect(document.activeElement).toBe(items[1]);
      press('End');
      expect(document.activeElement).toBe(items[2]);
      press('ArrowDown');
      expect(document.activeElement).toBe(items[0]);
      press('ArrowUp');
      expect(document.activeElement).toBe(items[2]);
      press('Home');
      expect(document.activeElement).toBe(items[0]);
    });

    it('closes on Escape and returns focus to the button', async () => {
      await open();

      press('Escape');

      expect(menu()).toBeNull();
      expect(document.activeElement).toBe(trigger());
    });

    it('closes on Tab and leaves focus to move on', async () => {
      await open();

      press('Tab');

      expect(menu()).toBeNull();
    });

    it('closes on a press outside the control', async () => {
      await open();

      document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      fixture.detectChanges();

      expect(menu()).toBeNull();
    });

    it('stays open for a press inside the control', async () => {
      await open();

      menu()!.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      fixture.detectChanges();

      expect(menu()).not.toBeNull();
    });

    it('closes when the button is pressed again', async () => {
      await open();

      trigger().click();
      fixture.detectChanges();

      expect(menu()).toBeNull();
    });
  });
});
