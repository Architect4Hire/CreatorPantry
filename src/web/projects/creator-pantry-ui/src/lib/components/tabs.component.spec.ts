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
});
