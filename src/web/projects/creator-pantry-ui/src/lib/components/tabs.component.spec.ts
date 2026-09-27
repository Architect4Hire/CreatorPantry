import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpTabDefinition, CpTabPanelComponent, CpTabsComponent } from './tabs.component';

@Component({
  standalone: true,
  imports: [CpTabsComponent, CpTabPanelComponent],
  template: `
    <cp-tabs [tabs]="tabs" [ariaLabel]="'Recipe views'" [(selectedId)]="selectedId">
      <cp-tab-panel [id]="'one'">Panel one content</cp-tab-panel>
      <cp-tab-panel [id]="'two'">Panel two content</cp-tab-panel>
      <cp-tab-panel [id]="'three'">Panel three content</cp-tab-panel>
    </cp-tabs>
  `,
})
class HostComponent {
  tabs: CpTabDefinition[] = [
    { id: 'one', label: 'One' },
    { id: 'two', label: 'Two', disabled: true },
    { id: 'three', label: 'Three' },
  ];
  selectedId: string | undefined;
}

describe('CpTabsComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    return fixture;
  }

  function tabButtons(host: HTMLElement): HTMLButtonElement[] {
    return Array.from(host.querySelectorAll('button[role="tab"]'));
  }

  function dispatchKey(target: HTMLElement, key: string) {
    target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
  }

  it('renders one tab button per entry with correct aria-selected and roving tabindex', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const buttons = tabButtons(host);

    expect(buttons.length).toBe(3);
    // 'one' is the first non-disabled tab, so it is selected by default.
    expect(buttons[0].getAttribute('aria-selected')).toBe('true');
    expect(buttons[0].getAttribute('tabindex')).toBe('0');
    expect(buttons[1].getAttribute('aria-selected')).toBe('false');
    expect(buttons[1].getAttribute('tabindex')).toBe('-1');
    expect(buttons[1].disabled).toBe(true);
    expect(buttons[2].getAttribute('aria-selected')).toBe('false');
    expect(buttons[2].getAttribute('tabindex')).toBe('-1');
  });

  it('moves selection and focus to the next non-disabled tab on ArrowRight and wraps from the last tab to the first', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const tablist = host.querySelector('[role="tablist"]') as HTMLElement;

    dispatchKey(tablist, 'ArrowRight');
    fixture.detectChanges();
    let buttons = tabButtons(host);
    // 'two' is disabled and is skipped entirely.
    expect(fixture.componentInstance.selectedId).toBe('three');
    expect(document.activeElement).toBe(buttons[2]);
    expect(buttons[2].getAttribute('aria-selected')).toBe('true');
    expect(buttons[2].getAttribute('tabindex')).toBe('0');

    dispatchKey(tablist, 'ArrowRight');
    fixture.detectChanges();
    buttons = tabButtons(host);
    expect(fixture.componentInstance.selectedId).toBe('one');
    expect(document.activeElement).toBe(buttons[0]);
  });

  it('wraps from the first tab to the last tab on ArrowLeft', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const tablist = host.querySelector('[role="tablist"]') as HTMLElement;

    dispatchKey(tablist, 'ArrowLeft');
    fixture.detectChanges();
    const buttons = tabButtons(host);
    expect(fixture.componentInstance.selectedId).toBe('three');
    expect(document.activeElement).toBe(buttons[2]);
  });

  it('Home and End jump to the first and last non-disabled tabs', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const tablist = host.querySelector('[role="tablist"]') as HTMLElement;

    dispatchKey(tablist, 'End');
    fixture.detectChanges();
    let buttons = tabButtons(host);
    expect(fixture.componentInstance.selectedId).toBe('three');
    expect(document.activeElement).toBe(buttons[2]);

    dispatchKey(tablist, 'Home');
    fixture.detectChanges();
    buttons = tabButtons(host);
    expect(fixture.componentInstance.selectedId).toBe('one');
    expect(document.activeElement).toBe(buttons[0]);
  });

  it('never selects a disabled tab via click or arrow navigation', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const buttons = tabButtons(host);

    buttons[1].click();
    fixture.detectChanges();
    expect(fixture.componentInstance.selectedId).toBe('one');
    expect(buttons[1].getAttribute('aria-selected')).toBe('false');

    const tablist = host.querySelector('[role="tablist"]') as HTMLElement;
    dispatchKey(tablist, 'ArrowRight');
    dispatchKey(tablist, 'ArrowLeft');
    fixture.detectChanges();
    expect(fixture.componentInstance.selectedId).not.toBe('two');
  });

  it("does not render a panel's projected content until its tab is first selected, and keeps it in the DOM (hidden) after switching away", async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const panels = Array.from(host.querySelectorAll('[role="tabpanel"]')) as HTMLElement[];

    expect(panels[0].textContent).toContain('Panel one content');
    expect(panels[0].hasAttribute('hidden')).toBe(false);
    // 'three' has not been selected yet: its content must not exist in the DOM.
    expect(panels[2].textContent).not.toContain('Panel three content');
    expect(panels[2].hasAttribute('hidden')).toBe(true);

    const tablist = host.querySelector('[role="tablist"]') as HTMLElement;
    dispatchKey(tablist, 'End');
    fixture.detectChanges();

    expect(panels[2].textContent).toContain('Panel three content');
    expect(panels[2].hasAttribute('hidden')).toBe(false);

    // Switching back away from panel 'one' keeps its content rendered, only hidden.
    expect(panels[0].hasAttribute('hidden')).toBe(true);
    expect(panels[0].textContent).toContain('Panel one content');
  });

  it('wraps a long tab list onto more rows instead of overflowing or clipping it', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.tabs = Array.from({ length: 10 }, (_, index) => ({
      id: `t${index}`,
      label: `A fairly long tab label ${index}`,
    }));
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const tablist = host.querySelector('[role="tablist"]') as HTMLElement;
    tablist.style.width = '30rem';
    fixture.detectChanges();

    // WCAG 2.2 SC 1.4.10 wants no horizontal scrolling at narrow widths, so the row wraps rather than
    // scrolling sideways. Ten tabs cannot fit on one 30rem row, so they must occupy more than one.
    expect(getComputedStyle(tablist).flexWrap).toBe('wrap');
    expect(tablist.scrollWidth).toBeLessThanOrEqual(tablist.clientWidth + 1);

    const buttons = tabButtons(host);
    const rows = new Set(buttons.map((button) => button.getBoundingClientRect().top));
    expect(rows.size).toBeGreaterThan(1);
  });

  it('gives every tab a touch target at least 40px tall', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    for (const button of tabButtons(fixture.nativeElement as HTMLElement)) {
      expect(button.getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
    }
  });
});

@Component({
  standalone: true,
  imports: [CpTabsComponent, CpTabPanelComponent],
  template: `
    <cp-tabs [tabs]="outerTabs" ariaLabel="Areas" [(selectedId)]="outerId">
      <cp-tab-panel id="shared">
        <cp-tabs [tabs]="innerTabs" ariaLabel="Tools" [(selectedId)]="innerId">
          <cp-tab-panel id="shared">Inner panel content</cp-tab-panel>
          <cp-tab-panel id="inner-other">Inner other content</cp-tab-panel>
        </cp-tabs>
      </cp-tab-panel>
      <cp-tab-panel id="outer-other">Outer other content</cp-tab-panel>
    </cp-tabs>
  `,
})
class NestedHostComponent {
  // Deliberately colliding ids: the outer and inner tablists both own a tab called 'shared'.
  outerTabs: CpTabDefinition[] = [
    { id: 'shared', label: 'Shared' },
    { id: 'outer-other', label: 'Outer other' },
  ];
  innerTabs: CpTabDefinition[] = [
    { id: 'shared', label: 'Shared' },
    { id: 'inner-other', label: 'Inner other' },
  ];
  outerId: string | undefined;
  innerId: string | undefined;
}

describe('CpTabsComponent nested inside a panel', () => {
  it('keyboard navigation focuses the tab of the tablist it happened in, even when ids collide across levels', async () => {
    await TestBed.configureTestingModule({ imports: [NestedHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(NestedHostComponent);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const tablists = Array.from(host.querySelectorAll('[role="tablist"]')) as HTMLElement[];
    expect(tablists.length).toBe(2);

    const [outerTablist, innerTablist] = tablists;
    const innerButtons = Array.from(innerTablist.querySelectorAll('button[role="tab"]')) as HTMLButtonElement[];
    const outerButtons = Array.from(outerTablist.querySelectorAll('button[role="tab"]')) as HTMLButtonElement[];

    // Home in the inner tablist targets the inner 'shared' tab. An unscoped focus lookup would find the
    // OUTER 'shared' button first — it comes earlier in the shared host subtree — and move focus out of
    // the tablist the creator was operating.
    innerTablist.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: false }));
    fixture.detectChanges();

    expect(fixture.componentInstance.innerId).toBe('shared');
    expect(document.activeElement).toBe(innerButtons[0]);
    expect(document.activeElement).not.toBe(outerButtons[0]);
  });

  it('does not let an inner tablist keypress change the outer selection', async () => {
    await TestBed.configureTestingModule({ imports: [NestedHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(NestedHostComponent);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const innerTablist = host.querySelectorAll('[role="tablist"]')[1] as HTMLElement;

    // bubbles: true is the realistic case — a real key event bubbles. The outer handler is bound to the
    // outer tablist, which is a sibling of the projected panels, so it is never on the path.
    innerTablist.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    fixture.detectChanges();

    expect(fixture.componentInstance.innerId).toBe('inner-other');
    expect(fixture.componentInstance.outerId).toBe('shared');
  });
});
