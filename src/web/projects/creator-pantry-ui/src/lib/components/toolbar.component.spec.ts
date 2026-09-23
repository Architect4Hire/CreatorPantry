import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpToolbarComponent } from './toolbar.component';

@Component({
  standalone: true,
  imports: [CpToolbarComponent],
  template: `
    <cp-toolbar [ariaLabel]="ariaLabel" [loading]="loading" [disabled]="disabled">
      <input id="search-input" cpToolbarSearch type="search" placeholder="Search recipes" />
      <select id="filter-select" cpToolbarFilters><option>All</option></select>
      <button id="action-one" cpToolbarActions type="button">Add recipe</button>
      <button id="action-two" cpToolbarActions type="button">Duplicate</button>
      <button id="overflow-action" cpToolbarOverflow type="button">Archive</button>
    </cp-toolbar>
  `,
})
class HostComponent {
  ariaLabel = 'Recipe list toolbar';
  loading = false;
  disabled = false;
}

describe('CpToolbarComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    return fixture;
  }

  it('renders role="toolbar" with the given aria-label and horizontal orientation', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const toolbar = host.querySelector('[role="toolbar"]') as HTMLElement;

    expect(toolbar).toBeTruthy();
    expect(toolbar.getAttribute('aria-label')).toBe('Recipe list toolbar');
    expect(toolbar.getAttribute('aria-orientation')).toBe('horizontal');
  });

  it('renders a role="status" aria-live="polite" element when loading', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.loading = true;
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const status = host.querySelector('[role="status"]') as HTMLElement;

    expect(status).toBeTruthy();
    expect(status.getAttribute('aria-live')).toBe('polite');
    expect(status.textContent).toContain('Loading');
  });

  it('does not render a status element when not loading', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[role="status"]')).toBeNull();
  });

  it('sets aria-disabled="true" and disables pointer interaction when disabled', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.disabled = true;
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const toolbar = host.querySelector('[role="toolbar"]') as HTMLElement;

    expect(toolbar.getAttribute('aria-disabled')).toBe('true');
    expect(getComputedStyle(toolbar).pointerEvents).toBe('none');
  });

  it('does not set aria-disabled when not disabled', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const toolbar = host.querySelector('[role="toolbar"]') as HTMLElement;

    expect(toolbar.hasAttribute('aria-disabled')).toBe(false);
  });

  it('projects search, filters, and actions content into separate groups in order', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const groups = Array.from(host.querySelectorAll('.group'));
    const searchInput = host.querySelector('#search-input') as HTMLElement;
    const filterSelect = host.querySelector('#filter-select') as HTMLElement;
    const actionOne = host.querySelector('#action-one') as HTMLElement;
    const actionTwo = host.querySelector('#action-two') as HTMLElement;

    expect(groups.length).toBe(3);
    expect(groups[0].contains(searchInput)).toBe(true);
    expect(groups[1].contains(filterSelect)).toBe(true);
    expect(groups[2].contains(actionOne)).toBe(true);
    expect(groups[2].contains(actionTwo)).toBe(true);
  });

  it('renders the overflow slot content inside a <details> disclosure with a "More actions" summary', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const details = host.querySelector('details') as HTMLDetailsElement;
    const summary = details?.querySelector('summary');
    const overflowAction = host.querySelector('#overflow-action') as HTMLElement;

    expect(details).toBeTruthy();
    expect(summary?.textContent).toContain('More actions');
    expect(details.contains(overflowAction)).toBe(true);
    // the overflow slot is not wrapped in a `.group` element like the other three slots.
    expect(overflowAction.closest('.group')).toBeNull();
  });

  it('moves focus to the next focusable descendant on ArrowRight, among real projected buttons', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const actionOne = host.querySelector('#action-one') as HTMLButtonElement;
    const actionTwo = host.querySelector('#action-two') as HTMLButtonElement;

    actionOne.focus();
    expect(document.activeElement).toBe(actionOne);

    actionOne.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();

    expect(document.activeElement).toBe(actionTwo);
  });

  it('moves focus to the previous focusable descendant on ArrowLeft, wrapping from the first to the last', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const searchInput = host.querySelector('#search-input') as HTMLInputElement;
    const summary = host.querySelector('summary') as HTMLElement;

    searchInput.focus();
    expect(document.activeElement).toBe(searchInput);

    searchInput.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    fixture.detectChanges();

    // the search input is the first focusable descendant, so ArrowLeft wraps to the last: the overflow summary.
    expect(document.activeElement).toBe(summary);
  });

  it('Home and End jump to the first and last focusable descendants', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const searchInput = host.querySelector('#search-input') as HTMLInputElement;
    const actionOne = host.querySelector('#action-one') as HTMLButtonElement;
    const summary = host.querySelector('summary') as HTMLElement;

    actionOne.focus();
    actionOne.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    fixture.detectChanges();
    expect(document.activeElement).toBe(summary);

    summary.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
    fixture.detectChanges();
    expect(document.activeElement).toBe(searchInput);
  });
});
