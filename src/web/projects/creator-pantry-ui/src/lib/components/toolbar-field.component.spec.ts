import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpSearchFieldComponent, CpSelectFieldComponent } from './toolbar-field.component';
import { CpToolbarComponent } from './toolbar.component';

@Component({
  imports: [CpToolbarComponent, CpSearchFieldComponent, CpSelectFieldComponent],
  template: `<div [style.width]="width()">
    <cp-toolbar ariaLabel="Library filters">
      <cp-search-field cpToolbarSearch label="Search the library">
        <input type="search" name="search" [value]="search()" (input)="onInput($event)" />
      </cp-search-field>
      <cp-select-field cpToolbarFilters label="Filter by channel">
        <select name="channel" [disabled]="off()" (change)="onChange($event)">
          <option value="">All channels</option>
          <option value="long">A channel whose name is very long indeed, longer than a narrow toolbar is wide</option>
        </select>
      </cp-select-field>
      <button cpToolbarActions type="button">Clear filters</button>
    </cp-toolbar>
  </div>`,
})
class HostComponent {
  readonly width = signal('900px');
  readonly search = signal('');
  readonly off = signal(false);
  readonly typed: string[] = [];
  readonly chosen: string[] = [];

  onInput(event: Event): void {
    this.typed.push((event.target as HTMLInputElement).value);
  }

  onChange(event: Event): void {
    this.chosen.push((event.target as HTMLSelectElement).value);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because focus and layout need an element in the document.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const input = (): HTMLInputElement => el.querySelector('input')!;
const select = (): HTMLSelectElement => el.querySelector('select')!;

describe('CpSearchFieldComponent and CpSelectFieldComponent', () => {
  afterEach(() => el?.remove());

  it('name the control by a label that wraps it, with no id needed', () => {
    mount();
    expect(input().labels?.length).toBe(1);
    // The label's own text: a wrapping label's textContent also holds a select's options.
    expect(input().labels?.[0].querySelector('.label')?.textContent).toBe('Search the library');
    expect(select().labels?.[0].querySelector('.label')?.textContent).toBe('Filter by channel');
    expect(input().id).toBe('');
  });

  it('announce the label without showing it', () => {
    mount();
    const label = el.querySelector('cp-search-field .label')!.getBoundingClientRect();
    expect(label.width).toBeLessThanOrEqual(1);
    expect(label.height).toBeLessThanOrEqual(1);
    // Clipped, not display:none — a hidden label names nothing.
    expect(getComputedStyle(el.querySelector('cp-search-field .label')!).display).not.toBe('none');
  });

  it("leave the control the consumer's own: its value, its events and its disabled state", () => {
    mount((h) => h.search.set('bread'));
    expect(input().value).toBe('bread');

    input().value = 'soda';
    input().dispatchEvent(new Event('input'));
    select().value = 'long';
    select().dispatchEvent(new Event('change'));
    expect(host.typed).toEqual(['soda']);
    expect(host.chosen).toEqual(['long']);

    host.off.set(true);
    fixture.detectChanges();
    expect(select().disabled).toBeTrue();
  });

  it('draw both controls at least 40px tall', () => {
    mount();
    expect(input().getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
    expect(select().getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
  });

  // It used to keep the browser's own select — a small native arrow, a different fill — beside forms whose
  // selects draw their caret, so the same choice looked like two different controls.
  it('draw the select as a form field draws one: its own caret, not the browser arrow', () => {
    mount();
    expect(getComputedStyle(select()).appearance).toBe('none');
    expect(parseFloat(getComputedStyle(select()).paddingInlineEnd)).withContext('room for the caret').toBeGreaterThanOrEqual(40);
  });

  it('do not let a long option or the search box hold the toolbar wider than a 320px page', () => {
    mount((h) => h.width.set('320px'));
    expect(select().getBoundingClientRect().width).toBeLessThanOrEqual(320);
    expect(input().getBoundingClientRect().width).toBeLessThanOrEqual(320);
    expect(el.querySelector('cp-toolbar')!.scrollWidth).toBeLessThanOrEqual(320);
  });

  it("stay inside the toolbar's arrow-key navigation, and keep the arrows a text field needs", () => {
    mount();
    const clear = el.querySelector('button')!;
    clear.focus();
    clear.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
    // Home from a button goes to the first control, which is the projected native input.
    expect(document.activeElement).toBe(input());

    // ArrowRight inside the search box moves the caret, not the focus.
    const arrow = new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
    input().dispatchEvent(arrow);
    expect(arrow.defaultPrevented).toBeFalse();
    expect(document.activeElement).toBe(input());
  });
});
