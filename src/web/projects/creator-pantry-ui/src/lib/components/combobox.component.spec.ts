import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpComboboxComponent, CpComboboxOption, cpComboboxResultsLabel } from './combobox.component';

const OPTIONS: CpComboboxOption[] = [
  { id: 'g', label: 'Grams', detail: 'g · mass' },
  { id: 'kg', label: 'Kilograms', detail: 'kg · mass' },
  { id: 'cup', label: 'Cups', detail: 'c · volume' },
  { id: 'tbsp', label: 'Tablespoons', detail: 'tbsp · volume' },
  { id: 'retired', label: 'Gills', detail: 'retired', disabled: true },
];

describe('CpComboboxComponent', () => {
  async function createFixture(): Promise<ComponentFixture<CpComboboxComponent>> {
    await TestBed.configureTestingModule({ imports: [CpComboboxComponent] }).compileComponents();
    const fixture = TestBed.createComponent(CpComboboxComponent);
    fixture.componentRef.setInput('options', OPTIONS);
    fixture.componentRef.setInput('inputId', 'unit');
    fixture.detectChanges();
    return fixture;
  }

  const host = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
  const input = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector<HTMLInputElement>('input')!;
  const listbox = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector<HTMLElement>('[role="listbox"]')!;
  const optionRows = (fixture: ComponentFixture<unknown>) =>
    Array.from(host(fixture).querySelectorAll<HTMLElement>('[role="option"]'));
  const labels = (fixture: ComponentFixture<unknown>) =>
    optionRows(fixture).map((row) => row.querySelector('.label')?.textContent?.trim());
  const status = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector<HTMLElement>('[role="status"]')!;

  const type = (fixture: ComponentFixture<unknown>, text: string) => {
    const field = input(fixture);
    field.value = text;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  const press = (fixture: ComponentFixture<unknown>, key: string) => {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    input(fixture).dispatchEvent(event);
    fixture.detectChanges();
    return event;
  };

  const activeLabel = (fixture: ComponentFixture<unknown>) => {
    const activeId = input(fixture).getAttribute('aria-activedescendant');
    if (activeId === null) return null;
    return host(fixture).querySelector(`[id="${activeId}"]`)?.querySelector('.label')?.textContent?.trim() ?? null;
  };

  // ---- Naming and wiring -------------------------------------------------------------------------

  it('wires the ARIA combobox pattern to the input, the listbox and the active option', async () => {
    const fixture = await createFixture();

    expect(input(fixture).getAttribute('role')).toBe('combobox');
    expect(input(fixture).id).toBe('unit');
    expect(input(fixture).getAttribute('aria-autocomplete')).toBe('list');
    expect(input(fixture).getAttribute('autocomplete')).toBe('off');
    expect(input(fixture).getAttribute('aria-expanded')).toBe('false');
    // Controlled by id even while closed, so aria-controls never points at an element that has gone away.
    expect(input(fixture).getAttribute('aria-controls')).toBe('unit-listbox');
    expect(listbox(fixture).id).toBe('unit-listbox');
    expect(listbox(fixture).hasAttribute('hidden')).toBeTrue();
    expect(input(fixture).getAttribute('aria-activedescendant')).toBeNull();

    press(fixture, 'ArrowDown');

    expect(input(fixture).getAttribute('aria-expanded')).toBe('true');
    expect(listbox(fixture).hasAttribute('hidden')).toBeFalse();
    expect(input(fixture).getAttribute('aria-activedescendant')).toBe('unit-option-g');
    expect(optionRows(fixture)[0].id).toBe('unit-option-g');
    expect(optionRows(fixture)[4].getAttribute('aria-disabled')).toBe('true');
  });

  it('keeps focus on the input while an option is active', async () => {
    const fixture = await createFixture();
    input(fixture).focus();

    press(fixture, 'ArrowDown');
    press(fixture, 'ArrowDown');

    // aria-activedescendant exists precisely so focus does not move: the creator is still typing.
    expect(document.activeElement).toBe(input(fixture));
    expect(activeLabel(fixture)).toBe('Kilograms');
  });

  it('marks the chosen option with aria-selected and a glyph', async () => {
    const fixture = await createFixture();
    press(fixture, 'ArrowDown');
    press(fixture, 'Enter');
    press(fixture, 'ArrowDown');

    const chosen = optionRows(fixture).find((row) => row.getAttribute('aria-selected') === 'true')!;
    expect(chosen.querySelector('.label')?.textContent?.trim()).toBe('Grams');
    expect(chosen.querySelector('.chosen')?.textContent?.trim()).toBe('✓');
  });

  // ---- Keyboard ----------------------------------------------------------------------------------

  it('opens downward from the first option and upward from the last', async () => {
    const fixture = await createFixture();

    press(fixture, 'ArrowDown');
    expect(activeLabel(fixture)).toBe('Grams');

    press(fixture, 'Escape');
    press(fixture, 'ArrowUp');
    // Tablespoons, not Gills: the disabled row is not a place a key may land.
    expect(activeLabel(fixture)).toBe('Tablespoons');
  });

  it('moves with the arrows, stops at each end, and skips a disabled option', async () => {
    const fixture = await createFixture();
    press(fixture, 'ArrowDown');

    press(fixture, 'ArrowUp');
    expect(activeLabel(fixture)).withContext('stops at the first').toBe('Grams');

    for (const expected of ['Kilograms', 'Cups', 'Tablespoons', 'Tablespoons']) {
      press(fixture, 'ArrowDown');
      expect(activeLabel(fixture)).withContext(`after ArrowDown → ${expected}`).toBe(expected);
    }
  });

  it('jumps to the ends with Home and End', async () => {
    const fixture = await createFixture();
    press(fixture, 'ArrowDown');
    press(fixture, 'ArrowDown');

    press(fixture, 'End');
    expect(activeLabel(fixture)).toBe('Tablespoons');

    press(fixture, 'Home');
    expect(activeLabel(fixture)).toBe('Grams');
  });

  it('selects the active option with Enter, and does nothing when none is active', async () => {
    const fixture = await createFixture();

    type(fixture, 'cu');
    press(fixture, 'Home');
    const chosen = press(fixture, 'Enter');

    expect(chosen.defaultPrevented).toBeTrue();
    expect(fixture.componentInstance.selected()?.id).toBe('cup');
    expect(fixture.componentInstance.value()).toBe('Cups');
    expect(input(fixture).getAttribute('aria-expanded')).toBe('false');

    // Nothing active: Enter is not a commit-anything key, and must not swallow a form submit.
    type(fixture, 'xyz');
    const empty = press(fixture, 'Enter');
    expect(empty.defaultPrevented).toBeFalse();
    expect(fixture.componentInstance.selected()?.id).toBe('cup');
  });

  it('closes on Escape without touching the text, and closes on Tab without trapping focus', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('mode', 'free-text');

    type(fixture, 'cup');
    expect(input(fixture).getAttribute('aria-expanded')).toBe('true');

    const escape = press(fixture, 'Escape');
    expect(escape.defaultPrevented).toBeTrue();
    expect(input(fixture).getAttribute('aria-expanded')).toBe('false');
    expect(fixture.componentInstance.value()).withContext('text survives Escape').toBe('cup');

    type(fixture, 'cup');
    const tab = press(fixture, 'Tab');
    expect(tab.defaultPrevented).withContext('Tab must move focus').toBeFalse();
    expect(input(fixture).getAttribute('aria-expanded')).toBe('false');
  });

  // ---- Filtering ---------------------------------------------------------------------------------

  it('filters as the creator types, on the label, and reactivates the first match', async () => {
    const fixture = await createFixture();

    type(fixture, 'gram');
    expect(labels(fixture)).toEqual(['Grams', 'Kilograms']);
    expect(activeLabel(fixture)).toBe('Grams');

    // Matching is case-insensitive, and the detail line is shown rather than searched.
    type(fixture, 'VOLUME');
    expect(labels(fixture)).toEqual([]);
  });

  it('reopens the whole list after a selection, so a creator can change their mind', async () => {
    const fixture = await createFixture();

    type(fixture, 'cup');
    press(fixture, 'Enter');
    expect(fixture.componentInstance.value()).toBe('Cups');

    press(fixture, 'ArrowDown');

    // Text that came from a selection is not a filter. Filtering by it would leave one row, and a keyboard-only
    // creator would have to guess that clearing the box is the way back to the rest of the list.
    expect(labels(fixture)).toEqual(['Grams', 'Kilograms', 'Cups', 'Tablespoons', 'Gills']);

    // Typing over it filters again, as any other text does.
    type(fixture, 'Cups and');
    expect(labels(fixture)).toEqual([]);
  });

  it('leaves an already-filtered list alone when the consumer filters elsewhere', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('filterLocally', false);

    type(fixture, 'zzz');

    // The seam a server-side filter needs: the list is the consumer's answer, not something to filter twice.
    expect(labels(fixture)).toEqual(['Grams', 'Kilograms', 'Cups', 'Tablespoons', 'Gills']);
  });

  it('shows an empty row when nothing matches, and the projected content when given', async () => {
    const fixture = await createFixture();
    type(fixture, 'zzz');

    expect(optionRows(fixture)).toEqual([]);
    expect(host(fixture).querySelector('.empty')?.textContent?.trim()).toBe('No matches');

    @Component({
      standalone: true,
      imports: [CpComboboxComponent],
      template: `<cp-combobox inputId="unit" [options]="options" [(value)]="text">
        <span cpComboboxEmpty>Nothing here yet — add a unit first.</span>
      </cp-combobox>`,
    })
    class HostComponent {
      readonly options = OPTIONS;
      readonly text = signal('');
    }

    const projected = TestBed.createComponent(HostComponent);
    projected.detectChanges();
    type(projected, 'zzz');

    expect(host(projected).querySelector('.empty')?.textContent?.trim()).toBe('Nothing here yet — add a unit first.');
  });

  // ---- Announcement ------------------------------------------------------------------------------

  it('announces how many results the typing left, and says nothing while closed', async () => {
    const fixture = await createFixture();

    expect(status(fixture).getAttribute('aria-live')).toBe('polite');
    expect(status(fixture).textContent?.trim()).withContext('silent while closed').toBe('');

    type(fixture, 'gram');
    expect(status(fixture).textContent?.trim()).toBe('2 results');

    type(fixture, 'cup');
    expect(status(fixture).textContent?.trim()).toBe('1 result');

    type(fixture, 'zzz');
    expect(status(fixture).textContent?.trim()).toBe('No results');

    press(fixture, 'Escape');
    expect(status(fixture).textContent?.trim()).toBe('');
  });

  it('lets an app supply its own wording for the count', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('resultsLabel', (count: number) => `${count} Einheiten`);

    type(fixture, 'gram');

    expect(status(fixture).textContent?.trim()).toBe('2 Einheiten');
    expect(cpComboboxResultsLabel(1)).toBe('1 result');
    expect(cpComboboxResultsLabel(0)).toBe('No results');
  });

  // ---- Modes -------------------------------------------------------------------------------------

  it('restricted: reverts text that matches nothing to the selection on blur', async () => {
    const fixture = await createFixture();

    type(fixture, 'cup');
    press(fixture, 'Enter');
    expect(fixture.componentInstance.value()).toBe('Cups');

    type(fixture, 'not a unit');
    input(fixture).dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    // The box may only read as the option it stands for.
    expect(fixture.componentInstance.value()).toBe('Cups');
    expect(fixture.componentInstance.selected()?.id).toBe('cup');
  });

  it('restricted: clears text typed before anything was ever selected', async () => {
    const fixture = await createFixture();

    type(fixture, 'not a unit');
    input(fixture).dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(fixture.componentInstance.value()).toBe('');
    expect(fixture.componentInstance.selected()).toBeNull();
  });

  it('free-text: keeps what was typed, and says whether it names a known option', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('mode', 'free-text');

    type(fixture, 'smidgen');
    input(fixture).dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(fixture.componentInstance.value()).toBe('smidgen');
    expect(fixture.componentInstance.selected()).withContext('nothing known was named').toBeNull();

    // An exact match — case and surrounding space aside — is a known thing, without anyone opening the list.
    type(fixture, '  cups ');
    input(fixture).dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(fixture.componentInstance.value()).toBe('  cups ');
    expect(fixture.componentInstance.selected()?.id).toBe('cup');
  });

  // ---- Pointer, disabled, layout -----------------------------------------------------------------

  it('selects on mousedown so the pointer cannot lose the input first, and refuses a disabled row', async () => {
    const fixture = await createFixture();
    press(fixture, 'ArrowDown');

    const event = new MouseEvent('mousedown', { bubbles: true, cancelable: true });
    optionRows(fixture)[2].dispatchEvent(event);
    fixture.detectChanges();

    // Prevented, because the default would blur the input and the blur handler would rewrite the text under
    // the pointer before the click landed.
    expect(event.defaultPrevented).toBeTrue();
    expect(fixture.componentInstance.selected()?.id).toBe('cup');

    press(fixture, 'ArrowDown');
    optionRows(fixture)[4].dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
    expect(fixture.componentInstance.selected()?.id).withContext('disabled row refused').toBe('cup');
  });

  it('disabled: the input is disabled and the list never opens', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();

    expect(input(fixture).disabled).toBeTrue();

    press(fixture, 'ArrowDown');

    expect(input(fixture).getAttribute('aria-expanded')).toBe('false');
    expect(listbox(fixture).hasAttribute('hidden')).toBeTrue();
  });

  it('gives every row a 40px target and stays inside its own width when narrow', async () => {
    const fixture = await createFixture();
    host(fixture).style.width = '18rem';
    press(fixture, 'ArrowDown');

    for (const row of optionRows(fixture)) {
      expect(row.getBoundingClientRect().height)
        .withContext(row.querySelector('.label')?.textContent?.trim() ?? 'option')
        .toBeGreaterThanOrEqual(40);
    }

    const limit = host(fixture).getBoundingClientRect().right + 1;
    const overflowing = Array.from(host(fixture).querySelectorAll('*'))
      .filter((node) => node.getBoundingClientRect().right > limit)
      .map((node) => node.className || node.tagName.toLowerCase());
    expect(overflowing).withContext('overflowing the combobox at 18rem').toEqual([]);

    // A long label wraps rather than pushing the popup wider than the field.
    expect(getComputedStyle(optionRows(fixture)[0]).overflowWrap).toBe('anywhere');
  });

  it('is one set of markup in both themes', async () => {
    const fixture = await createFixture();
    press(fixture, 'ArrowDown');

    const render = (theme: 'light' | 'dark') => {
      document.documentElement.setAttribute('data-cp-theme', theme);
      fixture.detectChanges();
      return { html: host(fixture).innerHTML, background: getComputedStyle(listbox(fixture)).backgroundColor };
    };

    const light = render('light');
    const dark = render('dark');
    document.documentElement.removeAttribute('data-cp-theme');

    expect(dark.html).toBe(light.html);
    expect(dark.background).not.toBe(light.background);
  });

  /**
   * The affordance, which is not decoration alone: before this the control rendered as a bare text input that
   * did nothing until something was typed, so a picker was indistinguishable from a plain field.
   */
  it('opens the list when the box is clicked, and marks itself as a picker', async () => {
    const fixture = await createFixture();

    const marker = host(fixture).querySelector<HTMLElement>('.caret')!;
    expect(marker).withContext('the caret').toBeTruthy();
    // Decoration: a screen reader is told about the list by aria-expanded, not by this.
    expect(marker.getAttribute('aria-hidden')).toBe('true');
    expect(getComputedStyle(marker).pointerEvents).toBe('none');

    expect(listbox(fixture).hidden).toBeTrue();
    expect(marker.classList).not.toContain('open');

    input(fixture).click();
    fixture.detectChanges();

    expect(listbox(fixture).hidden).toBeFalse();
    expect(input(fixture).getAttribute('aria-expanded')).toBe('true');
    // Shape as well as a list appearing, so the state does not rest on noticing the popup.
    expect(marker.classList).toContain('open');
    // The same entry point ArrowDown uses, so a pointer and a keyboard land in the same place.
    expect(host(fixture).querySelector('.option.active .label')?.textContent?.trim()).toBe('Grams');
  });

  // Clicking into the box to move the caret is not asking to lose the list being read.
  it('a second click leaves an open list alone', async () => {
    const fixture = await createFixture();

    input(fixture).click();
    fixture.detectChanges();
    press(fixture, 'ArrowDown');
    expect(host(fixture).querySelector('.option.active .label')?.textContent?.trim()).toBe('Kilograms');

    input(fixture).click();
    fixture.detectChanges();

    expect(listbox(fixture).hidden).toBeFalse();
    expect(host(fixture).querySelector('.option.active .label')?.textContent?.trim())
      .withContext('the active option is not reset under the pointer')
      .toBe('Kilograms');
  });

  /**
   * Focus deliberately does not open it. Tabbing through a form would pop every list open on the way past and
   * announce a count nobody asked for — the same noise the status region declines to make while closed.
   */
  it('does not open on focus alone', async () => {
    const fixture = await createFixture();

    input(fixture).dispatchEvent(new Event('focus'));
    fixture.detectChanges();

    expect(listbox(fixture).hidden).toBeTrue();
    expect(status(fixture).textContent?.trim()).toBe('');
  });

  it('disabled: a click does not open it, and the caret reads as unavailable', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();

    input(fixture).click();
    fixture.detectChanges();

    expect(listbox(fixture).hidden).toBeTrue();
    expect(host(fixture).querySelector('.caret')?.classList).toContain('disabled');
  });
});
