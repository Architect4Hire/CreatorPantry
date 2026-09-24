import { Component, signal } from '@angular/core';
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

@Component({
  standalone: true,
  imports: [CpToolbarComponent],
  template: `
    <cp-toolbar ariaLabel="Recipe filters">
      <input id="toggle-one" cpToolbarFilters type="checkbox" />
      <button id="toggle-two" cpToolbarFilters type="button">Ready</button>
    </cp-toolbar>
  `,
})
class ToggleHostComponent {}

@Component({
  standalone: true,
  imports: [CpToolbarComponent],
  template: `
    <cp-toolbar ariaLabel="Recipe filters">
      <input id="bare-search" cpToolbarSearch type="search" />
      <button id="bare-action" cpToolbarActions type="button">New recipe</button>
      @if (showOverflow()) {
        <button id="late-overflow" cpToolbarOverflow type="button">Export CSV</button>
      }
    </cp-toolbar>
  `,
})
class OverflowHostComponent {
  readonly showOverflow = signal(false);
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

  it('shows the overflow disclosure when the slot has content', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const details = (fixture.nativeElement as HTMLElement).querySelector('details') as HTMLDetailsElement;

    expect(getComputedStyle(details).display).not.toBe('none');
  });

  describe('with an empty overflow slot', () => {
    async function createOverflowFixture() {
      await TestBed.configureTestingModule({ imports: [OverflowHostComponent] }).compileComponents();
      const fixture = TestBed.createComponent(OverflowHostComponent);
      fixture.detectChanges();
      return fixture;
    }

    it('hides the "More actions" disclosure so it cannot open onto nothing', async () => {
      const fixture = await createOverflowFixture();

      const details = (fixture.nativeElement as HTMLElement).querySelector('details') as HTMLDetailsElement;

      expect(details).toBeTruthy();
      expect(getComputedStyle(details).display).toBe('none');
    });

    it('leaves the hidden disclosure out of roving navigation', async () => {
      const fixture = await createOverflowFixture();

      const host = fixture.nativeElement as HTMLElement;
      const action = host.querySelector('#bare-action') as HTMLButtonElement;
      const search = host.querySelector('#bare-search') as HTMLInputElement;

      action.focus();
      action.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
      fixture.detectChanges();

      // With no overflow actions the last focusable control is the action itself, not a summary that
      // discloses an empty popover.
      expect(document.activeElement).toBe(action);

      action.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      fixture.detectChanges();

      expect(document.activeElement).toBe(search);
    });

    it('reveals the disclosure when overflow content appears later', async () => {
      const fixture = await createOverflowFixture();

      // A signal drives the condition because the projected block is checked inside the OnPush toolbar:
      // mutating a plain field on the consumer never marks that view dirty, so the button would not appear.
      fixture.componentInstance.showOverflow.set(true);
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      const details = host.querySelector('details') as HTMLDetailsElement;

      expect(getComputedStyle(details).display).not.toBe('none');
      expect(details.contains(host.querySelector('#late-overflow'))).toBe(true);
    });
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

  it('moves focus to the previous focusable descendant on ArrowLeft', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const actionOne = host.querySelector('#action-one') as HTMLButtonElement;
    const actionTwo = host.querySelector('#action-two') as HTMLButtonElement;

    actionTwo.focus();
    actionTwo.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    fixture.detectChanges();

    expect(document.activeElement).toBe(actionOne);
  });

  it('wraps from the last focusable descendant to the first on ArrowRight', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const searchInput = host.querySelector('#search-input') as HTMLInputElement;
    const summary = host.querySelector('summary') as HTMLElement;

    summary.focus();
    summary.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();

    // The summary is the last focusable descendant, so ArrowRight wraps to the first: the search input.
    expect(document.activeElement).toBe(searchInput);
  });

  // A toolbar navigates its controls with arrows, but a text field uses the same keys to move the caret. This
  // component advertises a search slot, so stealing them would make that slot impossible to type in.
  for (const key of ['ArrowLeft', 'ArrowRight', 'Home', 'End']) {
    it(`leaves ${key} to a focused text input rather than roving away from it`, async () => {
      const fixture = await createFixture();
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      const searchInput = host.querySelector('#search-input') as HTMLInputElement;

      searchInput.focus();
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      searchInput.dispatchEvent(event);
      fixture.detectChanges();

      expect(document.activeElement).toBe(searchInput);

      // Not merely "focus stayed put": the browser must still receive the key, or the caret never moves.
      expect(event.defaultPrevented).toBeFalse();
    });
  }

  it('leaves arrow keys to a focused select, whose value they change', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const select = host.querySelector('#filter-select') as HTMLSelectElement;

    select.focus();
    const event = new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
    select.dispatchEvent(event);
    fixture.detectChanges();

    expect(document.activeElement).toBe(select);
    expect(event.defaultPrevented).toBeFalse();
  });

  it('still roves from a checkbox, which does not own the arrow keys', async () => {
    await TestBed.configureTestingModule({ imports: [ToggleHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ToggleHostComponent);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const checkbox = host.querySelector('#toggle-one') as HTMLInputElement;
    const button = host.querySelector('#toggle-two') as HTMLButtonElement;

    checkbox.focus();
    checkbox.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();

    // The exclusion is about moving a caret, not about being an <input>. A checkbox has no caret to move.
    expect(document.activeElement).toBe(button);
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
